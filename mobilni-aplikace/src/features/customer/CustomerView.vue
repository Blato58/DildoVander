<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { secureUuid } from '../../application/identifiers.mjs';
import { account, accountRequest } from '../identity/session';
import AccountArchiveList from '../identity/AccountArchiveList.vue';
import SettingsView from '../identity/SettingsView.vue';
import NewCharacterDialog, { type NewCharacterDraft } from './NewCharacterDialog.vue';
import CustomerSessionResultCard from './CustomerSessionResultCard.vue';
import CustomerWaiting from './CustomerWaiting.vue';
import CustomerCompletionUpdates from './CustomerCompletionUpdates.vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { customerBle, type CustomerBleDevice, type CustomerBleStatus } from './customerBle';
import { projectCustomerSessionError } from './customerSessionFeedback';
import { SessionRecoveryController, type SessionRecoveryState } from './sessionRecovery';

interface CharacterProfile {
  profileId: string; revision: number;
  userInput: { name: string; world: string; version?: string; description?: string; language?: string };
  status: string; enrichment?: unknown; errorCode?: string; updatedAt?: string;
}
interface CustomerSession {
  id: string; operationId: string; deviceId: string; profileId: string; profileRevision: number;
  state: string; endRequested: boolean; createdAt: number; characterName?: string;
}
interface SessionSnapshot { items: CustomerSession[]; expiredOperations?: string[] }
interface StartTicketRequest {
  protocolVersion: 1; operationId: string; deviceId: string; bootId: string; challenge: string;
  profileId: string; profileRevision: number; personalMemoryConsent?: true; openAiConsent: true;
}
const route = useRoute();
const interfaceLanguage = interfaceLanguagePreference.language;
watch(() => account.value?.id, id => interfaceLanguagePreference.setScope(id), { immediate: true, flush: 'sync' });
const pane = computed(() => route.path.split('/').at(-1) ?? 'character');
const profile = ref<CharacterProfile | null>(null);
const sessions = ref<CustomerSession[]>([]);
const resultSessionId = ref<string | null>(null);
const completedSessions = computed(() => sessions.value.filter(session => session.state === 'completed'));
const resultSession = computed(() => completedSessions.value.find(session => session.id === resultSessionId.value)
  ?? completedSessions.value[0] ?? null);
const busy = ref(false);
const profileLoading = ref(false);
const error = ref('');
const errorDiagnosticCode = ref('');
const notice = ref('');
const devices = ref<CustomerBleDevice[]>([]);
const connected = ref<CustomerBleStatus | null>(null);
const selectedDevice = ref('');
const personalMemoryConsent = ref(false);
const enrichmentOpenAiConsent = ref(false);
const newCharacterOpen = ref(false);
const newCharacterError = ref('');
const conversationOpenAiConsent = ref(false);
const form = ref({ name: '', world: '', version: '', description: '', language: '' });
let generation = 0;
let accountWorkController = new AbortController();
const pendingOperationId = ref<string>();
const pendingTicketRequest = ref<StartTicketRequest>();
const waitingClaim = ref<{ operationId: string; expiresAt: number; profileId: string; deviceId: string }>();
let bleStartAttempted = false;
const requestedEndSessions = new Set<string>();
let enrichmentTimer: ReturnType<typeof setTimeout> | undefined;
const sessionRecoveryState = reactive<SessionRecoveryState>({
  refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined,
});
const sessionRecovery = new SessionRecoveryController<SessionSnapshot>({
  refresh: refreshSessionSnapshot,
  hasActiveSession: snapshot => isRecoveryVisible() && (Boolean(pendingOperationId.value) || snapshot.items.some(isActiveSession)),
  state: sessionRecoveryState,
  pollIntervalMs: 2500,
  retryBaseDelayMs: 1000,
  retryMaxDelayMs: 30_000,
});
const sessionRecoveryFeedback = computed(() => projectCustomerSessionError(sessionRecoveryState.error));

const headings: Record<string, string> = {
  character: 'Moje postava', conversation: 'Rozhovor s kufrem', keepsakes: 'Moje památky', account: 'Můj účet',
};
const startInProgress = computed(() => sessions.value.some(isActiveSession));
const characterStatusLabels: Record<string, string> = { saved: 'Uloženo bez AI přípravy', queued: 'Čeká na přípravu', running: 'Příprava probíhá', ready: 'Připraveno', needs_clarification: 'Potřebuje upřesnění', failed: 'Příprava se nezdařila' };
const sessionStatusLabels: Record<string, string> = { starting: 'Kufr se připravuje', context_ready: 'Rozhovor se připravuje', running: 'Probíhá', completed: 'Dokončen', failed: 'Nepodařilo se spustit', recovering: 'Obnovuje se' };
const enrichmentText = computed(() => {
  if (!profile.value?.enrichment) return '';
  if (typeof profile.value.enrichment === 'string') return profile.value.enrichment;
  const value = profile.value.enrichment as Record<string, unknown>;
  return [value.summary, value.description, value.text].filter(item => typeof item === 'string').join('\n');
});
const evidenceSections = computed(() => {
  const enrichment = profile.value?.enrichment;
  if (!enrichment || typeof enrichment !== 'object') return [];
  const source = enrichment as Record<string, unknown>;
  return (['jobs', 'evidence', 'facts', 'sources', 'conversationIdeas'] as const).flatMap(key => {
    const items = key === 'evidence' ? source.evidence : source[key];
    if (!Array.isArray(items)) return [];
    const lines = items.map(item => {
      if (typeof item === 'string') return item;
      if (!item || typeof item !== 'object') return '';
      const record = item as Record<string, unknown>;
      const text = [record.title, record.name, record.summary, record.description, record.text]
        .find(value => typeof value === 'string') as string | undefined ?? '';
      const urls = Array.isArray(record.sourceUrls) ? record.sourceUrls.filter(value => typeof value === 'string').join(' · ') : '';
      const url = typeof record.url === 'string' ? record.url : '';
      return [text, urls || url].filter(Boolean).join(' · ');
    }).filter(Boolean);
    return lines.length ? [{ title: key === 'jobs' ? 'Příprava' : key === 'sources' ? 'Zdroje' : key === 'conversationIdeas' ? 'Nápady na rozhovor' : 'Ověřené body', lines }] : [];
  });
});
const clarificationText = computed(() => {
  const enrichment = profile.value?.enrichment;
  if (!enrichment || typeof enrichment !== 'object') return '';
  const value = enrichment as Record<string, unknown>;
  if (value.needsClarification !== true || !Array.isArray(value.ambiguities)) return '';
  return value.ambiguities.filter(item => typeof item === 'string').join('\n');
});

function resetPrivateState(): void {
  generation += 1;
  accountWorkController.abort(); accountWorkController = new AbortController();
  sessionRecovery.invalidate();
  if (enrichmentTimer) clearTimeout(enrichmentTimer); enrichmentTimer = undefined;
  profile.value = null; sessions.value = []; devices.value = []; connected.value = null; profileLoading.value = Boolean(account.value);
  busy.value = false;
  newCharacterOpen.value = false; newCharacterError.value = '';
  resultSessionId.value = null;
  waitingClaim.value = undefined;
  selectedDevice.value = ''; pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false; requestedEndSessions.clear(); error.value = ''; errorDiagnosticCode.value = ''; notice.value = '';
  form.value = { name: '', world: '', version: '', description: '', language: '' }; personalMemoryConsent.value = false; enrichmentOpenAiConsent.value = false; conversationOpenAiConsent.value = false;
  void Promise.resolve(customerBle.disconnect()).catch(() => {});
}

async function loadProfile(epoch = generation): Promise<void> {
  if (!account.value) return;
  profileLoading.value = true;
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation) return;
    profile.value = result.profile;
    form.value = {
      name: result.profile?.userInput.name ?? '', world: result.profile?.userInput.world ?? '',
      version: result.profile?.userInput.version ?? '', description: result.profile?.userInput.description ?? '',
      language: result.profile?.userInput.language ?? '',
    };
    scheduleEnrichmentRefresh(epoch);
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) profileLoading.value = false; }
}

async function loadSessions(epoch = generation, signal = accountWorkController.signal): Promise<SessionSnapshot | undefined> {
  const result = await accountRequest<SessionSnapshot>('/app/v1/customer/sessions', 'GET', undefined, signal);
  if (signal.aborted) throw new DOMException('Načítání relací bylo zrušeno.', 'AbortError');
  if (epoch !== generation) return undefined;
  sessions.value = result.items.map(item => requestedEndSessions.has(item.id) ? { ...item, endRequested: true } : item);
  if (pendingOperationId.value && result.expiredOperations?.includes(pendingOperationId.value)) {
    pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false;
    notice.value = 'Předchozí žádost vypršela dříve, než ji kufr přijal. Zkontrolujte připojení a stiskněte znovu „Promluvit s kufrem“ pro novou žádost.';
  }
  return result;
}

async function refreshSessionSnapshot(signal: AbortSignal): Promise<SessionSnapshot> {
  const epoch = generation;
  const result = await loadSessions(epoch, signal);
  if (!result) throw new DOMException('Účet se mezitím změnil.', 'AbortError');

  const pendingSession = pendingOperationId.value
    ? result.items.find(item => item.operationId === pendingOperationId.value)
    : undefined;
  if (pendingSession && isTerminal(pendingSession.state)) {
    pendingOperationId.value = undefined;
    pendingTicketRequest.value = undefined;
    bleStartAttempted = false;
    try { await reconcileBle(epoch); }
    catch (cause) { if (epoch === generation) error.value = projectCustomerSessionError(cause).message; }
  }
  for (const session of result.items) {
    if (isTerminal(session.state)) requestedEndSessions.delete(session.id);
  }
  return result;
}

async function saveCharacter(newCharacter = false, draft?: NewCharacterDraft): Promise<void> {
  if (busy.value) return;
  error.value = ''; notice.value = ''; busy.value = true; const epoch = generation;
  newCharacterError.value = '';
  try {
    const input = draft ?? form.value;
    const expectedRevision = profile.value?.revision ?? 0;
    const result = await accountRequest<{ profile: CharacterProfile }>('/app/v1/customer/character', 'PUT', {
      expectedRevision, expectedProfileId: profile.value?.profileId ?? null, openAiConsent: draft?.openAiConsent ?? enrichmentOpenAiConsent.value,
      userInput: { name: input.name.trim(), world: input.world.trim(), version: input.version.trim() || undefined,
        description: input.description.trim() || undefined, language: input.language.trim() || undefined },
      ...(newCharacter && profile.value ? { newCharacter: true } : {}),
    }, accountWorkController.signal);
    if (epoch !== generation) return;
    profile.value = result.profile;
    if (newCharacter) {
      form.value = {
        name: result.profile.userInput.name, world: result.profile.userInput.world,
        version: result.profile.userInput.version ?? '', description: result.profile.userInput.description ?? '',
        language: result.profile.userInput.language ?? '',
      };
      newCharacterOpen.value = false;
    }
    enrichmentOpenAiConsent.value = false;
    scheduleEnrichmentRefresh(epoch);
    notice.value = newCharacter ? 'Nová postava byla založena. Historie předchozí postavy zůstává uložená.' : 'Profil postavy byl uložen.';
  } catch (cause) {
    if (epoch === generation) {
      if (newCharacter) newCharacterError.value = (cause as Error).message;
      else error.value = (cause as Error).message;
    }
  }
  finally { if (epoch === generation) busy.value = false; }
}

function beginNewCharacter(): void {
  if (busy.value) return;
  newCharacterError.value = '';
  newCharacterOpen.value = true;
}
function cancelNewCharacter(): void {
  // A dispatched profile write cannot be undone by closing its dialog.
  if (!busy.value) newCharacterOpen.value = false;
}

async function requestCorrection(): Promise<void> {
  if (!profile.value || !enrichmentOpenAiConsent.value) return;
  error.value = ''; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character/correction', 'POST', {
      expectedRevision: profile.value.revision, expectedProfileId: profile.value.profileId, openAiConsent: true,
    }, accountWorkController.signal);
    if (epoch === generation) { enrichmentOpenAiConsent.value = false; notice.value = 'Označili jste, že tato postava není vaše. Příprava informací se obnoví.'; await refreshEnrichment(epoch); }
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function deleteCharacter(): Promise<void> {
  if (!profile.value || !window.confirm('Smazat profil postavy? Historie rozhovorů zůstane zachována.')) return;
  error.value = ''; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character?expectedRevision=' + profile.value.revision + '&expectedProfileId=' + encodeURIComponent(profile.value.profileId), 'DELETE', undefined, accountWorkController.signal);
    if (epoch === generation) { profile.value = null; form.value = { name: '', world: '', version: '', description: '', language: '' }; notice.value = 'Profil postavy byl smazán.'; }
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function discover(): Promise<void> {
  error.value = ''; errorDiagnosticCode.value = ''; devices.value = []; connected.value = null;
  if (!customerBle.supported()) { error.value = 'Bluetooth start je dostupný v instalované aplikaci pro iPhone. Safari nepodporuje tuto kufrovou funkci.'; return; }
  const epoch = generation; busy.value = true;
  try { const result = await customerBle.discover(); if (epoch === generation) devices.value = result.devices; }
  catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function connect(): Promise<void> {
  if (!selectedDevice.value) return;
  error.value = ''; errorDiagnosticCode.value = ''; busy.value = true; const epoch = generation;
  try { const status = await customerBle.connect(selectedDevice.value); if (epoch === generation) connected.value = status; }
  catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function startConversation(): Promise<void> {
  if (!profile.value || !connected.value || !connected.value.capabilities.customerStart
    || (!conversationOpenAiConsent.value && !pendingTicketRequest.value)) return;
  error.value = ''; errorDiagnosticCode.value = ''; notice.value = ''; busy.value = true; const epoch = generation;
  // A retried operation keeps its ID; a rejected or unknown outcome is reconciled first.
  const hadPendingOperation = Boolean(pendingOperationId.value);
  const claim = waitingClaim.value;
  if (!hadPendingOperation && claim && (claim.expiresAt <= Math.floor(Date.now() / 1000)
      || claim.profileId !== profile.value.profileId || claim.deviceId !== connected.value.deviceId)) {
    waitingClaim.value = undefined;
    busy.value = false;
    notice.value = 'Pozvánka do rozhovoru vypršela nebo patří jinému připojení. Ověřte znovu své místo ve frontě.';
    return;
  }
  const operationId = pendingOperationId.value ?? claim?.operationId ?? secureUuid(); pendingOperationId.value = operationId;
  waitingClaim.value = undefined;
  if (!hadPendingOperation) bleStartAttempted = false;
  try {
    const existing = await loadSessions(epoch);
    if (epoch !== generation) return;
    if (!existing) return;
    if (existing.expiredOperations?.includes(operationId)) return;
    const prior = existing.items.find(item => item.operationId === operationId);
    if (prior) {
      const status = await accountRequest<CustomerSession>(`/app/v1/customer/sessions/${encodeURIComponent(prior.id)}`, 'GET', undefined, accountWorkController.signal);
      if (epoch !== generation) return;
      sessions.value = existing.items.map(item => item.id === prior.id ? status : item);
      await reconcileBle(epoch);
      if (isTerminal(status.state)) { pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false; }
      else scheduleSessionPoll(epoch);
      return;
    }
    let request = pendingTicketRequest.value;
    if (!request) {
      // Only a new operation gets a new challenge/profile snapshot. Retries reuse this exact body.
      await reconcileBle(epoch);
      if (epoch !== generation || !connected.value || !profile.value) return;
      // Consent can be withdrawn while session/BLE reconciliation is awaiting a reply.
      // Once issuance is attempted, retries keep the original immutable consent-bearing body.
      if (!conversationOpenAiConsent.value) {
        pendingOperationId.value = undefined; bleStartAttempted = false;
        notice.value = 'Souhlas s OpenAI byl odvolán před odesláním žádosti. Rozhovor jsme nespustili.';
        return;
      }
      request = {
        protocolVersion: 1, operationId, deviceId: connected.value.deviceId, bootId: connected.value.bootId,
        challenge: connected.value.challenge, profileId: profile.value.profileId, profileRevision: profile.value.revision, openAiConsent: true,
        ...(personalMemoryConsent.value ? { personalMemoryConsent: true } : {}),
      };
      pendingTicketRequest.value = request;
    }
    const ticket = await accountRequest<{ ticket: string; operationId: string; expiresAt: string; sessionId?: string }>('/app/v1/customer/start-tickets', 'POST', request, accountWorkController.signal);
    if (epoch !== generation) return;
    bleStartAttempted = true;
    const bleStatus = await customerBle.start(ticket.ticket, operationId);
    if (epoch !== generation) return;
    connected.value = bleStatus;
    if (bleStatus.sessionId) {
      const status = await accountRequest<CustomerSession>(`/app/v1/customer/sessions/${encodeURIComponent(bleStatus.sessionId)}`, 'GET', undefined, accountWorkController.signal);
      if (epoch === generation) sessions.value = [status, ...sessions.value.filter(item => item.id !== status.id)];
      if (epoch === generation && isTerminal(status.state)) {
        pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false;
        await reconcileBle(epoch);
      }
    }
    scheduleSessionPoll(epoch);
  } catch (cause) {
    if (epoch === generation) {
      showProjectedError(cause);
      if (!bleStartAttempted && isDefinitiveTicketIssuerRejection(cause)) {
        pendingOperationId.value = undefined; pendingTicketRequest.value = undefined;
        notice.value = 'Kufr odmítl žádost ještě před pokusem o připojení. Stiskněte znovu „Promluvit s kufrem“ pro novou žádost.';
      }
      try { await loadSessions(epoch); await reconcileBle(epoch); } catch { /* Keep the original start error. */ }
      if (epoch === generation) scheduleSessionPoll(epoch);
    }
  } finally { if (epoch === generation) busy.value = false; }
}

function acceptWaitingClaim(value: { operationId: string; expiresAt: number; profileId: string }): void {
  if (!connected.value || !profile.value || value.profileId !== profile.value.profileId
      || value.expiresAt <= Math.floor(Date.now() / 1000) || pendingOperationId.value || busy.value
      || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.operationId)) return;
  waitingClaim.value = { ...value, deviceId: connected.value.deviceId };
  notice.value = 'Jste na řadě. Po potvrzení souhlasu stiskněte „Promluvit s kufrem“.';
}

async function endConversation(session: CustomerSession): Promise<void> {
  error.value = ''; busy.value = true; const epoch = generation;
  try {
    await accountRequest<{ state: string }>(`/app/v1/customer/sessions/${encodeURIComponent(session.id)}/end`, 'POST', undefined, accountWorkController.signal);
    if (epoch !== generation) return;
    notice.value = 'Konec rozhovoru byl požádán.';
    requestedEndSessions.add(session.id);
    if (session.state === 'recovering') {
      sessions.value = sessions.value.map(item => item.id === session.id ? { ...item, endRequested: true } : item);
    } else {
      try { connected.value = await customerBle.end(session.id); } catch { /* Phone disconnect never becomes a stop. */ }
    }
    scheduleSessionPoll(epoch);
    await sessionRecovery.refresh();
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function refreshConversationStatus(): Promise<void> {
  const result = isRecoveryVisible()
    ? await (sessionRecovery.setActive(true) ?? sessionRecovery.refresh())
    : await sessionRecovery.refresh();
  if (!result && pendingOperationId.value && !sessionRecoveryState.retrying) {
    notice.value = 'Stav se stále ověřuje. Další pokus použije stejnou žádost.';
  }
}

function isTerminal(state: string): boolean { return ['completed', 'failed'].includes(state); }
function isDefinitiveTicketIssuerRejection(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const status = 'status' in error && typeof error.status === 'number' ? error.status : 0;
  const code = 'code' in error && typeof error.code === 'string' ? error.code : '';
  return (status === 409 && ['device_challenge_unavailable', 'character_revision_mismatch', 'waiting_claim_required'].includes(code))
    || (status === 410 && code === 'customer_profile_revoked')
    || (status === 404 && code === 'character_not_found')
    || (status === 429 && code === 'start_ticket_limit');
}
function scheduleSessionPoll(epoch: number): void {
  if (epoch === generation && isRecoveryVisible()) void sessionRecovery.setActive(true);
}
async function reconcileBle(epoch: number): Promise<void> {
  const status = await customerBle.status();
  if (epoch !== generation) return;
  if (!status.challenge || status.capabilities.customerStart !== 1) throw new Error('Kufr zatím není připravený. Obnovte stav připojení a zkuste to znovu.');
  connected.value = status;
}
function scheduleEnrichmentRefresh(epoch: number): void {
  if (enrichmentTimer) clearTimeout(enrichmentTimer);
  if (profile.value && ['queued', 'running'].includes(profile.value.status)) {
    enrichmentTimer = setTimeout(() => { void refreshEnrichment(epoch); }, 3000);
  }
}
async function refreshEnrichment(epoch = generation): Promise<void> {
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation || !result.profile || result.profile.profileId !== profile.value?.profileId || result.profile.revision !== profile.value.revision) return;
    // Refresh server-derived enrichment only; never replace the visitor's in-progress form edits.
    profile.value = { ...profile.value, status: result.profile.status, enrichment: result.profile.enrichment, errorCode: result.profile.errorCode, updatedAt: result.profile.updatedAt };
    scheduleEnrichmentRefresh(epoch);
  } catch { if (epoch === generation) scheduleEnrichmentRefresh(epoch); }
}

function isActiveSession(session: CustomerSession): boolean {
  return Boolean(session.state) && !isTerminal(session.state);
}
function showProjectedError(cause: unknown): void {
  const feedback = projectCustomerSessionError(cause);
  error.value = feedback.message;
  errorDiagnosticCode.value = feedback.diagnosticCode ?? '';
}
function isRecoveryVisible(): boolean {
  return pane.value === 'conversation' && Boolean(account.value) && document.visibilityState !== 'hidden';
}
function updateRecoveryActivity(): void {
  if (isRecoveryVisible()) void sessionRecovery.setActive(true);
  else void sessionRecovery.setActive(false);
}
function refreshSessionsForCurrentPane(): void {
  if (isRecoveryVisible()) void sessionRecovery.setActive(true);
  else void sessionRecovery.refresh();
}
function onWindowOnline(): void {
  if (isRecoveryVisible()) void (sessionRecovery.setActive(true) ?? sessionRecovery.refresh());
}
function onDocumentVisibilityChange(): void { updateRecoveryActivity(); }

watch(pane, updateRecoveryActivity);
watch(() => account.value?.id, (next, previous) => {
  if (next !== previous) {
    resetPrivateState();
    if (next) {
      const epoch = generation;
      void loadProfile(epoch);
      refreshSessionsForCurrentPane();
    }
  }
}, { flush: 'sync' });
onMounted(() => {
  window.addEventListener('online', onWindowOnline);
  document.addEventListener('visibilitychange', onDocumentVisibilityChange);
  if (account.value) { const epoch = generation; void loadProfile(epoch); refreshSessionsForCurrentPane(); }
});
onBeforeUnmount(() => {
  window.removeEventListener('online', onWindowOnline);
  document.removeEventListener('visibilitychange', onDocumentVisibilityChange);
  sessionRecovery.dispose();
  resetPrivateState();
});
</script>

<template>
  <section class="page-stack customer-page" :aria-labelledby="`customer-${pane}-title`">
    <header><p class="eyebrow">Můj prostor</p><h1 :id="`customer-${pane}-title`">{{ headings[pane] ?? 'Můj prostor' }}</h1><p v-if="pane === 'character'">Vyberte postavu a její svět. Profil můžete uložit bez AI. Informace od OpenAI připravíme pouze s vaším souhlasem.</p><p v-else-if="pane === 'conversation'">Rozhovor spustíte ručně po připojení ke kufru.</p></header>
    <AwNotice v-if="error" tone="warning" role="alert">{{ error }}<small v-if="errorDiagnosticCode"> Diagnostika: {{ errorDiagnosticCode }}</small></AwNotice>
    <AwNotice v-if="notice" tone="info" role="status">{{ notice }}</AwNotice>

    <p v-if="pane === 'character' && profileLoading" role="status">Načítám váš profil…</p>
    <section v-if="pane === 'character' && !profileLoading" class="page-stack">
      <section v-if="profile" class="aw-card page-stack" aria-labelledby="character-confirmation-title">
        <h2 id="character-confirmation-title">{{ profile.userInput.name }}</h2>
        <p>{{ profile.userInput.world }}<span v-if="profile.userInput.version"> · {{ profile.userInput.version }}</span></p>
        <p role="status">{{ characterStatusLabels[profile.status] ?? 'Stav se ověřuje' }} · Revize {{ profile.revision }}</p>
        <p v-if="profile.status === 'saved'">Postava je uložená bez AI přípravy. Můžete pokračovat k rozhovoru.</p>
        <AwNotice v-if="clarificationText" tone="info">Server potřebuje upřesnit některé informace: {{ clarificationText }}</AwNotice>
        <RouterLink v-if="['saved', 'ready'].includes(profile.status) || startInProgress" class="aw-button aw-button--primary" to="/me/conversation">{{ startInProgress ? 'Vrátit se k rozhovoru' : 'Pokračovat k rozhovoru' }}</RouterLink>
        <p class="aw-muted">Úprava níže zachová identitu a historii této postavy. Novou identitu vytvoříte samostatně.</p>
        <AwButton type="button" variant="secondary" :disabled="busy" @click="beginNewCharacter">Nová postava</AwButton>
      </section>
      <details :open="!profile || profile.status === 'needs_clarification'" class="aw-card">
        <summary>{{ profile ? 'Upravit nebo upřesnit postavu' : 'Vytvořit první postavu' }}</summary>
    <form class="customer-form" @submit.prevent="saveCharacter(false)">
      <AwField id="character-name" label="Jméno postavy"><input id="character-name" v-model="form.name" class="aw-input" maxlength="120" required /></AwField>
      <AwField id="character-world" label="Film, seriál, hra nebo kniha"><input id="character-world" v-model="form.world" class="aw-input" maxlength="160" required /></AwField>
      <AwField id="character-version" label="Verze nebo období (nepovinné)"><input id="character-version" v-model="form.version" class="aw-input" maxlength="120" /></AwField>
      <AwField id="character-description" label="Co je pro vás důležité? (nepovinné)"><textarea id="character-description" v-model="form.description" class="aw-input" maxlength="1200" rows="4" /></AwField>
      <AwField id="character-language" label="Jazyk (nepovinné)"><input id="character-language" v-model="form.language" class="aw-input" maxlength="35" /></AwField>
      <AwField id="character-openai-consent" label="Volitelná příprava pomocí AI"><label><input id="character-openai-consent" v-model="enrichmentOpenAiConsent" type="checkbox" /> Souhlasím s odesláním jména postavy, jejího světa, verze, popisu a jazyka společnosti OpenAI pro přípravu informací a nápadů k rozhovoru, případně s vyhledáním veřejných zdrojů. Při opravě se odešle také předchozí výklad postavy.</label></AwField>
      <p class="aw-muted">Bez zaškrtnutí profil uložíme bez odeslání k AI přípravě.</p>
      <p v-if="profile" class="aw-muted">Stav přípravy: {{ characterStatusLabels[profile.status] ?? 'Stav se ověřuje' }}<span v-if="profile.errorCode"> · {{ profile.errorCode }}</span></p>
      <AwNotice v-if="profile && clarificationText" tone="info">Server potřebuje upřesnit některé informace: {{ clarificationText }}</AwNotice>
      <details v-if="enrichmentText || evidenceSections.length">
        <summary>Podrobnosti přípravy a zdroje</summary>
        <p v-if="enrichmentText" class="character-enrichment">{{ enrichmentText }}</p>
        <section v-for="section in evidenceSections" :key="section.title" class="aw-card"><h2>{{ section.title }}</h2><ul><li v-for="line in section.lines" :key="line">{{ line }}</li></ul></section>
      </details>
      <div class="customer-actions"><AwButton type="submit" variant="primary" :busy="busy">{{ profile ? 'Uložit změny' : 'Vytvořit postavu' }}</AwButton><AwButton v-if="profile" type="button" :disabled="busy || !enrichmentOpenAiConsent" @click="requestCorrection">Tohle není moje postava</AwButton><AwButton v-if="profile" type="button" variant="danger" :disabled="busy" @click="deleteCharacter">Smazat profil</AwButton></div>
      <p v-if="profile" class="aw-muted">Revize {{ profile.revision }}</p>
    </form>
      </details>
      <RouterLink to="/me/account">Účet, souhlasy a pomoc s přístupem</RouterLink>
      <NewCharacterDialog :open="newCharacterOpen" :busy="busy" :error="newCharacterError" :current-character-name="profile?.userInput.name" :active-session="startInProgress" @cancel="cancelNewCharacter" @confirm="draft => saveCharacter(true, draft)" />
    </section>

    <section v-else-if="pane === 'conversation'" class="page-stack">
      <CustomerWaiting :device-id="connected?.deviceId ?? null" :profile-id="profile?.profileId ?? null" :proximity="connected ? { bootId: connected.bootId, challenge: connected.challenge } : null" @claim="acceptWaitingClaim" />
      <AwNotice v-if="!customerBle.supported()" tone="info">Bluetooth připojení je dostupné v instalované aplikaci pro iPhone. Safari může zobrazit účet a památky, ale neumí připojit kufr.<br><a href="/ios/">Otevřít instalaci aplikace pro iPhone</a></AwNotice>
      <div class="aw-card"><h2>Vaše postava</h2><p v-if="profile">{{ profile.userInput.name }} · {{ profile.userInput.world }} (revize {{ profile.revision }})</p><p v-else>Nejprve vytvořte postavu.</p><RouterLink to="/me/character">Upravit postavu</RouterLink></div>
      <div class="aw-card customer-form"><h2>Připojení ke kufru</h2><p>Bluetooth se zapne až po stisknutí tlačítka. Připojení telefonu samo rozhovor nezastaví.</p>
        <AwButton variant="primary" :disabled="busy || !profile || !customerBle.supported()" @click="discover">Najít kufr poblíž</AwButton>
        <div v-if="devices.length" class="customer-actions"><label for="customer-device">Kufr</label><select id="customer-device" v-model="selectedDevice" class="aw-input"><option value="">Vyberte kufr</option><option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option></select><AwButton :disabled="busy || !selectedDevice" @click="connect">Připojit</AwButton></div>
        <p v-if="connected" role="status">{{ connected.state === 'running' ? 'Rozhovor právě probíhá' : connected.state === 'starting' ? 'Rozhovor se připravuje' : connected.state === 'idle' ? 'Kufr je připojený' : 'Stav kufru byl obnoven' }}</p>
        <AwField id="conversation-openai-consent" label="Souhlas s AI rozhovorem"><label><input id="conversation-openai-consent" v-model="conversationOpenAiConsent" type="checkbox" /> Souhlasím s odesíláním zvuku mého hlasu, přepisu rozhovoru a kontextu postavy (jméno, svět, verze, popis, jazyk a připravené informace) společnosti OpenAI pro porozumění řeči a tvorbu hlasových odpovědí během tohoto rozhovoru.</label></AwField>
        <p class="aw-muted">Bez tohoto souhlasu rozhovor nespustíme. Pokud níže povolíte soukromou kontinuitu, součástí kontextu pro OpenAI bude také povolená osobní paměť.</p>
        <AwField id="personal-memory-consent" label="Soukromá kontinuita"><label><input id="personal-memory-consent" v-model="personalMemoryConsent" type="checkbox" /> Souhlasím s použitím mé ověřené osobní paměti v tomto rozhovoru</label></AwField>
        <AwButton variant="primary" :busy="busy" :disabled="busy || !profile || !connected?.capabilities.customerStart || startInProgress || (!conversationOpenAiConsent && !pendingOperationId)" @click="startConversation">Promluvit s kufrem</AwButton>
        <p v-if="pendingOperationId" role="status">Ověřujeme, zda kufr rozhovor přijal. Telefonní spojení rozhovor neukončuje.</p>
      </div>
      <div class="aw-card session-recovery" aria-live="polite">
        <p v-if="sessionRecoveryState.refreshing" role="status">Obnovuji stav rozhovorů…</p>
        <p v-else-if="sessionRecoveryState.retrying" role="status">Spojení se serverem se obnovuje. Další pokus proběhne automaticky.</p>
        <AwNotice v-if="sessionRecoveryState.stale" tone="warning" role="alert">
          {{ sessionRecoveryFeedback.message }}
          <small v-if="sessionRecoveryFeedback.diagnosticCode"> Diagnostika: {{ sessionRecoveryFeedback.diagnosticCode }}</small>
        </AwNotice>
        <AwButton variant="secondary" :disabled="sessionRecoveryState.refreshing" @click="refreshConversationStatus">Obnovit stav rozhovoru</AwButton>
      </div>
      <div v-for="session in sessions" :key="session.id" class="aw-card"><h2>{{ session.characterName || profile?.userInput.name || 'Rozhovor' }}</h2><p>Stav: {{ sessionStatusLabels[session.state] ?? 'Stav se ověřuje' }}</p><p>Začátek: {{ new Date(session.createdAt * 1000).toLocaleString('cs') }}</p><AwButton v-if="['running', 'starting', 'context_ready', 'recovering'].includes(session.state)" variant="secondary" :disabled="busy || session.endRequested" @click="endConversation(session)">{{ session.endRequested ? 'Konec požádán' : 'Ukončit rozhovor' }}</AwButton></div>
      <section v-if="resultSession" class="page-stack">
        <AwField v-if="completedSessions.length > 1" id="result-session" label="Dokončený rozhovor">
          <select id="result-session" v-model="resultSessionId" class="aw-input">
            <option :value="null">Nejnovější dokončený rozhovor</option>
            <option v-for="session in completedSessions" :key="session.id" :value="session.id">{{ session.characterName || 'Rozhovor' }} · {{ new Date(session.createdAt * 1000).toLocaleString('cs') }}</option>
          </select>
        </AwField>
        <CustomerSessionResultCard :key="resultSession.id" :session-id="resultSession.id" />
      </section>
    </section>

    <section v-else-if="pane === 'keepsakes'" class="page-stack">
      <CustomerCompletionUpdates :locale="interfaceLanguage" />
      <AccountArchiveList :locale="interfaceLanguage" />
    </section>
    <SettingsView v-else-if="pane === 'account'" customer-surface />
  </section>
</template>

<style scoped>
.customer-page { max-width: 54rem; }
.customer-form { display: grid; gap: 1rem; }
.customer-actions { display: flex; flex-wrap: wrap; align-items: end; gap: .65rem; }
.character-enrichment { white-space: pre-line; padding: 1rem; border-left: 2px solid var(--aw-accent); background: var(--aw-surface); }
@media (max-width: 720px) { .customer-actions { display: grid; grid-template-columns: minmax(0, 1fr); } }
</style>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import GeneratedWand from '../archive/GeneratedWand.vue';
import { projectGeneratedWand, type GeneratedWand as GeneratedWandSnapshot } from '../archive/generatedWand';
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
import CustomerDuelHistory from './CustomerDuelHistory.vue';
import CustomerCompletionUpdates from './CustomerCompletionUpdates.vue';
import ConversationPreferences from './ConversationPreferences.vue';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { customerBle, type CustomerBleDevice, type CustomerBleStatus } from './customerBle';
import { projectCustomerSessionError, type CustomerSessionFeedback } from './customerSessionFeedback';
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
const selectedWand = ref<GeneratedWandSnapshot | null>(null);
const sessions = ref<CustomerSession[]>([]);
const resultSessionId = ref<string | null>(null);
const resultSessions = computed(() => sessions.value.filter(session => ['completed', 'failed'].includes(session.state)));
const resultSession = computed(() => resultSessions.value.find(session => session.id === resultSessionId.value)
  ?? resultSessions.value[0] ?? null);
const busy = ref(false);
const profileLoading = ref(false);
const errorFeedback = ref<CustomerSessionFeedback | null>(null);
const noticeCopyKey = ref<CustomerCopyKey | null>(null);
const devices = ref<CustomerBleDevice[]>([]);
const connected = ref<CustomerBleStatus | null>(null);
const selectedDevice = ref('');
const personalMemoryConsent = ref(false);
const enrichmentOpenAiConsent = ref(false);
const newCharacterOpen = ref(false);
const newCharacterError = ref<CustomerSessionFeedback | null>(null);
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

const startInProgress = computed(() => sessions.value.some(isActiveSession));
const paneTitleKeys: Record<string, CustomerCopyKey> = {
  character: 'customer.character-pane', conversation: 'customer.conversation-pane', keepsakes: 'customer.keepsakes-pane', account: 'customer.account-pane',
};
const profileStatusKeys: Record<string, CustomerCopyKey> = {
  saved: 'customer.status.saved', queued: 'customer.status.queued', running: 'customer.status.running', ready: 'customer.status.ready',
  needs_clarification: 'customer.status.needs_clarification', failed: 'customer.status.failed',
};
const sessionStatusKeys: Record<string, CustomerCopyKey> = {
  starting: 'customer.status.starting', context_ready: 'customer.status.context_ready', running: 'customer.status.session-running',
  completed: 'customer.status.completed', failed: 'customer.status.session-failed', recovering: 'customer.status.recovering',
};
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
    const titleKey: CustomerCopyKey = key === 'jobs' ? 'customer.evidence-preparation'
      : key === 'sources' ? 'customer.evidence-sources'
        : key === 'conversationIdeas' ? 'customer.evidence-ideas' : 'customer.evidence-facts';
    return lines.length ? [{ title: customerCopy.text(titleKey), lines }] : [];
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
  profile.value = null; selectedWand.value = null; sessions.value = []; devices.value = []; connected.value = null; profileLoading.value = Boolean(account.value);
  busy.value = false;
  newCharacterOpen.value = false; newCharacterError.value = null;
  resultSessionId.value = null;
  waitingClaim.value = undefined;
  selectedDevice.value = ''; pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false; requestedEndSessions.clear(); errorFeedback.value = null; noticeCopyKey.value = null;
  form.value = { name: '', world: '', version: '', description: '', language: '' }; personalMemoryConsent.value = false; enrichmentOpenAiConsent.value = false; conversationOpenAiConsent.value = false;
  void Promise.resolve(customerBle.disconnect()).catch(() => {});
}

async function loadProfile(epoch = generation): Promise<void> {
  if (!account.value) return;
  profileLoading.value = true;
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null; generatedWand?: unknown }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation) return;
    profile.value = result.profile;
    selectedWand.value = result.profile ? projectGeneratedWand(result.generatedWand) : null;
    form.value = {
      name: result.profile?.userInput.name ?? '', world: result.profile?.userInput.world ?? '',
      version: result.profile?.userInput.version ?? '', description: result.profile?.userInput.description ?? '',
      language: result.profile?.userInput.language ?? '',
    };
    scheduleEnrichmentRefresh(epoch);
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) profileLoading.value = false; }
}

async function loadSessions(epoch = generation, signal = accountWorkController.signal): Promise<SessionSnapshot | undefined> {
  const result = await accountRequest<SessionSnapshot>('/app/v1/customer/sessions', 'GET', undefined, signal);
  if (signal.aborted) throw new DOMException('Načítání relací bylo zrušeno.', 'AbortError');
  if (epoch !== generation) return undefined;
  sessions.value = result.items.map(item => requestedEndSessions.has(item.id) ? { ...item, endRequested: true } : item);
  if (pendingOperationId.value && result.expiredOperations?.includes(pendingOperationId.value)) {
    pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false;
    noticeCopyKey.value = 'customer.expired-start';
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
    catch (cause) { if (epoch === generation) showProjectedError(cause); }
  }
  for (const session of result.items) {
    if (isTerminal(session.state)) requestedEndSessions.delete(session.id);
  }
  return result;
}

async function saveCharacter(newCharacter = false, draft?: NewCharacterDraft): Promise<void> {
  if (busy.value) return;
  errorFeedback.value = null; noticeCopyKey.value = null; busy.value = true; const epoch = generation;
  newCharacterError.value = null;
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
    selectedWand.value = null;
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
    noticeCopyKey.value = newCharacter ? 'customer.saved-new-character' : 'customer.profile-saved';
  } catch (cause) {
    if (epoch === generation) {
      if (newCharacter) newCharacterError.value = projectCustomerSessionError(cause);
      else showProjectedError(cause);
    }
  }
  finally { if (epoch === generation) busy.value = false; }
}

function beginNewCharacter(): void {
  if (busy.value) return;
  newCharacterError.value = null;
  newCharacterOpen.value = true;
}
function cancelNewCharacter(): void {
  // A dispatched profile write cannot be undone by closing its dialog.
  if (!busy.value) newCharacterOpen.value = false;
}

async function requestCorrection(): Promise<void> {
  if (!profile.value || !enrichmentOpenAiConsent.value) return;
  errorFeedback.value = null; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character/correction', 'POST', {
      expectedRevision: profile.value.revision, expectedProfileId: profile.value.profileId, openAiConsent: true,
    }, accountWorkController.signal);
    if (epoch === generation) { enrichmentOpenAiConsent.value = false; noticeCopyKey.value = 'customer.not-my-profile'; await refreshEnrichment(epoch); }
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function deleteCharacter(): Promise<void> {
  if (!profile.value || !window.confirm(customerCopy.text('customer.delete-confirm'))) return;
  errorFeedback.value = null; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character?expectedRevision=' + profile.value.revision + '&expectedProfileId=' + encodeURIComponent(profile.value.profileId), 'DELETE', undefined, accountWorkController.signal);
    if (epoch === generation) { profile.value = null; selectedWand.value = null; form.value = { name: '', world: '', version: '', description: '', language: '' }; noticeCopyKey.value = 'customer.profile-deleted'; }
  } catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function discover(): Promise<void> {
  errorFeedback.value = null; devices.value = []; connected.value = null;
  if (!customerBle.supported()) { errorFeedback.value = { message: '', copyKey: 'customer.bluetooth-unsupported' }; return; }
  const epoch = generation; busy.value = true;
  try { const result = await customerBle.discover(); if (epoch === generation) devices.value = result.devices; }
  catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function connect(): Promise<void> {
  if (!selectedDevice.value) return;
  errorFeedback.value = null; busy.value = true; const epoch = generation;
  try { const status = await customerBle.connect(selectedDevice.value); if (epoch === generation) connected.value = status; }
  catch (cause) { if (epoch === generation) showProjectedError(cause); }
  finally { if (epoch === generation) busy.value = false; }
}

async function startConversation(): Promise<void> {
  if (!profile.value || !connected.value || !connected.value.capabilities.customerStart
    || (!conversationOpenAiConsent.value && !pendingTicketRequest.value)) return;
  errorFeedback.value = null; noticeCopyKey.value = null; busy.value = true; const epoch = generation;
  // A retried operation keeps its ID; a rejected or unknown outcome is reconciled first.
  const hadPendingOperation = Boolean(pendingOperationId.value);
  const claim = waitingClaim.value;
  if (!hadPendingOperation && claim && (claim.expiresAt <= Math.floor(Date.now() / 1000)
      || claim.profileId !== profile.value.profileId || claim.deviceId !== connected.value.deviceId)) {
    waitingClaim.value = undefined;
    busy.value = false;
    noticeCopyKey.value = 'customer.expired-invitation';
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
        noticeCopyKey.value = 'customer.consent-revoked';
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
        noticeCopyKey.value = 'customer.ticket-rejected';
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
  noticeCopyKey.value = 'customer.turn-ready';
}

async function endConversation(session: CustomerSession): Promise<void> {
  errorFeedback.value = null; busy.value = true; const epoch = generation;
  try {
    await accountRequest<{ state: string }>(`/app/v1/customer/sessions/${encodeURIComponent(session.id)}/end`, 'POST', undefined, accountWorkController.signal);
    if (epoch !== generation) return;
    noticeCopyKey.value = 'customer.end-submitted';
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
    noticeCopyKey.value = 'customer.still-verifying';
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
    const result = await accountRequest<{ profile: CharacterProfile | null; generatedWand?: unknown }>('/app/v1/customer/character', 'GET', undefined, accountWorkController.signal);
    if (epoch !== generation || !result.profile || result.profile.profileId !== profile.value?.profileId || result.profile.revision !== profile.value.revision) return;
    selectedWand.value = projectGeneratedWand(result.generatedWand);
    // Refresh server-derived enrichment only; never replace the visitor's in-progress form edits.
    profile.value = { ...profile.value, status: result.profile.status, enrichment: result.profile.enrichment, errorCode: result.profile.errorCode, updatedAt: result.profile.updatedAt };
    scheduleEnrichmentRefresh(epoch);
  } catch { if (epoch === generation) scheduleEnrichmentRefresh(epoch); }
}

function isActiveSession(session: CustomerSession): boolean {
  return Boolean(session.state) && !isTerminal(session.state);
}
function showProjectedError(cause: unknown): void {
  errorFeedback.value = projectCustomerSessionError(cause);
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
    <header><p class="eyebrow">{{ customerCopy.text('customer.space') }}</p><h1 :id="`customer-${pane}-title`">{{ customerCopy.text(paneTitleKeys[pane] ?? 'customer.space') }}</h1><p v-if="pane === 'character'">{{ customerCopy.text('customer.character-header') }}</p><p v-else-if="pane === 'conversation'">{{ customerCopy.text('customer.conversation-header') }}</p></header>
    <AwNotice v-if="errorFeedback" tone="warning" role="alert">{{ errorFeedback.copyKey ? customerCopy.text(errorFeedback.copyKey) : errorFeedback.message }}<small v-if="errorFeedback.diagnosticCode"> {{ customerCopy.text('customer.diagnostic', { code: errorFeedback.diagnosticCode }) }}</small></AwNotice>
    <AwNotice v-if="noticeCopyKey" tone="info" role="status">{{ customerCopy.text(noticeCopyKey) }}</AwNotice>

    <p v-if="pane === 'character' && profileLoading" role="status">{{ customerCopy.text('customer.profile-loading') }}</p>
    <section v-if="pane === 'character' && !profileLoading" class="page-stack">
      <section v-if="profile" class="aw-card page-stack" aria-labelledby="character-confirmation-title">
        <h2 id="character-confirmation-title">{{ profile.userInput.name }}</h2>
        <p>{{ profile.userInput.world }}<span v-if="profile.userInput.version"> · {{ profile.userInput.version }}</span></p>
        <p role="status">{{ customerCopy.text(profileStatusKeys[profile.status] ?? 'customer.status-unknown') }} · {{ customerCopy.text('customer.revision', { revision: profile.revision }) }}</p>
        <p v-if="profile.status === 'saved'">{{ customerCopy.text('customer.saved-guidance') }}</p>
        <AwNotice v-if="clarificationText" tone="info">{{ customerCopy.text('customer.clarification', { text: clarificationText }) }}</AwNotice>
        <RouterLink v-if="['saved', 'ready'].includes(profile.status) || startInProgress" class="aw-button aw-button--primary" to="/me/conversation">{{ customerCopy.text(startInProgress ? 'customer.return-conversation' : 'customer.continue-conversation') }}</RouterLink>
        <p class="aw-muted">{{ customerCopy.text('customer.correction-guidance') }}</p>
        <AwButton type="button" variant="secondary" :disabled="busy" @click="beginNewCharacter">{{ customerCopy.text('customer.new-character') }}</AwButton>
      </section>
      <GeneratedWand v-if="profile && selectedWand" :wand="selectedWand" show-name />
      <details :open="!profile || profile.status === 'needs_clarification'" class="aw-card">
        <summary>{{ customerCopy.text(profile ? 'customer.edit-character' : 'customer.create-first') }}</summary>
    <form class="customer-form" @submit.prevent="saveCharacter(false)">
      <AwField id="character-name" :label="customerCopy.text('profile.name')"><input id="character-name" v-model="form.name" class="aw-input" maxlength="120" required /></AwField>
      <AwField id="character-world" :label="customerCopy.text('profile.world')"><input id="character-world" v-model="form.world" class="aw-input" maxlength="160" required /></AwField>
      <AwField id="character-version" :label="customerCopy.text('profile.version')"><input id="character-version" v-model="form.version" class="aw-input" maxlength="120" /></AwField>
      <AwField id="character-description" :label="customerCopy.text('profile.description')"><textarea id="character-description" v-model="form.description" class="aw-input" maxlength="1200" rows="4" /></AwField>
      <AwField id="character-language" :label="customerCopy.text('new-character.language')"><input id="character-language" v-model="form.language" class="aw-input" maxlength="35" /></AwField>
      <AwField id="character-openai-consent" :label="customerCopy.text('profile.optional-ai-title')"><label><input id="character-openai-consent" v-model="enrichmentOpenAiConsent" type="checkbox" /> {{ customerCopy.text('customer.enrichment-consent') }}</label></AwField>
      <p class="aw-muted">{{ customerCopy.text('customer.no-ai-save') }}</p>
      <p v-if="profile" class="aw-muted">{{ customerCopy.text('customer.preparation-status') }}: {{ customerCopy.text(profileStatusKeys[profile.status] ?? 'customer.status-unknown') }}</p>
      <AwNotice v-if="profile && clarificationText" tone="info">{{ customerCopy.text('customer.clarification', { text: clarificationText }) }}</AwNotice>
      <details v-if="enrichmentText || evidenceSections.length">
        <summary>{{ customerCopy.text('customer.preparation-details') }}</summary>
        <p v-if="enrichmentText" class="character-enrichment">{{ enrichmentText }}</p>
        <section v-for="section in evidenceSections" :key="section.title" class="aw-card"><h2>{{ section.title }}</h2><ul><li v-for="line in section.lines" :key="line">{{ line }}</li></ul></section>
      </details>
      <div class="customer-actions"><AwButton type="submit" variant="primary" :busy="busy">{{ customerCopy.text(profile ? 'customer.save-changes' : 'customer.create-character') }}</AwButton><AwButton v-if="profile" type="button" :disabled="busy || !enrichmentOpenAiConsent" @click="requestCorrection">{{ customerCopy.text('customer.not-my-character') }}</AwButton><AwButton v-if="profile" type="button" variant="danger" :disabled="busy" @click="deleteCharacter">{{ customerCopy.text('customer.delete-profile') }}</AwButton></div>
      <p v-if="profile" class="aw-muted">{{ customerCopy.text('customer.revision', { revision: profile.revision }) }}</p>
    </form>
      </details>
      <RouterLink to="/me/account">{{ customerCopy.text('customer.account-help') }}</RouterLink>
      <NewCharacterDialog :open="newCharacterOpen" :busy="busy" :error="newCharacterError ? (newCharacterError.copyKey ? customerCopy.text(newCharacterError.copyKey) : newCharacterError.message) : ''" :current-character-name="profile?.userInput.name" :active-session="startInProgress" @cancel="cancelNewCharacter" @confirm="draft => saveCharacter(true, draft)" />
    </section>

    <section v-else-if="pane === 'conversation'" class="page-stack">
      <CustomerWaiting :device-id="connected?.deviceId ?? null" :profile-id="profile?.profileId ?? null" :proximity="connected ? { bootId: connected.bootId, challenge: connected.challenge } : null" @claim="acceptWaitingClaim" />
      <AwNotice v-if="!customerBle.supported()" tone="info">{{ customerCopy.text('customer.bluetooth-unsupported') }}<br><a href="/ios/">{{ customerCopy.text('customer.install-app') }}</a></AwNotice>
      <div class="aw-card"><h2>{{ customerCopy.text('profile.title') }}</h2><p v-if="profile">{{ profile.userInput.name }} · {{ profile.userInput.world }} ({{ customerCopy.text('customer.revision', { revision: profile.revision }) }})</p><p v-else>{{ customerCopy.text('customer.first-create') }}</p><RouterLink to="/me/character">{{ customerCopy.text('customer.edit-profile') }}</RouterLink></div>
      <div class="aw-card customer-form"><h2>{{ customerCopy.text('customer.connection') }}</h2><p>{{ customerCopy.text('customer.bluetooth-manual') }}</p>
        <AwButton variant="primary" :disabled="busy || !profile || !customerBle.supported()" @click="discover">{{ customerCopy.text('customer.find-suitcase') }}</AwButton>
        <div v-if="devices.length" class="customer-actions"><label for="customer-device">{{ customerCopy.text('customer.suitcase') }}</label><select id="customer-device" v-model="selectedDevice" class="aw-input"><option value="">{{ customerCopy.text('customer.choose-suitcase') }}</option><option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option></select><AwButton :disabled="busy || !selectedDevice" @click="connect">{{ customerCopy.text('customer.connect') }}</AwButton></div>
        <p v-if="connected" role="status">{{ connected.state === 'running' ? customerCopy.text('customer.live-running') : connected.state === 'starting' ? customerCopy.text('customer.live-starting') : connected.state === 'idle' ? customerCopy.text('customer.live-idle') : customerCopy.text('customer.live-restored') }}</p>
        <AwField id="conversation-openai-consent" :label="customerCopy.text('customer.consent-label')"><label><input id="conversation-openai-consent" v-model="conversationOpenAiConsent" type="checkbox" /> {{ customerCopy.text('customer.consent-copy') }}</label></AwField>
        <p class="aw-muted">{{ customerCopy.text('customer.consent-required') }}</p>
        <AwField id="personal-memory-consent" :label="customerCopy.text('customer.memory-label')"><label><input id="personal-memory-consent" v-model="personalMemoryConsent" type="checkbox" /> {{ customerCopy.text('customer.memory-consent') }}</label></AwField>
        <AwButton variant="primary" :busy="busy" :disabled="busy || !profile || !connected?.capabilities.customerStart || startInProgress || (!conversationOpenAiConsent && !pendingOperationId)" @click="startConversation">{{ customerCopy.text('customer.start-conversation') }}</AwButton>
        <p v-if="pendingOperationId" role="status">{{ customerCopy.text('customer.pending-start') }}</p>
      </div>
      <div class="aw-card session-recovery" aria-live="polite">
        <p v-if="sessionRecoveryState.refreshing" role="status">{{ customerCopy.text('customer.refreshing-sessions') }}</p>
        <p v-else-if="sessionRecoveryState.retrying" role="status">{{ customerCopy.text('customer.retrying-sessions') }}</p>
        <AwNotice v-if="sessionRecoveryState.stale" tone="warning" role="alert">
          {{ sessionRecoveryFeedback.copyKey ? customerCopy.text(sessionRecoveryFeedback.copyKey) : sessionRecoveryFeedback.message }}
          <small v-if="sessionRecoveryFeedback.diagnosticCode"> {{ customerCopy.text('customer.diagnostic', { code: sessionRecoveryFeedback.diagnosticCode }) }}</small>
        </AwNotice>
        <AwButton variant="secondary" :disabled="sessionRecoveryState.refreshing" @click="refreshConversationStatus">{{ customerCopy.text('customer.refresh-sessions') }}</AwButton>
      </div>
      <div v-for="session in sessions" :key="session.id" class="aw-card"><h2>{{ session.characterName || profile?.userInput.name || customerCopy.text('customer.conversation') }}</h2><p>{{ customerCopy.text('customer.status-label', { status: customerCopy.text(sessionStatusKeys[session.state] ?? 'customer.status-unknown') }) }}</p><p>{{ customerCopy.text('customer.started-at', { date: new Date(session.createdAt * 1000).toLocaleString(interfaceLanguage === 'en' ? 'en-US' : 'cs-CZ') }) }}</p><AwButton v-if="['running', 'starting', 'context_ready', 'recovering'].includes(session.state)" variant="secondary" :disabled="busy || session.endRequested" @click="endConversation(session)">{{ customerCopy.text(session.endRequested ? 'customer.end-requested' : 'customer.end-conversation') }}</AwButton></div>
      <section v-if="resultSession" class="page-stack">
        <AwField v-if="resultSessions.length > 1" id="result-session" :label="customerCopy.language.value === 'en' ? 'Conversation result' : 'Výsledek rozhovoru'">
          <select id="result-session" v-model="resultSessionId" class="aw-input">
            <option :value="null">{{ customerCopy.language.value === 'en' ? 'Latest result' : 'Nejnovější výsledek' }}</option>
            <option v-for="session in resultSessions" :key="session.id" :value="session.id">{{ session.characterName || customerCopy.text('customer.conversation') }} · {{ new Date(session.createdAt * 1000).toLocaleString(interfaceLanguage === 'en' ? 'en-US' : 'cs-CZ') }}</option>
          </select>
        </AwField>
        <CustomerSessionResultCard :key="resultSession.id" :session-id="resultSession.id" />
      </section>
    </section>

    <section v-else-if="pane === 'keepsakes'" class="page-stack">
      <CustomerCompletionUpdates :locale="interfaceLanguage" />
      <AccountArchiveList :locale="interfaceLanguage" />
      <CustomerDuelHistory />
    </section>
    <section v-else-if="pane === 'account'" class="page-stack">
      <ConversationPreferences v-if="profile" :profile-id="profile.profileId" :profile-revision="profile.revision" />
      <SettingsView customer-surface />
    </section>
  </section>
</template>

<style scoped>
.customer-page { max-width: 54rem; }
.customer-form { display: grid; gap: 1rem; }
.customer-actions { display: flex; flex-wrap: wrap; align-items: end; gap: .65rem; }
.character-enrichment { white-space: pre-line; padding: 1rem; border-left: 2px solid var(--aw-accent); background: var(--aw-surface); }
@media (max-width: 720px) { .customer-actions { display: grid; grid-template-columns: minmax(0, 1fr); } }
</style>

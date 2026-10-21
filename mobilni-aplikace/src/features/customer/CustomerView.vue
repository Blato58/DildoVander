<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { secureUuid } from '../../application/identifiers.mjs';
import { account, accountRequest } from '../identity/session';
import AccountArchiveList from '../identity/AccountArchiveList.vue';
import SettingsView from '../identity/SettingsView.vue';
import { customerBle, type CustomerBleDevice, type CustomerBleStatus } from './customerBle';

interface CharacterProfile {
  profileId: string; revision: number;
  userInput: { name: string; world: string; version?: string; description?: string; language?: string };
  status: string; enrichment?: unknown; errorCode?: string; updatedAt?: string;
}
interface CustomerSession {
  id: string; operationId: string; deviceId: string; profileId: string; profileRevision: number;
  state: string; endRequested: boolean; createdAt: number; characterName?: string;
}
interface StartTicketRequest {
  protocolVersion: 1; operationId: string; deviceId: string; bootId: string; challenge: string;
  profileId: string; profileRevision: number; personalMemoryConsent?: true;
}
const route = useRoute();
const pane = computed(() => route.path.split('/').at(-1) ?? 'character');
const profile = ref<CharacterProfile | null>(null);
const sessions = ref<CustomerSession[]>([]);
const busy = ref(false);
const profileLoading = ref(false);
const error = ref('');
const notice = ref('');
const devices = ref<CustomerBleDevice[]>([]);
const connected = ref<CustomerBleStatus | null>(null);
const selectedDevice = ref('');
const personalMemoryConsent = ref(false);
const form = ref({ name: '', world: '', version: '', description: '', language: '' });
let generation = 0;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
let pollController: AbortController | undefined;
const pendingOperationId = ref<string>();
const pendingTicketRequest = ref<StartTicketRequest>();
let bleStartAttempted = false;
let pendingPollCount = 0;
let enrichmentTimer: ReturnType<typeof setTimeout> | undefined;
const MAX_PENDING_POLLS = 12;

const headings: Record<string, string> = {
  character: 'Moje postava', conversation: 'Rozhovor s kufrem', keepsakes: 'Moje památky', account: 'Můj účet',
};
const startInProgress = computed(() => sessions.value.some(session => ['starting', 'context_ready', 'running', 'recovering'].includes(session.state)));
const characterStatusLabels: Record<string, string> = { queued: 'Čeká na přípravu', running: 'Příprava probíhá', ready: 'Připraveno', needs_clarification: 'Potřebuje upřesnění', failed: 'Příprava se nezdařila' };
const sessionStatusLabels: Record<string, string> = { queued: 'Čeká na kufr', starting: 'Kufr se připravuje', context_ready: 'Rozhovor se připravuje', running: 'Probíhá', completed: 'Dokončen', ended: 'Ukončen', failed: 'Nepodařilo se spustit', recovering: 'Obnovuje se' };
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
  pollController?.abort(); pollController = undefined;
  if (pollTimer) clearTimeout(pollTimer); pollTimer = undefined;
  if (enrichmentTimer) clearTimeout(enrichmentTimer); enrichmentTimer = undefined;
  profile.value = null; sessions.value = []; devices.value = []; connected.value = null; profileLoading.value = Boolean(account.value);
  busy.value = false;
  selectedDevice.value = ''; pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false; pendingPollCount = 0; error.value = ''; notice.value = '';
  form.value = { name: '', world: '', version: '', description: '', language: '' }; personalMemoryConsent.value = false;
  void Promise.resolve(customerBle.disconnect()).catch(() => {});
}

async function loadProfile(epoch = generation): Promise<void> {
  if (!account.value) return;
  profileLoading.value = true;
  try {
    const result = await accountRequest<{ profile: CharacterProfile | null }>('/app/v1/customer/character');
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

async function loadSessions(epoch = generation, signal?: AbortSignal): Promise<{ items: CustomerSession[]; expiredOperations?: string[] } | undefined> {
  const result = await accountRequest<{ items: CustomerSession[]; expiredOperations?: string[] }>('/app/v1/customer/sessions', 'GET', undefined, signal);
  if (epoch !== generation) return undefined;
  sessions.value = result.items;
  if (pendingOperationId.value && result.expiredOperations?.includes(pendingOperationId.value)) {
    pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false; pendingPollCount = 0;
    notice.value = 'Předchozí žádost vypršela dříve, než ji kufr přijal. Zkontrolujte připojení a stiskněte znovu „Promluvit s kufrem“ pro novou žádost.';
  }
  return result;
}

async function saveCharacter(newCharacter = false): Promise<void> {
  error.value = ''; notice.value = ''; busy.value = true; const epoch = generation;
  try {
    const expectedRevision = profile.value?.revision ?? 0;
    const result = await accountRequest<{ profile: CharacterProfile }>('/app/v1/customer/character', 'PUT', {
      expectedRevision, expectedProfileId: profile.value?.profileId ?? null,
      userInput: { name: form.value.name.trim(), world: form.value.world.trim(), version: form.value.version.trim() || undefined,
        description: form.value.description.trim() || undefined, language: form.value.language.trim() || undefined },
      ...(newCharacter && profile.value ? { newCharacter: true } : {}),
    });
    if (epoch !== generation) return;
    profile.value = result.profile;
    scheduleEnrichmentRefresh(epoch);
    notice.value = newCharacter ? 'Nová postava byla založena. Historie předchozí postavy zůstává uložená.' : 'Profil postavy byl uložen.';
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function requestCorrection(): Promise<void> {
  if (!profile.value) return;
  error.value = ''; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character/correction', 'POST', {
      expectedRevision: profile.value.revision, expectedProfileId: profile.value.profileId,
    });
    if (epoch === generation) { notice.value = 'Označili jste, že tato postava není vaše. Příprava informací se obnoví.'; await refreshEnrichment(epoch); }
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function deleteCharacter(): Promise<void> {
  if (!profile.value || !window.confirm('Smazat profil postavy? Historie rozhovorů zůstane zachována.')) return;
  error.value = ''; busy.value = true; const epoch = generation;
  try {
    await accountRequest('/app/v1/customer/character?expectedRevision=' + profile.value.revision + '&expectedProfileId=' + encodeURIComponent(profile.value.profileId), 'DELETE');
    if (epoch === generation) { profile.value = null; form.value = { name: '', world: '', version: '', description: '', language: '' }; notice.value = 'Profil postavy byl smazán.'; }
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function discover(): Promise<void> {
  error.value = ''; devices.value = []; connected.value = null;
  if (!customerBle.supported()) { error.value = 'Bluetooth start je dostupný v instalované aplikaci pro iPhone. Safari nepodporuje tuto kufrovou funkci.'; return; }
  const epoch = generation; busy.value = true;
  try { const result = await customerBle.discover(); if (epoch === generation) devices.value = result.devices; }
  catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function connect(): Promise<void> {
  if (!selectedDevice.value) return;
  error.value = ''; busy.value = true; const epoch = generation;
  try { const status = await customerBle.connect(selectedDevice.value); if (epoch === generation) connected.value = status; }
  catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function startConversation(): Promise<void> {
  if (!profile.value || !connected.value || !connected.value.capabilities.customerStart) return;
  error.value = ''; notice.value = ''; busy.value = true; const epoch = generation;
  // A retried operation keeps its ID; a rejected or unknown outcome is reconciled first.
  const hadPendingOperation = Boolean(pendingOperationId.value);
  const operationId = pendingOperationId.value ?? secureUuid(); pendingOperationId.value = operationId;
  if (!hadPendingOperation) bleStartAttempted = false;
  try {
    const existing = await loadSessions(epoch);
    if (epoch !== generation) return;
    if (!existing) return;
    if (existing.expiredOperations?.includes(operationId)) return;
    const prior = existing.items.find(item => item.operationId === operationId);
    if (prior) {
      const status = await accountRequest<CustomerSession>(`/app/v1/customer/sessions/${encodeURIComponent(prior.id)}`);
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
      request = {
        protocolVersion: 1, operationId, deviceId: connected.value.deviceId, bootId: connected.value.bootId,
        challenge: connected.value.challenge, profileId: profile.value.profileId, profileRevision: profile.value.revision,
        ...(personalMemoryConsent.value ? { personalMemoryConsent: true } : {}),
      };
      pendingTicketRequest.value = request;
    }
    const ticket = await accountRequest<{ ticket: string; operationId: string; expiresAt: string; sessionId?: string }>('/app/v1/customer/start-tickets', 'POST', request);
    if (epoch !== generation) return;
    bleStartAttempted = true;
    const bleStatus = await customerBle.start(ticket.ticket, operationId);
    if (epoch !== generation) return;
    connected.value = bleStatus;
    if (bleStatus.sessionId) {
      const status = await accountRequest<CustomerSession>(`/app/v1/customer/sessions/${encodeURIComponent(bleStatus.sessionId)}`);
      if (epoch === generation) sessions.value = [status, ...sessions.value.filter(item => item.id !== status.id)];
      if (epoch === generation && isTerminal(status.state)) {
        pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false;
        await reconcileBle(epoch);
      }
    }
    pendingPollCount = 0;
    scheduleSessionPoll(epoch);
  } catch (cause) {
    if (epoch === generation) {
      error.value = (cause as Error).message;
      if (!bleStartAttempted && isDefinitiveTicketIssuerRejection(cause)) {
        pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; pendingPollCount = 0;
        notice.value = 'Kufr odmítl žádost ještě před pokusem o připojení. Stiskněte znovu „Promluvit s kufrem“ pro novou žádost.';
      }
      try { await loadSessions(epoch); await reconcileBle(epoch); } catch { /* Keep the original start error. */ }
      if (epoch === generation) scheduleSessionPoll(epoch);
    }
  } finally { if (epoch === generation) busy.value = false; }
}

async function endConversation(session: CustomerSession): Promise<void> {
  error.value = ''; busy.value = true; const epoch = generation;
  try {
    await accountRequest(`/app/v1/customer/sessions/${encodeURIComponent(session.id)}/end`, 'POST');
    if (epoch !== generation) return;
    notice.value = 'Konec rozhovoru byl požádán.';
    try { connected.value = await customerBle.end(session.id); } catch { /* Phone disconnect never becomes a stop. */ }
    await pollSessions(epoch);
  } catch (cause) { if (epoch === generation) error.value = (cause as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}

async function pollSessions(epoch: number): Promise<void> {
  pollController?.abort(); pollController = new AbortController();
  try {
    await loadSessions(epoch, pollController.signal);
    if (epoch !== generation) return;
    const pendingSession = pendingOperationId.value && sessions.value.find(item => item.operationId === pendingOperationId.value);
    if (pendingSession && isTerminal(pendingSession.state)) {
      pendingOperationId.value = undefined; pendingTicketRequest.value = undefined; bleStartAttempted = false; pendingPollCount = 0;
      await reconcileBle(epoch);
    }
    const keepChecking = Boolean(pendingOperationId.value && pendingPollCount < MAX_PENDING_POLLS)
      || sessions.value.some(item => ['starting', 'context_ready', 'running', 'recovering'].includes(item.state));
    if (pendingOperationId.value) pendingPollCount += 1;
    if (keepChecking && epoch === generation) pollTimer = setTimeout(() => { void pollSessions(epoch); }, 2500);
  } catch (cause) { if (epoch === generation && (cause as Error).name !== 'AbortError') error.value = (cause as Error).message; }
}
async function refreshConversationStatus(): Promise<void> {
  const epoch = generation;
  if (busy.value) return;
  error.value = ''; busy.value = true;
  try {
    await pollSessions(epoch);
    if (epoch === generation && pendingOperationId.value && !pollTimer) notice.value = 'Stav se stále ověřuje. Při dalším pokusu se použije stejná žádost.';
  } finally { if (epoch === generation) busy.value = false; }
}

function isTerminal(state: string): boolean { return !['queued', 'starting', 'context_ready', 'running', 'recovering'].includes(state); }
function isDefinitiveTicketIssuerRejection(error: unknown): boolean {
  if (!error || typeof error !== 'object') return false;
  const status = 'status' in error && typeof error.status === 'number' ? error.status : 0;
  const code = 'code' in error && typeof error.code === 'string' ? error.code : '';
  return (status === 409 && ['device_challenge_unavailable', 'character_revision_mismatch'].includes(code))
    || (status === 410 && code === 'customer_profile_revoked')
    || (status === 404 && code === 'character_not_found')
    || (status === 429 && code === 'start_ticket_limit');
}
function scheduleSessionPoll(epoch: number): void {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = setTimeout(() => { void pollSessions(epoch); }, 2500);
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
    const result = await accountRequest<{ profile: CharacterProfile | null }>('/app/v1/customer/character');
    if (epoch !== generation || !result.profile || result.profile.profileId !== profile.value?.profileId || result.profile.revision !== profile.value.revision) return;
    // Refresh server-derived enrichment only; never replace the visitor's in-progress form edits.
    profile.value = { ...profile.value, status: result.profile.status, enrichment: result.profile.enrichment, errorCode: result.profile.errorCode, updatedAt: result.profile.updatedAt };
    scheduleEnrichmentRefresh(epoch);
  } catch { if (epoch === generation) scheduleEnrichmentRefresh(epoch); }
}

watch(() => account.value?.id, (next, previous) => {
  if (next !== previous) { resetPrivateState(); if (next) void loadProfile(); }
}, { flush: 'sync' });
onMounted(() => { if (account.value) { const epoch = generation; void loadProfile(epoch); void loadSessions(epoch).then(() => { if (sessions.value.some(item => ['starting', 'context_ready', 'running', 'recovering'].includes(item.state))) void pollSessions(epoch); }).catch(cause => { if (epoch === generation) error.value = (cause as Error).message; }); } });
onBeforeUnmount(resetPrivateState);
</script>

<template>
  <section class="page-stack customer-page" :aria-labelledby="`customer-${pane}-title`">
    <header><p class="eyebrow">Můj prostor</p><h1 :id="`customer-${pane}-title`">{{ headings[pane] ?? 'Můj prostor' }}</h1><p v-if="pane === 'character'">Vyberte postavu a její svět. Ověřené informace připraví server na pozadí.</p><p v-else-if="pane === 'conversation'">Rozhovor spustíte ručně po připojení ke kufru.</p></header>
    <AwNotice v-if="error" tone="warning" role="alert">{{ error }}</AwNotice>
    <AwNotice v-if="notice" tone="info" role="status">{{ notice }}</AwNotice>

    <p v-if="pane === 'character' && profileLoading" role="status">Načítám váš profil…</p>
    <form v-if="pane === 'character' && !profileLoading" class="aw-card customer-form" @submit.prevent="saveCharacter(false)">
      <AwField id="character-name" label="Jméno postavy"><input id="character-name" v-model="form.name" class="aw-input" maxlength="120" required /></AwField>
      <AwField id="character-world" label="Film, seriál, hra nebo kniha"><input id="character-world" v-model="form.world" class="aw-input" maxlength="160" required /></AwField>
      <AwField id="character-version" label="Verze nebo období (nepovinné)"><input id="character-version" v-model="form.version" class="aw-input" maxlength="120" /></AwField>
      <AwField id="character-description" label="Co je pro vás důležité? (nepovinné)"><textarea id="character-description" v-model="form.description" class="aw-input" maxlength="1200" rows="4" /></AwField>
      <AwField id="character-language" label="Jazyk (nepovinné)"><input id="character-language" v-model="form.language" class="aw-input" maxlength="35" /></AwField>
      <p v-if="profile" class="aw-muted">Stav přípravy: {{ characterStatusLabels[profile.status] ?? 'Stav se ověřuje' }}<span v-if="profile.errorCode"> · {{ profile.errorCode }}</span></p>
      <AwNotice v-if="profile && clarificationText" tone="info">Server potřebuje upřesnit některé informace: {{ clarificationText }}</AwNotice>
      <p v-if="enrichmentText" class="character-enrichment">{{ enrichmentText }}</p>
      <section v-for="section in evidenceSections" :key="section.title" class="aw-card"><h2>{{ section.title }}</h2><ul><li v-for="line in section.lines" :key="line">{{ line }}</li></ul></section>
      <div class="customer-actions"><AwButton type="submit" variant="primary" :busy="busy">{{ profile ? 'Uložit změny' : 'Vytvořit postavu' }}</AwButton><AwButton v-if="profile" :disabled="busy" @click="saveCharacter(true)">Nová postava</AwButton><AwButton v-if="profile" :disabled="busy" @click="requestCorrection">Tohle není moje postava</AwButton><AwButton v-if="profile" variant="danger" :disabled="busy" @click="deleteCharacter">Smazat profil</AwButton></div>
      <p v-if="profile" class="aw-muted">Revize {{ profile.revision }}</p>
    </form>

    <section v-else-if="pane === 'conversation'" class="page-stack">
      <AwNotice v-if="!customerBle.supported()" tone="info">Bluetooth připojení je dostupné v instalované aplikaci pro iPhone. Safari může zobrazit účet a památky, ale neumí připojit kufr.<br><a href="/ios/">Otevřít instalaci aplikace pro iPhone</a></AwNotice>
      <div class="aw-card"><h2>Vaše postava</h2><p v-if="profile">{{ profile.userInput.name }} · {{ profile.userInput.world }} (revize {{ profile.revision }})</p><p v-else>Nejprve vytvořte postavu.</p><RouterLink to="/me/character">Upravit postavu</RouterLink></div>
      <div class="aw-card customer-form"><h2>Připojení ke kufru</h2><p>Bluetooth se zapne až po stisknutí tlačítka. Připojení telefonu samo rozhovor nezastaví.</p>
        <AwButton variant="primary" :disabled="busy || !profile || !customerBle.supported()" @click="discover">Najít kufr poblíž</AwButton>
        <div v-if="devices.length" class="customer-actions"><label for="customer-device">Kufr</label><select id="customer-device" v-model="selectedDevice" class="aw-input"><option value="">Vyberte kufr</option><option v-for="device in devices" :key="device.id" :value="device.id">{{ device.name }}</option></select><AwButton :disabled="busy || !selectedDevice" @click="connect">Připojit</AwButton></div>
        <p v-if="connected" role="status">{{ connected.state === 'running' ? 'Rozhovor právě probíhá' : connected.state === 'starting' ? 'Rozhovor se připravuje' : connected.state === 'idle' ? 'Kufr je připojený' : 'Stav kufru byl obnoven' }}</p>
        <AwField id="personal-memory-consent" label="Soukromá kontinuita"><label><input id="personal-memory-consent" v-model="personalMemoryConsent" type="checkbox" /> Souhlasím s použitím mé ověřené osobní paměti v tomto rozhovoru</label></AwField>
        <AwButton variant="primary" :busy="busy" :disabled="busy || !profile || !connected?.capabilities.customerStart || startInProgress" @click="startConversation">Promluvit s kufrem</AwButton>
        <p v-if="pendingOperationId" role="status">Ověřujeme, zda kufr rozhovor přijal. Telefonní spojení rozhovor neukončuje.</p>
        <AwButton v-if="pendingOperationId" :disabled="busy" variant="secondary" @click="refreshConversationStatus">Obnovit stav rozhovoru</AwButton>
      </div>
      <div v-for="session in sessions" :key="session.id" class="aw-card"><h2>{{ session.characterName || profile?.userInput.name || 'Rozhovor' }}</h2><p>Stav: {{ sessionStatusLabels[session.state] ?? 'Stav se ověřuje' }}</p><p>Začátek: {{ new Date(session.createdAt * 1000).toLocaleString('cs') }}</p><AwButton v-if="session.state === 'running' || session.state === 'starting' || session.state === 'context_ready'" variant="secondary" :disabled="busy || session.endRequested" @click="endConversation(session)">{{ session.endRequested ? 'Konec požádán' : 'Ukončit rozhovor' }}</AwButton></div>
    </section>

    <AccountArchiveList v-else-if="pane === 'keepsakes'" />
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

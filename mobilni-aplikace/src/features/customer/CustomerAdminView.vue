<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest } from '../identity/session';
import { projectCustomerSessionError } from './customerSessionFeedback';
import { SessionRecoveryController, type SessionRecoveryState } from './sessionRecovery';

interface Device { deviceId: string; enabled: boolean; name?: string; manageable: boolean }
interface Session { id: string; operationId: string; deviceId: string; state: string; endRequested: boolean; createdAt: number; characterName?: string }
interface EnrichmentJob { profileId: string; revision: number; status: string; attempt: number; errorCode?: string; updatedAt: number }
const devices = ref<Device[]>([]);
const sessions = ref<Session[]>([]);
const enrichmentJobs = ref<EnrichmentJob[]>([]);
const busy = ref(false);
const error = ref('');
const recoveryState = reactive<SessionRecoveryState>({ refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined });
const recoveryFeedback = computed(() => projectCustomerSessionError(recoveryState.error));
let generation = 0;
let controller: AbortController | undefined;
let accountWorkController = new AbortController();
const requestedEndSessions = new Set<string>();

const sessionRecovery = new SessionRecoveryController<Session[]>({
  refresh: refreshAdminSessions,
  hasActiveSession: items => isRecoveryVisible() && items.some(isActiveSession),
  state: recoveryState,
  pollIntervalMs: 2500,
  retryBaseDelayMs: 1000,
  retryMaxDelayMs: 30_000,
});

async function load(): Promise<void> {
  if (!account.value) return;
  const epoch = ++generation; controller?.abort(); controller = new AbortController();
  const { signal } = controller; busy.value = true; error.value = '';
  try {
    const isAdministrator = account.value?.roles.some(role => ['Owner', 'Administrator'].includes(role)) ?? false;
    const [sessionResult, accountDetails, jobsResult] = await Promise.all([
      isRecoveryVisible() ? sessionRecovery.setActive(true) ?? sessionRecovery.refresh() : sessionRecovery.refresh(),
      accountRequest<{ devices?: { deviceId: string }[]; grants?: { resourceType: string; resourceId: string; permission: string }[] }>('/app/v1/account', 'GET', undefined, signal),
      isAdministrator ? accountRequest<{ items: EnrichmentJob[] }>('/app/v1/customer/admin/characters', 'GET', undefined, signal) : Promise.resolve({ items: [] as EnrichmentJob[] }),
    ]);
    if (generation !== epoch) return;
    enrichmentJobs.value = jobsResult.items;
    const activeSessions = sessionResult ?? sessions.value;
    const deviceIds = [...new Set([...(accountDetails.devices ?? []).map(item => item.deviceId), ...activeSessions.map(item => item.deviceId)])];
    const deviceResults = await Promise.all(deviceIds.map(async deviceId => {
      const permission = accountDetails.grants?.find(grant => grant.resourceType === 'device' && grant.resourceId === deviceId && grant.permission === 'manage');
      const accountRole = account.value?.roles.some(role => ['Owner', 'Administrator'].includes(role));
      try {
        const device = await accountRequest<{ deviceId: string; enabled: boolean; name?: string }>(`/app/v1/customer/admin/devices/${encodeURIComponent(deviceId)}`, 'GET', undefined, signal);
        return { ...device, manageable: true };
      } catch (cause) {
        if (permission || accountRole) throw cause;
        return { deviceId, enabled: false, manageable: false };
      }
    }));
    if (generation === epoch) devices.value = deviceResults;
  } catch (cause) { if (generation === epoch) error.value = (cause as Error).message; }
  finally { if (generation === epoch) busy.value = false; }
}

async function toggle(device: Device): Promise<void> {
  const epoch = generation; busy.value = true; error.value = '';
  try {
    if (!device.manageable) return;
    await accountRequest(`/app/v1/customer/admin/devices/${encodeURIComponent(device.deviceId)}`, 'PUT', { enabled: !device.enabled }, accountWorkController.signal);
    if (generation === epoch) await load();
  } catch (cause) { if (generation === epoch) error.value = (cause as Error).message; }
  finally { if (generation === epoch) busy.value = false; }
}

async function end(session: Session): Promise<void> {
  if (!window.confirm('Požádat kufr o ukončení tohoto rozhovoru?')) return;
  const epoch = generation; busy.value = true; error.value = '';
  try {
    await accountRequest<{ state: string }>(`/app/v1/customer/admin/sessions/${encodeURIComponent(session.id)}/end`, 'POST', undefined, accountWorkController.signal);
    if (generation === epoch) {
      requestedEndSessions.add(session.id);
      sessions.value = sessions.value.map(item => item.id === session.id ? { ...item, endRequested: true } : item);
      void sessionRecovery.setActive(true);
      await load();
    }
  } catch (cause) { if (generation === epoch) error.value = projectCustomerSessionError(cause).message; }
  finally { if (generation === epoch) busy.value = false; }
}

async function refreshAdminSessions(signal: AbortSignal): Promise<Session[]> {
  const result = await accountRequest<{ items: Session[] }>('/app/v1/customer/admin/sessions', 'GET', undefined, signal);
  if (signal.aborted) throw new DOMException('Načítání relací bylo zrušeno.', 'AbortError');
  sessions.value = result.items.map(item => requestedEndSessions.has(item.id) ? { ...item, endRequested: true } : item);
  for (const session of result.items) if (isTerminal(session.state)) requestedEndSessions.delete(session.id);
  return result.items;
}

function isTerminal(state: string): boolean { return ['completed', 'failed'].includes(state); }
function isActiveSession(session: Session): boolean { return Boolean(session.state) && !isTerminal(session.state); }
function isRecoveryVisible(): boolean { return Boolean(account.value) && document.visibilityState !== 'hidden'; }
function onVisible(): void { if (isRecoveryVisible()) void (sessionRecovery.setActive(true) ?? sessionRecovery.refresh()); else void sessionRecovery.setActive(false); }
function onOnline(): void { if (isRecoveryVisible()) void (sessionRecovery.setActive(true) ?? sessionRecovery.refresh()); }

watch(() => account.value?.id, (next, previous) => {
  if (next === previous) return;
  generation += 1;
  controller?.abort();
  accountWorkController.abort(); accountWorkController = new AbortController();
  sessionRecovery.invalidate();
  requestedEndSessions.clear();
  devices.value = []; sessions.value = []; enrichmentJobs.value = []; busy.value = false; error.value = '';
  if (next) void load();
}, { flush: 'sync' });

onMounted(() => {
  window.addEventListener('online', onOnline);
  document.addEventListener('visibilitychange', onVisible);
  void load();
});
onBeforeUnmount(() => {
  generation += 1;
  controller?.abort();
  accountWorkController.abort();
  sessionRecovery.dispose();
  window.removeEventListener('online', onOnline);
  document.removeEventListener('visibilitychange', onVisible);
  devices.value = []; sessions.value = []; enrichmentJobs.value = [];
});
</script>

<template>
  <section class="page-stack customer-admin" aria-labelledby="customer-admin-title">
    <header><p class="eyebrow">Obsluha</p><h1 id="customer-admin-title">Zákaznické relace</h1><p>Přehled aktuálních rozhovorů a zákaznického režimu dostupných kufrů. Zákaznické účty a jejich soukromé památky se zde nezobrazují.</p></header>
      <AwNotice v-if="error" tone="warning" role="alert">{{ error }}</AwNotice>
    <p v-if="recoveryState.refreshing" role="status">Obnovuji zákaznické relace…</p>
    <p v-else-if="recoveryState.retrying" role="status">Spojení se serverem se obnovuje. Další pokus proběhne automaticky.</p>
    <AwNotice v-if="recoveryState.stale" tone="warning" role="alert">{{ recoveryFeedback.message }}<small v-if="recoveryFeedback.diagnosticCode"> Diagnostika: {{ recoveryFeedback.diagnosticCode }}</small></AwNotice>
    <div class="admin-toolbar"><AwButton :disabled="busy" @click="load">Obnovit</AwButton></div>
    <section class="aw-card"><h2>Kufry</h2><p v-if="busy && !devices.length" role="status">Načítám kufry…</p><p v-else-if="!devices.length">K tomuto účtu nejsou přiřazené žádné kufry.</p><ul v-else class="device-list"><li v-for="device in devices" :key="device.deviceId"><div><strong>{{ device.name || device.deviceId }}</strong><p>Režim zákaznického startu: {{ device.manageable ? device.enabled ? 'zapnutý' : 'vypnutý' : 'stav není dostupný' }}</p></div><AwButton v-if="device.manageable" :disabled="busy" @click="toggle(device)">{{ device.enabled ? 'Vypnout' : 'Zapnout' }}</AwButton><span v-else>Vyžaduje oprávnění spravovat kufr</span></li></ul></section>
    <section class="aw-card"><h2>Aktuální rozhovory</h2><p v-if="!sessions.length">Není vidět žádná zákaznická relace.</p><ul v-else class="device-list"><li v-for="session in sessions" :key="session.id"><div><strong>{{ session.characterName || 'Rozhovor' }}</strong><p>{{ session.deviceId }} · {{ ({ starting: 'Kufr se připravuje', context_ready: 'Rozhovor se připravuje', running: 'Probíhá', recovering: 'Obnovuje se', completed: 'Dokončen', failed: 'Nepodařilo se spustit' } as Record<string, string>)[session.state] ?? 'Stav se ověřuje' }} · {{ new Date(session.createdAt * 1000).toLocaleString('cs') }}</p></div><AwButton v-if="['queued','starting','context_ready','running','recovering'].includes(session.state)" variant="danger" :disabled="busy || session.endRequested" @click="end(session)">{{ session.endRequested ? 'Konec požádán' : 'Ukončit rozhovor' }}</AwButton></li></ul></section>
    <section v-if="account?.roles.some(role => ['Owner', 'Administrator'].includes(role))" class="aw-card"><h2>Příprava postav</h2><p>Souhrn stavů úloh bez jmen, vstupů nebo identifikátorů účtů.</p><p v-if="!enrichmentJobs.length">Žádná příprava postavy není v evidenci.</p><ul v-else class="device-list"><li v-for="job in enrichmentJobs" :key="`${job.profileId}:${job.revision}`"><div><strong>{{ ({ queued: 'Čeká', running: 'Probíhá', ready: 'Připraveno', needs_clarification: 'Vyžaduje upřesnění', failed: 'Selhalo' } as Record<string, string>)[job.status] ?? 'Neznámý stav' }}</strong><p>Profil {{ job.profileId }} · revize {{ job.revision }} · pokus {{ job.attempt }}<span v-if="job.errorCode"> · {{ job.errorCode }}</span></p><small>Aktualizováno {{ new Date(job.updatedAt * 1000).toLocaleString('cs') }}</small></div></li></ul></section>
  </section>
</template>

<style scoped>
.customer-admin { max-width: 58rem; }
.admin-toolbar { display: flex; justify-content: end; }
.device-list { list-style: none; padding: 0; }
.device-list li { display: flex; align-items: center; justify-content: space-between; gap: 1rem; padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }
.device-list p { margin: .35rem 0 0; color: var(--aw-muted); }
@media (max-width: 560px) { .device-list li { align-items: stretch; flex-direction: column; } }
</style>

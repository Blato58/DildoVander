<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest } from '../identity/session';

interface Device { deviceId: string; enabled: boolean; name?: string; manageable: boolean }
interface Session { id: string; operationId: string; deviceId: string; state: string; endRequested: boolean; createdAt: number; characterName?: string }
interface EnrichmentJob { profileId: string; revision: number; status: string; attempt: number; errorCode?: string; updatedAt: number }
const devices = ref<Device[]>([]);
const sessions = ref<Session[]>([]);
const enrichmentJobs = ref<EnrichmentJob[]>([]);
const busy = ref(false);
const error = ref('');
let generation = 0;
let controller: AbortController | undefined;

async function load(): Promise<void> {
  const epoch = ++generation; controller?.abort(); controller = new AbortController();
  const { signal } = controller; busy.value = true; error.value = '';
  try {
    const isAdministrator = account.value?.roles.some(role => ['Owner', 'Administrator'].includes(role)) ?? false;
    const [sessionResult, accountDetails, jobsResult] = await Promise.all([
      accountRequest<{ items: Session[] }>('/app/v1/customer/admin/sessions', 'GET', undefined, signal),
      accountRequest<{ devices?: { deviceId: string }[]; grants?: { resourceType: string; resourceId: string; permission: string }[] }>('/app/v1/account', 'GET', undefined, signal),
      isAdministrator ? accountRequest<{ items: EnrichmentJob[] }>('/app/v1/customer/admin/characters', 'GET', undefined, signal) : Promise.resolve({ items: [] as EnrichmentJob[] }),
    ]);
    if (generation !== epoch) return;
    sessions.value = sessionResult.items;
    enrichmentJobs.value = jobsResult.items;
    const deviceIds = [...new Set([...(accountDetails.devices ?? []).map(item => item.deviceId), ...sessionResult.items.map(item => item.deviceId)])];
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
    await accountRequest(`/app/v1/customer/admin/devices/${encodeURIComponent(device.deviceId)}`, 'PUT', { enabled: !device.enabled });
    if (generation === epoch) await load();
  } catch (cause) { if (generation === epoch) error.value = (cause as Error).message; }
  finally { if (generation === epoch) busy.value = false; }
}

async function end(session: Session): Promise<void> {
  if (!window.confirm('Požádat kufr o ukončení tohoto rozhovoru?')) return;
  const epoch = generation; busy.value = true; error.value = '';
  try {
    await accountRequest(`/app/v1/customer/admin/sessions/${encodeURIComponent(session.id)}/end`, 'POST');
    if (generation === epoch) await load();
  } catch (cause) { if (generation === epoch) error.value = (cause as Error).message; }
  finally { if (generation === epoch) busy.value = false; }
}

onMounted(load);
onBeforeUnmount(() => { generation += 1; controller?.abort(); devices.value = []; sessions.value = []; enrichmentJobs.value = []; });
</script>

<template>
  <section class="page-stack customer-admin" aria-labelledby="customer-admin-title">
    <header><p class="eyebrow">Obsluha</p><h1 id="customer-admin-title">Zákaznické relace</h1><p>Přehled aktuálních rozhovorů a zákaznického režimu dostupných kufrů. Zákaznické účty a jejich soukromé památky se zde nezobrazují.</p></header>
      <AwNotice v-if="error" tone="warning" role="alert">{{ error }}</AwNotice>
    <div class="admin-toolbar"><AwButton :disabled="busy" @click="load">Obnovit</AwButton></div>
    <section class="aw-card"><h2>Kufry</h2><p v-if="busy && !devices.length" role="status">Načítám kufry…</p><p v-else-if="!devices.length">K tomuto účtu nejsou přiřazené žádné kufry.</p><ul v-else class="device-list"><li v-for="device in devices" :key="device.deviceId"><div><strong>{{ device.name || device.deviceId }}</strong><p>Režim zákaznického startu: {{ device.manageable ? device.enabled ? 'zapnutý' : 'vypnutý' : 'stav není dostupný' }}</p></div><AwButton v-if="device.manageable" :disabled="busy" @click="toggle(device)">{{ device.enabled ? 'Vypnout' : 'Zapnout' }}</AwButton><span v-else>Vyžaduje oprávnění spravovat kufr</span></li></ul></section>
    <section class="aw-card"><h2>Aktuální rozhovory</h2><p v-if="!sessions.length">Není vidět žádná zákaznická relace.</p><ul v-else class="device-list"><li v-for="session in sessions" :key="session.id"><div><strong>{{ session.characterName || 'Rozhovor' }}</strong><p>{{ session.deviceId }} · {{ ({ queued: 'Čeká na kufr', starting: 'Kufr se připravuje', context_ready: 'Rozhovor se připravuje', running: 'Probíhá', completed: 'Dokončen', ended: 'Ukončen', failed: 'Nepodařilo se spustit', recovering: 'Obnovuje se' } as Record<string, string>)[session.state] ?? 'Stav se ověřuje' }} · {{ new Date(session.createdAt * 1000).toLocaleString('cs') }}</p></div><AwButton v-if="['starting','context_ready','running'].includes(session.state)" variant="danger" :disabled="busy || session.endRequested" @click="end(session)">{{ session.endRequested ? 'Konec požádán' : 'Ukončit rozhovor' }}</AwButton></li></ul></section>
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

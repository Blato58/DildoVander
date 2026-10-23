<script setup lang="ts">
import { computed, onBeforeUnmount, onDeactivated, ref, shallowRef, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { debugBridge, nativeDebug as debug } from './nativeDebug';
import { deviceAssociation as cloud } from './deviceAssociation';
import { deviceSession as device } from './deviceSession';
import type { MemoryInspection } from '../debug/conversationCapabilities';
const conversationId = ref(''); const inspection = shallowRef<MemoryInspection>(); const inspectedId = ref('');
const confirmed = ref(false); const message = ref('Čtení nevolá AI. Zvolte konkrétní rozhovor.');
const canAdmin = computed(() => debug.status.value.accessMode === 'legacy-service' || cloud.administrator.value);
const canRead = computed(() => device.native && debug.status.value.configured && canAdmin.value && !debug.pending.value && !!conversationId.value.trim());
const canDelete = computed(() => canRead.value && !debug.status.value.session && !debug.status.value.running
  && !!inspection.value && inspectedId.value === conversationId.value.trim() && confirmed.value);
const storageStatusLabel = computed(() => {
  switch (inspection.value?.memoryStatus.storageStatus) {
    case 'available': return 'Dostupná';
    case 'warning': return 'Varování: využití dosáhlo alespoň 80 %';
    case 'full': return 'Plná';
    default: return 'Stav kapacity není dostupný';
  }
});
const hasStorageCapacity = computed(() => {
  const status = inspection.value?.memoryStatus;
  return !!status && [status.storedBytes, status.capacityBytes].every(value => Number.isFinite(value) && value >= 0);
});
const hasRejectedWriteCount = computed(() => {
  const count = inspection.value?.memoryStatus.rejectedWrites;
  return Number.isSafeInteger(count) && count! >= 0;
});
const lastStorageErrorLabel = computed(() => {
  switch (inspection.value?.memoryStatus.lastStorageErrorCode) {
    case 'archive_capacity': return 'Kapacita společného archivu byla dosažena.';
    case null:
    case undefined: return '';
    default: return 'Poslední odmítnutí mělo jinou příčinu.';
  }
});
const lastStorageErrorTime = computed(() => {
  const value = inspection.value?.memoryStatus.lastStorageErrorAt;
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value)
    || !Number.isFinite(Date.parse(value))) return '';
  return value;
});
function formatBytes(value: number): string { return value.toLocaleString('cs-CZ'); }
function formatMebibytes(value: number): string { return (value / 1_048_576).toLocaleString('cs-CZ', { maximumFractionDigits: 2 }); }
let epoch = 0;
function clear(): void { ++epoch; conversationId.value = ''; inspection.value = undefined; inspectedId.value = ''; confirmed.value = false; message.value = 'Čtení nevolá AI. Zvolte konkrétní rozhovor.'; }
watch(conversationId, () => { ++epoch; inspection.value = undefined; inspectedId.value = ''; confirmed.value = false; }, { flush: 'sync' });
watch(() => `${cloud.account.value?.id ?? ''}:${canAdmin.value}:${debug.status.value.deviceId ?? ''}`, clear, { flush: 'sync' });
function visibility(): void { if (document.hidden) clear(); }
document.addEventListener('visibilitychange', visibility);
onBeforeUnmount(() => { clear(); document.removeEventListener('visibilitychange', visibility); }); onDeactivated(clear);
async function inspect(): Promise<void> {
  if (!canRead.value) return;
  const request = ++epoch; const id = conversationId.value.trim(); confirmed.value = false;
  await debug.run(async () => {
    const value = await debugBridge.inspectMemory({ conversationId: id });
    if (request !== epoch) return;
    inspection.value = value; inspectedId.value = id; message.value = 'Načtená paměť zvoleného rozhovoru.';
  });
}
async function remove(): Promise<void> {
  if (!canDelete.value || !inspection.value) return;
  const request = ++epoch; const id = inspectedId.value; const expectedEpoch = inspection.value.epoch;
  confirmed.value = false;
  await debug.run(async () => {
    const result = await debugBridge.deleteConversationMemory({ conversationId: id, expectedEpoch, confirm: true });
    if (request !== epoch) return;
    inspection.value = undefined; inspectedId.value = '';
    message.value = result.remoteDeletion === 'pending' ? 'Místní paměť odstraněna. Vzdálený úklid čeká na dokončení.' : 'Místní i odvozená vzdálená paměť odstraněna.';
  });
}
</script>
<template>
  <section class="aw-card memory-panel" aria-labelledby="guest-memory-heading"><h2 id="guest-memory-heading">Paměť hosta</h2>
    <p>Správce může zkontrolovat oprávněné záznamy konkrétního rozhovoru. Čtení ani odstranění nevolá AI a nemění mechanický stav kufru.</p>
    <p v-if="!canAdmin" class="aw-notice">Je vyžadován účet správce nebo výslovný servisní přístup.</p>
    <label class="aw-field">ID rozhovoru<input v-model="conversationId" class="aw-input" maxlength="64" autocomplete="off" spellcheck="false" data-memory-conversation></label>
    <AwButton :disabled="!canRead" @click="inspect">Načíst paměť rozhovoru</AwButton><p role="status">{{ message }}</p>
    <template v-if="inspection"><p v-if="inspection.identity">{{ inspection.identity.identityVerified ? 'Ověřená vazba' : 'Neověřená nebo odvolaná vazba' }}: {{ inspection.identity.guestId }} · verze {{ inspection.identity.version }}.<br>Dlouhodobá osobní paměť: {{ inspection.identity.longTermAllowed ? 'povolena' : 'nepovolena' }} · cloudový index: {{ inspection.identity.cloudExportAllowed ? 'povolen' : 'nepovolen' }}.</p><p v-else>Anonymní rozhovor bez trvalé identity.</p><p>Generace {{ inspection.epoch }} · generace smazání {{ inspection.deletionEpoch }}.</p>
      <p>Cloudový index {{ inspection.memoryStatus.configured ? 'nakonfigurovaný' : 'nenakonfigurovaný' }} · {{ inspection.memoryStatus.configurationStatus }} · {{ inspection.memoryStatus.degraded ? 'omezený provoz' : 'běžný stav' }}. Čekající {{ inspection.memoryStatus.pending }}, chybné {{ inspection.memoryStatus.failed }}, úklid {{ inspection.memoryStatus.deleting }}.</p>
      <div class="storage-status" role="group" aria-label="Kapacita sdíleného archivu">
        <h3>Kapacita paměti ve společném globálním archivu</h3>
        <p role="status">Stav úložiště: {{ storageStatusLabel }}.</p>
        <p v-if="hasStorageCapacity">Využito {{ formatMebibytes(inspection.memoryStatus.storedBytes) }} MiB ({{ formatBytes(inspection.memoryStatus.storedBytes) }} B) z {{ formatMebibytes(inspection.memoryStatus.capacityBytes) }} MiB ({{ formatBytes(inspection.memoryStatus.capacityBytes) }} B).</p>
        <p v-else>Údaje o využití a kapacitě nejsou dostupné.</p>
        <p v-if="hasRejectedWriteCount">Odmítnuté zápisy: {{ formatBytes(inspection.memoryStatus.rejectedWrites) }}.</p>
        <p v-if="inspection.memoryStatus.storageIncomplete" class="aw-notice">Některé zápisy v minulosti nebyly uloženy. Tento přehled nepotvrzuje uložení všech zápisů.</p>
        <p v-if="lastStorageErrorLabel">{{ lastStorageErrorLabel }}<template v-if="lastStorageErrorTime"> Čas posledního odmítnutí: {{ lastStorageErrorTime }}.</template></p>
        <p>Kapacita se vztahuje ke společnému globálnímu archivu, nikoli pouze k tomuto rozhovoru.</p>
      </div>
      <p>{{ inspection.totalRecords }} použitelných záznamů. {{ inspection.hasMore ? 'Výpis není úplný; server omezuje stránku na 50 záznamů.' : '' }}</p>
      <ol><li v-for="record in inspection.records" :key="record.memoryId"><p>{{ record.text }}</p><small>{{ record.sourceReference }} · revize {{ record.revision }}</small></li></ol>
      <label><input v-model="confirmed" type="checkbox" :disabled="debug.pending.value || debug.status.value.session || debug.status.value.running" data-memory-confirm> Potvrzuji odstranění paměti tohoto rozhovoru.</label>
      <p>Nejprve ukončete rozhovor. Mazání vyžaduje správce a nové ověření účtu; cizí rozhovory ani archiv památek tato akce nemaže.</p>
      <AwButton variant="danger" :disabled="!canDelete" @click="remove">Odstranit paměť tohoto rozhovoru</AwButton>
    </template>
  </section>
</template>
<style scoped>.memory-panel{display:grid;gap:1rem}.aw-field{display:grid;gap:.4rem}.memory-panel li{overflow-wrap:anywhere}.memory-panel li p{white-space:pre-wrap}</style>

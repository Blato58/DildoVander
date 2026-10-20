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
      <p>{{ inspection.totalRecords }} použitelných záznamů. {{ inspection.hasMore ? 'Výpis není úplný; server omezuje stránku na 50 záznamů.' : '' }}</p>
      <ol><li v-for="record in inspection.records" :key="record.memoryId"><p>{{ record.text }}</p><small>{{ record.sourceReference }} · revize {{ record.revision }}</small></li></ol>
      <label><input v-model="confirmed" type="checkbox" :disabled="debug.pending.value || debug.status.value.session || debug.status.value.running" data-memory-confirm> Potvrzuji odstranění paměti tohoto rozhovoru.</label>
      <p>Nejprve ukončete rozhovor. Mazání vyžaduje správce a nové ověření účtu; cizí rozhovory ani archiv památek tato akce nemaže.</p>
      <AwButton variant="danger" :disabled="!canDelete" @click="remove">Odstranit paměť tohoto rozhovoru</AwButton>
    </template>
  </section>
</template>
<style scoped>.memory-panel{display:grid;gap:1rem}.aw-field{display:grid;gap:.4rem}.memory-panel li{overflow-wrap:anywhere}.memory-panel li p{white-space:pre-wrap}</style>

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountTransport, refreshIdentity, type AccountDetails } from './session';

interface Evening { eveningId: string; name: string; createdAt: string; exchangeCount: number; droppedCount: number }
interface EveningList { activeEveningId: string; evenings: Evening[] }
interface Mutation { path: string; method: 'POST' | 'PUT'; body: string; key: string; deviceId: string }
const props = defineProps<{ details: AccountDetails | null }>();
const deviceId = ref(''); const name = ref('');
const data = ref<EveningList | null>(null); const error = ref(''); const busy = ref(false);
const pending = ref<Mutation | null>(null);
const devices = computed(() => props.details?.devices ?? []);
const canOperate = computed(() => devices.value.some(device => device.deviceId === deviceId.value && device.canOperate));
let generation = 0;
let controller: AbortController | null = null;

function clear() { ++generation; controller?.abort(); data.value = null; pending.value = null; error.value = ''; busy.value = false; }
async function readResponse(response: Response): Promise<EveningList> {
  if (!response.ok) {
    if (response.status === 401) await refreshIdentity();
    const problem = await response.json().catch(() => ({})) as { code?: string };
    throw new Error(response.status === 409 ? 'Aktivní večer se změnil nebo právě probíhá rozhovor. Obnovte seznam.'
      : problem.code === 'access_token_required' || response.status === 403 ? 'K tomuto kufru nebo změně večera už nemáte oprávnění.'
      : 'Večery se nepodařilo načíst nebo změnit.');
  }
  return response.json() as Promise<EveningList>;
}
async function load() {
  clear();
  if (!account.value || !deviceId.value) return;
  const epoch = generation; controller = new AbortController(); busy.value = true;
  try {
    const response = await accountTransport('/v1/evenings', { headers: { 'X-DV-Device-Id': deviceId.value }, signal: controller.signal });
    const result = await readResponse(response);
    if (epoch === generation) data.value = result;
  } catch (e) { if (epoch === generation) error.value = (e as Error).message; }
  finally { if (epoch === generation) busy.value = false; }
}
async function send(mutation: Mutation) {
  const epoch = generation; busy.value = true; error.value = '';
  try {
    const response = await accountTransport(mutation.path, { method: mutation.method,
      headers: { 'Content-Type': 'application/json', 'X-DV-Device-Id': mutation.deviceId, 'Idempotency-Key': mutation.key }, body: mutation.body });
    const result = await readResponse(response);
    if (epoch === generation) { data.value = result; pending.value = null; name.value = ''; }
  } catch (e) { if (epoch === generation) { data.value = null; error.value = (e as Error).message; } }
  finally { if (epoch === generation) busy.value = false; }
}
function change(target?: string) {
  if (!data.value || !canOperate.value || busy.value || pending.value || (!target && !name.value.trim())) return;
  if (!window.confirm(target ? 'Aktivovat tento večer pro vybraný kufr? Nové rozhovory budou používat jeho paměť.'
    : 'Vytvořit a aktivovat nový večer pro vybraný kufr? Dosavadní večer zůstane uložený.')) return;
  pending.value = { path: target ? `/v1/evenings/${target}/active` : '/v1/evenings', method: target ? 'PUT' : 'POST',
    body: JSON.stringify({ ...(target ? {} : { name: name.value.trim() }), expectedEveningId: data.value.activeEveningId }),
    key: crypto.randomUUID(), deviceId: deviceId.value };
  void send(pending.value);
}
watch(() => account.value?.id, () => { clear(); deviceId.value = ''; }, { flush: 'sync' });
watch(deviceId, () => { void load(); });
watch(devices, list => { if (!list.some(device => device.deviceId === deviceId.value)) deviceId.value = list[0]?.deviceId ?? ''; }, { immediate: true });
onBeforeUnmount(clear);
</script>
<template>
  <section class="aw-card" aria-labelledby="account-evenings-title">
    <h2 id="account-evenings-title">Večery kufru</h2>
    <p>Večery oddělují paměť rozhovorů vybraného kufru. Vytvoření ani aktivace nespouští AI a nemaže starší večery.</p>
    <p v-if="!devices.length">Nemáte přístup k žádnému nakonfigurovanému kufru. Vlastník může přidělit oprávnění ke konkrétnímu zařízení.</p>
    <template v-else>
      <label for="evening-device">Kufr</label>
      <select id="evening-device" v-model="deviceId" class="aw-select" :disabled="busy"><option v-for="device in devices" :key="device.deviceId" :value="device.deviceId">{{ props.details?.associations.find(item => item.deviceId === device.deviceId)?.alias || device.deviceId }}</option></select>
      <AwButton :disabled="busy" @click="load">Obnovit večery</AwButton>
      <p v-if="busy" role="status">Načítám stav večerů…</p>
      <AwNotice v-if="error" tone="warning">{{ error }}</AwNotice>
      <AwButton v-if="pending && error" :disabled="busy" @click="send(pending)">Zopakovat stejný požadavek</AwButton>
      <p v-if="!canOperate">Máte oprávnění ke čtení. Změna večera vyžaduje oprávnění k obsluze tohoto kufru.</p>
      <template v-if="data">
        <ul class="evening-list"><li v-for="evening in data.evenings" :key="evening.eveningId"><strong>{{ evening.name }}</strong><span v-if="evening.eveningId === data.activeEveningId"> · aktivní večer</span><p>{{ new Date(evening.createdAt).toLocaleString('cs') }} · rozhovorů: {{ evening.exchangeCount }} · mimo uchovanou paměť: {{ evening.droppedCount }}</p><AwButton v-if="canOperate && evening.eveningId !== data.activeEveningId" :disabled="busy || !!pending" @click="change(evening.eveningId)">Aktivovat večer</AwButton></li></ul>
        <form v-if="canOperate" class="evening-form" @submit.prevent="change()"><label for="evening-name">Název nového večera</label><input id="evening-name" v-model="name" class="aw-input" maxlength="80" required :disabled="busy || !!pending"><AwButton type="submit" :disabled="busy || !!pending || !name.trim()">Vytvořit a aktivovat večer</AwButton></form>
      </template>
    </template>
  </section>
</template>
<style scoped>.aw-select { display: block; margin-block: .5rem 1rem; max-width: 100%; }.evening-list { list-style: none; padding: 0; }.evening-list li { padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }.evening-form { display: grid; gap: .75rem; }.evening-form button { justify-self: start; }</style>

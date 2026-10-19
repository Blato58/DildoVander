<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { account, accountTransport, accountRequest, hasRole, type AccountDetails } from '../identity/session';
import { createArchiveClient } from './nativeGuest';
import { GuestError } from './client';
import { projectChronicle, type Chronicle } from './showContracts';
import './archive.css';
const client = createArchiveClient();
const snapshot = ref<Chronicle>(); const busy = ref(false); const message = ref('Načítám večerní knihu…');
const device = ref(''); const evening = ref(''); const privateView = ref(false); const archivedEditions = ref(0);
interface DeviceChoice { id: string; label: string }
interface EveningChoice { id: string; label: string }
const devices = ref<DeviceChoice[]>([]); const evenings = ref<EveningChoice[]>([]);
const devicesLoading = ref(false); const eveningsLoading = ref(false); const contextError = ref('');
const isAdmin = computed(() => hasRole('Administrator'));
const mayInspect = computed(() => isAdmin.value && devices.value.some(item => item.id === device.value) && evenings.value.some(item => item.id === evening.value));
const popular = computed(() => snapshot.value?.wands.filter(wand => snapshot.value?.mostPopularSlots.includes(wand.slot)).map(wand => wand.officialName) ?? []);
let generation = 0; let controller = new AbortController();
let devicesGeneration = 0; let eveningsGeneration = 0;
let devicesController = new AbortController(); let eveningsController = new AbortController();
let requestKind: 'public' | 'admin' | null = null;
const validContextId = (value: string) => value.trim().length > 0 && value.length <= 240 && !/[\u0000-\u001f\u007f]/.test(value);
function clear() { generation++; client.invalidate(); controller.abort(); controller = new AbortController(); snapshot.value = undefined; busy.value = false; privateView.value = false; archivedEditions.value = 0; requestKind = null; }
function clearPrivateView() { if (privateView.value || requestKind === 'admin') { clear(); message.value = 'Výběr se změnil. Načti přehled zvoleného kufru a večera.'; } }
function clearContext() {
  devicesGeneration++; eveningsGeneration++; devicesController.abort(); eveningsController.abort();
  devicesController = new AbortController(); eveningsController = new AbortController();
  devices.value = []; evenings.value = []; device.value = ''; evening.value = ''; devicesLoading.value = false; eveningsLoading.value = false;
}
async function loadDevices() {
  if (!isAdmin.value || document.hidden) return;
  const request = ++devicesGeneration; const accountId = account.value?.id;
  devicesController.abort(); devicesController = new AbortController(); devicesLoading.value = true; contextError.value = '';
  clearPrivateView();
  try {
    const details = await accountRequest<AccountDetails>('/app/v1/account', 'GET', undefined, devicesController.signal);
    if (request !== devicesGeneration || accountId !== account.value?.id || !isAdmin.value) return;
    const associations = Array.isArray(details.associations) ? details.associations : [];
    devices.value = [...new Map((details.devices ?? []).filter(item => validContextId(item.deviceId)).map((item, index) => [item.deviceId, {
      id: item.deviceId, label: associations.find(association => association.deviceId === item.deviceId)?.alias || `Kufr ${index + 1}`,
    }])).values()];
    const previous = device.value;
    device.value = devices.value.some(item => item.id === previous) ? previous : devices.value.length === 1 ? devices.value[0].id : '';
    if (device.value && device.value === previous) void loadEvenings();
  } catch (error) {
    if (request !== devicesGeneration || (error as Error).name === 'AbortError') return;
    clearContext(); clearPrivateView(); contextError.value = 'Seznam povolených kufrů není dostupný. Obnov oprávnění účtu.';
  } finally { if (request === devicesGeneration) devicesLoading.value = false; }
}
async function loadEvenings() {
  const selected = device.value; const request = ++eveningsGeneration; const accountId = account.value?.id;
  eveningsController.abort(); eveningsController = new AbortController(); evenings.value = []; evening.value = ''; clearPrivateView();
  if (!isAdmin.value || !devices.value.some(item => item.id === selected) || document.hidden) { eveningsLoading.value = false; return; }
  eveningsLoading.value = true; contextError.value = '';
  try {
    const response = await accountTransport('/v1/evenings', { method: 'GET', headers: { 'X-DV-Device-Id': selected }, signal: eveningsController.signal, redirect: 'error' });
    if (request !== eveningsGeneration || accountId !== account.value?.id || device.value !== selected || !isAdmin.value) return;
    if (!response.ok) throw new GuestError(response.status);
    const value = await response.json();
    if (request !== eveningsGeneration || accountId !== account.value?.id || device.value !== selected || !isAdmin.value) return;
    if (!Array.isArray(value.evenings)) throw new GuestError(503);
    evenings.value = value.evenings.filter((item: { eveningId: string }) => typeof item.eveningId === 'string' && validContextId(item.eveningId))
      .map((item: { eveningId: string; name?: string }, index: number) => ({ id: item.eveningId, label: `${item.name || `Archivovaný večer ${index + 1}`}${item.eveningId === value.activeEveningId ? ' · aktivní večer' : ''}` }));
    evening.value = evenings.value.some(item => item.id === value.activeEveningId) ? value.activeEveningId : '';
  } catch (error) {
    if (request !== eveningsGeneration || (error as Error).name === 'AbortError') return;
    if (error instanceof GuestError && [401, 403].includes(error.status)) clearContext();
    clearPrivateView(); contextError.value = 'Večery vybraného kufru nejsou dostupné. Obnov seznam a oprávnění.';
  } finally { if (request === eveningsGeneration) eveningsLoading.value = false; }
}
async function load(admin = false) {
  if (busy.value || document.hidden || admin && (!mayInspect.value || devicesLoading.value || eveningsLoading.value)) return;
  clear(); const request = generation; const accountId = account.value?.id; busy.value = true; message.value = 'Načítám večerní knihu…';
  requestKind = admin ? 'admin' : 'public';
  try {
    if (admin) {
      const requestedEvening = evening.value;
      const query = new URLSearchParams({ deviceId: device.value.trim(), eveningId: requestedEvening });
      const response = await accountTransport(`/v1/admin/show/chronicle?${query}`, { method: 'GET', headers: { 'X-DV-Device-Id': device.value.trim() }, signal: controller.signal, redirect: 'error' });
      if (request !== generation || accountId !== account.value?.id || !isAdmin.value) return;
      if (!response.ok) throw new GuestError(response.status);
      const value = await response.json();
      if (request !== generation || accountId !== account.value?.id || !isAdmin.value) return;
      const projected = projectChronicle(value.snapshot);
      if (projected.eveningId !== requestedEvening) throw new GuestError(503);
      snapshot.value = projected; archivedEditions.value = Number.isInteger(value.archivedEditions) ? value.archivedEditions : 0; privateView.value = true;
    } else {
      const value = await client.json('/guest/v1/chronicle', {}, true); if (request !== generation) return;
      snapshot.value = projectChronicle(value);
    }
    message.value = '';
  } catch (error) {
    if (request !== generation || (error as Error).name === 'AbortError') return;
    snapshot.value = undefined;
    if (admin && error instanceof GuestError && [401, 403].includes(error.status)) clearContext();
    message.value = admin ? 'Soukromý přehled není dostupný. Ověř oprávnění správce, zařízení a večer.'
      : error instanceof GuestError && error.status === 404 ? 'Kronika zatím není dostupná. Obsluha musí nejprve připravit večer; později ji můžeš obnovit.' : 'Večerní kniha teď není dostupná. Zkus ji znovu obnovit.';
  } finally { if (request === generation) { busy.value = false; requestKind = null; } }
}
function suspend() { clear(); clearContext(); }
function visibility() { if (document.hidden) suspend(); else { void load(); void loadDevices(); } }
function pageShow() { if (!document.hidden) { void load(); void loadDevices(); } }
watch(device, () => { void loadEvenings(); }, { flush: 'sync' });
watch(evening, clearPrivateView, { flush: 'sync' });
watch(() => `${account.value?.id}:${account.value?.roles.join(',')}`, () => { suspend(); message.value = 'Přihlášení se změnilo. Soukromý přehled byl uzavřen.'; void loadDevices(); }, { flush: 'sync' });
onMounted(() => { document.addEventListener('visibilitychange', visibility); window.addEventListener('pagehide', suspend); window.addEventListener('pageshow', pageShow); void load(); void loadDevices(); });
onBeforeUnmount(() => { suspend(); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', suspend); window.removeEventListener('pageshow', pageShow); });
</script>
<template>
  <section class="archive-view" aria-labelledby="chronicle-title">
    <div><h1 id="chronicle-title">Večerní kronika</h1><p>Potvrzené události večera. Přehled neobsahuje rozhovory, fotografie ani soukromá proroctví.</p></div>
    <div class="archive-actions"><RouterLink to="/archive">Hůlky večera</RouterLink><button class="aw-button aw-button--secondary" :disabled="busy" @click="load()">Obnovit kroniku</button></div>
    <p v-if="message" class="aw-notice" role="status" aria-live="polite">{{ message }}</p>
    <article v-if="snapshot" class="aw-card" aria-labelledby="chronicle-edition">
      <h2 id="chronicle-edition">{{ privateView ? 'Soukromý přehled obsluhy · ' : '' }}{{ snapshot.provisional ? 'Živý provizorní přehled' : `Publikovaná edice ${snapshot.revision}` }}</h2>
      <p>{{ snapshot.createdAt }}</p><dl class="archive-counts"><div><dt>Dokončené návštěvy</dt><dd>{{ snapshot.completedVisits }}</dd></div><div><dt>Potvrzené prezentace</dt><dd>{{ snapshot.confirmedPresentations }}</dd></div><div><dt>Dokončené souboje</dt><dd>{{ snapshot.completedDuels }}</dd></div></dl>
      <p class="archive-story">{{ snapshot.narrative }}</p><p class="archive-caption">Autorská fikce vychází z doložených počtů. Nejde o citace hostů.</p>
      <table><caption>Potvrzené prezentace hůlek</caption><thead><tr><th scope="col">Hůlka</th><th scope="col">Počet</th></tr></thead><tbody><tr v-for="wand in snapshot.wands" :key="wand.slot"><th scope="row">{{ wand.officialName }}</th><td>{{ wand.confirmedPresentations }}</td></tr></tbody></table>
      <p>{{ !popular.length ? 'První potvrzená prezentace teprve přijde.' : `${popular.length > 1 ? 'Na prvním místě je remíza' : 'Nejvíc potvrzených prezentací'}: ${popular.join(', ')}.` }}</p>
      <p>Neuzavřené návštěvy: {{ snapshot.unresolvedVisits }}. Přerušené návštěvy: {{ snapshot.interruptedVisits }}. Pozdní potvrzení patří původnímu večeru a vytvoří další edici.</p>
      <p v-if="privateView">Archivované edice: {{ archivedEditions }}. Vyčištění záloh není potvrzené. Vyčištění u poskytovatele nebylo vyžádáno.</p>
    </article>
    <form v-if="isAdmin" class="aw-card" @submit.prevent="load(true)">
      <h2>Soukromý přehled správce</h2><p>Vyber povolený kufr a jeho večer. Čtení nevolá AI. Použije se právě přihlášený účet správce.</p>
      <p v-if="contextError" class="aw-notice" role="status">{{ contextError }}</p>
      <p v-if="!devicesLoading && !devices.length">Účet nemá dostupný povolený kufr. Obnov oprávnění nebo požádej správce.</p>
      <label for="chronicle-device">Kufr</label><select id="chronicle-device" v-model="device" class="aw-input" :disabled="devicesLoading || busy" required><option value="" disabled>Vyber kufr</option><option v-for="item in devices" :key="item.id" :value="item.id">{{ item.label }}</option></select>
      <label for="chronicle-evening">Večer</label><select id="chronicle-evening" v-model="evening" class="aw-input" :disabled="devicesLoading || eveningsLoading || busy || !device" required><option value="" disabled>{{ eveningsLoading ? 'Načítám večery…' : 'Vyber večer' }}</option><option v-for="item in evenings" :key="item.id" :value="item.id">{{ item.label }}</option></select>
      <p v-if="device && !eveningsLoading && !evenings.length">Tento kufr zatím nemá dostupný večer.</p>
      <div class="archive-actions"><button class="aw-button" :disabled="busy || devicesLoading || eveningsLoading || !mayInspect" type="submit">Načíst soukromý přehled</button><button class="aw-button aw-button--secondary" :disabled="devicesLoading || eveningsLoading || busy" type="button" @click="loadDevices">Obnovit výběr</button></div>
      <details v-if="device"><summary>Technické údaje výběru</summary><dl><dt>ID zařízení</dt><dd>{{ device }}</dd><dt>ID večera</dt><dd>{{ evening || 'Večer není zvolený' }}</dd></dl></details>
    </form>
    <p>Archiv se uchovává do ručního smazání. Změna večera ani expirace přístupu obsah nemažou.</p>
  </section>
</template>

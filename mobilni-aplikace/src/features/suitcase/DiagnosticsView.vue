<script setup lang="ts">
import AwButton from '../../components/AwButton.vue';
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue';
import { deviceSession as device } from './deviceSession';
import { debugBridge, nativeDebug as debug } from './nativeDebug';
import PhoneTestPanel from './PhoneTestPanel.vue';
import GuestMemoryPanel from './GuestMemoryPanel.vue';
import ConversationCapabilities from '../debug/ConversationCapabilities.vue';
import ServoBenchPanel from './ServoBenchPanel.vue';
import { deviceAssociation as cloud } from './deviceAssociation';
const snapshot = device.snapshot;
const status = debug.status;
const legacySettings = computed(() => status.value.accessMode === 'legacy-service' || !status.value.accessMode);
const serverAdminAccess = computed(() => status.value.accessMode === 'legacy-service' || !status.value.accessMode || cloud.administrator.value);
const personality = ref('');
const characterEnabled = ref(false);
const tone = ref('Adult');
const finale = ref('Blocked');
const eveningName = ref('');
const eveningId = ref('');
const level = ref('warning');
const trace = ref('');
const journalCategory = ref('all');
const journalPaused = ref(false);
const liveLogs = ref(false);
const localEntries = ref(device.journalEntries.value);
let poll: ReturnType<typeof setInterval> | undefined;
let draftRevision: string | undefined;
let lastLogRefresh = 0;
watch(device.journalEntries, entries => { if (!journalPaused.value) localEntries.value = entries; });
watch(status, value => {
  if (value.server && value.server.revision !== draftRevision) {
    draftRevision = value.server.revision;
    personality.value = value.server.personalityId ?? ''; characterEnabled.value = value.server.characterEnabled ?? false;
    tone.value = value.server.characterTone ?? 'Adult'; finale.value = value.server.characterFinaleProfile ?? 'Blocked';
  }
  if (value.evenings) eveningId.value = value.evenings.activeEveningId;
});
function activate(): void {
  if (poll || !device.native) return;
  void device.refreshSaved();
  void cloud.refresh();
  void debug.refresh();
  poll = setInterval(() => {
    if (document.hidden || debug.pending.value) return;
    if (liveLogs.value && status.value.configured && Date.now() - lastLogRefresh >= 2000) {
      lastLogRefresh = Date.now(); void debug.readLogs(level.value);
    } else void debug.refresh();
  }, 1000);
}
function deactivate(): void { if (poll) clearInterval(poll); poll = undefined; }
onMounted(activate); onActivated(activate); onDeactivated(deactivate); onBeforeUnmount(deactivate);
async function configure(): Promise<void> { debug.clearLogs(); await debug.configure(); }
function inspectBackend(): void { void debug.run(() => debugBridge.inspectBackend()); }
function inspectServer(): void { void debug.run(() => debugBridge.inspectServer()); }
function inspectEvenings(): void { void debug.run(() => debugBridge.inspectEvenings()); }
async function applyModes(character = false, reset = false): Promise<void> {
  const server = status.value.server;
  if (!server || !debug.idle.value || status.value.accessMode === 'account') return;
  await debug.run(() => debugBridge.changeServerModes({ expectedRevision: server.revision,
    personalityId: character || reset ? server.personalityId : personality.value || undefined,
    ...(character ? { characterEnabled: characterEnabled.value, characterTone: tone.value, characterFinaleProfile: finale.value } : {}),
    resetCharacterVariety: reset }));
}
async function createEvening(): Promise<void> {
  if (!status.value.evenings || !debug.idle.value || !eveningName.value.trim()) return;
  await debug.run(() => debugBridge.createEvening({ name: eveningName.value.trim(), expectedEveningId: status.value.evenings!.activeEveningId }));
  eveningName.value = '';
}
async function activateEvening(): Promise<void> {
  if (!status.value.evenings || !debug.idle.value || !eveningId.value) return;
  await debug.run(() => debugBridge.activateEvening({ eveningId: eveningId.value, expectedEveningId: status.value.evenings!.activeEveningId }));
}
function exportJournal(): void {
  const json = device.exportJournal();
  if (device.native) void debug.run(() => debugBridge.shareLog({ json }));
  else {
    const url = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'kufr-diagnostic.json'; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}
watch(level, () => debug.clearLogs());
</script>

<template>
  <div class="diagnostics-view">
    <header class="feature-heading"><p class="aw-eyebrow">Servis</p><h1>Diagnostika</h1><p>Technický stav a oddělený testovací přístup. Server i nativní adaptéry ověřují oprávnění každého požadavku.</p><RouterLink to="/suitcase">Zpět na kufr</RouterLink></header>
    <section class="aw-card"><h2>Stav místního kufru</h2><p v-if="!snapshot">Kufr není připojený. Žádný stav hardwaru není potvrzený.</p><template v-else><p>{{ snapshot.source === 'native-device' ? 'Skutečný kufr' : 'SIMULOVANÝ STAV' }} · {{ snapshot.status }}</p><dl class="diagnostic-metrics"><div><dt>Fáze</dt><dd>{{ snapshot.phase }}</dd></div><div><dt>Revize</dt><dd>{{ snapshot.revision ?? 'Neznámá' }}</dd></div><template v-if="snapshot.diagnostics"><div><dt>Boot ID</dt><dd>{{ snapshot.diagnostics.bootId }}</dd></div><div><dt>Stav zařízení</dt><dd>{{ snapshot.diagnostics.devicePhase }}</dd></div><div><dt>Událost</dt><dd>{{ snapshot.diagnostics.eventSequence }}</dd></div><div><dt>Doba běhu</dt><dd>{{ Math.round(snapshot.diagnostics.deviceUptimeMs / 1000) }} s</dd></div><div><dt>Odezva</dt><dd>{{ snapshot.diagnostics.roundTripMs ?? 'Neznámá' }} ms</dd></div><div><dt>Chyby</dt><dd>{{ snapshot.diagnostics.faultCount }}</dd></div></template></dl></template></section>
    <RouterLink v-if="device.savedDevice.value.paired && device.savedDevice.value.deviceId" :to="{ path: '/workshop', query: { mode: 'live', source: device.savedDevice.value.deviceId } }" class="aw-button aw-button--secondary">Pozorovat tento kufr v Dílně</RouterLink>
    <section class="aw-card" aria-labelledby="server-heading">
      <h2 id="server-heading">Diagnostika přes váš účet</h2>
      <p>Použijte přihlášený účet a přesné oprávnění k uloženému kufru. Nativní HTTPS používá stejnou soukromou relaci účtu; žádný serverový klíč se nezadává. Místní PoP, Keychain a připnuté LAN spojení zůstávají samostatné.</p>
      <p v-if="!cloud.account.value"><RouterLink to="/signin">Přihlásit se k účtu</RouterLink></p>
      <p v-else-if="!device.savedDevice.value.paired"><RouterLink to="/connections">Nejprve ověřit a uložit kufr</RouterLink></p>
      <p v-else>{{ device.savedDevice.value.deviceId }} · {{ cloud.canOperate.value ? 'oprávnění ovládat' : cloud.canRead.value ? 'pouze čtení' : 'oprávnění neověřené nebo nepřidělené' }}</p>
      <p role="status">{{ debug.message.value }}</p>
      <p>{{ status.accessMode === 'account' ? 'Přihlášený účet' : status.accessMode === 'legacy-service' ? 'Vývojový servisní profil' : 'Diagnostika není připojená' }} · {{ status.deviceId }} · {{ status.phase }} · engine {{ status.effectiveEngine ?? 'neověřený' }} · model {{ status.effectiveModel ?? 'neověřený' }}</p>
      <p v-if="status.previousAccountCleanupPending" class="aw-notice">Předchozí účet nemá potvrzený úklid cloudového testu. Tento účet jej nepřebírá; jeho data a stav musí ověřit oprávněný správce.</p>
      <div class="diagnostic-actions">
        <AwButton variant="primary" :disabled="!device.native || !cloud.account.value || !cloud.canRead.value || !device.savedDevice.value.deviceId || debug.pending.value || status.running || status.session" @click="debug.useAccount(device.savedDevice.value.deviceId!)">Použít přihlášený účet pro tento kufr</AwButton>
        <AwButton :disabled="!device.native || cloud.pending.value" @click="cloud.refresh">Obnovit oprávnění účtu</AwButton>
        <AwButton :disabled="!device.native || debug.pending.value" @click="debug.forget">Odpojit diagnostický přístup</AwButton>
        <AwButton :disabled="!device.native || debug.pending.value" @click="debug.refresh">Obnovit nativní stav</AwButton>
        <AwButton :disabled="!debug.idle.value" @click="inspectBackend">Ověřit backend</AwButton>
        <AwButton :disabled="!debug.idle.value || !serverAdminAccess" @click="inspectServer">Obnovit režimy serveru</AwButton>
      </div>
      <p v-if="status.accessMode === 'account'">Změny režimů a postavy vyžadují správu aplikace, potvrzení a nové ověření účtu. <RouterLink v-if="cloud.administrator.value" to="/settings/admin">Otevřít správu aplikace</RouterLink></p>
      <details><summary>Vývojová servisní obnova · starší profil</summary><p>Oddělená kompatibilita pro vývojáře a obnovu. Běžný provoz používá účet výše. Starší credential se zadává pouze v zabezpečeném nativním dialogu a lze jej odebrat tlačítkem Odpojit diagnostický přístup.</p><AwButton :disabled="!device.native || debug.pending.value || status.running || status.session" @click="configure">Nastavit vývojový servisní profil</AwButton></details>
      <ConversationCapabilities :capabilities="status.engines ?? status.server?.engines" />
      <fieldset v-if="legacySettings" :disabled="!debug.idle.value || !status.server"><legend>Režimy serveru pro další rozhovory</legend><div class="diagnostic-fields"><label class="aw-field">Personalita<select v-model="personality" class="aw-input"><option value="">Vyberte personalitu</option><option v-for="item in status.server?.personalities" :key="item.id" :value="item.id">{{ item.name }}</option></select></label></div><p>{{ status.server?.personalities?.find(item => item.id === personality)?.description }}</p><AwButton variant="primary" @click="applyModes()">Použít nastavení serveru</AwButton><p class="aw-help">Osobnost a pravidla postavy jsou trvalé; engine vybírá obsluha pro nový rozhovor. Změna používá aktuální revizi, bez automatického opakování.</p></fieldset>
      <fieldset v-if="legacySettings" :disabled="!debug.idle.value || !status.server"><legend>DV01 · dospělá roast postava</legend><label><input v-model="characterEnabled" type="checkbox"> Zapnout pro nové rozhovory</label><div class="diagnostic-fields"><label class="aw-field">Hostitelský tón<select v-model="tone" class="aw-input"><option>Reserved</option><option>Dry</option><option>Adult</option><option>Chaos</option></select></label><label class="aw-field">Finále<select v-model="finale" class="aw-input"><option value="Blocked">Blokované · bez fyzického reveal</option><option value="Limited">Omezené · bez vysloveného názvu hůlky</option></select></label></div><p>Pojmenované finále a roast fotografie čekají na samostatné ověření. DV01 vyžaduje ollivander-trunk a AutonomousActor. Přepis DV01 se nepřenáší do dalšího hosta.</p><div class="diagnostic-actions"><AwButton @click="applyModes(true)">Použít DV01 pro nové rozhovory</AwButton><AwButton @click="applyModes(false, true)">Resetovat anonymní pestrost</AwButton></div></fieldset>
    </section>
    <PhoneTestPanel />
    <GuestMemoryPanel />
    <ServoBenchPanel />
    <section class="aw-card" aria-labelledby="evening-heading"><h2 id="evening-heading">Paměť večera</h2><p>Vytvoření nebo aktivace odděluje následující hosty. Existující obsah se těmito akcemi nemaže.</p><AwButton :disabled="!debug.idle.value" @click="inspectEvenings">Obnovit seznam večerů</AwButton><p v-if="!status.evenings">Nejprve načtěte seznam pro svou servisní identitu.</p><template v-else><p>Aktivní: {{ status.evenings.evenings.find(item => item.eveningId === status.evenings?.activeEveningId)?.name || status.evenings.activeEveningId }}</p><form @submit.prevent="createEvening"><label class="aw-field">Název nového večera<input v-model="eveningName" class="aw-input" maxlength="80" :disabled="!debug.idle.value" required></label><AwButton type="submit" :disabled="!debug.idle.value || !eveningName.trim() || status.evenings.evenings.length >= 100">Vytvořit večer</AwButton></form><label class="aw-field">Existující večer<select v-model="eveningId" class="aw-input" :disabled="!debug.idle.value"><option v-for="item in status.evenings.evenings" :key="item.eveningId" :value="item.eveningId">{{ item.name }} · {{ item.exchangeCount }} výměn</option></select></label><AwButton :disabled="!debug.idle.value || eveningId === status.evenings.activeEveningId" @click="activateEvening">Aktivovat vybraný večer</AwButton></template></section>
    <section class="aw-card" aria-labelledby="logs-heading"><h2 id="logs-heading">Log serveru</h2><p>Pouze typované technické události. Čtení nevolá AI a neposkytuje obsah paměti hostů.</p><label><input v-model="liveLogs" type="checkbox" :disabled="!device.native || !status.configured"> Průběžně obnovovat log na této obrazovce</label><div class="diagnostic-fields"><label class="aw-field">Úroveň<select v-model="level" class="aw-input"><option value="warning">Varování a chyby</option><option value="all">Všechny</option><option value="error">Chyby</option></select></label><label class="aw-field">Trace / operation ID<input v-model="trace" class="aw-input" autocomplete="off" placeholder="Filtrovat načtené události"></label></div><div class="diagnostic-actions"><AwButton :disabled="!device.native || !status.configured || debug.pending.value" @click="debug.readLogs(level)">{{ debug.hasMore.value ? 'Načíst další stránku' : 'Obnovit log serveru' }}</AwButton><AwButton :disabled="!device.native || debug.pending.value || !debug.logEntries.value.length" @click="debug.run(debug.exportLogs)">Exportovat log serveru</AwButton></div><p v-if="debug.logGap.value || debug.droppedWrites.value" class="aw-notice">Log obsahuje mezeru nebo zahozené zápisy ({{ debug.droppedWrites.value }}). Chybějící událost nedokazuje úspěch.</p><p v-if="!debug.logEntries.value.length">Žádné načtené události. Obnovte log s odpovídajícím přístupem.</p><ol class="technical-log"><li v-for="entry in debug.logEntries.value.filter(item => !trace || item.traceId?.includes(trace) || item.operationId?.includes(trace))" :key="entry.id"><time>{{ entry.timestamp }} UTC</time> · {{ entry.level }} · <strong>{{ entry.eventCode }}</strong><p>{{ entry.method }} {{ entry.route }} {{ entry.status }} · {{ entry.durationMs }} ms</p><p v-if="entry.traceId">traceId: {{ entry.traceId }}</p><p v-if="entry.operationId">operationId: {{ entry.operationId }}</p></li></ol></section>
    <section class="aw-card"><h2>Místní technický deník</h2><p>Bez audio obsahu, tokenů a textu hostů. Živé snímky zůstávají v paměti aplikace.</p><label class="aw-field">Kategorie<select v-model="journalCategory" class="aw-input"><option value="all">Všechny</option><option value="device">Kufr</option><option value="voice">Hlas</option><option value="app">Aplikace</option></select></label><div class="diagnostic-actions"><AwButton @click="journalPaused = !journalPaused; if (!journalPaused) localEntries = device.journalEntries.value">{{ journalPaused ? 'Obnovit zobrazení' : 'Pozastavit zobrazení' }}</AwButton><AwButton @click="device.clearJournal(); localEntries = []">Vymazat místní deník</AwButton><AwButton @click="exportJournal">Exportovat místní deník</AwButton></div><ol class="technical-log"><li v-for="entry in localEntries.filter(item => journalCategory === 'all' || item.category === journalCategory)" :key="entry.sequence">{{ entry.time }} · {{ entry.category }} · {{ entry.code }}<p>{{ Object.entries(entry.values).map(([key, value]) => `${key}: ${value}`).join(' · ') }}</p></li></ol></section>
  </div>
</template>

<style scoped>
.diagnostics-view{display:grid;gap:1.5rem}.diagnostic-actions{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.diagnostic-fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,13rem),1fr));gap:1rem}.aw-field{display:grid;gap:.5rem;margin-block:.75rem}.diagnostic-metrics{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,12rem),1fr));gap:1rem}.diagnostic-metrics dd{margin:0;overflow-wrap:anywhere}fieldset{border:1px solid var(--aw-border);border-radius:.75rem;margin-block:1rem;padding:1rem;min-width:0}.technical-log{padding-inline-start:1.5rem;max-height:28rem;overflow:auto;font-family:var(--aw-font-mono);overflow-wrap:anywhere}.technical-log li{padding-block:.75rem}.technical-log p{margin-block:.25rem}
</style>

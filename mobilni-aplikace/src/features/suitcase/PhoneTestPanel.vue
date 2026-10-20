<script setup lang="ts">
import AwButton from '../../components/AwButton.vue';
import { computed, onBeforeUnmount, onDeactivated, ref, watch } from 'vue';
import { debugBridge, nativeDebug as debug } from './nativeDebug';
import { deviceSession as device } from './deviceSession';
import { phoneError } from './phoneErrors';
import { deviceAssociation as cloud } from './deviceAssociation';
import { engineLabels, enginePreferenceKey, migrateConversationEngine, parseEngineCapabilities, validGuestMemoryId,
  type ConversationEngine, type GuestMemoryBinding } from '../debug/conversationCapabilities';
const status = debug.status;
let stored: string | null = null;
try { stored = window.localStorage.getItem(enginePreferenceKey); } catch { /* Optional preference only. */ }
const migration = migrateConversationEngine(stored);
const engine = ref<ConversationEngine | ''>(migration.engine ?? '');
const migrationMessage = ref(migration.message ?? '');
const voice = ref(''); const speed = ref(1);
const ready = ref([1, 2, 3, 4, 5, 6]);
const costume = ref(''); const consent = ref(false); const photoDraft = ref<string>();
const guestConfirmed = ref(false);
const canBindGuest = computed(() => status.value.accessMode === 'legacy-service' || cloud.administrator.value);
const guestId = ref(''); const identityVerified = ref(false); const longTermAllowed = ref(false); const cloudExportAllowed = ref(false);
const photoMessage = ref('Bez fotky. Rozhovor ji nepotřebuje.');
const photoUsed = ref(false);
let photoSequence = 0; let viewEpoch = 0; let photoTimer: ReturnType<typeof setTimeout> | undefined;
const inventory = computed(() => parseEngineCapabilities(status.value.engines ?? status.value.server?.engines));
const capability = computed(() => inventory.value?.engines.find(item => item.engine === engine.value));
const accountAccess = computed(() => status.value.accessMode !== 'account' ||
  (!!cloud.account.value && cloud.canOperate.value && status.value.deviceId === device.savedDevice.value.deviceId));
const guestValid = computed(() => !guestId.value.trim() || canBindGuest.value && guestConfirmed.value && validGuestMemoryId(guestId.value.trim()) && identityVerified.value && (!cloudExportAllowed.value || longTermAllowed.value));
const canAI = computed(() => accountAccess.value && debug.idle.value && capability.value?.ready && capability.value.inputModes.includes('audio')
  && capability.value.voices.includes(voice.value) && Number.isFinite(speed.value) && speed.value >= capability.value.speed.min && speed.value <= capability.value.speed.max && guestValid.value);
const photoCapability = computed(() => inventory.value?.engines.find(item => item.engine === (status.value.session ? status.value.effectiveEngine ?? engine.value : engine.value)));
const cameraAvailable = computed(() => !!photoCapability.value?.cameraSources.includes('phone') && accountAccess.value);
const canPhoto = computed(() => device.native && status.value.configured && !debug.pending.value && !status.value.cameraActive && cameraAvailable.value);
watch(() => JSON.stringify({ engine: engine.value, voices: capability.value?.voices, speed: capability.value?.speed }), () => {
  const value = capability.value;
  if (!value) { voice.value = ''; return; }
  if (!value.voices.includes(voice.value)) voice.value = value.voices[0] ?? '';
  speed.value = value.speed.default;
}, { immediate: true });
watch(engine, value => { migrationMessage.value = ''; if (value) { try { window.localStorage.setItem(enginePreferenceKey, value); } catch { /* Optional preference. */ } } });
watch(longTermAllowed, value => { if (!value) cloudExportAllowed.value = false; });
watch(identityVerified, value => { if (!value) { longTermAllowed.value = false; cloudExportAllowed.value = false; } });
watch(guestId, () => { identityVerified.value = false; longTermAllowed.value = false; cloudExportAllowed.value = false; guestConfirmed.value = false; }, { flush: 'sync' });
watch([identityVerified, longTermAllowed, cloudExportAllowed], () => { guestConfirmed.value = false; }, { flush: 'sync' });
watch(status, (value, previous) => {
  if (previous.configured && !value.configured || previous.deviceId !== value.deviceId
    || previous.conversationId && previous.conversationId !== value.conversationId
    || previous.sessionGeneration != null && previous.sessionGeneration !== value.sessionGeneration) hide();
});
watch(() => cloud.account.value?.id, hide);
watch(canBindGuest, value => { if (!value) clearGuest(); });
function clearGuest(): void { guestConfirmed.value = false; guestId.value = ''; identityVerified.value = false; longTermAllowed.value = false; cloudExportAllowed.value = false; }
function clearDraft(): void {
  ++photoSequence; if (photoTimer) clearTimeout(photoTimer);
  photoDraft.value = undefined; consent.value = false; costume.value = '';
  photoMessage.value = 'Bez fotky. Rozhovor ji nepotřebuje.';
}
async function discard(): Promise<void> {
  clearDraft(); if (!device.native) return;
  await debug.run(async () => { await debugBridge.discardCostumePhoto(); photoUsed.value = false; });
}
function hide(): void {
  ++viewEpoch;
  const hadDraft = photoDraft.value !== undefined; clearDraft(); clearGuest();
  if (hadDraft && device.native) void debugBridge.discardCostumePhoto().catch(() => undefined);
}
function visibility(): void { if (document.hidden) hide(); }
document.addEventListener('visibilitychange', visibility);
onBeforeUnmount(() => { hide(); document.removeEventListener('visibilitychange', visibility); }); onDeactivated(hide);
async function capture(): Promise<void> {
  if (!consent.value || !canPhoto.value) return;
  const sequence = ++photoSequence; consent.value = false;
  await debug.run(async () => {
    const photo = await debugBridge.captureCostumePhoto();
    if (sequence !== photoSequence) { await debugBridge.discardCostumePhoto(); return; }
    photoDraft.value = photo.draftId ?? undefined;
    photoMessage.value = photo.draftId ? 'Schválená fotka se odešle jen do následujícího rozhovoru.' : 'Fotografie není dostupná; pokračujte bez ní.';
    photoTimer = setTimeout(hide, 120000);
  });
}
async function attach(): Promise<void> {
  if (!photoDraft.value || !status.value.session || !canPhoto.value) return;
  const draftId = photoDraft.value; const epoch = viewEpoch;
  await debug.run(async () => {
    await debugBridge.attachCostumePhoto({ draftId });
    if (epoch !== viewEpoch) return;
    clearDraft(); photoUsed.value = true; photoMessage.value = 'Schválená fotka připojena k tomuto rozhovoru.';
  });
}
async function start(): Promise<void> {
  if (!canAI.value || !engine.value) return;
  const selectedEngine = engine.value; const selectedVoice = voice.value; const selectedSpeed = speed.value;
  const draft = photoDraft.value; const epoch = viewEpoch;
  const description = costume.value.trim() || undefined;
  const guestMemory: GuestMemoryBinding | undefined = guestId.value.trim() ? { guestId: guestId.value.trim(), identityVerified: identityVerified.value,
    longTermAllowed: longTermAllowed.value, cloudExportAllowed: cloudExportAllowed.value } : undefined;
  const readyMask = ready.value.reduce((mask, number) => mask | (1 << (number - 1)), 0);
  clearDraft(); clearGuest(); photoUsed.value = false;
  await debug.run(async () => {
    await debugBridge.startConversation({ expectedMode: 'openai', readyMask, costumeDescription: description,
      voice: selectedVoice, speed: selectedSpeed, engine: selectedEngine, ...(guestMemory ? { guestMemory, confirmGuestMemory: true as const } : {}) });
    if (draft && epoch === viewEpoch) {
      try { await debugBridge.attachCostumePhoto({ draftId: draft }); if (epoch === viewEpoch) photoUsed.value = true; }
      catch { if (epoch === viewEpoch) photoMessage.value = 'Fotku se nepodařilo připojit. Hlas pokračuje bez ní.'; }
    }
  });
}
async function cancel(): Promise<void> { hide(); photoUsed.value = false; await debug.cancel(); }
function speak(): void { void debug.run(() => debugBridge.speakSystem()); }
</script>

<template>
  <section class="aw-card" aria-labelledby="phone-heading">
    <h2 id="phone-heading">Test hlasu na telefonu</h2>
    <p>Telefon používá trvalé autentizované spojení s backendem. Dostupnost přihrádek je simulovaná; návrh nespouští pohony kufru. Rozhovor může čerpat OpenAI účet.</p>
    <p v-if="!device.native" class="aw-notice">Nativní mikrofon, hlas a fotoaparát vyžadují aplikaci na iPhonu. Web je neověřuje.</p>
    <fieldset :disabled="!debug.idle.value || !accountAccess"><legend>Nový testovací rozhovor</legend>
      <div class="phone-fields"><label class="aw-field">Konverzační engine<select v-model="engine" class="aw-input" data-engine><option value="" disabled>Vyberte podporovaný engine</option><option v-for="(label, key) in engineLabels" :key="key" :value="key">{{ label }}</option></select></label>
        <label class="aw-field">Hlas<select v-model="voice" class="aw-input" data-voice><option v-for="item in capability?.voices ?? []" :key="item" :value="item">{{ item }}</option></select></label>
        <label class="aw-field">Tempo<input v-model.number="speed" type="number" class="aw-input" :min="capability?.speed.min" :max="capability?.speed.max" step="0.05" :disabled="!capability?.speed.supported" data-speed></label></div>
      <p v-if="migrationMessage" role="status">{{ migrationMessage }}</p>
      <p v-if="!inventory" role="status">Obnovte stav serveru. Je vyžadován kontrakt enginů v1.</p>
      <p v-else-if="!capability?.ready" role="status">Zvolený engine není dostupný. Ověřte konfiguraci a přístup k modelu; jiný engine se sám nezvolí.</p>
      <p v-else>Konfigurace {{ capability.model }} připravena. Přístup k modelu ověří první spojení. {{ capability.speed.supported ? 'Tempo nastavuje zvolený engine.' : 'Engine používá pevné tempo.' }}</p>
      <fieldset><legend>Dostupné přihrádky pouze pro test</legend><label v-for="number in [1, 2, 3, 4, 5, 6]" :key="number" class="ready-option"><input v-model="ready" type="checkbox" :value="number"> {{ number }}</label></fieldset>
      <fieldset v-if="canBindGuest"><legend>Identita a souhlas nového hosta</legend><p>Bez ID začne anonymní host. Identitu ověřuje obsluha; fotografie ani odhad jména ji neověřují. Volba se neukládá do telefonu a po startu se vymaže.</p>
        <label class="aw-field">Stálé ID hosta (volitelné)<input v-model="guestId" class="aw-input" maxlength="80" autocomplete="off" data-guest-id></label>
        <label><input v-model="identityVerified" type="checkbox" :disabled="!guestId.trim()" data-guest-verified> Obsluha ověřila identitu hosta.</label><br>
        <label><input v-model="longTermAllowed" type="checkbox" :disabled="!identityVerified" data-guest-long-term> Host souhlasí s dlouhodobou osobní pamětí.</label><br>
        <label><input v-model="cloudExportAllowed" type="checkbox" :disabled="!identityVerified || !longTermAllowed" data-guest-cloud> Host souhlasí také s cloudovým indexem.</label>
        <p v-if="!guestValid" role="alert">Ověřte identitu a ID (1–80 znaků A–Z, a–z, 0–9, tečka, podtržítko, pomlčka). Cloudový export vyžaduje dlouhodobý souhlas.</p>
        <br><label><input v-model="guestConfirmed" type="checkbox" data-guest-confirm> Potvrzuji použití této ověřené identity a zvolených souhlasů pro nový rozhovor.</label>
      </fieldset>
      <label class="aw-field">Volitelný popis kostýmu<input v-model="costume" class="aw-input" maxlength="240" autocomplete="off"></label>
      <AwButton variant="primary" :disabled="!canAI" @click="start">Spustit AI rozhovor</AwButton>
    </fieldset>
    <fieldset :disabled="!canPhoto"><legend>Volitelná fotografie tohoto hosta</legend>
      <label><input v-model="consent" type="checkbox"> Host souhlasí s odesláním jedné fotografie do tohoto rozhovoru.</label>
      <p class="aw-help">Fotografie se předá serveru a OpenAI. Soukromý archiv rozhovoru ji uchová do výslovného ručního smazání; do galerie se neukládá.</p>
      <div class="phone-actions"><AwButton :disabled="!consent" @click="capture">Pořídit schválenou fotku</AwButton><AwButton :disabled="!photoDraft || !status.session" @click="attach">Odeslat schválenou fotku</AwButton><AwButton :disabled="!photoDraft && !photoUsed && !status.costumeStatus" @click="discard">Zahodit fotku</AwButton></div><p role="status">{{ photoMessage }}</p>
    </fieldset>
    <div class="phone-actions"><AwButton :disabled="!device.native || debug.cancellationPending.value || (!status.session && !status.running && !debug.pending.value && !photoDraft && !(status.transcript?.length))" @click="cancel">Ukončit testovací rozhovor</AwButton><AwButton :disabled="!device.native || debug.pending.value || status.session" @click="speak">Test reproduktoru telefonu</AwButton></div>
    <p>Průběžný poslech a přehrávání vlastní nativní audio. Staré textové, STT/TTS a PTT vstupy nejsou podporovány. Po ztrátě spojení rozhovor výslovně ukončete a začněte nový; staré audio se neopakuje.</p>
    <p v-if="status.effectiveEngine">Aktivní engine {{ status.effectiveEngine }} · {{ status.effectiveModel }} · generace {{ status.sessionGeneration }}.</p>
    <p v-if="status.errorCode" role="alert">{{ phoneError(status.errorCode) }}</p><ol v-if="status.transcript?.length" class="transcript" aria-label="Přepis testovacího rozhovoru"><li v-for="(line, index) in status.transcript" :key="index"><strong>{{ line.role === 'visitor' ? 'Host' : 'Kufr' }}</strong><p>{{ line.text }}</p></li></ol><p v-if="status.selected != null">Návrh přihrádky {{ status.selected }} · pouze testovací návrh, bez spuštění pohonů.</p>
    <dl v-if="status.audioOutput" class="phone-metrics"><div><dt>Výstup audia</dt><dd>{{ status.audioOutput.route }} · {{ Math.round(status.audioOutput.volume * 100) }} %</dd></div><div><dt>Přijato / přehráno</dt><dd>{{ status.audioOutput.receivedSeconds.toFixed(1) }} / {{ status.audioOutput.renderedSeconds.toFixed(1) }} s</dd></div><div><dt>Audio engine</dt><dd>{{ status.audioOutput.engineRunning ? 'Běží' : 'Stojí' }}</dd></div></dl>
  </section>
</template>
<style scoped>
.phone-actions{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.phone-fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,13rem),1fr));gap:1rem}.aw-field{display:grid;gap:.5rem;margin-block:.75rem}.ready-option{display:inline-flex;gap:.4rem;margin-inline-end:1rem}fieldset{border:1px solid var(--aw-border);border-radius:.75rem;margin-block:1rem;padding:1rem;min-width:0}.transcript{padding-inline-start:1.5rem}.transcript p{white-space:pre-wrap}.phone-metrics{display:flex;flex-wrap:wrap;gap:1.5rem}.phone-metrics dd{margin:0}
</style>

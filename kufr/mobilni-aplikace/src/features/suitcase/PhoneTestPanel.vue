<script setup lang="ts">
import AwButton from '../../components/AwButton.vue';
import { computed, onBeforeUnmount, onDeactivated, ref, watch } from 'vue';
import { debugBridge, nativeDebug as debug } from './nativeDebug';
import { deviceSession as device } from './deviceSession';
import { phoneError } from './phoneErrors';
import { deviceAssociation as cloud } from './deviceAssociation';
const status = debug.status;
const engine = ref<'realtime' | 'gpt-live' | 'realtime-ptt' | 'chained'>('realtime');
const voice = ref<'cedar' | 'marin' | 'onyx' | 'coral'>('cedar');
const speed = ref(1);
const ready = ref([1, 2, 3, 4, 5, 6]);
const answer = ref('');
const costume = ref('');
const consent = ref(false);
const photoDraft = ref<string>();
const photoMessage = ref('Bez fotky. Rozhovor ji nepotřebuje.');
let photoSequence = 0;
let photoTimer: ReturnType<typeof setTimeout> | undefined;
const accountAccess = computed(() => status.value.accessMode !== 'account' ||
  (!!cloud.account.value && cloud.canOperate.value && status.value.deviceId === device.savedDevice.value.deviceId));
const canAnswer = computed(() => accountAccess.value && device.native && status.value.configured && status.value.session && !debug.pending.value && !status.value.running && !status.value.recording && status.value.phase === 'awaiting');
const canRecord = computed(() => device.native && status.value.configured && !debug.pending.value && status.value.session && (status.value.recording || (!status.value.running && status.value.phase === 'awaiting')));
const canAI = computed(() => accountAccess.value && debug.idle.value && status.value.mode === 'openai' && (status.value.dialogMode ?? status.value.server?.dialogMode) === 'responses');
const canFake = computed(() => accountAccess.value && debug.idle.value && status.value.mode === 'fake' && (status.value.dialogMode ?? status.value.server?.dialogMode) === 'fake');
const handsFree = computed(() => status.value.handsFree || (['realtime', 'gpt-live'].includes(engine.value) && (status.value.dialogMode ?? status.value.server?.dialogMode) === 'responses'));
watch(engine, next => { if (next !== 'chained' && voice.value === 'onyx') voice.value = 'cedar'; if (next === 'gpt-live') speed.value = 1; });
watch(status, (value, previous) => { if (previous.configured && !value.configured) { hide(); answer.value = ''; } });
function clearDraft(): void {
  ++photoSequence;
  if (photoTimer) clearTimeout(photoTimer);
  photoDraft.value = undefined; consent.value = false; costume.value = '';
  photoMessage.value = 'Bez fotky. Rozhovor ji nepotřebuje.';
}
async function discard(): Promise<void> { clearDraft(); if (device.native) await debug.run(() => debugBridge.discardCostumePhoto()); }
function hide(): void {
  // A camera grant applies to one upcoming guest, never a later route visit.
  const hadDraft = photoDraft.value !== undefined;
  clearDraft(); answer.value = '';
  if (hadDraft && device.native) void debugBridge.discardCostumePhoto().catch(() => undefined);
}
function visibility(): void { if (document.hidden) hide(); }
document.addEventListener('visibilitychange', visibility);
onBeforeUnmount(() => { hide(); document.removeEventListener('visibilitychange', visibility); });
onDeactivated(hide);
async function capture(): Promise<void> {
  if (!consent.value || !debug.idle.value) return;
  const sequence = ++photoSequence;
  consent.value = false;
  await debug.run(async () => {
    const photo = await debugBridge.captureCostumePhoto();
    if (sequence !== photoSequence) { await debugBridge.discardCostumePhoto(); return; }
    photoDraft.value = photo.draftId ?? undefined;
    photoMessage.value = photo.draftId ? 'Jednorázová fotka připravená. Použije se jen pro následující rozhovor.' : 'Fotografie není dostupná; pokračujte bez ní.';
    photoTimer = setTimeout(() => { hide(); }, 120000);
  });
}
async function start(expectedMode: 'fake' | 'openai'): Promise<void> {
  if (expectedMode === 'fake' ? !canFake.value : !canAI.value) return;
  const draft = photoDraft.value;
  const description = costume.value.trim() || undefined;
  const readyMask = ready.value.reduce((mask, number) => mask | (1 << (number - 1)), 0);
  clearDraft(); answer.value = '';
  await debug.run(() => debugBridge.startConversation({ expectedMode, readyMask, photoDraftId: draft,
    costumeDescription: description, voice: voice.value, speed: speed.value, engine: expectedMode === 'fake' ? 'chained' : engine.value }));
}
async function send(): Promise<void> { if (!canAnswer.value || !answer.value.trim()) return;
  const text = answer.value; answer.value = ''; await debug.run(() => debugBridge.answerConversation({ text })); }
async function cancel(): Promise<void> { clearDraft(); answer.value = ''; await debug.cancel(); }
function speakSystem(): void { void debug.run(() => debugBridge.speakSystem()); }
function opening(expectedMode: 'fake' | 'openai'): void { void debug.run(() => debugBridge.runOpeningSpeech({ expectedMode })); }
function record(): void { void debug.run(() => status.value.recording ? debugBridge.finishRecording() : debugBridge.startRecording()); }
</script>

<template>
  <section class="aw-card phone-test" aria-labelledby="phone-heading">
    <h2 id="phone-heading">Test hlasu na telefonu</h2><p>Tento test komunikuje přímo s backendem přes ověřený účet a konkrétní povolený kufr. Nepotvrzuje mikrofon, reproduktor ani fyzické efekty kufru. AI režimy čerpají OpenAI účet.</p>
    <p class="aw-notice">{{ device.native ? `Nativní audio: ${status.mode} · ${status.phase}` : 'Nativní mikrofon a přístup k backendu vyžadují aplikaci na iPhonu.' }}</p>
    <div class="phone-actions"><AwButton :disabled="!debug.idle.value" @click="speakSystem">Systémový hlas iPhonu</AwButton><AwButton :disabled="!canFake" @click="opening('fake')">Fake úvodní replika</AwButton><AwButton :disabled="!canAI" @click="opening('openai')">AI úvodní replika</AwButton></div>
    <fieldset :disabled="!debug.idle.value || !accountAccess"><legend>Nový testovací rozhovor</legend><div class="phone-fields"><label class="aw-field">Režim<select v-model="engine" class="aw-input"><option value="realtime">Realtime · průběžný poslech</option><option value="gpt-live">GPT-Live + Sol · test kvality</option><option value="realtime-ptt">Realtime · stiskem</option><option value="chained">STT → dialog → TTS</option></select></label><label class="aw-field">Hlas<select v-model="voice" class="aw-input"><option value="cedar">Cedar</option><option value="marin">Marin</option><option value="coral">Coral</option><option value="onyx" :disabled="engine !== 'chained'">Onyx · STT/TTS</option></select></label><label class="aw-field">Rychlost<select v-model="speed" class="aw-input" :disabled="engine === 'gpt-live'"><option :value="0.85">0.85×</option><option :value="1">1×</option><option :value="1.15">1.15×</option></select></label></div>
      <fieldset><legend>Dostupné přihrádky pouze pro test</legend><label v-for="number in [1, 2, 3, 4, 5, 6]" :key="number" class="ready-option"><input v-model="ready" type="checkbox" :value="number"> {{ number }}</label></fieldset>
      <label class="aw-field">Volitelný popis kostýmu<input v-model="costume" class="aw-input" maxlength="500" autocomplete="off"></label><label><input v-model="consent" type="checkbox"> Host souhlasí s jednou fotografií pro tento rozhovor.</label><p class="aw-help">Fotka je dočasná a předá se serveru a OpenAI pouze pro následující rozhovor. Přepis může uchovávat paměť večera.</p><div class="phone-actions"><AwButton :disabled="!consent" @click="capture">Pořídit schválenou fotku</AwButton><AwButton :disabled="!photoDraft" @click="discard">Zahodit fotku</AwButton></div><p role="status">{{ photoMessage }}</p>
      <div class="phone-actions"><AwButton variant="primary" :disabled="!canFake" @click="start('fake')">Spustit fake rozhovor</AwButton><AwButton variant="primary" :disabled="!canAI" @click="start('openai')">Spustit AI rozhovor</AwButton></div>
    </fieldset>
    <AwButton :disabled="!device.native || debug.cancellationPending.value || (!status.session && !status.running && !debug.pending.value && !photoDraft && !(status.transcript?.length))" @click="cancel">Ukončit testovací rozhovor</AwButton>
    <template v-if="!handsFree"><form @submit.prevent="send"><label class="aw-field">Odpověď hosta<textarea v-model="answer" class="aw-input" rows="3" maxlength="2000" :disabled="!canAnswer" /></label><div class="phone-actions"><AwButton type="submit" :disabled="!canAnswer || !answer.trim()">Odeslat odpověď</AwButton><AwButton :disabled="!canRecord" @click="record">{{ status.recording ? 'Dokončit nahrávání a odeslat' : 'Začít nahrávat odpověď' }}</AwButton></div></form></template>
    <p v-else>Průběžný poslech obsluhuje nativní audio. Ukončit rozhovor zůstává dostupné i během přehrávání.</p>
    <p v-if="status.errorCode" role="alert">{{ phoneError(status.errorCode) }}</p><ol v-if="status.transcript?.length" class="transcript" aria-label="Přepis testovacího rozhovoru"><li v-for="(line, index) in status.transcript" :key="index"><strong>{{ line.role === 'visitor' ? 'Host' : 'Kufr' }}</strong><p>{{ line.text }}</p></li></ol><p v-if="status.selected != null">Návrh přihrádky {{ status.selected }} · pouze testovací návrh, bez spuštění pohonů.</p>
    <dl v-if="status.audioOutput" class="phone-metrics"><div><dt>Výstup audia</dt><dd>{{ status.audioOutput.route }} · {{ Math.round(status.audioOutput.volume * 100) }} %</dd></div><div><dt>Přijato / přehráno</dt><dd>{{ status.audioOutput.receivedSeconds.toFixed(1) }} / {{ status.audioOutput.renderedSeconds.toFixed(1) }} s</dd></div><div><dt>Audio engine</dt><dd>{{ status.audioOutput.engineRunning ? 'Běží' : 'Stojí' }}</dd></div></dl><p v-if="status.timings">Celkem {{ status.timings.totalMs }} ms · dialog {{ status.timings.dialogMs ?? '—' }} ms · generování řeči {{ status.timings.speechMs ?? '—' }} ms · stažení {{ status.timings.downloadMs ?? '—' }} ms · první audio {{ status.timings.firstAudioMs ?? '—' }} ms</p>
  </section>
</template>

<style scoped>
.phone-actions{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.phone-fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,13rem),1fr));gap:1rem}.aw-field{display:grid;gap:.5rem;margin-block:.75rem}.ready-option{display:inline-flex;gap:.4rem;margin-inline-end:1rem}fieldset{border:1px solid var(--aw-border);border-radius:.75rem;margin-block:1rem;padding:1rem;min-width:0}.transcript{padding-inline-start:1.5rem}.transcript p{white-space:pre-wrap}.phone-metrics{display:flex;flex-wrap:wrap;gap:1.5rem}.phone-metrics dd{margin:0}textarea{width:100%;box-sizing:border-box}
</style>

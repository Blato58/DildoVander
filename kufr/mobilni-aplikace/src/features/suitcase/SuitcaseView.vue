<script setup lang="ts">
import AwButton from '../../components/AwButton.vue';
import { computed, nextTick, onActivated, onBeforeUnmount, onDeactivated, ref, watch, type ComponentPublicInstance } from 'vue';
import { deviceSession as device } from './deviceSession';
import { liveActionBlocker, operatorReadiness } from '../device_connection/OperatorReadiness';

const snapshot = device.snapshot;
const workspace = ref<'voice' | 'dance' | 'text' | 'manual'>('voice');
const mode = ref<'off' | 'realtime' | 'gpt-live'>('off');
const voice = ref<'marin' | 'cedar' | 'coral'>('marin');
const voiceDraft = ref(false);
const imageConsent = ref(false);
const text = ref('');
const intent = ref<{ kind: 'select' | 'reload'; compartment: number; revision?: number } | null>(null);
const cancelConfirmation = ref<ComponentPublicInstance>();
const readiness = computed(() => snapshot.value ? operatorReadiness(snapshot.value) : null);
const manualReady = computed(() => snapshot.value?.controlMode === 'manual' && snapshot.value.phase === 'ready' && !snapshot.value.busy);
const voiceBlocker = computed(() => snapshot.value ? liveActionBlocker(snapshot.value, 'voice') : 'Nejprve připojte skutečný kufr.');
const danceBlocker = computed(() => snapshot.value ? liveActionBlocker(snapshot.value, 'dance') : 'Nejprve připojte skutečný kufr.');
const compartments = [1, 2, 3, 4, 5, 6];
const fixtureText = computed(() => snapshot.value?.source === 'e2-simulator');
const workspaces = computed(() => (['voice', 'dance', ...(fixtureText.value ? ['text'] : []), 'manual'] as Array<'voice' | 'dance' | 'text' | 'manual'>));
const workspaceLabels = { voice: 'Hlas', dance: 'Tanec', text: 'Text', manual: 'Ruční ovládání' };
function clearIntent(): void { intent.value = null; imageConsent.value = false; }
function visibility(): void { if (document.hidden) { clearIntent(); text.value = ''; } }
document.addEventListener('visibilitychange', visibility);
onBeforeUnmount(() => document.removeEventListener('visibilitychange', visibility));
onDeactivated(clearIntent);
onActivated(clearIntent);
watch(snapshot, (state, previous) => {
  if (!state || !manualReady.value || intent.value?.revision !== state.revision) intent.value = null;
  if (!state || state.phase === 'disconnected' || state.phase === 'stopped' || (previous?.controlMode === 'manual' && state.controlMode !== 'manual')) text.value = '';
  if (!state?.voice?.cameraAvailable) imageConsent.value = false;
  if (workspace.value === 'text' && !fixtureText.value) { workspace.value = 'voice'; text.value = ''; }
  if (!voiceDraft.value && state?.voice) { mode.value = state.voice.mode; voice.value = state.voice.voice; }
});
watch(workspace, clearIntent);
watch(intent, async action => { if (action) { await nextTick(); if (intent.value) cancelConfirmation.value?.$el.focus(); } });
function confirm(): void {
  const action = intent.value;
  intent.value = null;
  if (!action || !manualReady.value || action.revision !== snapshot.value?.revision) return;
  if (action.kind === 'select') device.select(action.compartment); else device.reload(action.compartment);
}
function configure(): void { if (device.configureVoice(mode.value, voice.value)) voiceDraft.value = false; }
function startVoice(): void { const consent = imageConsent.value; imageConsent.value = false; device.startVoice(consent); }
function submit(): void { if (fixtureText.value && text.value.trim() && device.submitText(text.value)) text.value = ''; }
</script>

<template>
  <div class="suitcase-view">
    <header class="feature-heading"><p class="aw-eyebrow">Provoz</p><h1>Kufr</h1>
      <p>Hlas, tanec a ruční ovládání podle potvrzeného stavu kufru.</p>
      <RouterLink to="/connections" class="aw-button aw-button--secondary">{{ device.connected.value ? 'Spravovat připojení' : 'Připojit kufr' }}</RouterLink>
    </header>
    <section v-if="!snapshot" class="aw-card" aria-labelledby="unavailable-title">
      <h2 id="unavailable-title">Kufr není připojený</h2><p>Nejprve vyberte skutečný kufr v Připojení. Pro prohlídku webu je dostupná označená místní ukázka.</p>
      <p role="status">{{ device.message.value }}</p>
      <AwButton v-if="!device.native" class="aw-button aw-button--secondary" @click="device.useDemo">Otevřít místní ukázku</AwButton>
    </section>
    <template v-else>
      <p v-if="snapshot.source !== 'native-device'" class="aw-notice" role="note"><strong>{{ snapshot.source === 'local-fake' ? 'MÍSTNÍ UKÁZKA' : 'E2 SIMULÁTOR' }}</strong> · Žádné fyzické efekty ani ověřený hardware.</p>
      <section class="aw-card" aria-labelledby="readiness-title">
        <h2 id="readiness-title">{{ readiness?.title }}</h2><p>{{ readiness?.message }}</p><p role="status">{{ snapshot.status }}</p>
        <dl class="suitcase-metrics"><div><dt>Spojení</dt><dd>{{ snapshot.phase }}</dd></div><div><dt>Řízení</dt><dd>{{ snapshot.controlMode === 'manual' ? 'Ruční' : 'Pozorování' }}</dd></div><div><dt>Přihrádky připravené</dt><dd>{{ compartments.filter(n => snapshot!.readyMask & (1 << (n - 1))).length }} / 6</dd></div></dl>
      </section>
      <nav class="suitcase-modes" aria-label="Způsob provozu"><AwButton v-for="item in workspaces" :key="item" :variant="workspace === item ? 'primary' : 'secondary'" :aria-pressed="workspace === item" @click="workspace = item">{{ workspaceLabels[item] }}</AwButton></nav>
      <section v-if="workspace === 'voice'" class="aw-card" aria-labelledby="voice-heading">
        <h2 id="voice-heading">Hlas kufru</h2><p>Kufr poslouchá svým mikrofonem a odpovídá reproduktorem. Spuštění používá AI a OpenAI účet; přepis uchovává paměť večera.</p>
        <form class="suitcase-form" @submit.prevent="configure">
          <label class="aw-field">Hlasový režim<select v-model="mode" class="aw-input" :disabled="!snapshot.voice?.canConfigure" @change="voiceDraft = true"><option value="off">Vypnutý</option><option value="gpt-live" :disabled="!snapshot.voice?.available">GPT Live · gpt-live-1</option><option value="realtime" :disabled="!snapshot.voice?.available">GPT Realtime 2.1 · gpt-realtime-2.1</option></select></label>
          <label class="aw-field">Hlas<select v-model="voice" class="aw-input" :disabled="!snapshot.voice?.canConfigure" @change="voiceDraft = true"><option value="marin">Marin</option><option value="cedar">Cedar</option><option value="coral">Coral</option></select></label>
          <AwButton type="submit" :disabled="!snapshot.voice?.canConfigure || snapshot.source !== 'native-device'">Použít nastavení hlasu</AwButton>
        </form>
        <label><input v-model="imageConsent" type="checkbox" :disabled="!snapshot.voice?.cameraAvailable || !snapshot.voice?.canStart || snapshot.busy"> Host souhlasí s jednou fotkou kostýmu pro tento rozhovor.</label>
        <p class="aw-help">Volitelné. Fotka se předá serveru a OpenAI jako nejistá nápověda. Soukromý archiv rozhovoru ji uchová do výslovného ručního smazání.</p>
        <p v-if="snapshot.source === 'native-device'">Textové tahy, STT/TTS a PTT byly vyřazeny. Skutečný kufr používá podporovaný audio engine; ruční řízení funguje i bez AI.</p>
        <p v-if="voiceBlocker" class="aw-notice">{{ voiceBlocker }}</p><p role="status">{{ snapshot.voice?.status }}</p>
        <div class="suitcase-actions"><AwButton variant="primary" :disabled="!!voiceBlocker || !snapshot.voice?.canStart" @click="startVoice">Spustit hlas na kufru</AwButton><AwButton :disabled="!snapshot.voice?.canStop" @click="device.stopVoice">Zastavit hlas</AwButton></div>
      </section>
      <section v-if="workspace === 'dance'" class="aw-card" aria-labelledby="dance-heading">
        <h2 id="dance-heading">Tanec</h2><p>Kufr vybere skladbu na SD kartě a provede její efekty.</p><p v-if="danceBlocker" class="aw-notice">{{ danceBlocker }}</p><p role="status">{{ snapshot.dance?.status }}</p>
        <progress v-if="snapshot.dance?.track" :max="Math.max(1, snapshot.dance.durationMs)" :value="Math.min(snapshot.dance.durationMs, snapshot.dance.positionMs)" aria-label="Průběh skladby" /><p v-if="snapshot.dance?.track">{{ snapshot.dance.track }} · {{ Math.round(snapshot.dance.positionMs / 1000) }} / {{ Math.round(snapshot.dance.durationMs / 1000) }} s · {{ snapshot.dance.bpm }} BPM</p>
        <div class="suitcase-actions"><AwButton variant="primary" :disabled="!!danceBlocker || !snapshot.dance?.canStart" @click="device.startDance">Spustit tanec</AwButton><AwButton :disabled="!snapshot.dance?.canStop" @click="device.stopDance">Zastavit tanec</AwButton></div>
      </section>
      <section v-if="workspace === 'text' && fixtureText" class="aw-card" aria-labelledby="text-heading">
        <h2 id="text-heading">Textová fixture simulátoru</h2><p>E2 simulátor poskytuje pevnou označenou odpověď bez provideru. Skutečný kufr textové tahy nepodporuje.</p>
        <p v-if="snapshot.controlMode !== 'manual'" class="aw-notice">Pro textový rozhovor převezměte ruční řízení.</p>
        <form @submit.prevent="submit"><label class="aw-field">Vaše odpověď<textarea v-model="text" class="aw-input" rows="3" maxlength="2000" required :disabled="!snapshot.conversation?.canSubmit" /></label><div class="suitcase-actions"><AwButton type="submit" variant="primary" :disabled="!snapshot.conversation?.canSubmit || !text.trim()">Odeslat text kufru</AwButton><AwButton :disabled="!snapshot.conversation?.canCancel" @click="text = ''; device.cancelText()">Zrušit tah</AwButton></div></form>
        <p role="status">{{ snapshot.conversation?.status }}</p><blockquote v-if="snapshot.conversation?.reply"><strong>{{ snapshot.conversation.simulated ? 'SIMULOVANÁ ODPOVĚĎ' : 'Odpověď kufru' }}</strong><p>{{ snapshot.conversation.reply }}</p></blockquote>
      </section>
      <section v-if="workspace === 'manual'" class="aw-card" aria-labelledby="manual-heading">
        <h2 id="manual-heading">Ruční ovládání</h2><p>Převzetí řízení blokuje autonomní hlas a tanec. Před výběrem ověřte volný prostor a skutečný stav přihrádek.</p>
        <div class="suitcase-actions"><AwButton variant="primary" :disabled="snapshot.phase !== 'ready' || snapshot.busy || snapshot.controlMode === 'manual'" @click="device.acquire">Převzít ruční řízení</AwButton><AwButton :disabled="snapshot.controlMode !== 'manual'" @click="intent = null; device.release()">Uvolnit řízení</AwButton><AwButton v-if="snapshot.source === 'local-fake' && snapshot.phase === 'stopped'" class="aw-button aw-button--secondary" @click="device.inspectDemo">Simulovat místní kontrolu</AwButton></div>
        <div class="suitcase-compartments"><article v-for="number in compartments" :key="number" class="aw-card"><h3>Přihrádka {{ number }}</h3><p>{{ snapshot.readyMask & (1 << (number - 1)) ? 'Dostupná podle kufru' : 'Nedostupná / vyžaduje kontrolu' }}</p><AwButton :disabled="!manualReady" @click="intent = { kind: snapshot.readyMask & (1 << (number - 1)) ? 'select' : 'reload', compartment: number, revision: snapshot.revision }">{{ snapshot.readyMask & (1 << (number - 1)) ? 'Vybrat přihrádku' : 'Potvrdit založení' }}</AwButton></article></div>
        <section v-if="intent" class="aw-notice" aria-labelledby="intent-title"><h3 id="intent-title">{{ intent.kind === 'select' ? 'Spustit výběr' : 'Potvrdit skutečné založení' }} · přihrádka {{ intent.compartment }}?</h3><p>{{ intent.kind === 'select' ? 'Potvrzení může spustit fyzický pohyb. Ověřte bezpečný prostor.' : 'Potvrďte pouze po skutečné místní kontrole hůlky a přihrádky.' }}</p><div class="suitcase-actions"><AwButton ref="cancelConfirmation" @click="intent = null">Zrušit</AwButton><AwButton variant="primary" @click="confirm">Potvrdit</AwButton></div></section>
      </section>
      <RouterLink to="/suitcase/diagnostics">Diagnostika a servisní nástroje</RouterLink>
    </template>
  </div>
</template>

<style scoped>
.suitcase-view{display:grid;gap:1.5rem}.suitcase-actions,.suitcase-modes{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.suitcase-metrics{display:flex;flex-wrap:wrap;gap:2rem}.suitcase-metrics dd{margin:0;font-weight:600}.suitcase-compartments{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,15rem),1fr));gap:1rem;margin-block:1rem}.suitcase-form{display:grid;gap:1rem;margin-block:1rem}.aw-field{display:grid;gap:.4rem}textarea{width:100%;box-sizing:border-box}blockquote{margin-inline:0;padding:1rem;border-inline-start:3px solid var(--aw-accent)}
</style>

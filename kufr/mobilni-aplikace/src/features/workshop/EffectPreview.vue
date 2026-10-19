<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import { previewFrame, validateEffect } from './effect-preview.mjs';

const props = defineProps<{ model: any; angles: number[] }>();
const emit = defineEmits<{ frame: [value: { angles: number[]; ledColors: Record<string, number[]>; outputs: Record<string, string> }]; active: [value: boolean] }>();
const selected = ref(1), audioEnabled = ref(false), playing = ref(false), stopped = ref(false);
const phases = ref<any[]>([]), phaseIndex = ref(-1), elapsed = ref(0), status = ref('Ilustrativní sekvence; žádné fyzické povely.');
const total = computed(() => phases.value.reduce((sum, phase) => sum + phase.duration_ms, 0));
let animation: number | null = null, context: AudioContext | null = null, oscillator: OscillatorNode | null = null, gain: GainNode | null = null;
let admissionTime = 0, phaseStarted: number | null = null, initial: number[] = [];
let lastAngles: number[] | null = null;
function stopTone() { if (oscillator) { try { oscillator.stop(); } catch { /* The oscillator may have ended. */ } oscillator.disconnect(); oscillator = null; } gain?.disconnect(); gain = null; }
function clearOutput() { emit('frame', { angles: [...(lastAngles || props.angles)], ledColors: {}, outputs: {} }); }
function halt(fault = true) {
  if (animation !== null) cancelAnimationFrame(animation); animation = null; playing.value = false; emit('active', false);
  stopTone(); clearOutput(); stopped.value = fault;
  if (fault) status.value = 'Stop ilustrace: lokální výstupy vypnuty, dosažená modelová poloha zachována. Pro další náhled potvrďte reset.';
}
function tone(frequency: number) {
  stopTone(); if (!audioEnabled.value || !context || !frequency) return;
  gain = context.createGain(); gain.gain.value = .035;
  oscillator = context.createOscillator(); oscillator.frequency.value = frequency; oscillator.connect(gain); gain.connect(context.destination); oscillator.start();
}
function tick(timestamp: number) {
  animation = null; if (!playing.value) return;
  if (performance.now() < admissionTime || performance.now() - admissionTime >= 15000) { halt(); status.value = 'Technický limit 15 s: ilustrace zastavena; poloha zůstává modelová.'; return; }
  const phase = phases.value[phaseIndex.value];
  if (phaseStarted === null) { phaseStarted = timestamp; initial = [...props.angles]; tone(phase.audio ? phase.tone_hz : 0); }
  const fraction = Math.max(0, Math.min(1, (timestamp - phaseStarted) / phase.duration_ms));
  elapsed.value = phases.value.slice(0, phaseIndex.value).reduce((sum, value) => sum + value.duration_ms, 0) + fraction * phase.duration_ms;
  const frame = previewFrame(props.model, phase, initial, selected.value - 1, fraction);
  lastAngles = frame.angles; emit('frame', frame);
  status.value = `${phase.phase}${phase.vibration ? ' · ilustrace vibrace' : ''}`;
  if (fraction >= 1) { phaseIndex.value++; phaseStarted = null; stopTone(); }
  if (phaseIndex.value === phases.value.length) { playing.value = false; emit('active', false); clearOutput(); status.value = 'Ilustrační sekvence dokončena. Není to fyzická zkouška.'; return; }
  animation = requestAnimationFrame(tick);
}
function start(kind: string) {
  if (playing.value || stopped.value) return;
  try {
    phases.value = validateEffect(props.model.effects?.[kind]);
    admissionTime = performance.now(); phaseIndex.value = 0; phaseStarted = null; elapsed.value = 0; lastAngles = [...props.angles];
    if (audioEnabled.value) { context ??= new AudioContext(); void context.resume().catch(() => { status.value = 'Lokální zvuk není dostupný; pokračuje vizuální náhled.'; }); }
    playing.value = true; emit('active', true); animation = requestAnimationFrame(tick);
  } catch (cause) { status.value = cause instanceof Error ? cause.message : String(cause); }
}
function reset() { stopped.value = false; status.value = 'Reset pouze ilustrace potvrzen. Fyzická poloha zůstává neověřena.'; }
onBeforeUnmount(() => { if (animation !== null) cancelAnimationFrame(animation); animation = null; stopTone(); emit('active', false); if (context) void context.close(); context = null; });
</script>

<template>
  <details class="effect-preview">
    <summary>Ilustrační sekvence efektů</summary>
    <p>Lokální náhled světel, pohybu a volitelných tónů podle sdílených dat. Nepoužívá AI ani skutečný kufr.</p>
    <AwField id="effect-slot" label="Přihrádka pro ilustraci">
      <select id="effect-slot" v-model.number="selected" class="aw-input" :disabled="playing"><option v-for="slot in model.mechanism.count" :key="slot" :value="slot">{{ slot }}</option></select>
    </AwField>
    <label><input v-model="audioEnabled" type="checkbox" :disabled="playing" /> Lokální zvukový náhled tónů</label>
    <div class="effect-actions">
      <AwButton :disabled="playing || stopped" @click="start('main')">Hlavní ilustrace</AwButton>
      <AwButton :disabled="playing || stopped" @click="start('short')">Krátká ilustrace</AwButton>
      <AwButton variant="danger" :disabled="!playing" @click="halt()">Stop ilustrace</AwButton>
      <AwButton :disabled="!stopped" @click="reset">Potvrdit reset ilustrace</AwButton>
    </div>
    <p role="status">{{ status }}</p><p>{{ (elapsed / 1000).toFixed(1) }} / {{ (total / 1000).toFixed(1) }} s</p>
    <ol><li v-for="(phase, index) in phases" :key="index" :aria-current="playing && index === phaseIndex ? 'step' : undefined">{{ phase.phase }} · {{ phase.duration_ms }} ms</li></ol>
  </details>
</template>

<style scoped>
.effect-preview { margin-top: 1rem; }.effect-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-block: .75rem; }
</style>

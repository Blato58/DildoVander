<script setup lang="ts">
import { computed, onBeforeUnmount, onDeactivated, onMounted, ref, shallowRef } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { decodeMusic, MUSIC_FEATURES, waveformPaths } from '../../../../firmware/diagnostics/servo-bench/web/music.mjs';
import { servoBench as bench } from './servoBench';

const audio = ref<HTMLAudioElement>();
let music: ReturnType<typeof bench.createMusic> | null = null;
const state = shallowRef({ active: false, time: 0, message: 'Vyberte místní skladbu.', width: null as number | null,
  rttMs: 0, updateHz: 0, appliedWidths: [] as number[] });
const analysis = shallowRef<Awaited<ReturnType<typeof decodeMusic>> | null>(null);
const loading = ref(false);
const duration = ref(0);
const summary = ref('Zvuk zůstává v prohlížeči. Nic se nenahrává na server.');
const dance = ref(false); const consent = ref(false); const strength = ref(40); const leadMs = ref(100); const preview = ref(0);
const channels = ref(MUSIC_FEATURES.map((feature, channel) => ({ channel, enabled: channel === 0, feature: feature.id })));
const paths = computed(() => waveformPaths(analysis.value, strength.value, channels.value[preview.value]?.feature || 'phrase'));
const cursor = computed(() => state.value.time / (duration.value || 1) * 1000);
const applied = computed(() => state.value.appliedWidths.map((value, channel) => value ? `${channel}: ${value} µs` : '').filter(Boolean).join(' · '));
let url: string | null = null; let selection = 0;
onMounted(() => { music = bench.createMusic(audio.value!, () => { if (music) state.value = { ...music.state }; }); });
function leave(): void { ++selection; loading.value = false; consent.value = false; music?.stop(); }
onDeactivated(leave);
onBeforeUnmount(() => { leave(); bench.releaseMusic(); if (url) URL.revokeObjectURL(url); });
async function load(event: Event): Promise<void> {
  const file = (event.target as HTMLInputElement).files?.[0]; const current = ++selection;
  music?.stop(); if (music) music.analysis = null;
  duration.value = 0; analysis.value = null;
  if (url) URL.revokeObjectURL(url);
  url = null; audio.value!.removeAttribute('src'); audio.value!.load();
  if (!file) return;
  loading.value = true; summary.value = 'Analyzuji waveform, pásma a akcenty…';
  try {
    const decoded = await decodeMusic(file);
    if (selection !== current || !music) return;
    music.analysis = decoded; analysis.value = decoded; duration.value = decoded.duration;
    url = URL.createObjectURL(file); audio.value!.src = url; audio.value!.load();
    summary.value = `${file.name} · ${decoded.duration.toFixed(1)} s · ${decoded.bpm ? `odhad ${Math.round(decoded.bpm)} BPM` : 'bez zřetelného rytmu'} · ${decoded.accentCount} akcentů · FFT / 10 ms`;
    music.state.message = 'Připraveno. Vyberte kanály a jejich zvukové složky.'; state.value = { ...music.state };
  } catch (error) { if (selection === current) summary.value = error instanceof Error ? error.message : 'Zvuk nelze načíst.'; }
  finally { if (selection === current) loading.value = false; }
}
function play(): void {
  const permitted = consent.value; consent.value = false;
  void music?.play({ dance: dance.value, consent: permitted, channels: channels.value.filter(value => value.enabled), strength: strength.value, leadMs: leadMs.value });
}
function stop(): void { ++selection; loading.value = false; music?.stop(); }
</script>

<template>
  <section aria-labelledby="servo-music-title" class="servo-music">
    <h3 id="servo-music-title">Waveform a tanec serv</h3>
    <p>Plynulé polohy sledují waveform, basy, středy, výšky a akcenty. Každý vybraný kanál může sledovat jinou složku. Společné snímky mění kanály současně, cílově 25× za sekundu; tanec nemá 500ms minimum ani pulzní cyklus.</p>
    <p>Meze 600–2300 µs jsou fyzicky potvrzené pro všech šest serv (21. 10. 2026). Použijte pouze volná serva bez mechanismu a ověřte napájení pro jejich společný provoz. Poloha ani proud nejsou měřené.</p>
    <label class="aw-field">Místní skladba · do 32 MiB a 10 minut<input class="aw-input" type="file" accept="audio/*" :disabled="loading || state.active" @change="load"></label>
    <p>{{ summary }}</p><audio ref="audio" preload="auto"></audio>
    <label><input v-model="dance" type="checkbox" :disabled="state.active"> Vybraná serva tančí se zvukem</label>
    <fieldset :disabled="state.active"><legend>Kanály a zvukové složky</legend>
      <div v-for="value in channels" :key="value.channel" class="music-channel"><label><input v-model="value.enabled" type="checkbox"> Kanál {{ value.channel }}</label><select v-model="value.feature" class="aw-input" :aria-label="`Zvuková složka kanálu ${value.channel}`"><option v-for="feature in MUSIC_FEATURES" :key="feature.id" :value="feature.id">{{ feature.label }}</option></select></div>
    </fieldset>
    <label><input v-model="consent" type="checkbox" :disabled="state.active"> Vybraná volná serva, napájení a prostor jsou připravené. Povoluji toto Play a zůstávám u vypínače.</label>
    <label class="aw-field">Rozsah tance · % potvrzeného rozsahu<input v-model.number="strength" class="aw-input" type="number" min="1" max="100" :disabled="state.active"></label>
    <label class="aw-field">Předstih serv vůči slyšitelnému zvuku · ms<input v-model.number="leadMs" class="aw-input" type="number" min="0" max="300" step="10" :disabled="state.active"></label>
    <p>Výstupní hodiny zvuku a odhad RTT/2 korigují časování; předstih dolaďte podle odezvy serv. Zpožděné polohy se nezřazují do fronty. Watchdog vyžádá FULL_OFF po 250 ms bez snímku; blokované I2C ani vypnutí 5 V tím nejsou zaručené.</p>
    <label class="aw-field">Náhled pohybu kanálu<select v-model.number="preview" class="aw-input"><option v-for="value in channels" :key="value.channel" :value="value.channel">Kanál {{ value.channel }}</option></select></label>
    <svg class="music-waveform" viewBox="0 0 1000 190" role="img" aria-label="Waveform nahoře a plánovaný pohyb kanálu dole"><path class="music-wave" :d="paths.waveform"/><path class="music-motion" :d="paths.motion"/><line class="music-cursor" :x1="cursor" :x2="cursor" y1="0" y2="190"/></svg>
    <p>RTT {{ state.rttMs.toFixed(1) }} ms · {{ state.updateHz.toFixed(1) }} potvrzených snímků/s · {{ applied || 'Žádné potvrzené cíle.' }}</p>
    <div class="bench-actions"><AwButton variant="primary" :disabled="loading || !duration || state.active" @click="play">Play</AwButton><AwButton :disabled="!state.active" @click="music?.pause()">Pause</AwButton><AwButton variant="danger" :disabled="!duration && !loading" @click="stop">Stop hudby a tance</AwButton></div>
    <progress :max="duration || 1" :value="state.time" aria-label="Průběh skladby"></progress>
    <p>{{ state.time.toFixed(1) }} / {{ duration.toFixed(1) }} s</p><p role="status">{{ state.message }}</p>
  </section>
</template>

<style scoped>
.servo-music{display:grid;gap:.75rem}.aw-field{display:grid;gap:.5rem}.bench-actions{display:flex;flex-wrap:wrap;gap:.75rem}progress{width:100%}fieldset{display:grid;gap:.75rem;min-width:0;border:1px solid var(--aw-border);border-radius:var(--aw-radius-control);padding:1rem}.music-channel{display:grid;grid-template-columns:minmax(6rem,1fr) minmax(0,2fr);gap:.75rem;align-items:center}.music-waveform{width:100%;border:1px solid var(--aw-border);border-radius:var(--aw-radius-control);background:var(--aw-surface)}.music-wave{fill:var(--aw-muted);opacity:.5}.music-motion{fill:none;stroke:var(--aw-accent);stroke-width:3}.music-cursor{stroke:var(--aw-ivory);stroke-width:2}
</style>

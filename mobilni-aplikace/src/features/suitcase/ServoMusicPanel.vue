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
const dance = ref(false); const consent = ref(false); const strength = ref(40); const leadMs = ref(100);
const channels = ref(MUSIC_FEATURES.map((feature, channel) => ({ channel, enabled: channel === 0, feature: feature.id })));
const paths = computed(() => channels.value.map(value => waveformPaths(analysis.value, strength.value, value.feature)));
const waveform = computed(() => waveformPaths(analysis.value).waveform);
const featureName = (id: string) => MUSIC_FEATURES.find(feature => feature.id === id)?.label.split(' · ')[0];
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
    <label class="aw-field">Místní skladba · do 32 MiB a 10 minut<input class="aw-input" type="file" accept="audio/*" :disabled="loading || state.active" @change="load"></label>
    <p>{{ summary }}</p><audio ref="audio" preload="auto"></audio>
    <div class="music-overview" aria-label="Přehled skladby a všech šesti kanálů">
      <div class="music-lane-labels"><div class="music-lane-label"><strong>Zvuk</strong><small>celá skladba</small></div><div v-for="value in channels" :key="value.channel" class="music-lane-label"><strong>{{ value.channel }} · {{ featureName(value.feature) }}</strong><small>{{ value.enabled ? 'vybraný kanál' : 'vypnuto · náhled' }}</small></div></div>
      <svg class="music-waveform" viewBox="0 0 1000 280" preserveAspectRatio="none" role="img" aria-label="Waveform skladby a šest plánovaných drah. Svislá linka ukazuje společnou pozici přehrávání.">
        <g transform="scale(1 .4)"><path class="music-wave" :d="waveform"/></g>
        <g v-for="value in channels" :key="value.channel" :class="{ 'music-lane-off': !value.enabled }" :transform="`translate(0 ${4 + value.channel * 40}) scale(1 .4)`"><line class="music-baseline" x1="0" x2="1000" y1="140" y2="140"/><path class="music-motion" :d="paths[value.channel].motion"/></g>
        <g class="music-playhead" :transform="`translate(${Math.min(1000, Math.max(0, cursor))} 0)`"><line class="music-cursor-outline" x1="0" x2="0" y1="0" y2="280"/><line class="music-cursor" x1="0" x2="0" y1="0" y2="280"/><path class="music-cursor-tip" d="M-6,0 L6,0 L0,7 Z"/></g>
      </svg>
    </div>
    <label><input v-model="dance" type="checkbox" :disabled="state.active"> Vybraná serva tančí se zvukem</label>
    <label><input v-model="consent" type="checkbox" :disabled="state.active"> Vybraná volná serva, napájení a prostor jsou připravené. Povoluji toto Play a zůstávám u vypínače.</label>
    <div class="bench-actions"><AwButton variant="primary" :disabled="loading || !duration || state.active" @click="play">Play</AwButton><AwButton :disabled="!state.active" @click="music?.pause()">Pause</AwButton><AwButton variant="danger" :disabled="!duration && !loading" @click="stop">Stop hudby a tance</AwButton></div>
    <progress :max="duration || 1" :value="state.time" aria-label="Průběh skladby"></progress>
    <p>{{ state.time.toFixed(1) }} / {{ duration.toFixed(1) }} s</p><p role="status">{{ state.message }}</p>
    <p>RTT {{ state.rttMs.toFixed(1) }} ms · {{ state.updateHz.toFixed(1) }} potvrzených snímků/s · {{ applied || 'Žádné potvrzené cíle.' }}</p>
    <fieldset :disabled="state.active"><legend>Kanály a zvukové složky</legend>
      <div v-for="value in channels" :key="value.channel" class="music-channel"><label><input v-model="value.enabled" type="checkbox"> Kanál {{ value.channel }}</label><select v-model="value.feature" class="aw-input" :aria-label="`Zvuková složka kanálu ${value.channel}`"><option v-for="feature in MUSIC_FEATURES" :key="feature.id" :value="feature.id">{{ feature.label }}</option></select></div>
    </fieldset>
    <label class="aw-field">Rozsah tance · % potvrzeného rozsahu<input v-model.number="strength" class="aw-input" type="number" min="1" max="100" :disabled="state.active"></label>
    <label class="aw-field">Předstih serv vůči slyšitelnému zvuku · ms<input v-model.number="leadMs" class="aw-input" type="number" min="0" max="300" step="10" :disabled="state.active"></label>
    <p>Výstupní hodiny zvuku a odhad RTT/2 korigují časování; předstih dolaďte podle odezvy serv. Zpožděné polohy se nezřazují do fronty. Watchdog vyžádá FULL_OFF po 250 ms bez snímku; blokované I2C ani vypnutí 5 V tím nejsou zaručené.</p>
    <details><summary>Jak číst křivky a připravit serva</summary><p>Každý řádek ukazuje plánovanou dráhu podle přiřazené složky. Vypnuté kanály jsou přerušované. Linka sleduje slyšitelnou pozici skladby, bez předstihu serv; neukazuje měřenou polohu.</p>
    <p>Plynulé polohy sledují waveform, basy, středy, výšky a akcenty. Každý vybraný kanál může sledovat jinou složku. Společné snímky mění kanály současně, cílově 25× za sekundu; tanec nemá 500ms minimum ani pulzní cyklus.</p>
    <p>Meze 600–2300 µs jsou fyzicky potvrzené pro všech šest serv (21. 10. 2026). Použijte pouze volná serva bez mechanismu a ověřte napájení pro jejich společný provoz. Poloha ani proud nejsou měřené.</p>
    </details>
  </section>
</template>

<style scoped>
.servo-music{display:grid;gap:.75rem}.servo-music p,.servo-music h3{margin:0}.aw-field{display:grid;gap:.5rem}.bench-actions{display:grid;grid-template-columns:1fr 1fr 1.5fr;gap:.5rem}progress{width:100%}fieldset{display:grid;gap:.75rem;min-width:0;border:1px solid var(--aw-border);border-radius:var(--aw-radius-control);padding:1rem}.music-channel{display:grid;grid-template-columns:minmax(6rem,1fr) minmax(0,2fr);gap:.75rem;align-items:center}
.music-overview{--music-row-height:max(40px,2.5rem);display:grid;grid-template-columns:minmax(6rem,8rem) minmax(0,1fr);gap:.5rem;align-items:start}.music-lane-labels{display:grid;grid-template-rows:repeat(7,var(--music-row-height))}.music-lane-label{display:flex;flex-direction:column;justify-content:center;min-width:0;font-size:.875rem;line-height:1.2}.music-lane-label small{font-size:.75rem;color:var(--aw-muted)}.music-waveform{display:block;width:100%;height:calc(7 * var(--music-row-height));border:1px solid var(--aw-border);border-radius:var(--aw-radius-control);background:var(--aw-surface)}.music-wave{fill:var(--aw-muted);opacity:.65}.music-motion{fill:none;stroke:var(--aw-accent);stroke-width:1.5;vector-effect:non-scaling-stroke}.music-baseline{stroke:var(--aw-border);stroke-width:1;vector-effect:non-scaling-stroke}.music-lane-off .music-motion{stroke:var(--aw-muted);stroke-dasharray:4 3}.music-cursor-outline{stroke:var(--aw-surface);stroke-width:5;vector-effect:non-scaling-stroke}.music-cursor{stroke:var(--aw-danger);stroke-width:2;vector-effect:non-scaling-stroke}.music-cursor-tip{fill:var(--aw-danger)}summary{min-height:44px;cursor:pointer}
@media(max-width:480px){.music-overview{grid-template-columns:6.25rem minmax(0,1fr)}.bench-actions{font-size:.875rem}.bench-actions :deep(button){padding-inline:.5rem}}
</style>

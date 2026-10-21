<script setup lang="ts">
import { onBeforeUnmount, onDeactivated, onMounted, ref, shallowRef } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { decodeMusic } from '../../../../firmware/diagnostics/servo-bench/web/music.mjs';
import { servoBench as bench } from './servoBench';

const props = defineProps<{ channel: number; prepared: boolean }>();
const audio = ref<HTMLAudioElement>();
let music: ReturnType<typeof bench.createMusic> | null = null;
const state = shallowRef({ active: false, time: 0, message: 'Vyberte místní skladbu.', width: null as number | null });
const loading = ref(false);
const duration = ref(0);
const summary = ref('Zvuk zůstává v prohlížeči. Nic se nenahrává na server.');
const dance = ref(false);
const consent = ref(false);
const strength = ref(40);
let url: string | null = null;
let selection = 0;
onMounted(() => { music = bench.createMusic(audio.value!, () => { if (music) state.value = { ...music.state }; }); });
function leave(): void { ++selection; loading.value = false; consent.value = false; music?.stop(); }
onDeactivated(leave);
onBeforeUnmount(() => { leave(); bench.releaseMusic(); if (url) URL.revokeObjectURL(url); });
async function load(event: Event): Promise<void> {
  const file = (event.target as HTMLInputElement).files?.[0];
  const current = ++selection;
  music?.stop(); if (music) music.analysis = null;
  duration.value = 0;
  if (url) URL.revokeObjectURL(url);
  url = null; audio.value!.removeAttribute('src'); audio.value!.load();
  if (!file) return;
  loading.value = true; summary.value = 'Analyzuji energii a odhad tempa…';
  try {
    const analysis = await decodeMusic(file);
    if (selection !== current || !music) return;
    music.analysis = analysis; duration.value = analysis.duration;
    url = URL.createObjectURL(file); audio.value!.src = url; audio.value!.load();
    summary.value = `${file.name} · ${analysis.duration.toFixed(1)} s · ${analysis.bpm ? `odhad ${Math.round(analysis.bpm)} BPM` : 'bez zřetelného rytmu'}`;
    music.state.message = 'Připraveno. Play spustí zvuk a volitelně tanec vybraného kanálu.';
    state.value = { ...music.state };
  } catch (error) { if (selection === current) summary.value = error instanceof Error ? error.message : 'Zvuk nelze načíst.'; }
  finally { if (selection === current) loading.value = false; }
}
function play(): void {
  const permitted = props.prepared && consent.value;
  consent.value = false;
  void music?.play({ dance: dance.value, consent: permitted, channel: props.channel, strength: strength.value });
}
function stop(): void { ++selection; loading.value = false; music?.stop(); }
</script>

<template>
  <section aria-labelledby="servo-music-title" class="servo-music">
    <h3 id="servo-music-title">Hudba a tanec serva</h3>
    <p>Meze 600–2300 µs jsou fyzicky potvrzené pro všech šest serv (21. 10. 2026). Tanec ovládá pouze vybraný kanál s volným servem, podle rytmu a energie skladby. Poloha a zatížení nejsou měřené.</p>
    <label class="aw-field">Místní skladba · do 32 MiB a 10 minut<input class="aw-input" type="file" accept="audio/*" :disabled="loading || state.active" @change="load"></label>
    <p>{{ summary }}</p><audio ref="audio" preload="auto"></audio>
    <label><input v-model="dance" type="checkbox" :disabled="state.active"> Servo tančí se zvukem · kanál {{ channel }}</label>
    <label><input v-model="consent" type="checkbox" :disabled="state.active || !prepared"> Povoluji posloupnost pulzů pro toto Play a zůstávám u vypínače.</label>
    <label class="aw-field">Rozsah tance · % potvrzeného rozsahu<input v-model.number="strength" class="aw-input" type="number" min="1" max="100" :disabled="state.active"></label>
    <div class="bench-actions"><AwButton variant="primary" :disabled="loading || !duration || state.active" @click="play">Play</AwButton><AwButton :disabled="!state.active" @click="music?.pause()">Pause</AwButton><AwButton variant="danger" :disabled="!duration && !loading" @click="stop">Stop hudby a tance</AwButton></div>
    <progress :max="duration || 1" :value="state.time" aria-label="Průběh skladby"></progress>
    <p>{{ state.time.toFixed(1) }} / {{ duration.toFixed(1) }} s<span v-if="state.width"> · {{ state.width }} µs</span></p>
    <p role="status">{{ state.message }}</p>
    <p>Pauza ponechá čas, Stop vrátí skladbu na začátek; obě akce vyžádají PWM Stop. Pulzy trvají 500 ms, zpožděné kroky se přeskočí. Po skrytí stránky ani chybě se tanec sám neobnoví.</p>
  </section>
</template>

<style scoped>
.servo-music{display:grid;gap:.75rem}.aw-field{display:grid;gap:.5rem}.bench-actions{display:flex;flex-wrap:wrap;gap:.75rem}progress{width:100%}
</style>

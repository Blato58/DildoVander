<script setup lang="ts">
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { servoBench as bench } from './servoBench';
const state = bench.state;
const capabilities = bench.capabilities;
const automatic = bench.automatic;
const channel = ref(0);
const pulseWidth = ref(1500);
const calibrationWidth = ref(1500);
const duration = ref(1500);
const label = ref('');
const physicallyPrepared = ref(false);
const automatedConsent = ref(false);
const toolConsent = ref(false);
const exported = ref('');
const noiseWarning = ref(false);
const channels = [0, 1, 2, 3, 4, 5];
const profile = computed(() => bench.profiles.value[channel.value]);
const contextLocked = computed(() => automatic.value?.active || !!state.value.pending || state.value.writing || !!state.value.armedUntil || bench.closing.value);
const calibrationValid = computed(() => Number.isInteger(calibrationWidth.value) && calibrationWidth.value >= 1000 && calibrationWidth.value <= 2000 && Number.isInteger(duration.value) && duration.value >= 500 && duration.value <= 3000);
watch(channel, () => { bench.changeContext(); label.value = profile.value?.label ?? ''; physicallyPrepared.value = false; automatedConsent.value = false; });
watch([calibrationWidth, duration], () => bench.changeContext());
watch(physicallyPrepared, (value, previous) => { if (previous && !value) { bench.disableTools(); if (bench.connected.value) void bench.stop(); } });
function show(): void { bench.show(true); label.value = profile.value?.label ?? ''; }
function hide(): void { physicallyPrepared.value = false; automatedConsent.value = false; toolConsent.value = false; bench.show(false); }
onMounted(show); onActivated(show); onDeactivated(hide); onBeforeUnmount(hide);
function startAutomatic(direction: 'min' | 'max'): void {
  if (!physicallyPrepared.value || !automatedConsent.value) return;
  automatedConsent.value = false; noiseWarning.value = false; bench.startAutomatic(channel.value, direction);
}
function noise(): void { noiseWarning.value = true; bench.noise(); }
function exportProfiles(): void { exported.value = bench.exportProfiles(); }
async function enableTools(): Promise<void> {
  if (!physicallyPrepared.value || !toolConsent.value) return;
  toolConsent.value = false; await bench.enableTools();
}
</script>

<template>
  <section class="aw-card servo-bench" aria-labelledby="servo-bench-title">
    <header>
      <p class="eyebrow">Místní USB servis</p>
      <h2 id="servo-bench-title">Stolní test a ruční profil serva</h2>
      <p>Skutečný USB nástroj pro diagnostický firmware servo-bench a jedno volné servo odpojené od mechanismu. Nepoužívejte s provozním firmwarem ani osazenou přihrádkou.</p>
    </header>
    <AwNotice tone="warning" :live="false" title="Napájení a poloha nejsou měřené">
      Před připojením ověřte 5 V, polaritu a zapojení PCA9685. Diagnostický firmware neovládá OE; Stop vypíná PWM a neodpojuje 5 V. Pro okamžité odpojení použijte zelený vypínač pohonů. První pulz může znamenat velký pohyb z neznámé polohy.
    </AwNotice>
    <p v-if="!bench.supported" class="aw-notice">Web Serial není dostupné. USB vyžaduje desktopový Chrome nebo Edge v zabezpečeném kontextu (HTTPS nebo localhost), vybraný CH343 a fyzicky připojený počítač. iPhone tento servisní transport nepodporuje.</p>
    <p>USB spojuje tento prohlížeč přímo s místním portem při 115200 baud. Žádný USB port ani povel se nevystavuje po síti. Přepnutí obrazovky USB odpojí po pokusu o Stop.</p>
    <div class="bench-actions">
      <AwButton variant="primary" :busy="bench.connecting.value" :disabled="!bench.supported || state.connected || bench.closing.value" @click="bench.connect()">Připojit USB</AwButton>
      <AwButton :disabled="!state.connected || bench.closing.value" @click="bench.disconnect">Odpojit USB</AwButton>
      <AwButton variant="danger" :disabled="!capabilities.stop || bench.closing.value" @click="bench.stop">Stop USB · PWM FULL_OFF</AwButton>
      <AwButton variant="danger" :disabled="!capabilities.stop || bench.closing.value" @click="noise">Drhne / vrčí · Stop</AwButton>
    </div>
    <AwNotice v-if="noiseWarning" tone="danger" :live="false">Vypněte napájení V+ zelenou kolébkou a zkontrolujte volné servo. Potvrzení firmwaru neměří skutečný pohyb, proud ani polohu.</AwNotice>
    <p role="status">{{ state.message }}</p>
    <p v-if="state.error" role="alert">{{ state.error }}</p>
    <dl class="bench-metrics">
      <div><dt>USB</dt><dd>{{ state.connected ? 'Otevřené · CH343' : 'Odpojené' }}</dd></div>
      <div><dt>Výchozí Stop</dt><dd>{{ state.baseline ? 'Potvrzeno PWM FULL_OFF' : 'Nepotvrzený' }}</dd></div>
      <div><dt>Kalibrační protokol</dt><dd>{{ state.calibrationSupported ? 'calibration=v1 potvrzen' : 'Nepotvrzený / nedostupný' }}</dd></div>
      <div><dt>Napájení / poloha</dt><dd>Neznámé / neměřená</dd></div>
    </dl>
    <label class="aw-field">Kanál PCA9685
      <select v-model="channel" class="aw-input" :disabled="contextLocked"><option v-for="number in channels" :key="number" :value="number">Kanál {{ number }} · přihrádka {{ number + 1 }}</option></select>
    </label>
    <label><input v-model="physicallyPrepared" type="checkbox" :disabled="!!automatic?.active"> Potvrzuji jedno volné servo, bezpečný prostor a dostupný vypínač 5 V pro tento kanál.</label>
    <fieldset :disabled="!physicallyPrepared || !!automatic?.active || bench.closing.value">
      <legend>Jeden zkušební pulz · 500 ms</legend>
      <label class="aw-field">PWM<select v-model="pulseWidth" class="aw-input" :disabled="contextLocked"><option :value="1400">1400 µs</option><option :value="1500">1500 µs</option><option :value="1600">1600 µs</option></select></label>
      <div class="bench-actions"><AwButton :disabled="!capabilities.arm" @click="bench.arm">Arm · jeden pulz</AwButton><AwButton variant="primary" :disabled="!capabilities.pulse" @click="bench.pulse(channel, pulseWidth)">Provést zkušební pulz</AwButton></div>
    </fieldset>
    <fieldset :disabled="!physicallyPrepared || !!automatic?.active || bench.closing.value">
      <legend>Ruční kalibrační pulz</legend>
      <div class="bench-fields">
        <label class="aw-field">PWM v µs<input v-model.number="calibrationWidth" class="aw-input" type="number" min="1000" max="2000" step="1" :disabled="contextLocked"></label>
        <label class="aw-field">Doba v ms<input v-model.number="duration" class="aw-input" type="number" min="500" max="3000" step="100" :disabled="contextLocked"></label>
      </div>
      <input v-model.number="calibrationWidth" type="range" min="1000" max="2000" step="5" aria-label="PWM kalibrace v mikrosekundách" :disabled="contextLocked">
      <div class="bench-actions"><AwButton v-for="width in [1200, 1500, 1800]" :key="width" :disabled="contextLocked" @click="calibrationWidth = width">{{ width }} µs</AwButton><AwButton :disabled="contextLocked" @click="calibrationWidth = Math.max(1000, calibrationWidth - 10)">−10 µs</AwButton><AwButton :disabled="contextLocked" @click="calibrationWidth = Math.min(2000, calibrationWidth + 10)">+10 µs</AwButton></div>
      <p v-if="!calibrationValid" role="alert">Zadejte celé PWM 1000–2000 µs a dobu 500–3000 ms.</p>
      <div class="bench-actions"><AwButton :disabled="!capabilities.arm || !state.calibrationSupported || !calibrationValid" @click="bench.arm">Arm · kalibrační pulz</AwButton><AwButton variant="primary" :disabled="!capabilities.calibrate || !calibrationValid" @click="bench.calibrate(channel, calibrationWidth, duration)">Provést kalibrační pulz</AwButton></div>
    </fieldset>
    <section aria-labelledby="bench-profile-title">
      <h3 id="bench-profile-title">Ruční profil · kanál {{ channel }}</h3>
      <p>Profil se ukládá jen do tohoto prohlížeče a originu. Není to provozní kalibrace ani měřená poloha; do NVS kufru se nezapisuje.</p>
      <label class="aw-field">Označení serva<input v-model="label" class="aw-input" maxlength="80" :disabled="!!automatic?.active"></label>
      <AwButton :disabled="!!automatic?.active" @click="bench.saveLabel(channel, label)">Uložit označení</AwButton>
      <dl class="bench-metrics"><div><dt>Minimum</dt><dd>{{ profile?.min ?? 'Neuložené' }} µs</dd></div><div><dt>Střed</dt><dd>{{ profile?.center ?? 'Neuložený' }} µs</dd></div><div><dt>Maximum</dt><dd>{{ profile?.max ?? 'Neuložené' }} µs</dd></div></dl>
      <div class="bench-actions"><AwButton v-for="point in (['min', 'center', 'max'] as const)" :key="point" :disabled="!!automatic?.active || !bench.canSavePoint(channel, calibrationWidth)" @click="bench.savePoint(channel, point, calibrationWidth)">Uložit {{ point === 'min' ? 'minimum' : point === 'max' ? 'maximum' : 'střed' }}</AwButton><AwButton :disabled="!!automatic?.active" @click="bench.clearProfile(channel)">Vymazat profil kanálu</AwButton><AwButton @click="exportProfiles">Exportovat ruční profily</AwButton></div>
      <p v-if="bench.profileError.value" role="alert">{{ bench.profileError.value }}</p>
      <label v-if="exported" class="aw-field">Export profilů<textarea class="aw-input" :value="exported" rows="8" readonly /></label>
    </section>
    <section aria-labelledby="bench-auto-title">
      <h3 id="bench-auto-title">Asistované hledání meze PWM</h3>
      <p>Vyžaduje uložený střed tohoto kanálu. Každý potvrzený krok posune PWM o 25 µs, pulz trvá 1500 ms a mezi kroky je 3 s pauza. Obsluha celou dobu sleduje volné servo a při hluku ihned použije Stop. Návrh vyžaduje samostatné uložení.</p>
      <label><input v-model="automatedConsent" type="checkbox" :disabled="!!automatic?.active || !physicallyPrepared"> Výslovně povoluji tuto posloupnost pulzů a zůstávám u vypínače.</label>
      <div class="bench-actions"><AwButton :disabled="!automatedConsent || !physicallyPrepared || profile?.center == null || !bench.canStartAutomatic.value" @click="startAutomatic('min')">Hledat minimum</AwButton><AwButton :disabled="!automatedConsent || !physicallyPrepared || profile?.center == null || !bench.canStartAutomatic.value" @click="startAutomatic('max')">Hledat maximum</AwButton><AwButton :disabled="!bench.canSaveCandidate(channel)" @click="bench.saveCandidate(channel)">Uložit potvrzený návrh</AwButton></div>
      <p role="status">{{ automatic?.message || 'Asistovaná kalibrace není spuštěná.' }}</p>
      <p v-if="automatic?.candidateUs != null">Návrh: {{ automatic.candidateUs }} µs · Stop {{ automatic.stopConfirmed ? 'potvrzený firmwarem' : 'nepotvrzený' }} · poloha neměřená.</p>
    </section>
    <details><summary>PCA9685 readback a technický log</summary><p>Registry jsou čtení PCA9685, ne snímač skutečné polohy serva. Po odpojení je log historický.</p>
      <dl v-if="state.readback" class="bench-metrics"><div v-for="(value, key) in state.readback" :key="key"><dt>{{ key }}</dt><dd>{{ value }}</dd></div></dl><p v-else>Žádný potvrzený readback.</p><pre class="bench-log">{{ bench.log.value.join('\n') || 'Žádné technické události.' }}</pre>
    </details>
    <details><summary>Místní nástroje WebMCP</summary><p>Volitelné nástroje sdílejí stejné firmware potvrzení, Arm a Stop. Připojení nástrojem smí použít právě jeden již povolený port CH343; oprávnění k portu vyberte ručně.</p><label><input v-model="toolConsent" type="checkbox" :disabled="!physicallyPrepared || bench.toolsEnabled.value"> Povoluji místní automatizaci USB po dobu této servisní obrazovky.</label><div class="bench-actions"><AwButton :disabled="!bench.supported || !physicallyPrepared || !toolConsent || bench.toolsEnabled.value" @click="enableTools">Zapnout místní nástroje</AwButton><AwButton :disabled="!bench.toolsEnabled.value" @click="bench.disableTools">Vypnout místní nástroje</AwButton></div><p role="status">{{ bench.toolMessage.value }}</p></details>
  </section>
</template>

<style scoped>
.servo-bench{display:grid;gap:1rem}.bench-actions{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.bench-metrics,.bench-fields{display:grid;grid-template-columns:repeat(auto-fit,minmax(min(100%,12rem),1fr));gap:1rem}.bench-metrics dd{margin:0;overflow-wrap:anywhere}.aw-field{display:grid;gap:.5rem;margin-block:.75rem}fieldset{border:1px solid var(--aw-border);border-radius:.75rem;padding:1rem;min-width:0}input[type=range],textarea{width:100%;box-sizing:border-box}.bench-log{max-height:20rem;overflow:auto;white-space:pre-wrap;overflow-wrap:anywhere;font-family:var(--aw-font-mono)}summary{cursor:pointer;font-weight:600}
</style>

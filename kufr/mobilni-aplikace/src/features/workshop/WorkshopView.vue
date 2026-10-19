<script setup lang="ts">
import { computed, markRaw, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowRef, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwStatus from '../../components/AwStatus.vue';
import SimulationControls from './SimulationControls.vue';
import MediaPanel from './MediaPanel.vue';
import EffectPreview from './EffectPreview.vue';
import ModelLabels from './ModelLabels.vue';
import ModelDownloads from './ModelDownloads.vue';
import type { ViewportOverlay } from './workshop-types';
import { account, accountRequest } from '../identity/session';
import { usePreferences } from '../../application/preferences';
import { createViewerEngine, defaultSceneState, matchesPart } from './engine.mjs';
import { loadBundle, loadPublishedBundle, loadFileBundle } from './model-bundle.mjs';
import { projectObservation, twin, timeline, validateObserverBase } from './observation.mjs';
import { publicResourceClient } from './public-resources';

const props = withDefaults(defineProps<{ bundle?: any; offline?: boolean }>(), { offline: false });
const preferences = props.offline ? usePreferences() : null;
const canvas = ref<HTMLCanvasElement>();
const overlay = shallowRef<ViewportOverlay | null>(null), hovered = ref<string | null>(null);
const mediaPanel = ref<InstanceType<typeof MediaPanel>>();
const bundle = shallowRef<any>(null), engine = shallowRef<any>(null);
const localBundle = shallowRef<any>(null);
const loading = ref(false), error = ref(''), storageNotice = ref('');
const state = reactive<any>(defaultSceneState());
const cameraName = ref('Izometrie'), search = ref(''), inspector = ref<'parts' | 'assembly' | 'runtime' | 'timeline'>('parts');
const previewRunning = ref(false);
let loadGeneration = 0, loadAbort: AbortController | null = null;
const groups = computed<string[]>(() => [...new Set<string>((bundle.value?.model.meshes || []).map((mesh: any) => mesh.group))]);
const parts = computed<any[]>(() => (bundle.value?.model.meshes || []).filter((mesh: any) => `${mesh.name} ${mesh.info?.title || ''}`.toLocaleLowerCase().includes(search.value.toLocaleLowerCase())));
const selected = computed<any>(() => bundle.value?.model.meshes.find((mesh: any) => mesh.id === (state.selected || hovered.value)));
const selectedRoute = computed<any>(() => bundle.value?.model.routes.find((route: any) => route.id === state.selectedWire));
const steps = computed<any[]>(() => bundle.value?.model.assemblyGuide.steps || []);
const currentStep = computed<any>(() => steps.value[state.assemblyIndex]);
const fixture = computed(() => bundle.value?.manifest.provenance.kind === 'synthetic-fixture');
const colorStorageKey = computed(() => `dildovander.model-colors.v1.${bundle.value?.manifest.modelId}`);
const selectedColor = computed(() => state.colors[state.selected] || '#' + (selected.value?.color || [0, 0, 0]).map((value: number) => Math.round(value * 255).toString(16).padStart(2, '0')).join(''));

async function loadModel() {
  const generation = ++loadGeneration;
  loadAbort?.abort(); loadAbort = new AbortController();
  disconnect(); engine.value?.dispose(); engine.value = null; bundle.value = null;
  Object.assign(state, defaultSceneState()); loading.value = true; error.value = '';
  try {
    let result;
    if (localBundle.value) result = localBundle.value;
    else if (props.bundle) result = props.bundle;
    else if (import.meta.env.DEV && new URLSearchParams(location.search).get('fixture') === '1') {
      const manifest = (await import('./fixture/model-bundle.json')).default;
      const data = await import('./fixture/model.json?raw');
      result = await loadBundle(manifest, async () => new TextEncoder().encode(data.default), { allowFixture: true });
    } else {
      const resources = await publicResourceClient(loadAbort.signal);
      result = await loadPublishedBundle(new URL('/models/', resources.origin).href, { signal: loadAbort.signal, readBytes: resources.read });
    }
    if (generation !== loadGeneration) return;
    bundle.value = markRaw(result);
    try {
      const colors = JSON.parse(localStorage.getItem(colorStorageKey.value) || (result.manifest.modelId === 'dildovander-f2' ? localStorage.getItem('dildovander.f2.object-colors.v1') : null) || '{}');
      state.colors = Object.fromEntries(result.model.meshes.filter((mesh: any) => /^#[a-f0-9]{6}$/i.test(colors[mesh.id])).map((mesh: any) => [mesh.id, colors[mesh.id]]));
    } catch { storageNotice.value = 'Barvy platí pro tuto relaci; místní úložiště není dostupné.'; }
    await nextTick();
    if (generation !== loadGeneration || !canvas.value) return;
    engine.value = markRaw(createViewerEngine(canvas.value, result.model, {
      onSelect: selectPart,
      onHover: (id: string | null) => { hovered.value = id; },
      onOverlay: (value: ViewportOverlay) => { overlay.value = markRaw(value); },
      onCamera: (value: any) => { cameraName.value = value.label; },
      onError: (message: string) => { error.value = message; },
    }));
    engine.value.update(sceneState());
  } catch (cause) { if (generation === loadGeneration && !loadAbort.signal.aborted) error.value = message(cause); }
  finally { if (generation === loadGeneration) loading.value = false; }
}
function message(cause: unknown) { return cause instanceof Error ? cause.message : String(cause); }
async function importModel(event: Event) {
  const files = Array.from((event.target as HTMLInputElement).files || []); if (!files.length) return;
  const generation = loadGeneration;
  try {
    const value = await loadFileBundle(files);
    if (generation !== loadGeneration) return;
    localBundle.value = markRaw(value); await loadModel();
  } catch (cause) { if (generation === loadGeneration) error.value = message(cause); }
  finally { (event.target as HTMLInputElement).value = ''; }
}
function sceneState() { return { ...state, hidden: [...state.hidden], hiddenGroups: [...state.hiddenGroups], colors: { ...state.colors }, angles: [...state.angles], ledColors: { ...state.ledColors }, uncertain: [...state.uncertain] }; }
watch(state, () => engine.value?.update(sceneState()), { deep: true });
watch(() => state.mode, value => {
  state.ledColors = {}; state.outputs = {}; state.selected = null; hovered.value = null;
  state.selectedWire = ''; state.selectedEndpoint = ''; state.lid = value === 'transport' ? 0 : 105;
  state.wires = value === 'service'; state.dimensions = false;
  if (!connected.value && !isReplay.value && ['transport', 'assembly'].includes(value)) state.angles = Array(6).fill(0);
  restoreVisibility();
}, { flush: 'sync' });
function setColor(event: Event) {
  if (!state.selected && selected.value) state.selected = selected.value.id;
  state.colors = { ...state.colors, [state.selected]: (event.target as HTMLInputElement).value };
  try { localStorage.setItem(colorStorageKey.value, JSON.stringify(state.colors)); storageNotice.value = 'Barvy jsou uloženy v tomto prohlížeči.'; }
  catch { storageNotice.value = 'Barvy platí pro tuto relaci; místní úložiště není dostupné.'; }
}
function toggle(values: string[], value: string) { return values.includes(value) ? values.filter(item => item !== value) : [...values, value]; }
function selectPart(id: string | null) { state.selected = id; state.selectedWire = ''; state.selectedEndpoint = ''; inspector.value = 'parts'; }
function selectWire(id: string, endpoint = '') { state.selected = null; hovered.value = null; state.selectedWire = id; state.selectedEndpoint = endpoint; inspector.value = 'parts'; }
function restoreVisibility() {
  state.hidden = []; state.hiddenGroups = []; state.isolate = null; state.visualMode = 'complete';
  Object.assign(state, { case: true, cover: state.mode !== 'service', beds: true, hardware: true, wands: !['transport','service'].includes(state.mode), bank: state.mode !== 'transport', textile: !['transport','service'].includes(state.mode), opacity: state.mode === 'service' ? .2 : 1 });
  engine.value?.camera('overview');
}
function camera(preset: string, label: string) { cameraName.value = label; engine.value?.camera(preset); }
function keyCamera(event: KeyboardEvent) {
  const shifts: Record<string, [number, number]> = { ArrowLeft: [.1, 0], ArrowRight: [-.1, 0], ArrowUp: [0, .1], ArrowDown: [0, -.1] };
  if (shifts[event.key]) { event.preventDefault(); event.shiftKey ? engine.value?.pan(...shifts[event.key]) : engine.value?.orbit(...shifts[event.key]); }
  else if (['+', '=', '-'].includes(event.key)) { event.preventDefault(); engine.value?.zoom(event.key === '-' ? 1.15 : .85); }
  else if (event.key === 'Home') { event.preventDefault(); engine.value?.camera('overview'); }
}
function assemblyStep(index: number) { state.assemblyIndex = Math.max(0, Math.min(steps.value.length - 1, index)); state.mode = 'assembly'; const view = currentStep.value?.camera; camera(['top', 'front', 'side'].includes(view) ? view : 'iso', currentStep.value?.direction || 'Montážní pohled'); }
function focusAssembly() {
  const step = currentStep.value; if (!step) return;
  const patterns = [...(step.benchParts ?? step.caseParts ?? step.parts ?? []), ...(step.preparedParts || []), ...(step.installation?.targets || [])];
  engine.value?.focus(bundle.value.model.meshes.filter((mesh: any) => patterns.some(pattern => matchesPart(mesh.name, pattern))).map((mesh: any) => mesh.id));
}

// Observation is GET-only. Selecting a route/model never connects or starts work.
const sourceMode = ref('simulation'), observerBase = ref(''), sources = ref<any[]>([]), sourceId = ref('');
const wantedLiveSource = ref(''), sourcesListed = ref(false);
const connecting = ref(false), connected = ref(false), runtimeError = ref(''), runtimeStatus = ref('Odpojeno');
const currentEnvelope = shallowRef<any>(null), receivedAt = ref(''), clock = ref(0), observation = shallowRef<any>(null);
const recording = shallowRef<any[]>([]), recordingDrops = ref(0), isReplay = ref(false), replayIndex = ref(0), playing = ref(false), speed = ref(1);
const recordingOrigin = ref<'server' | 'local' | null>(null);
let observerAbort: AbortController | null = null, observerGeneration = 0, observerTimer: ReturnType<typeof setTimeout> | undefined;
let projection: any = null, freshnessTimer: ReturnType<typeof setInterval> | undefined, replayTimer: ReturnType<typeof setInterval> | undefined;
let acceptedAt = 0, recordingBytes = 0;
const telemetryEvents = computed<any[]>(() => currentEnvelope.value?.snapshot.trace?.events || []);
const chosenSource = computed<any>(() => sources.value.find(source => source.instanceId === sourceId.value));
const mediaContext = computed(() => ({
  active: connected.value, replay: isReplay.value, role: chosenSource.value?.canControl ? 'control' : 'observer',
  instanceId: sourceId.value, mode: sourceMode.value, profile: chosenSource.value?.profile || 'unknown',
  bootId: currentEnvelope.value?.snapshot.source.bootId || '', status: observation.value?.status || 'waiting',
}));
function resetObservation() { currentEnvelope.value = null; observation.value = null; receivedAt.value = ''; state.angles = Array(6).fill(0); state.uncertain = []; state.ledColors = {}; state.outputs = {}; }
function stopReplay() { playing.value = false; if (replayTimer) clearInterval(replayTimer); replayTimer = undefined; }
function disconnect(options: { keepSources?: boolean; preserveLocalReplay?: boolean } = {}) {
  const localReplay = options.preserveLocalReplay && recordingOrigin.value === 'local' && isReplay.value;
  void mediaPanel.value?.invalidate('Zdroj se mění; lokální média zastavena.');
  ++observerGeneration; observerAbort?.abort(); observerAbort = null;
  if (observerTimer) clearTimeout(observerTimer); observerTimer = undefined;
  if (freshnessTimer) clearInterval(freshnessTimer); freshnessTimer = undefined;
  connected.value = false; connecting.value = false; stopReplay();
  projection = null;
  if (!options.keepSources) { sources.value = []; sourceId.value = ''; sourcesListed.value = false; }
  if (!localReplay) { recording.value = []; recordingBytes = 0; recordingDrops.value = 0; recordingOrigin.value = null; replayIndex.value = 0; isReplay.value = false; resetObservation(); }
  runtimeStatus.value = 'Odpojeno';
}
function clearAuthorizedObservation(reason: string) { disconnect({ preserveLocalReplay: true }); runtimeError.value = reason; }
const authorizationFailure = (cause: unknown) => [401, 403].includes((cause as { status?: number })?.status ?? 0);
watch(() => account.value?.id, () => { if (!props.offline) clearAuthorizedObservation('Účet se změnil nebo přístup skončil. Načtěte dostupné zdroje znovu.'); }, { flush: 'sync' });
function changeSource() { disconnect(); sourceId.value = ''; sources.value = []; sourcesListed.value = false; runtimeError.value = ''; }
async function getObserver(path: string, signal: AbortSignal) {
  if (!observerBase.value) return accountRequest<any>(path, 'GET', undefined, signal);
  const base = validateObserverBase(observerBase.value || location.origin);
  const response = await fetch(base + path, { method: 'GET', credentials: 'include', cache: 'no-store', signal, redirect: 'error' });
  if (!response.ok) throw await twin.httpFailure(response, path);
  const text = await response.text();
  if (new TextEncoder().encode(text).length > twin.MAX_BYTES) throw new Error('Snapshot překročil 8 MiB.');
  return JSON.parse(text);
}
async function listSources() {
  disconnect(); runtimeError.value = ''; const generation = observerGeneration;
  observerAbort = new AbortController(); connecting.value = true;
  try {
    const result = await getObserver('/v1/twin/instances', observerAbort.signal);
    if (generation !== observerGeneration) return;
    sources.value = (result.instances || []).filter((item: any) => item.mode === sourceMode.value);
    sourcesListed.value = true;
    sourceId.value = sourceMode.value === 'live' && sources.value.some(source => source.instanceId === wantedLiveSource.value) ? wantedLiveSource.value : '';
    runtimeStatus.value = sourceId.value ? 'Požadovaný živý zdroj je vybrán. Pozorování spustíte tlačítkem Pozorovat.' : sources.value.length ? 'Vyberte konkrétní zdroj.' : 'Žádný dostupný zdroj v tomto režimu.';
  } catch (cause) { if (generation === observerGeneration) { if (authorizationFailure(cause)) clearAuthorizedObservation(message(cause)); else runtimeError.value = message(cause); } }
  finally { if (generation === observerGeneration) connecting.value = false; }
}
function drawObservation() {
  if (!currentEnvelope.value || !bundle.value) return;
  observation.value = projectObservation(bundle.value.model, currentEnvelope.value, performance.now(), acceptedAt, isReplay.value);
  state.angles = observation.value.angles.map((angle: number | null) => angle ?? 0);
  state.uncertain = observation.value.uncertain; state.ledColors = observation.value.ledColors; state.outputs = observation.value.outputs;
  runtimeStatus.value = observation.value.status;
}
async function connectObserver() {
  const selectedId = sourceId.value; disconnect({ keepSources: true }); sourceId.value = selectedId;
  if (!selectedId || !bundle.value || props.offline) return;
  runtimeError.value = ''; observerAbort = new AbortController(); const generation = observerGeneration;
  const signal = observerAbort.signal; connecting.value = true;
  projection = new twin.Projection(bundle.value.model.twinManifest); projection.select(selectedId);
  recording.value = []; recordingBytes = 0; recordingDrops.value = 0; recordingOrigin.value = 'server';
  const poll = async () => {
    try {
      const value = await getObserver('/v1/twin/instances/' + encodeURIComponent(selectedId), signal);
      if (generation !== observerGeneration) return;
      if (value.snapshot?.source.mode !== sourceMode.value || !projection.accept(value, performance.now())) throw new Error('Odmítnut cizí nebo starší snapshot.');
      connected.value = true; connecting.value = false; acceptedAt = performance.now(); currentEnvelope.value = markRaw(value); receivedAt.value = value.receivedAtUtc;
      const size = new TextEncoder().encode(JSON.stringify(value)).length;
      if (recording.value.at(-1)?.snapshot.sequence !== value.snapshot.sequence || recording.value.at(-1)?.snapshot.source.bootId !== value.snapshot.source.bootId) {
        if (recording.value.length < twin.MAX_SNAPSHOTS && recordingBytes + size < twin.MAX_BYTES - 1024) { recording.value = [...recording.value, value]; recordingBytes += size; } else recordingDrops.value++;
      }
      drawObservation();
    } catch (cause) {
      if (generation !== observerGeneration) return;
      if (authorizationFailure(cause)) { clearAuthorizedObservation(message(cause)); return; }
      runtimeError.value = message(cause); runtimeStatus.value = 'Spojení nedostupné; poslední hodnoty jsou historické.'; connecting.value = false;
    }
    if (generation === observerGeneration) observerTimer = setTimeout(poll, 750);
  };
  freshnessTimer = setInterval(() => { clock.value++; drawObservation(); }, 500);
  await poll();
}
function exportRecording() {
  const value = { schemaVersion: 1, profile: 'observer', cadManifestRevision: bundle.value.model.twinManifest?.revision || null, snapshots: recording.value };
  const url = URL.createObjectURL(new Blob([JSON.stringify(value)], { type: 'application/json' }));
  const link = document.createElement('a'); link.href = url; link.download = 'dildovander-observation.json'; link.click(); setTimeout(() => URL.revokeObjectURL(url), 0);
}
async function importRecording(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return;
  disconnect(); runtimeError.value = ''; const generation = observerGeneration;
  try {
    if (file.size > twin.MAX_BYTES) throw new Error('Záznam překročil 8 MiB.');
    const value = twin.parseRecording(await file.text());
    if (generation !== observerGeneration) return;
    recording.value = markRaw(value.snapshots); recordingOrigin.value = 'local'; isReplay.value = true; seek(0);
  } catch (cause) { if (generation === observerGeneration) runtimeError.value = message(cause); }
  finally { (event.target as HTMLInputElement).value = ''; }
}
function seek(index: number) {
  stopReplay(); if (!isReplay.value || !recording.value[index]) return;
  replayIndex.value = index; currentEnvelope.value = recording.value[index]; receivedAt.value = currentEnvelope.value.receivedAtUtc; acceptedAt = performance.now(); drawObservation();
}
function playReplay() {
  if (playing.value) { stopReplay(); return; }
  if (replayIndex.value >= recording.value.length - 1) seek(0);
  const origin = Date.parse(recording.value[replayIndex.value].receivedAtUtc), start = performance.now(); playing.value = true;
  replayTimer = setInterval(() => {
    const time = origin + (performance.now() - start) * speed.value;
    while (replayIndex.value < recording.value.length - 1 && Date.parse(recording.value[replayIndex.value + 1].receivedAtUtc) <= time) replayIndex.value++;
    currentEnvelope.value = recording.value[replayIndex.value]; acceptedAt = performance.now(); receivedAt.value = currentEnvelope.value.receivedAtUtc; drawObservation();
    if (replayIndex.value === recording.value.length - 1) stopReplay();
  }, 50);
}

const trace = shallowRef<any>(null), comparison = shallowRef<any>(null), traceError = ref('');
const traceIndex = ref(0), tracePlaying = ref(false);
let traceTimer: ReturnType<typeof setInterval> | undefined;
const traceEvent = computed<any>(() => trace.value?.events[traceIndex.value]);
const traceMismatches = computed<string[]>(() => trace.value && bundle.value ? timeline.compatibility(trace.value, bundle.value.model.showManifest, bundle.value.model.showPlan) : []);
const traceDiff = computed<any>(() => trace.value && comparison.value ? timeline.diffTraces(trace.value, comparison.value) : null);
async function importTrace(event: Event, compare = false) {
  const file = (event.target as HTMLInputElement).files?.[0]; if (!file) return;
  const generation = loadGeneration;
  try {
    if (file.size > timeline.MAX_BYTES) throw new Error('Stopa překročila 1 MiB.');
    const value = timeline.parseTrace(await file.text()); if (generation !== loadGeneration) return;
    if (compare) comparison.value = markRaw(value);
    else { stopTraceReplay(); traceIndex.value = 0; trace.value = markRaw(value); }
    traceError.value = '';
  }
  catch (cause) { traceError.value = message(cause); }
  finally { (event.target as HTMLInputElement).value = ''; }
}
function stopTraceReplay() { tracePlaying.value = false; if (traceTimer) clearInterval(traceTimer); traceTimer = undefined; }
function seekTrace(event: Event) { stopTraceReplay(); traceIndex.value = Number((event.target as HTMLInputElement).value); }
function playTrace() {
  if (tracePlaying.value) { stopTraceReplay(); return; }
  if (!trace.value?.events.length) return;
  if (traceIndex.value >= trace.value.events.length - 1) traceIndex.value = 0;
  const start = performance.now(), origin = Number(traceEvent.value.timeMs);
  tracePlaying.value = true;
  traceTimer = setInterval(() => {
    const deadline = origin + performance.now() - start;
    while (traceIndex.value < trace.value.events.length - 1 && Number(trace.value.events[traceIndex.value + 1].timeMs) <= deadline) traceIndex.value++;
    if (traceIndex.value === trace.value.events.length - 1) stopTraceReplay();
  }, 50);
}
onMounted(() => {
  const query = new URLSearchParams(location.search), requested = query.get('mode');
  if (requested === 'live' || requested === 'simulation') { sourceMode.value = requested; inspector.value = 'runtime'; }
  const source = query.get('source');
  if (requested === 'live' && query.getAll('source').length === 1 && source && /^[A-Za-z0-9._-]{1,96}$/.test(source)) wantedLiveSource.value = source;
  void loadModel();
});
onBeforeUnmount(() => { ++loadGeneration; loadAbort?.abort(); disconnect(); stopTraceReplay(); engine.value?.dispose(); engine.value = null; });
</script>

<template>
  <section class="workshop" aria-labelledby="workshop-title">
    <header class="workshop-heading">
      <div>
        <h1 id="workshop-title">
          Workshop
        </h1>
        <p>
          Model, montáž a pozorování kufru
        </p>
      </div>
      <AwStatus>
        {{ offline ? 'Offline · pouze prohlížení' : 'Žádné fyzické povely' }}
      </AwStatus>
      <AwField v-if="preferences" id="offline-theme" label="Vzhled offline vieweru"><select id="offline-theme" v-model="preferences.theme" class="aw-input"><option value="dark">Tmavý</option><option value="light">Světlý</option><option value="system">Podle zařízení</option></select></AwField>
    </header>
    <AwNotice v-if="loading" title="Načítám vydání modelu">
      Kontroluji identitu, velikost a integritu dat.
    </AwNotice>
    <AwNotice v-if="error" tone="warning" title="Model není dostupný">
      <p>
        {{ error }}
      </p>
      <AwButton :busy="loading" @click="loadModel">
        Načíst znovu
      </AwButton>
      <p>
        Ostatní části aplikace zůstávají dostupné.
      </p>
    </AwNotice>
    <details v-if="!offline">
      <summary>Otevřít místní vydání modelu</summary>
      <AwField id="model-import" label="Složka ModelBundle (manifest a deklarovaná data)">
        <input id="model-import" type="file" multiple webkitdirectory @change="importModel" />
      </AwField>
      <p>Data zůstanou v této relaci; výběr modelu nezískává ovládání kufru ani nic nepublikuje.</p>
    </details>
    <template v-if="bundle">
      <AwNotice v-if="fixture" tone="warning" title="SYNTETICKÁ UI FIXTURE">
        Testovací blok není CAD model kufru, výrobní podklad ani fyzické ověření.
      </AwNotice>
      <div class="workshop-provenance">
        <span>
          {{ bundle.manifest.modelId }} · {{ bundle.manifest.revision }}
        </span>
        <span>
          CAD: {{ bundle.manifest.provenance.cadVerification }}
        </span>
        <span>
          Fyzické ověření: nedostupné
        </span>
        <details>
          <summary>
            Provenience modelu
          </summary>
          <dl>
            <dt>
              Model SHA-256
            </dt>
            <dd>
              {{ bundle.manifest.bundleHash }}
            </dd>
            <dt>
              Zdroj modelu
            </dt>
            <dd>
              {{ bundle.manifest.provenance.sourceRevision }}
            </dd>
            <dt>
              Souřadnice
            </dt>
            <dd>
              mm · pravotočivé · Z nahoru
            </dd>
          </dl>
        </details>
      </div>
      <ModelDownloads :bundle="bundle" :offline="offline" />
      <div class="workshop-layout">
        <section class="workshop-scene aw-card" aria-label="Pracovní plocha modelu">
          <div class="workshop-toolbar">
            <AwField id="model-mode" label="Zobrazení">
              <select id="model-mode" v-model="state.mode" class="aw-input" :disabled="connected || isReplay">
                <option value="operation">
                  Provozní model
                </option>
                <option value="transport">
                  Transport
                </option>
                <option value="service">Servisní náhled</option>
                <option value="assembly">
                  Montáž
                </option>
              </select>
            </AwField>
            <AwField id="visual-mode" label="Vrstvy">
              <select id="visual-mode" v-model="state.visualMode" class="aw-input">
                <option value="complete">
                  Úplný model
                </option>
                <option value="structure">
                  Konstrukce
                </option>
                <option value="electronics">
                  Elektronika
                </option>
                <option value="cutaway">
                  Řez
                </option>
              </select>
            </AwField>
            <AwButton @click="engine?.fit()">
              Celý model
            </AwButton>
            <AwButton @click="restoreVisibility">
              Obnovit viditelnost
            </AwButton>
          </div>
          <div class="workshop-viewport">
            <canvas ref="canvas" class="workshop-canvas" tabindex="0" aria-label="3D model. Šipky otáčejí, Shift a šipky posouvají, plus a minus mění přiblížení. Home obnoví pohled. Klepnutím vyberete díl." @keydown="keyCamera" />
            <svg v-if="overlay" class="workshop-overlay" :viewBox="`0 0 ${overlay.width} ${overlay.height}`" aria-hidden="true">
              <g v-for="route in overlay.routes" :key="route.id">
                <polyline :points="route.points" fill="none" :stroke="route.color" :stroke-width="route.active ? 3.5 : 1.2" :stroke-opacity="route.active ? 1 : .5" :data-wire-id="route.id" :data-from="route.from" :data-to="route.to" :data-net="route.net" :data-bundle="route.bundle" />
                <polyline v-if="overlay.inspect" :points="route.points" fill="none" stroke="transparent" stroke-width="14" class="workshop-wire-hit" @pointerdown.stop.prevent="selectWire(route.id)" />
              </g>
              <g v-for="(flex, index) in overlay.flex" :key="`flex-${index}`"><line :x1="flex.start[0]" :y1="flex.start[1]" :x2="flex.end[0]" :y2="flex.end[1]" stroke="var(--aw-accent)" stroke-width="4" stroke-dasharray="4 3" :data-flexible="flex.id" /><text :x="flex.end[0] + 5" :y="flex.end[1] - 16">{{ flex.label }}</text></g>
              <g v-for="(pin, index) in overlay.pins" :key="`pin-${index}`"><circle :cx="pin.point[0]" :cy="pin.point[1]" :r="pin.active ? 4 : 2.5" :fill="pin.color" :data-endpoint="pin.endpoint" /><circle v-if="overlay.inspect" :cx="pin.point[0]" :cy="pin.point[1]" r="7" fill="transparent" class="workshop-wire-hit" @pointerdown.stop.prevent="selectWire(pin.routeId, pin.endpoint)" /><text v-if="pin.active" :x="pin.point[0] + 5" :y="pin.point[1] + 5">{{ pin.label }}</text></g>
              <g v-for="dimension in overlay.dimensions" :key="dimension.label"><line :x1="dimension.start[0]" :y1="dimension.start[1]" :x2="dimension.end[0]" :y2="dimension.end[1]" stroke="var(--aw-accent)" stroke-width="2" /><text :x="(dimension.start[0] + dimension.end[0]) / 2" :y="(dimension.start[1] + dimension.end[1]) / 2">{{ dimension.label }}</text></g>
              <text v-for="orientation in overlay.orientation" :key="orientation.label" :x="orientation.x" :y="orientation.y + 14" class="workshop-orientation">{{ orientation.label }}</text>
            </svg>
            <ModelLabels v-if="overlay?.names.length" :names="overlay.names" :width="overlay.width" :height="overlay.height" @select="selectPart" />
          </div>
          <p class="workshop-context">
            {{ state.mode === 'assembly' ? `Montáž ${state.assemblyIndex + 1}/${steps.length} · ${currentStep?.title || 'Vyberte krok'}` : state.mode === 'transport' ? 'Transportní konfigurace' : 'Provozní konfigurace' }} · {{ cameraName }} · {{ selected?.info?.title || selected?.name || 'Žádný vybraný díl' }}
          </p>
          <div v-if="state.mode === 'assembly' && currentStep" class="workshop-context">
            <strong>
              {{ currentStep.location }}
            </strong>
            <p>
              {{ currentStep.direction }} · {{ currentStep.partSummary }}
            </p>
          </div>
          <div class="workshop-toolbar">
            <AwButton @click="camera('iso', 'Izometrie')">
              Izometrie
            </AwButton>
            <AwButton @click="camera('front', 'Zepředu')">
              Zepředu
            </AwButton>
            <AwButton @click="camera('top', 'Shora')">
              Shora
            </AwButton>
            <AwButton @click="camera('side', 'Z boku')">
              Z boku
            </AwButton>
              <AwButton @click="camera('back', 'Zezadu')">Zezadu</AwButton>
              <AwButton @click="camera('left', 'Zleva')">Zleva</AwButton>
              <AwButton :disabled="!selected" @click="engine?.focus([selected.id])">Přiblížit výběr</AwButton>
            <AwButton aria-label="Přiblížit" @click="engine?.zoom(.8)">
              +
            </AwButton>
            <AwButton aria-label="Oddálit" @click="engine?.zoom(1.25)">
              −
            </AwButton>
          </div>
          <details class="workshop-controls">
            <summary>
              Víko, kabely a ilustrativní pohyb
            </summary>
            <label>
              Víko {{ state.lid }}°
              <input v-model.number="state.lid" type="range" min="0" max="160" />
            </label>
            <label>
              Průhlednost pláště
              <input v-model.number="state.opacity" type="range" min="0.05" max="1" step="0.05" />
            </label>
            <label>
              <input v-model="state.wires" type="checkbox" />
              Kabeláž
            </label>
            <label>
              <input v-model="state.wands" type="checkbox" />
              Hůlky
            </label>
              <label><input v-model="state.inspect" type="checkbox" /> Inspect · názvy a výběr vodičů</label>
              <label><input v-model="state.dimensions" type="checkbox" /> Rozměry návrhu</label>
              <label v-for="layer in (['bank','textile','cover','case','beds','hardware'] as const)" :key="layer"><input v-model="state[layer]" type="checkbox" /> {{ {bank:'Powerbanka',textile:'Textil',cover:'Kryty',case:'Korpus',beds:'Lůžka',hardware:'Elektronika a spoje'}[layer] }}</label>
            <template v-if="!connected && !isReplay">
              <p>
                Ilustrativní model; nejde o měřenou polohu ani povel.
              </p>
              <label v-for="(_, index) in state.angles" :key="index">
                Přihrádka {{ Number(index) + 1 }}
                <input v-model.number="state.angles[index]" type="range" min="0" :max="bundle.model.mechanism.travel_degrees" :disabled="previewRunning" />
              </label>
            </template>
          </details>
          <EffectPreview v-if="!connected && !isReplay && state.mode === 'operation'" :model="bundle.model" :angles="state.angles" @frame="Object.assign(state, $event)" @active="previewRunning = $event" />
        </section>
        <aside class="workshop-inspector aw-card" aria-label="Nástroje Workshopu">
          <div class="workshop-tabs" role="group" aria-label="Panel nástrojů">
            <AwButton v-for="tab in (['parts', 'assembly', 'runtime', 'timeline'] as const)" :key="tab" :aria-pressed="inspector === tab" @click="inspector = tab">
              {{ {parts:'Díly',assembly:'Montáž',runtime:'Pozorování',timeline:'Časová osa'}[tab] }}
            </AwButton>
          </div>
          <section v-if="inspector === 'parts'" aria-labelledby="parts-title">
            <h2 id="parts-title">
              Díly a barvy
            </h2>
            <AwField id="part-search" label="Najít díl">
              <input id="part-search" v-model="search" type="search" class="aw-input" />
            </AwField>
            <details>
              <summary>
                Skupiny
              </summary>
              <label v-for="group in groups" :key="group">
                <input type="checkbox" :checked="!state.hiddenGroups.includes(group)" @change="state.hiddenGroups = toggle(state.hiddenGroups, group)" />
                {{ group }}
              </label>
            </details>
            <ul class="workshop-parts">
              <li v-for="mesh in parts" :key="mesh.id">
                <input type="checkbox" :aria-label="`Viditelnost ${mesh.info?.title || mesh.name}`" :checked="!state.hidden.includes(mesh.id)" @change="state.hidden = toggle(state.hidden, mesh.id)" />
                <button class="workshop-part" :aria-pressed="state.selected === mesh.id" @click="state.selected = mesh.id">
                  {{ mesh.info?.title || mesh.name }}
                </button>
              </li>
            </ul>
            <div v-if="selected">
              <h3>
                {{ selected.info?.title || selected.name }}
              </h3>
              <p>
                {{ selected.info?.description }}
              </p>
              <ul>
                <li v-for="(detail, index) in selected.info?.details || []" :key="index">
                  {{ detail }}
                </li>
              </ul>
              <AwField id="part-color" label="Barva dílu">
                <input id="part-color" type="color" :value="selectedColor" @input="setColor" />
              </AwField>
              <AwButton @click="state.isolate = state.isolate === selected.id ? null : selected.id">
                {{ state.isolate === selected.id ? 'Ukončit izolaci' : 'Izolovat díl' }}
              </AwButton>
              <p>
                {{ storageNotice }}
              </p>
              <details>
                <summary>
                  Identita a vazby
                </summary>
                <p>
                  {{ selected.id }} · {{ selected.group }}
                </p>
                <pre>
                  {{ selected.info }}
                </pre>
              </details>
              <p>Rozměry obálky: {{ [0, 1, 2].map(axis => (selected.bounds[axis + 3] - selected.bounds[axis]).toFixed(1)).join(' × ') }} mm · návrhová geometrie</p>
            </div>
            <details v-if="bundle.model.routes.length">
              <summary>Kabeláž a propojení</summary>
              <AwField id="workshop-wire" label="Vodič"><select id="workshop-wire" v-model="state.selectedWire" class="aw-input"><option value="">Vyberte vodič</option><option v-for="route in bundle.model.routes" :key="route.id" :value="route.id">{{ route.id }} · {{ route.from }} → {{ route.to }}</option></select></AwField>
              <AwField id="workshop-wire-scope" label="Rozsah zvýraznění"><select id="workshop-wire-scope" v-model="state.wireScope" class="aw-input"><option value="branch">Vodič</option><option value="net">Síť</option><option value="bundle">Svazek</option></select></AwField>
              <template v-if="selectedRoute"><p>{{ selectedRoute.id }}: {{ selectedRoute.from }} → {{ selectedRoute.to }}</p><p>Síť {{ selectedRoute.net }} · svazek {{ selectedRoute.bundle }}</p><p>{{ selectedRoute.note }}</p><pre>{{ bundle.model.endpoints?.[selectedRoute.from] }}{{ bundle.model.endpoints?.[selectedRoute.to] }}</pre></template>
              <p>Vodiče a piny jsou návrhové obálky; nejde o změřené souřadnice.</p>
            </details>
          </section>
          <section v-if="inspector === 'assembly'" aria-labelledby="assembly-title">
            <h2 id="assembly-title">
              Montážní postup
            </h2>
            <AwField id="assembly-step" label="Krok">
              <select id="assembly-step" :value="state.assemblyIndex" class="aw-input" @change="assemblyStep(Number(($event.target as HTMLSelectElement).value))">
                <option value="-1" disabled>
                  Vyberte krok
                </option>
                <option v-for="(step, index) in steps" :key="step.id" :value="index">
                  {{ index + 1 }} · {{ step.title }}
                </option>
              </select>
            </AwField>
            <template v-if="currentStep">
              <h3>
                {{ currentStep.title }}
              </h3>
              <p>
                {{ currentStep.instruction }}
              </p>
              <p>
                <strong>
                  Kontrola:
                </strong>
                {{ currentStep.check }}
              </p>
              <p>
                {{ currentStep.location }} · {{ currentStep.direction }}
              </p>
              <AwButton :disabled="state.assemblyIndex <= 0" @click="assemblyStep(state.assemblyIndex - 1)">
                Předchozí
              </AwButton>
              <AwButton :disabled="state.assemblyIndex >= steps.length - 1" @click="assemblyStep(state.assemblyIndex + 1)">
                Další
              </AwButton>
              <AwButton @click="focusAssembly">Přiblížit díly kroku</AwButton>
              <details><summary>Materiál, nástroje a servisní vazby</summary><p v-if="currentStep.depends?.length">Předchůdci: {{ currentStep.depends.join(', ') }}</p><ul><li v-for="material in currentStep.materials || []" :key="material.id">{{ material.quantity }} × {{ bundle.model.assemblyGuide.materialCatalog?.[material.id]?.label || material.id }}</li></ul><p>Nástroje: {{ (currentStep.tools || []).join(', ') }}</p><p>Přístup: {{ currentStep.access }}</p><p>Konektory: {{ currentStep.connectors }}</p><p>Demontáž: {{ currentStep.removal }}</p><p>{{ currentStep.installation }}</p></details>
            </template>
            <p v-else>
              Model ani postup nepotvrzují fyzické sestavení.
            </p>
          </section>
          <section v-if="inspector === 'runtime'" aria-labelledby="runtime-title">
            <h2 id="runtime-title">
              Pozorování a replay
            </h2>
            <p>
              Snapshoty jádra jsou pouze ke čtení. Poloha je simulace nebo kalibrovaný odhad; fyzické měření není dostupné.
            </p>
            <template v-if="!offline">
              <p v-if="wantedLiveSource && sourceMode === 'live'">Požadovaný živý kufr: <code>{{ wantedLiveSource }}</code>. Dostupnost ověříte načtením zdrojů.</p>
              <AwNotice v-if="wantedLiveSource && sourceMode === 'live' && sourcesListed && !sources.some(source => source.instanceId === wantedLiveSource)" tone="warning">Požadovaný kufr není v dostupném autorizovaném seznamu. Žádný náhradní zdroj nebyl vybrán.</AwNotice>
              <AwField id="observer-mode" label="Režim zdroje">
                <select id="observer-mode" v-model="sourceMode" class="aw-input" @change="changeSource">
                  <option value="simulation">
                    Simulace
                  </option>
                  <option value="live">
                    Živý kufr
                  </option>
                </select>
              </AwField>
              <AwField id="observer-origin" label="Server pozorovatele" hint="Prázdné pole použije server aplikace a vaši existující relaci.">
                <input id="observer-origin" v-model="observerBase" class="aw-input" type="url" placeholder="https://server" @change="changeSource" />
              </AwField>
              <AwButton :busy="connecting" @click="listSources">
                Načíst dostupné zdroje
              </AwButton>
              <AwField id="observer-source" label="Konkrétní zdroj">
                <select id="observer-source" v-model="sourceId" class="aw-input" @change="disconnect({ keepSources: true })">
                  <option value="">
                    Vyberte zdroj
                  </option>
                  <option v-for="source in sources" :key="source.instanceId" :value="source.instanceId">
                    {{ source.instanceId }} · {{ source.mode }}
                  </option>
                </select>
              </AwField>
              <AwButton :disabled="!sourceId || connected" :busy="connecting" @click="connectObserver">
                Pozorovat
              </AwButton>
              <AwButton :disabled="!connected && !connecting" @click="disconnect()">
                Odpojit
              </AwButton>
            </template>
            <AwStatus>
              {{ isReplay ? 'REPLAY · historie' : runtimeStatus }}
            </AwStatus>
            <p v-if="receivedAt">
              Poslední přijatý snapshot: {{ receivedAt }} UTC
            </p>
            <AwNotice v-if="runtimeError" tone="warning">
              {{ runtimeError }}
            </AwNotice>
            <AwNotice v-if="observation && (!observation.compatible || !observation.fresh)" tone="warning">
              {{ observation.compatibility }} {{ observation.fresh ? '' : 'Data jsou stará; animace vypnuta.' }}
            </AwNotice>
            <p v-if="observation">
              Odhady přihrádek: {{ observation.estimates.join(', ') }}
            </p>
            <p v-if="observation">Audio: {{ observation.audioEvidence }} · evidence software enqueue/cancel/drain, nikoli měření slyšitelnosti.</p>
            <AwButton :disabled="!recording.length" @click="exportRecording">
              Uložit záznam
            </AwButton>
            <p>
              {{ recording.length }} snapshotů · {{ recordingDrops }} nezařazených kvůli limitu
            </p>
            <AwField id="recording-import" label="Otevřít záznam (JSON, nejvýše 8 MiB)">
              <input id="recording-import" type="file" accept="application/json,.json" @change="importRecording" />
            </AwField>
            <template v-if="isReplay">
              <label>
                Snapshot {{ replayIndex + 1 }}/{{ recording.length }}
                <input type="range" :value="replayIndex" min="0" :max="recording.length - 1" @input="seek(Number(($event.target as HTMLInputElement).value))" />
              </label>
              <AwButton @click="playReplay">
                {{ playing ? 'Pozastavit' : 'Přehrát záznam' }}
              </AwButton>
              <label>
                Rychlost
                <select v-model.number="speed" @change="stopReplay">
                  <option :value=".5">
                    0.5×
                  </option>
                  <option :value="1">
                    1×
                  </option>
                  <option :value="2">
                    2×
                  </option>
                  <option :value="4">
                    4×
                  </option>
                </select>
              </label>
            </template>
            <details v-if="currentEnvelope">
              <summary>
                Snapshot a evidence
              </summary>
              <pre>
                {{ currentEnvelope }}
              </pre>
            </details>
            <SimulationControls v-if="!offline && !observerBase && connected && !isReplay && chosenSource" :instance-id="sourceId" :profile="chosenSource.profile || 'unknown'" :can-control="chosenSource.canControl === true" :online="observation?.fresh === true" :mode="sourceMode" :replay="isReplay" @stop="mediaPanel?.invalidate()" />
          </section>
          <section v-if="inspector === 'timeline'" aria-labelledby="timeline-title">
            <h2 id="timeline-title">
              Časová osa jádra
            </h2>
            <AwField id="trace-import" label="Otevřít stopu jádra (nejvýše 1 MiB)">
              <input id="trace-import" type="file" accept="application/json,.json" @change="importTrace($event)" />
            </AwField>
            <AwField id="trace-compare" label="Stopa pro porovnání">
              <input id="trace-compare" type="file" accept="application/json,.json" @change="importTrace($event, true)" />
            </AwField>
            <AwNotice v-if="traceError" tone="warning">
              {{ traceError }}
            </AwNotice>
            <template v-if="trace">
              <h3>
                {{ trace.sceneId }} · {{ trace.operationId }}
              </h3>
              <p>
                Stav {{ trace.state }} · {{ trace.evidence }} · ztracené události {{ trace.droppedEvents }}
              </p>
              <AwNotice v-if="trace.incomplete || traceMismatches.length" tone="warning">
                {{ trace.incomplete ? 'Neúplná stopa. ' : '' }}{{ traceMismatches.length ? `Nesoulad plánu/modelu: ${traceMismatches.join(', ')}. Události nejsou důkazem shody.` : '' }}
              </AwNotice>
              <pre v-if="traceDiff">
                {{ traceDiff }}
              </pre>
              <template v-if="trace.events.length">
                <label>Událost {{ traceIndex + 1 }}/{{ trace.events.length }}<input type="range" min="0" :max="trace.events.length - 1" :value="traceIndex" @input="seekTrace" /></label>
                <AwButton @click="playTrace">{{ tracePlaying ? 'Pozastavit stopu' : 'Přehrát stopu' }}</AwButton>
                <p>Historický kurzor; žádné fyzické povely.</p><pre>{{ traceEvent }}</pre>
              </template>
            </template>
            <ol class="workshop-timeline">
              <li v-for="(event, index) in (trace?.events || telemetryEvents)" :key="index">
                <strong>
                  {{ event.timeMs }} ms · {{ event.kind }}
                </strong>
                <p>
                  {{ event.phaseId }} · {{ event.evidence }} · {{ event.reason }}
                </p>
                <small v-if="event.commandedAt">
                  Povel {{ event.commandedAt }} · měření {{ event.measuredAt || 'nedostupné' }}
                </small>
              </li>
            </ol>
          </section>
          <MediaPanel v-if="!offline && !observerBase && connected && !isReplay && chosenSource?.canControl" v-show="inspector === 'runtime'" ref="mediaPanel" :context="mediaContext" />
        </aside>
      </div>
    </template>
  </section>
</template>

<style scoped>
.workshop { display: grid; gap: var(--aw-space-4, 1rem); }
.workshop-heading, .workshop-toolbar, .workshop-tabs, .workshop-provenance { display: flex; flex-wrap: wrap; gap: var(--aw-space-3, .75rem); align-items: center; }
.workshop-heading { justify-content: space-between; }.workshop-heading h1 { margin-bottom: .25rem; }.workshop-heading p { margin-top: 0; }
.workshop-layout { display: grid; grid-template-columns: minmax(0, 1fr) minmax(18rem, 25rem); gap: 1rem; align-items: start; }
.workshop-scene, .workshop-inspector { min-width: 0; padding: 1rem; }
.workshop-viewport { position: relative; margin-block: .75rem; overflow: hidden; border-radius: var(--aw-radius-card); }
.workshop-canvas { display: block; width: 100%; height: min(65vh, 48rem); min-height: 22rem; touch-action: none; border-radius: var(--aw-radius-card); }
.workshop-overlay { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; overflow: hidden; }
.workshop-overlay text { font-family: var(--aw-font-mono); font-size: 10px; fill: var(--aw-ivory); paint-order: stroke; stroke: var(--aw-surface); stroke-width: 3px; }
.workshop-overlay .workshop-orientation { font-size: 9px; fill: var(--aw-muted); }
.workshop-wire-hit { pointer-events: stroke; cursor: crosshair; }
.workshop-canvas:focus-visible { outline: 3px solid var(--aw-accent); outline-offset: 3px; }
.workshop-context { margin: .5rem 0; }.workshop-provenance { font-size: .875rem; }.workshop-provenance dd { overflow-wrap: anywhere; }
.workshop-toolbar .aw-field { min-width: 10rem; flex: 1; }.workshop-tabs { margin-bottom: 1rem; }.workshop-tabs [aria-pressed="true"] { border-color: var(--aw-accent); }
.workshop-parts { max-height: 20rem; overflow: auto; padding: 0; list-style: none; }.workshop-parts li { display: flex; align-items: center; gap: .5rem; }
.workshop-part { width: 100%; text-align: left; padding: .65rem .5rem; background: transparent; border: 1px solid transparent; color: inherit; cursor: pointer; }.workshop-part[aria-pressed="true"] { border-color: var(--aw-accent); }
.workshop-controls label, .workshop-inspector details label { display: block; margin-block: .6rem; }.workshop input[type="range"] { width: 100%; }.workshop input[type="file"] { max-width: 100%; }
.workshop pre { white-space: pre-wrap; overflow-wrap: anywhere; max-height: 22rem; overflow: auto; font-size: .8rem; }.workshop-timeline { padding-left: 1.25rem; max-height: 36rem; overflow: auto; }.workshop-timeline p { margin-top: .25rem; }
@media (max-width: 900px) { .workshop-layout { grid-template-columns: 1fr; }.workshop-canvas { min-height: 20rem; height: 55vh; }.workshop-inspector { width: 100%; } }
</style>

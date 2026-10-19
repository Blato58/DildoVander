<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import AwNotice from '../../components/AwNotice.vue';
import { safeModelResources, validateDistributionIndex } from './downloads.mjs';

const props = defineProps<{ bundle: any; offline: boolean }>();
const distributions = shallowRef<any[]>([]), loading = ref(false), error = ref('');
let request: AbortController | null = null, generation = 0;
const resources = computed(() => props.offline ? [] : safeModelResources(props.bundle, location.origin));
const current = computed(() => distributions.value.filter(item => !item.historical));
const historical = computed(() => distributions.value.filter(item => item.historical));
const fixture = computed(() => props.bundle.manifest.provenance.kind === 'synthetic-fixture');
const size = (bytes: number) => `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
watch(() => props.bundle.manifest.bundleHash, async () => {
  const own = ++generation; request?.abort(); distributions.value = []; error.value = '';
  if (props.offline || fixture.value) return;
  request = new AbortController(); const signal = request.signal; loading.value = true;
  try {
    const response = await fetch('/downloads/index.json', { credentials: 'omit', redirect: 'error', cache: 'no-cache', signal });
    if (!response.ok) throw new Error(`Přehled exportů není dostupný (HTTP ${response.status}).`);
    if (Number(response.headers.get('content-length')) > 262144) throw new Error('Přehled exportů překročil velikostní mez.');
    const text = await response.text();
    if (new TextEncoder().encode(text).length > 262144) throw new Error('Přehled exportů překročil velikostní mez.');
    const items = validateDistributionIndex(JSON.parse(text), props.bundle.manifest, location.origin);
    if (own === generation) distributions.value = items;
  } catch (cause) { if (own === generation && !signal.aborted) error.value = cause instanceof Error ? cause.message : String(cause); }
  finally { if (own === generation) loading.value = false; }
}, { immediate: true });
onBeforeUnmount(() => { ++generation; request?.abort(); });
</script>

<template>
  <details class="model-downloads">
    <summary>Model a exporty ke stažení</summary>
    <p v-if="fixture">Syntetická UI fixture nemá výrobní exporty. Není určena k výrobě.</p>
    <p v-else-if="offline">Toto je vybrané offline vydání. Nevyžaduje cloud ani síť; další exporty otevřete v původní distribuci.</p>
    <template v-else>
      <p>Modelová, aplikační a výrobní vydání mají samostatnou provenienci. CAD ověření není fyzická přejímka.</p>
      <ul v-if="resources.length"><li v-for="resource in resources" :key="resource.href"><a :href="resource.href" download>{{ resource.label }}</a><small v-if="resource.bytes"> · {{ size(resource.bytes) }}</small><details><summary>{{ resource.canonical ? 'Identita kanonického manifestu' : 'SHA-256 souboru' }}</summary><code>{{ resource.sha256 }}</code></details></li></ul>
      <p v-if="loading" role="status">Načítám zveřejněné exporty.</p>
      <AwNotice v-if="error" tone="warning">{{ error }} Samotný model zůstává použitelný.</AwNotice>
      <h3 v-if="current.length">Exporty přiřazené k těmto datům modelu</h3>
      <ul><li v-for="item in current" :key="item.releaseId"><a :href="item.href" download>{{ item.label }} · {{ item.version }}</a><p>{{ size(item.bytes) }} · {{ item.kind }} · CAD {{ item.cadVerification }} · fyzické ověření nedostupné</p><details><summary>SHA-256 neměnného souboru</summary><code>{{ item.sha256 }}</code></details></li></ul>
      <h3 v-if="historical.length">Historické výrobní archivy</h3>
      <p v-if="historical.length">Tyto původní archivy nemají vazbu na hash aktuálních 3D dat. Nepotvrzují shodu s vybraným modelem.</p>
      <ul><li v-for="item in historical" :key="item.releaseId"><a :href="item.href" download>{{ item.label }} · {{ item.version }}</a><p>{{ size(item.bytes) }} · původní doklad CAD ověření; fyzická přejímka nedostupná</p><details><summary>SHA-256 původního archivu</summary><code>{{ item.sha256 }}</code></details></li></ul>
      <p v-if="!loading && !error && !distributions.length">Pro tato data nejsou v přehledu publikované další exporty.</p>
    </template>
  </details>
</template>

<style scoped>
.model-downloads { margin-top: 1rem; }.model-downloads code { font-size: .75rem; overflow-wrap: anywhere; }.model-downloads li { margin-block: .75rem; }
</style>

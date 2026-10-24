<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import type { TestKeepsake } from './contracts';
import type { TestingSessionsClient } from './client';
const props = defineProps<{ sessionId: string; items: TestKeepsake[]; client: TestingSessionsClient }>();
const retryConfirmed = ref(false); const retryBusy = ref(false); const retryError = ref('');
const emit=defineEmits<{refresh:[]}>();
async function retry(jobId:string):Promise<void>{if(!retryConfirmed.value||retryBusy.value)return;retryBusy.value=true;retryError.value='';try{await props.client.retryGeneration(props.sessionId,jobId,true);emit('refresh');}catch(cause){retryError.value=cause instanceof Error?cause.message:'test_retry_failed';}finally{retryBusy.value=false;}}
const images = ref<Record<string, string>>({}); const failures = ref<string[]>([]);
let generation = 0;
function clear(): void { generation++; for (const url of Object.values(images.value)) URL.revokeObjectURL(url); images.value = {}; failures.value = []; }
watch(() => `${props.sessionId}:${props.items.flatMap(item => item.assets.map(asset => asset.id)).join(',')}`, async () => {
  clear(); const current = generation;
  const assets = props.items.flatMap(item => item.assets.filter(asset => ['image/png', 'image/jpeg', 'image/webp'].includes(asset.mediaType)).map(asset => ({ keepsakeId: item.id, asset })));
  await Promise.allSettled(assets.map(async ({ keepsakeId, asset }) => {
    try { const blob = await props.client.asset(props.sessionId, keepsakeId, asset.id); if (current === generation) images.value = { ...images.value, [asset.id]: URL.createObjectURL(blob) }; }
    catch (cause) { if (current === generation && !(cause instanceof DOMException && cause.name === 'AbortError')) failures.value.push(asset.id); }
  }));
}, { immediate: true });
onBeforeUnmount(clear);
</script>

<template>
  <div class="testing-keepsakes">
    <article v-for="item in items" :key="item.id">
      <h3>Testovací památka · {{ item.state }}</h3>
      <p v-if="!item.editions.length" class="aw-muted">Příběh zatím nemá potvrzené dokončené vydání.</p>
      <section v-for="edition in item.editions" :key="edition.id">
        <h4>{{ edition.story.title }}</h4><p>{{ edition.story.text }}</p>
        <img v-if="images[edition.artworkAssetId]" :src="images[edition.artworkAssetId]" :alt="`Generovaný obraz: ${edition.story.title}`" loading="lazy" />
        <p class="aw-muted">{{ edition.synthetic ? 'Řízený testovací výstup — není živá generace' : 'Uložený výstup poskytovatele' }} · {{ edition.provider }} · {{ edition.model }}</p>
      </section>
      <section v-for="portrait in item.portraits" :key="portrait.id"><h4>Portrét</h4><img v-if="images[portrait.assetId]" :src="images[portrait.assetId]" alt="AI portrét testovací postavy" loading="lazy" /><p class="aw-muted">{{ portrait.synthetic ? 'Řízený testovací výstup' : 'Uložený výstup poskytovatele' }} · {{ portrait.provider }} · {{ portrait.model }}</p></section>
      <ul v-if="item.jobs.length"><li v-for="job in item.jobs" :key="job.id">{{ job.kind }} · {{ job.state }}<span v-if="job.errorCode"> · {{ job.errorCode }}</span><AwButton v-if="['awaiting_reconciliation','failed'].includes(job.state)" :busy="retryBusy" :disabled="!retryConfirmed" @click="retry(job.id)">Výslovně zopakovat generování</AwButton></li></ul>
    </article>
    <details v-if="items.some(item=>item.jobs.some(job=>['awaiting_reconciliation','failed'].includes(job.state)))"><summary>Opakování neúspěšné nebo nejasné generace</summary><label><input v-model="retryConfirmed" type="checkbox" /> Potvrzuji nový skutečný požadavek. Předchozí nejasný pokus mohl být zpoplatněn poskytovatelem; opakování může čerpat další API.</label><AwNotice v-if="retryError" tone="danger">{{ retryError }}</AwNotice></details>
    <AwNotice v-if="failures.length" tone="warning">Některé uložené obrazy se nepodařilo načíst. Obnovte stav sezení pro další pokus.</AwNotice>
  </div>
</template>

<style scoped>
.testing-keepsakes { display: grid; gap: 1.5rem; }
.testing-keepsakes article { border-top: 1px solid var(--aw-border-subtle); padding-top: 1rem; }
.testing-keepsakes img { display: block; max-width: min(100%, 32rem); max-height: 36rem; object-fit: contain; border-radius: var(--aw-radius-card); }
.testing-keepsakes p { white-space: pre-wrap; overflow-wrap: anywhere; }
</style>

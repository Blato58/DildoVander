<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest } from './session';
interface LinkedKeepsake {locator:string;linkedAt:number;officialName?:string;storyTitle?:string;hasPortrait?:boolean}
const items = ref<LinkedKeepsake[]>([]);
const error = ref(''); const loading = ref(false); let generation = 0;
async function load() {
  const epoch = ++generation; items.value = []; error.value = '';
  if (!account.value) return;
  loading.value = true;
  try {
    const result = await accountRequest<{items: LinkedKeepsake[]}>('/app/v1/archive/associations');
    if (generation === epoch) items.value = result.items;
  } catch (e) { if (generation === epoch) error.value = (e as Error).message; }
  finally { if (generation === epoch) loading.value = false; }
}
watch(() => account.value?.id, () => { ++generation; items.value = []; void load(); }, { flush: 'sync' });
onMounted(load); onBeforeUnmount(() => { ++generation; items.value = []; });
async function remove(locator: string) {
  if (!window.confirm('Odpojit tuto památku od účtu? Návštěvnické vlastnictví ani uložené soubory se tím nemění.')) return;
  try { await accountRequest(`/app/v1/archive/associations/${encodeURIComponent(locator)}`, 'DELETE'); await load(); }
  catch (e) { error.value = (e as Error).message; }
}
</script>
<template>
  <section v-if="account" class="aw-card" aria-labelledby="account-archive-title">
    <h2 id="account-archive-title">Památky propojené s účtem</h2>
    <p>Propojení vytváříte výslovně z vlastní návštěvnické památky. Umožňuje číst příběh a výsledné portréty; původní fotografie zůstávají mimo přístup účtu.</p>
    <AwNotice v-if="error" tone="warning">{{ error }}</AwNotice>
    <p v-if="loading" role="status">Načítám propojené památky…</p>
    <p v-else-if="!items.length && !error">Zatím nemáte propojenou památku. Otevřete svou památku a zvolte propojení s účtem.</p>
    <ul v-else class="archive-links"><li v-for="item in items" :key="item.locator"><RouterLink :to="{path:'/keepsake/',query:{account:'1',k:item.locator}}">{{item.officialName || 'Otevřít propojenou památku'}}</RouterLink><p v-if="item.storyTitle">{{item.storyTitle}}</p><small v-if="item.hasPortrait">Obsahuje soukromý výsledný portrét</small><small>Propojeno {{ new Date(item.linkedAt * 1000).toLocaleDateString('cs') }}</small><AwButton @click="remove(item.locator)">Odpojit od účtu</AwButton></li></ul>
  </section>
</template>
<style scoped>.archive-links { list-style: none; padding: 0; }.archive-links li { display: grid; gap: .5rem; padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }.archive-links button { justify-self: start; }</style>

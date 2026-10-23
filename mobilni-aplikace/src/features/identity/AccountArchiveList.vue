<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest, offlineSession } from './session';
interface LinkedKeepsake {
  locator: string;
  linkedAt: number;
  officialName?: string;
  storyTitle?: string;
  hasPortrait?: boolean;
  accessKind?: 'originalOwner' | 'linkedViewer' | string;
  canManage?: boolean;
  canUnlink?: boolean;
}
const items = ref<LinkedKeepsake[]>([]);
const error = ref(''); const loading = ref(false); let generation = 0;
function canUnlink(item: LinkedKeepsake) {
  return item.accessKind === 'linkedViewer' && item.canManage === false && item.canUnlink === true;
}
function isOriginalOwner(item: LinkedKeepsake) {
  return item.accessKind === 'originalOwner' && item.canManage === true && item.canUnlink === false;
}
function currentItem(item: LinkedKeepsake) {
  return items.value.find(candidate => candidate.locator === item.locator);
}
async function load() {
  const epoch = ++generation; items.value = []; error.value = '';
  if (!account.value || offlineSession.value) return;
  loading.value = true;
  try {
    const result = await accountRequest<{items: LinkedKeepsake[]}>('/app/v1/archive/associations');
    if (generation === epoch) items.value = result.items;
  } catch (e) { if (generation === epoch) error.value = (e as Error).message; }
  finally { if (generation === epoch) loading.value = false; }
}
watch(() => `${account.value?.id ?? ''}:${offlineSession.value}`,  () => { ++generation; items.value = []; void load(); }, { flush: 'sync' });
onMounted(load); onBeforeUnmount(() => { ++generation; items.value = []; });
async function remove(item: LinkedKeepsake) {
  if (!currentItem(item) || !canUnlink(currentItem(item)!)) return;
  const epoch = generation;
  const accountId = account.value?.id;
  if (!window.confirm('Odpojit tuto památku od účtu? Návštěvnické vlastnictví ani uložené soubory se tím nemění.')) return;
  if (generation !== epoch || account.value?.id !== accountId || !currentItem(item) || !canUnlink(currentItem(item)!)) return;
  try {
    await accountRequest(`/app/v1/archive/associations/${encodeURIComponent(item.locator)}`, 'DELETE');
    if (generation === epoch && account.value?.id === accountId) await load();
  } catch (e) { if (generation === epoch && account.value?.id === accountId) error.value = (e as Error).message; }
}
</script>
<template>
  <section v-if="account && !offlineSession" class="aw-card" aria-labelledby="account-archive-title">
    <h2 id="account-archive-title">Památky dostupné z účtu</h2>
    <p>Původní vlastník může spravovat svou soukromou památku i zdrojové fotografie. Výslovně propojené památky jsou pouze ke čtení: účet uvidí příběh a výsledné portréty, nikoli původní fotografie.</p>
    <AwNotice v-if="error" tone="warning">{{ error }}</AwNotice>
    <p v-if="loading" role="status">Načítám propojené památky…</p>
    <p v-else-if="!items.length && !error">Zatím zde nemáte žádnou památku dostupnou z účtu. Propojení můžete vytvořit ze své návštěvnické památky.</p>
    <ul v-else class="archive-links"><li v-for="item in items" :key="item.locator"><RouterLink :to="{path:'/keepsake/',query:{account:'1',k:item.locator}}">{{item.officialName || 'Otevřít propojenou památku'}}</RouterLink><p v-if="item.storyTitle">{{item.storyTitle}}</p><small v-if="item.hasPortrait">Obsahuje soukromý výsledný portrét</small><small v-if="isOriginalOwner(item)">Vaše památka · tento účet ji může spravovat</small><small v-else-if="canUnlink(item)">Propojená památka · pouze čtení</small><small v-else>Přístup nelze ověřit; odpojení není dostupné.</small><small>Propojeno {{ new Date(item.linkedAt * 1000).toLocaleDateString('cs') }}</small><AwButton v-if="canUnlink(item)" @click="remove(item)">Odpojit od účtu</AwButton></li></ul>
  </section>
</template>
<style scoped>.archive-links { list-style: none; padding: 0; }.archive-links li { display: grid; gap: .5rem; padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }.archive-links button { justify-self: start; }</style>

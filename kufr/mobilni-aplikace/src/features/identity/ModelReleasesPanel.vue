<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest, hasRole } from './session';

interface ModelRelease { bundleHash: string; modelId: string; revision: string; sourceRevision: string; cadVerification: string; canSelect: boolean; bytes: number }
interface ActiveModel { schemaVersion: number; modelId: string; bundleHash: string; manifestPath: string }
interface Catalog { enabled: boolean; active: ActiveModel | null; releases: ModelRelease[]; invalidReleases: number; truncated: boolean; integrity: string }
const catalog = ref<Catalog | null>(null), selectedHash = ref('');
const confirmed = ref(false), busy = ref(false), error = ref(''), message = ref('');
const selected = computed(() => catalog.value?.releases.find(release => release.bundleHash === selectedHash.value));
const canActivate = computed(() => hasRole('Administrator') && catalog.value?.enabled && selected.value?.canSelect
  && selectedHash.value !== catalog.value?.active?.bundleHash && confirmed.value && !busy.value);
let generation = 0;
function current(ticket: number, accountId: string | undefined) { return ticket === generation && accountId === account.value?.id && hasRole('Administrator'); }
async function load() {
  const ticket = ++generation, accountId = account.value?.id;
  if (!hasRole('Administrator')) return;
  busy.value = true; error.value = ''; confirmed.value = false;
  try {
    const result = await accountRequest<Catalog>('/app/v1/admin/model-releases');
    if (!current(ticket, accountId)) return;
    catalog.value = result;
    if (!result.releases.some(release => release.bundleHash === selectedHash.value)) selectedHash.value = '';
  } catch (failure) { if (current(ticket, accountId)) error.value = (failure as Error).message; }
  finally { if (ticket === generation) busy.value = false; }
}
async function activate() {
  if (!canActivate.value) return;
  const ticket = ++generation, accountId = account.value?.id;
  const bundleHash = selectedHash.value, expectedActiveHash = catalog.value?.active?.bundleHash ?? null;
  busy.value = true; error.value = ''; message.value = ''; confirmed.value = false;
  try {
    await accountRequest('/app/v1/admin/model-releases/active', 'POST', { bundleHash, expectedActiveHash, confirm: true });
    if (!current(ticket, accountId)) return;
    message.value = 'Aktivní model byl změněn. Otevřené prohlížeče načtou nový model po obnovení dílny.';
    await load();
  } catch (failure) { if (current(ticket, accountId)) error.value = (failure as Error).message; }
  finally { if (ticket === generation) busy.value = false; }
}
watch(selectedHash, () => { confirmed.value = false; message.value = ''; });
watch(() => `${account.value?.id ?? ''}:${account.value?.roles.join(',') ?? ''}`, () => {
  generation++; catalog.value = null; selectedHash.value = ''; confirmed.value = false; busy.value = false; message.value = ''; error.value = '';
  if (hasRole('Administrator')) void load();
});
onMounted(load);
onBeforeUnmount(() => { generation++; });
</script>
<template>
  <section class="aw-card model-releases" aria-labelledby="model-release-title" :aria-busy="busy">
    <h2 id="model-release-title">Publikované modely</h2>
    <p>Zde vybíráte již publikovaný model dílny. Změna modelu nespouští výrobu, AI ani ovládání kufru.</p>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }} <RouterLink :to="{ path: '/signin', query: { reauth: '1', return: '/settings/admin' } }">Znovu ověřit přihlášení</RouterLink></AwNotice>
    <AwNotice v-if="message" tone="success" role="status">{{ message }}</AwNotice>
    <AwNotice v-if="!hasRole('Administrator')" tone="warning">Výběr modelu vyžaduje oprávnění vlastníka nebo správce.</AwNotice>
    <template v-else-if="catalog">
      <AwNotice v-if="!catalog.enabled" tone="warning">Publikování modelů není na serveru povolené.</AwNotice>
      <template v-else>
        <p v-if="catalog.active">Aktivní model: <strong>{{ catalog.active.modelId }}</strong><br /><code>{{ catalog.active.bundleHash }}</code></p>
        <p v-else>Žádný model zatím není aktivní. Ostatní části aplikace zůstávají dostupné.</p>
        <AwNotice v-if="catalog.invalidReleases" tone="warning">Některé modelové balíčky neprošly kontrolou a nejsou k dispozici.</AwNotice>
        <AwNotice v-if="catalog.truncated" tone="warning">Zobrazuje se omezený seznam modelů. Další verze musí zkontrolovat správce serveru.</AwNotice>
        <p v-if="!catalog.releases.length">Zatím nejsou k dispozici žádné publikované modely.</p>
        <form v-else class="model-selection" @submit.prevent="activate">
          <label for="published-model">Model dílny</label>
          <select id="published-model" v-model="selectedHash" class="aw-select" :disabled="busy">
            <option value="" disabled>Vyberte publikovanou verzi</option>
            <option v-for="release in catalog.releases" :key="release.bundleHash" :value="release.bundleHash" :disabled="!release.canSelect">
              {{ release.modelId }} · {{ release.revision }} · {{ release.bundleHash.slice(0, 12) }}{{ release.canSelect ? '' : ' · bez ověření CAD' }}
            </option>
          </select>
          <dl v-if="selected"><dt>Revize modelových zdrojů</dt><dd><code>{{ selected.sourceRevision }}</code></dd><dt>Ověření</dt><dd>Ověřený export CAD. Fyzické přijetí tím není potvrzené.</dd></dl>
          <p>Před změnou server znovu ověří celý balíček. Potřebujete přihlášení přístupovým klíčem z posledních 10 minut.</p>
          <label class="model-confirm"><input v-model="confirmed" type="checkbox" :disabled="!selected?.canSelect || busy || selectedHash === catalog.active?.bundleHash" /> Potvrzuji změnu aktivního modelu pro všechny návštěvníky dílny.</label>
          <AwButton type="submit" variant="primary" :disabled="!canActivate" :busy="busy">Nastavit aktivní model</AwButton>
        </form>
      </template>
    </template>
    <AwButton :disabled="busy || !hasRole('Administrator')" @click="load">Obnovit seznam</AwButton>
  </section>
</template>
<style scoped>
.model-releases { display: grid; gap: 1rem; }.model-releases h2, .model-releases p { margin: 0; }.model-selection { display: grid; gap: .75rem; max-width: 44rem; }.model-releases code { overflow-wrap: anywhere; font-size: .85em; }.model-releases dl { display: grid; gap: .4rem; margin: 0; }.model-releases dd { margin: 0; }.model-confirm { display: flex; align-items: flex-start; gap: .6rem; }.model-confirm input { margin-top: .3rem; }
</style>

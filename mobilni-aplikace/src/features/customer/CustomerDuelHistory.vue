<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest, accountDeletionRequested, offlineSession } from '../identity/session';
import { customerCopy } from '../../application/customerCopy';
import { isUuid } from '../archive/client';
import { projectDuel, duelWandName, duelStates, type Duel } from '../archive/showContracts';
const items = ref<Duel[]>([]); const busy = ref(false); const failed = ref(false);
const en = computed(() => customerCopy.language.value === 'en');
const statesEn: Record<string, string> = { AwaitingParticipants: 'Waiting for both participants.', Ready: 'Ready for the event operator to start.', Running: 'Battle in progress.', Completed: 'Battle completed.', Cancelled: 'Battle cancelled. No winner.', Interrupted: 'Battle interrupted. No winner.' };
const allowed = computed(() => !!account.value && !accountDeletionRequested.value && !offlineSession.value);
let sequence = 0; let controller = new AbortController();
function clear() { sequence++; controller.abort(); controller = new AbortController(); items.value = []; busy.value = false; failed.value = false; }
async function refresh() {
  clear(); if (!allowed.value || document.hidden) return;
  const request = sequence; const owner = account.value?.id; busy.value = true;
  try {
    const value = await accountRequest<{ items?: unknown[] }>('/app/v1/customer/duels', 'GET', undefined, controller.signal);
    if (request !== sequence || owner !== account.value?.id) return;
    if (!Array.isArray(value.items) || value.items.length > 100) throw new Error('Invalid battle history');
    items.value = value.items.map(item => {
      if (!item || typeof item !== 'object' || !isUuid((item as Record<string, unknown>).id)) throw new Error('Invalid battle');
      return projectDuel(item, (item as Record<string, unknown>).id as string);
    });
  } catch (error) { if (request === sequence && (error as Error).name !== 'AbortError') failed.value = true; }
  finally { if (request === sequence) busy.value = false; }
}
function visibility() { if (document.hidden) clear(); else void refresh(); }
watch(() => [account.value?.id, accountDeletionRequested.value, offlineSession.value], () => { clear(); void refresh(); }, { flush: 'sync' });
onMounted(() => { document.addEventListener('visibilitychange', visibility); void refresh(); });
onBeforeUnmount(() => { clear(); document.removeEventListener('visibilitychange', visibility); });
</script>
<template>
  <section v-if="allowed" class="page-stack" aria-labelledby="customer-duels-title">
    <h2 id="customer-duels-title">{{ en ? 'Your battles' : 'Tvoje souboje' }}</h2>
    <p>{{ en ? 'Saved battles keep the characters, wands and spells used at the time.' : 'Uložené souboje zachovávají tehdejší postavy, hůlky a kouzla.' }}</p>
    <AwButton variant="secondary" :busy="busy" @click="refresh">{{ en ? 'Refresh battles' : 'Obnovit souboje' }}</AwButton>
    <AwNotice v-if="failed" tone="warning">{{ en ? 'Battle history could not be verified. Refresh to try again.' : 'Historii soubojů se nepodařilo ověřit. Zkus ji znovu obnovit.' }}</AwNotice>
    <p v-else-if="!busy && !items.length">{{ en ? 'No battles are saved yet. The event operator can invite two characters with completed wand assignments.' : 'Zatím tu není uložený souboj. Obsluha večera může pozvat dvě postavy s dokončeným výběrem hůlky.' }}</p>
    <ul v-else class="customer-duel-history"><li v-for="duel in items" :key="duel.id"><h3>{{ duel.left.displayName }} × {{ duel.right.displayName }}</h3><p>{{ duelWandName(duel.left) }} · {{ duelWandName(duel.right) }}</p><p>{{ en ? statesEn[duel.state] : duelStates[duel.state] }}</p><p v-if="duel.result">{{ duel.result.leftPoints }} : {{ duel.result.rightPoints }} · {{ duel.result.ending }}</p><RouterLink :to="`/archive/duel?account=1&duel=${duel.id}`">{{ en ? 'Open saved battle' : 'Otevřít uložený souboj' }}</RouterLink></li></ul>
  </section>
</template>
<style scoped>
.customer-duel-history { list-style: none; padding: 0; display: grid; gap: var(--aw-space-6); }
.customer-duel-history li { border-bottom: 1px solid var(--aw-border-subtle); padding-bottom: var(--aw-space-6); overflow-wrap: anywhere; }
</style>

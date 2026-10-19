<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { useRoute } from 'vue-router';
import { consumeDuelGrant } from '../../application/guestClaim';
import { GuestError, isUuid } from './client';
import { createGuestTransport } from './nativeGuest';
import { DuelClient } from './showClient';
import { duelActions, duelNarration, duelStates, type Duel, type DuelAccess, type DuelAction } from './showContracts';
import './archive.css';
const route = useRoute();
const client = new DuelClient(createGuestTransport());
const access = shallowRef<DuelAccess | null>(consumeDuelGrant());
const current = ref<Duel>();
const pending = ref(false); const refreshing = ref(false);
const message = ref('Načítáme souboj…');
const id = computed(() => typeof route.query.duel === 'string' && isUuid(route.query.duel) ? route.query.duel : '');
const side = computed(() => access.value?.side);
const locked = computed(() => side.value === 'left' ? current.value?.leftChoiceLocked : current.value?.rightChoiceLocked);
const choiceOpen = computed(() => current.value?.state === 'Running' && current.value.deadline !== null
  && ['choice', 'web_only_no_spoken_confirmation'].includes(current.value.narrationStatus));
const mayChoose = computed(() => !!access.value && current.value?.mode === 'interactive' && choiceOpen.value && !locked.value && !pending.value && !refreshing.value);
let timer: ReturnType<typeof setTimeout> | undefined;
let sequence = 0; let lastRevision = -1;
function accept(view: Duel) { if (view.revision >= lastRevision) { current.value = view; lastRevision = view.revision; } }
function invalidate(discardGrant = false) {
  sequence++; client.invalidate(); clearTimeout(timer); current.value = undefined; pending.value = false; refreshing.value = false;
  if (discardGrant) access.value = null;
}
async function refresh() {
  if (document.hidden || pending.value) return;
  clearTimeout(timer);
  if (!id.value) { invalidate(); message.value = 'Odkaz na souboj není platný.'; return; }
  const request = ++sequence; const check = client.capture(); refreshing.value = true;
  try {
    const view = await client.read(id.value); check(); if (request !== sequence) return;
    accept(view); message.value = !current.value ? 'Čekáme na aktuální revizi souboje.' : access.value ? duelStates[current.value.state] : 'Sleduješ veřejný průběh souboje. Tahy vyžadují soukromý přístup pro jednu stranu.';
  } catch (error) {
    if (request !== sequence || (error as Error).name === 'AbortError') return;
    current.value = undefined;
    message.value = error instanceof GuestError && [403, 404, 410].includes(error.status)
      ? 'Souboj není veřejně dostupný. Obsluha může ověřit zveřejnění obou identit.'
      : 'Spojení se přerušilo. Obnovujeme stav řízený serverem.';
  } finally {
    if (request === sequence) { refreshing.value = false; if (!document.hidden) timer = setTimeout(refresh, current.value && ['Completed', 'Cancelled', 'Interrupted'].includes(current.value.state) ? 10000 : 1500); }
  }
}
async function choose(action: DuelAction) {
  if (!mayChoose.value || !access.value || !current.value) return;
  clearTimeout(timer); sequence++; const request = sequence; const check = client.capture();
  pending.value = true; refreshing.value = false;
  try { const view = await client.choose(current.value, access.value, action); check(); if (request === sequence) { accept(view); message.value = 'Tah přijat. Stav kola potvrzuje server.'; } }
  catch (error) {
    if (request !== sequence || (error as Error).name === 'AbortError') return;
    if (error instanceof GuestError && [401, 403, 410].includes(error.status)) {
      access.value = null; message.value = 'Soubojový přístup není platný. Nový přístup poskytne obsluha.';
    } else message.value = error instanceof GuestError && error.status === 409 ? 'Stav kola se změnil. Obnovujeme platné možnosti.' : 'Potvrzení tahu nedorazilo. Stav ověř serverovým obnovením; tah automaticky neopakujeme.';
    current.value = undefined;
  } finally { if (request === sequence) { pending.value = false; await refresh(); } }
}
function hashChange() {
  const grant = consumeDuelGrant();
  invalidate(); access.value = grant; void refresh();
}
function visibility() { if (document.hidden) invalidate(); else void refresh(); }
function pageHide() { invalidate(true); }
function pageShow() { if (!document.hidden) void refresh(); }
watch(() => route.query.duel, () => { invalidate(true); lastRevision = -1; void refresh(); });
onMounted(() => {
  document.addEventListener('visibilitychange', visibility); window.addEventListener('pagehide', pageHide); window.addEventListener('pageshow', pageShow); window.addEventListener('hashchange', hashChange); void refresh();
});
onBeforeUnmount(() => { invalidate(true); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', pageHide); window.removeEventListener('pageshow', pageShow); window.removeEventListener('hashchange', hashChange); });
function points(participant: 'left' | 'right') { return current.value?.rounds.reduce((sum, round) => sum + (round.leftScore === round.rightScore ? .5 : (participant === 'left' ? round.leftScore > round.rightScore : round.rightScore > round.leftScore) ? 1 : 0), 0) ?? 0; }
</script>
<template>
  <section class="archive-view" aria-labelledby="duel-title">
    <div><h1 id="duel-title">Souboj kouzelnických identit</h1><p>Komediální fikce · body a průběh potvrzuje server. Rekvizity zůstávají v přihrádkách; nejde o šermování.</p></div>
    <div class="archive-actions"><RouterLink to="/archive">Hůlky večera</RouterLink><RouterLink to="/archive/chronicle">Večerní kronika</RouterLink><button class="aw-button aw-button--secondary" :disabled="pending || refreshing" @click="refresh">Obnovit souboj</button></div>
    <p class="aw-notice" role="status" aria-live="polite">{{ message }}</p>
    <template v-if="current">
      <p>{{ current.mode === 'exhibition' ? 'Fiktivní exhibice · ' : '' }}{{ duelStates[current.state] }}</p><p>{{ duelNarration(current) }}</p>
      <div class="archive-duel-sides"><section v-for="participant in (['left', 'right'] as const)" :key="participant" class="aw-card"><h2>{{ current[participant].displayName }}</h2><p>Digitální hůlka: {{ current[participant].wandId }}</p><p>Síla {{ current[participant].strength }} · Lest {{ current[participant].cunning }} · Obrana {{ current[participant].defense }}</p><p>{{ points(participant) }} b.</p></section></div>
      <section v-if="access && current.mode === 'interactive'" class="aw-card" aria-labelledby="duel-choice">
        <h2 id="duel-choice">Tvůj tah · {{ current[access.side].displayName }}</h2>
        <p>{{ locked ? 'Tah přijat a uzamčen. Protivníkův tah zůstává skrytý.' : choiceOpen ? `Kolo ${current.round} ze 3. Vyber jednu akci.` : duelStates[current.state] }}</p>
        <div class="archive-actions"><button v-for="(label, action) in duelActions" :key="action" class="aw-button" :disabled="!mayChoose" @click="choose(action)">{{ label }}</button></div>
        <p>Termín a uzamčení vyhodnocuje server. Přihlášení k účtu nenahrazuje přístup této strany.</p>
        <p>První přijatý tah je uzamčený. Při odpojení server po 15 sekundách použije předem připravený automatický tah.</p>
      </section>
      <ol class="archive-duel-rounds"><li v-for="round in current.rounds" :key="round.number"><h2>Kolo {{ round.number }} · {{ round.leftScore }} : {{ round.rightScore }}</h2><p>{{ duelActions[round.leftAction as DuelAction] ?? 'Tah' }}{{ round.leftAutomatic ? ' (automaticky)' : '' }} × {{ duelActions[round.rightAction as DuelAction] ?? 'Tah' }}{{ round.rightAutomatic ? ' (automaticky)' : '' }}</p><p>{{ current.commentary?.rounds.find(item => item.round === round.number)?.quip ?? round.quip }}</p><p>Vyprávěná nehoda bez vlivu na body: {{ current.commentary?.rounds.find(item => item.round === round.number)?.mishap ?? round.mishap }}</p></li></ol>
      <p v-if="current.commentary?.provider === 'openai'" class="archive-caption">Komentář vznikl pomocí OpenAI. Výpočet bodů je oddělený.</p>
      <section v-if="current.result" class="aw-card"><h2>Výsledek</h2><p v-if="current.result.exhibition">Fiktivní exhibice se nezapočítává jako výhra hosta.</p><p>{{ current.result.ending }} {{ current.result.leftPoints }} : {{ current.result.rightPoints }}</p></section>
    </template>
  </section>
</template>

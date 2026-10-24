<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { useRoute } from 'vue-router';
import { account, accountTransport, accountDeletionRequested, offlineSession } from '../identity/session';
import { consumeDuelGrant } from '../../application/guestClaim';
import { GuestError, isUuid } from './client';
import { createGuestTransport } from './nativeGuest';
import { DuelClient } from './showClient';
import { duelSpell, duelWandName, duelActions, duelNarration, duelStates, type Duel, type DuelAccess, type DuelAction } from './showContracts';
import './archive.css';
const route = useRoute();
const accountRead = computed(() => route.query.account === '1');
const guestTransport = createGuestTransport();
const client = new DuelClient((input, init) => accountRead.value ? accountTransport(String(input), init) : guestTransport(input, init), () => accountRead.value);
const access = shallowRef<DuelAccess | null>(consumeDuelGrant());
if (accountRead.value) access.value = null;
const current = ref<Duel>();
const consent = ref(false);
const now = ref(Date.now());
const countdown = computed(() => { const deadline = current.value?.deadline; return deadline && Number.isFinite(Date.parse(deadline)) ? Math.max(0, Math.ceil((Date.parse(deadline) - now.value) / 1000)) : null; });
const joined = computed(() => side.value === 'left' ? current.value?.leftJoined : current.value?.rightJoined);
const pending = ref(false); const refreshing = ref(false);
const message = ref('Načítáme souboj…');
const id = computed(() => typeof route.query.duel === 'string' && isUuid(route.query.duel) ? route.query.duel : '');
const side = computed(() => access.value?.side);
const locked = computed(() => side.value === 'left' ? current.value?.leftChoiceLocked : current.value?.rightChoiceLocked);
const choiceOpen = computed(() => current.value?.state === 'Running' && current.value.deadline !== null
  && ['choice', 'web_only_no_spoken_confirmation'].includes(current.value.narrationStatus));
const mayChoose = computed(() => !!access.value && current.value?.mode === 'interactive' && choiceOpen.value && !locked.value && (!current.value.requiresParticipantConsent || joined.value) && !pending.value && !refreshing.value);
let timer: ReturnType<typeof setTimeout> | undefined;
let countdownTimer: ReturnType<typeof setInterval> | undefined;
let sequence = 0; let lastRevision = -1;
function accept(view: Duel) { if (view.revision >= lastRevision) { current.value = view; lastRevision = view.revision; } }
function invalidate(discardGrant = false) {
  sequence++; client.invalidate(); clearTimeout(timer); current.value = undefined; pending.value = false; refreshing.value = false; consent.value = false;
  if (discardGrant) access.value = null;
}
async function refresh() {
  if (document.hidden || pending.value) return;
  if (accountRead.value && (!account.value || accountDeletionRequested.value || offlineSession.value)) { invalidate(true); message.value = 'Přihlas se ke svému účtu a připoj se k síti pro ověření historie soubojů.'; return; }
  clearTimeout(timer);
  if (!id.value) { invalidate(); message.value = 'Odkaz na souboj není platný.'; return; }
  const request = ++sequence; const check = client.capture(); refreshing.value = true;
  try {
    const view = await client.read(id.value); check(); if (request !== sequence) return;
    accept(view); message.value = !current.value ? 'Čekáme na aktuální revizi souboje.' : accountRead.value || access.value ? duelStates[current.value.state] : 'Sleduješ veřejný průběh souboje. Tahy vyžadují soukromý přístup pro jednu stranu.';
  } catch (error) {
    if (request !== sequence || (error as Error).name === 'AbortError') return;
    current.value = undefined;
    message.value = error instanceof GuestError && [403, 404, 410].includes(error.status)
      ? accountRead.value ? 'Uložený souboj není pro tento účet dostupný. Obnov přístup nebo se vrať k památkám.' : 'Souboj není veřejně dostupný. Obsluha může ověřit zveřejnění obou identit.'
      : 'Spojení se přerušilo. Obnovujeme stav řízený serverem.';
  } finally {
    if (request === sequence) { refreshing.value = false; if (!document.hidden) timer = setTimeout(refresh, current.value && ['Completed', 'Cancelled', 'Interrupted'].includes(current.value.state) ? 10000 : 1500); }
  }
}
async function join() {
  if (!consent.value || !access.value || !current.value || pending.value || refreshing.value) return;
  clearTimeout(timer); const request = ++sequence; const check = client.capture(); pending.value = true;
  try { const view = await client.join(current.value, access.value); check(); if (request === sequence) { accept(view); consent.value = false; message.value = 'Účast potvrzena. Čekáme na druhého soupeře a spuštění obsluhou.'; } }
  catch (error) {
    if (request !== sequence || (error as Error).name === 'AbortError') return;
    if (error instanceof GuestError && [401, 403, 410].includes(error.status)) access.value = null;
    message.value = 'Účast se nepodařilo potvrdit. Obnovujeme platný stav souboje.';
  } finally { if (request === sequence) { pending.value = false; await refresh(); } }
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
  const incoming = consumeDuelGrant();
  const grant = accountRead.value ? null : incoming;
  invalidate(); access.value = grant; void refresh();
}
function visibility() { if (document.hidden) invalidate(); else void refresh(); }
function pageHide() { invalidate(true); }
function pageShow() { if (!document.hidden) void refresh(); }
watch(() => [route.query.duel, route.query.account, accountRead.value ? account.value?.id : null, accountRead.value ? accountDeletionRequested.value : false, accountRead.value ? offlineSession.value : false], () => { invalidate(true); lastRevision = -1; void refresh(); }, { flush: 'sync' });
onMounted(() => {
  countdownTimer = setInterval(() => { now.value = Date.now(); }, 250);
  document.addEventListener('visibilitychange', visibility); window.addEventListener('pagehide', pageHide); window.addEventListener('pageshow', pageShow); window.addEventListener('hashchange', hashChange); void refresh();
});
onBeforeUnmount(() => { clearInterval(countdownTimer); invalidate(true); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', pageHide); window.removeEventListener('pageshow', pageShow); window.removeEventListener('hashchange', hashChange); });
function points(participant: 'left' | 'right') { return current.value?.rounds.reduce((sum, round) => sum + (round.leftScore === round.rightScore ? .5 : (participant === 'left' ? round.leftScore > round.rightScore : round.rightScore > round.leftScore) ? 1 : 0), 0) ?? 0; }
</script>
<template>
  <section class="archive-view" aria-labelledby="duel-title">
    <div><h1 id="duel-title">Souboj postav</h1><p v-if="accountRead">Uložená historie tvého účtu · <RouterLink to="/me/keepsakes">Zpět k památkám</RouterLink></p><p>Komediální fikce · body a průběh potvrzuje server. Rekvizity zůstávají v přihrádkách; nejde o šermování.</p></div>
    <div class="archive-actions"><RouterLink to="/archive">Hůlky večera</RouterLink><RouterLink to="/archive/chronicle">Večerní kronika</RouterLink><button class="aw-button aw-button--secondary" :disabled="pending || refreshing" @click="refresh">Obnovit souboj</button></div>
    <p class="aw-notice" role="status" aria-live="polite">{{ message }}</p>
    <template v-if="current">
      <p>{{ duelNarration(current) }}</p><p v-if="current.mode === 'exhibition'">Fiktivní exhibice</p>
      <section v-if="access && current.requiresParticipantConsent && !joined && current.state === 'AwaitingParticipants'" class="aw-card" aria-labelledby="duel-join">
        <h2 id="duel-join">Připojit se · {{ current[access.side].displayName }}</h2><p>Tvá hůlka: {{ duelWandName(current[access.side]) }}. Ve třech kolech si vybereš vlastní kouzla. Výsledek a veřejná fikce souboje se uloží.</p>
        <label><input v-model="consent" type="checkbox" :disabled="pending"> Souhlasím s účastí v tomto souboji.</label><button class="aw-button" :disabled="!consent || pending || refreshing" @click="join">Potvrdit účast</button>
      </section>
      <p v-if="choiceOpen && countdown !== null" class="archive-duel-countdown" role="timer" :aria-label="`Do uzavření kola zbývá ${countdown} sekund`">Kolo {{ current.round }} ze 3 · <span class="aw-mono">{{ countdown }} s</span><span v-if="countdown === 0"> · Ověřujeme výsledek na serveru…</span></p>
      <section v-if="access && current.mode === 'interactive' && !['Completed', 'Cancelled', 'Interrupted'].includes(current.state)" class="aw-card" aria-labelledby="duel-choice">
        <h2 id="duel-choice">Tvůj tah · {{ current[access.side].displayName }}</h2>
        <p>{{ locked ? 'Tah přijat a uzamčen. Protivníkův tah zůstává skrytý.' : choiceOpen ? `Kolo ${current.round} ze 3. Vyber jednu akci.` : duelStates[current.state] }}</p>
        <div class="archive-duel-actions"><button v-for="(label, action) in duelActions" :key="action" class="aw-button" :disabled="!mayChoose" @click="choose(action)"><strong>{{ duelSpell(current[access.side], action) }}</strong><span v-if="current[access.side].spells">{{ label }}</span></button></div>
        <p>Termín a uzamčení vyhodnocuje server. Přihlášení k účtu nenahrazuje přístup této strany.</p>
        <p>První přijatý tah je uzamčený. Při odpojení server po 15 sekundách použije předem připravený automatický tah.</p>
      </section>
      <div class="archive-duel-sides"><section v-for="participant in (['left', 'right'] as const)" :key="participant" class="aw-card" :aria-label="current[participant].displayName">
        <h2>{{ current[participant].displayName }}</h2><p class="archive-duel-wand">{{ duelWandName(current[participant]) }}</p>
        <p v-if="!current[participant].wandName" class="archive-caption">Původní katalogová identita: {{ current[participant].wandId }}</p>
        <p>Síla {{ current[participant].strength }} · Lest {{ current[participant].cunning }} · Obrana {{ current[participant].defense }}</p>
        <details v-if="current[participant].spells"><summary>Tři kouzla hůlky</summary><ul class="archive-wand-spells"><li v-for="spell in current[participant].spells" :key="spell.action"><strong>{{ spell.name }}</strong><span>{{ duelActions[spell.action] }} · {{ spell.description }}</span></li></ul></details>
        <p class="archive-duel-points">{{ points(participant) }} b.</p>
        <p v-if="current.requiresParticipantConsent">{{ current[participant === 'left' ? 'leftJoined' : 'rightJoined'] ? 'Účast potvrzena' : 'Čekáme na potvrzení účasti' }}</p>
      </section></div>
      <section v-if="current.result" class="aw-card"><h2>Výsledek</h2><p v-if="current.result.exhibition">Fiktivní exhibice se nezapočítává jako výhra hosta.</p><p>{{ current.result.ending }} {{ current.result.leftPoints }} : {{ current.result.rightPoints }}</p></section>
      <ol class="archive-duel-rounds"><li v-for="round in current.rounds" :key="round.number" class="archive-duel-reveal" :data-effect="round.leftAction"><svg class="archive-spell-effect" viewBox="0 0 180 32" aria-hidden="true" focusable="false"><path v-if="round.leftAction === 'shield'" d="M65 5 Q90 2 115 5 L110 18 Q90 32 70 18 Z" /><path v-else-if="round.leftAction === 'trick'" d="M5 16 Q35 -3 65 16 T125 16 T175 16" /><path v-else d="M5 24 L75 10 L68 22 L175 7" /></svg><h2>Kolo {{ round.number }} · {{ round.leftScore }} : {{ round.rightScore }}</h2><p>{{ duelSpell(current.left, round.leftAction) }}{{ round.leftAutomatic ? ' (automaticky)' : '' }} × {{ duelSpell(current.right, round.rightAction) }}{{ round.rightAutomatic ? ' (automaticky)' : '' }}</p><p class="archive-caption">Webová animace kouzel · neoznačuje fyzický efekt kufru.</p><p>{{ current.commentary?.rounds.find(item => item.round === round.number)?.quip ?? round.quip }}</p><p>Vyprávěná nehoda bez vlivu na body: {{ current.commentary?.rounds.find(item => item.round === round.number)?.mishap ?? round.mishap }}</p></li></ol>
      <p v-if="current.commentary?.provider === 'openai'" class="archive-caption">Komentář vznikl pomocí OpenAI. Výpočet bodů je oddělený.</p>

    </template>
  </section>
</template>

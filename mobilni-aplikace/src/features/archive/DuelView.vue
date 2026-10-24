<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { useRoute } from 'vue-router';
import { account, accountTransport, accountDeletionRequested, offlineSession } from '../identity/session';
import { consumeDuelGrant } from '../../application/guestClaim';
import { GuestError, isUuid } from './client';
import { createGuestTransport } from './nativeGuest';
import { DuelClient } from './showClient';
import { duelSpell, duelWandName, duelActions, duelNarration, duelStates, type Duel, type DuelAccess, type DuelAction } from './showContracts';
import './archive.css';
import SpellGlyph from './SpellGlyph.vue';
import { createBattleSummary } from './battleSummary';
import { Capacitor } from '@capacitor/core';
import { shareNativeGuestFile } from './nativeGuest';
import { combatSpecialty } from './generatedWand';
import { useArchiveMedia } from './useArchiveMedia';
import { ArchiveClient } from './client';
const route = useRoute();
const accountRead = computed(() => route.query.account === '1');
const guestTransport = createGuestTransport();
const client = new DuelClient((input, init) => accountRead.value ? accountTransport(String(input), init) : guestTransport(input, init), () => accountRead.value);
const access = shallowRef<DuelAccess | null>(consumeDuelGrant());
if (accountRead.value) access.value = null;
const current = ref<Duel>();
const imageClient = new ArchiveClient(guestTransport);
const leftArt = useArchiveMedia(imageClient); const rightArt = useArchiveMedia(imageClient);
const arts = { left: leftArt, right: rightArt };
watch(() => [current.value?.left.artworkUrl, current.value?.right.artworkUrl], (paths, previous) => {
  if (previous && paths.some((path, index) => path !== previous[index])) clearExports();
  for (const [index, participant] of (['left', 'right'] as const).entries()) {
    if (paths[index] !== previous?.[index]) void arts[participant].artwork(paths[index], true);
  }
}, { flush: 'sync' });
const winner = computed(() => current.value?.result ? current.value.result.leftPoints === current.value.result.rightPoints ? 'draw' : current.value.result.leftPoints > current.value.result.rightPoints ? 'left' : 'right' : null);
const resultTitle = computed(() => winner.value === 'draw' ? 'Remíza. Magie odmítá rozhodnout.' : winner.value ? `${current.value![winner.value].displayName} vítězí!` : '');
const resultTone = computed(() => winner.value === 'draw' ? 'draw' : side.value ? winner.value === side.value ? 'victory' : 'defeat' : 'victory');
const opponentReady = computed(() => side.value === 'left' ? current.value?.rightChoiceLocked : current.value?.leftChoiceLocked);
const actionDescriptions: Record<DuelAction, string> = { strike: 'Přímý úder prorazí lest. Štít jej zastaví.', trick: 'Klam obejde štít. Přímému úderu podlehne.', shield: 'Štít zastaví úder. Lest si najde cestu.' };
function spellDescription(action: DuelAction) { return current.value && side.value ? current.value[side.value].spells?.find(spell => spell.action === action)?.description : ''; }
function roundWinner(leftScore: number, rightScore: number) { return leftScore === rightScore ? 'Vyrovnané kolo' : `Kolo získává ${current.value![leftScore > rightScore ? 'left' : 'right'].displayName}`; }
const exportedSummary = ref('');
let exportGeneration = 0;
const exportedCard = ref(''); const cardBusy = ref(false); const cardError = ref(false); const summaryBusy = ref(false); const summaryError = ref(false);
function clearExports() {
  exportGeneration++;
  if (exportedSummary.value) URL.revokeObjectURL(exportedSummary.value); exportedSummary.value = '';
  if (exportedCard.value) URL.revokeObjectURL(exportedCard.value); exportedCard.value = '';
}

async function exportCard() {
  if (!current.value?.result || cardBusy.value) return;
  const check = client.capture(); cardBusy.value = true; cardError.value = false;
  const temporary: string[] = [];
  try {
    // Publication may have been withdrawn since the last poll. Recheck the view and both assets on this explicit action.
    const view = await client.read(id.value); check(); accept(view);
    if (!view.result) throw new Error('Battle is not complete');
    const generation = exportGeneration;
    const requireCurrentExport = () => { check(); if (generation !== exportGeneration) throw new DOMException('Publication changed', 'AbortError'); };
    const artwork = { left: '', right: '' };
    for (const participant of (['left', 'right'] as const)) {
      const path = view[participant].artworkUrl; if (!path) continue;
      const blob = await imageClient.blob(path, ['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml'], true); requireCurrentExport();
      artwork[participant] = URL.createObjectURL(blob); temporary.push(artwork[participant]);
    }
    const style = getComputedStyle(document.documentElement);
    const color = (token: string) => style.getPropertyValue(token).trim();
    const blob = await createBattleSummary(view, artwork, { background: color('--aw-void'), surface: color('--aw-surface'), text: color('--aw-ivory'), muted: color('--aw-muted'), accent: color('--aw-accent') });
    requireCurrentExport();
    if (Capacitor.isNativePlatform()) await shareNativeGuestFile(new File([blob], 'dildovander-souboj.png', { type: 'image/png' }), requireCurrentExport);
    else { if (exportedCard.value) URL.revokeObjectURL(exportedCard.value); exportedCard.value = URL.createObjectURL(blob); }
  } catch (error) { if ((error as Error).name !== 'AbortError') { clearExports(); cardError.value = true; } }
  finally { for (const url of temporary) URL.revokeObjectURL(url); cardBusy.value = false; }
}

async function exportSummary() {
  if (!current.value?.result || summaryBusy.value) return;
  const check = client.capture(); summaryBusy.value = true; summaryError.value = false;
  try {
    const view = await client.read(id.value); check(); accept(view); if (!view.result) throw new Error('Battle is not complete');
    // Public fiction only: no grants, private URLs, account IDs or artwork capabilities.
    const ending = view.result.leftPoints === view.result.rightPoints ? 'Remíza' : `${view[view.result.leftPoints > view.result.rightPoints ? 'left' : 'right'].displayName} vítězí!`;
    const lines = ['DildoVander · Souboj postav', `${view.left.displayName} — ${duelWandName(view.left)}`, `${view.right.displayName} — ${duelWandName(view.right)}`, ending, `${view.result.leftPoints} : ${view.result.rightPoints}`, view.result.ending,
      ...view.rounds.map(round => `Kolo ${round.number}: ${duelSpell(view.left, round.leftAction)} × ${duelSpell(view.right, round.rightAction)} · ${round.leftScore} : ${round.rightScore}\n${view.commentary?.rounds.find(item => item.round === round.number)?.quip ?? round.quip}`)];
    if (exportedSummary.value) URL.revokeObjectURL(exportedSummary.value);
    exportedSummary.value = URL.createObjectURL(new Blob([lines.join('\n\n')], { type: 'text/plain;charset=utf-8' }));
  } catch (error) { if ((error as Error).name !== 'AbortError') { clearExports(); summaryError.value = true; } }
  finally { summaryBusy.value = false; }
}

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
function accept(view: Duel) {
  if (view.revision < lastRevision) return;
  const enteringChoices = view.state === 'Running' && current.value?.state !== 'Running' && !!access.value;
  current.value = view; lastRevision = view.revision;
  if (enteringChoices) void nextTick(() => {
    if (document.hidden || current.value?.state !== 'Running') return;
    const panel = document.querySelector<HTMLElement>('.archive-duel-choice');
    panel?.scrollIntoView?.({ block: 'start', behavior: 'auto' });
  });
}
function invalidate(discardGrant = false) {
  sequence++; client.invalidate(); imageClient.invalidate(); leftArt.clear(); rightArt.clear(); clearExports(); cardError.value = false; summaryError.value = false; clearTimeout(timer); current.value = undefined; pending.value = false; refreshing.value = false; consent.value = false;
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
  <section class="archive-view archive-duel-view" :data-state="current?.state" aria-labelledby="duel-title">
    <div class="archive-duel-intro"><h1 id="duel-title">Souboj postav</h1><p v-if="accountRead">Uložená historie tvého účtu · <RouterLink to="/me/keepsakes">Zpět k památkám</RouterLink></p><p v-if="current?.state !== 'Running'">Komediální fikce · body a průběh potvrzuje server. Rekvizity zůstávají v přihrádkách; nejde o šermování.</p></div>
    <div class="archive-actions archive-duel-toolbar"><RouterLink to="/archive">Hůlky večera</RouterLink><RouterLink to="/archive/chronicle">Večerní kronika</RouterLink><button class="aw-button aw-button--secondary" :disabled="pending || refreshing" @click="refresh">Obnovit souboj</button></div>
    <p class="aw-notice" role="status" aria-live="polite">{{ message }}</p>
    <template v-if="current">
      <p>{{ duelNarration(current) }}</p><p v-if="current.mode === 'exhibition'">Fiktivní exhibice</p>
      <section class="archive-duel-arena archive-duel-sides" aria-label="Soupeři a jejich hůlky" :data-result="current.result ? resultTone : undefined">
        <article v-for="participant in (['left', 'right'] as const)" :key="participant" class="archive-duelist" :data-side="participant" :data-winner="winner === participant">
          <div class="archive-duelist-heading"><span class="archive-caption">{{ side === participant ? 'Tvoje postava' : side ? 'Tvůj soupeř' : participant === 'left' ? 'První vyzyvatel' : 'Druhý vyzyvatel' }}</span><h2>{{ current[participant].displayName }}</h2></div>
          <div class="archive-duelist-art"><img v-if="arts[participant].artworkUrl.value" :src="arts[participant].artworkUrl.value" :alt="`Digitální ilustrace hůlky ${duelWandName(current[participant])}`" @error="arts[participant].clearArtwork(); arts[participant].artworkError.value = true" /><div v-else class="archive-duelist-no-art"><span aria-hidden="true">✦</span><p>{{ arts[participant].artworkError.value ? 'Ilustrace není dostupná' : current[participant].artworkUrl ? 'Načítáme ilustraci…' : 'Hůlka bez zveřejněné ilustrace' }}</p></div></div>
          <p v-if="current[participant].artworkSynthetic" class="archive-caption">Syntetická testovací ilustrace</p><h3 class="archive-duel-wand">{{ duelWandName(current[participant]) }}</h3>
          <p class="archive-wand-specialty">{{ combatSpecialty(current[participant].combatArchetype) }}</p>
          <p v-if="current[participant].temperament" class="archive-caption">{{ current[participant].temperament }}</p>
          <dl class="archive-combat-stats"><div><dt>Síla</dt><dd>{{ current[participant].strength }}<meter min="0" max="5" :value="current[participant].strength" :aria-label="`Síla ${current[participant].strength}`" /></dd></div><div><dt>Lest</dt><dd>{{ current[participant].cunning }}<meter min="0" max="5" :value="current[participant].cunning" :aria-label="`Lest ${current[participant].cunning}`" /></dd></div><div><dt>Obrana</dt><dd>{{ current[participant].defense }}<meter min="0" max="5" :value="current[participant].defense" :aria-label="`Obrana ${current[participant].defense}`" /></dd></div></dl>
          <p v-if="current[participant].affinity" class="archive-caption">Zaměření: {{ current[participant].affinity }}<span v-if="current[participant].weakness"> · Slabina: {{ current[participant].weakness }}</span></p>
          <details v-if="current[participant].spells"><summary>Tři kouzla hůlky</summary><ul class="archive-wand-spells"><li v-for="spell in current[participant].spells" :key="spell.action"><strong>{{ spell.name }}</strong><span>{{ duelActions[spell.action] }} · {{ spell.description }}</span></li></ul></details>
          <div class="archive-duelist-footer"><strong class="archive-duel-points">{{ points(participant) }} <span>b.</span></strong><span v-if="current.requiresParticipantConsent">{{ current[participant === 'left' ? 'leftJoined' : 'rightJoined'] ? 'Účast potvrzena' : 'Čekáme na účast' }}</span></div>
        </article>
        <div class="archive-duel-vs" aria-hidden="true"><span>VS</span><small>{{ current.result ? 'Finále' : `Kolo ${current.round} / 3` }}</small></div>
      </section>
      <section v-if="access && current.requiresParticipantConsent && !joined && current.state === 'AwaitingParticipants'" class="aw-card" aria-labelledby="duel-join">
        <h2 id="duel-join">Připojit se · {{ current[access.side].displayName }}</h2><p>Tvá hůlka: {{ duelWandName(current[access.side]) }}. Ve třech kolech si vybereš vlastní kouzla. Výsledek a veřejná fikce souboje se uloží.</p>
        <label><input v-model="consent" type="checkbox" :disabled="pending"> Souhlasím s účastí v tomto souboji.</label><button class="aw-button" :disabled="!consent || pending || refreshing" @click="join">Potvrdit účast</button>
      </section>
      <section v-if="current.result" class="archive-duel-result" :data-result="resultTone" aria-labelledby="duel-result-title">
        <p class="archive-wand-kicker">{{ resultTone === 'victory' ? 'Sláva má nového majitele' : resultTone === 'defeat' ? 'I prohra potřebuje publikum' : 'Dva mágové. Jedna nerozhodnost.' }}</p>
        <h2 id="duel-result-title">Výsledek</h2><p class="archive-duel-verdict">{{ resultTitle }}</p><p class="archive-duel-final-score">{{ current.result.leftPoints }} <span>:</span> {{ current.result.rightPoints }}</p>
        <p v-if="current.result.exhibition">Fiktivní exhibice se nezapočítává jako výhra hosta.</p><p class="archive-wand-lore">{{ current.result.ending }}</p>
        <p class="archive-caption">Uložený souboj · tři kola a jejich kouzla najdeš níže.</p>
        <div class="archive-actions"><button class="aw-button" :disabled="cardBusy" @click="exportCard">{{ cardBusy ? 'Připravujeme kartu…' : 'Uložit kartu souboje PNG' }}</button><a v-if="exportedCard" class="aw-button" :href="exportedCard" download="dildovander-souboj.png">Stáhnout kartu PNG</a><button class="aw-button aw-button--secondary" :disabled="summaryBusy" @click="exportSummary">Připravit shrnutí ke stažení</button><a v-if="exportedSummary" class="aw-button" :href="exportedSummary" download="dildovander-souboj.txt">Stáhnout shrnutí</a><RouterLink v-if="accountRead" to="/me/keepsakes">Historie soubojů</RouterLink></div><p v-if="cardError || summaryError" role="alert">Souhrn se nepodařilo připravit. Obnov platný stav souboje.</p>
      </section>
      <section v-if="access && current.mode === 'interactive' && !['Completed', 'Cancelled', 'Interrupted'].includes(current.state)" class="archive-duel-choice" aria-labelledby="duel-choice">
        <div class="archive-duel-choice-heading"><div><p class="archive-wand-kicker">{{ locked ? 'Kouzlo je uzamčené' : 'Magie čeká na tvé rozhodnutí' }}</p><h2 id="duel-choice">Tvůj tah · {{ current[access.side].displayName }}</h2></div>
          <p v-if="choiceOpen && countdown !== null" class="archive-duel-countdown" role="timer" :aria-label="`Do uzavření kola zbývá ${countdown} sekund`"><span class="aw-mono">{{ countdown }} <small>s</small></span><span v-if="countdown === 0">Ověřujeme výsledek…</span><span v-else>Kolo {{ current.round }} ze 3</span></p>
        </div>
        <p role="status">{{ locked ? 'Tah přijat a uzamčen. Protivníkův tah zůstává skrytý.' : choiceOpen ? `Kolo ${current.round} ze 3. Vyber jednu akci.` : duelStates[current.state] }}</p>
        <div class="archive-duel-readiness" aria-live="polite"><span :data-ready="locked">{{ locked ? 'Tvé kouzlo připraveno' : 'Vybíráš kouzlo' }}</span><span :data-ready="opponentReady">{{ opponentReady ? 'Soupeř je připraven' : 'Soupeř vybírá kouzlo' }}</span></div>
        <div class="archive-duel-actions"><button v-for="(label, action) in duelActions" :key="action" class="aw-button archive-spell-button" :data-spell="action" :disabled="!mayChoose" :aria-label="`${duelSpell(current[access.side], action)}${current[access.side].spells ? ` ${label}` : ''}`" :aria-describedby="`spell-description-${action}`" @click="choose(action)"><SpellGlyph :action="action" /><span class="archive-spell-kind">{{ label }}</span><strong>{{ duelSpell(current[access.side], action) }}</strong><span v-if="spellDescription(action)">{{ spellDescription(action) }}</span><small class="archive-spell-tactics" :id="`spell-description-${action}`">{{ actionDescriptions[action] }}</small></button></div>
        <p v-if="current.rulesVersion === 'duel-personalized-v3'" class="archive-caption">Volba přebije výhodu stylu: úder porazí lest, lest obejde štít, štít zastaví úder.</p>
        <details class="archive-caption"><summary>Jak se kolo uzavírá</summary><p>Termín a uzamčení vyhodnocuje server. První přijatý tah je uzamčený. Obě kouzla se odhalí současně až po uzavření kola. Při odpojení server po 15 sekundách použije předem připravený automatický tah. Přihlášení k účtu nenahrazuje přístup této strany.</p></details>
      </section>
      <p v-else-if="choiceOpen && countdown !== null" class="archive-duel-countdown" role="timer" :aria-label="`Do uzavření kola zbývá ${countdown} sekund`">Kolo {{ current.round }} ze 3 · <span class="aw-mono">{{ countdown }} s</span></p>
      <ol class="archive-duel-rounds" aria-label="Odhalená kola"><li v-for="round in current.rounds" :key="round.number" class="archive-duel-reveal" :data-effect="round.leftAction">
        <div class="archive-round-heading"><h2>Kolo {{ round.number }} · {{ round.leftScore }} : {{ round.rightScore }}</h2><strong>{{ roundWinner(round.leftScore, round.rightScore) }}</strong></div>
        <div class="archive-spell-clash"><div v-for="participant in (['left', 'right'] as const)" :key="participant" class="archive-revealed-spell" :data-spell="participant === 'left' ? round.leftAction : round.rightAction"><SpellGlyph :action="participant === 'left' ? round.leftAction : round.rightAction" /><span class="archive-caption">{{ current[participant].displayName }}</span><strong>{{ duelSpell(current[participant], participant === 'left' ? round.leftAction : round.rightAction) }}</strong><small v-if="participant === 'left' ? round.leftAutomatic : round.rightAutomatic">Automatický tah</small></div><span class="archive-clash-mark" aria-hidden="true">×</span></div>
        <p class="archive-round-quip">{{ current.commentary?.rounds.find(item => item.round === round.number)?.quip ?? round.quip }}</p><p class="archive-caption">{{ current.commentary?.rounds.find(item => item.round === round.number)?.mishap ?? round.mishap }}</p>
      </li></ol>
      <p v-if="current.commentary?.provider === 'openai'" class="archive-caption">Komentář vznikl pomocí OpenAI. Výpočet bodů je oddělený.</p>

    </template>
  </section>
</template>

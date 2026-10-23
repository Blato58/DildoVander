<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { Capacitor } from '@capacitor/core';
import { account, accountDeletionRequested, accountRequest } from '../features/identity/session';
import { deviceSession } from '../features/suitcase/deviceSession';
import { useRoute } from 'vue-router';
import { canOperateSuitcase, isCustomerAccount } from './access';
import AwNotice from '../components/AwNotice.vue';
import CustomerJourneyCard from '../features/customer/CustomerJourneyCard.vue';
import { SessionRecoveryController, type SessionRecoveryState } from '../features/customer/sessionRecovery';
import type { CustomerJourneyInput } from '../features/customer/customerJourney';
import { projectCustomerResult, type CustomerResultProjection } from '../features/customer/customerResult';

const route = useRoute();
const canOperate = computed(() => canOperateSuitcase(account.value));
const isCustomer = computed(() => isCustomerAccount(account.value) && !accountDeletionRequested.value);
const source = computed(() => deviceSession.target.value);
const browserMode = !Capacitor.isNativePlatform();
const greeting = computed(() => account.value ? `Vítejte, ${account.value.displayName}` : 'Kufr plný příběhů.');
const evening = ref<{ label: string; latest?: string } | null>(null);
const eveningState = ref('Načítám přehled večera…');
const eveningController = new AbortController();
interface CustomerProfileSnapshot { profile: { status: string } | null; sessionState: string | null; activeSession: boolean; latestResult: CustomerResultProjection | null }
interface CustomerSessionsResponse { items: unknown[] }
const terminalSessionStates = new Set(['completed', 'failed']);
const journeyProfile = ref<CustomerProfileSnapshot['profile']>(null);
const journeySessionState = ref<string | null>(null);
const journeyResult = ref<CustomerResultProjection | null>(null);
const journeyLoading = ref(false);
const journeyStale = ref(false);
const recoveryState = reactive<SessionRecoveryState>({
  refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined,
});
const journeyRecovery = new SessionRecoveryController<CustomerProfileSnapshot>({
  state: recoveryState,
  pollIntervalMs: 2500,
  retryBaseDelayMs: 1000,
  retryMaxDelayMs: 30_000,
  refresh: async signal => {
    const [character, sessions] = await Promise.all([
      accountRequest<{ profile: unknown }>('/app/v1/customer/character', 'GET', undefined, signal),
      accountRequest<CustomerSessionsResponse>('/app/v1/customer/sessions', 'GET', undefined, signal),
    ]);
    const profile = parseCustomerProfile(character.profile);
    const sessionState = parseSessionState(sessions);
    const activeSession = sessionState !== null && !terminalSessionStates.has(sessionState);
    // The account endpoint returns newest sessions first. Resolve its exact
    // completed session through the server binding; archive associations are not outcomes.
    const completed = !activeSession ? sessions.items.find(item =>
      !!item && typeof item === 'object' && (item as { state?: unknown }).state === 'completed') : undefined;
    const sessionId = (completed as { id?: unknown } | undefined)?.id;
    if (completed && (typeof sessionId !== 'string' || !sessionId)) throw new Error('Neplatná identita rozhovoru.');
    const latestResult = typeof sessionId === 'string'
      ? projectCustomerResult(await accountRequest<unknown>(`/app/v1/customer/sessions/${encodeURIComponent(sessionId)}/result`, 'GET', undefined, signal), sessionId)
      : null;
    return {
      profile,
      sessionState,
      activeSession,
      latestResult,
    };
  },
  hasActiveSession: snapshot => snapshot.activeSession || snapshot.latestResult?.shouldPoll === true,
  onValue: snapshot => {
    journeyProfile.value = snapshot.profile;
    journeySessionState.value = snapshot.sessionState;
    journeyResult.value = snapshot.latestResult;
    journeyLoading.value = false;
    journeyStale.value = false;
  },
});
const customerJourney = computed<CustomerJourneyInput>(() => ({
  account: isCustomer.value ? account.value : null,
  profile: journeyProfile.value,
  sessionState: journeySessionState.value,
  loading: journeyLoading.value || recoveryState.refreshing,
  error: Boolean(recoveryState.error) || journeyResult.value?.valid === false,
  stale: journeyStale.value || recoveryState.stale,
  latestAuthorizedResult: journeyResult.value?.locator ? { locator: journeyResult.value.locator } : null,
  resultPending: journeyResult.value?.valid === true && !journeyResult.value.route && journeyResult.value.shouldPoll,
}));
const pageMounted = ref(false);
watch(() => recoveryState.refreshing, refreshing => {
  if (!refreshing) journeyLoading.value = false;
});

watch(
  [() => account.value?.id, () => isCustomerAccount(account.value), accountDeletionRequested],
  () => {
    journeyRecovery.invalidate();
    journeyProfile.value = null;
    journeySessionState.value = null;
    journeyResult.value = null;
    journeyLoading.value = isCustomer.value;
    journeyStale.value = isCustomer.value;
    if (isCustomer.value && pageMounted.value) refreshCustomerJourney();
  },
  { flush: 'sync', immediate: true },
);

function refreshCustomerJourney(force = false): void {
  if (!isCustomer.value) {
    journeyRecovery.setActive(false);
    journeyLoading.value = false;
    return;
  }
  if (!pageMounted.value || document.visibilityState === 'hidden' || navigator.onLine === false) {
    journeyRecovery.setActive(false);
    journeyLoading.value = false;
    journeyStale.value = true;
    return;
  }
  if (force) journeyRecovery.setActive(false);
  journeyLoading.value = true;
  void journeyRecovery.setActive(true);
}

function onVisibilityChange(): void {
  refreshCustomerJourney(document.visibilityState !== 'hidden');
}

function onOffline(): void {
  if (!isCustomer.value) return;
  journeyRecovery.setActive(false);
  journeyLoading.value = false;
  journeyStale.value = true;
}

function onOnline(): void {
  refreshCustomerJourney(true);
}

function parseCustomerProfile(value: unknown): CustomerProfileSnapshot['profile'] {
  if (value === null) return null;
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('Neplatný profil zákazníka.');
  const status = (value as { status?: unknown }).status;
  if (typeof status !== 'string' || !status) throw new Error('Neplatný stav profilu zákazníka.');
  return { status };
}

function parseSessionState(value: CustomerSessionsResponse): string | null {
  if (!value || !Array.isArray(value.items)) throw new Error('Neplatný seznam rozhovorů.');
  const states = value.items.map(item => {
    if (!item || typeof item !== 'object' || Array.isArray(item)) throw new Error('Neplatný stav rozhovoru.');
    const state = (item as { state?: unknown }).state;
    if (typeof state !== 'string' || !state) throw new Error('Neplatný stav rozhovoru.');
    return state;
  });
  return states.find(state => !terminalSessionStates.has(state)) ?? states[0] ?? null;
}

onMounted(async () => {
  pageMounted.value = true;
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onOnline);
  window.addEventListener('offline', onOffline);
  refreshCustomerJourney();
  try {
    const response = await fetch('/guest/v1/evening?limit=1', { credentials: 'omit', cache: 'no-store', redirect: 'error', signal: eveningController.signal });
    if (!response.ok) throw new Error();
    const value = await response.json();
    if (eveningController.signal.aborted) return;
    if (value.schemaVersion !== 1) throw new Error();
    if (value.evening && typeof value.evening.label === 'string') {
      evening.value = { label: value.evening.label.slice(0, 240), latest: typeof value.latest?.officialName === 'string' ? value.latest.officialName.slice(0, 240) : undefined };
      eveningState.value = 'Veřejný přehled aktivního večera';
    } else { eveningState.value = 'Veřejný večer zatím není otevřený.'; }
  } catch {
    if (!eveningController.signal.aborted) eveningState.value = 'Přehled večera je nyní nedostupný. Ostatní části aplikace zůstávají použitelné.';
  }
});
onBeforeUnmount(() => {
  pageMounted.value = false;
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('offline', onOffline);
  eveningController.abort();
  journeyRecovery.dispose();
});
</script>

<template>
  <div class="home-layout">
    <section class="home-introduction page-stack">
      <p class="eyebrow">DildoVander</p><h1>{{ greeting }}</h1>
      <p class="home-lead">Prohlédněte si model kufru a příběhy hůlek. Pro párování a ovládání fyzického kufru použijte aplikaci pro iPhone a přihlaste se k oprávněnému účtu.</p>
      <AwNotice v-if="['operator', 'administrator'].includes(String(route.query.access))" tone="warning">K této části nemáte potřebnou roli. Oprávnění ke konkrétnímu kufru přiděluje vlastník.</AwNotice>
      <AwNotice v-if="route.query.access === 'online'" tone="warning">Tato část vyžaduje připojení a ověření účtu serverem. Offline můžete ovládat již spárovaný místní kufr.</AwNotice>
      <div v-if="canOperate" class="home-primary-actions"><RouterLink class="aw-button aw-button--primary" :to="source === 'unavailable' ? '/connections' : '/suitcase'">{{ source === 'unavailable' ? 'Připojit kufr' : 'Otevřít kufr' }}</RouterLink><RouterLink class="aw-button aw-button--secondary" to="/archive">Najít příběh hůlky</RouterLink></div>
      <CustomerJourneyCard v-if="isCustomer" :journey="customerJourney" />
      <nav v-if="isCustomer" class="home-primary-actions" aria-label="Další části vašeho prostoru"><RouterLink class="aw-button aw-button--secondary" to="/me/keepsakes">Moje památky</RouterLink></nav>
      <div v-else-if="!canOperate" class="home-primary-actions"><a v-if="browserMode" class="aw-button aw-button--primary" href="/ios/">Stáhnout aplikaci pro iPhone</a><RouterLink class="aw-button" :class="browserMode ? 'aw-button--secondary' : 'aw-button--primary'" :to="account ? '/settings' : { path: '/signin', query: { return: '/suitcase' } }">{{ account ? 'Můj účet' : 'Přihlásit se' }}</RouterLink></div>
      <div v-if="browserMode" class="home-download"><p class="aw-muted">Na webu si prohlédnete model, archiv a označenou simulaci. Nativní funkce iPhonu jsou dostupné v instalované aplikaci.</p><a v-if="canOperate" class="aw-button aw-button--secondary" href="/ios/">Stáhnout aplikaci pro iPhone</a></div>
      <p v-if="!account" class="aw-muted">Veřejné příběhy a model si můžete prohlédnout rovnou. Účet zpřístupní oprávnění a správu.</p>
      <section class="home-evening" aria-label="Aktivní večer"><p class="eyebrow">{{ eveningState }}</p><template v-if="evening"><h2>{{ evening.label }}</h2><p v-if="evening.latest">Poslední připravená hůlka: {{ evening.latest }}</p><RouterLink to="/archive">Otevřít tento večer →</RouterLink></template></section>
    </section>
    <section v-if="canOperate" class="home-readiness" aria-labelledby="readiness-title"><span class="eyebrow">Právě teď</span><h2 id="readiness-title">Váš kufr</h2>
      <template v-if="source === 'unavailable'"><p class="readiness-value">Čeká na připojení</p><p>Vyberte uložený kufr nebo ho bezpečně spárujte. Samotné přihlášení přístup k pohonům neuděluje.</p><RouterLink to="/connections">Připojení a párování →</RouterLink></template>
      <template v-else><p class="readiness-value">{{ source === 'demo' ? 'Ukázkový režim' : source === 'simulator' ? 'Simulátor' : 'Místní připojení' }}</p><p>{{ deviceSession.message.value || 'Aktuální stav a dostupné akce najdete u vybraného kufru.' }}</p><RouterLink to="/suitcase">Zobrazit stav →</RouterLink></template>
    </section>
    <section class="home-workspaces" aria-label="Co chcete udělat">
      <RouterLink class="workspace-link" to="/workshop"><span class="workspace-number">01 / DÍLNA</span><h2>Rozumět sestavě</h2><p>Model, montážní kroky a pozorování v jednom pracovním prostoru.</p><span class="workspace-action">Otevřít dílnu →</span></RouterLink>
      <RouterLink class="workspace-link" to="/archive"><span class="workspace-number">02 / ARCHIV</span><h2>Příběhy z večera</h2><p>Hůlky a veřejné památky. Soukromé fotografie zůstávají za vaším přístupem.</p><span class="workspace-action">Prohlédnout archiv →</span></RouterLink>
      <RouterLink class="workspace-link" to="/settings"><span class="workspace-number">03 / VÁŠ PROSTOR</span><h2>Účet a oprávnění</h2><p>Přístupové klíče, důvěryhodné instalace a bezpečné přihlášení.</p><span class="workspace-action">Otevřít nastavení →</span></RouterLink>
    </section>
  </div>
</template>

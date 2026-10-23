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
import { customerCopy, type CustomerCopyKey } from './customerCopy';

const route = useRoute();
const canOperate = computed(() => canOperateSuitcase(account.value));
const isCustomer = computed(() => isCustomerAccount(account.value) && !accountDeletionRequested.value);
const source = computed(() => deviceSession.target.value);
const browserMode = !Capacitor.isNativePlatform();
const greeting = computed(() => account.value
  ? customerCopy.text('home.greeting-account', { name: account.value.displayName })
  : customerCopy.text('home.greeting-public'));
const evening = ref<{ label: string; latest?: string } | null>(null);
const eveningState = ref<CustomerCopyKey>('home.evening-loading');
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
      eveningState.value = 'home.evening-active';
    } else { eveningState.value = 'home.evening-closed'; }
  } catch {
    if (!eveningController.signal.aborted) eveningState.value = 'home.evening-unavailable';
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
      <p class="home-lead">{{ customerCopy.text('home.lead') }}</p>
      <AwNotice v-if="['operator', 'administrator'].includes(String(route.query.access))" tone="warning">{{ customerCopy.text('home.access-role') }}</AwNotice>
      <AwNotice v-if="route.query.access === 'online'" tone="warning">{{ customerCopy.text('home.access-online') }}</AwNotice>
      <div v-if="canOperate" class="home-primary-actions"><RouterLink class="aw-button aw-button--primary" :to="source === 'unavailable' ? '/connections' : '/suitcase'">{{ customerCopy.text(source === 'unavailable' ? 'home.connect-suitcase' : 'home.open-suitcase') }}</RouterLink><RouterLink class="aw-button aw-button--secondary" to="/archive">{{ customerCopy.text('home.find-wand-story') }}</RouterLink></div>
      <CustomerJourneyCard v-if="isCustomer" :journey="customerJourney" />
      <nav v-if="isCustomer" class="home-primary-actions" :aria-label="customerCopy.text('home.tasks-aria')"><RouterLink class="aw-button aw-button--secondary" to="/me/keepsakes">{{ customerCopy.text('home.customer-keepsakes') }}</RouterLink></nav>
      <div v-else-if="!canOperate" class="home-primary-actions"><a v-if="browserMode" class="aw-button aw-button--primary" href="/ios/">{{ customerCopy.text('home.download-iphone') }}</a><RouterLink class="aw-button" :class="browserMode ? 'aw-button--secondary' : 'aw-button--primary'" :to="account ? '/settings' : { path: '/signin', query: { return: '/suitcase' } }">{{ customerCopy.text(account ? 'home.my-account' : 'shell.sign-in') }}</RouterLink></div>
      <div v-if="browserMode" class="home-download"><p class="aw-muted">{{ customerCopy.text('home.web-capabilities') }}</p><a v-if="canOperate" class="aw-button aw-button--secondary" href="/ios/">{{ customerCopy.text('home.download-iphone') }}</a></div>
      <p v-if="!account" class="aw-muted">{{ customerCopy.text('home.public-guidance') }}</p>
      <RouterLink v-if="!canOperate" to="/demo">{{ customerCopy.text('home.demo-link') }}</RouterLink>
      <section class="home-evening" :aria-label="customerCopy.text('home.evening-aria')"><p class="eyebrow">{{ customerCopy.text(eveningState) }}</p><template v-if="evening"><h2>{{ evening.label }}</h2><p v-if="evening.latest">{{ customerCopy.text('home.latest-wand', { wand: evening.latest }) }}</p><RouterLink to="/archive">{{ customerCopy.text('home.open-evening') }}</RouterLink></template></section>
    </section>
    <section v-if="canOperate" class="home-readiness" aria-labelledby="readiness-title"><span class="eyebrow">{{ customerCopy.text('home.now') }}</span><h2 id="readiness-title">{{ customerCopy.text('home.your-suitcase') }}</h2>
      <template v-if="source === 'unavailable'"><p class="readiness-value">{{ customerCopy.text('home.waiting-connection') }}</p><p>{{ customerCopy.text('home.pairing-guidance') }}</p><RouterLink to="/connections">{{ customerCopy.text('home.connect-and-pair') }}</RouterLink></template>
      <template v-else><p class="readiness-value">{{ customerCopy.text(source === 'demo' ? 'home.demo-mode' : source === 'simulator' ? 'home.simulator' : 'home.local-connection') }}</p><p>{{ deviceSession.message.value || customerCopy.text('home.device-guidance') }}</p><RouterLink to="/suitcase">{{ customerCopy.text('home.show-status') }}</RouterLink></template>
    </section>
    <section class="home-workspaces" :aria-label="customerCopy.text('home.tasks-aria')">
      <RouterLink class="workspace-link" to="/workshop"><span class="workspace-number">{{ customerCopy.text('home.workshop-number') }}</span><h2>{{ customerCopy.text('home.workshop-title') }}</h2><p>{{ customerCopy.text('home.workshop-description') }}</p><span class="workspace-action">{{ customerCopy.text('home.workshop-action') }}</span></RouterLink>
      <RouterLink class="workspace-link" to="/archive"><span class="workspace-number">{{ customerCopy.text('home.archive-number') }}</span><h2>{{ customerCopy.text('home.archive-title') }}</h2><p>{{ customerCopy.text('home.archive-description') }}</p><span class="workspace-action">{{ customerCopy.text('home.archive-action') }}</span></RouterLink>
      <RouterLink class="workspace-link" to="/settings"><span class="workspace-number">{{ customerCopy.text('home.account-number') }}</span><h2>{{ customerCopy.text('home.account-title') }}</h2><p>{{ customerCopy.text('home.account-description') }}</p><span class="workspace-action">{{ customerCopy.text('home.account-action') }}</span></RouterLink>
    </section>
  </div>
</template>

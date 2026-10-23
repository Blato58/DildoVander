<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { isCustomerAccount } from '../../application/access';
import { account, accountDeletionRequested, accountRequest } from '../identity/session';
import { projectCustomerResult, type CustomerResultProjection } from './customerResult';
import { SessionRecoveryController, type SessionRecoveryState } from './sessionRecovery';

const props = defineProps<{ sessionId: string }>();

const projection = ref<CustomerResultProjection | null>(null);
const currentReadVerified = ref(false);
const recoveryState = reactive<SessionRecoveryState>({
  refreshing: false,
  stale: false,
  retrying: false,
  retryDelayMs: null,
  error: undefined,
});
const canRequest = computed(() => Boolean(
  props.sessionId
  && account.value
  && isCustomerAccount(account.value)
  && !accountDeletionRequested.value,
));
const isVisible = ref(typeof document === 'undefined' || document.visibilityState !== 'hidden');
const isOnline = ref(typeof navigator === 'undefined' || navigator.onLine);
const isCurrentVerificationFresh = computed(() => currentReadVerified.value
  && canRequest.value
  && isVisible.value
  && isOnline.value
  && !recoveryState.refreshing
  && !recoveryState.stale);
const displayedGuidance = computed(() => {
  if (isCurrentVerificationFresh.value && projection.value?.valid) return projection.value.guidance;
  if (!isOnline.value) {
    return projection.value?.valid
      ? `Offline. Poslední ověřený stav: ${projection.value.guidance}`
      : 'Pro ověření stavu památky se připojte k internetu.';
  }
  if (!isVisible.value) {
    return projection.value?.valid
      ? `Aplikace je na pozadí. Poslední ověřený stav: ${projection.value.guidance}`
      : 'Stav památky se obnoví po návratu do aplikace.';
  }
  if (recoveryState.stale) {
    return projection.value?.valid
      ? `Stav se nepodařilo obnovit. Poslední ověřený stav: ${projection.value.guidance}`
      : 'Stav památky se nepodařilo ověřit. Obnovte stav.';
  }
  if (recoveryState.refreshing) {
    return projection.value?.valid
      ? `Ověřuji aktuální stav. Poslední ověřený stav: ${projection.value.guidance}`
      : 'Ověřuji stav památky…';
  }
  return projection.value?.valid
    ? `Aktuální stav čeká na ověření. Poslední ověřený stav: ${projection.value.guidance}`
    : 'Stav památky se nepodařilo ověřit. Obnovte stav.';
});

const recovery = new SessionRecoveryController<CustomerResultProjection>({
  refresh: async signal => {
    const requestedSessionId = props.sessionId;
    const requestedAccountId = account.value?.id;
    const response = await accountRequest<unknown>(
      `/app/v1/customer/sessions/${encodeURIComponent(requestedSessionId)}/result`,
      'GET',
      undefined,
      signal,
    );
    if (requestedAccountId !== account.value?.id || requestedSessionId !== props.sessionId) {
      throw new DOMException('Kontext požadavku se změnil.', 'AbortError');
    }
    return projectCustomerResult(response, requestedSessionId);
  },
  hasActiveSession: result => result.shouldPoll,
  onValue: result => {
    projection.value = result;
    currentReadVerified.value = true;
  },
  state: recoveryState,
  pollIntervalMs: 2500,
  retryBaseDelayMs: 1000,
  retryMaxDelayMs: 30_000,
});

function syncActivity(): void {
  recovery.setActive(canRequest.value && isVisible.value && isOnline.value);
}

function contextChanged(): void {
  projection.value = null;
  currentReadVerified.value = false;
  recovery.invalidate();
  syncActivity();
}

function onVisibilityChange(): void {
  isVisible.value = document.visibilityState !== 'hidden';
  currentReadVerified.value = false;
  syncActivity();
}

function onOnline(): void {
  isOnline.value = true;
  syncActivity();
}

function onOffline(): void {
  isOnline.value = false;
  currentReadVerified.value = false;
  syncActivity();
}

function refreshManually(): void {
  if (!canRequest.value || !isVisible.value || !isOnline.value) return;
  currentReadVerified.value = false;
  // Activating first also enables bounded transport retries for a manual refresh
  // made after a terminal result stopped automatic polling.
  recovery.setActive(true);
  void recovery.refresh();
}

watch(
  () => [props.sessionId, account.value?.id, isCustomerAccount(account.value), accountDeletionRequested.value],
  contextChanged,
  { flush: 'sync' },
);

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onOnline);
  window.addEventListener('offline', onOffline);
  syncActivity();
});

onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('offline', onOffline);
  recovery.dispose();
});
</script>

<template>
  <section v-if="canRequest" class="customer-session-result" aria-labelledby="customer-session-result-title">
    <h2 id="customer-session-result-title">Památka rozhovoru</h2>
    <AwNotice :tone="isCurrentVerificationFresh && projection?.valid ? 'info' : 'warning'">
      {{ displayedGuidance }}
      <template v-if="recoveryState.stale && isOnline">
        <br>Další pokus proběhne automaticky.
      </template>
    </AwNotice>
    <p v-if="projection?.valid && projection.state === 'partial'" class="result-parts">
      <span v-if="!isCurrentVerificationFresh">V posledním ověřeném výsledku:</span>
      <span v-if="projection.hasStory">Text památky je dostupný.</span>
      <span v-if="projection.hasArtwork">Ilustrace památky je dostupná.</span>
    </p>
    <RouterLink v-if="isCurrentVerificationFresh && projection?.valid && projection.route" :to="projection.route">Otevřít památku</RouterLink>
    <AwButton :busy="recoveryState.refreshing" :disabled="!isOnline" @click="refreshManually">
      Obnovit stav památky
    </AwButton>
  </section>
</template>

<style scoped>
.customer-session-result { display: grid; gap: 1rem; }
.customer-session-result h2, .customer-session-result p { margin: 0; }
.result-parts { display: flex; flex-wrap: wrap; gap: .5rem; }
</style>

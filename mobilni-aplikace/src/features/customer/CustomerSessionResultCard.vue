<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import GeneratedWand from '../archive/GeneratedWand.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy } from '../../application/customerCopy';
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
const generationUnavailable = computed(() => projection.value?.wandGeneration && ['failed', 'unavailable', 'cancelled', 'stale'].includes(projection.value.wandGeneration.status));
const localizedProjectionGuidance = computed(() => projection.value
  ? customerCopy.text(projection.value.guidanceKey)
  : '');
const displayedGuidance = computed(() => {
  if (isCurrentVerificationFresh.value && projection.value?.valid) return localizedProjectionGuidance.value;
  if (!isOnline.value) {
    return projection.value?.valid
      ? customerCopy.text('result.offline', { guidance: localizedProjectionGuidance.value })
      : customerCopy.text('result.connect-online');
  }
  if (!isVisible.value) {
    return projection.value?.valid
      ? customerCopy.text('result.background', { guidance: localizedProjectionGuidance.value })
      : customerCopy.text('result.return-to-app');
  }
  if (recoveryState.stale) {
    return projection.value?.valid
      ? customerCopy.text('result.stale', { guidance: localizedProjectionGuidance.value })
      : customerCopy.text('result.verification-needed');
  }
  if (recoveryState.refreshing) {
    return projection.value?.valid
      ? customerCopy.text('result.refreshing', { guidance: localizedProjectionGuidance.value })
      : customerCopy.text('result.checking');
  }
  return projection.value?.valid
    ? customerCopy.text('result.unverified', { guidance: localizedProjectionGuidance.value })
    : customerCopy.text('result.verification-needed');
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
    <h2 id="customer-session-result-title">{{ customerCopy.text('result.title') }}</h2>
    <AwNotice :tone="isCurrentVerificationFresh && projection?.valid ? 'info' : 'warning'">
      {{ displayedGuidance }}
      <template v-if="recoveryState.stale && isOnline">
        <br>{{ customerCopy.text('result.retry-later') }}
      </template>
    </AwNotice>
    <p v-if="projection?.valid && projection.state === 'partial'" class="result-parts">
      <span v-if="!isCurrentVerificationFresh">{{ customerCopy.text('result.partial-heading') }}</span>
      <span v-if="projection.hasStory">{{ customerCopy.text('result.story-available') }}</span>
      <span v-if="projection.hasArtwork">{{ customerCopy.text('result.artwork-available') }}</span>
    </p>
    <AwNotice v-if="isCurrentVerificationFresh && projection?.valid && generationUnavailable" tone="warning">
      {{ customerCopy.language.value === 'en' ? 'Your personal wand could not be created. No replacement wand has been assigned.' : 'Osobní hůlku se nepodařilo vytvořit. Náhradní hůlka nebyla přiřazena.' }}
      <template v-if="projection.wandGeneration?.canRetry"><br>{{ customerCopy.language.value === 'en' ? 'When the suitcase is idle, start a new conversation to try again.' : 'Až bude kufr volný, můžeš zahájit nový rozhovor a zkusit to znovu.' }} <RouterLink to="/me/conversation">{{ customerCopy.language.value === 'en' ? 'Open conversation' : 'Přejít k rozhovoru' }}</RouterLink></template>
    </AwNotice>
    <GeneratedWand v-if="isCurrentVerificationFresh && projection?.generatedWand" :wand="projection.generatedWand" show-name />
    <p v-if="isCurrentVerificationFresh && projection?.generatedWand && projection.wandPublicationApproved === false">{{ customerCopy.language.value === 'en' ? 'Your wand stays private. Review the completed keepsake to approve its public fiction for the evening page and duels.' : 'Hůlka zůstává soukromá. V hotové památce zkontroluj fikci a schval její zveřejnění pro večer a souboje.' }}</p>
    <RouterLink v-if="isCurrentVerificationFresh && projection?.valid && projection.route" :to="projection.route">{{ customerCopy.text('result.open') }}</RouterLink>
    <AwButton :busy="recoveryState.refreshing" :disabled="!isOnline" @click="refreshManually">
      {{ customerCopy.text('result.refresh') }}
    </AwButton>
  </section>
</template>

<style scoped>
.customer-session-result { display: grid; gap: 1rem; }
.customer-session-result h2, .customer-session-result p { margin: 0; }
.result-parts { display: flex; flex-wrap: wrap; gap: .5rem; }
</style>

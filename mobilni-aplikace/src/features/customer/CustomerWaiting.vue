<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, useId, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { isCustomerAccount } from '../../application/access';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { account, accountDeletionRequested, accountTransport, offlineSession } from '../identity/session';
import { SessionRecoveryController, type SessionRecoveryState } from './sessionRecovery';
import {
  CustomerWaitingClient,
  customerWaitingAvailabilityCopyKey,
  customerWaitingErrorCopyKey,
  isCustomerWaitingDeviceId,
  type CustomerWaitingCommandKind,
  type CustomerWaitingCommandMetadata,
  type CustomerWaitingEntry,
  type CustomerWaitingSnapshot,
} from './customerWaiting';

const props = defineProps<{
  deviceId: string | null;
  profileId: string | null;
  proximity: { bootId: string; challenge: string } | null;
}>();
const emit = defineEmits<{
  claim: [value: { operationId: string; expiresAt: number; eventId: string; entryId: string; profileId: string }];
}>();

const instanceId = useId().replace(/[^A-Za-z0-9_-]/g, '');
const titleId = `customer-waiting-title-${instanceId}`;
const snapshot = ref<CustomerWaitingSnapshot | null>(null);
const currentReadVerified = ref(false);
const actionError = ref<CustomerCopyKey | null>(null);
const pendingCommand = ref<CustomerWaitingCommandMetadata | null>(null);
const actionBusy = ref(false);
const nowUnixSeconds = ref(Math.floor(Date.now() / 1000));
const isVisible = ref(typeof document === 'undefined' || document.visibilityState !== 'hidden');
const isOnline = ref(typeof navigator === 'undefined' || navigator.onLine);
const recoveryState = reactive<SessionRecoveryState>({
  refreshing: false,
  stale: false,
  retrying: false,
  retryDelayMs: null,
  error: undefined,
});

const clientsByAccount = new Map<string, CustomerWaitingClient>();
let client: CustomerWaitingClient | null = createClient(account.value?.id);
let pendingClaimIntent: { eventId: string; entryId: string; profileId: string; accountId: string; deviceId: string } | null = null;
let commandController: AbortController | undefined;
let expiryTimer: ReturnType<typeof setInterval> | undefined;
let disposed = false;

function createClient(accountId: string | undefined): CustomerWaitingClient | null {
  if (!accountId) return null;
  const existing = clientsByAccount.get(accountId);
  if (existing) return existing;
  try {
    const created = new CustomerWaitingClient({ accountId, transport: accountTransport });
    clientsByAccount.set(accountId, created);
    return created;
  }
  catch { return null; }
}

const canRequest = computed(() => Boolean(
  account.value
  && isCustomerAccount(account.value)
  && !accountDeletionRequested.value
  && !offlineSession.value
  && isCustomerWaitingDeviceId(props.deviceId),
));
const isCurrentReadFresh = computed(() => currentReadVerified.value
  && canRequest.value
  && isOnline.value
  && isVisible.value
  && !recoveryState.refreshing
  && !recoveryState.stale);
const currentEntry = computed(() => snapshot.value?.entry ?? null);
const hasMatchingProfile = computed(() => !!props.profileId && currentEntry.value?.profileId === props.profileId);
const validProximity = computed(() => !!props.proximity
  && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(props.proximity.bootId)
  && /^[\x21-\x7e]{16,128}$/.test(props.proximity.challenge));
const availabilityLabel = computed(() => snapshot.value
  ? customerCopy.text(customerWaitingAvailabilityCopyKey(snapshot.value.availability))
  : customerCopy.text('waiting.status-waiting'));
const waitingPositionLabel = computed(() => {
  const value = snapshot.value;
  if (!value?.entry || value.entry.state === 'accepted' || value.position === null) return '';
  return customerCopy.text('waiting.position', { position: value.position, count: value.count });
});
const currentClaim = computed(() => currentEntry.value?.claim ?? null);
const claimExpiresLabel = computed(() => currentClaim.value
  ? new Date(currentClaim.value.expiresAt * 1000).toLocaleTimeString(customerCopy.language.value === 'en' ? 'en-US' : 'cs-CZ')
  : '');
const claimExpired = computed(() => !!currentClaim.value && currentClaim.value.expiresAt <= nowUnixSeconds.value);
const canAct = computed(() => isCurrentReadFresh.value && snapshot.value?.availability.fresh === true
  && snapshot.value.event?.state === 'open' && !actionBusy.value);
const canJoin = computed(() => canAct.value
  && !snapshot.value?.entry
  && ['available', 'busy', 'recovering'].includes(snapshot.value?.availability.state ?? 'unknown')
  && !!props.profileId
  && validProximity.value);
const canLeave = computed(() => isCurrentReadFresh.value && !actionBusy.value
  && (snapshot.value?.event?.state === 'open' || snapshot.value?.event?.state === 'paused')
  && !!currentEntry.value
  && currentEntry.value.state !== 'accepted');
const canClaim = computed(() => canAct.value
  && snapshot.value?.availability.state === 'available'
  && currentEntry.value?.state === 'waiting'
  && hasMatchingProfile.value
  && snapshot.value.position === 1
  && validProximity.value);
const canUseClaim = computed(() => canAct.value
  && snapshot.value?.availability.state === 'available'
  && currentEntry.value?.state === 'claimed' && !!currentClaim.value
  && !claimExpired.value && hasMatchingProfile.value && validProximity.value);
const freshnessGuidance = computed(() => {
  const last = { guidance: availabilityLabel.value };
  if (!isOnline.value) return snapshot.value ? customerCopy.text('waiting.offline-last', last) : customerCopy.text('waiting.verify-online');
  if (!isVisible.value) return snapshot.value ? customerCopy.text('waiting.background-last', last) : customerCopy.text('waiting.return-app');
  if (recoveryState.stale) return snapshot.value ? customerCopy.text('waiting.stale-last', last) : customerCopy.text('waiting.verify-failed');
  if (recoveryState.refreshing) return snapshot.value ? customerCopy.text('waiting.refreshing-last', last) : customerCopy.text('waiting.checking');
  return isCurrentReadFresh.value ? availabilityLabel.value : snapshot.value ? customerCopy.text('waiting.unverified-last', last) : customerCopy.text('waiting.verify-pending');
});

const recovery = new SessionRecoveryController<CustomerWaitingSnapshot>({
  refresh: async signal => {
    if (!canRequest.value || !props.deviceId || !client) throw new Error('not-available');
    const requestedAccountId = account.value?.id;
    const requestedDeviceId = props.deviceId;
    const requestedProfileId = props.profileId;
    const requestedClient = client;
    const value = await requestedClient.getSnapshot(requestedDeviceId, signal);
    if (disposed || requestedAccountId !== account.value?.id || requestedDeviceId !== props.deviceId
      || requestedProfileId !== props.profileId || requestedClient !== client) {
      throw new DOMException('Context changed', 'AbortError');
    }
    return value;
  },
  hasActiveSession: value => value.event?.state === 'open' || value.entry !== null,
  onValue: value => {
    snapshot.value = value;
    currentReadVerified.value = true;
    actionError.value = null;
    const activeClient = client;
    if (!activeClient || !props.deviceId) return;
    try {
      const outcome = activeClient.reconcile(props.deviceId, value);
      if (outcome !== 'none') actionError.value = null;
      const pending = activeClient.getPendingCommand(props.deviceId);
      pendingCommand.value = pending;
      if (pending && outcome === 'pending') {
        actionError.value = 'waiting.error.pending-not-confirmed';
      }
      confirmPendingClaim(value);
    } catch (error) {
      actionError.value = customerWaitingErrorCopyKey(error);
    }
  },
  state: recoveryState,
  pollIntervalMs: 5000,
  retryBaseDelayMs: 1000,
  retryMaxDelayMs: 30_000,
});

function confirmPendingClaim(value: CustomerWaitingSnapshot): void {
  const intent = pendingClaimIntent;
  const claim = value.entry?.claim;
  if (!intent || !claim || !props.deviceId || !account.value?.id) return;
  if (intent.accountId !== account.value.id || intent.deviceId !== props.deviceId
      || intent.eventId !== value.event?.id || intent.entryId !== value.entry?.id
      || intent.profileId !== value.entry?.profileId || value.entry.state !== 'claimed'
      || claim.expiresAt <= nowUnixSeconds.value || !value.availability.fresh || !validProximity.value
      || value.availability.state !== 'available' || value.event?.state !== 'open'
      || props.profileId !== intent.profileId || !isCurrentReadFresh.value) return;
  pendingClaimIntent = null;
  emit('claim', {
    operationId: claim.id,
    expiresAt: claim.expiresAt,
    eventId: intent.eventId,
    entryId: intent.entryId,
    profileId: intent.profileId,
  });
}

function useReservedTurn(): void {
  const value = snapshot.value;
  if (!canUseClaim.value || !value?.event || !value.entry || !account.value?.id || !props.deviceId) return;
  pendingClaimIntent = { eventId: value.event.id, entryId: value.entry.id,
    profileId: value.entry.profileId, accountId: account.value.id, deviceId: props.deviceId };
  // A restored claim is handed off only on this click, never by a GET or
  // remount. The parent still requires its separate consent/start action.
  confirmPendingClaim(value);
}

function syncActivity(): void {
  recovery.setActive(canRequest.value && isVisible.value && isOnline.value);
}

function resetForContextChange(): void {
  currentReadVerified.value = false;
  snapshot.value = null;
  actionError.value = null;
  pendingCommand.value = null;
  pendingClaimIntent = null;
  commandController?.abort();
  commandController = undefined;
  recovery.invalidate();
  client = createClient(account.value?.id);
  syncActivity();
}

function onVisibilityChange(): void {
  isVisible.value = document.visibilityState !== 'hidden';
  currentReadVerified.value = false;
  if (!isVisible.value) commandController?.abort();
  syncActivity();
}

function onOnline(): void {
  isOnline.value = true;
  currentReadVerified.value = false;
  syncActivity();
}

function onOffline(): void {
  isOnline.value = false;
  currentReadVerified.value = false;
  commandController?.abort();
  syncActivity();
}

function refreshManually(): void {
  if (!canRequest.value || !isVisible.value || !isOnline.value) return;
  currentReadVerified.value = false;
  recovery.setActive(true);
  void recovery.refresh();
}

function sameActionReady(kind: CustomerWaitingCommandKind): boolean {
  if (!isCurrentReadFresh.value || !snapshot.value?.event || !client || !props.deviceId) return false;
  return kind === 'leave' ? canLeave.value : canAct.value;
}

async function runCommand(kind: CustomerWaitingCommandKind): Promise<void> {
  if (!sameActionReady(kind) || !client || !props.deviceId || !snapshot.value?.event) return;
  actionBusy.value = true;
  actionError.value = null;
  const deviceId = props.deviceId;
  const event = snapshot.value.event;
  const entry: CustomerWaitingEntry | null = currentEntry.value;
  const requestedAccountId = account.value?.id;
  const requestedProfileId = props.profileId;
  const commandClient = client;
  const controller = new AbortController();
  commandController?.abort();
  commandController = controller;
  if (kind === 'claim' && entry && requestedProfileId) {
    pendingClaimIntent = { accountId: requestedAccountId ?? '', deviceId, eventId: event.id, entryId: entry.id, profileId: requestedProfileId };
  }
  try {
    if (kind === 'join' && props.profileId && props.proximity) {
      await commandClient.join(deviceId, event.id, props.profileId, props.proximity, controller.signal);
    } else if (kind === 'leave' && entry) {
      await commandClient.leave(deviceId, event.id, entry.id, entry.revision, controller.signal);
    } else if (kind === 'claim' && entry && props.proximity) {
      await commandClient.claim(deviceId, event.id, entry.id, entry.revision, props.proximity, controller.signal);
    } else {
      return;
    }
    if (requestedAccountId !== account.value?.id || requestedProfileId !== props.profileId
        || deviceId !== props.deviceId || commandClient !== client) return;
    refreshManually();
  } catch (error) {
    if (!controller.signal.aborted && requestedAccountId === account.value?.id && deviceId === props.deviceId && commandClient === client) {
      actionError.value = customerWaitingErrorCopyKey(error);
      try { pendingCommand.value = commandClient.getPendingCommand(deviceId); } catch { pendingCommand.value = null; }
      refreshManually();
    }
  } finally {
    actionBusy.value = false;
    if (commandController === controller) commandController = undefined;
  }
}

watch(
  () => [account.value?.id, isCustomerAccount(account.value), accountDeletionRequested.value, offlineSession.value, props.deviceId, props.profileId],
  resetForContextChange,
  { flush: 'sync' },
);

watch(currentClaim, claim => {
  if (expiryTimer) clearInterval(expiryTimer);
  expiryTimer = undefined;
  if (claim) expiryTimer = setInterval(() => { nowUnixSeconds.value = Math.floor(Date.now() / 1000); }, 1000);
}, { immediate: true });

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onOnline);
  window.addEventListener('offline', onOffline);
  syncActivity();
});

onBeforeUnmount(() => {
  disposed = true;
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('offline', onOffline);
  if (expiryTimer) clearInterval(expiryTimer);
  commandController?.abort();
  recovery.dispose();
});
</script>

<template>
  <section v-if="canRequest" class="customer-waiting aw-card" :aria-labelledby="titleId">
    <h2 :id="titleId">{{ customerCopy.text('waiting.title') }}</h2>
    <AwNotice :tone="isCurrentReadFresh && snapshot?.availability.fresh ? 'info' : 'warning'">
      {{ freshnessGuidance }}
      <template v-if="recoveryState.stale && isOnline"><br>{{ customerCopy.text('waiting.retrying') }}</template>
    </AwNotice>
    <p v-if="snapshot?.event?.state === 'closed'">{{ customerCopy.text('waiting.event-closed') }}</p>
    <p v-if="snapshot?.event && snapshot.event.state === 'open'">{{ customerCopy.text('waiting.event-open') }}</p>
    <p v-if="snapshot?.entry && snapshot.entry.state === 'waiting'">{{ waitingPositionLabel }} {{ customerCopy.text('waiting.position-retained') }}</p>
    <p v-if="snapshot?.entry?.state === 'claimed' && currentClaim && !claimExpired">
      {{ customerCopy.text('waiting.claim-until', { time: claimExpiresLabel }) }}
    </p>
    <p v-if="snapshot?.entry?.state === 'claimed' && claimExpired">{{ customerCopy.text('waiting.claim-expired') }}</p>
    <p v-if="snapshot?.entry?.state === 'accepted'">{{ customerCopy.text('waiting.accepted') }}</p>
    <p v-if="snapshot?.entry && !hasMatchingProfile">{{ customerCopy.text('waiting.other-profile') }}</p>
    <AwNotice v-if="pendingCommand && client?.metadataStatus === 'memory-only'" tone="warning">
      {{ customerCopy.text('waiting.memory-only') }}
    </AwNotice>
    <AwNotice v-if="actionError" tone="warning">{{ customerCopy.text(actionError) }}</AwNotice>
    <p v-if="recoveryState.retrying" role="status">{{ customerCopy.text('waiting.retry-in', { seconds: Math.ceil((recoveryState.retryDelayMs ?? 0) / 1000) }) }}</p>
    <div class="customer-waiting__actions">
      <AwButton :busy="recoveryState.refreshing" :disabled="!isOnline || !isVisible" @click="refreshManually">{{ customerCopy.text('waiting.refresh') }}</AwButton>
      <AwButton v-if="!snapshot?.entry && snapshot?.event?.state === 'open'" variant="primary" :disabled="!canJoin" :busy="actionBusy" @click="runCommand('join')">{{ customerCopy.text('waiting.join') }}</AwButton>
      <AwButton v-if="canLeave" variant="secondary" :disabled="!canLeave" :busy="actionBusy" @click="runCommand('leave')">{{ customerCopy.text('waiting.leave') }}</AwButton>
      <AwButton v-if="snapshot?.entry?.state === 'waiting' && snapshot.position === 1" variant="primary" :disabled="!canClaim" :busy="actionBusy" @click="runCommand('claim')">{{ customerCopy.text('waiting.claim') }}</AwButton>
      <AwButton v-if="snapshot?.entry?.state === 'claimed'" variant="primary" :disabled="!canUseClaim" @click="useReservedTurn">{{ customerCopy.text('waiting.use-claim') }}</AwButton>
      <RouterLink v-if="snapshot?.entry?.state === 'accepted'" to="/me/conversation">{{ customerCopy.text('waiting.open-conversation') }}</RouterLink>
    </div>
  </section>
</template>

<style scoped>
.customer-waiting { display: grid; gap: 1rem; }
.customer-waiting h2, .customer-waiting p { margin: 0; }
.customer-waiting__actions { display: flex; flex-wrap: wrap; align-items: center; gap: var(--aw-space-3); }
</style>

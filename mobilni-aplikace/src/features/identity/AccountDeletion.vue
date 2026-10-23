<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, authenticatePasskey, deleteAccount, getAccountDeletionStatus } from './session';
import { customerCopy } from '../../application/customerCopy';

const props = withDefaults(defineProps<{ receiptOnly?: boolean }>(), { receiptOnly: false });
const busy = ref(false);
const verifiedAccountId = ref('');
const error = ref('');
const accepted = ref(false);
const completed = ref(false);
const checking = ref(false);
const statusError = ref('');
const receiptMissing = ref(false);
let statusRequest: AbortController | undefined;
let statusTimer: ReturnType<typeof setTimeout> | undefined;
let checksRemaining = 60;
let submitting = false;
let disposed = false;
let generation = 0;
function cancelStatus() {
  clearTimeout(statusTimer);
  statusRequest?.abort();
  statusRequest = undefined;
  checking.value = false;
}
async function checkStatus() {
  if (disposed || account.value || checking.value || document.visibilityState === 'hidden') return;
  clearTimeout(statusTimer);
  const controller = new AbortController();
  statusRequest = controller;
  checking.value = true;
  statusError.value = '';
  try {
    const status = await getAccountDeletionStatus(controller.signal);
    if (disposed || controller.signal.aborted || account.value) return;
    receiptMissing.value = !status;
    if (status) {
      accepted.value = true;
      completed.value = status === 'deleted';
    } else if (accepted.value) {
      statusError.value = customerCopy.text('deletion.status-unavailable');
    }
    if (status === 'deletion_requested' && --checksRemaining > 0) statusTimer = setTimeout(() => void checkStatus(), 5000);
  } catch (failure) {
    if (!controller.signal.aborted && !disposed) statusError.value = customerCopy.text('deletion.status-error');
  } finally {
    if (statusRequest === controller) { statusRequest = undefined; checking.value = false; }
  }
}
function visibilityChanged() {
  if (document.visibilityState === 'hidden') cancelStatus();
  else if (accepted.value && !completed.value && checksRemaining > 0) void checkStatus();
}
onMounted(() => { document.addEventListener('visibilitychange', visibilityChanged); if (!account.value) void checkStatus(); });
onBeforeUnmount(() => { disposed = true; cancelStatus(); document.removeEventListener('visibilitychange', visibilityChanged); });
watch(() => account.value?.id, next => {
  ++generation;
  cancelStatus();
  verifiedAccountId.value = '';
  error.value = '';
  accepted.value = false;
  completed.value = false;
  statusError.value = '';
  receiptMissing.value = false;
  checksRemaining = 60;
  busy.value = false;
  if (!next && !submitting) void checkStatus();
}, { flush: 'sync' });

async function verify() {
  const accountId = account.value?.id;
  if (!accountId || busy.value) return;
  const attempt = generation;
  busy.value = true;
  error.value = '';
  verifiedAccountId.value = '';
  try {
    await authenticatePasskey('reauth');
    if (attempt === generation && account.value?.id === accountId) verifiedAccountId.value = accountId;
  } catch (failure) {
    if (attempt === generation) error.value = customerCopy.text('deletion.passkey-error');
  } finally {
    if (attempt === generation) busy.value = false;
  }
}

async function submit() {
  const accountId = verifiedAccountId.value;
  if (!accountId || account.value?.id !== accountId || busy.value) return;
  const attempt = generation;
  busy.value = true;
  error.value = '';
  submitting = true;
  try {
    await deleteAccount(accountId);
    // Successful deletion clears account synchronously. Never show its receipt
    // to a different account that signed in while this request was pending.
    if (!disposed && !account.value) { accepted.value = true; void checkStatus(); }
  } catch (failure) {
    if (attempt === generation) {
      error.value = customerCopy.text('deletion.request-error');
      if (!failure || typeof failure !== 'object' || !('status' in failure)) {
        error.value += ` ${customerCopy.text('deletion.uncertain')}`;
      }
    }
  } finally {
    submitting = false;
    if (attempt === generation) {
      busy.value = false;
      verifiedAccountId.value = '';
    }
  }
}
</script>

<template>
  <section v-if="account || accepted || statusError || receiptOnly" class="aw-card account-deletion" aria-labelledby="account-deletion-title" :aria-busy="busy || checking">
    <h2 id="account-deletion-title">{{ customerCopy.text('deletion.title') }}</h2>
    <p v-if="receiptOnly && account">{{ customerCopy.text('deletion.receipt-signed-in') }}</p>
    <template v-else-if="accepted || !account || receiptOnly">
      <p v-if="receiptOnly && checking && !accepted" role="status">{{ customerCopy.text('deletion.checking') }}</p>
      <p v-if="receiptOnly && receiptMissing && !accepted">{{ customerCopy.text('deletion.receipt-missing') }}</p>
      <AwNotice v-if="accepted" :tone="completed ? 'success' : 'info'" role="status">
        <template v-if="completed">{{ customerCopy.text('deletion.completed') }}</template>
        <template v-else>{{ customerCopy.text('deletion.accepted') }}</template>
      </AwNotice>
      <p v-if="accepted">{{ customerCopy.text('deletion.provider-backups') }} {{ customerCopy.text('deletion.provider-retention') }}</p>
      <AwNotice v-if="statusError" tone="danger" role="alert">{{ statusError }}</AwNotice>
      <AwButton v-if="!completed" :busy="checking" :disabled="checking" @click="checksRemaining = 60; checkStatus()">{{ customerCopy.text('deletion.check-status') }}</AwButton>
    </template>
    <template v-else>
      <p>{{ customerCopy.text('deletion.irreversible') }}</p>
      <p>{{ customerCopy.text('deletion.other-accounts-preserved') }}</p>
      <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
      <AwButton v-if="!verifiedAccountId" :busy="busy" :disabled="busy" @click="verify">{{ customerCopy.text('deletion.verify') }}</AwButton>
      <template v-else>
        <p role="status">{{ customerCopy.text('deletion.verify-complete', { name: account?.displayName ?? '' }) }}</p>
        <div class="deletion-actions">
          <AwButton variant="danger" :busy="busy" :disabled="busy" @click="submit">{{ customerCopy.text('deletion.confirm') }}</AwButton>
          <AwButton :disabled="busy" @click="verifiedAccountId = ''">{{ customerCopy.text('deletion.cancel') }}</AwButton>
        </div>
      </template>
    </template>
  </section>
</template>

<style scoped>
.deletion-actions { display: flex; flex-wrap: wrap; gap: .75rem; }
</style>

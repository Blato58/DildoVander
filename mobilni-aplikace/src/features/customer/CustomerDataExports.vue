<script setup lang="ts">
import { Capacitor } from '@capacitor/core';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { account, accountDeletionRequested, accountTransport, offlineSession, shareNativeAccountDataFile } from '../identity/session';
import {
  CustomerDataExportClient,
  CustomerDataExportError,
  deliverBrowserDownload,
  type CustomerAccountTransport,
  type CustomerDataExportJob,
  type CustomerDataExportStorage,
} from './customerDataExport';

const props = defineProps<{
  transport?: CustomerAccountTransport;
  storage?: CustomerDataExportStorage;
  native?: boolean;
  deliver?: (blob: Blob, filename: string) => void | Promise<void>;
}>();

const nativePlatform = computed(() => props.native ?? Capacitor.isNativePlatform());
const client = new CustomerDataExportClient({
  currentAccountId: () => account.value?.id ?? null,
  transport: props.transport ?? accountTransport,
  storage: props.storage,
});
const job = ref<CustomerDataExportJob | null>(null);
const loading = ref(false);
const submitting = ref(false);
const deleting = ref(false);
const downloading = ref(false);
const stale = ref(true);
const expired = ref(false);
const missing = ref(false);
const hasStoredJob = ref(false);
const browserOnline = ref(typeof navigator === 'undefined' || navigator.onLine);
const pageVisible = ref(typeof document !== 'undefined' && !document.hidden);
const errorMessage = ref<CustomerCopyKey | ''>('');
const deliveryMessage = ref<CustomerCopyKey | ''>('');
const pendingKey = ref(false);
const pendingKeyRejected = ref(false);
const nowTick = ref(Date.now());
let mounted = false;
let disposed = false;
let generation = 0;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
let expiryTimer: ReturnType<typeof setTimeout> | undefined;
let refreshPromise: Promise<void> | undefined;
let retryInitialLookup = false;
let deliveryController: AbortController | undefined;

const accountId = computed(() => account.value?.id ?? '');
const allowed = computed(() => !!accountId.value && !accountDeletionRequested.value);
const busy = computed(() => loading.value || submitting.value || deleting.value || downloading.value);
const canDownload = computed(() => allowed.value && !offlineSession.value && !stale.value
  && !busy.value && job.value?.state === 'ready' && !isExpired());
function exportErrorKey(error: unknown): CustomerCopyKey {
  if (error instanceof CustomerDataExportError) {
    if (error.kind === 'unauthenticated') return 'export.error-unauthenticated';
    if (error.kind === 'expired' || error.status === 410) return 'export.error-expired';
    if (error.kind === 'too-large' || error.status === 413) return 'export.error-too-large';
    if (error.status === 404) return 'export.error-not-found';
    if (error.status === 409) return 'export.error-conflict';
    if (error.status === 503) return 'export.error-busy';
    if (error.kind === 'invalid-response') return 'export.error-invalid';
  }
  return 'export.error-generic';
}
const reasonKeys = {
  inventory_unavailable: 'export.reason-inventory-unavailable',
  memory_unavailable: 'export.reason-memory-unavailable',
  source_changed: 'export.reason-source-changed',
  payload_too_large: 'export.reason-payload-too-large',
  preparation_interrupted: 'export.reason-preparation-interrupted',
  download_expired: 'export.reason-download-expired',
  export_failed: 'export.reason-failed',
} as const satisfies Record<NonNullable<CustomerDataExportJob['reason']>, CustomerCopyKey>;
const stateLabel = computed(() => {
  if (pendingKeyRejected.value && !job.value) return customerCopy.text('export.pending-key-rejected-state');
  if (pendingKey.value && !job.value) return customerCopy.text('export.pending-key-state');
  if (missing.value) return customerCopy.text('export.missing-state');
  if (!job.value && hasStoredJob.value) return customerCopy.text(loading.value ? 'export.stored-loading' : 'export.stored-unverified');
  if (!job.value) return customerCopy.text('export.none');
  return customerCopy.text(({
    requested: 'export.state-requested',
    preparing: 'export.state-preparing',
    ready: 'export.state-ready',
    failed: 'export.state-failed',
  } as const)[job.value.state]);
});
const reason = computed(() => job.value?.reason ? customerCopy.text(reasonKeys[job.value.reason]) : '');

function isVisibleAndOnline(): boolean {
  return browserOnline.value && pageVisible.value;
}

function isExpired(): boolean {
  return expired.value || (!!job.value?.downloadExpiresAt && Date.parse(job.value.downloadExpiresAt) <= nowTick.value);
}

function clearTimers() {
  clearTimeout(pollTimer);
  clearTimeout(expiryTimer);
  pollTimer = undefined;
  expiryTimer = undefined;
}

function suspend() {
  deliveryController?.abort();
  deliveryController = undefined;
  deliveryMessage.value = '';
  generation++;
  clearTimers();
  refreshPromise = undefined;
  client.invalidateRequests();
  stale.value = true;
  loading.value = false;
  submitting.value = false;
  deleting.value = false;
  downloading.value = false;
}

function schedulePolling() {
  clearTimeout(pollTimer);
  const retryingInitialLookup = retryInitialLookup && !missing.value && !job.value;
  const jobIsActive = job.value?.state === 'requested' || job.value?.state === 'preparing';
  if (!mounted || disposed || !allowed.value || offlineSession.value || !isVisibleAndOnline()
    || (!retryingInitialLookup && !jobIsActive)) return;
  pollTimer = setTimeout(() => { void refreshStatus(false); }, 5000);
}

function scheduleExpiry() {
  clearTimeout(expiryTimer);
  if (!job.value?.downloadExpiresAt) return;
  const wait = Date.parse(job.value.downloadExpiresAt) - Date.now();
  if (wait <= 0) { expired.value = true; nowTick.value = Date.now(); return; }
  expiryTimer = setTimeout(() => {
    expired.value = true;
    nowTick.value = Date.now();
    deliveryController?.abort();
  }, wait);
}

function applyJob(next: CustomerDataExportJob | null) {
  job.value = next;
  retryInitialLookup = false;
  expired.value = false;
  nowTick.value = Date.now();
  scheduleExpiry();
  schedulePolling();
}

async function refreshStatus(manual = true): Promise<void> {
  if (!allowed.value || offlineSession.value || !isVisibleAndOnline()) return;
  if (refreshPromise) return refreshPromise;
  if (!client.recoveryMetadata.jobId) {
    pendingKey.value = !!client.recoveryMetadata.pendingIdempotencyKey;
    return;
  }
  if (manual) { stale.value = true; errorMessage.value = ''; }
  clearTimeout(pollTimer);
  pollTimer = undefined;
  const attempt = generation;
  loading.value = true;
  const task = (async () => {
    try {
      const refreshed = await client.refresh();
      if (disposed || attempt !== generation || !allowed.value) return;
      missing.value = false;
      applyJob(refreshed);
      pendingKey.value = !!client.recoveryMetadata.pendingIdempotencyKey;
      errorMessage.value = '';
      stale.value = false;
    } catch (failure) {
      if ((failure as Error)?.name === 'AbortError' || disposed || attempt !== generation) return;
      const status = failure instanceof CustomerDataExportError ? failure.status : undefined;
      if (status === 404) { missing.value = true; applyJob(null); }
      errorMessage.value = exportErrorKey(failure);
      retryInitialLookup = !job.value && (status === undefined
        ? !(failure instanceof CustomerDataExportError)
        : [408, 429, 500, 502, 503, 504].includes(status));
      schedulePolling();
    } finally {
      if (!disposed && attempt === generation) loading.value = false;
    }
  })();
  refreshPromise = task;
  try { await task; }
  finally { if (refreshPromise === task) refreshPromise = undefined; }
}

async function prepareExport(): Promise<void> {
  if (!allowed.value || offlineSession.value || !isVisibleAndOnline() || busy.value) return;
  const metadata = client.recoveryMetadata;
  const rejectedKey = pendingKeyRejected.value;
  if (!metadata.pendingIdempotencyKey || rejectedKey) {
    const confirmed = window.confirm(customerCopy.text('export.prepare-confirmation'));
    if (!confirmed) return;
  }
  stale.value = true;
  errorMessage.value = '';
  missing.value = false;
  job.value = null;
  pendingKeyRejected.value = false;
  const attempt = generation;
  submitting.value = true;
  try {
    pendingKeyRejected.value = false;
    const created = rejectedKey ? await client.createNewAttempt() : await client.createOrRetry();
    if (disposed || attempt !== generation || !allowed.value) return;
    pendingKey.value = false;
    hasStoredJob.value = true;
    missing.value = false;
    applyJob(created);
    stale.value = false;
  } catch (failure) {
    if ((failure as Error)?.name !== 'AbortError' && !disposed && attempt === generation) {
      errorMessage.value = exportErrorKey(failure);
      pendingKeyRejected.value = failure instanceof CustomerDataExportError && failure.status === 409
        && !!client.recoveryMetadata.pendingIdempotencyKey;
    }
    if (!disposed && attempt === generation) pendingKey.value = !!client.recoveryMetadata.pendingIdempotencyKey;
  } finally { if (!disposed && attempt === generation) submitting.value = false; }
}

async function deleteTechnicalJob(): Promise<void> {
  if (!allowed.value || busy.value || offlineSession.value || !isVisibleAndOnline() || !client.recoveryMetadata.jobId) return;
  const confirmed = window.confirm(customerCopy.text('export.delete-confirmation'));
  if (!confirmed || !allowed.value || busy.value || offlineSession.value || !isVisibleAndOnline()) return;
  stale.value = true;
  deleting.value = true;
  errorMessage.value = '';
  const attempt = generation;
  try {
    await client.deleteJob();
    if (disposed || attempt !== generation || !allowed.value) return;
    applyJob(null);
    hasStoredJob.value = false;
    missing.value = false;
    pendingKey.value = false;
    stale.value = false;
  } catch (failure) {
    if ((failure as Error)?.name !== 'AbortError' && !disposed && attempt === generation) errorMessage.value = exportErrorKey(failure);
  } finally { if (!disposed && attempt === generation) deleting.value = false; }
}

async function downloadExport(): Promise<void> {
  const selected = job.value;
  if (!selected || !canDownload.value || !isVisibleAndOnline()) return;
  downloading.value = true;
  stale.value = true;
  errorMessage.value = '';
  const attempt = generation;
  const controller = new AbortController();
  deliveryController = controller;
  deliveryMessage.value = '';
  const checkDelivery = () => {
    if (controller.signal.aborted || disposed || attempt !== generation || !allowed.value
      || job.value !== selected || !isVisibleAndOnline() || offlineSession.value || isExpired()
      || !selected.downloadExpiresAt || Date.parse(selected.downloadExpiresAt) <= Date.now()) {
      throw new DOMException('Export context changed.', 'AbortError');
    }
  };
  try {
    const file = await client.download(selected);
    checkDelivery();
    if (nativePlatform.value) {
      const result = await shareNativeAccountDataFile(file, 'customer-data-export.json', {
        signal: controller.signal, check: checkDelivery,
      });
      checkDelivery();
      deliveryMessage.value = result.completed
        ? 'export.share-completed'
        : 'export.share-cancelled';
    } else {
      await (props.deliver ?? deliverBrowserDownload)(file, 'customer-data-export.json');
    }
    if (!disposed && attempt === generation && allowed.value && job.value === selected) stale.value = false;
  } catch (failure) {
    if ((failure as Error)?.name !== 'AbortError' && !disposed && attempt === generation) errorMessage.value = exportErrorKey(failure);
  } finally {
    if (deliveryController === controller) deliveryController = undefined;
    if (!disposed && attempt === generation) downloading.value = false;
  }
}

function accountContextChanged() {
  deliveryController?.abort();
  deliveryController = undefined;
  deliveryMessage.value = '';
  generation++;
  clearTimers();
  refreshPromise = undefined;
  retryInitialLookup = false;
  client.setAccount(allowed.value ? accountId.value : null);
  hasStoredJob.value = !!client.recoveryMetadata.jobId;
  job.value = null;
  loading.value = false;
  submitting.value = false;
  deleting.value = false;
  downloading.value = false;
  expired.value = false;
  missing.value = false;
  errorMessage.value = '';
  pendingKey.value = !!client.recoveryMetadata.pendingIdempotencyKey;
  pendingKeyRejected.value = false;
  stale.value = true;
  if (mounted && allowed.value && !offlineSession.value) void refreshStatus(false);
}

function visibilityChanged() {
  pageVisible.value = !document.hidden;
  if (!pageVisible.value) suspend();
  else if (allowed.value && !offlineSession.value) {
    stale.value = true;
    void refreshStatus(false);
  }
}

function networkChanged() {
  browserOnline.value = navigator.onLine;
  if (!browserOnline.value) suspend();
  else if (allowed.value && !offlineSession.value) {
    stale.value = true;
    void refreshStatus(false);
  }
}

watch(() => [account.value?.id, accountDeletionRequested.value] as const, accountContextChanged, { flush: 'sync' });
watch(() => offlineSession.value, offline => {
  if (offline) suspend();
  else if (allowed.value && mounted && isVisibleAndOnline()) void refreshStatus(false);
}, { flush: 'sync' });

onMounted(() => {
  mounted = true;
  document.addEventListener('visibilitychange', visibilityChanged);
  window.addEventListener('online', networkChanged);
  window.addEventListener('offline', networkChanged);
  accountContextChanged();
});

onBeforeUnmount(() => {
  disposed = true;
  suspend();
  client.dispose();
  document.removeEventListener('visibilitychange', visibilityChanged);
  window.removeEventListener('online', networkChanged);
  window.removeEventListener('offline', networkChanged);
});
</script>

<template>
  <section class="customer-data-exports page-stack" aria-labelledby="customer-data-export-title">
    <h2 id="customer-data-export-title">{{ customerCopy.text('export.title') }}</h2>
    <AwNotice tone="info" :title="customerCopy.text('export.contents-heading')">
      {{ customerCopy.text('export.contents') }}
    </AwNotice>

    <AwNotice v-if="nativePlatform" tone="warning">
      {{ customerCopy.text('export.native-share-warning') }}
    </AwNotice>
    <AwNotice v-if="offlineSession || !isVisibleAndOnline()" tone="warning">
      {{ customerCopy.text('export.offline') }}
    </AwNotice>
    <AwNotice v-if="pendingKey && !job && !pendingKeyRejected" tone="warning">
      {{ customerCopy.text('export.pending-uncertain') }}
    </AwNotice>
    <AwNotice v-if="pendingKeyRejected" tone="warning">
      {{ customerCopy.text('export.pending-rejected') }}
    </AwNotice>
    <AwNotice v-if="missing" tone="warning">
      {{ customerCopy.text('export.missing') }}
    </AwNotice>
    <AwNotice v-if="job" :tone="job.state === 'failed' ? 'warning' : 'info'" :title="customerCopy.text('export.state-heading')">
      <p>{{ stateLabel }}</p>
      <p>{{ customerCopy.text('export.requested-at', { date: job.requestedAt }) }}</p>
      <p v-if="job.readyAt">{{ customerCopy.text('export.ready-at', { date: job.readyAt }) }}</p>
      <p v-if="job.downloadExpiresAt">{{ customerCopy.text('export.expires-at', { date: job.downloadExpiresAt }) }}</p>
      <p v-if="reason">{{ reason }}</p>
      <p v-if="isExpired()">{{ customerCopy.text('export.expired') }}</p>
    </AwNotice>
    <AwNotice v-if="errorMessage" tone="danger">{{ customerCopy.text(errorMessage) }}</AwNotice>
    <AwNotice v-if="deliveryMessage" tone="info">{{ customerCopy.text(deliveryMessage) }}</AwNotice>
    <p v-if="!allowed" role="status">{{ customerCopy.text('export.signed-out') }}</p>
    <p v-else role="status" aria-live="polite">{{ stateLabel }}</p>

    <div v-if="allowed" class="customer-data-exports__actions">
      <AwButton :disabled="busy || offlineSession || !isVisibleAndOnline()" :busy="loading" @click="refreshStatus()">
        {{ customerCopy.text('export.refresh') }}
      </AwButton>
      <AwButton variant="primary" :disabled="busy || offlineSession || !isVisibleAndOnline()" :busy="submitting" @click="prepareExport">
        {{ customerCopy.text(pendingKeyRejected ? 'export.prepare-rejected-button' : pendingKey ? 'export.prepare-pending-button' : job || missing ? 'export.prepare-existing-button' : 'export.prepare-button') }}
      </AwButton>
      <AwButton v-if="job?.state === 'ready'" :disabled="!canDownload" :busy="downloading" @click="downloadExport">
        {{ customerCopy.text(nativePlatform ? 'export.share-json' : 'export.download-json') }}
      </AwButton>
      <AwButton v-if="job" variant="danger" :disabled="busy || offlineSession || !isVisibleAndOnline()" :busy="deleting" @click="deleteTechnicalJob">
        {{ customerCopy.text('export.delete-technical-job') }}
      </AwButton>
    </div>
  </section>
</template>

<style scoped>
.customer-data-exports { align-items: start; }
.customer-data-exports h2, .customer-data-exports p { margin: 0; }
.customer-data-exports__actions { display: flex; flex-wrap: wrap; gap: var(--aw-space-3); }
</style>

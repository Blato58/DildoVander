<script setup lang="ts">
import { Capacitor } from '@capacitor/core';
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, accountTransport, offlineSession, shareNativeAccountDataFile } from '../identity/session';
import {
  CustomerDataExportClient,
  CustomerDataExportError,
  customerDataExportReasonCopy,
  deliverBrowserDownload,
  exportErrorCopy,
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
const errorMessage = ref('');
const deliveryMessage = ref('');
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
const stateLabel = computed(() => {
  if (pendingKeyRejected.value && !job.value) return 'Server odmítl předchozí klíč požadavku. Nový export můžete výslovně vyžádat s novým klíčem.';
  if (pendingKey.value && !job.value) return 'Požadavek čeká na vaše potvrzení opakování.';
  if (missing.value) return 'Předchozí export už pro toto přihlášení není dostupný.';
  if (!job.value && hasStoredJob.value) return loading.value ? 'Ověřuji uložený export.' : 'Stav uloženého exportu se nepodařilo ověřit.';
  if (!job.value) return 'Zatím nemáte připravený export.';
  return {
    requested: 'Export byl vyžádán.',
    preparing: 'Export se připravuje.',
    ready: 'Export je připraven.',
    failed: 'Export se nepodařilo připravit.',
  }[job.value.state];
});
const reason = computed(() => job.value?.reason ? customerDataExportReasonCopy[job.value.reason] : '');

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
      errorMessage.value = exportErrorCopy(failure);
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
    const confirmed = window.confirm('Požádat o export? Pokud už účet obsahuje jinou úlohu exportu, server ji nahradí a zneplatní její soubor. Tímto se nemažou zdrojová data.');
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
      errorMessage.value = exportErrorCopy(failure);
      pendingKeyRejected.value = failure instanceof CustomerDataExportError && failure.status === 409
        && !!client.recoveryMetadata.pendingIdempotencyKey;
    }
    if (!disposed && attempt === generation) pendingKey.value = !!client.recoveryMetadata.pendingIdempotencyKey;
  } finally { if (!disposed && attempt === generation) submitting.value = false; }
}

async function deleteTechnicalJob(): Promise<void> {
  if (!allowed.value || busy.value || offlineSession.value || !isVisibleAndOnline() || !client.recoveryMetadata.jobId) return;
  const confirmed = window.confirm('Smazat pouze technickou úlohu exportu? Zdrojová data, postava ani památky se tím nesmažou.');
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
    if ((failure as Error)?.name !== 'AbortError' && !disposed && attempt === generation) errorMessage.value = exportErrorCopy(failure);
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
        ? 'Systém oznámil dokončení sdílení. To nepotvrzuje uložení ani doručení souboru.'
        : 'Sdílení bylo zrušeno. Soubor můžete znovu výslovně sdílet.';
    } else {
      await (props.deliver ?? deliverBrowserDownload)(file, 'customer-data-export.json');
    }
    if (!disposed && attempt === generation && allowed.value && job.value === selected) stale.value = false;
  } catch (failure) {
    if ((failure as Error)?.name !== 'AbortError' && !disposed && attempt === generation) errorMessage.value = exportErrorCopy(failure);
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
    <h2 id="customer-data-export-title">Stažení údajů účtu</h2>
    <AwNotice tone="info" title="Co export obsahuje">
      Zahrne identifikátor účtu, metadata historie postav a rozhovorů, identifikátory a revize vašich původních soukromých památek a pouze právě dostupná povolená fakta z paměti. Prázdný seznam faktů nepotvrzuje, že další data neexistují.
      Nezahrne celé příběhy, fotografie ani jiné soubory, přepisy, podklady postav či jejich AI obohacení, údaje jiných lidí, propojené soukromé památky jiných vlastníků, přihlašovací údaje, bezpečnostní doklady, logy poskytovatelů ani prompty.
    </AwNotice>

    <AwNotice v-if="nativePlatform" tone="warning">
      Soubor předáte systémové nabídce sdílení. Zvolená aplikace může uchovat vlastní kopii; po předání ji zde nelze odvolat.
    </AwNotice>
    <AwNotice v-if="offlineSession || !isVisibleAndOnline()" tone="warning">
      Pro přípravu a ověření exportu je potřeba připojení k internetu.
    </AwNotice>
    <AwNotice v-if="pendingKey && !job && !pendingKeyRejected" tone="warning">
      Předchozí požadavek nemá potvrzený výsledek. Nic neposíláme automaticky; ruční opakování použije stejný klíč požadavku.
    </AwNotice>
    <AwNotice v-if="pendingKeyRejected" tone="warning">
      Server tento klíč odmítl. Další výslovná žádost použije nový klíč a může nahradit dřívější technickou úlohu.
    </AwNotice>
    <AwNotice v-if="missing" tone="warning">
      Tento export není pro aktuální přihlášení dostupný. Připravit nový můžete výslovně tlačítkem níže.
    </AwNotice>
    <AwNotice v-if="job" :tone="job.state === 'failed' ? 'warning' : 'info'" title="Stav exportu">
      <p>{{ stateLabel }}</p>
      <p>Vyžádáno: {{ job.requestedAt }}</p>
      <p v-if="job.readyAt">Připraveno: {{ job.readyAt }}</p>
      <p v-if="job.downloadExpiresAt">Stažení vyprší: {{ job.downloadExpiresAt }}</p>
      <p v-if="reason">{{ reason }}</p>
      <p v-if="isExpired()">Lhůta pro stažení vypršela. Obnovte stav nebo připravte nový export.</p>
    </AwNotice>
    <AwNotice v-if="errorMessage" tone="danger">{{ errorMessage }}</AwNotice>
    <AwNotice v-if="deliveryMessage" tone="info">{{ deliveryMessage }}</AwNotice>
    <p v-if="!allowed" role="status">Export je dostupný po přihlášení k účtu, jehož údaje chcete stáhnout.</p>
    <p v-else role="status" aria-live="polite">{{ stateLabel }}</p>

    <div v-if="allowed" class="customer-data-exports__actions">
      <AwButton :disabled="busy || offlineSession || !isVisibleAndOnline()" :busy="loading" @click="refreshStatus()">
        Obnovit stav exportu
      </AwButton>
      <AwButton variant="primary" :disabled="busy || offlineSession || !isVisibleAndOnline()" :busy="submitting" @click="prepareExport">
        {{ pendingKeyRejected ? 'Vyžádat nový export' : pendingKey ? 'Ručně opakovat požadavek' : job || missing ? 'Připravit nový export' : 'Připravit export' }}
      </AwButton>
      <AwButton v-if="job?.state === 'ready'" :disabled="!canDownload" :busy="downloading" @click="downloadExport">
        {{ nativePlatform ? 'Sdílet JSON' : 'Stáhnout JSON' }}
      </AwButton>
      <AwButton v-if="job" variant="danger" :disabled="busy || offlineSession || !isVisibleAndOnline()" :busy="deleting" @click="deleteTechnicalJob">
        Smazat pouze úlohu exportu
      </AwButton>
    </div>
  </section>
</template>

<style scoped>
.customer-data-exports { align-items: start; }
.customer-data-exports h2, .customer-data-exports p { margin: 0; }
.customer-data-exports__actions { display: flex; flex-wrap: wrap; gap: var(--aw-space-3); }
</style>

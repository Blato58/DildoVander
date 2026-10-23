<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, accountTransport, offlineSession, signingOut } from '../identity/session';
import { SessionRecoveryController, type SessionRecoveryState } from '../customer/sessionRecovery';
import {
  TonightWaitingClient,
  isTonightWaitingDeviceId,
  tonightWaitingAvailabilityText,
  tonightWaitingErrorText,
  type TonightWaitingCommandMetadata,
  type TonightWaitingEvent,
  type TonightWaitingSnapshot,
} from './tonightWaiting';

const props = defineProps<{ deviceId: string | null; authorized: boolean }>();

const snapshot = ref<TonightWaitingSnapshot | null>(null);
const recoveryState = reactive<SessionRecoveryState>({
  refreshing: false,
  stale: false,
  retrying: false,
  retryDelayMs: null,
  error: undefined,
});
const pendingCommand = ref<TonightWaitingCommandMetadata | null>(null);
const actionBusy = ref(false);
const actionError = ref('');
const visible = ref(typeof document === 'undefined' || document.visibilityState !== 'hidden');
const online = ref(typeof navigator === 'undefined' || navigator.onLine);
const isSignedIn = computed(() => !!account.value && !signingOut.value);
const canRead = computed(() => isSignedIn.value && props.authorized && isTonightWaitingDeviceId(props.deviceId)
  && !accountDeletionRequested.value && !offlineSession.value && online.value && visible.value);
const currentReadFresh = computed(() => canRead.value && !!snapshot.value
  && currentReadVerified.value && !recoveryState.refreshing && !recoveryState.stale);
const isCurrentReadFresh = ref(false);

let disposed = false;
let generation = 0;
let currentReadVerified = isCurrentReadFresh;
let recovery: SessionRecoveryController<TonightWaitingSnapshot> | undefined;
let client: TonightWaitingClient | undefined;
let commandController: AbortController | undefined;

const event = computed(() => snapshot.value?.event ?? null);
const head = computed(() => snapshot.value?.head ?? null);
const canOpen = computed(() => currentReadFresh.value && !!snapshot.value
  && (!event.value || event.value.state === 'closed') && !pendingCommand.value && !actionBusy.value);
const canPause = computed(() => currentReadFresh.value && event.value?.state === 'open'
  && !pendingCommand.value && !actionBusy.value);
const canResume = computed(() => currentReadFresh.value && event.value?.state === 'paused'
  && !pendingCommand.value && !actionBusy.value);
const canClose = computed(() => currentReadFresh.value && !!event.value && event.value.state !== 'closed'
  && !pendingCommand.value && !actionBusy.value);
const canSkip = computed(() => currentReadFresh.value && event.value?.state === 'open'
  && !!head.value && !pendingCommand.value && !actionBusy.value);
const canRetryPending = computed(() => currentReadFresh.value && !!pendingCommand.value && !actionBusy.value);
const eventStateLabel = computed(() => event.value?.state === 'open' ? 'Otevřená'
  : event.value?.state === 'paused' ? 'Pozastavená'
    : event.value?.state === 'closed' ? 'Uzavřená' : 'Není otevřena');

function isContextCurrent(expectedGeneration: number, expectedAccountId: string, expectedDeviceId: string): boolean {
  return !disposed && generation === expectedGeneration && account.value?.id === expectedAccountId
    && props.deviceId === expectedDeviceId && props.authorized && !accountDeletionRequested.value
    && !offlineSession.value && !signingOut.value && visible.value && online.value;
}

function stopRecovery(): void {
  recovery?.dispose();
  recovery = undefined;
  client = undefined;
  snapshot.value = null;
  pendingCommand.value = null;
  currentReadVerified.value = false;
  recoveryState.refreshing = false;
  recoveryState.stale = false;
  recoveryState.retrying = false;
  recoveryState.retryDelayMs = null;
  recoveryState.error = undefined;
}

function createController(expectedGeneration: number, accountId: string, deviceId: string): void {
  const nextClient = new TonightWaitingClient({ accountId, transport: accountTransport });
  client = nextClient;
  recovery = new SessionRecoveryController<TonightWaitingSnapshot>({
    state: recoveryState,
    pollIntervalMs: 4000,
    refresh: signal => nextClient.getSnapshot(deviceId, signal),
    hasActiveSession: value => !!value.event && value.event.state !== 'closed',
    onValue: value => {
      if (!isContextCurrent(expectedGeneration, accountId, deviceId) || client !== nextClient) return;
      snapshot.value = value;
      currentReadVerified.value = true;
      try {
        nextClient.reconcile(deviceId, value);
        pendingCommand.value = nextClient.getPendingCommand(deviceId);
      } catch (error) {
        pendingCommand.value = null;
        actionError.value = tonightWaitingErrorText(error);
      }
    },
  });
}

function syncActivity(): void {
  if (disposed || !canRead.value || !props.deviceId || !account.value) {
    recovery?.setActive(false);
    return;
  }
  if (!recovery || !client) {
    const expectedGeneration = generation;
    createController(expectedGeneration, account.value.id, props.deviceId);
  }
  recovery?.setActive(true);
}

function resetContext(): void {
  ++generation;
  commandController?.abort();
  commandController = undefined;
  actionBusy.value = false;
  actionError.value = '';
  stopRecovery();
  if (canRead.value && account.value && props.deviceId) {
    createController(generation, account.value.id, props.deviceId);
  }
  syncActivity();
}

function refresh(): void {
  if (!canRead.value || !recovery) return;
  currentReadVerified.value = false;
  actionError.value = '';
  void recovery.refresh();
}

function commandBodyForState(current: TonightWaitingEvent, state: 'open' | 'paused' | 'closed') {
  if (!client || !props.deviceId || !currentReadFresh.value) return Promise.resolve(undefined);
  return runCommand((signal) => client!.changeState(props.deviceId!, current.id, current.revision, state, signal));
}

async function runCommand(send: (signal: AbortSignal) => Promise<TonightWaitingSnapshot>): Promise<void> {
  if (!client || !props.deviceId || !account.value || !currentReadFresh.value || actionBusy.value) return;
  actionBusy.value = true;
  actionError.value = '';
  const requestedAccountId = account.value.id;
  const requestedDeviceId = props.deviceId;
  const requestedGeneration = generation;
  const controller = new AbortController();
  commandController?.abort();
  commandController = controller;
  currentReadVerified.value = false;
  try {
    await send(controller.signal);
    if (!isContextCurrent(requestedGeneration, requestedAccountId, requestedDeviceId)) return;
    refresh();
  } catch (error) {
    if (!controller.signal.aborted && isContextCurrent(requestedGeneration, requestedAccountId, requestedDeviceId)) {
      actionError.value = tonightWaitingErrorText(error);
      refresh();
    }
  } finally {
    if (commandController === controller) commandController = undefined;
    if (generation === requestedGeneration) actionBusy.value = false;
  }
}

function openCurrentEvening(): void {
  if (!canOpen.value || !client || !props.deviceId) return;
  const previous = event.value;
  void runCommand(signal => client!.open(props.deviceId!, previous?.id ?? null, previous?.revision ?? null, signal));
}

function pauseEvent(): void {
  if (canPause.value && event.value) void commandBodyForState(event.value, 'paused');
}

function resumeEvent(): void {
  if (canResume.value && event.value) void commandBodyForState(event.value, 'open');
}

function closeEvent(): void {
  if (canClose.value && event.value) void commandBodyForState(event.value, 'closed');
}

function skipHead(): void {
  if (!canSkip.value || !client || !props.deviceId || !event.value || !head.value) return;
  const current = event.value;
  const currentHead = head.value;
  void runCommand(signal => client!.skipHead(props.deviceId!, current.id,
    currentHead.entryId, currentHead.revision, signal));
}

function retryPending(): void {
  if (!canRetryPending.value || !client || !props.deviceId) return;
  void runCommand(signal => client!.retryPending(props.deviceId!, signal));
}

function onVisibilityChange(): void {
  visible.value = document.visibilityState !== 'hidden';
  currentReadVerified.value = false;
  if (!visible.value) commandController?.abort();
  syncActivity();
}

function onOnline(): void {
  online.value = true;
  currentReadVerified.value = false;
  syncActivity();
}

function onOffline(): void {
  online.value = false;
  currentReadVerified.value = false;
  commandController?.abort();
  syncActivity();
}

watch(() => [account.value?.id ?? '', props.deviceId ?? '', props.authorized,
  accountDeletionRequested.value, offlineSession.value, signingOut.value] as const,
resetContext, { flush: 'sync' });

onMounted(() => {
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onOnline);
  window.addEventListener('offline', onOffline);
  syncActivity();
});

onBeforeUnmount(() => {
  disposed = true;
  ++generation;
  commandController?.abort();
  commandController = undefined;
  recovery?.dispose();
  recovery = undefined;
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onOnline);
  window.removeEventListener('offline', onOffline);
});
</script>

<template>
  <section class="aw-card tonight-waiting" aria-labelledby="tonight-waiting-title">
    <div class="tonight-waiting__header">
      <div>
        <h2 id="tonight-waiting-title">Čekací seznam</h2>
        <p>Ruční správa pořadí hostů pro vybraný kufr.</p>
      </div>
      <AwButton :disabled="!canRead || recoveryState.refreshing" @click="refresh">
        {{ recoveryState.refreshing ? 'Obnovuji…' : 'Obnovit stav' }}
      </AwButton>
    </div>

    <AwNotice v-if="!isSignedIn || !props.authorized || accountDeletionRequested || offlineSession || signingOut"
      tone="warning" :live="false">
      Stav a ovládání čekacího seznamu vyžadují přihlášení a aktuální oprávnění obsluhy.
    </AwNotice>
    <AwNotice v-else-if="!isTonightWaitingDeviceId(props.deviceId)" tone="info" :live="false">
      Vyberte oprávněný kufr pro zobrazení čekacího seznamu.
    </AwNotice>
    <AwNotice v-else-if="!online || !visible" tone="warning" :live="false">
      Ovládání je pozastavené, dokud je tato obrazovka skrytá nebo zařízení offline. Po návratu stav ověřte.
    </AwNotice>
    <AwNotice v-else-if="recoveryState.stale" tone="warning" :live="false">
      Poslední stav je neověřený. Obnovte jej před každým zásahem.
    </AwNotice>
    <AwNotice v-else-if="recoveryState.refreshing && !snapshot" tone="info" :live="false">
      Načítá se aktuální stav čekacího seznamu.
    </AwNotice>
    <AwNotice v-else-if="recoveryState.error || actionError" tone="warning">
      {{ actionError || tonightWaitingErrorText(recoveryState.error) }}
    </AwNotice>

    <template v-if="snapshot">
      <div class="tonight-waiting__summary" :class="{ 'tonight-waiting__summary--stale': !currentReadFresh }">
        <p><strong>Událost:</strong> {{ eventStateLabel }}<span v-if="!currentReadFresh"> · poslední známý stav</span></p>
        <p><strong>Místa v seznamu:</strong> {{ snapshot.count }}</p>
        <p><strong>Dostupnost kufru:</strong> {{ tonightWaitingAvailabilityText(snapshot.availability) }}</p>
      </div>
      <p v-if="head" class="tonight-waiting__head">
        Vedoucí místo: {{ head.state === 'claimed' ? 'čeká na dokončení rezervace' : 'čeká na obsluhu' }}.
      </p>
      <p v-else-if="snapshot.count === 0" class="tonight-waiting__head">V seznamu nyní nikdo nečeká.</p>

      <AwNotice v-if="pendingCommand" tone="warning" :live="false">
        Předchozí {{ pendingCommand.kind === 'skip' ? 'vynechání' : pendingCommand.kind === 'open' ? 'otevření' : 'změna stavu' }}
        čeká na ověření. Nejprve obnovte stav; opakování použije stejný požadavek.
      </AwNotice>

      <div class="tonight-waiting__actions">
        <AwButton v-if="canOpen" variant="primary" :busy="actionBusy" @click="openCurrentEvening">
          Otevřít seznam pro aktuální večer
        </AwButton>
        <AwButton v-if="canPause" :busy="actionBusy" @click="pauseEvent">Pozastavit přijímání</AwButton>
        <AwButton v-if="canResume" variant="primary" :busy="actionBusy" @click="resumeEvent">Obnovit přijímání</AwButton>
        <AwButton v-if="canClose" variant="danger" :busy="actionBusy" @click="closeEvent">Uzavřít seznam</AwButton>
        <AwButton v-if="canSkip" variant="danger" :busy="actionBusy" @click="skipHead">Vynechat vedoucí místo</AwButton>
        <AwButton v-if="canRetryPending" :busy="actionBusy" @click="retryPending">
          Opakovat stejný požadavek
        </AwButton>
      </div>
    </template>
  </section>
</template>

<style scoped>
.tonight-waiting { display: grid; gap: var(--aw-space-3); }
.tonight-waiting__header { display: flex; flex-wrap: wrap; align-items: start; justify-content: space-between; gap: var(--aw-space-3); }
.tonight-waiting h2, .tonight-waiting p { margin: 0; }
.tonight-waiting__header p { color: var(--aw-muted); }
.tonight-waiting__summary { display: grid; gap: var(--aw-space-2); }
.tonight-waiting__summary--stale { opacity: .8; }
.tonight-waiting__actions { display: flex; flex-wrap: wrap; gap: var(--aw-space-2); }
.tonight-waiting__head { color: var(--aw-muted); }
</style>

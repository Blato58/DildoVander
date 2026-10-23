<script setup lang="ts">
import { computed, markRaw, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwNotice from '../../components/AwNotice.vue';
import { canOperateSuitcase } from '../../application/access';
import { DirectorClient } from '../director/DirectorClient';
import { DirectorSession, type DirectorLocal } from '../director/DirectorSession';
import { account, accountRequest, accountTransport, type AccountDetails } from '../identity/session';
import { deviceSession as device } from '../suitcase/deviceSession';
import { SessionRecoveryController } from '../customer/sessionRecovery';
import TonightView from './TonightView.vue';
import TonightWaiting from './TonightWaiting.vue';
import type { AudioHealthObservation, DeviceHealthObservation, NetworkHealthObservation, ScopedObservation, TonightScope } from './tonightProjection';
import { useTonight, type TonightLocalDeviceObservation, type TonightPermission, type TonightSessionSnapshot } from './useTonight';

const details = ref<AccountDetails | null>(null);
const loadingDetails = ref(false);
const detailsError = ref(false);
const selectedDeviceId = ref('');
const accountIdentityKey = computed(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : '');
const authorizedDevices = computed(() => (details.value?.devices ?? []).filter(item => item.canOperate));
let accountEpoch = 0;
let accountRequestController: AbortController | undefined;
let manuallySelectedDevice = false;

function isCurrentAccountRequest(epoch: number, identityKey: string): boolean {
  return epoch === accountEpoch && identityKey === accountIdentityKey.value;
}

watch(accountIdentityKey, identityKey => {
  const epoch = ++accountEpoch;
  accountRequestController?.abort();
  accountRequestController = undefined;
  details.value = null;
  detailsError.value = false;
  loadingDetails.value = false;
  selectedDeviceId.value = '';
  manuallySelectedDevice = false;

  const currentAccount = account.value;
  if (!identityKey || !currentAccount || !canOperateSuitcase(currentAccount)) return;

  const controller = new AbortController();
  accountRequestController = controller;
  loadingDetails.value = true;
  void accountRequest<AccountDetails>('/app/v1/account', 'GET', undefined, controller.signal)
    .then(value => {
      if (controller.signal.aborted || !isCurrentAccountRequest(epoch, identityKey)) return;
      details.value = value;
    })
    .catch(() => {
      if (!controller.signal.aborted && isCurrentAccountRequest(epoch, identityKey)) detailsError.value = true;
    })
    .finally(() => {
      if (isCurrentAccountRequest(epoch, identityKey)) loadingDetails.value = false;
    });
}, { immediate: true, flush: 'sync' });

watch([authorizedDevices, () => device.savedDevice.value.deviceId], ([available, savedDeviceId]) => {
  if (manuallySelectedDevice) return;
  selectedDeviceId.value = available.find(item => item.deviceId === savedDeviceId)?.deviceId
    ?? available[0]?.deviceId ?? '';
}, { flush: 'sync' });

const permission = computed<TonightPermission>(() => {
  const currentAccount = account.value;
  if (!currentAccount) return 'unknown';
  if (!canOperateSuitcase(currentAccount)) return 'denied';
  if (loadingDetails.value || !details.value) return 'unknown';
  return authorizedDevices.value.some(item => item.deviceId === selectedDeviceId.value) ? 'allowed' : 'denied';
});

const scope = computed<TonightScope | null>(() => permission.value === 'allowed' && account.value && selectedDeviceId.value
  ? { accountId: account.value.id, deviceId: selectedDeviceId.value }
  : null);

const localMatches = (deviceId: string): boolean => device.savedDevice.value.deviceId === deviceId
  && device.snapshot.value?.source === 'native-device'
  && device.snapshot.value.diagnostics?.deviceId === deviceId
  && device.snapshot.value.phase !== 'disconnected';

const directorLocal: DirectorLocal = markRaw({
  matches: localMatches,
  offline: device.requestOffline,
  nextVisit: () => device.startVoice(false),
  stop: () => { void device.stop(); },
  cancelSpeech: () => { void device.stopVoice(); },
});
const director = reactive(new DirectorSession(
  markRaw(new DirectorClient(accountTransport)),
  directorLocal,
));

const localDevice = computed<TonightLocalDeviceObservation | null>(() => {
  const snapshot = device.snapshot.value;
  const deviceId = selectedDeviceId.value;
  if (!snapshot || !deviceId || snapshot.source !== 'native-device'
    || device.savedDevice.value.deviceId !== deviceId || snapshot.diagnostics?.deviceId !== deviceId) return null;
  return {
    source: snapshot.source,
    deviceId,
    phase: snapshot.phase,
    busy: snapshot.busy,
    readyMask: snapshot.readyMask,
    voice: { canStart: snapshot.voice?.canStart },
  };
});
const deviceReceivedAtMs = computed<number | null>(() => localDevice.value ? device.lastObservedAt.value : null);
// Neither browser reachability nor the local audio phase is authoritative, device-correlated health evidence.
const network = ref<ScopedObservation<NetworkHealthObservation> | undefined>(undefined);
const audio = ref<ScopedObservation<AudioHealthObservation> | undefined>(undefined);

const tonight = useTonight({
  scope,
  permission,
  localDevice,
  deviceReceivedAtMs,
  network,
  audio,
  accountRequest,
  createRecovery: options => new SessionRecoveryController<TonightSessionSnapshot>(options),
  director,
  now: () => Date.now(),
});
const model = tonight.model;

function selectDevice(event: Event): void {
  const value = (event.target as HTMLSelectElement).value;
  if (!authorizedDevices.value.some(item => item.deviceId === value)) return;
  manuallySelectedDevice = true;
  selectedDeviceId.value = value;
}

onMounted(() => {
  if (device.native) void device.refreshSaved();
});

onBeforeUnmount(() => {
  ++accountEpoch;
  accountRequestController?.abort();
  accountRequestController = undefined;
  // useTonight owns leaveView and intentionally retains the global Stop registration.
});
</script>

<template>
  <div class="page-stack tonight-page">
    <section class="aw-card tonight-context" aria-label="Vybraný provozní kontext">
      <p><strong>Účet obsluhy:</strong> {{ account?.displayName ?? 'ověřuje se' }}</p>
      <label v-if="authorizedDevices.length" for="tonight-device-select">
        Vybraný kufr
        <select id="tonight-device-select" class="aw-input" :value="selectedDeviceId" @change="selectDevice">
          <option v-for="item in authorizedDevices" :key="item.deviceId" :value="item.deviceId">{{ item.deviceId }}</option>
        </select>
      </label>
      <p v-else-if="loadingDetails">Ověřují se oprávnění pro zařízení.</p>
      <p v-else-if="detailsError">Oprávnění a seznam zařízení se nepodařilo ověřit. Obnovte stránku nebo zkontrolujte účet.</p>
      <nav class="tonight-advanced-links" aria-label="Podrobné provozní nástroje">
        <RouterLink to="/suitcase/director" class="aw-button aw-button--secondary">Režie kufru</RouterLink>
        <RouterLink to="/admin/customer-sessions" class="aw-button aw-button--secondary">Zákaznické relace</RouterLink>
      </nav>
    </section>
    <AwNotice v-if="detailsError" tone="warning">Soukromý provozní stav zůstává skrytý, dokud se nepodaří ověřit oprávnění.</AwNotice>
    <TonightWaiting :device-id="selectedDeviceId || null" :authorized="permission === 'allowed'" />
    <TonightView
      :model="model"
      @refresh="tonight.refresh()"
      @next-visit="tonight.nextVisit()"
      @quick-finale="tonight.quickFinale()"
      @guest-leave="tonight.guestLeave()"
    />
  </div>
</template>

<style scoped>
.tonight-context { display: grid; gap: var(--aw-space-3); }
.tonight-context p { margin: 0; }
.tonight-context label { display: grid; gap: var(--aw-space-2); max-width: 28rem; }
.tonight-advanced-links { display: flex; flex-wrap: wrap; gap: var(--aw-space-2); }
</style>

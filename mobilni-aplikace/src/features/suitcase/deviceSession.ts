import type { OfflineCommandKind, OfflineCommandOptions } from '../device_connection/SimulatorDeviceConnection';
import { computed, ref, shallowRef } from 'vue';
import { Capacitor, registerPlugin } from '@capacitor/core';
import { FakeDeviceConnection, type DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import { NativeDeviceConnection, nativeBridge, type SavedNativeDevice } from '../device_connection/NativeDeviceConnection';
import { SimulatorDeviceConnection } from '../device_connection/SimulatorDeviceConnection';
import { ProvisioningClient, type ProvisioningBridge, type ProvisioningDevice, type ProvisioningStatus } from '../provisioning/ProvisioningClient';
import { DebugJournal } from '../debug/DebugJournal';
import { nativeDebug } from './nativeDebug';

type Connection = FakeDeviceConnection | SimulatorDeviceConnection | NativeDeviceConnection;
export type DeviceTarget = 'unavailable' | 'demo' | 'simulator' | 'native';
const snapshot = shallowRef<DeviceSnapshot | null>(null);
const target = ref<DeviceTarget>('unavailable');
const pending = ref(false);
const stopPending = ref(false);
const message = ref('Vyberte a připojte kufr. Web může výslovně otevřít označenou simulaci.');
const savedDevice = shallowRef<SavedNativeDevice>({ paired: false });
// Hardware contact history is scoped to this app session and device identity.
// It grants no account or local control authority.
const observedDevices = shallowRef(new Map<string, number>());
const lastObservedAt = computed(() => savedDevice.value.deviceId
  ? observedDevices.value.get(savedDevice.value.deviceId) ?? null : null);
const discovered = shallowRef<Array<{ deviceId: string; host: string; capabilities: string[] }>>([]);
const bleDevices = shallowRef<ProvisioningDevice[]>([]);
const bleStatus = shallowRef<ProvisioningStatus | null>(null);
const bleMessage = ref('Bluetooth vyžaduje nativní aplikaci na iPhonu.');
const blePending = ref(false);
const journal = new DebugJournal();
const journalEntries = shallowRef(journal.read());
const native = Capacitor.getPlatform() === 'ios';
const provisioningBridge = registerPlugin<ProvisioningBridge>('KufrProvisioning');
let connection: Connection | null = null;
let generation = 0;
let connectionEpoch = 0;
let labConnectionGeneration = 0;
let heartbeat: ReturnType<typeof setInterval> | undefined;
let listening = false;
const provisioning = new ProvisioningClient(provisioningBridge, (status, text, busy) => {
  bleStatus.value = status;
  if (text) bleMessage.value = text;
  blePending.value = busy;
});

function observe(value: DeviceSnapshot): void {
  if (snapshot.value?.phase !== 'disconnected' && value.phase === 'disconnected'
    || snapshot.value?.diagnostics?.bootId !== value.diagnostics?.bootId) ++labConnectionGeneration;
  if (stopPending.value && (value.phase === 'stopped' || value.phase === 'disconnected')) {
    stopPending.value = false; message.value = value.status;
  }
  snapshot.value = value;
  const evidence = value.diagnostics;
  if (value.source === 'native-device' && value.phase !== 'disconnected' && evidence?.deviceId
    && Number.isSafeInteger(evidence.lastObservedAtMs) && evidence.lastObservedAtMs! > 0
    && observedDevices.value.get(evidence.deviceId) !== evidence.lastObservedAtMs) {
    observedDevices.value = new Map(observedDevices.value).set(evidence.deviceId, evidence.lastObservedAtMs!);
  }
  journal.observe(value);
  journalEntries.value = journal.read();
}

function suspend(): void {
  ++generation;
  ++labConnectionGeneration;
  connection?.release();
  if (connection instanceof SimulatorDeviceConnection || connection instanceof NativeDeviceConnection) connection.suspend();
}

function startLifecycle(): void {
  if (listening) return;
  listening = true;
  heartbeat = setInterval(() => { if (!document.hidden) connection?.heartbeat(); }, 2000);
  document.addEventListener('visibilitychange', visibility);
  window.addEventListener('pagehide', suspend);
}

function visibility(): void {
  if (document.hidden) suspend();
  else if (connection instanceof SimulatorDeviceConnection) connection.connect();
}

function replaceConnection(nextTarget: DeviceTarget, create: (onChange: (value: DeviceSnapshot) => void) => Connection): void {
  suspend();
  if (connection instanceof FakeDeviceConnection && snapshot.value?.phase === 'running') connection.stop();
  connection = null;
  snapshot.value = null;
  target.value = nextTarget;
  stopPending.value = false;
  const epoch = ++connectionEpoch;
  connection = create(value => { if (epoch === connectionEpoch) observe(value); });
  startLifecycle();
}

async function perform(action: () => Promise<void>): Promise<void> {
  if (pending.value) return;
  pending.value = true;
  try { await action(); }
  catch (error) { message.value = error instanceof Error ? error.message : 'Připojení selhalo. Zkuste je obnovit.'; }
  finally { pending.value = false; }
}

async function refreshSaved(): Promise<void> {
  if (!native) return;
  savedDevice.value = await nativeBridge.savedDevice();
  message.value = savedDevice.value.paired
    ? `Uložený kufr ${savedDevice.value.deviceId} · ${savedDevice.value.host}. Připojte jej výslovně.`
    : 'Nejprve ověřte kufr přes Bluetooth pomocí PoP ze servisního štítku.';
}

function disconnect(): void {
  suspend();
  if (snapshot.value) observe({ ...snapshot.value, phase: 'disconnected', controlMode: 'observer', busy: false,
    status: 'Telefon odpojený. Samostatný kufr pokračuje bez telefonu.' });
}

async function connectSaved(): Promise<void> {
  if (!native) return;
  await perform(async () => {
    const requested = generation;
    if (!await provisioning.refreshLocalAddress() || generation !== requested || document.hidden) {
      message.value = 'Bluetooth adresu neobnovilo. Dokončete BLE připojení a zkuste znovu.';
      return;
    }
    const saved = await nativeBridge.savedDevice();
    if (generation !== requested || document.hidden) return;
    savedDevice.value = saved;
    if (!saved.paired || !saved.deviceId || !saved.host) {
      message.value = 'Chybí ověřené párování v Keychain. Spárujte kufr přes Bluetooth.';
      return;
    }
    // Reuse the same adapter to preserve unresolved command reconciliation.
    if (!(connection instanceof NativeDeviceConnection) || target.value !== 'native' || connectedDeviceId !== saved.deviceId) {
      replaceConnection('native', onChange => new NativeDeviceConnection(onChange, saved.deviceId!));
      connectedDeviceId = saved.deviceId;
    }
    if (connection instanceof NativeDeviceConnection) connection.connect(saved.host);
    message.value = 'Připojuji uložený kufr jako pozorovatele. Ruční řízení se nepřebírá.';
  });
}
let connectedDeviceId: string | undefined;

async function discoverLocal(): Promise<void> {
  if (!native) return;
  await perform(async () => {
    const result = await nativeBridge.discover();
    discovered.value = result.devices.filter(item => !savedDevice.value.paired || item.deviceId === savedDevice.value.deviceId);
    message.value = result.failed ? 'LAN hledání selhalo. Ověřte oprávnění místní sítě; použijte zabezpečené BLE nebo ruční adresu.'
      : discovered.value.length ? 'Vyberte adresu svého spárovaného kufru.' : 'Žádný kufr v síti. Ověřte společnou Wi-Fi a napájení.';
    if (result.bleAddressAvailable) message.value += ' Adresa je dostupná také přes zabezpečené Bluetooth.';
  });
}

async function setAddress(host: string): Promise<void> {
  if (!native || !host.trim()) return;
  await perform(async () => { disconnect(); await nativeBridge.setAddress({ host: host.trim() }); await refreshSaved(); });
}

async function pairManual(deviceId: string, host: string, spkiSha256: string): Promise<void> {
  if (!native) return;
  await perform(async () => {
    disconnect(); await nativeBridge.pair({ deviceId: deviceId.trim(), host: host.trim(), spkiSha256: spkiSha256.trim() }); await refreshSaved();
  });
}

async function forgetDevice(): Promise<void> {
  if (!native) return;
  await perform(async () => { disconnect(); await nativeBridge.forget();
    if (savedDevice.value.deviceId) {
      const next = new Map(observedDevices.value); next.delete(savedDevice.value.deviceId); observedDevices.value = next;
    }
    ++connectionEpoch; connection = null; snapshot.value = null;
    target.value = 'unavailable'; connectedDeviceId = undefined; discovered.value = []; await refreshSaved(); });
}

function useDemo(): void {
  if (pending.value || native) return;
  replaceConnection('demo', onChange => new FakeDeviceConnection(onChange));
  message.value = 'Místní ukázka. Bez sítě, skutečné AI a fyzického kufru.';
}

async function useSimulator(): Promise<void> {
  if (native) return;
  await perform(async () => {
    const response = await fetch('/dev-mode');
    if (!response.ok || !response.headers.get('content-type')?.includes('application/json') ||
        (await response.json() as { simulator?: boolean }).simulator !== true) {
      message.value = 'Hostitelský E2 simulátor není dostupný. Spusťte jej podle vývojového návodu.'; return;
    }
    replaceConnection('simulator', onChange => new SimulatorDeviceConnection(onChange));
    message.value = 'E2 simulátor. Textové odpovědi a stav jsou simulované; nejde o hardware.';
  });
}

async function discoverBle(): Promise<void> {
  if (!native || blePending.value) return;
  await perform(async () => { await provisioning.disconnect(); bleMessage.value = 'Hledám Bluetooth kufry…';
    bleDevices.value = (await provisioningBridge.discover()).devices;
    bleMessage.value = bleDevices.value.length ? 'Vyberte kufr. Nativní dialog ověří PoP.' : 'Žádný kufr. Ověřte Bluetooth a servisní okno.'; });
}

function stop(): void {
  if (stopPending.value || !connection || !snapshot.value || ['stopped', 'disconnected'].includes(snapshot.value.phase)) return;
  stopPending.value = true;
  message.value = 'Čekám na potvrzení Stop. Fyzické zastavení není ověřené.';
  connection.stop();
}

function protocol(): SimulatorDeviceConnection | NativeDeviceConnection | null {
  return connection instanceof SimulatorDeviceConnection || connection instanceof NativeDeviceConnection ? connection : null;
}

export interface LabVoiceTarget {
  deviceId: string; bootId: string; revision: number; connectionEpoch: number;
  mode: 'gpt-live' | 'realtime'; voice: 'cedar' | 'marin' | 'coral';
}

function startLabVoice(imageConsent: false, testContext: string, expected: LabVoiceTarget): boolean {
  const current = connection;
  const value = snapshot.value;
  if (imageConsent !== false || !/^[A-Za-z0-9_-]{43}$/.test(testContext)
    || expected.connectionEpoch !== labConnectionGeneration
    || target.value !== 'native' || !(current instanceof NativeDeviceConnection)
    || !savedDevice.value.paired || savedDevice.value.deviceId !== expected.deviceId
    || value?.source !== 'native-device' || value.phase === 'disconnected'
    || value.diagnostics?.deviceId !== expected.deviceId || value.diagnostics.bootId !== expected.bootId
    || value.revision !== expected.revision || !value.capabilities?.includes('voiceTestContext')
    || !value.voice?.canStart || value.voice.mode !== expected.mode || value.voice.voice !== expected.voice) return false;
  // This is submission to the already pinned connection, never a physical ACK.
  return current.startVoice(false, testContext);
}

function stopLabVoice(expected: { deviceId: string; bootId: string; connectionEpoch: number; testContextId: string }): boolean {
  const current = connection;
  const value = snapshot.value;
  if (target.value !== 'native' || !(current instanceof NativeDeviceConnection)
    || expected.connectionEpoch !== labConnectionGeneration
    || value?.source !== 'native-device' || value.phase === 'disconnected'
    || !value.capabilities?.includes('voiceTestContext')
    || !/^[a-f0-9]{64}$/.test(expected.testContextId) || value.voice?.testContextId !== expected.testContextId
    || value.diagnostics?.deviceId !== expected.deviceId || value.diagnostics.bootId !== expected.bootId) return false;
  return current.stopVoice(expected.testContextId);
}

export const deviceSession = {
  snapshot, target, pending, stopPending, message, native, savedDevice, lastObservedAt, discovered, bleDevices, bleStatus, bleMessage, blePending, journalEntries,
  connected: computed(() => snapshot.value !== null && snapshot.value.phase !== 'disconnected'),
  refreshSaved: () => perform(refreshSaved), connectSaved, discoverLocal, setAddress, pairManual, forgetDevice, useDemo, useSimulator, disconnect, stop,
  requestOffline: async (kind: OfflineCommandKind, compartment?: number, commandId?: string, options?: OfflineCommandOptions) => {
    const current = connection;
    const epoch = connectionEpoch;
    const lifecycle = generation;
    if (target.value !== 'native' || !(current instanceof NativeDeviceConnection) || !snapshot.value
      || snapshot.value.source !== 'native-device' || snapshot.value.phase === 'disconnected') {
      throw new Error('Místní Show vyžaduje výslovně připojený a ověřený skutečný kufr.');
    }
    const result = await current.requestOffline(kind, compartment, commandId, options);
    if (epoch !== connectionEpoch || lifecycle !== generation || current !== connection) throw new Error('Vybraný kufr se změnil; výsledek patří původnímu spojení.');
    return result;
  },
  acquire: () => connection?.acquire() ?? false,
  release: () => connection?.release(),
  select: (compartment: number) => connection?.select(compartment) ?? false,
  reload: (compartment: number) => connection?.confirmReload(compartment) ?? false,
  inspectDemo: () => { if (connection instanceof FakeDeviceConnection) connection.inspectLocally(); },
  submitText: (text: string) => protocol()?.submitText(text) ?? false,
  cancelText: () => protocol()?.cancelConversation(),
  configureVoice: (mode: 'off' | 'realtime' | 'gpt-live', voice: 'marin' | 'cedar' | 'coral') => protocol()?.configureVoice(mode, voice) ?? false,
  startVoice: (consent: boolean) => protocol()?.startVoice(consent), stopVoice: () => protocol()?.stopVoice(),
  startLabVoice, stopLabVoice,
  labConnectionEpoch: () => labConnectionGeneration,
  startDance: () => protocol()?.startDance(), stopDance: () => protocol()?.stopDance(),
  discoverBle, connectBle: (name: string) => provisioning.connect(name), disconnectBle: () => provisioning.disconnect(),
  showConnections: (visible: boolean) => provisioning.show(visible),
  pairLocal: () => provisioning.pairLocal(async () => {
    disconnect(); ++connectionEpoch; connection = null; snapshot.value = null;
    target.value = 'unavailable'; connectedDeviceId = undefined;
    await refreshSaved();
  }),
  configureNetwork: (profile: 'hotspot' | 'fallback') => provisioning.perform(() => provisioningBridge.configure({ profile })),
  networkCommand: (command: 'test' | 'delete' | 'ota-check' | 'ota-install' | 'ota-policy', profile: 'hotspot' | 'fallback', automaticInstall?: boolean) =>
    provisioning.perform(() => provisioningBridge.command({ command, profile, automaticInstall })),
  refreshBle: () => provisioning.perform(() => provisioningBridge.status()),
  openSettings: () => perform(() => provisioningBridge.openSettings()),
  clearJournal: () => { journal.clear(); journalEntries.value = []; },
  exportJournal: () => journal.export(),
  dispose(): void {
    disconnect(); ++connectionEpoch;
    if (heartbeat) clearInterval(heartbeat);
    heartbeat = undefined; listening = false;
    document.removeEventListener('visibilitychange', visibility);
    window.removeEventListener('pagehide', suspend);
    provisioning.show(false);
  },
  async clearUserGrants(): Promise<void> {
    // Account logout must not erase the device's independent Keychain identity.
    nativeDebug.invalidatePrivateState();
    disconnect();
    // Begin scoped native cleanup before awaiting unrelated USB work. A slow
    // bench or native Stop must never leave old account content in the VM.
    const phoneCleanup = native ? nativeDebug.revokeUserGrants() : Promise.resolve();
    const benchCleanup = import('./servoBench').then(({ servoBench }) => servoBench.disconnect());
    await Promise.all([benchCleanup, phoneCleanup]);
  },
};

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';

const harness = vi.hoisted(() => ({
  platform: 'web',
  saved: vi.fn(), forget: vi.fn(), pair: vi.fn(), setAddress: vi.fn(), discover: vi.fn(),
  refreshBle: vi.fn(), stopDebug: vi.fn(), forgetProfile: vi.fn(), disconnectBench: vi.fn(),
  adapters: [] as Array<{ connect: ReturnType<typeof vi.fn>; acquire: ReturnType<typeof vi.fn>;
    requestOffline: ReturnType<typeof vi.fn>; release: ReturnType<typeof vi.fn>; suspend: ReturnType<typeof vi.fn>; stop: ReturnType<typeof vi.fn>; uncertain: string;
    publish: (value: DeviceSnapshot) => void }>,
}));
vi.mock('@capacitor/core', () => ({
  Capacitor: { getPlatform: () => harness.platform },
  registerPlugin: (name: string) => name === 'KufrDebug' ? { stop: harness.stopDebug, forgetProfile: harness.forgetProfile,
    status: async () => ({ configured: false, running: false, phase: 'idle', mode: 'unknown' }) } : {},
}));
vi.mock('../device_connection/NativeDeviceConnection', () => ({
  nativeBridge: { savedDevice: harness.saved, forget: harness.forget, pair: harness.pair, setAddress: harness.setAddress, discover: harness.discover },
  NativeDeviceConnection: class {
    connect = vi.fn(); acquire = vi.fn(); release = vi.fn(); suspend = vi.fn(); stop = vi.fn(); requestOffline = vi.fn(); uncertain = 'selection-in-flight';
    constructor(public publish: (value: DeviceSnapshot) => void) { harness.adapters.push(this); }
  },
}));
vi.mock('../provisioning/ProvisioningClient', () => ({
  ProvisioningClient: class { refreshLocalAddress = harness.refreshBle; show = vi.fn(); },
}));
vi.mock('./servoBench', () => ({ servoBench: { disconnect: harness.disconnectBench } }));
let session: (typeof import('./deviceSession'))['deviceSession'];
beforeEach(async () => {
  vi.resetModules(); vi.useFakeTimers(); vi.clearAllMocks(); harness.adapters = []; harness.platform = 'ios';
  harness.saved.mockResolvedValue({ paired: true, deviceId: 'kufr-1', host: '192.168.0.62' });
  harness.refreshBle.mockResolvedValue(true);
  harness.stopDebug.mockResolvedValue(undefined); harness.forgetProfile.mockResolvedValue(undefined);
  harness.disconnectBench.mockResolvedValue(undefined);
  session = (await import('./deviceSession')).deviceSession;
});
afterEach(() => { session.dispose(); vi.useRealTimers(); vi.unstubAllGlobals(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(finish => { resolve = finish; }); return { promise, resolve }; }

describe('explicit device connection boundary', () => {
  it('starts unavailable without network, lease, simulator or actuator work', () => {
    expect(session.snapshot.value).toBeNull(); expect(session.target.value).toBe('unavailable');
    expect(harness.adapters).toHaveLength(0); expect(harness.saved).not.toHaveBeenCalled();
  });
  it('refreshes BLE before connecting and preserves same-device unresolved command state', async () => {
    await session.connectSaved(); const adapter = harness.adapters[0];
    await session.connectSaved();
    expect(harness.refreshBle).toHaveBeenCalledTimes(2); expect(harness.adapters).toHaveLength(1);
    expect(adapter.connect).toHaveBeenCalledTimes(2); expect(adapter.connect).toHaveBeenCalledWith('192.168.0.62');
    expect(adapter.acquire).not.toHaveBeenCalled(); expect(adapter.uncertain).toBe('selection-in-flight');
  });
  it('does not use a stale route when BLE refresh is skipped or fails', async () => {
    harness.refreshBle.mockResolvedValue(false); await session.connectSaved();
    expect(harness.saved).not.toHaveBeenCalled(); expect(harness.adapters).toHaveLength(0);
    harness.refreshBle.mockRejectedValue(new Error('BLE lost')); await session.connectSaved();
    expect(harness.adapters).toHaveLength(0); expect(session.message.value).toBe('BLE lost');
  });
  it('invalidates a pending connect at both awaited boundaries on disconnect', async () => {
    const refresh = deferred<boolean>(); harness.refreshBle.mockReturnValueOnce(refresh.promise);
    const old = session.connectSaved(); session.disconnect(); refresh.resolve(true); await old;
    expect(harness.adapters).toHaveLength(0);
    const saved = deferred<{ paired: boolean; deviceId: string; host: string }>(); harness.saved.mockReturnValueOnce(saved.promise);
    const next = session.connectSaved(); await Promise.resolve(); session.disconnect();
    saved.resolve({ paired: true, deviceId: 'kufr-1', host: 'old-host' }); await next;
    expect(harness.adapters).toHaveLength(0);
  });
  it('ignores a late snapshot from a replaced native device', async () => {
    await session.connectSaved(); const previous = harness.adapters[0];
    harness.saved.mockResolvedValue({ paired: true, deviceId: 'kufr-2', host: 'new-host' }); await session.connectSaved();
    previous.publish({ source: 'native-device', phase: 'ready', controlMode: 'manual', readyMask: 63,
      activeCompartment: null, busy: false, status: 'stale privileged state' });
    expect(session.snapshot.value).toBeNull(); expect(harness.adapters).toHaveLength(2);
  });
  it('account change releases control and clears only user debug grants, retaining hardware pairing', async () => {
    await session.connectSaved(); const adapter = harness.adapters[0];
    await session.clearUserGrants(); expect(adapter.release).toHaveBeenCalled(); expect(adapter.suspend).toHaveBeenCalled();
    expect(harness.stopDebug).toHaveBeenCalledOnce(); expect(harness.forgetProfile).toHaveBeenCalledOnce();
    expect(harness.forget).not.toHaveBeenCalled(); expect(session.savedDevice.value.paired).toBe(true);
  });
  it('clears prior account content before a noncooperative bench disconnect or phone Stop completes', async () => {
    await session.connectSaved();
    const debug = (await import('./nativeDebug')).nativeDebug;
    debug.status.value = { configured: true, running: true, session: true, phase: 'listening', mode: 'openai',
      accessMode: 'account', transcript: [{ role: 'visitor', text: 'previous account guest' }], lastConversationId: 'private-id' };
    const bench = deferred<void>(); const stop = deferred<void>();
    harness.disconnectBench.mockReturnValue(bench.promise); harness.stopDebug.mockReturnValue(stop.promise);
    const clearing = session.clearUserGrants();
    expect(debug.status.value.transcript).toEqual([]); expect(debug.status.value.lastConversationId).toBeUndefined();
    expect(debug.status.value.configured).toBe(false); expect(harness.stopDebug).toHaveBeenCalledOnce();
    // A dynamic USB adapter load cannot delay native cancellation or redaction.
    await Promise.resolve(); await Promise.resolve();
    expect(harness.forgetProfile).not.toHaveBeenCalled();
    bench.resolve(); stop.resolve(); await clearing;
    expect(harness.disconnectBench).toHaveBeenCalledOnce(); expect(harness.forgetProfile).toHaveBeenCalledOnce();
    expect(session.savedDevice.value.paired).toBe(true); expect(harness.forget).not.toHaveBeenCalled();
  });
  it('Stop is submitted once and remains pending across unrelated heartbeats until terminal evidence', async () => {
    await session.connectSaved(); const adapter = harness.adapters[0];
    const state: DeviceSnapshot = { source: 'native-device', phase: 'ready', controlMode: 'observer', readyMask: 63,
      activeCompartment: null, busy: false, status: 'ready' };
    adapter.publish(state); session.stop(); adapter.publish({ ...state, status: 'ordinary heartbeat' }); session.stop();
    expect(adapter.stop).toHaveBeenCalledOnce(); expect(session.stopPending.value).toBe(true);
    expect(session.message.value).toContain('Čekám na potvrzení Stop');
    adapter.publish({ ...state, phase: 'stopped', status: 'Stopped by device' });
    expect(session.stopPending.value).toBe(false); expect(session.message.value).toBe('Stopped by device');
  });
  it('offline Show requires a live native snapshot and fences late receipts after disconnect', async () => {
    await expect(session.requestOffline('offline_preflight')).rejects.toThrow('skutečný kufr');
    await session.connectSaved(); const adapter = harness.adapters[0];
    await expect(session.requestOffline('offline_preflight')).rejects.toThrow('skutečný kufr');
    expect(adapter.requestOffline).not.toHaveBeenCalled();
    adapter.publish({ source: 'native-device', phase: 'ready', controlMode: 'manual', readyMask: 63,
      activeCompartment: null, busy: false, status: 'native fixture' });
    adapter.requestOffline.mockResolvedValueOnce({ state: 'missing_assets', generation: 2 });
    await expect(session.requestOffline('offline_preflight')).resolves.toEqual({ state: 'missing_assets', generation: 2 });
    expect(adapter.acquire).not.toHaveBeenCalled();
    const deferredReceipt = deferred<unknown>(); adapter.requestOffline.mockReturnValueOnce(deferredReceipt.promise);
    const pending = session.requestOffline('offline_arm');
    const rejection = expect(pending).rejects.toThrow('původnímu spojení');
    session.disconnect(); deferredReceipt.resolve({ state: 'ready', armed: true }); await rejection;
    expect(adapter.requestOffline).toHaveBeenCalledTimes(2);
  });

});

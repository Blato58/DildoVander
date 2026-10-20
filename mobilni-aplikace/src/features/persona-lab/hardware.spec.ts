import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { effectScope, ref, shallowRef } from 'vue';
import { flushPromises } from '@vue/test-utils';
import { useLabHardware } from './useLabHardware';
import { hardwareContextId, hardwareEligibility } from './hardware';
import { deviceSession } from '../suitcase/deviceSession';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import type { Catalog, LabSession } from './contracts';
const api = vi.hoisted(() => ({ request: vi.fn(), start: vi.fn(), stop: vi.fn(), configure: vi.fn(), epoch: 1 }));
const associationFixture = await vi.hoisted(async () => {
  const { ref } = await import('vue');
  return { canOperate: ref(true) };
});
vi.mock('../identity/session', () => ({ accountTransport: api.request }));
vi.mock('../suitcase/deviceSession', async () => {
  const { shallowRef } = await import('vue');
  return { deviceSession: { native: true, snapshot: shallowRef(null), savedDevice: shallowRef({ paired: true, deviceId: 'case-one' }),
    startLabVoice: api.start, stopLabVoice: api.stop, labConnectionEpoch: () => api.epoch, configureVoice: api.configure } };
});
vi.mock('../suitcase/deviceAssociation', () => ({ deviceAssociation: {
  canOperate: associationFixture.canOperate, refresh: vi.fn(async () => {}),
} }));
const boot = '11111111-1111-4111-8111-111111111111';
const contextId = '0f007385b6f9d4b7eeb2748605afe1a984a0a3bfa3f014d09e2a784ce9e5cd1a';
let session: LabSession; let stored: LabSession;
const cleanups: (() => void)[] = [];
function snapshot(): DeviceSnapshot {
  return { source: 'native-device', phase: 'ready', controlMode: 'observer', readyMask: 63, activeCompartment: null, busy: false,
    status: 'Test fixture', revision: 5, capabilities: ['voiceTestContext'],
    diagnostics: { deviceId: 'case-one', bootId: boot, eventSequence: 1, deviceUptimeMs: 100, receivedAtMs: 100,
      roundTripMs: null, faultCount: 0, devicePhase: 'ready' },
    voice: { available: true, mode: 'gpt-live', voice: 'cedar', phase: 'idle', error: null, pending: false,
      canConfigure: true, canStart: true, canStop: false, status: 'Synthetic voice' } };
}
beforeEach(() => {
  vi.useFakeTimers(); vi.setSystemTime('2026-10-20T12:00:00Z');
  api.request.mockReset();
  api.start.mockReset().mockImplementation(() => {
    const current = deviceSession.snapshot.value!;
    deviceSession.snapshot.value = { ...current, voice: { ...current.voice!, testContextId: contextId } };
    return true;
  });
  api.stop.mockReset().mockImplementation(target => target.connectionEpoch === api.epoch
    && deviceSession.snapshot.value?.voice?.testContextId === target.testContextId);
  api.configure.mockReset().mockReturnValue(true);
  api.epoch = 1; deviceSession.native = true; deviceSession.snapshot.value = snapshot(); associationFixture.canOperate.value = true;
  session = { id: 'session', ownerId: 'owner', status: 'open', engine: 'gpt-live-1', platform: 'ios', label: 'Synthetic hardware test',
    config: { id: 'r', voice: 'cedar', engine: 'gpt-live-1' }, turns: [], events: [] } as unknown as LabSession;
  stored = structuredClone(session);
  api.request.mockImplementation(async (path: string) => {
    if (path.endsWith('/hardware-ticket')) {
      stored.hardware = { deviceId: 'case-one', bootId: boot, expectedRevision: 5, conversationId: null, status: 'prepared', expiresAt: '2026-10-20T12:03:00Z' };
      return new Response(JSON.stringify({ testContext: 'A'.repeat(43), expiresAt: '2026-10-20T12:01:30Z' }));
    }
    if (path.endsWith('/end')) { stored = { ...stored, status: 'ended', hardware: stored.hardware ? { ...stored.hardware, status: 'ended' } : null }; }
    return new Response(JSON.stringify(stored));
  });
});
afterEach(() => { cleanups.splice(0).forEach(clear => clear()); vi.useRealTimers(); });
function setup() {
  const selected = shallowRef(session); const scope = effectScope(); const failure = vi.fn(); const notice = vi.fn();
  const catalog = shallowRef<Catalog>({ activeRevisionId: 'r', versions: [session.config], scenarios: [], memoryFixtures: [],
    capabilities: { hardwareContext: true, live: true, realtime: true, voice: true, text: true, simulationOnly: false } });
  const hardware = scope.run(() => useLabHardware({ selected, catalog, busy: ref(false),
    identity: () => 'owner:Operator', ownerId: () => 'owner', isolatedBusy: () => false, remember: value => { selected.value = value; }, failure, notice }))!;
  cleanups.push(() => { hardware.clear(); scope.stop(); });
  return { hardware, selected, catalog, failure, notice };
}
describe('isolated native hardware test admission', () => {
  it('requires the real nested catalog capability and ignores a misplaced top-level flag', async () => {
    const { hardware, catalog } = setup();
    expect(hardware.eligibility.value.ready).toBe(true);
    catalog.value = { ...catalog.value, capabilities: { ...catalog.value.capabilities, hardwareContext: undefined }, ...{ hardwareContext: true } };
    hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    expect(hardware.eligibility.value.ready).toBe(false); expect(api.request).not.toHaveBeenCalled();
  });
  it('derives only the lowercase SHA-256 technical fingerprint from the context', async () => {
    expect(await hardwareContextId('A'.repeat(43))).toBe(contextId);
  });
  it('defaults to simulation and denies Viewer or unadvertised firmware before minting a capability', async () => {
    associationFixture.canOperate.value = false;
    const { hardware } = setup(); expect(hardware.mode.value).toBe(false);
    hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    expect(api.request).not.toHaveBeenCalled(); expect(api.start).not.toHaveBeenCalled();
    associationFixture.canOperate.value = true; deviceSession.snapshot.value = { ...snapshot(), capabilities: [] };
    expect(hardware.eligibility.value.reason).toContain('voiceTestContext');
    hardware.confirmed.value = true; await hardware.start(); expect(api.start).not.toHaveBeenCalled();
  });
  it('requires explicit confirmation and never silently changes a mismatched model or voice', async () => {
    const { hardware } = setup(); hardware.mode.value = true; await hardware.start(); expect(api.request).not.toHaveBeenCalled();
    deviceSession.snapshot.value = { ...snapshot(), voice: { ...snapshot().voice!, mode: 'realtime' } };
    hardware.confirmed.value = true; await hardware.start(); expect(api.configure).not.toHaveBeenCalled(); expect(api.start).not.toHaveBeenCalled();
    hardware.configure(); expect(api.configure).toHaveBeenCalledWith('gpt-live', 'cedar'); expect(hardware.confirmed.value).toBe(false);
  });
  it('never consumes a late ticket on a changed boot and revokes the original lease', async () => {
    let complete!: (response: Response) => void;
    const fallback = api.request.getMockImplementation()!;
    api.request.mockImplementation((path: string, init: RequestInit) => path.endsWith('/hardware-ticket')
      ? new Promise<Response>(resolve => { complete = resolve; }) : fallback(path, init));
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true;
    const starting = hardware.start();
    deviceSession.snapshot.value = { ...snapshot(), diagnostics: { ...snapshot().diagnostics!, bootId: '22222222-2222-4222-8222-222222222222' } };
    complete(new Response(JSON.stringify({ testContext: 'A'.repeat(43), expiresAt: '2026-10-20T12:01:30Z' }))); await starting; await flushPromises();
    expect(api.start).not.toHaveBeenCalled(); expect(api.stop).not.toHaveBeenCalled();
    expect(api.request.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(true);
  });
  it('treats carrier rejection as uncertain, revokes once and never falls back to production or retries', async () => {
    api.start.mockReturnValue(false);
    const { hardware, selected } = setup(); hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    await vi.advanceTimersByTimeAsync(10000);
    expect(api.start).toHaveBeenCalledTimes(1); expect(api.start.mock.calls[0][0]).toBe(false);
    expect(selected.value.hardware?.status).toBe('ended'); expect(hardware.bound.value).toBe(true);
    expect(api.request.mock.calls.filter(([path]) => path.endsWith('/hardware-ticket'))).toHaveLength(1);
    expect(JSON.stringify(selected.value)).not.toContain('A'.repeat(43));
  });
  it('polls owned observations without overlap and clears timers and lease on disposal', async () => {
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    expect(api.start).toHaveBeenCalledWith(false, 'A'.repeat(43), expect.objectContaining({ deviceId: 'case-one', bootId: boot, revision: 5, mode: 'gpt-live', voice: 'cedar' }));
    await vi.advanceTimersByTimeAsync(1000); const requests = api.request.mock.calls.filter(([path]) => path.endsWith('/session')).length;
    hardware.clear(); await flushPromises(); await vi.advanceTimersByTimeAsync(10000);
    expect(api.request.mock.calls.filter(([path]) => path.endsWith('/session'))).toHaveLength(requests);
    expect(api.stop).toHaveBeenCalled(); expect(api.request.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(true);
  });
  it('requires a real native source and a paired identity even when an operate claim is available', () => {
    expect(hardwareEligibility(session, { ...snapshot(), source: 'e2-simulator' }, true, 'case-one', true, true).ready).toBe(false);
    expect(hardwareEligibility(session, snapshot(), true, 'different-case', true, true).ready).toBe(false);
  });
  it('does not reuse a ticket or target Stop after a same-boot connection epoch changes', async () => {
    let complete!: (response: Response) => void;
    const fallback = api.request.getMockImplementation()!;
    api.request.mockImplementation((path: string, init: RequestInit) => path.endsWith('/hardware-ticket')
      ? new Promise<Response>(resolve => { complete = resolve; }) : fallback(path, init));
    api.stop.mockImplementation(target => target.connectionEpoch === api.epoch);
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true;
    const starting = hardware.start(); api.epoch = 2; deviceSession.snapshot.value = snapshot();
    complete(new Response(JSON.stringify({ testContext: 'A'.repeat(43), expiresAt: '2026-10-20T12:01:30Z' }))); await starting; await flushPromises();
    expect(api.start).not.toHaveBeenCalled();
    expect(api.stop).not.toHaveBeenCalled();
  });
  it('revokes the lease and stops polling when operate authority disappears', async () => {
    const { hardware, selected } = setup(); hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    associationFixture.canOperate.value = false; await flushPromises();
    expect(selected.value.hardware?.status).toBe('ended'); expect(api.stop).toHaveBeenCalled();
    const count = api.request.mock.calls.length; await vi.advanceTimersByTimeAsync(10000);
    expect(api.request).toHaveBeenCalledTimes(count); expect(api.start).toHaveBeenCalledTimes(1);
  });
  it('retains the original epoch for Stop after a submitted test loses its same-boot connection', async () => {
    api.stop.mockImplementation(target => target.connectionEpoch === api.epoch);
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    api.epoch = 2; deviceSession.snapshot.value = { ...snapshot(), voice: { ...snapshot().voice!, testContextId: contextId } }; await flushPromises();
    expect(api.stop).toHaveBeenCalledWith({ deviceId: 'case-one', bootId: boot, connectionEpoch: 1, testContextId: contextId });
    expect(api.stop.mock.results.every(result => result.value === false)).toBe(true);
    expect(api.request.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(true);
    expect(api.start).toHaveBeenCalledTimes(1);
  });
  it.each([undefined, 'b'.repeat(64)])('never stops a later same-boot production or test operation (%s)', async nextId => {
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    deviceSession.snapshot.value = { ...snapshot(), voice: { ...snapshot().voice!, testContextId: nextId } };
    expect(hardware.stopLocal()).toBe(false); expect(api.stop).not.toHaveBeenCalled();
    stored = { ...stored, status: 'ended', hardware: { ...stored.hardware!, status: 'ended' } };
    await vi.advanceTimersByTimeAsync(1000);
    expect(api.stop).not.toHaveBeenCalled();
  });
  it('stops a terminal observation only when the live operation fingerprint still matches', async () => {
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true; await hardware.start();
    stored = { ...stored, status: 'ended', hardware: { ...stored.hardware!, status: 'ended' } };
    await vi.advanceTimersByTimeAsync(1000);
    expect(api.stop).toHaveBeenCalledWith({ deviceId: 'case-one', bootId: boot, connectionEpoch: 1, testContextId: contextId });
    expect(api.stop).toHaveBeenCalledTimes(1);
  });
  it('rejects a model change while hashing the context before any carrier submission', async () => {
    let finish!: (buffer: ArrayBuffer) => void;
    vi.spyOn(crypto.subtle, 'digest').mockImplementation(() => new Promise<ArrayBuffer>(resolve => { finish = resolve; }));
    const { hardware } = setup(); hardware.mode.value = true; hardware.confirmed.value = true;
    const starting = hardware.start(); await flushPromises();
    deviceSession.snapshot.value = { ...snapshot(), voice: { ...snapshot().voice!, mode: 'realtime' } };
    finish(new Uint8Array(32).buffer); await starting;
    expect(api.start).not.toHaveBeenCalled(); expect(api.stop).not.toHaveBeenCalled();
    expect(api.request.mock.calls.some(([path]) => path.endsWith('/end'))).toBe(true);
  });
});

import { defineComponent, h, markRaw, nextTick, reactive, ref, type Ref } from 'vue';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DirectorClient } from '../director/DirectorClient';
import { DirectorSession, type DirectorLocal } from '../director/DirectorSession';
import { clearDirectorStop, directorStop } from '../director/directorStop';
import type { TonightScope, ScopedObservation } from './tonightProjection';
import {
  useTonight,
  type TonightAccountRequest,
  type TonightRecoveryController,
  type TonightRecoveryFactory,
  type TonightRecoveryOptions,
  type TonightRecoveryState,
  type TonightSessionSnapshot,
  type TonightLocalDeviceObservation,
} from './useTonight';

const nowMs = 1_800_000_000_000;
const scope: TonightScope = { accountId: 'operator-1', deviceId: 'case-1' };
const activeSession = {
  id: 'session-1', operationId: 'operation-1', deviceId: 'case-1', profileId: 'profile-1',
  profileRevision: 1, state: 'running', endRequested: false, createdAt: 1_799_999_000, characterName: 'Host večera',
};

function showState(revision = 1) {
  return {
    deviceId: 'case-1', eveningId: 'evening-1', revision, phase: 'idle',
    mood: { dominant: 'amused', locked: false }, sessionId: 'guest-session',
    sessionGeneration: 'generation-1', tone: 'dry', tempo: 'standard', quiet: false,
    ambientEnabled: true, readyMask: 63, selectionLocked: false,
    updatedAt: new Date(nowMs).toISOString(),
  };
}

function memoryStatus(overrides: Record<string, unknown> = {}) {
  return {
    storedBytes: 1_572_864, capacityBytes: 2_097_152, storageStatus: 'warning' as const,
    rejectedWrites: 2, storageIncomplete: true, lastStorageErrorCode: 'archive_capacity',
    lastStorageErrorAt: '2026-10-23T10:15:30.000Z', ...overrides,
  };
}

function accountResponse(path: string): unknown {
  if (path === '/app/v1/customer/admin/sessions') return { items: [activeSession] };
  if (path.endsWith('/memory-status')) return memoryStatus();
  return {};
}

class TestRecovery<T> implements TonightRecoveryController<T> {
  readonly state: TonightRecoveryState;
  active = false;
  disposed = false;
  refreshCount = 0;
  aborts: AbortController[] = [];
  private request?: Promise<T | undefined>;

  constructor(readonly options: TonightRecoveryOptions<T>) {
    this.state = options.state;
  }

  async refresh(): Promise<T | undefined> {
    if (this.disposed) return undefined;
    if (this.request) return this.request;
    this.refreshCount += 1;
    const controller = new AbortController();
    this.aborts.push(controller);
    const request = (async (): Promise<T | undefined> => {
      try {
      const value = await this.options.refresh(controller.signal);
      if (controller.signal.aborted || this.disposed) return undefined;
      this.state.refreshing = false;
      this.state.stale = false;
      this.options.onValue?.(value);
      this.active = this.options.hasActiveSession(value);
      return value;
      } catch {
        if (!controller.signal.aborted && !this.disposed) this.state.stale = true;
        return undefined;
      }
    })();
    this.request = request;
    try { return await request; }
    finally { if (this.request === request) this.request = undefined; }
  }

  setActive(active: boolean): Promise<T | undefined> | undefined {
    if (this.disposed) return undefined;
    this.active = active;
    if (active) return this.refresh();
    this.invalidate();
    return undefined;
  }

  invalidate(): void {
    this.active = false;
    this.aborts.forEach(controller => controller.abort());
    this.state.refreshing = false;
    this.state.stale = false;
    this.state.retrying = false;
    this.state.retryDelayMs = null;
    this.state.error = undefined;
  }

  dispose(): void {
    this.invalidate();
    this.disposed = true;
  }
}

function rig(options: {
  permission?: 'allowed' | 'denied' | 'unknown';
  request?: unknown;
  scope?: TonightScope | null;
  device?: TonightLocalDeviceObservation | null;
} = {}) {
  const currentScope = ref<TonightScope | null>(options.scope ?? scope);
  const permission = ref(options.permission ?? 'allowed');
  const localDevice: Ref<TonightLocalDeviceObservation | null> = ref(options.device === undefined ? {
    source: 'native-device', deviceId: 'case-1', phase: 'ready', busy: false,
    readyMask: 63, voice: { canStart: true },
  } : options.device) as Ref<TonightLocalDeviceObservation | null>;
  const network = ref<ScopedObservation<{ deviceId: string; state: 'connected' | 'disconnected' }> | undefined>({
    scope, value: { deviceId: 'case-1', state: 'connected' }, receivedAtMs: nowMs - 500,
  });
  const audio = ref<ScopedObservation<{ deviceId: string; phase: string }> | undefined>({
    scope, value: { deviceId: 'case-1', phase: 'speaking' }, receivedAtMs: nowMs - 500,
  });
  const deviceReceivedAtMs = ref<number | null>(nowMs - 500);
  const request = (options.request ?? vi.fn(async (path: string) => accountResponse(path))) as unknown as TonightAccountRequest;
  const requestMock = request as unknown as ReturnType<typeof vi.fn>;
  const controllers: TestRecovery<TonightSessionSnapshot>[] = [];
  const createRecovery: TonightRecoveryFactory<TonightSessionSnapshot> = recoveryOptions => {
    const controller = new TestRecovery(recoveryOptions);
    controllers.push(controller);
    return controller;
  };
  const transport = vi.fn(async (path: string, _init?: RequestInit) => Response.json(showState()));
  const director = reactive(new DirectorSession(markRaw(new DirectorClient(transport)), markRaw({
    matches: () => true,
    offline: async () => ({}),
    nextVisit: () => true,
    stop: () => undefined,
    cancelSpeech: () => undefined,
  } satisfies DirectorLocal)));
  const dependencies = {
    scope: currentScope,
    permission,
    localDevice,
    deviceReceivedAtMs,
    network,
    audio,
    accountRequest: request,
    createRecovery,
    director,
    now: () => Date.now(),
  };
  let adapter!: ReturnType<typeof useTonight>;
  const Host = defineComponent({ setup: () => { adapter = useTonight(dependencies); return () => h('div'); } });
  const wrapper = mount(Host, { attachTo: document.body });

  return { currentScope, permission, localDevice, request: requestMock, controllers, transport, director, adapter, wrapper };
}

let wrappers: VueWrapper[] = [];
beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(nowMs);
  Object.defineProperty(document, 'hidden', { configurable: true, value: false });
  clearDirectorStop();
});
afterEach(() => { wrappers.forEach(wrapper => wrapper.unmount()); wrappers = []; clearDirectorStop(); vi.useRealTimers(); });

describe('useTonight', () => {
  it('loads only scoped status endpoints with client receive time and no provider/configuration fields', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();

    expect(value.request).toHaveBeenCalledWith('/app/v1/customer/admin/sessions', 'GET', undefined, expect.any(AbortSignal));
    expect(value.request).toHaveBeenCalledWith('/app/v1/tonight/devices/case-1/memory-status', 'GET', undefined, expect.any(AbortSignal));
    expect(value.adapter.model.value.projection.session.state).toBe('active');
    expect(value.adapter.model.value.projection.memory).toMatchObject({
      storageStatus: 'warning', storageIncomplete: true, rejectedWrites: 2,
      storedBytes: 1_572_864, capacityBytes: 2_097_152,
      lastStorageErrorCode: 'archive_capacity', lastStorageErrorAt: '2026-10-23T10:15:30.000Z',
    });
    expect(value.adapter.model.value.projection.memory).not.toHaveProperty('provider');
    expect(value.adapter.model.value.projection.memory.receivedAtMs).toBe(nowMs);
    expect(value.controllers[0].options.hasActiveSession({
      sessions: { scope, value: [], receivedAtMs: nowMs },
      memory: { scope, value: memoryStatus(), receivedAtMs: nowMs },
    })).toBe(true);
    expect(value.controllers[0].options.pollIntervalMs).toBe(5_000);
  });

  it('does not query private operation data without confirmed operator permission', async () => {
    const value = rig({ permission: 'denied' }); wrappers.push(value.wrapper);
    await flushPromises();

    expect(value.request).not.toHaveBeenCalled();
    expect(value.adapter.model.value.access).toBe('denied');
    expect(value.adapter.model.value.projection.session.state).toBe('unknown');
    expect(directorStop.value).toBeNull();
  });

  it('fences a late account response on account/device handoff and clears old private values', async () => {
    let finishOld!: (response: unknown) => void;
    const request = vi.fn((path: string, _method: string, _body: unknown, signal: AbortSignal) => {
      if (path === '/app/v1/customer/admin/sessions' && !signal.aborted) {
        return new Promise<unknown>(resolve => { finishOld = resolve; });
      }
      if (path.endsWith('/case-2/memory-status')) return Promise.reject(new Error('private trace'));
      return Promise.resolve(accountResponse(path));
    });
    const value = rig({ request }); wrappers.push(value.wrapper);
    await nextTick();
    expect(value.adapter.model.value.projection.session.state).toBe('unknown');

    value.currentScope.value = { accountId: 'operator-2', deviceId: 'case-2' };
    await flushPromises();
    finishOld({ items: [{ ...activeSession, characterName: 'Old account guest' }] });
    await flushPromises();

    expect(value.adapter.model.value.scope).toEqual({ accountId: 'operator-2', deviceId: 'case-2' });
    expect(value.adapter.model.value.projection.session.state).toBe('no_active_session');
    expect(value.adapter.model.value.projection.session.current).toBeUndefined();
    expect(value.adapter.model.value.projection.memory.storageStatus).toBe('unknown');
    expect(value.request.mock.calls.some(call => String(call[0]).includes('case-2'))).toBe(true);
    expect(value.adapter.model.value.projection.session.current?.characterName).not.toBe('Old account guest');
  });

  it('keeps a status failure generic and unknown instead of rendering backend error text', async () => {
    const request = vi.fn(async () => { throw new Error('private trace from backend'); });
    const value = rig({ request }); wrappers.push(value.wrapper);
    await flushPromises();

    expect(value.adapter.model.value.projection.session.state).toBe('unknown');
    expect(value.adapter.model.value.projection.memory.storageStatus).toBe('unknown');
    expect(value.adapter.model.value.error).toBe('Stav se nepodařilo ověřit. Obnovte jej ručně.');
    expect(value.adapter.model.value.error).not.toContain('private trace');
  });

  it('fails recovery when both reads fail, then accepts a later successful retry', async () => {
    const request = vi.fn(async (_path: string): Promise<unknown> => { throw new Error('private trace'); });
    const value = rig({ request }); wrappers.push(value.wrapper);
    await flushPromises();
    const controller = value.controllers[0];
    expect(controller.state.stale).toBe(true);
    expect(value.adapter.model.value.projection.memory.storageStatus).toBe('unknown');

    request.mockImplementation(async (path: string) => accountResponse(path));
    await controller.refresh();

    expect(controller.state.stale).toBe(false);
    expect(value.adapter.model.value.projection.session.state).toBe('active');
    expect(value.adapter.model.value.projection.memory.storageStatus).toBe('warning');
  });

  it('keeps a single failed status facet unknown while preserving the fresh successful facet', async () => {
    const request = vi.fn(async (path: string) => {
      if (path.endsWith('/memory-status')) throw new Error('private trace');
      return accountResponse(path);
    });
    const value = rig({ request }); wrappers.push(value.wrapper);
    await flushPromises();

    expect(value.adapter.model.value.projection.session.state).toBe('active');
    expect(value.adapter.model.value.projection.session.freshness).toBe('fresh');
    expect(value.adapter.model.value.projection.memory.storageStatus).toBe('unknown');
    expect(value.adapter.model.value.error).toBe('Stav se nepodařilo ověřit. Obnovte jej ručně.');
  });

  it('routes actions through the existing director session with strict readiness and no retries', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    const command = vi.spyOn(value.director, 'command').mockResolvedValue();

    await value.adapter.nextVisit();
    await value.adapter.quickFinale();
    await value.adapter.guestLeave();
    expect(command.mock.calls).toEqual([['next-visit'], ['quick-finale'], ['guest-leave']]);

    command.mockClear();
    value.localDevice.value = { source: 'native-device', deviceId: 'case-1', phase: 'ready', busy: true, readyMask: 0, voice: { canStart: true } };
    await nextTick();
    await value.adapter.nextVisit();
    expect(command).not.toHaveBeenCalled();
  });

  it('blocks next visit when native telemetry reports the device busy', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    value.localDevice.value = { source: 'native-device', deviceId: 'case-1', phase: 'ready', busy: true, readyMask: 63, voice: { canStart: true } };
    await nextTick();
    const command = vi.spyOn(value.director, 'command').mockResolvedValue();

    expect(value.adapter.model.value.canNextVisit).toBe(false);
    await value.adapter.nextVisit();
    expect(command).not.toHaveBeenCalled();
  });

  it('never uses customer-session existence to enable finale or guest-left', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    value.director.state = undefined;
    const command = vi.spyOn(value.director, 'command').mockResolvedValue();

    await value.adapter.quickFinale();
    await value.adapter.guestLeave();
    expect(command).not.toHaveBeenCalled();
  });

  it('clears private reads on unmount but keeps the existing global Stop participant usable', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    expect(directorStop.value?.canStop()).toBe(true);
    const ordinaryCommand = vi.spyOn(value.director, 'command').mockResolvedValue();
    value.wrapper.unmount();
    wrappers = [];

    expect(directorStop.value?.canStop()).toBe(true);
    await value.adapter.quickFinale();
    expect(ordinaryCommand).not.toHaveBeenCalled();
    await directorStop.value!.stop();

    expect(value.transport.mock.calls.filter(([path, init]) => path.endsWith('/commands') && init?.method === 'POST')).toHaveLength(1);
    expect(directorStop.value).not.toBeNull();
  });

  it('uses refresh-only behavior for manual reconnect refreshes and disposes recovery on handoff', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    const controller = value.controllers[0];
    const initialCommands = value.transport.mock.calls.filter(([path]) => path.endsWith('/commands')).length;

    await value.adapter.refresh();
    expect(value.transport.mock.calls.filter(([path]) => path.endsWith('/commands')).length).toBe(initialCommands);
    expect(controller.refreshCount).toBeGreaterThan(1);

    value.currentScope.value = { accountId: 'operator-1', deviceId: 'case-2' };
    await nextTick();
    expect(controller.disposed).toBe(true);
  });

  it('deduplicates overlapping status refreshes without issuing any command', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    const controller = value.controllers[0];
    const before = controller.refreshCount;
    const requestsBefore = (value.request as ReturnType<typeof vi.fn>).mock.calls.length;

    await Promise.all([controller.refresh(), controller.refresh()]);

    expect(controller.refreshCount).toBe(before + 1);
    expect((value.request as ReturnType<typeof vi.fn>).mock.calls.length).toBe(requestsBefore + 2);
    expect(value.transport.mock.calls.some(([path]) => path.endsWith('/commands'))).toBe(false);
  });

  it('expires client-received status during a hung read and rechecks online guards before commands', async () => {
    const value = rig(); wrappers.push(value.wrapper);
    await flushPromises();
    const command = vi.spyOn(value.director, 'command').mockResolvedValue();
    let release!: (value: unknown) => void;
    value.request.mockImplementation(() => new Promise(resolve => { release = resolve; }));

    void value.adapter.refresh();
    await nextTick();
    await vi.advanceTimersByTimeAsync(13_000);

    expect(value.adapter.model.value.nowMs).toBe(nowMs + 13_000);
    expect(value.adapter.model.value.projection.session.freshness).toBe('stale');
    expect(value.adapter.model.value.canNextVisit).toBe(false);
    await value.adapter.nextVisit();
    expect(command).not.toHaveBeenCalled();
    expect(directorStop.value?.canStop()).toBe(true);
    release({ items: [activeSession] });
    await flushPromises();
  });
});

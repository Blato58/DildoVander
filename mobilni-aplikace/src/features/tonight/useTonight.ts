import { computed, onBeforeUnmount, onMounted, reactive, shallowRef, watch, type ComputedRef, type Ref } from 'vue';
import { registerDirectorStop, clearDirectorStop } from '../director/directorStop';
import type { ShowState } from '../director/DirectorClient';
import type { RemoteStopOperation } from '../director/directorState';
import type { CustomerSessionSummary, TonightMemoryStatusSnapshot, TonightProjection, TonightScope, ScopedObservation,
  DeviceHealthObservation, NetworkHealthObservation, AudioHealthObservation } from './tonightProjection';
import { projectTonight } from './tonightProjection';

export interface TonightSessionSnapshot {
  sessions: ScopedObservation<CustomerSessionSummary[]>;
  memory: ScopedObservation<TonightMemoryStatusSnapshot>;
}

export interface TonightRecoveryState {
  refreshing: boolean;
  stale: boolean;
  retrying: boolean;
  retryDelayMs: number | null;
  error: unknown;
}

export interface TonightRecoveryOptions<T> {
  refresh(signal: AbortSignal): Promise<T>;
  hasActiveSession(value: T): boolean;
  pollIntervalMs?: number;
  onValue?(value: T): void;
  state: TonightRecoveryState;
}

export interface TonightRecoveryController<T> {
  readonly state: TonightRecoveryState;
  refresh(): Promise<T | undefined>;
  setActive(active: boolean): Promise<T | undefined> | undefined;
  invalidate(): void;
  dispose(): void;
}

export type TonightRecoveryFactory<T> = (options: TonightRecoveryOptions<T>) => TonightRecoveryController<T>;
export type TonightAccountRequest = <T>(path: string, method: 'GET', body: undefined, signal: AbortSignal) => Promise<T>;

export type TonightPermission = 'allowed' | 'denied' | 'unknown';

export interface TonightLocalDeviceObservation extends DeviceHealthObservation {
  readyMask?: number;
  voice?: { canStart?: boolean };
}

export interface TonightDirectorPort {
  client: { deviceId: string };
  state?: ShowState;
  operation?: { id: string; label: string; status: string };
  remoteStop?: RemoteStopOperation;
  pending: boolean;
  online(): boolean;
  canRemoteStop(): boolean;
  bind(deviceId: string): void;
  refresh(): Promise<void>;
  command(kind: string): Promise<void>;
  stopThroughBackend(): Promise<void>;
  clear(): void;
  leaveView(): void;
}

export interface TonightViewModel {
  scope: TonightScope | null;
  nowMs: number;
  projection: TonightProjection;
  refreshing: boolean;
  stale: boolean;
  access: TonightPermission;
  canNextVisit: boolean;
  canSessionCommand: boolean;
  error: string | null;
  operation: { id: string; label: string; status: string } | null;
  remoteStop: RemoteStopOperation | null;
  clientReceivedAtMs: number | null;
}

export interface TonightOptions {
  scope: Readonly<Ref<TonightScope | null>>;
  permission: Readonly<Ref<TonightPermission>>;
  localDevice: Readonly<Ref<TonightLocalDeviceObservation | null>>;
  deviceReceivedAtMs: Readonly<Ref<number | null>>;
  network: Readonly<Ref<ScopedObservation<NetworkHealthObservation> | undefined>>;
  audio: Readonly<Ref<ScopedObservation<AudioHealthObservation> | undefined>>;
  accountRequest: TonightAccountRequest;
  createRecovery: TonightRecoveryFactory<TonightSessionSnapshot>;
  director: TonightDirectorPort;
  now: () => number;
}

const safeRefreshError = 'Stav se nepodařilo ověřit. Obnovte jej ručně.';

function validScope(scope: TonightScope | null): scope is TonightScope {
  return !!scope && typeof scope.accountId === 'string' && scope.accountId.trim().length > 0
    && typeof scope.deviceId === 'string' && scope.deviceId.trim().length > 0;
}

function emptyProjection(): TonightProjection {
  return projectTonight({ scope: null as unknown as TonightScope }, Number.NaN);
}

export function useTonight(options: TonightOptions): {
  model: ComputedRef<TonightViewModel>;
  refresh(): Promise<void>;
  nextVisit(): Promise<void>;
  quickFinale(): Promise<void>;
  guestLeave(): Promise<void>;
} {
  const received = shallowRef<TonightSessionSnapshot>();
  const recoveryState = reactive<TonightRecoveryState>({
    refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined,
  });
  const clockMs = shallowRef(options.now());
  let recovery: TonightRecoveryController<TonightSessionSnapshot> | undefined;
  let mounted = false;
  let visible = true;
  let disposed = false;
  let generation = 0;
  let contextKey = '';
  let freshnessTimer: ReturnType<typeof setInterval> | undefined;
  let stopRecoveryWatch: (() => void) | undefined;

  const stopParticipant = {
    canStop: () => options.permission.value === 'allowed'
      && validScope(options.scope.value)
      && options.director.client.deviceId === options.scope.value.deviceId
      && options.director.canRemoteStop(),
    busy: () => options.director.remoteStop?.pending === true,
    // The app shell's Stop remains available after this view leaves the route.
    stop: () => options.director.stopThroughBackend(),
    clear: () => options.director.clear(),
  };

  function buildProjection(nowMs: number): TonightProjection {
    const scope = options.scope.value;
    if (!validScope(scope)) return emptyProjection();

    const snapshot = received.value;
    const device = options.localDevice.value;
    const deviceObservation: ScopedObservation<TonightLocalDeviceObservation> | undefined = device
      && options.deviceReceivedAtMs.value !== null
      ? { scope, value: device, receivedAtMs: options.deviceReceivedAtMs.value }
      : undefined;
    return projectTonight({
      scope,
      sessions: snapshot?.sessions && { ...snapshot.sessions, stale: snapshot.sessions.stale || recoveryState.stale },
      memory: snapshot?.memory && { ...snapshot.memory, stale: snapshot.memory.stale || recoveryState.stale },
      device: deviceObservation,
      network: options.network.value,
      audio: options.audio.value,
    }, nowMs);
  }

  const model = computed<TonightViewModel>(() => {
    const scope = options.scope.value;
    const projection = buildProjection(clockMs.value);
    const local = options.localDevice.value;
    const activeDirectorSession = !!options.director.state?.sessionId && !!options.director.state.sessionGeneration;
    const localReady = validScope(scope) && !!local && local.source === 'native-device'
      && local.deviceId === scope.deviceId && local.phase !== 'disconnected' && local.busy !== true
      && projection.device.freshness === 'fresh'
      && local.voice?.canStart === true && Number.isInteger(local.readyMask) && (local.readyMask ?? 0) > 0;
    const canOperate = options.permission.value === 'allowed'
      && options.director.client.deviceId === scope?.deviceId && options.director.online();
    const error = received.value && (received.value.sessions.error || received.value.memory.error)
      ? safeRefreshError
      : recoveryState.stale ? safeRefreshError : null;

    return {
      scope,
      nowMs: clockMs.value,
      projection,
      refreshing: recoveryState.refreshing || options.director.pending,
      stale: recoveryState.stale || projection.session.freshness === 'stale'
        || projection.memory.freshness === 'stale',
      access: options.permission.value,
      canNextVisit: !!canOperate && !!localReady,
      canSessionCommand: !!canOperate && !!activeDirectorSession,
      error,
      operation: options.director.operation ? { ...options.director.operation } : null,
      remoteStop: options.director.remoteStop ? { ...options.director.remoteStop } : null,
      clientReceivedAtMs: projection.session.receivedAtMs ?? null,
    };
  });

  async function loadSnapshot(scope: TonightScope, signal: AbortSignal, expectedGeneration: number): Promise<TonightSessionSnapshot> {
    const sessionPath = '/app/v1/customer/admin/sessions';
    const memoryPath = `/app/v1/tonight/devices/${encodeURIComponent(scope.deviceId)}/memory-status`;
    const receivedAt = async <T>(path: string): Promise<{ value?: T; receivedAtMs?: number; failed: boolean }> => {
      try {
        const value = await options.accountRequest<T>(path, 'GET', undefined, signal);
        return { value, receivedAtMs: options.now(), failed: false };
      } catch {
        return { failed: true };
      }
    };

    const [sessionsResult, memoryResult] = await Promise.all([
      receivedAt<{ items?: CustomerSessionSummary[] }>(sessionPath),
      receivedAt<TonightMemoryStatusSnapshot>(memoryPath),
    ]);
    if (signal.aborted || disposed || !sameContext(scope, expectedGeneration)) {
      throw new DOMException('Kontext se změnil.', 'AbortError');
    }

    const sessionsValid = !sessionsResult.failed && Array.isArray(sessionsResult.value?.items);
    const memoryValid = !memoryResult.failed && !!memoryResult.value && typeof memoryResult.value === 'object';
    if (!sessionsValid && !memoryValid) throw new Error(safeRefreshError);
    const value: TonightSessionSnapshot = {
      sessions: {
        scope,
        value: sessionsValid ? sessionsResult.value!.items! : [],
        receivedAtMs: sessionsResult.receivedAtMs ?? null,
        error: !sessionsValid,
      },
      memory: {
        scope,
        value: memoryValid ? memoryResult.value! : {} as TonightMemoryStatusSnapshot,
        receivedAtMs: memoryResult.receivedAtMs ?? null,
        error: !memoryValid,
      },
    };
    return value;
  }

  function sameContext(scope: TonightScope, expectedGeneration: number): boolean {
    return !disposed && generation === expectedGeneration && validScope(options.scope.value)
      && options.scope.value.accountId === scope.accountId && options.scope.value.deviceId === scope.deviceId
      && options.permission.value === 'allowed';
  }

  async function refreshCycle(scope: TonightScope, signal: AbortSignal, expectedGeneration: number): Promise<TonightSessionSnapshot> {
    const snapshot = await loadSnapshot(scope, signal, expectedGeneration);
    if (signal.aborted || !sameContext(scope, expectedGeneration)) {
      throw new DOMException('Kontext se změnil.', 'AbortError');
    }
    await options.director.refresh();
    if (signal.aborted || !sameContext(scope, expectedGeneration)) {
      throw new DOMException('Kontext se změnil.', 'AbortError');
    }
    registerStopWhenAuthorized(expectedGeneration);
    return snapshot;
  }

  function createForScope(scope: TonightScope, expectedGeneration: number): void {
    const state = reactive<TonightRecoveryState>({
      refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined,
    });
    recoveryState.refreshing = false;
    recoveryState.stale = false;
    recoveryState.retrying = false;
    recoveryState.retryDelayMs = null;
    recoveryState.error = undefined;
    stopRecoveryWatch?.();
    recovery = options.createRecovery({
      state,
      refresh: signal => refreshCycle(scope, signal, expectedGeneration),
      pollIntervalMs: 5_000,
      // Keep observing this authorized device scope after an empty snapshot.
      hasActiveSession: () => sameContext(scope, expectedGeneration) && visible,
      onValue: value => {
        if (!sameContext(scope, expectedGeneration)) return;
        received.value = value;
        clockMs.value = options.now();
        recoveryState.error = value.sessions.error || value.memory.error ? safeRefreshError : undefined;
      },
    });
    stopRecoveryWatch = watch(() => [state.refreshing, state.stale, state.retrying, state.retryDelayMs, state.error] as const, () => {
      if (recovery?.state === state) {
        recoveryState.refreshing = state.refreshing;
        recoveryState.stale = state.stale;
        recoveryState.retrying = state.retrying;
        recoveryState.retryDelayMs = state.retryDelayMs;
        recoveryState.error = state.error;
      }
    }, { flush: 'sync' });
  }

  function registerStopWhenAuthorized(expectedGeneration: number): void {
    if (!disposed && generation === expectedGeneration && options.permission.value === 'allowed'
      && validScope(options.scope.value) && options.director.client.deviceId === options.scope.value.deviceId
      && options.director.canRemoteStop()) {
      registerDirectorStop(stopParticipant);
    }
  }

  function activate(): void {
    const scope = options.scope.value;
    if (disposed || !mounted || !visible || options.permission.value !== 'allowed' || !validScope(scope) || !recovery) return;
    void recovery.setActive(true);
  }

  function resetContext(): void {
    const scope = options.scope.value;
    const nextKey = validScope(scope) ? `${scope.accountId}\u0000${scope.deviceId}\u0000${options.permission.value}` : '';
    if (nextKey === contextKey) return;
    const hadContext = contextKey.length > 0;
    contextKey = nextKey;
    ++generation;
    recovery?.dispose();
    recovery = undefined;
    stopRecoveryWatch?.();
    stopRecoveryWatch = undefined;
    received.value = undefined;
    recoveryState.refreshing = false;
    recoveryState.stale = false;
    recoveryState.retrying = false;
    recoveryState.retryDelayMs = null;
    recoveryState.error = undefined;
    if (hadContext) {
      clearDirectorStop();
      options.director.clear();
    }
    if (options.permission.value === 'allowed' && validScope(scope)) {
      options.director.bind(scope.deviceId);
      createForScope(scope, generation);
      activate();
    } else if (validScope(scope)) {
      options.director.clear();
    }
  }

  async function refresh(): Promise<void> {
    if (disposed || !visible || options.permission.value !== 'allowed' || !recovery || !validScope(options.scope.value)) return;
    await recovery.refresh();
  }

  function operatorContextIsCurrent(): boolean {
    const scope = options.scope.value;
    return !disposed && visible && options.permission.value === 'allowed' && validScope(scope)
      && options.director.client.deviceId === scope.deviceId && options.director.online();
  }

  function canNextVisitNow(): boolean {
    const scope = options.scope.value;
    if (!validScope(scope) || !operatorContextIsCurrent()) return false;
    const device = options.localDevice.value;
    const observation: ScopedObservation<TonightLocalDeviceObservation> | undefined = device
      && options.deviceReceivedAtMs.value !== null
      ? { scope, value: device, receivedAtMs: options.deviceReceivedAtMs.value }
      : undefined;
    const projected = projectTonight({ scope, device: observation }, options.now()).device;
    return projected.freshness === 'fresh' && device?.source === 'native-device'
      && device.deviceId === scope.deviceId && device.phase !== 'disconnected' && device.busy !== true
      && device.voice?.canStart === true && Number.isInteger(device.readyMask) && (device.readyMask ?? 0) > 0;
  }

  function canSessionCommandNow(): boolean {
    const scope = options.scope.value;
    if (!validScope(scope) || !operatorContextIsCurrent()) return false;
    return !!scope && options.director.state?.deviceId === scope.deviceId
      && !!options.director.state.sessionId && !!options.director.state.sessionGeneration;
  }

  async function command(kind: 'next-visit' | 'quick-finale' | 'guest-leave'): Promise<void> {
    if (kind === 'next-visit' ? !canNextVisitNow() : !canSessionCommandNow()) return;
    await options.director.command(kind);
  }

  function stopFreshnessClock(): void {
    if (freshnessTimer !== undefined) clearInterval(freshnessTimer);
    freshnessTimer = undefined;
  }

  function startFreshnessClock(): void {
    stopFreshnessClock();
    clockMs.value = options.now();
    freshnessTimer = setInterval(() => { clockMs.value = options.now(); }, 1_000);
  }

  function visibilityChanged(): void {
    if (document.hidden) {
      visible = false;
      stopFreshnessClock();
      ++generation;
      recovery?.dispose();
      recovery = undefined;
      stopRecoveryWatch?.();
      stopRecoveryWatch = undefined;
      received.value = undefined;
      clockMs.value = options.now();
      options.director.leaveView();
      return;
    }
    visible = true;
    startFreshnessClock();
    // Returning to the view refreshes status only; it never replays commands.
    if (options.permission.value === 'allowed' && validScope(options.scope.value)) {
      createForScope(options.scope.value, generation);
    }
    activate();
  }

  watch(() => {
    const scope = options.scope.value;
    return [scope?.accountId ?? '', scope?.deviceId ?? '', options.permission.value] as const;
  }, resetContext, { immediate: true, flush: 'sync' });

  onMounted(() => {
    mounted = true;
    visible = !document.hidden;
    document.addEventListener('visibilitychange', visibilityChanged);
    if (visible) {
      startFreshnessClock();
      activate();
    }
  });

  onBeforeUnmount(() => {
    disposed = true;
    mounted = false;
    visible = false;
    stopFreshnessClock();
    ++generation;
    recovery?.dispose();
    recovery = undefined;
    stopRecoveryWatch?.();
    stopRecoveryWatch = undefined;
    received.value = undefined;
    document.removeEventListener('visibilitychange', visibilityChanged);
    // Keep the registered participant and remote Stop authority across routes.
    options.director.leaveView();
  });

  return {
    model,
    refresh,
    nextVisit: () => command('next-visit'),
    quickFinale: () => command('quick-finale'),
    guestLeave: () => command('guest-leave'),
  };
}

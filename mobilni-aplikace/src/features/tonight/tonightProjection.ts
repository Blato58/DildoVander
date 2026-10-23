import type { MemoryStorageStatus } from '../debug/conversationCapabilities';

export interface TonightScope {
  accountId: string;
  deviceId: string;
}

export interface ScopedObservation<T> {
  scope: TonightScope;
  value: T;
  receivedAtMs: number | null;
  stale?: boolean;
  error?: boolean;
}

export type CurrentSessionState = 'starting' | 'context_ready' | 'running' | 'recovering';
export interface CustomerSessionSummary {
  id: string;
  operationId: string;
  deviceId: string;
  state: string;
  endRequested: boolean;
  createdAt: number;
  characterName?: string | null;
}

export interface DeviceHealthObservation {
  source: string;
  deviceId: string;
  phase: string;
  roundTripMs?: number | null;
  busy?: boolean;
}

export interface NetworkHealthObservation {
  deviceId: string;
  /** Explicit device-scoped status; do not infer it from browser reachability or round-trip alone. */
  state: 'connected' | 'disconnected' | 'unknown';
}

export interface AudioHealthObservation {
  deviceId: string;
  /** Reported phase only; it does not prove audible output. */
  phase: string;
}

/** Narrow wire shape from the operator memory-status endpoint. */
export interface TonightMemoryStatusSnapshot {
  storedBytes: number;
  capacityBytes: number;
  storageStatus?: MemoryStorageStatus;
  rejectedWrites: number;
  storageIncomplete?: boolean;
  lastStorageErrorCode?: string | null;
  lastStorageErrorAt?: string | null;
}

export interface TonightInput {
  scope: TonightScope;
  sessions?: ScopedObservation<CustomerSessionSummary[]>;
  device?: ScopedObservation<DeviceHealthObservation>;
  network?: ScopedObservation<NetworkHealthObservation>;
  audio?: ScopedObservation<AudioHealthObservation>;
  memory?: ScopedObservation<TonightMemoryStatusSnapshot>;
}

export type ObservationFreshness = 'fresh' | 'stale' | 'unknown';
export type CurrentSessionSummary = Pick<CustomerSessionSummary,
  'id' | 'operationId' | 'deviceId' | 'state' | 'endRequested' | 'createdAt' | 'characterName'>;

export interface SessionProjection {
  freshness: ObservationFreshness;
  receivedAtMs?: number;
  state: 'active' | 'no_active_session' | 'ambiguous' | 'unknown';
  current?: CurrentSessionSummary;
}

export interface ObservedValue<T> {
  freshness: ObservationFreshness;
  receivedAtMs?: number;
  value?: T;
}

export interface MemoryProjection {
  freshness: ObservationFreshness;
  receivedAtMs?: number;
  storageStatus: MemoryStorageStatus | 'unknown';
  storageIncomplete?: boolean;
  storedBytes?: number;
  capacityBytes?: number;
  rejectedWrites?: number;
  lastStorageErrorCode?: 'archive_capacity';
  lastStorageErrorAt?: string;
}

export interface TonightProjection {
  session: SessionProjection;
  device: ObservedValue<Pick<DeviceHealthObservation, 'phase' | 'roundTripMs' | 'busy'>>;
  network: ObservedValue<Pick<NetworkHealthObservation, 'state'>>;
  audio: ObservedValue<Pick<AudioHealthObservation, 'phase'>>;
  memory: MemoryProjection;
  nextGuest: { state: 'unknown' };
  keepsake: { state: 'unknown' };
}

const maxObservationAgeMs = 12_000;
const currentSessionStates = new Set<CurrentSessionState>(['starting', 'context_ready', 'running', 'recovering']);
const terminalSessionStates = new Set(['completed', 'failed']);
const audioPhases = new Set([
  'idle', 'starting', 'photo_capture', 'photo_upload', 'photo_wait',
  'listening', 'thinking', 'speaking', 'stopping', 'completed', 'failed', 'unavailable',
]);
const devicePhases = new Set(['ready', 'running', 'stopped', 'disconnected']);

interface CheckedObservation<T> {
  freshness: ObservationFreshness;
  receivedAtMs?: number;
  value?: T;
}

function validScope(scope: TonightScope | undefined): scope is TonightScope {
  return !!scope && typeof scope.accountId === 'string' && scope.accountId.trim().length > 0
    && typeof scope.deviceId === 'string' && scope.deviceId.trim().length > 0;
}

function sameScope(left: TonightScope, right: TonightScope): boolean {
  return left.accountId === right.accountId && left.deviceId === right.deviceId;
}

function checkObservation<T>(
  observation: ScopedObservation<T> | undefined,
  scope: TonightScope,
  nowMs: number,
  maxAgeMs: number,
): CheckedObservation<T> {
  if (!validScope(scope) || !Number.isFinite(nowMs) || !Number.isFinite(maxAgeMs) || maxAgeMs <= 0
    || !observation || !validScope(observation.scope) || !sameScope(observation.scope, scope)
    || observation.value === undefined || observation.error === true
    || observation.error !== undefined && typeof observation.error !== 'boolean'
    || observation.stale !== undefined && typeof observation.stale !== 'boolean') {
    return { freshness: 'unknown' };
  }

  const receivedAtMs = observation.receivedAtMs;
  if (typeof receivedAtMs !== 'number' || !Number.isFinite(receivedAtMs) || receivedAtMs > nowMs) {
    return { freshness: 'unknown' };
  }

  const freshness = observation.stale === true || nowMs - receivedAtMs > maxAgeMs ? 'stale' : 'fresh';
  return { freshness, receivedAtMs, value: observation.value };
}

function isSessionSummary(value: unknown): value is CustomerSessionSummary {
  if (!value || typeof value !== 'object') return false;
  const session = value as Partial<CustomerSessionSummary>;
  return typeof session.id === 'string' && session.id.trim().length > 0
    && typeof session.operationId === 'string' && session.operationId.trim().length > 0
    && typeof session.deviceId === 'string' && session.deviceId.trim().length > 0
    && typeof session.state === 'string' && session.state.trim().length > 0
    && typeof session.endRequested === 'boolean' && Number.isFinite(session.createdAt)
    && (session.characterName === undefined || session.characterName === null || typeof session.characterName === 'string');
}

function projectSession(
  observation: ScopedObservation<CustomerSessionSummary[]> | undefined,
  scope: TonightScope,
  nowMs: number,
  maxAgeMs: number,
): SessionProjection {
  const checked = checkObservation(observation, scope, nowMs, maxAgeMs);
  if (checked.freshness === 'unknown' || !Array.isArray(checked.value)) {
    return { freshness: 'unknown', state: 'unknown' };
  }
  if (!checked.value.every(isSessionSummary)) return { freshness: 'unknown', state: 'unknown' };

  const deviceSessions = checked.value.filter(session => session.deviceId === scope.deviceId);
  const activeSessions = deviceSessions.filter(session => currentSessionStates.has(session.state as CurrentSessionState));
  const hasUnknownState = deviceSessions.some(session => !currentSessionStates.has(session.state as CurrentSessionState)
    && !terminalSessionStates.has(session.state));
  const base = { freshness: checked.freshness, receivedAtMs: checked.receivedAtMs };

  if (hasUnknownState) return { ...base, state: 'unknown' };
  if (activeSessions.length > 1) return { ...base, state: 'ambiguous' };
  if (activeSessions.length === 0) return { ...base, state: 'no_active_session' };

  const selected = activeSessions[0];
  if (!selected || typeof selected.id !== 'string' || typeof selected.operationId !== 'string'
    || typeof selected.endRequested !== 'boolean' || !Number.isFinite(selected.createdAt)) {
    return { ...base, state: 'unknown' };
  }

  const current: CurrentSessionSummary = {
    id: selected.id,
    operationId: selected.operationId,
    deviceId: selected.deviceId,
    state: selected.state as CurrentSessionState,
    endRequested: selected.endRequested,
    createdAt: selected.createdAt,
    ...(typeof selected.characterName === 'string' || selected.characterName === null
      ? { characterName: selected.characterName } : {}),
  };
  return { ...base, state: 'active', current };
}

function projectDevice(
  observation: ScopedObservation<DeviceHealthObservation> | undefined,
  scope: TonightScope,
  nowMs: number,
  maxAgeMs: number,
): ObservedValue<Pick<DeviceHealthObservation, 'phase' | 'roundTripMs' | 'busy'>> {
  const checked = checkObservation(observation, scope, nowMs, maxAgeMs);
  const value = checked.value;
  if (checked.freshness === 'unknown' || !value || value.source !== 'native-device'
    || value.deviceId !== scope.deviceId || !devicePhases.has(value.phase)) return { freshness: 'unknown' };

  return {
    freshness: checked.freshness,
    receivedAtMs: checked.receivedAtMs,
    value: {
      phase: value.phase,
      ...(typeof value.roundTripMs === 'number' && Number.isFinite(value.roundTripMs) && value.roundTripMs >= 0
        ? { roundTripMs: value.roundTripMs } : {}),
      ...(typeof value.busy === 'boolean' ? { busy: value.busy } : {}),
    },
  };
}

function projectNetwork(
  observation: ScopedObservation<NetworkHealthObservation> | undefined,
  scope: TonightScope,
  nowMs: number,
  maxAgeMs: number,
): ObservedValue<Pick<NetworkHealthObservation, 'state'>> {
  const checked = checkObservation(observation, scope, nowMs, maxAgeMs);
  const value = checked.value;
  if (checked.freshness === 'unknown' || !value || value.deviceId !== scope.deviceId
    || !['connected', 'disconnected'].includes(value.state)) return { freshness: 'unknown' };

  return { freshness: checked.freshness, receivedAtMs: checked.receivedAtMs, value: { state: value.state } };
}

function projectAudio(
  observation: ScopedObservation<AudioHealthObservation> | undefined,
  scope: TonightScope,
  nowMs: number,
  maxAgeMs: number,
): ObservedValue<Pick<AudioHealthObservation, 'phase'>> {
  const checked = checkObservation(observation, scope, nowMs, maxAgeMs);
  const value = checked.value;
  if (checked.freshness === 'unknown' || !value || value.deviceId !== scope.deviceId || !audioPhases.has(value.phase)) {
    return { freshness: 'unknown' };
  }

  return { freshness: checked.freshness, receivedAtMs: checked.receivedAtMs, value: { phase: value.phase } };
}

function projectMemory(
  observation: ScopedObservation<TonightMemoryStatusSnapshot> | undefined,
  scope: TonightScope,
  nowMs: number,
  maxAgeMs: number,
): MemoryProjection {
  const checked = checkObservation(observation, scope, nowMs, maxAgeMs);
  const value = checked.value;
  if (checked.freshness === 'unknown' || !value) return { freshness: 'unknown', storageStatus: 'unknown' };

  const storageStatus = typeof value.storageStatus === 'string'
    && ['available', 'warning', 'full'].includes(value.storageStatus)
    ? value.storageStatus as MemoryStorageStatus : 'unknown';
  const hasUsableCapacity = Number.isSafeInteger(value.storedBytes) && value.storedBytes >= 0
    && Number.isSafeInteger(value.capacityBytes) && value.capacityBytes > 0;
  const rejectedWrites = Number.isSafeInteger(value.rejectedWrites) && value.rejectedWrites >= 0
    ? value.rejectedWrites : undefined;
  const lastStorageErrorCode = value.lastStorageErrorCode === 'archive_capacity' ? value.lastStorageErrorCode : undefined;
  const lastStorageErrorAt = lastStorageErrorCode && typeof value.lastStorageErrorAt === 'string'
    ? validUtcTimestamp(value.lastStorageErrorAt) ? value.lastStorageErrorAt : undefined
    : undefined;

  return {
    freshness: checked.freshness,
    receivedAtMs: checked.receivedAtMs,
    storageStatus,
    ...(typeof value.storageIncomplete === 'boolean' ? { storageIncomplete: value.storageIncomplete } : {}),
    ...(hasUsableCapacity ? { storedBytes: value.storedBytes, capacityBytes: value.capacityBytes } : {}),
    ...(rejectedWrites !== undefined ? { rejectedWrites } : {}),
    ...(lastStorageErrorCode ? { lastStorageErrorCode } : {}),
    ...(lastStorageErrorAt ? { lastStorageErrorAt } : {}),
  };
}

function validUtcTimestamp(value: string): boolean {
  const match = /^(\d{4})-(\d{2})-(\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,7}))?(Z|\+00:00)$/.exec(value);
  if (!match) return false;
  if (Number(match[1]) < 1) return false;
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) return false;
  const canonical = `${match[1]}-${match[2]}-${match[3]}.${(match[4] ?? '').padEnd(3, '0').slice(0, 3)}Z`;
  return new Date(parsed).toISOString() === canonical;
}

/** Uses client receive time for display freshness only; the 12-second default is inclusive. */
export function projectTonight(input: TonightInput, nowMs: number, maxAgeMs = maxObservationAgeMs): TonightProjection {
  const scope = input?.scope;
  if (!validScope(scope) || !Number.isFinite(nowMs) || !Number.isFinite(maxAgeMs) || maxAgeMs <= 0) {
    return {
      session: { freshness: 'unknown', state: 'unknown' },
      device: { freshness: 'unknown' },
      network: { freshness: 'unknown' },
      audio: { freshness: 'unknown' },
      memory: { freshness: 'unknown', storageStatus: 'unknown' },
      nextGuest: { state: 'unknown' },
      keepsake: { state: 'unknown' },
    };
  }

  return {
    session: projectSession(input.sessions, scope, nowMs, maxAgeMs),
    device: projectDevice(input.device, scope, nowMs, maxAgeMs),
    network: projectNetwork(input.network, scope, nowMs, maxAgeMs),
    audio: projectAudio(input.audio, scope, nowMs, maxAgeMs),
    memory: projectMemory(input.memory, scope, nowMs, maxAgeMs),
    nextGuest: { state: 'unknown' },
    keepsake: { state: 'unknown' },
  };
}

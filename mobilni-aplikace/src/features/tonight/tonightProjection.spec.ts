import { describe, expect, it } from 'vitest';
import { projectTonight, type ScopedObservation, type TonightInput, type TonightScope } from './tonightProjection';
import type { TonightMemoryStatusSnapshot } from './tonightProjection';

const scope: TonightScope = { accountId: 'operator-1', deviceId: 'case-1' };
const nowMs = 1_800_000_000_000;
const freshAt = nowMs - 1000;
const activeSession = {
  id: 'session-1', operationId: 'operation-1', deviceId: 'case-1', profileId: 'profile-1',
  profileRevision: 3, state: 'running', endRequested: false, createdAt: 1_799_999_000,
  characterName: 'Evening guest',
};
function observation<T>(value: T, overrides: Partial<ScopedObservation<T>> = {}): ScopedObservation<T> {
  return { scope, value, receivedAtMs: freshAt, ...overrides };
}
function project(overrides: Partial<TonightInput> = {}, now = nowMs) {
  return projectTonight({ scope, ...overrides }, now);
}

describe('Tonight status projection', () => {
  it('shows one current session without turning an end request into a terminal or ready state', () => {
    const source = { ...activeSession, endRequested: true };
    const result = project({ sessions: observation([source]) });

    expect(result.session.freshness).toBe('fresh');
    expect(result.session.state).toBe('active');
    expect(result.session.current).toMatchObject({ state: 'running', endRequested: true, characterName: 'Evening guest' });
    expect(result.session.current).not.toHaveProperty('leaseAvailable');
    expect(result.session.current).not.toHaveProperty('canStart');
    expect(result.nextGuest.state).toBe('unknown');
    expect(result.keepsake.state).toBe('unknown');
  });

  it('preserves recovering as a distinct live state without implying release or completion', () => {
    const result = project({ sessions: observation([{ ...activeSession, state: 'recovering' }]) });

    expect(result.session.state).toBe('active');
    expect(result.session.current?.state).toBe('recovering');
    expect(result.session.current).not.toHaveProperty('leaseAvailable');
  });

  it('reports no active session without claiming the device is available', () => {
    const sessions = [
      { ...activeSession, state: 'completed' },
      { ...activeSession, id: 'failed-1', state: 'failed' },
    ];
    const result = project({ sessions: observation(sessions) });

    expect(result.session.state).toBe('no_active_session');
    expect(result.session).not.toHaveProperty('deviceAvailable');
    expect(result.nextGuest.state).toBe('unknown');
  });

  it('marks multiple current sessions ambiguous and unknown or queued states unknown', () => {
    const ambiguous = project({ sessions: observation([
      activeSession,
      { ...activeSession, id: 'session-2', operationId: 'operation-2', state: 'starting' },
    ]) });
    const unknown = project({ sessions: observation([{ ...activeSession, state: 'awaiting_handoff_ack' }]) });
    const queued = project({ sessions: observation([{ ...activeSession, state: 'queued' }]) });

    expect(ambiguous.session.state).toBe('ambiguous');
    expect(ambiguous.session.current).toBeUndefined();
    expect(unknown.session.state).toBe('unknown');
    expect(unknown.session.current).toBeUndefined();
    expect(queued.session.state).toBe('unknown');
    expect(queued.session.current).toBeUndefined();
  });

  it('treats ended as an unknown state because it is not a verified terminal journal state', () => {
    const result = project({ sessions: observation([{ ...activeSession, state: 'ended' }]) });

    expect(result.session.state).toBe('unknown');
    expect(result.session.current).toBeUndefined();
  });

  it('does not hide an unknown nonterminal row behind one known active session', () => {
    const result = project({ sessions: observation([
      activeSession,
      { ...activeSession, id: 'unknown-1', operationId: 'unknown-operation', state: 'future_state' },
    ]) });

    expect(result.session.state).toBe('unknown');
    expect(result.session.current).toBeUndefined();
  });

  it('returns unknown for malformed rows but ignores well-formed rows scoped to another device', () => {
    const malformed = project({ sessions: observation([
      activeSession,
      { ...activeSession, deviceId: '' },
    ]) });
    const otherDevice = project({ sessions: observation([
      { ...activeSession, deviceId: 'case-2' },
    ]) });

    expect(malformed.session.state).toBe('unknown');
    expect(malformed.session.current).toBeUndefined();
    expect(otherDevice.session.state).toBe('no_active_session');
  });

  it('uses inclusive client-receive freshness boundary and labels older observations stale', () => {
    const boundary = project({ sessions: observation([activeSession], { receivedAtMs: nowMs - 12_000 }) });
    const old = project({ sessions: observation([activeSession], { receivedAtMs: nowMs - 12_001 }) });

    expect(boundary.session.freshness).toBe('fresh');
    expect(old.session.freshness).toBe('stale');
    expect(old.session.state).toBe('active');
  });

  it('does not treat missing, nonfinite, future, errored, or explicitly stale timestamps as fresh', () => {
    const missing = project({ sessions: observation([activeSession], { receivedAtMs: null }) });
    const nonfinite = project({ sessions: observation([activeSession], { receivedAtMs: Number.NaN }) });
    const future = project({ sessions: observation([activeSession], { receivedAtMs: nowMs + 1 }) });
    const errored = project({ sessions: observation([activeSession], { error: true }) });
    const explicitlyStale = project({ sessions: observation([activeSession], { stale: true }) });

    for (const result of [missing, nonfinite, future, errored]) {
      expect(result.session.freshness).toBe('unknown');
      expect(result.session.current).toBeUndefined();
    }
    expect(explicitlyStale.session.freshness).toBe('stale');
  });

  it('does not use the session creation time as an observation receive time', () => {
    const result = project({ sessions: observation([activeSession], { receivedAtMs: undefined as unknown as number }) });

    expect(result.session.freshness).toBe('unknown');
    expect(result.session.state).toBe('unknown');
  });

  it('drops session and status values when the operator or selected device scope changes', () => {
    const memory = { storageStatus: 'warning', storageIncomplete: true, rejectedWrites: 4 } as TonightMemoryStatusSnapshot;
    const previousAccountInput = observation([activeSession]);
    const previousMemoryInput = observation(memory);
    const result = project({
      sessions: { ...previousAccountInput, scope: { ...scope, accountId: 'different-account' } },
      memory: { ...previousMemoryInput, scope: { ...scope, deviceId: 'different-device' } },
    });

    expect(result.session.freshness).toBe('unknown');
    expect(result.session.current).toBeUndefined();
    expect(result.memory.freshness).toBe('unknown');
    expect(result.memory.storageStatus).toBe('unknown');
    expect(result.memory.rejectedWrites).toBeUndefined();
  });

  it('keeps the authoritative storage status and historical rejection flag after capacity recovers', () => {
    const memory = {
      provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready',
      pending: 0, failed: 0, deleting: 0, storedBytes: 10, capacityBytes: 1000, rejectedWrites: 7,
      storageStatus: 'available', storageIncomplete: true,
    } as TonightMemoryStatusSnapshot;
    const result = project({ memory: observation(memory) });

    expect(result.memory.freshness).toBe('fresh');
    expect(result.memory.storageStatus).toBe('available');
    expect(result.memory.storageIncomplete).toBe(true);
    expect(result.memory.rejectedWrites).toBe(7);
  });

  it.each(['available', 'warning', 'full'] as const)('preserves authoritative memory status %s', storageStatus => {
    const result = project({ memory: observation({
      provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready',
      pending: 0, failed: 0, deleting: 0, storedBytes: 1, capacityBytes: 100, rejectedWrites: 0, storageStatus,
    }) });

    expect(result.memory.storageStatus).toBe(storageStatus);
  });

  it('does not derive available status from low byte usage when status is absent', () => {
    const result = project({ memory: observation({
      provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready',
      pending: 0, failed: 0, deleting: 0, storedBytes: 1, capacityBytes: 100, rejectedWrites: 0,
    }) });

    expect(result.memory.freshness).toBe('fresh');
    expect(result.memory.storageStatus).toBe('unknown');
  });

  it('fails closed on malformed storage status values without coercing them', () => {
    const memory = {
      provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready',
      pending: 0, failed: 0, deleting: 0, storedBytes: 1, capacityBytes: 100, rejectedWrites: 0,
      storageStatus: { toString: 'invalid' } as unknown as TonightMemoryStatusSnapshot['storageStatus'],
    } as TonightMemoryStatusSnapshot;

    expect(project({ memory: observation(memory) }).memory.storageStatus).toBe('unknown');
  });

  it('omits invalid counters and unusable capacity while retaining a valid status', () => {
    const result = project({ memory: observation({
      provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready',
      pending: 0, failed: 0, deleting: 0, storedBytes: Number.POSITIVE_INFINITY,
      capacityBytes: 0, rejectedWrites: -1, storageStatus: 'full', storageIncomplete: false,
    }) });

    expect(result.memory.storageStatus).toBe('full');
    expect(result.memory.storageIncomplete).toBe(false);
    expect(result.memory.storedBytes).toBeUndefined();
    expect(result.memory.capacityBytes).toBeUndefined();
    expect(result.memory.rejectedWrites).toBeUndefined();
  });

  it('preserves stale memory history without presenting it as fresh', () => {
    const memory = {
      provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready',
      pending: 0, failed: 0, deleting: 0, storedBytes: 10, capacityBytes: 100, rejectedWrites: 3,
      storageStatus: 'available', storageIncomplete: true,
    } as TonightMemoryStatusSnapshot;
    const result = project({ memory: observation(memory, { receivedAtMs: nowMs - 20_000 }) });

    expect(result.memory.freshness).toBe('stale');
    expect(result.memory.storageStatus).toBe('available');
    expect(result.memory.storageIncomplete).toBe(true);
    expect(result.memory.rejectedWrites).toBe(3);
  });

  it('accepts the seven-field wire payload and exposes only a known safe error code with valid UTC time', () => {
    const memory: TonightMemoryStatusSnapshot = {
      storedBytes: 10, capacityBytes: 100, rejectedWrites: 2,
      storageStatus: 'warning', storageIncomplete: true,
      lastStorageErrorCode: 'archive_capacity', lastStorageErrorAt: '2026-10-23T10:15:30.000Z',
    };
    const result = project({ memory: observation(memory) });

    expect(result.memory).toMatchObject({
      storageStatus: 'warning', storageIncomplete: true, rejectedWrites: 2,
      storedBytes: 10, capacityBytes: 100,
      lastStorageErrorCode: 'archive_capacity', lastStorageErrorAt: '2026-10-23T10:15:30.000Z',
    });
    expect(project({ memory: observation({ ...memory, lastStorageErrorCode: 'private_backend_code' }) }).memory)
      .not.toHaveProperty('lastStorageErrorCode');
    expect(project({ memory: observation({ ...memory, lastStorageErrorAt: '2026-10-23T12:15:30+02:00' }) }).memory)
      .not.toHaveProperty('lastStorageErrorAt');
    expect(project({ memory: observation({ ...memory, lastStorageErrorAt: '2026-02-30T10:15:30Z' }) }).memory)
      .not.toHaveProperty('lastStorageErrorAt');
  });

  it.each([
    '2026-10-23T10:15:30.123+00:00',
    '2026-10-23T10:15:30.000+00:00',
    '2026-10-23T10:15:30+00:00',
    '2026-10-23T10:15:30Z',
  ])('accepts canonical UTC DateTimeOffset timestamps from the wire: %s', lastStorageErrorAt => {
    const memory: TonightMemoryStatusSnapshot = {
      storedBytes: 10, capacityBytes: 100, storageStatus: 'warning', rejectedWrites: 2,
      storageIncomplete: true, lastStorageErrorCode: 'archive_capacity', lastStorageErrorAt,
    };

    expect(project({ memory: observation(memory) }).memory.lastStorageErrorAt).toBe(lastStorageErrorAt);
  });

  it('requires scoped native-device evidence and keeps missing network/audio health unknown', () => {
    const result = project({
      device: observation({ source: 'simulator', deviceId: 'case-1', phase: 'ready' }),
      network: observation({ deviceId: 'other-case', state: 'connected' }),
      audio: observation({ deviceId: 'case-1', phase: 'speaking' }),
    });
    const missing = project();

    expect(result.device.freshness).toBe('unknown');
    expect(result.network.freshness).toBe('unknown');
    expect(result.audio.freshness).toBe('fresh');
    expect(result.audio.value?.phase).toBe('speaking');
    expect(missing.device.freshness).toBe('unknown');
    expect(missing.network.freshness).toBe('unknown');
    expect(missing.audio.freshness).toBe('unknown');
  });

  it('passes through explicit scoped device and network observations but suppresses errored audio', () => {
    const result = project({
      device: observation({ source: 'native-device', deviceId: 'case-1', phase: 'ready', roundTripMs: 18, busy: true }),
      network: observation({ deviceId: 'case-1', state: 'connected' }),
      audio: observation({ deviceId: 'case-1', phase: 'speaking' }, { error: true }),
    });

    expect(result.device.freshness).toBe('fresh');
    expect(result.device.value).toEqual({ phase: 'ready', roundTripMs: 18, busy: true });
    expect(result.device.value).not.toHaveProperty('canStart');
    expect(result.device.value).not.toHaveProperty('available');
    expect(result.network.freshness).toBe('fresh');
    expect(result.network.value?.state).toBe('connected');
    expect(result.audio.freshness).toBe('unknown');
    expect(result.audio.value).toBeUndefined();
  });

  it('does not mutate observed input or retain the caller session array', () => {
    const source = { ...activeSession, endRequested: true };
    const sessions = [source];
    const input = observation(sessions);
    const result = project({ sessions: input });
    source.endRequested = false;
    sessions.push({ ...activeSession, id: 'session-2', operationId: 'operation-2' });

    expect(result.session.current?.endRequested).toBe(true);
    expect(result.session.current?.id).toBe('session-1');
    expect(result.session.current).not.toBe(source);
    expect(result.session.current?.createdAt).toBe(activeSession.createdAt);
  });

  it('returns unknown for invalid clock configuration or future receive times', () => {
    const input: TonightInput = { scope, sessions: observation([activeSession]) };
    const invalidNow = projectTonight(input, Number.NaN);
    const invalidAge = projectTonight(input, nowMs, Number.POSITIVE_INFINITY);
    const nonpositiveAge = projectTonight(input, nowMs, 0);
    const negativeAge = projectTonight(input, nowMs, -1);

    expect(invalidNow.session.freshness).toBe('unknown');
    expect(invalidAge.session.freshness).toBe('unknown');
    expect(nonpositiveAge.session.freshness).toBe('unknown');
    expect(negativeAge.session.freshness).toBe('unknown');
  });
});

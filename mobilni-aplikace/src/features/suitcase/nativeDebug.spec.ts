import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
const bridge = vi.hoisted(() => ({ status: vi.fn(), stop: vi.fn(), forgetProfile: vi.fn(), readServerLogs: vi.fn(), useAccount: vi.fn() }));
vi.mock('@capacitor/core', () => ({ registerPlugin: () => bridge }));
vi.mock('./deviceSession', () => ({ deviceSession: { native: true } }));
let debug: (typeof import('./nativeDebug'))['nativeDebug'];
beforeEach(async () => {
  vi.resetModules(); vi.clearAllMocks();
  bridge.status.mockResolvedValue({ configured: true, running: false, phase: 'idle', mode: 'fake' });
  bridge.stop.mockResolvedValue(undefined); bridge.forgetProfile.mockResolvedValue(undefined);
  bridge.useAccount.mockResolvedValue(undefined);
  debug = (await import('./nativeDebug')).nativeDebug;
});
afterEach(() => { debug.dispose(); vi.restoreAllMocks(); });
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(finish => { resolve = finish; }); return { promise, resolve }; }
describe('native diagnostic ownership', () => {
  it('serializes cancellation while a previous request finishes', async () => {
    const answer = deferred<void>(); const stop = deferred<void>(); bridge.stop.mockReturnValue(stop.promise);
    const oldAction = debug.run(() => answer.promise); const cancellation = debug.cancel();
    const repeat = debug.cancel(); expect(bridge.stop).toHaveBeenCalledOnce(); expect(debug.cancellationPending.value).toBe(true);
    answer.resolve(); await oldAction; expect(debug.pending.value).toBe(true);
    const forbidden = vi.fn(); await debug.run(forbidden); expect(forbidden).not.toHaveBeenCalled();
    stop.resolve(); await Promise.all([cancellation, repeat]); expect(debug.pending.value).toBe(false);
  });
  it('late previous guest completion cannot unlock a newer request', async () => {
    const previous = deferred<void>(); const next = deferred<void>();
    const old = debug.run(() => previous.promise); await debug.cancel();
    const current = debug.run(() => next.promise); previous.resolve(); await old;
    expect(debug.pending.value).toBe(true); next.resolve(); await current; expect(debug.pending.value).toBe(false);
  });
  it('revocation waits for cancellation and surfaces failed profile deletion', async () => {
    const stop = deferred<void>(); bridge.stop.mockReturnValue(stop.promise);
    const cancellation = debug.cancel(); const revoke = debug.revokeUserGrants();
    expect(bridge.forgetProfile).not.toHaveBeenCalled(); stop.resolve(); await Promise.all([cancellation, revoke]);
    expect(bridge.forgetProfile).toHaveBeenCalledOnce(); expect(debug.status.value.configured).toBe(false);
    bridge.forgetProfile.mockRejectedValue(new Error('Keychain locked'));
    await expect(debug.revokeUserGrants()).rejects.toThrow('Keychain locked');
  });
  it('an old log response cannot restore events after user grants were revoked', async () => {
    const log = deferred<unknown>(); bridge.readServerLogs.mockReturnValue(log.promise);
    const reading = debug.readLogs('warning'); await debug.revokeUserGrants();
    log.resolve({ storeId: 'old', oldestCursor: 1, latestCursor: 1, nextCursor: 1, hasMore: false,
      truncated: false, droppedWrites: 0, entries: [{ id: 1, eventCode: 'old-private-technical-event' }] });
    await reading; expect(debug.logEntries.value).toEqual([]);
  });
  it('backgrounding cancels native audio and redacts guest content while routing itself does not cancel', async () => {
    bridge.status.mockResolvedValue({ configured: true, running: true, session: true, phase: 'listening', mode: 'openai',
      transcript: [{ role: 'visitor', text: 'private guest' }], selected: 3, evenings: { activeEveningId: 'private' } });
    await debug.refresh(); expect(bridge.stop).not.toHaveBeenCalled();
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true); document.dispatchEvent(new Event('visibilitychange'));
    expect(bridge.stop).toHaveBeenCalledOnce(); expect(debug.status.value.transcript).toEqual([]);
    await debug.cancel(); expect(debug.status.value.transcript).toEqual([]); expect(debug.status.value.evenings).toBeNull();
  });
  it('redacts synchronously and blocks old polls while noncooperative account Stop is pending', async () => {
    const stop = deferred<void>(); bridge.stop.mockReturnValue(stop.promise);
    bridge.status.mockResolvedValue({ configured: true, running: true, session: true, phase: 'listening', mode: 'openai',
      accessMode: 'account', deviceId: 'old-device', transcript: [{ role: 'visitor', text: 'old private guest' }], selected: 4, lastConversationId: 'old-private-id' });
    await debug.refresh(); const revoking = debug.revokeUserGrants();
    expect(debug.status.value.transcript).toEqual([]); expect(debug.status.value.configured).toBe(false);
    expect(debug.status.value.lastConversationId).toBeUndefined();
    await debug.refresh(); expect(debug.status.value.transcript).toEqual([]); expect(debug.status.value.configured).toBe(false);
    stop.resolve(); await revoking;
    await debug.refresh(); expect(debug.status.value.transcript).toEqual([]);
    bridge.status.mockResolvedValue({ configured: true, running: false, phase: 'idle', mode: 'openai', accessMode: 'account', deviceId: 'new-device', transcript: [] });
    await debug.useAccount('new-device'); expect(bridge.useAccount).toHaveBeenCalledWith({ deviceId: 'new-device' });
    expect(debug.status.value.configured).toBe(true); expect(debug.status.value.deviceId).toBe('new-device');
  });
  it('ignores blocked-era and in-admission old status responses after a new account is admitted', async () => {
    debug.invalidatePrivateState();
    const old = deferred<unknown>(); bridge.status.mockReturnValueOnce(old.promise);
    const oldPoll = debug.refresh();
    const admission = deferred<void>(); bridge.useAccount.mockReturnValueOnce(admission.promise);
    const admitting = debug.useAccount('new-device');
    const during = deferred<unknown>(); bridge.status.mockReturnValueOnce(during.promise);
    const duringPoll = debug.refresh();
    bridge.status.mockResolvedValue({ configured: true, running: false, phase: 'idle', mode: 'openai', accessMode: 'account', deviceId: 'new-device', transcript: [] });
    admission.resolve(); await admitting;
    const prior = { configured: true, running: true, phase: 'listening', mode: 'openai', accessMode: 'account', deviceId: 'old-device', transcript: [{ role: 'visitor', text: 'old account private text' }] };
    old.resolve(prior); during.resolve(prior); await Promise.all([oldPoll, duringPoll]);
    expect(debug.status.value.deviceId).toBe('new-device'); expect(debug.status.value.transcript).toEqual([]);
    expect(debug.status.value.configured).toBe(true);
  });
});

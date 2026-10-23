import { nextTick, reactive, watchEffect } from 'vue';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { SessionRecoveryController } from './sessionRecovery';

interface Sessions { states: string[] }
const active = (value: Sessions) => value.states.some(state => ['queued', 'starting', 'context_ready', 'running', 'recovering'].includes(state));

afterEach(() => vi.useRealTimers());

describe('SessionRecoveryController', () => {
  it('retries a failed active-session poll and clears stale state after success', async () => {
    vi.useFakeTimers();
    const refresh = vi.fn()
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce({ states: ['running'] });
    const controller = new SessionRecoveryController({ refresh, hasActiveSession: active, retryBaseDelayMs: 100, retryMaxDelayMs: 400 });

    const first = controller.setActive(true);
    await first;
    expect(controller.state).toMatchObject({ stale: true, retrying: true, retryDelayMs: 100 });
    await vi.advanceTimersByTimeAsync(100);

    expect(refresh).toHaveBeenCalledTimes(2);
    expect(controller.state).toMatchObject({ refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined });
    controller.dispose();
  });

  it('caps retry delay without abandoning an active session', async () => {
    vi.useFakeTimers();
    const refresh = vi.fn().mockRejectedValue(new TypeError('Failed to fetch'));
    const controller = new SessionRecoveryController({
      refresh,
      hasActiveSession: active,
      retryBaseDelayMs: 100,
      retryMaxDelayMs: 400,
    });

    await controller.setActive(true);
    for (let attempt = 0; attempt < 5; attempt += 1) {
      const delay = controller.state.retryDelayMs;
      expect(delay).toBeLessThanOrEqual(400);
      await vi.advanceTimersByTimeAsync(delay ?? 0);
      await Promise.resolve();
    }

    expect(refresh).toHaveBeenCalledTimes(6);
    expect(controller.state).toMatchObject({ stale: true, retrying: true, retryDelayMs: 400 });
    controller.dispose();
  });

  it('aborts and ignores work invalidated by an account generation change', async () => {
    let signal: AbortSignal | undefined;
    let finish: ((value: Sessions) => void) | undefined;
    const onValue = vi.fn();
    const controller = new SessionRecoveryController({
      refresh: requestSignal => {
        signal = requestSignal;
        return new Promise(resolve => { finish = resolve; });
      },
      hasActiveSession: active,
      onValue,
    });

    const pending = controller.refresh();
    controller.invalidate();
    finish?.({ states: ['running'] });
    await pending;

    expect(signal?.aborted).toBe(true);
    expect(onValue).not.toHaveBeenCalled();
    expect(controller.state).toMatchObject({ refreshing: false, stale: false, retrying: false });
    controller.dispose();
  });

  it('deduplicates manual and foreground refreshes while a request is pending', async () => {
    let finish: ((value: Sessions) => void) | undefined;
    const refresh = vi.fn(() => new Promise<Sessions>(resolve => { finish = resolve; }));
    const controller = new SessionRecoveryController({ refresh, hasActiveSession: active });

    const manual = controller.refresh();
    const foreground = controller.refresh();
    expect(foreground).toBe(manual);
    expect(refresh).toHaveBeenCalledTimes(1);
    finish?.({ states: [] });
    await manual;
    controller.dispose();
  });

  it('allows a consecutive refresh after a synchronous refresh failure', async () => {
    const refresh = vi.fn()
      .mockImplementationOnce(() => { throw new Error('temporary failure'); })
      .mockResolvedValueOnce({ states: [] });
    const controller = new SessionRecoveryController({ refresh, hasActiveSession: active });

    await controller.refresh();
    expect(controller.state.stale).toBe(true);
    await controller.refresh();

    expect(refresh).toHaveBeenCalledTimes(2);
    expect(controller.state).toMatchObject({ stale: false, refreshing: false, error: undefined });
    controller.dispose();
  });

  it('keeps polling a restored active session even without a local pending operation', async () => {
    vi.useFakeTimers();
    const refresh = vi.fn().mockResolvedValue({ states: ['recovering'] });
    const controller = new SessionRecoveryController({ refresh, hasActiveSession: active, pollIntervalMs: 250 });

    await controller.refresh();
    expect(controller.state.stale).toBe(false);
    await vi.advanceTimersByTimeAsync(250);
    expect(refresh).toHaveBeenCalledTimes(2);
    controller.dispose();
  });

  it('writes through the caller supplied reactive state object', async () => {
    const state = reactive({ refreshing: false, stale: false, retrying: false, retryDelayMs: null, error: undefined }) as {
      refreshing: boolean; stale: boolean; retrying: boolean; retryDelayMs: number | null; error: unknown;
    };
    const observed: boolean[] = [];
    const stop = watchEffect(() => { observed.push(state.refreshing); });
    const controller = new SessionRecoveryController({
      refresh: async () => ({ states: [] }),
      hasActiveSession: active,
      state,
    });

    await controller.refresh();
    await nextTick();

    expect(controller.state).toBe(state);
    expect(observed).toContain(true);
    expect(state.refreshing).toBe(false);
    stop();
    controller.dispose();
  });

  it('stops timers and ignores completion after disposal', async () => {
    vi.useFakeTimers();
    let finish: ((value: Sessions) => void) | undefined;
    const onValue = vi.fn();
    const controller = new SessionRecoveryController({
      refresh: () => new Promise(resolve => { finish = resolve; }),
      hasActiveSession: active,
      onValue,
    });

    const pending = controller.refresh();
    controller.dispose();
    finish?.({ states: ['running'] });
    await pending;
    await vi.advanceTimersByTimeAsync(5000);

    expect(onValue).not.toHaveBeenCalled();
    expect(controller.state.refreshing).toBe(false);
  });
});

export interface SessionRecoveryState {
  refreshing: boolean;
  stale: boolean;
  retrying: boolean;
  retryDelayMs: number | null;
  error: unknown;
}

export interface SessionRecoveryOptions<T> {
  refresh(signal: AbortSignal): Promise<T>;
  hasActiveSession(value: T): boolean;
  onValue?(value: T): void;
  /** Supply a Vue reactive object before construction when UI state must react. */
  state?: SessionRecoveryState;
  pollIntervalMs?: number;
  retryBaseDelayMs?: number;
  retryMaxDelayMs?: number;
}

/** Polls authoritative session state; it never starts or ends a session. */
export class SessionRecoveryController<T> {
  readonly state: SessionRecoveryState;

  private generation = 0;
  private active = false;
  private disposed = false;
  private failures = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private activeController: AbortController | undefined;
  private request: { generation: number; promise: Promise<T | undefined> } | undefined;

  constructor(private readonly options: SessionRecoveryOptions<T>) {
    this.state = options.state ?? {
      refreshing: false,
      stale: false,
      retrying: false,
      retryDelayMs: null,
      error: undefined,
    };
  }

  /** Start automatic recovery, or explicitly retry the current status request. */
  refresh(): Promise<T | undefined> {
    if (this.disposed) return Promise.resolve(undefined);
    if (this.request?.generation === this.generation) return this.request.promise;

    this.clearTimer();
    this.state.refreshing = true;
    this.state.retrying = false;
    this.state.retryDelayMs = null;
    const generation = this.generation;
    const controller = new AbortController();
    this.activeController = controller;
    let resolveRequest!: (value: T | undefined) => void;
    const promise = new Promise<T | undefined>(resolve => { resolveRequest = resolve; });
    this.request = { generation, promise };
    // Register the shared promise before invoking the loader. This also handles
    // loaders that throw before returning a Promise.
    void this.runRefresh(generation, controller).then(resolveRequest, () => resolveRequest(undefined));
    return promise;
  }

  /** Enable or stop polling. Enabling also performs an immediate refresh. */
  setActive(active: boolean): Promise<T | undefined> | undefined {
    if (this.disposed) return undefined;
    if (active) {
      if (this.active) return undefined;
      this.active = true;
      return this.refresh();
    }

    this.active = false;
    this.invalidateRequest();
    this.state.refreshing = false;
    this.state.stale = false;
    this.state.retrying = false;
    this.state.retryDelayMs = null;
    this.state.error = undefined;
    return undefined;
  }

  /** Invalidate work tied to the old account or view and allow later reuse. */
  invalidate(): void {
    if (this.disposed) return;
    this.active = false;
    this.invalidateRequest();
    this.state.refreshing = false;
    this.state.stale = false;
    this.state.retrying = false;
    this.state.retryDelayMs = null;
    this.state.error = undefined;
    this.failures = 0;
  }

  dispose(): void {
    if (this.disposed) return;
    this.invalidate();
    this.disposed = true;
  }

  private runRefresh(generation: number, controller: AbortController): Promise<T | undefined> {
    return (async () => {
      try {
        const value = await this.options.refresh(controller.signal);
        if (!this.isCurrent(generation, controller)) return undefined;

        this.state.refreshing = false;
        this.state.stale = false;
        this.state.retrying = false;
        this.state.retryDelayMs = null;
        this.state.error = undefined;
        this.failures = 0;
        this.options.onValue?.(value);
        if (!this.isCurrent(generation, controller)) return undefined;
        this.setPolling(this.options.hasActiveSession(value));
        return value;
      } catch (error) {
        if (!this.isCurrent(generation, controller)) return undefined;

        this.state.refreshing = false;
        this.state.stale = true;
        this.state.error = error;
        if (this.active) this.scheduleRetry();
        return undefined;
      } finally {
        if (this.request?.generation === generation) this.request = undefined;
        if (this.activeController === controller) this.activeController = undefined;
      }
    })();
  }

  private setPolling(active: boolean): void {
    this.active = active;
    this.clearTimer();
    if (active) {
      const delay = this.options.pollIntervalMs ?? 2500;
      this.schedule(delay, false);
    }
  }

  private scheduleRetry(): void {
    const base = Math.max(1, this.options.retryBaseDelayMs ?? 1000);
    const maximum = Math.max(base, this.options.retryMaxDelayMs ?? 30_000);
    const delay = Math.min(maximum, base * (2 ** Math.min(this.failures, 30)));
    this.failures = Math.min(this.failures + 1, 31);
    this.schedule(delay, true);
  }

  private schedule(delay: number, retrying: boolean): void {
    this.clearTimer();
    if (!this.active || this.disposed) return;
    this.state.retrying = retrying;
    this.state.retryDelayMs = retrying ? delay : null;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      void this.refresh();
    }, delay);
  }

  private invalidateRequest(): void {
    ++this.generation;
    this.clearTimer();
    this.activeController?.abort();
    this.activeController = undefined;
    this.request = undefined;
  }

  private clearTimer(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = undefined;
    this.state.retrying = false;
    this.state.retryDelayMs = null;
  }

  private isCurrent(generation: number, controller: AbortController): boolean {
    return !this.disposed && generation === this.generation && !controller.signal.aborted;
  }
}

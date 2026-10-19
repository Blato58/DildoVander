const STEP_US = 25;
const PULSE_MS = 1500;
const INTERVAL_MS = 3000;
const REASONS = {
  stop: "Stop", manual_stop: "ruční Stop", noise: "obsluha zaznamenala hluk",
  bound: "dosažena softwarová mez PWM", ack_timeout: "chybí potvrzení firmwaru",
  fault: "chyba firmwaru", disconnected: "USB odpojeno", controller_error: "chyba komunikace",
  baseline_lost: "ztraceno potvrzení bezpečného stavu", command_replaced: "povel přerušila jiná akce",
  unexpected_ack: "neočekávané potvrzení", command_rejected: "povel odmítnut",
  write_error: "zápis selhal", observer_error: "sledování stavu selhalo",
  state_error: "stav nelze přečíst", callback_error: "ovládání selhalo", step_limit: "limit počtu kroků",
};

// This scheduler proposes PWM values only. Firmware ACKs do not measure a servo position.
export class AutoCalibration {
  constructor({ getState, subscribe, arm, calibrate, stop, onChange = () => {},
    now = () => performance.now(), setTimer = (callback, delay) => setTimeout(callback, delay),
    clearTimer = id => clearTimeout(id) }) {
    Object.assign(this, { getState, subscribe, arm, calibrate, stop, onChange, now, setTimer, clearTimer });
    this.state = { active: false, stage: "idle", channel: null, direction: null,
      centerUs: null, currentUs: null, previousUs: null, candidateUs: null,
      candidateEvidence: null, stopConfirmed: false, reason: "", message: "" };
    this.token = 0;
    this.unsubscribe = null;
    this.timer = null;
    this.waiter = null;
    this.stopObserver = null;
  }

  start({ channel, direction, centerUs } = {}) {
    if (this.state.active || this.stopObserver || !Number.isInteger(channel) || channel < 0 || channel > 5 ||
      !["min", "max"].includes(direction) || !Number.isInteger(centerUs) ||
      centerUs < 1000 || centerUs > 2000) return false;
    let controller;
    try { controller = this.getState(); } catch { return false; }
    if (!this.ready(controller) || !controller.capabilities?.arm) return false;
    const token = ++this.token;
    this.state = { active: true, stage: "starting", channel, direction, centerUs,
      currentUs: centerUs, previousUs: null, candidateUs: null, candidateEvidence: null, stopConfirmed: false,
      reason: "", message: "Začíná asistovaná kalibrace; mějte Stop po ruce." };
    try {
      const unsubscribe = this.subscribe(() => this.check(token));
      if (!this.live(token)) unsubscribe?.();
      else this.unsubscribe = unsubscribe;
    } catch {
      this.cancel("observer_error");
      return false;
    }
    this.changed();
    if (this.live(token)) void this.run(token).catch(() => {
      if (this.live(token)) this.cancel("callback_error");
    });
    return true;
  }

  ready(state) {
    return state?.connected && state.baseline && state.calibrationSupported &&
      !state.fault && !state.error && !state.connecting && !state.closing;
  }

  live(token) { return this.state.active && token === this.token; }

  changed() {
    try { this.onChange({ ...this.state }); } catch { /* Rendering cannot resume or queue motion. */ }
  }

  clearWaiting() {
    this.clearTimer(this.timer);
    this.timer = null;
    const waiter = this.waiter;
    this.waiter = null;
    waiter?.resolve(false);
    const unsubscribe = this.unsubscribe;
    this.unsubscribe = null;
    try { unsubscribe?.(); } catch { /* Cancellation remains final. */ }
  }

  clearStopObserver() {
    const observer = this.stopObserver;
    this.stopObserver = null;
    if (!observer) return;
    this.clearTimer(observer.timer);
    try { observer.unsubscribe?.(); } catch { /* No motion depends on an observer. */ }
  }

  safeStop(trackCandidate = false) {
    let state;
    try { state = this.getState(); } catch { return; }
    // Firmware faults already run the firmware stop path; never retry failed I2C automatically.
    if (!state.connected || state.fault || state.pending === "stop") return;
    if (!trackCandidate) {
      try { Promise.resolve(this.stop()).catch(() => {}); } catch { /* No retries or OFF claim. */ }
      return;
    }
    const token = this.token;
    const observer = { observedPending: false, writeDone: false, unsubscribe: null, timer: null };
    this.stopObserver = observer;
    const check = () => {
      if (token !== this.token || this.stopObserver !== observer) return;
      let current;
      try { current = this.getState(); } catch { this.clearStopObserver(); return; }
      if (!current.connected || current.fault || current.error) { this.clearStopObserver(); return; }
      if (current.pending === "stop") observer.observedPending = true;
      if (observer.observedPending && observer.writeDone && !current.pending && !current.writing &&
        current.baseline && !current.armedUntil) {
        this.clearStopObserver();
        this.state.stopConfirmed = true;
        this.changed();
      }
    };
    let invoked = false;
    try {
      const unsubscribe = this.subscribe(check);
      if (this.stopObserver === observer) {
        observer.unsubscribe = unsubscribe;
        observer.timer = this.setTimer(() => {
          if (this.stopObserver === observer) this.clearStopObserver();
        }, 3000);
      } else unsubscribe?.();
      invoked = true;
      Promise.resolve(this.stop()).then(accepted => {
        if (token !== this.token || this.stopObserver !== observer) return;
        if (accepted !== true) { this.clearStopObserver(); return; }
        observer.writeDone = true;
        check();
      }, () => { if (this.stopObserver === observer) this.clearStopObserver(); });
    } catch {
      this.clearStopObserver();
      if (!invoked) {
        try { Promise.resolve(this.stop()).catch(() => {}); } catch { /* Stop is attempted once. */ }
      }
    }
  }

  finish(reason, candidateUs, sendStop, stage = "cancelled") {
    ++this.token; // Invalidate async continuations before Stop can publish controller state.
    this.clearStopObserver();
    this.state = { ...this.state, active: false, stage, candidateUs,
      candidateEvidence: candidateUs === null ? null : {
        channel: this.state.channel, pulseWidthUs: candidateUs, centerUs: this.state.centerUs,
        direction: this.state.direction, completed: true,
      }, stopConfirmed: false, reason,
      message: candidateUs === null ? `Kalibrace ukončena (${REASONS[reason] ?? "přerušeno"}); bez návrhu.` :
        `Návrh PWM ${candidateUs} µs (${REASONS[reason] ?? "přerušeno"}); vyžaduje potvrzení obsluhou.`,
    };
    this.clearWaiting();
    if (sendStop) this.safeStop(candidateUs !== null);
    this.changed();
  }

  cancel(reason = "stop", { sendStop = true } = {}) {
    if (!this.state.active) {
      this.discardCandidate();
      return false;
    }
    this.finish(reason, null, sendStop);
    return true;
  }

  markNoise() {
    if (!this.state.active) return false;
    // Even after DONE, currentUs is excluded: noise may have been noticed during the pause.
    this.finish("noise", this.state.previousUs, true);
    return true;
  }

  discardCandidate(message = "Návrh zahozen; profil se nezměnil.") {
    this.clearStopObserver();
    if (this.state.candidateUs === null) return;
    this.state = { ...this.state, candidateUs: null, candidateEvidence: null, stopConfirmed: false,
      message };
    this.changed();
  }

  check(token) {
    if (!this.live(token)) return;
    let state;
    try { state = this.getState(); } catch { this.cancel("state_error"); return; }
    if (!this.ready(state)) {
      this.cancel(!state?.connected ? "disconnected" : state.fault ? "fault" :
        state.error ? "controller_error" : "baseline_lost", {
        sendStop: !!state?.connected && !state.fault && state.pending !== "stop",
      });
      return;
    }
    const waiter = this.waiter;
    if (!waiter?.kind) {
      if (state.pending || state.writing || state.armedUntil) this.cancel("command_replaced");
      return;
    }
    if (state.pending && state.pending !== waiter.kind) {
      this.cancel("command_replaced");
      return;
    }
    if (state.pending === waiter.kind) waiter.observedPending = true;
    if (!waiter.observedPending || state.pending || state.writing) return;
    const complete = waiter.kind === "arm" ?
      state.armedUntil > 0 && state.capabilities?.calibrate :
      !state.armedUntil && state.lastCompleted?.calibration === true &&
        state.lastCompleted.channel === this.state.channel &&
        state.lastCompleted.pulseWidthUs === this.state.currentUs &&
        state.lastCompleted.durationMs === PULSE_MS;
    if (!complete) {
      this.cancel("unexpected_ack");
      return;
    }
    if (waiter.writeDone && !waiter.settled) {
      waiter.settled = true;
      this.clearTimer(this.timer);
      this.timer = null;
      waiter.resolve(true);
    }
  }

  command(token, kind, invoke) {
    if (!this.live(token)) return Promise.resolve(false);
    this.state.stage = kind === "arm" ? "arming" : "pulsing";
    this.state.message = kind === "arm" ? "Čekám na potvrzení Arm; hluk ihned zastavte." :
      "Čekám na kalibrační pulz 1500 ms a potvrzení DONE; hluk ihned zastavte.";
    return new Promise(resolve => {
      const waiter = { kind, resolve, observedPending: false, writeDone: false, settled: false };
      this.waiter = waiter;
      this.timer = this.setTimer(() => {
        if (this.live(token) && this.waiter === waiter) this.cancel("ack_timeout");
      }, kind === "arm" ? 3000 : 4500);
      this.changed();
      if (!this.live(token)) return;
      // Invoke synchronously after installing the waiter, so a Stop cannot race a queued invocation.
      try {
        const result = invoke();
        if (this.live(token)) this.changed();
        Promise.resolve(result).then(accepted => {
          if (!this.live(token) || this.waiter !== waiter) return;
          if (accepted !== true) { this.cancel("command_rejected"); return; }
          waiter.writeDone = true;
          this.check(token);
        }, () => { if (this.live(token)) this.cancel("write_error"); });
      } catch {
        if (this.live(token)) this.cancel("write_error");
      }
    });
  }

  pause(token, milliseconds) {
    if (!this.live(token)) return Promise.resolve(false);
    this.waiter = null;
    this.state.stage = "pause";
    this.state.message = `PWM ${this.state.currentUs} µs má potvrzené DONE; čekám před dalším krokem. Hluk ihned zastavte.`;
    this.changed();
    if (!this.live(token)) return Promise.resolve(false);
    return new Promise(resolve => {
      this.waiter = { resolve };
      this.timer = this.setTimer(() => {
        this.timer = null;
        if (this.live(token)) resolve(true);
      }, milliseconds);
    });
  }

  async run(token) {
    let target = this.state.centerUs;
    let lastCompleted = null;
    const endpoint = this.state.direction === "min" ? 1000 : 2000;
    const step = this.state.direction === "min" ? -STEP_US : STEP_US;
    for (let count = 0; count < 41 && this.live(token); ++count) {
      if (!await this.command(token, "arm", () => this.arm()) || !this.live(token)) return;
      let started;
      if (!await this.command(token, "pulse", () => {
        // Keep the audible target ledger through the next Arm/write window. Only
        // an actual calibration attempt may replace it; no callback runs in between.
        this.state.currentUs = target;
        this.state.previousUs = lastCompleted;
        started = this.now();
        return this.calibrate(this.state.channel, target, PULSE_MS);
      }) || !this.live(token)) return;
      lastCompleted = target;
      if (target === endpoint) {
        this.finish("bound", target === this.state.centerUs ? null : target, true, "completed");
        return;
      }
      if (!await this.pause(token, Math.max(0, INTERVAL_MS - (this.now() - started))) ||
        !this.live(token)) return;
      target = step < 0 ? Math.max(endpoint, target + step) : Math.min(endpoint, target + step);
    }
    if (this.live(token)) this.cancel("step_limit");
  }
}

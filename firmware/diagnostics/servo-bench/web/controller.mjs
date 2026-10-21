// Firmware acknowledgements are authoritative. No motion command is retried.
export class LineParser {
  constructor(onLine, onOverflow = () => {}) {
    this.onLine = onLine;
    this.onOverflow = onOverflow;
    this.buffer = "";
    this.dropping = false;
  }

  push(text) {
    for (const character of text) {
      if (character === "\n") {
        if (!this.dropping) this.onLine(this.buffer.replace(/\r$/, ""));
        this.buffer = "";
        this.dropping = false;
      } else if (!this.dropping) {
        this.buffer += character;
        if (this.buffer.length > 2048) {
          this.buffer = "";
          this.dropping = true;
          this.onOverflow();
        }
      }
    }
  }
}

export function parseReadback(line) {
  const match = line.match(/(?:^|\s)PCA_READBACK\s+ch=(\d+) mode1=(0x[\da-f]+) mode2=(0x[\da-f]+) prescale=(\d+) on=(\d+) off=(\d+) full_on=([01]) full_off=([01]) expected_off=(\d+) match=([01])\s*$/i);
  if (!match) return null;
  return {
    channel: Number(match[1]), mode1: match[2], mode2: match[3], prescale: Number(match[4]),
    on: Number(match[5]), off: Number(match[6]), fullOn: match[7] === "1",
    fullOff: match[8] === "1", expectedOff: Number(match[9]), match: match[10] === "1",
  };
}

const LegacyCalibrationMinimumUs = 1000;
const LegacyCalibrationMaximumUs = 3000;

export class BenchController {
  constructor({ write, onChange = () => {}, onLog = () => {}, now = Date.now,
    setTimer = (callback, delay) => setTimeout(callback, delay), clearTimer = id => clearTimeout(id) }) {
    Object.assign(this, { write, onChange, onLog, now, setTimer, clearTimer });
    this.state = { connected: false, baseline: false, fault: false, pending: null, writing: false,
      armedUntil: 0, calibrationSupported: false, calibrationMinimumUs: null,
      calibrationMaximumUs: null, lastCompleted: null,
      danceSupported: false, danceBoot: null, armDanceToken: null, danceSession: null,
      danceSequence: 0, lastApplied: null, danceRttMs: null,
      message: "Neaktivní · odpojeno", error: "", errorKind: "", readback: null };
    this.generation = 0;
  }

  get capabilities() {
    const state = this.state;
    const idle = state.connected && state.baseline && !state.fault && !state.pending && !state.writing;
    return { arm: idle && !state.armedUntil && !state.danceSession,
      pulse: idle && state.armedUntil > this.now() && !state.danceSession,
      calibrate: idle && state.armedUntil > this.now() && state.calibrationSupported && !state.danceSession,
      danceStart: idle && state.armedUntil > this.now() && state.danceSupported && !!state.armDanceToken,
      danceTarget: idle && !!state.danceSession, stop: state.connected };
  }

  changed() { this.onChange(this.state, this.capabilities); }

  clearArm() {
    this.clearTimer(this.armTimer);
    this.state.armedUntil = 0;
    this.state.armDanceToken = null;
  }

  clearDance() {
    this.state.danceSession = null;
    this.state.danceSequence = 0;
    this.state.lastApplied = null;
    this.expectedTarget = null;
    this.requestedDance = null;
  }

  clearPending() {
    this.clearTimer(this.ackTimer);
    this.state.pending = null;
  }

  connected() {
    this.generation++;
    this.clearArm();
    this.clearPending();
    this.activePulse = null;
    this.clearDance();
    Object.assign(this.state, { connected: true, baseline: false, fault: false, writing: false,
      error: "", errorKind: "", readback: null, calibrationSupported: false,
      calibrationMinimumUs: null, calibrationMaximumUs: null, lastCompleted: null,
      danceSupported: false, danceBoot: null, danceRttMs: null });
    return this.stop("!\n");
  }

  disconnected(message = "Odpojeno · stav PWM není potvrzen") {
    this.generation++;
    this.clearArm();
    this.clearPending();
    this.activePulse = null;
    this.clearDance(); this.state.danceSupported = false; this.state.danceBoot = null;
    Object.assign(this.state, { connected: false, baseline: false, writing: false, message,
      calibrationSupported: false, calibrationMinimumUs: null, calibrationMaximumUs: null, lastCompleted: null });
    this.changed();
  }

  async command(command, pending, message, timeoutMs = 3000) {
    const generation = ++this.generation;
    this.state.pending = pending;
    this.state.writing = true;
    this.state.message = message;
    this.state.error = "";
    this.state.errorKind = "";
    // Start before write; a stalled transport must never leave Arm usable.
    this.ackTimer = this.setTimer(() => {
      if (generation !== this.generation) return;
      this.fail("Chybí potvrzení firmwaru. Stav PWM není známý; použijte Stop nebo odpojte napájení serv.", false, "ack_timeout");
    }, timeoutMs);
    this.changed();
    this.onLog(`→ ${command.trim()}`);
    try {
      await this.write(command, pending === "stop");
      if (generation === this.generation) {
        this.state.writing = false;
        this.changed();
      }
      return true;
    } catch (error) {
      if (generation === this.generation) this.fail(`Zápis selhal: ${error.message}`);
      return false;
    }
  }

  arm() {
    if (!this.capabilities.arm) return false;
    // Conservative deadline starts on send, before firmware receives Arm.
    this.armDeadline = this.now() + 9500;
    return this.command("arm\n", "arm", "Čekám na potvrzení Arm…");
  }

  pulse(channel, width) {
    if (!this.capabilities.pulse || !Number.isInteger(channel) || channel < 0 || channel > 5 ||
      ![1400, 1500, 1600].includes(width)) return false;
    this.clearArm();
    this.state.lastCompleted = null;
    this.activePulse = { channel, pulseWidthUs: width, durationMs: 500, calibration: false };
    return this.command(`pulse ${channel} ${width}\n`, "pulse", "Čekám na pulz a potvrzení PWM FULL_OFF…");
  }

  calibrate(channel, width, durationMs) {
    if (!this.capabilities.calibrate || !Number.isInteger(channel) || channel < 0 || channel > 5 ||
      !Number.isInteger(width) || width < this.state.calibrationMinimumUs ||
      width > this.state.calibrationMaximumUs ||
      !Number.isInteger(durationMs) || durationMs < 500 || durationMs > 3000) return false;
    this.clearArm();
    this.state.lastCompleted = null;
    this.activePulse = { channel, pulseWidthUs: width, durationMs, calibration: true };
    const command = `cal ${channel} ${String(width).padStart(4, "0")} ${String(durationMs).padStart(4, "0")}\n`;
    return this.command(command, "pulse", `Čekám na kalibrační pulz ${durationMs} ms a PWM FULL_OFF…`,
      Math.max(3000, durationMs + 1500));
  }

  startDance(mask) {
    if (!this.capabilities.danceStart || !Number.isInteger(mask) || mask < 1 || mask > 63) return false;
    const token = this.state.armDanceToken;
    this.clearArm(); this.clearDance();
    this.requestedDance = { token, mask };
    return this.command(`dance ${mask.toString(16).padStart(2, '0')} ${this.state.danceBoot} ${token}\n`, 'dance-start', 'Čekám na potvrzení plynulého tance…', 150);
  }

  target(widths) {
    if (!this.capabilities.danceTarget || !Array.isArray(widths) || widths.length !== 6 || this.state.danceSequence >= 99999 ||
      !widths.every((width, channel) => Number.isInteger(width) && (this.state.danceSession.mask & (1 << channel) ? width >= 600 && width <= 2300 : width === 0))) return false;
    const sequence = this.state.danceSequence + 1;
    this.expectedTarget = { token: this.state.danceSession.token, sequence, sentAt: this.now() };
    return this.command(`target ${this.expectedTarget.token} ${String(sequence).padStart(5, '0')} ${widths.map(width => String(width).padStart(4, '0')).join('')}\n`,
      'dance-target', 'Plynulý tanec · čekám na potvrzení registrů.', 150);
  }

  stop(command = "!\n") {
    if (!this.state.connected) return false;
    this.clearArm();
    this.clearPending();
    this.clearDance();
    this.state.baseline = false;
    this.activePulse = null;
    this.state.lastCompleted = null;
    return this.command(command, "stop", "Stop odesílán · čekám na PWM FULL_OFF…");
  }

  fail(message, fault = false, errorKind = "") {
    this.generation++;
    this.clearArm();
    this.clearPending();
    this.clearDance();
    this.state.baseline = false;
    this.state.writing = false;
    this.activePulse = null;
    this.state.lastCompleted = null;
    this.state.fault ||= fault;
    this.state.error = message;
    this.state.errorKind = errorKind;
    this.state.message = "Neaktivní · chyba nebo nepotvrzený stav";
    this.changed();
  }

  receive(line) {
    this.onLog(`← ${line}`);
    if (!this.state.connected) return;
    const readback = parseReadback(line);
    if (readback) {
      this.state.readback = readback;
      if (!readback.match) this.fail("Čtení registrů PCA9685 neodpovídá zápisu. Odpojte napájení serv a zkontrolujte zařízení.", true);
      else this.changed();
      return;
    }
    const message = line.replace(/^.*?SERVO_BENCH\s+/, "");
    if (/^(?:FAULT|NOT READY|PCA_READBACK failed)|\berror\b/i.test(message)) {
      this.fail(`Firmware: ${message}`, true);
    } else if (/^REJECT\b/.test(message)) {
      this.fail(`Firmware odmítl povel: ${message}`);
    } else if (/^READY PWM FULL_OFF\b/.test(message)) {
      this.clearArm();
      if (this.state.pending !== "stop") this.clearPending();
      this.state.baseline = false;
      this.state.fault = false;
      this.state.readback = null;
      this.clearDance();
      const danceBoot = message.match(/(?:^|\s)dance_boot=([0-9a-f]{8})(?=\s|;|$)/)?.[1];
      this.state.danceSupported = /(?:^|\s)dance=v1(?:\s|;|$)/.test(message) && !!danceBoot && danceBoot !== '00000000';
      this.state.danceBoot = this.state.danceSupported ? danceBoot : null;
      const configuredLimits = message.match(
        /(?:^|\s)calibration_min_us=(\d{1,4})\s+calibration_max_us=(\d{1,4})(?=\s|;|$)/);
      const configuredMinimum = configuredLimits ? Number(configuredLimits[1]) : null;
      const configuredMaximum = configuredLimits ? Number(configuredLimits[2]) : null;
      const hasValidConfiguredLimits = configuredMinimum > 0 &&
        configuredMinimum <= configuredMaximum && configuredMaximum <= 9999;
      const legacyCalibration = /(?:^|\s)calibration=v1(?:\s|;|$)/.test(message);
      const configuredCalibration = /(?:^|\s)calibration=v2(?:\s|;|$)/.test(message) &&
        hasValidConfiguredLimits;
      // Older firmware may advertise a wider range than the confirmed six servos.
      const minimum = Math.max(600, configuredCalibration ? configuredMinimum : LegacyCalibrationMinimumUs);
      const maximum = Math.min(2300, configuredCalibration ? configuredMaximum : LegacyCalibrationMaximumUs);
      this.state.calibrationSupported = (legacyCalibration || configuredCalibration) && minimum <= maximum;
      this.state.calibrationMinimumUs = this.state.calibrationSupported ? minimum : null;
      this.state.calibrationMaximumUs = this.state.calibrationSupported ? maximum : null;
      this.state.lastCompleted = null;
      this.activePulse = null;
      this.state.error = "";
      this.state.errorKind = "";
      this.state.message = "Firmware připraven po startu · vyžaduje potvrzení Stop";
      this.changed();
    } else if (/^STOP PWM FULL_OFF\b/.test(message)) {
      const requested = this.state.pending === "stop";
      this.clearArm();
      this.clearPending();
      this.clearDance();
      this.activePulse = null;
      this.state.lastCompleted = null;
      this.state.baseline = requested && !this.state.fault;
      this.state.message = this.state.fault ? "PWM FULL_OFF potvrzeno · chyba vyžaduje kontrolu a restart" :
        "PWM FULL_OFF potvrzeno · Stop neodpojuje 5 V";
      if (requested && !this.state.fault) this.state.error = "";
      this.changed();
    } else if (/^ARMED for one pulse\b/.test(message) && this.state.pending === "arm") {
      this.clearPending();
      this.state.armedUntil = this.armDeadline;
      this.state.armDanceToken = message.match(/(?:^|\s)dance_token=([0-9a-f]{8})(?=\s|;|$)/)?.[1] ?? null;
      const remaining = this.armDeadline - this.now();
      if (remaining <= 0) {
        this.clearArm();
        this.state.message = "Příprava vypršela · znovu stiskněte Arm";
      } else {
        this.state.message = "Arm potvrzen · jeden pulz, platnost nejvýše 10 sekund";
        this.armTimer = this.setTimer(() => {
          this.clearArm();
          this.state.message = "Příprava vypršela · znovu stiskněte Arm";
          this.changed();
        }, remaining);
      }
      this.changed();
    } else if (/^DANCE READY\b/.test(message) && this.state.pending === 'dance-start') {
      const match = message.match(/^DANCE READY token=([0-9a-f]{8}) mask=([0-3][0-9a-f]) watchdog_ms=250$/);
      if (!match || match[1] !== this.requestedDance?.token || parseInt(match[2], 16) !== this.requestedDance.mask) return;
      this.state.danceSession = { ...this.requestedDance };
      this.requestedDance = null; this.clearPending();
      this.state.message = 'Plynulý tanec připraven · první cíl zatím nebyl odeslán.'; this.changed();
    } else if (/^DANCE APPLIED\b/.test(message) && this.state.pending === 'dance-target') {
      const match = message.match(/^DANCE APPLIED token=([0-9a-f]{8}) seq=(\d{5}) us=(\d{24})$/);
      if (!match || match[1] !== this.expectedTarget?.token || Number(match[2]) !== this.expectedTarget.sequence) return;
      const widths = match[3].match(/\d{4}/g).map(Number);
      if (!widths.every((width, channel) => this.state.danceSession.mask & (1 << channel) ? width >= 600 && width <= 2300 : width === 0)) {
        this.fail('Firmware oznámil PWM mimo potvrzené meze / vybrané kanály.'); return;
      }
      this.state.danceRttMs = Math.max(0, this.now() - this.expectedTarget.sentAt);
      this.state.danceSequence = this.expectedTarget.sequence;
      this.state.lastApplied = { token: match[1], sequence: this.expectedTarget.sequence, pulseWidthsUs: widths };
      this.expectedTarget = null; this.clearPending();
      this.state.message = 'Plynulý tanec · registry cíle potvrzeny; poloha neměřená.'; this.changed();
    } else if (/^DANCE (?:TIMEOUT|LIMIT)\b/.test(message)) {
      this.fail('Firmware ukončil tanec: watchdog / časový limit. Potvrďte nový Stop.');
    } else if (/^PULSE nominal \d+ ms\b/.test(message) && this.state.pending === "pulse") {
      this.state.message = `Firmware hlásí pulz ${this.activePulse?.durationMs ?? "—"} ms · čekám na PWM FULL_OFF`;
      this.changed();
    } else if (/^DONE PWM FULL_OFF\b/.test(message) && this.state.pending === "pulse") {
      this.clearArm();
      this.clearPending();
      this.state.lastCompleted = this.activePulse ? { ...this.activePulse } : null;
      this.activePulse = null;
      this.state.message = "PWM FULL_OFF po pulzu potvrzeno · pro další pulz stiskněte Arm";
      this.changed();
    }
  }
}

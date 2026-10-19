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
      message: "Neaktivní · odpojeno", error: "", errorKind: "", readback: null };
    this.generation = 0;
  }

  get capabilities() {
    const state = this.state;
    const idle = state.connected && state.baseline && !state.fault && !state.pending && !state.writing;
    return { arm: idle && !state.armedUntil,
      pulse: idle && state.armedUntil > this.now(),
      calibrate: idle && state.armedUntil > this.now() && state.calibrationSupported, stop: state.connected };
  }

  changed() { this.onChange(this.state, this.capabilities); }

  clearArm() {
    this.clearTimer(this.armTimer);
    this.state.armedUntil = 0;
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
    Object.assign(this.state, { connected: true, baseline: false, fault: false, writing: false,
      error: "", errorKind: "", readback: null, calibrationSupported: false,
      calibrationMinimumUs: null, calibrationMaximumUs: null, lastCompleted: null });
    return this.stop("!\n");
  }

  disconnected(message = "Odpojeno · stav PWM není potvrzen") {
    this.generation++;
    this.clearArm();
    this.clearPending();
    this.activePulse = null;
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

  stop(command = "!\n") {
    if (!this.state.connected) return false;
    this.clearArm();
    this.clearPending();
    this.state.baseline = false;
    this.activePulse = null;
    this.state.lastCompleted = null;
    return this.command(command, "stop", "Stop odesílán · čekám na PWM FULL_OFF…");
  }

  fail(message, fault = false, errorKind = "") {
    this.generation++;
    this.clearArm();
    this.clearPending();
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
      const configuredLimits = message.match(
        /(?:^|\s)calibration_min_us=(\d{1,4})\s+calibration_max_us=(\d{1,4})(?=\s|;|$)/);
      const configuredMinimum = configuredLimits ? Number(configuredLimits[1]) : null;
      const configuredMaximum = configuredLimits ? Number(configuredLimits[2]) : null;
      const hasValidConfiguredLimits = configuredMinimum > 0 &&
        configuredMinimum <= configuredMaximum && configuredMaximum <= 9999;
      const legacyCalibration = /(?:^|\s)calibration=v1(?:\s|;|$)/.test(message);
      const configuredCalibration = /(?:^|\s)calibration=v2(?:\s|;|$)/.test(message) &&
        hasValidConfiguredLimits;
      this.state.calibrationSupported = legacyCalibration || configuredCalibration;
      this.state.calibrationMinimumUs = configuredCalibration ? configuredMinimum :
        legacyCalibration ? LegacyCalibrationMinimumUs : null;
      this.state.calibrationMaximumUs = configuredCalibration ? configuredMaximum :
        legacyCalibration ? LegacyCalibrationMaximumUs : null;
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

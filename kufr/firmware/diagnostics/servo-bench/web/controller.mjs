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

export class BenchController {
  constructor({ write, onChange = () => {}, onLog = () => {}, now = Date.now,
    setTimer = (callback, delay) => setTimeout(callback, delay), clearTimer = id => clearTimeout(id) }) {
    Object.assign(this, { write, onChange, onLog, now, setTimer, clearTimer });
    this.state = { connected: false, baseline: false, fault: false, pending: null, writing: false,
      armedUntil: 0, message: "Neaktivní · odpojeno", error: "", readback: null };
    this.generation = 0;
  }

  get capabilities() {
    const state = this.state;
    const idle = state.connected && state.baseline && !state.fault && !state.pending && !state.writing;
    return { arm: idle && !state.armedUntil,
      pulse: idle && state.armedUntil > this.now(), stop: state.connected };
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
    Object.assign(this.state, { connected: true, baseline: false, fault: false, writing: false,
      error: "", readback: null });
    return this.stop("!\n");
  }

  disconnected(message = "Odpojeno · stav PWM není potvrzen") {
    this.generation++;
    this.clearArm();
    this.clearPending();
    Object.assign(this.state, { connected: false, baseline: false, writing: false, message });
    this.changed();
  }

  async command(command, pending, message) {
    const generation = ++this.generation;
    this.state.pending = pending;
    this.state.writing = true;
    this.state.message = message;
    this.state.error = "";
    // Start before write; a stalled transport must never leave Arm usable.
    this.ackTimer = this.setTimer(() => {
      if (generation !== this.generation) return;
      this.fail("Chybí potvrzení firmwaru. Stav PWM není známý; použijte Stop nebo odpojte napájení serv.");
    }, 3000);
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
    return this.command(`pulse ${channel} ${width}\n`, "pulse", "Čekám na pulz a potvrzení PWM FULL_OFF…");
  }

  stop(command = "!\n") {
    if (!this.state.connected) return false;
    this.clearArm();
    this.clearPending();
    this.state.baseline = false;
    return this.command(command, "stop", "Stop odesílán · čekám na PWM FULL_OFF…");
  }

  fail(message, fault = false) {
    this.generation++;
    this.clearArm();
    this.clearPending();
    this.state.baseline = false;
    this.state.writing = false;
    this.state.fault ||= fault;
    this.state.error = message;
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
      this.state.error = "";
      this.state.message = "Firmware připraven po startu · vyžaduje potvrzení Stop";
      this.changed();
    } else if (/^STOP PWM FULL_OFF\b/.test(message)) {
      const requested = this.state.pending === "stop";
      this.clearArm();
      this.clearPending();
      this.state.baseline = requested && !this.state.fault;
      this.state.message = this.state.fault ? "PWM FULL_OFF potvrzeno · chyba vyžaduje kontrolu a restart" :
        "PWM FULL_OFF potvrzeno · 5 V zůstává připojeno";
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
    } else if (/^PULSE nominal 500 ms\b/.test(message) && this.state.pending === "pulse") {
      this.state.message = "Firmware hlásí pulz · čekám na PWM FULL_OFF";
      this.changed();
    } else if (/^DONE PWM FULL_OFF\b/.test(message) && this.state.pending === "pulse") {
      this.clearArm();
      this.clearPending();
      this.state.message = "PWM FULL_OFF po pulzu potvrzeno · pro další pulz stiskněte Arm";
      this.changed();
    }
  }
}

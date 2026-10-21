// Local PCM analysis only. No upload, provider, or connection to USB.
export const MUSIC_FRAME_SECONDS = 0.02;
export const SERVO_MIN_US = 600;
export const SERVO_MAX_US = 2300;

export function analyzeMusic(buffer) {
  if (!Number.isFinite(buffer.duration) || buffer.duration <= 0 || buffer.duration > 600) {
    throw new Error("Vyberte zvuk dlouhý nejvýše 10 minut.");
  }
  const size = Math.max(1, Math.round(buffer.sampleRate * MUSIC_FRAME_SECONDS));
  const count = Math.ceil(buffer.length / size);
  const energy = new Float32Array(count);
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    const samples = buffer.getChannelData(channel);
    for (let frame = 0; frame < count; frame++) {
      const end = Math.min(samples.length, (frame + 1) * size);
      let sum = 0;
      for (let index = frame * size; index < end; index++) sum += samples[index] ** 2;
      energy[frame] += sum / Math.max(1, end - frame * size) / buffer.numberOfChannels;
    }
  }
  for (let frame = 0; frame < count; frame++) energy[frame] = Math.sqrt(energy[frame]);
  const sorted = Float32Array.from(energy).sort();
  const reference = sorted[Math.floor((count - 1) * 0.95)];
  const peak = sorted[count - 1];
  const onset = new Float32Array(count);
  for (let frame = 1; frame < count; frame++) onset[frame] = Math.max(0, energy[frame] - energy[frame - 1]);
  // Autocorrelation of positive energy changes estimates pulse, not musical meter.
  let bestScore = 0;
  let lag = 0;
  for (let candidate = 17; candidate <= 50; candidate++) {
    let score = 0;
    for (let frame = candidate; frame < count; frame++) score += onset[frame] * onset[frame - candidate];
    score /= Math.max(1, count - candidate);
    if (score > bestScore) { bestScore = score; lag = candidate; }
  }
  let offset = 0;
  if (lag) {
    let strongest = 0;
    for (let phase = 0; phase < lag; phase++) {
      let score = 0;
      for (let frame = phase; frame < count; frame += lag) score += onset[frame];
      if (score > strongest) { strongest = score; offset = phase * MUSIC_FRAME_SECONDS; }
    }
  }
  const scale = Math.max(reference, peak * 0.1, 0.00001);
  return { duration: buffer.duration, bpm: lag ? 60 / (lag * MUSIC_FRAME_SECONDS) : null,
    beatSeconds: lag ? lag * MUSIC_FRAME_SECONDS : 1, offset,
    energy: Array.from(energy, value => Math.min(1, value / scale)) };
}

export async function decodeMusic(file) {
  if (!file || file.size <= 0 || file.size > 32 * 1024 * 1024) throw new Error("Vyberte zvuk do 32 MiB.");
  const context = new OfflineAudioContext(2, 1, 16000);
  return analyzeMusic(await context.decodeAudioData(await file.arrayBuffer()));
}

export function musicCue(analysis, time, strength = 40) {
  // At least 600 ms between cues; firmware pulses last 500 ms. Missed cues are skipped.
  const stepSeconds = analysis.beatSeconds * Math.max(1, Math.ceil(0.6 / analysis.beatSeconds));
  const step = Math.floor((time - analysis.offset) / stepSeconds);
  const energy = analysis.energy[Math.min(analysis.energy.length - 1, Math.max(0, Math.floor(time / MUSIC_FRAME_SECONDS)))] ?? 0;
  const amplitude = 850 * Math.max(0, Math.min(100, strength)) / 100 * energy;
  return { step, energy, width: Math.max(SERVO_MIN_US, Math.min(SERVO_MAX_US,
    Math.round(1450 + (step % 2 === 0 ? 1 : -1) * amplitude))) };
}

// One Arm -> acknowledgement -> one 500 ms cal -> DONE. No motion retries or queue.
export class MusicDance {
  constructor({ audio, getState, arm, calibrate, stop, onChange = () => {},
    schedule = callback => setTimeout(callback, 25), cancel = id => clearTimeout(id) }) {
    Object.assign(this, { audio, getState, arm, calibrate, stopOutput: stop, onChange, schedule, cancel });
    this.state = { active: false, starting: false, dancing: false, message: "Vyberte místní skladbu.", time: 0, width: null };
    this.generation = 0;
    this.analysis = null;
    this.handlers = new Map();
    for (const event of ["pause", "ended", "error", "waiting", "stalled", "seeking", "ratechange"]) {
      const handler = () => {
        // Initial buffering is safe: no Arm is issued before play() resolves.
        if (this.state.starting && (event === "waiting" || event === "stalled")) return;
        if (this.state.active) this.pause(event === "ended" ? "Skladba dohrála." : "Přehrávání přerušeno; PWM Stop vyžádán.");
      };
      audio.addEventListener(event, handler);
      this.handlers.set(event, handler);
    }
  }

  changed() { this.onChange({ ...this.state }); }

  ready() {
    const state = this.getState();
    return state.connected && state.baseline && !state.fault && !state.error && state.calibrationSupported &&
      state.calibrationMinimumUs <= SERVO_MIN_US && state.calibrationMaximumUs >= SERVO_MAX_US &&
      !state.connecting && !state.closing && !state.automatic;
  }

  async play({ dance = false, channel = 0, strength = 40, consent = false } = {}) {
    if (this.state.active || this.state.starting || !this.analysis ||
      !Number.isInteger(channel) || channel < 0 || channel > 5 || !Number.isFinite(strength)) return false;
    if (dance && (!consent || !this.ready() || !this.getState().capabilities.arm)) {
      this.state.message = "Tanec vyžaduje připravené volné servo, potvrzený Stop a firmware s mezemi 600–2300 µs.";
      this.changed(); return false;
    }
    const generation = ++this.generation;
    this.channel = channel; this.strength = strength;
    this.stage = "idle"; this.lastStep = null;
    this.lastPulseTime = null;
    Object.assign(this.state, { active: true, starting: true, dancing: dance, width: null, message: "Spouštím zvuk…" });
    this.changed();
    try {
      this.audio.playbackRate = 1;
      await this.audio.play();
      if (generation !== this.generation) { if (!this.state.active) this.audio.pause(); return false; }
      this.state.starting = false;
      this.state.message = dance ? "Hudba + servo · časování podle zvuku, poloha neměřená." : "Pouze poslech · USB bez pohybu.";
      this.changed(); this.tick(generation); return true;
    } catch {
      if (generation === this.generation) this.pause("Zvuk se nepodařilo spustit. Zkuste Play znovu.");
      return false;
    }
  }

  tick(generation) {
    if (!this.state.active || generation !== this.generation) return;
    if (this.audio.paused || this.audio.ended || this.audio.seeking || this.audio.readyState < 3 || this.audio.playbackRate !== 1) {
      this.pause("Zvuk není připraven; tanec pozastaven."); return;
    }
    this.state.time = this.audio.currentTime;
    if (this.state.dancing) {
      const state = this.getState();
      if (!this.ready() || state.pending === "stop") { this.pause("USB stav se změnil; tanec zastaven."); return; }
      const cue = musicCue(this.analysis, this.audio.currentTime, this.strength);
      if (this.stage === "arming" && state.capabilities.calibrate) {
        // Recompute after Arm ACK; never replay a target from an earlier audio beat.
        this.lastStep = cue.step;
        this.lastPulseTime = this.audio.currentTime;
        this.state.width = cue.width;
        this.stage = "moving";
        void this.submit(() => this.calibrate(this.channel, cue.width, 500), generation);
      } else if (this.stage === "moving" && !state.pending && !state.writing) {
        const completed = state.lastCompleted;
        if (!completed?.calibration || completed.channel !== this.channel || completed.pulseWidthUs !== this.state.width) {
          this.pause("Chybí potvrzení dokončeného pulzu; tanec zastaven."); return;
        }
        this.stage = "idle";
      } else if (this.stage === "idle" && cue.step !== this.lastStep && state.capabilities.arm &&
        (this.lastPulseTime === null || this.audio.currentTime - this.lastPulseTime >= 0.6)) {
        this.lastStep = cue.step;
        if (cue.energy > 0.04) {
          this.stage = "arming";
          void this.submit(() => this.arm(), generation);
        }
      }
    }
    this.changed();
    if (this.state.active && generation === this.generation) this.timer = this.schedule(() => this.tick(generation));
  }

  async submit(command, generation) {
    try {
      if (!await command() && generation === this.generation) this.pause("Povel nebyl přijat; tanec zastaven.");
    } catch {
      if (generation === this.generation) this.pause("USB povel selhal; tanec zastaven.");
    }
  }

  pause(message = "Pauza · PWM Stop vyžádán. Play pokračuje od stejného místa.", sendStop = true) {
    const wasDancing = this.state.dancing && this.state.active;
    ++this.generation; this.cancel(this.timer);
    Object.assign(this.state, { active: false, starting: false, dancing: false, message });
    this.audio.pause();
    // Output confirmation stays in the bench controller; this message never claims it.
    if (wasDancing && sendStop && this.getState().connected && !this.getState().fault && this.getState().pending !== "stop") {
      void Promise.resolve(this.stopOutput()).catch(() => {});
    }
    this.changed();
  }

  stop(sendStop = true) {
    this.pause("Stop · skladba vrácena na začátek; stav PWM sledujte v USB diagnostice.", sendStop);
    this.audio.currentTime = 0; this.state.time = 0; this.state.width = null; this.changed();
  }

  dispose() {
    this.stop();
    for (const [event, handler] of this.handlers) this.audio.removeEventListener(event, handler);
  }
}

import { analyzeWaveform, sampleMotion, MUSIC_FEATURES, FRAME_SECONDS } from './waveform.mjs';
export { MUSIC_FEATURES, waveformPaths } from './waveform.mjs';
export const MUSIC_FRAME_SECONDS = FRAME_SECONDS;
export const SERVO_MIN_US = 600;
export const SERVO_MAX_US = 2300;
export const STREAM_INTERVAL_MS = 40;
export const analyzeMusic = analyzeWaveform;

export async function decodeMusic(file) {
  if (!file || file.size <= 0 || file.size > 32 * 1024 * 1024) throw new Error('Vyberte zvuk do 32 MiB.');
  const context = new OfflineAudioContext(2, 1, 16000);
  const buffer = await context.decodeAudioData(await file.arrayBuffer());
  await new Promise(resolve => setTimeout(resolve, 0));
  return analyzeWaveform(buffer);
}

export function musicCue(analysis, time, strength = 40, feature = 'phrase') {
  return sampleMotion(analysis, time, strength, feature);
}

// The media element supplies position; output timestamps estimate what is audible.
// Servo travel and USB jitter still require measured/manual lead compensation.
export class AudioOutputClock {
  constructor(audio) { this.audio = audio; this.context = null; }
  async resume() {
    if (typeof AudioContext === 'undefined') return;
    if (!this.context) {
      this.context = new AudioContext({ latencyHint: 'interactive' });
      this.source = this.context.createMediaElementSource(this.audio);
      this.source.connect(this.context.destination);
    }
    await this.context.resume();
  }
  time() {
    let latency = 0;
    if (this.context?.state === 'running') {
      const timestamp = this.context.getOutputTimestamp?.();
      if (timestamp?.performanceTime > 0) {
        const rendered = timestamp.contextTime + (performance.now() - timestamp.performanceTime) / 1000;
        latency = Math.max(0, Math.min(0.5, this.context.currentTime - rendered));
      } else latency = Math.max(0, Math.min(0.5, this.context.outputLatency || 0));
    }
    return Math.max(0, this.audio.currentTime - latency);
  }
  isRunning() { return !this.context || this.context.state === 'running'; }
  dispose() { void this.context?.close().catch(() => {}); }
}

// One Arm and session per Play; one atomic multi-servo frame until write AND ACK.
export class MusicDance {
  constructor({ audio, getState, arm, startDance, target, stop, onChange = () => {},
    schedule = callback => setTimeout(callback, 10), cancel = id => clearTimeout(id),
    now = () => performance.now(), clock = new AudioOutputClock(audio) }) {
    Object.assign(this, { audio, getState, arm, startDance, target, stopOutput: stop, onChange, schedule, cancel, now, clock });
    this.state = { active: false, starting: false, dancing: false, message: 'Vyberte místní skladbu.',
      time: 0, width: null, widths: [], appliedWidths: [], rttMs: 0, updateHz: 0, leadMs: 100 };
    this.generation = 0; this.analysis = null; this.handlers = new Map();
    for (const event of ['pause', 'ended', 'error', 'waiting', 'stalled', 'seeking', 'ratechange']) {
      const handler = () => {
        if (this.state.starting && (event === 'waiting' || event === 'stalled')) return;
        if (this.state.active) this.pause(event === 'ended' ? 'Skladba dohrála.' : 'Zvuk přerušen; všechny taneční kanály zastaveny.');
      };
      audio.addEventListener(event, handler); this.handlers.set(event, handler);
    }
  }
  changed(force = true) {
    const now = this.now();
    if (force || now - (this.lastUiTime ?? -Infinity) >= 40) {
      this.lastUiTime = now; this.onChange({ ...this.state });
    }
  }
  ready() {
    const state = this.getState();
    return state.connected && state.baseline && !state.fault && !state.error && state.danceSupported &&
      state.calibrationMinimumUs <= 600 && state.calibrationMaximumUs >= 2300 &&
      !state.connecting && !state.closing && !state.automatic;
  }
  async play({ dance = false, channels = [{ channel: 0, feature: 'phrase' }], strength = 40, leadMs = 100, consent = false } = {}) {
    const features = new Set(MUSIC_FEATURES.map(feature => feature.id));
    const validChannels = Array.isArray(channels) && channels.length >= 1 && channels.length <= 6 &&
      channels.every(value => value && Number.isInteger(value.channel) && value.channel >= 0 && value.channel <= 5 && features.has(value.feature)) &&
      new Set(channels.map(value => value.channel)).size === channels.length;
    if (this.state.active || this.state.starting || !this.analysis || !Number.isFinite(strength) || strength < 1 || strength > 100 ||
      !Number.isFinite(leadMs) || leadMs < 0 || leadMs > 300 || (dance && !validChannels)) return false;
    if (dance && (!consent || !this.ready() || !this.getState().capabilities.arm)) {
      this.state.message = 'Tanec vyžaduje připravená volná serva, potvrzený Stop a nový firmware s dance=v1.';
      this.changed(); return false;
    }
    const generation = ++this.generation;
    this.channels = dance ? channels.map(value => ({ ...value })) : []; this.strength = strength;
    this.mask = this.channels.reduce((mask, value) => mask | (1 << value.channel), 0);
    this.stage = 'idle'; this.expectedSequence = null; this.lastSent = -Infinity;
    this.lastTick = this.now(); this.progressAt = this.lastTick; this.lastAudioTime = this.audio.currentTime;
    this.startTime = this.lastTick; this.appliedCount = 0;
    Object.assign(this.state, { active: true, starting: true, dancing: dance, width: null, widths: [], appliedWidths: [],
      rttMs: 0, updateHz: 0, leadMs, message: 'Spouštím zvuk…' });
    this.changed();
    try {
      this.audio.playbackRate = 1;
      await this.clock.resume();
      if (generation !== this.generation) return false;
      await this.audio.play();
      if (generation !== this.generation) { if (!this.state.active) this.audio.pause(); return false; }
      this.lastTick = this.now(); this.progressAt = this.lastTick; this.playResolvedAt = this.lastTick; this.heardAudio = false;
      this.state.message = dance ? 'Plynulý tanec podle waveformu · každý kanál má vlastní zvukovou složku.' : 'Pouze poslech · USB bez pohybu.';
      this.changed(); this.tick(generation); return true;
    } catch {
      if (generation === this.generation) this.pause('Zvuk se nepodařilo spustit. Zkuste Play znovu.');
      return false;
    }
  }
  tick(generation) {
    if (!this.state.active || generation !== this.generation) return;
    const now = this.now(); const gap = now - this.lastTick; this.lastTick = now;
    if (this.clock.isRunning?.() === false) {
      this.pause('Výstup zvuku byl přerušen; pro nový tanec znovu použijte Play.'); return;
    }
    if (this.audio.currentTime > this.lastAudioTime + 0.001) {
      this.progressAt = now; this.lastAudioTime = this.audio.currentTime; this.heardAudio = true; this.state.starting = false;
    }
    if ((this.heardAudio && (gap > 150 || now - this.progressAt > 150)) || this.audio.paused || this.audio.ended || this.audio.seeking || this.audio.readyState < 3 || this.audio.playbackRate !== 1) {
      this.pause('Zvuk nebo prohlížeč se zpozdil; tanec zastaven bez dohánění kroků.'); return;
    }
    this.state.time = this.clock.time();
    // Some audio devices preroll after play() resolves. Never Arm during that gap.
    if (!this.heardAudio) {
      if (now - this.playResolvedAt >= 1000) { this.pause('Výstup zvuku nezačal; žádný taneční povel nebyl odeslán.'); return; }
      this.timer = this.schedule(() => this.tick(generation)); return;
    }
    if (this.state.dancing) {
      const state = this.getState();
      if (!this.ready() || state.pending === 'stop') { this.pause('USB stav se změnil; všechny taneční kanály zastaveny.'); return; }
      if (this.stage === 'idle') {
        this.stage = 'arming'; void this.submit(() => this.arm(), generation);
      } else if (this.stage === 'arming' && state.capabilities.danceStart) {
        this.stage = 'starting-session'; void this.submit(() => this.startDance(this.mask), generation);
      } else if (this.stage === 'starting-session' && state.capabilities.danceTarget) {
        this.stage = 'streaming';
      }
      if (this.stage === 'streaming') {
        if (!state.danceSession || state.danceSession.mask !== this.mask) { this.pause('Taneční session se změnila.'); return; }
        if (!state.pending && !state.writing) {
          if (this.expectedSequence !== null) {
            if (state.lastApplied?.sequence !== this.expectedSequence || state.lastApplied.token !== state.danceSession.token) {
              this.pause('Chybí odpovídající potvrzení tanečního snímku.'); return;
            }
            if (state.danceRttMs > 150) { this.pause('USB potvrzení přišlo příliš pozdě.'); return; }
            this.state.rttMs = this.appliedCount ? this.state.rttMs * 0.8 + state.danceRttMs * 0.2 : state.danceRttMs;
            this.state.appliedWidths = [...state.lastApplied.pulseWidthsUs]; this.appliedCount++;
            this.state.updateHz = this.appliedCount / Math.max(0.001, (now - this.startTime) / 1000);
            this.expectedSequence = null;
          }
          if (now - this.lastSent >= STREAM_INTERVAL_MS && state.capabilities.danceTarget) {
            // Compensate estimated transport delay and adjustable mechanical lead.
            // Sample the newest audio position: never queue historical frames.
            const time = Math.min(this.analysis.duration, this.state.time + (this.state.leadMs + this.state.rttMs / 2) / 1000);
            const widths = Array(6).fill(0);
            for (const channel of this.channels) widths[channel.channel] = musicCue(this.analysis, time, this.strength, channel.feature).width;
            this.state.widths = widths; this.state.width = widths[this.channels[0].channel];
            this.expectedSequence = state.danceSequence + 1; this.lastSent = now;
            void this.submit(() => this.target(widths), generation);
          }
        }
      }
    }
    this.changed(false);
    if (this.state.active && generation === this.generation) this.timer = this.schedule(() => this.tick(generation));
  }
  async submit(command, generation) {
    try { if (!await command() && generation === this.generation) this.pause('Povel nebyl přijat; tanec zastaven.'); }
    catch { if (generation === this.generation) this.pause('USB povel selhal; tanec zastaven.'); }
  }
  pause(message = 'Pauza · PWM Stop vyžádán pro všechny vybrané kanály.', sendStop = true) {
    const wasDancing = this.state.dancing && this.state.active;
    ++this.generation; this.cancel(this.timer);
    Object.assign(this.state, { active: false, starting: false, dancing: false, message });
    this.audio.pause();
    if (wasDancing && sendStop && this.getState().connected && !this.getState().fault && this.getState().pending !== 'stop') {
      void Promise.resolve(this.stopOutput()).catch(() => {});
    }
    this.changed();
  }
  stop(sendStop = true) {
    this.pause('Stop · skladba na začátku; všechny taneční kanály zastaveny.', sendStop);
    this.audio.currentTime = 0; this.state.time = 0; this.state.width = null; this.changed();
  }
  dispose() {
    this.stop(); this.clock.dispose?.();
    for (const [event, handler] of this.handlers) this.audio.removeEventListener(event, handler);
  }
}

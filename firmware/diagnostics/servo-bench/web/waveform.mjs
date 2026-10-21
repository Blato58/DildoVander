// Offline, channel-preserving spectral analysis. No raw audio is sent to USB.
export const FRAME_SECONDS = 0.01;
export const MUSIC_FEATURES = Object.freeze([
  { id: 'bass', label: 'Basy · důrazy a pružné pohyby' },
  { id: 'mid', label: 'Středy · plynulé kontury' },
  { id: 'treble', label: 'Výšky · drobné švihy' },
  { id: 'onsets', label: 'Akcenty · nástupy zvuku' },
  { id: 'energy', label: 'Dynamika · celková energie' },
  { id: 'phrase', label: 'Fráze · pomalá změna barvy' },
]);
const FFT_SIZE = 512;
const clamp = (value, minimum = 0, maximum = 1) => Math.max(minimum, Math.min(maximum, value));

function fftPlan() {
  const reverse = new Uint16Array(FFT_SIZE);
  const window = new Float32Array(FFT_SIZE);
  const cosine = new Float32Array(FFT_SIZE / 2);
  const sine = new Float32Array(FFT_SIZE / 2);
  for (let index = 0; index < FFT_SIZE; index++) {
    let value = index; let reversed = 0;
    for (let bit = 0; bit < 9; bit++) { reversed = (reversed << 1) | (value & 1); value >>= 1; }
    reverse[index] = reversed;
    window[index] = 0.5 - 0.5 * Math.cos(2 * Math.PI * index / (FFT_SIZE - 1));
    if (index < FFT_SIZE / 2) {
      cosine[index] = Math.cos(-2 * Math.PI * index / FFT_SIZE);
      sine[index] = Math.sin(-2 * Math.PI * index / FFT_SIZE);
    }
  }
  return { reverse, window, cosine, sine };
}

function transform(real, imaginary, plan) {
  for (let length = 2; length <= FFT_SIZE; length *= 2) {
    const half = length / 2; const stride = FFT_SIZE / length;
    for (let start = 0; start < FFT_SIZE; start += length) {
      for (let index = 0; index < half; index++) {
        const even = start + index; const odd = even + half; const twiddle = index * stride;
        const re = real[odd] * plan.cosine[twiddle] - imaginary[odd] * plan.sine[twiddle];
        const im = real[odd] * plan.sine[twiddle] + imaginary[odd] * plan.cosine[twiddle];
        real[odd] = real[even] - re; imaginary[odd] = imaginary[even] - im;
        real[even] += re; imaginary[even] += im;
      }
    }
  }
}

function normalize(values, floor = 0.000001) {
  const sorted = Float32Array.from(values).sort();
  const peak = sorted.at(-1) || 0;
  const scale = Math.max(sorted[Math.floor((sorted.length - 1) * 0.95)] || 0, peak * 0.12, floor);
  return Array.from(values, value => clamp(value / scale));
}

function estimateTempo(onsets) {
  let best = 0; let lag = 0;
  for (let candidate = 34; candidate <= 100; candidate++) {
    let product = 0; let first = 0; let second = 0;
    for (let index = candidate; index < onsets.length; index++) {
      product += onsets[index] * onsets[index - candidate];
      first += onsets[index] ** 2; second += onsets[index - candidate] ** 2;
    }
    const score = product / Math.max(0.000001, Math.sqrt(first * second));
    if (score > best) { best = score; lag = candidate; }
  }
  return { bpm: lag && best > 0.05 ? 60 / (lag * FRAME_SECONDS) : null,
    beatSeconds: lag ? lag * FRAME_SECONDS : 1, confidence: best };
}

export function analyzeWaveform(buffer) {
  if (!Number.isFinite(buffer.duration) || buffer.duration <= 0 || buffer.duration > 600 ||
    !Number.isFinite(buffer.sampleRate) || buffer.sampleRate < 1000 ||
    !Number.isInteger(buffer.numberOfChannels) || buffer.numberOfChannels < 1 || buffer.numberOfChannels > 8) {
    throw new Error('Vyberte zvuk dlouhý nejvýše 10 minut.');
  }
  const count = Math.ceil(buffer.duration / FRAME_SECONDS);
  const energy = new Float32Array(count); const peaks = new Float32Array(count);
  const bass = new Float32Array(count); const mid = new Float32Array(count); const treble = new Float32Array(count);
  const flux = new Float32Array(count);
  const plan = fftPlan(); const real = new Float32Array(FFT_SIZE); const imaginary = new Float32Array(FFT_SIZE);
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    const samples = buffer.getChannelData(channel);
    const previous = new Float32Array(FFT_SIZE / 2);
    for (let frame = 0; frame < count; frame++) {
      const center = Math.round(frame * FRAME_SECONDS * buffer.sampleRate);
      let sum = 0; let peak = 0;
      for (let index = 0; index < FFT_SIZE; index++) {
        const sample = samples[center + index - FFT_SIZE / 2] || 0;
        sum += sample * sample; peak = Math.max(peak, Math.abs(sample));
        real[plan.reverse[index]] = sample * plan.window[index]; imaginary[plan.reverse[index]] = 0;
      }
      energy[frame] += sum / FFT_SIZE / buffer.numberOfChannels;
      peaks[frame] = Math.max(peaks[frame], peak);
      transform(real, imaginary, plan);
      for (let bin = 1; bin < FFT_SIZE / 2; bin++) {
        const frequency = bin * buffer.sampleRate / FFT_SIZE;
        const power = (real[bin] ** 2 + imaginary[bin] ** 2) / buffer.numberOfChannels;
        if (frequency >= 40 && frequency < 220) bass[frame] += power;
        else if (frequency >= 220 && frequency < 2000) mid[frame] += power;
        else if (frequency >= 2000 && frequency <= 7500) treble[frame] += power;
        const magnitude = Math.sqrt(power);
        flux[frame] += Math.max(0, magnitude - previous[bin]); previous[bin] = magnitude;
      }
    }
  }
  for (let frame = 0; frame < count; frame++) energy[frame] = Math.sqrt(energy[frame]);
  const rawBandPower = { bass: bass.reduce((sum, value) => sum + value, 0),
    mid: mid.reduce((sum, value) => sum + value, 0), treble: treble.reduce((sum, value) => sum + value, 0) };
  const spectralFloor = Math.max(...[bass, mid, treble].map(values => values.reduce((peak, value) => Math.max(peak, value), 0))) * 0.0001;
  const features = { energy: normalize(energy), peaks: normalize(peaks), bass: normalize(bass, spectralFloor),
    mid: normalize(mid, spectralFloor), treble: normalize(treble, spectralFloor), onsets: normalize(flux) };
  const tempo = estimateTempo(features.onsets);
  const motions = Object.fromEntries(MUSIC_FEATURES.map(feature => [feature.id, new Array(count)]));
  const poses = Object.fromEntries(MUSIC_FEATURES.map(feature => [feature.id, { pose: 0, velocity: 0 }]));
  let slowEnergy = 0; let slowTone = 0; let slowBass = 0; let slowMid = 0;
  let kick = 0; let flick = 0;
  let lastAccent = -100; let lastDirection = 1; let accentCount = 0;
  for (let frame = 0; frame < count; frame++) {
    const level = features.energy[frame];
    const tone = (features.mid[frame] + 0.35 * features.treble[frame] - 1.1 * features.bass[frame]) / 1.35;
    slowEnergy += (level - slowEnergy) * 0.018;
    slowTone += (tone - slowTone) * 0.025;
    slowBass += (features.bass[frame] - slowBass) * 0.04;
    slowMid += (features.mid[frame] - slowMid) * 0.035;
    const transient = features.onsets[frame];
    // Accents choose their direction from spectral balance and recent phrase energy.
    // Repeated bass hits can repeat the same gesture; no beat-parity oscillator.
    if (transient > 0.32 && level > 0.05 && frame - lastAccent >= 12) {
      let direction = tone > slowTone + 0.05 ? 1 : tone < slowTone - 0.05 ? -1 :
        level > slowEnergy + 0.08 ? 1 : -1;
      if (frame - lastAccent > 100) direction = lastDirection;
      kick = direction * transient * (0.3 + 0.3 * features.bass[frame]);
      flick = (features.treble[frame] - features.bass[frame]) * transient * 0.3;
      lastAccent = frame; lastDirection = direction; accentCount++;
    }
    kick *= 0.935; flick *= 0.84;
    const targets = {
      bass: 1.15 * (features.bass[frame] - slowBass) - 0.35 * slowBass,
      mid: 0.85 * (features.mid[frame] - slowMid) + 0.45 * (features.mid[frame] - features.bass[frame]),
      treble: 0.7 * (features.treble[frame] - features.bass[frame]) * transient + flick,
      onsets: kick + flick,
      energy: 1.2 * (level - slowEnergy) + 0.5 * (slowEnergy - 0.5),
      phrase: 0.7 * slowTone + 0.3 * (slowEnergy - 0.5),
    };
    // Each assigned feature has independent damping and holds, never a beat-parity oscillator.
    for (const feature of MUSIC_FEATURES) {
      const target = level < 0.025 ? 0 : clamp(targets[feature.id], -1, 1);
      const state = poses[feature.id];
      state.velocity += (target - state.pose) * 0.1 - state.velocity * 0.5;
      state.velocity = clamp(state.velocity, -0.047, 0.047); // <=3995 us/s at full range.
      state.pose = clamp(state.pose + state.velocity, -1, 1);
      motions[feature.id][frame] = state.pose;
    }
  }
  for (const feature of MUSIC_FEATURES) {
    const values = motions[feature.id];
    const magnitudes = Float32Array.from(values, Math.abs).sort();
    const gain = Math.min(4, 0.85 / Math.max(0.08, magnitudes[Math.floor((count - 1) * 0.95)]));
    let previous = 0;
    for (let frame = 0; frame < count; frame++) {
      const target = clamp(values[frame] * gain, -1, 1);
      previous += clamp(target - previous, -0.047, 0.047);
      values[frame] = previous;
    }
  }
  return { duration: buffer.duration, frameSeconds: FRAME_SECONDS, ...tempo, offset: 0,
    ...features, motions, motion: motions.phrase, accentCount, rawBandPower };
}

export function sampleMotion(analysis, time, strength = 40, feature = 'phrase') {
  const frameSeconds = analysis.frameSeconds || 0.02;
  const index = clamp(time / frameSeconds, 0, analysis.energy.length - 1);
  const lower = Math.floor(index); const blend = index - lower;
  const interpolate = values => (values?.[lower] || 0) * (1 - blend) + (values?.[lower + 1] ?? values?.[lower] ?? 0) * blend;
  return { energy: interpolate(analysis.energy), width: Math.round(1450 + 850 * clamp(strength / 100) * interpolate(analysis.motions?.[feature] || analysis.motion)) };
}

export function waveformPaths(analysis, strength = 40, feature = 'phrase') {
  if (!analysis) return { waveform: '', motion: '' };
  const samples = Math.min(900, analysis.energy.length); let top = ''; let bottom = ''; let motion = '';
  for (let point = 0; point < samples; point++) {
    const time = point / Math.max(1, samples - 1) * analysis.duration;
    const frame = Math.min(analysis.energy.length - 1, Math.floor(time / (analysis.frameSeconds || 0.02)));
    const x = (point / Math.max(1, samples - 1) * 1000).toFixed(1);
    const amplitude = (analysis.peaks?.[frame] ?? analysis.energy[frame]) * 36;
    top += `${point ? 'L' : 'M'}${x},${(43 - amplitude).toFixed(1)} `;
    bottom = `L${x},${(43 + amplitude).toFixed(1)} ` + bottom;
    motion += `${point ? 'L' : 'M'}${x},${(140 - (sampleMotion(analysis, time, strength, feature).width - 1450) / 850 * 35).toFixed(1)} `;
  }
  return { waveform: top + bottom + 'Z', motion };
}

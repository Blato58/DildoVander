import test from 'node:test';
import assert from 'node:assert/strict';
import { analyzeWaveform, sampleMotion, waveformPaths, MUSIC_FEATURES } from '../waveform.mjs';

function buffer(seconds, sample, channels = 1) {
  const sampleRate = 16000; const length = Math.round(sampleRate * seconds);
  const data = Array.from({ length: channels }, (_, channel) => Float32Array.from({ length }, (_, index) => sample(index / sampleRate, channel)));
  return { duration: seconds, sampleRate, length, numberOfChannels: channels, getChannelData: channel => data[channel] };
}

test('FFT identifies bass, midrange and treble and does not amplify leakage into another band', () => {
  for (const [frequency, expected] of [[100, 'bass'], [1000, 'mid'], [4000, 'treble']]) {
    const analysis = analyzeWaveform(buffer(1, time => Math.sin(time * 2 * Math.PI * frequency) * 0.5));
    const powers = analysis.rawBandPower;
    const total = Object.values(powers).reduce((sum, value) => sum + value, 0);
    assert(powers[expected] / total > 0.98);
    for (const band of ['bass', 'mid', 'treble']) if (band !== expected) assert(analysis[band][50] < 0.05);
  }
});

test('anti-phase stereo is analyzed per channel and never cancels the waveform', () => {
  const signal = (time, channel = 0) => Math.sin(time * 2 * Math.PI * 1000) * 0.6 * (channel ? -1 : 1);
  const mono = analyzeWaveform(buffer(1, signal)); const stereo = analyzeWaveform(buffer(1, signal, 2));
  assert(Math.abs(stereo.rawBandPower.mid / mono.rawBandPower.mid - 1) < 0.00001);
  assert(Math.abs(stereo.energy[50] - mono.energy[50]) < 0.00001);
});

test('silence rests, dynamics generate varied feature-specific motion, and trajectories obey range/slew', () => {
  const silent = analyzeWaveform(buffer(1, () => 0));
  for (const feature of MUSIC_FEATURES) assert(silent.motions[feature.id].every(value => value === 0));
  const analysis = analyzeWaveform(buffer(6, time => {
    const frequency = time < 2 ? 100 : time < 4 ? 1000 : 4000;
    return Math.sin(time * 2 * Math.PI * frequency) * (time % 0.5 < 0.08 ? 0.8 : 0.05);
  }));
  assert(analysis.accentCount >= 8);
  assert.notDeepEqual(analysis.motions.bass, analysis.motions.mid);
  assert.notDeepEqual(analysis.motions.mid, analysis.motions.treble);
  assert.notDeepEqual(analysis.motions.onsets, analysis.motions.energy);
  for (const feature of MUSIC_FEATURES) {
    let previous = 1450;
    for (let frame = 0; frame < analysis.energy.length; frame++) {
      const width = sampleMotion(analysis, frame * 0.01, 100, feature.id).width;
      assert(width >= 600 && width <= 2300); assert(Math.abs(width - previous) <= 41); previous = width;
    }
  }
  const paths = waveformPaths(analysis, 40, 'bass');
  assert(paths.waveform.startsWith('M') && paths.waveform.endsWith('Z'));
  assert(paths.motion.startsWith('M')); assert(!paths.motion.includes('NaN'));
});

test('time interpolation and clipping sample the requested feature at the actual audio position', () => {
  const analysis = { duration: 0.02, frameSeconds: 0.01, energy: [0, 1, 0], motions: { bass: [-1, 0, 1], mid: [1, 0, -1] } };
  assert.equal(sampleMotion(analysis, 0.005, 100, 'bass').width, 1025);
  assert.equal(sampleMotion(analysis, 0.005, 100, 'mid').width, 1875);
  assert.equal(sampleMotion(analysis, -10, 100, 'bass').width, 600);
  assert.equal(sampleMotion(analysis, 100, 100, 'bass').width, 2300);
});

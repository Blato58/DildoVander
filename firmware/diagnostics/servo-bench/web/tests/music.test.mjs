import assert from "node:assert/strict";
import test from "node:test";
import { analyzeMusic, musicCue, MusicDance } from "../music.mjs";
import { BenchController } from "../controller.mjs";

const analysis = { duration: 30, bpm: 120, beatSeconds: 0.5, offset: 0, energy: Array(1500).fill(1) };
const flush = async () => { await Promise.resolve(); await Promise.resolve(); };
class Audio extends EventTarget {
  paused = true; currentTime = 0; readyState = 4; ended = false; seeking = false; playbackRate = 1;
  async play() { this.paused = false; }
  pause() { if (!this.paused) { this.paused = true; this.dispatchEvent(new Event("pause")); } }
}
async function fixture(writeOverride) {
  const writes = [];
  const controller = new BenchController({ write: async (...args) => { writes.push(args[0]); await writeOverride?.(...args); },
    setTimer: () => 1, clearTimer: () => {} });
  await controller.connected();
  controller.receive("READY PWM FULL_OFF calibration=v2 calibration_min_us=600 calibration_max_us=2300");
  controller.receive("STOP PWM FULL_OFF"); writes.length = 0;
  const audio = new Audio();
  const music = new MusicDance({ audio, getState: () => ({ ...controller.state, capabilities: controller.capabilities }),
    arm: () => controller.arm(), calibrate: (...args) => controller.calibrate(...args), stop: () => controller.stop(),
    schedule: () => 1, cancel: () => {} });
  music.analysis = analysis;
  const tick = async (time = audio.currentTime) => { audio.currentTime = time; music.tick(music.generation); await flush(); };
  return { writes, controller, music, audio, tick };
}

test("PCM energy/onset analysis estimates a 120 BPM pulse and silent input stays still", () => {
  const sampleRate = 1000;
  const samples = new Float32Array(20000);
  for (let start = 0; start < samples.length; start += 500) samples.fill(0.8, start, start + 40);
  const buffer = { duration: 20, sampleRate, length: samples.length, numberOfChannels: 1, getChannelData: () => samples };
  const result = analyzeMusic(buffer);
  assert.equal(result.bpm, 120); assert.equal(result.duration, 20);
  assert(result.energy.some(value => value > 0.9));
  samples.fill(0);
  const silent = analyzeMusic(buffer);
  assert.equal(silent.bpm, null); assert.equal(musicCue(silent, 2, 100).energy, 0);
  assert.throws(() => analyzeMusic({ ...buffer, duration: 601 }));
});

test("cues alternate within all confirmed endpoints and never need a sub-600ms interval", () => {
  assert.equal(musicCue(analysis, 0, 100).width, 2300);
  assert.equal(musicCue(analysis, 1, 100).width, 600);
  assert.equal(musicCue(analysis, 0.5, 100).step, 0);
  assert.equal(musicCue(analysis, 0, 40).width, 1790);
  for (const strength of [-1, 0, 50, 100, 200]) for (let time = 0; time < 30; time += 0.13) {
    const cue = musicCue(analysis, time, strength);
    assert(cue.width >= 600 && cue.width <= 2300);
  }
});

test("loading/preview sends no USB; dance needs explicit consent, channel and firmware limits", async () => {
  const { music, controller, writes } = await fixture();
  assert.equal(await music.play({ dance: true }), false);
  assert.equal(await music.play({ dance: true, consent: true, channel: 6 }), false);
  controller.state.calibrationMinimumUs = 1000;
  assert.equal(await music.play({ dance: true, consent: true }), false);
  assert.equal(await music.play(), true); assert.deepEqual(writes, []);
  music.pause(); assert.deepEqual(writes, []);
});

test("dance waits for Arm and matching DONE, skips missed cues, and pause invalidates late ACK", async () => {
  const { music, audio, controller, writes, tick } = await fixture();
  assert.equal(await music.play({ dance: true, consent: true, channel: 3 }), true);
  assert.deepEqual(writes, ["arm\n"]);
  await tick(0.2); assert.equal(writes.length, 1);
  controller.receive("ARMED for one pulse"); await tick(0.2);
  assert.deepEqual(writes, ["arm\n", "cal 3 1790 0500\n"]);
  await tick(4.5); assert.equal(writes.length, 2); // No DONE, no next target.
  controller.receive("DONE PWM FULL_OFF"); await tick(4.5); await tick(4.5);
  assert.equal(writes.at(-1), "arm\n");
  controller.receive("ARMED for one pulse"); await tick(5.2);
  assert.equal(writes.at(-1), "cal 3 1110 0500\n"); // Current cue, not queued beat 1.
  music.pause(); assert.equal(audio.currentTime, 5.2); assert.equal(audio.paused, true);
  assert.equal(writes.at(-1), "!\n");
  controller.receive("ARMED for one pulse"); controller.receive("DONE PWM FULL_OFF"); await tick(6);
  assert.equal(writes.length, 5);
  music.stop(); assert.equal(audio.currentTime, 0);
});

test("early firmware ACKs cannot outrun an unresolved transport write", async () => {
  let finish;
  const { music, controller, writes, tick } = await fixture(text => text === "arm\n" ? new Promise(resolve => { finish = resolve; }) : undefined);
  await music.play({ dance: true, consent: true });
  controller.receive("ARMED for one pulse"); await tick(0.1);
  assert.equal(writes.length, 1);
  finish(); await flush(); await tick(0.2);
  assert.equal(writes.at(-1), "cal 0 1790 0500\n"); music.stop();
});

test("delayed Arm never compresses the next pulse below the 600ms cadence", async () => {
  const { music, controller, writes, tick } = await fixture();
  await music.play({ dance: true, consent: true });
  await flush();
  controller.receive("ARMED for one pulse"); await tick(0.9);
  controller.receive("DONE PWM FULL_OFF"); await tick(1.4); await tick(1.4);
  assert.equal(writes.length, 2);
  await tick(1.51); assert.equal(writes.at(-1), "arm\n"); music.stop();
});

test("media interruption, USB disconnect/reset/fault, rejected and missing ACK never resume motion", async () => {
  for (const event of ["waiting", "stalled", "error", "seeking", "ended", "pause", "ratechange"]) {
    const { music, audio, controller, writes, tick } = await fixture();
    await music.play({ dance: true, consent: true });
    audio.dispatchEvent(new Event(event));
    assert.equal(music.state.active, false); assert.equal(writes.at(-1), "!\n");
    controller.receive("ARMED for one pulse"); await tick(2); assert.equal(writes.length, 2);
  }
  for (const change of [controller => controller.disconnected(), controller => controller.fail("timeout"),
    controller => controller.fail("fault", true), controller => controller.receive("REJECT cal"),
    controller => controller.receive("READY PWM FULL_OFF calibration=v2 calibration_min_us=600 calibration_max_us=2300")]) {
    const { music, controller, writes, tick } = await fixture();
    await music.play({ dance: true, consent: true }); change(controller); await tick(1);
    assert.equal(music.state.active, false);
    assert.equal(writes.filter(value => value.startsWith("cal ")).length, 0);
  }
});

test("cancel during blocked media Play cannot later start USB; playback failure stays stopped", async () => {
  const { music, audio, writes } = await fixture();
  let finish;
  audio.play = () => new Promise(resolve => { finish = resolve; });
  const play = music.play({ dance: true, consent: true });
  music.stop(); finish(); await play;
  assert.equal(music.state.active, false); assert.equal(writes.filter(value => value === "arm\n").length, 0);
  audio.play = async () => { throw Error("blocked"); };
  assert.equal(await music.play(), false); assert.equal(music.state.active, false);
});

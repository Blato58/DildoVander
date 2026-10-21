import assert from 'node:assert/strict';
import test from 'node:test';
import { MusicDance, AudioOutputClock } from '../music.mjs';
import { BenchController } from '../controller.mjs';
const flush = async () => { for (let index = 0; index < 5; index++) await Promise.resolve(); };
const analysis = { duration: 4, frameSeconds: 0.01, energy: Array(401).fill(1), motions: {
  bass: Array.from({ length: 401 }, (_, index) => Math.sin(index * 0.1) * 0.8),
  mid: Array.from({ length: 401 }, (_, index) => Math.cos(index * 0.05) * 0.5), phrase: Array(401).fill(0.2),
} };
class Audio extends EventTarget {
  paused = true; currentTime = 0; readyState = 4; ended = false; seeking = false; playbackRate = 1;
  async play() { this.paused = false; }
  pause() { if (!this.paused) { this.paused = true; this.dispatchEvent(new Event('pause')); } }
}
async function fixture(writeOverride) {
  let time = 0; let nextTimer = 0; const timers = new Map(); const writes = [];
  const controller = new BenchController({ write: async (...args) => { writes.push(args[0]); await writeOverride?.(...args); },
    now: () => time, setTimer: (callback, delay) => { const id = ++nextTimer; timers.set(id, { callback, at: time + delay }); return id; }, clearTimer: id => timers.delete(id) });
  const audio = new Audio();
  const music = new MusicDance({ audio, getState: () => ({ ...controller.state, capabilities: controller.capabilities }),
    arm: () => controller.arm(), startDance: mask => controller.startDance(mask), target: widths => controller.target(widths), stop: () => controller.stop(),
    now: () => time, clock: { resume: async () => {}, time: () => audio.currentTime }, schedule: () => 1, cancel: () => {} });
  music.analysis = analysis;
  await controller.connected();
  controller.receive('READY PWM FULL_OFF calibration=v2 calibration_min_us=600 calibration_max_us=2300 dance=v1 dance_boot=1234abcd');
  controller.receive('STOP PWM FULL_OFF'); writes.length = 0;
  const tick = async seconds => {
    time = seconds * 1000; audio.currentTime = seconds;
    for (const [id, timer] of [...timers]) if (timer.at <= time) { timers.delete(id); timer.callback(); }
    music.tick(music.generation); await flush();
  };
  const ack = () => {
    const match = writes.at(-1)?.match(/^target (\w{8}) (\d{5}) (\d{24})\n$/);
    assert(match); controller.receive(`DANCE APPLIED token=${match[1]} seq=${match[2]} us=${match[3]}`);
  };
  const start = async (channels = [{ channel: 0, feature: 'bass' }, { channel: 2, feature: 'mid' }]) => {
    assert.equal(await music.play({ dance: true, consent: true, channels, leadMs: 0 }), true); await flush();
    await tick(0.005);
    controller.receive('ARMED for one pulse; dance_token=00000001'); await tick(0.01);
    const mask = channels.reduce((value, channel) => value | (1 << channel.channel), 0).toString(16).padStart(2, '0');
    controller.receive(`DANCE READY token=00000001 mask=${mask} watchdog_ms=250`); await tick(0.02);
  };
  return { music, audio, controller, writes, tick, ack, start };
}

test('audio preview has no USB writes; streaming requires feature assignments, consent and new firmware', async () => {
  const { music, controller, writes } = await fixture();
  assert.equal(await music.play({ dance: true }), false);
  assert.equal(await music.play({ dance: true, consent: true, channels: [] }), false);
  assert.equal(await music.play({ dance: true, consent: true, channels: [null] }), false);
  assert.equal(await music.play({ dance: true, consent: true, channels: [{ channel: 6, feature: 'bass' }] }), false);
  assert.equal(await music.play({ dance: true, consent: true, channels: [{ channel: 0, feature: 'bass' }, { channel: 0, feature: 'mid' }] }), false);
  assert.equal(await music.play({ dance: true, consent: true, channels: [{ channel: 0, feature: 'unknown' }] }), false);
  controller.state.danceSupported = false;
  assert.equal(await music.play({ dance: true, consent: true }), false);
  assert.equal(await music.play(), true); assert.deepEqual(writes, []); music.stop(); assert.deepEqual(writes, []);
});

test('Arm and session ACK gate independent multi-channel targets with no 500ms duration', async () => {
  const { music, controller, writes, start } = await fixture(); await start();
  assert.equal(writes[0], 'arm\n'); assert.equal(writes[1], 'dance 05 1234abcd 00000001\n');
  const match = writes[2].match(/^target 00000001 00001 (\d{24})\n$/); assert(match);
  const widths = match[1].match(/\d{4}/g).map(Number);
  assert(widths[0] >= 600 && widths[0] <= 2300); assert(widths[2] >= 600 && widths[2] <= 2300);
  assert.notEqual(widths[0], widths[2]); assert.deepEqual([widths[1], widths[3], widths[4], widths[5]], [0, 0, 0, 0]);
  assert.equal(controller.arm(), false); assert.equal(controller.calibrate(0, 1500, 500), false);
  assert(writes.every(value => !value.startsWith('cal '))); music.stop();
});

test('matching ACK and write gate each40ms frame; late scheduling samples current audio and never catches up', async () => {
  const { music, controller, writes, start, ack, tick } = await fixture(); await start();
  await tick(0.03); assert.equal(writes.length, 3);
  ack(); await tick(0.04); assert.equal(writes.length, 3);
  await tick(0.06); assert.equal(writes.length, 4);
  assert(writes.at(-1).includes(' 00002 '));
  controller.receive('DANCE APPLIED token=ffffffff seq=00002 us=150000001500000000000000');
  await tick(0.08); assert.equal(writes.length, 4);
  ack(); await tick(0.13); assert.equal(writes.length, 5); // One newest frame, no missed-frame burst.
  assert(writes.at(-1).includes(' 00003 '));
  await tick(0.31); assert.equal(music.state.active, false); assert.equal(writes.at(-1), '!\n');
});

test('unresolved target write cannot be outrun by early matching ACK', async () => {
  let finish;
  const { music, controller, writes, start, ack, tick } = await fixture(text => text.startsWith('target ') ? new Promise(resolve => { finish = resolve; }) : undefined);
  await start(); ack(); await tick(0.06); assert.equal(writes.length, 3);
  finish(); await flush(); await tick(0.07); assert.equal(writes.length, 4); music.stop();
});

test('Pause, all media interruptions and USB faults invalidate callbacks and never resume on late ACK', async () => {
  for (const event of ['waiting', 'stalled', 'error', 'seeking', 'ended', 'pause', 'ratechange']) {
    const { music, audio, controller, writes, start, tick } = await fixture(); await start();
    audio.dispatchEvent(new Event(event)); const count = writes.length;
    assert.equal(music.state.active, false); assert.equal(writes.at(-1), '!\n');
    controller.receive('DANCE READY token=00000001 mask=05 watchdog_ms=250'); await tick(0.08); assert.equal(writes.length, count);
  }
  for (const change of [controller => controller.disconnected(), controller => controller.fail('fault', true),
    controller => controller.receive('REJECT target'), controller => controller.receive('DANCE TIMEOUT PWM FULL_OFF'),
    controller => controller.receive('READY PWM FULL_OFF calibration=v2 calibration_min_us=600 calibration_max_us=2300 dance=v1 dance_boot=1234abce')]) {
    const { music, controller, start, tick } = await fixture(); await start(); change(controller); await tick(0.03);
    assert.equal(music.state.active, false);
  }
});

test('missing ACK stops; software timing lead samples future feature positions independently', async () => {
  const { music, writes, start, tick } = await fixture(); await start(); await tick(0.18);
  assert.equal(music.state.active, false); assert.equal(writes.at(-1), '!\n');
  const f = await fixture(); f.music.state.leadMs = 0;
  await f.start(); const withoutLead = f.writes.at(-1); f.music.stop();
  const g = await fixture(); const pending = g.music.play({ dance: true, consent: true, channels: [{ channel: 0, feature: 'bass' }, { channel: 2, feature: 'mid' }], leadMs: 100 });
  await pending; await g.tick(0.005); g.controller.receive('ARMED for one pulse; dance_token=00000001'); await g.tick(0.01);
  g.controller.receive('DANCE READY token=00000001 mask=05 watchdog_ms=250'); await g.tick(0.02);
  assert.notEqual(g.writes.at(-1), withoutLead); g.music.stop();
});

test('Stop during unresolved audio Play cannot later send Arm and decoder autoplay failure stays stopped', async () => {
  const { music, audio, writes } = await fixture(); let finish;
  audio.play = () => new Promise(resolve => { finish = resolve; });
  const play = music.play({ dance: true, consent: true }); await flush(); music.stop(); finish(); await play;
  assert.equal(writes.filter(value => value === 'arm\n').length, 0);
  audio.play = async () => { throw Error('blocked'); };
  assert.equal(await music.play(), false); assert.equal(music.state.active, false); assert.equal(audio.currentTime, 0);
});

test('interrupted audio output stops before Arm and during streaming; recovery never resumes motion', async () => {
  for (const streaming of [false, true]) {
    const f = await fixture();
    const clock = new AudioOutputClock(f.audio);
    clock.context = { state: 'running', resume: async () => {} };
    f.music.clock = clock;
    if (streaming) await f.start();
    else await f.music.play({ dance: true, consent: true });
    clock.context.state = 'interrupted';
    await f.tick(streaming ? 0.04 : 0.02);
    assert.equal(f.music.state.active, false);
    assert.equal(f.writes.at(-1), '!\n');
    if (!streaming) assert.equal(f.writes.includes('arm\n'), false);
    const stoppedWrites = f.writes.length;
    clock.context.state = 'running'; await f.tick(0.06);
    assert.equal(f.writes.length, stoppedWrites);
    assert.equal(f.music.state.active, false);
  }
});

test('audio preroll never arms before media advances', async () => {
  const f = await fixture();
  await f.music.play({ dance: true, consent: true });
  f.music.now = () => 200; f.music.tick(f.music.generation); await flush();
  assert.equal(f.music.state.active, true); assert.deepEqual(f.writes, []);
  f.music.now = () => 1001; f.music.tick(f.music.generation); await flush();
  assert.equal(f.music.state.active, false); assert.deepEqual(f.writes, ['!\n']);
});

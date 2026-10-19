import assert from "node:assert/strict";
import test from "node:test";
import { AutoCalibration } from "../auto-calibration.mjs";
import { BenchController } from "../controller.mjs";

const ARMED = "SERVO_BENCH ARMED for one pulse, 10 seconds";
const DONE = "SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse";
const STOP = "SERVO_BENCH STOP PWM FULL_OFF";
const flush = async () => { for (let i = 0; i < 20; ++i) await Promise.resolve(); };

function clock() {
  let time = 0;
  let next = 0;
  const timers = new Map();
  return {
    now: () => time,
    setTimer: (callback, delay) => { timers.set(++next, { callback, at: time + delay }); return next; },
    clearTimer: id => timers.delete(id),
    async advance(milliseconds) {
      time += milliseconds;
      // Callbacks see the actual delayed clock, never a historical deadline.
      for (const [id, timer] of [...timers].sort((a, b) => a[1].at - b[1].at)) {
        if (timer.at <= time && timers.delete(id)) timer.callback();
      }
      await flush();
    },
    timers,
  };
}

async function fixture(options = {}) {
  const time = clock();
  const writes = [];
  const listeners = new Set();
  const deferred = [];
  let controller;
  let initializing = true;
  const write = text => {
    writes.push({ text, at: time.now() });
    if (text === "arm\n" && options.armAck !== false) {
      time.setTimer(() => controller.receive(ARMED), options.armDelay ?? 0);
    } else if (text.startsWith("cal ") && options.pulseAck !== false) {
      time.setTimer(() => controller.receive(DONE), options.pulseDelay ?? 1500);
    } else if (text === "!\n" && (initializing || options.stopAck !== false)) {
      time.setTimer(() => controller.receive(STOP), options.stopDelay ?? 0);
    }
    if (!initializing && options.deferWrite?.(text)) {
      return new Promise((resolve, reject) => deferred.push({ text, resolve, reject }));
    }
    return Promise.resolve();
  };
  controller = new BenchController({ write, now: time.now, setTimer: time.setTimer, clearTimer: time.clearTimer,
    onChange: () => { for (const listener of [...listeners]) listener(); } });
  await controller.connected();
  controller.receive("SERVO_BENCH READY PWM FULL_OFF; calibration=v2 calibration_min_us=500 calibration_max_us=2400");
  await time.advance(0);
  writes.length = 0;
  initializing = false;
  const getState = () => ({ ...controller.state, capabilities: { ...controller.capabilities } });
  const auto = new AutoCalibration({ getState,
    subscribe: callback => { listeners.add(callback); return () => listeners.delete(callback); },
    arm: () => controller.arm(), calibrate: (channel, width, duration) => controller.calibrate(channel, width, duration),
    stop: () => controller.stop(), now: time.now, setTimer: time.setTimer, clearTimer: time.clearTimer,
    onChange: options.onChange,
  });
  const start = (overrides = {}) => auto.start({ channel: 2, direction: "max", centerUs: 1500, ...overrides });
  return { auto, controller, getState, writes, deferred, time, start,
    notify: () => { for (const listener of [...listeners]) listener(); },
    pulseWrites: () => writes.filter(item => item.text.startsWith("cal ")),
    stopWrites: () => writes.filter(item => item.text === "!\n") };
}

async function startPulse(f, overrides) {
  assert.equal(f.start(overrides), true);
  await f.time.advance(0);
  assert.equal(f.pulseWrites().length, 1);
}

test("start validates bounds, direction, readiness and never queues a second run", async () => {
  const f = await fixture();
  for (const values of [{ channel: 6 }, { channel: -1 }, { centerUs: 499 }, { centerUs: 2401 },
    { centerUs: 1500.5 }, { direction: "both" }]) assert.equal(f.start(values), false);
  f.controller.state.calibrationSupported = false;
  assert.equal(f.start(), false);
  f.controller.state.calibrationSupported = true;
  assert.equal(f.start(), true);
  assert.equal(f.start(), false);
  assert.equal(f.writes.length, 1);
  f.auto.cancel();
});

test("each target requires actual Arm and matching DONE ACKs and respects 3-second cadence", async () => {
  const f = await fixture({ armDelay: 200 });
  assert.equal(f.start(), true);
  await f.time.advance(199);
  assert.equal(f.pulseWrites().length, 0);
  await f.time.advance(1);
  assert.equal(f.pulseWrites()[0].text, "cal 2 1500 1500\n");
  await f.time.advance(1499);
  assert.equal(f.auto.state.stage, "pulsing");
  await f.time.advance(1);
  assert.equal(f.auto.state.stage, "pause");
  await f.time.advance(1499);
  assert.equal(f.writes.filter(item => item.text === "arm\n").length, 1);
  await f.time.advance(1);
  assert.equal(f.writes.at(-1).text, "arm\n");
  await f.time.advance(200);
  assert.equal(f.pulseWrites()[1].text, "cal 2 1525 1500\n");
  assert.ok(f.pulseWrites()[1].at - f.pulseWrites()[0].at >= 3000);
  f.auto.cancel();
});

test("early ARMED and DONE cannot outrun unresolved USB writes", async () => {
  const f = await fixture({ deferWrite: text => text === "arm\n" || text.startsWith("cal ") });
  assert.equal(f.start(), true);
  await f.time.advance(0);
  assert.equal(f.pulseWrites().length, 0);
  f.deferred.shift().resolve();
  await flush();
  assert.equal(f.pulseWrites().length, 1);
  await f.time.advance(1500);
  assert.equal(f.auto.state.stage, "pulsing");
  f.deferred.shift().resolve();
  await flush();
  assert.equal(f.auto.state.stage, "pause");
  f.auto.cancel();
});

test("browser delay never catches up or bursts targets", async () => {
  const f = await fixture();
  await startPulse(f);
  await f.time.advance(1500);
  await f.time.advance(20000);
  await f.time.advance(0);
  assert.equal(f.pulseWrites().length, 2);
  const delayedStart = f.pulseWrites()[1].at;
  await f.time.advance(1500);
  await f.time.advance(1499);
  assert.equal(f.pulseWrites().length, 2);
  await f.time.advance(1);
  await f.time.advance(0);
  assert.equal(f.pulseWrites().length, 3);
  assert.ok(f.pulseWrites()[2].at - delayedStart >= 3000);
  f.auto.cancel();
});

test("noise during first center, a pulse, and its pause excludes the current target", async () => {
  for (const noiseInPause of [false, true]) {
    const f = await fixture();
    await startPulse(f);
    if (noiseInPause) await f.time.advance(1500);
    f.auto.markNoise();
    assert.equal(f.auto.state.active, false);
    assert.equal(f.auto.state.candidateUs, null);
    assert.equal(f.stopWrites().length, 1);
    await f.time.advance(20000);
    assert.equal(f.pulseWrites().length, 1);
  }
  for (const noiseInPause of [false, true]) {
    const f = await fixture();
    await startPulse(f);
    await f.time.advance(1500);
    await f.time.advance(1500);
    await f.time.advance(0);
    if (noiseInPause) await f.time.advance(1500);
    f.auto.markNoise();
    assert.equal(f.auto.state.currentUs, 1525);
    assert.equal(f.auto.state.previousUs, 1500);
    assert.equal(f.auto.state.candidateUs, 1500);
    assert.equal(f.auto.state.stopConfirmed, false);
    await f.time.advance(0);
    assert.equal(f.auto.state.stopConfirmed, true);
    assert.deepEqual(f.auto.state.candidateEvidence, {
      channel: 2, pulseWidthUs: 1500, centerUs: 1500, direction: "max", completed: true,
    });
    await f.time.advance(20000);
    assert.equal(f.pulseWrites().length, 2);
    assert.equal(f.stopWrites().length, 1);
    f.auto.discardCandidate();
    assert.equal(f.auto.state.candidateUs, null);
    assert.equal(f.auto.state.stopConfirmed, false);
  }
});

test("candidate Stop proof needs a fresh ACK and its write completion", async () => {
  const f = await fixture({ deferWrite: text => text === "!\n" });
  await startPulse(f);
  await f.time.advance(1500);
  await f.time.advance(1500);
  await f.time.advance(0);
  f.auto.markNoise();
  assert.equal(f.auto.state.stopConfirmed, false);
  await f.time.advance(0);
  assert.equal(f.auto.state.stopConfirmed, false);
  f.deferred.shift().resolve();
  await flush();
  assert.equal(f.auto.state.stopConfirmed, true);
});

test("noise during the next Arm or its pending write excludes the last audible target", async () => {
  for (const pendingWrite of [false, true]) {
    const f = await fixture({ deferWrite: text => pendingWrite && text === "arm\n" });
    f.start({ direction: "min" });
    await f.time.advance(0);
    if (pendingWrite) { f.deferred.shift().resolve(); await flush(); }
    await f.time.advance(1500);
    await f.time.advance(1500);
    if (pendingWrite) {
      await f.time.advance(0);
      f.deferred.shift().resolve();
      await flush();
    } else await f.time.advance(0);
    assert.equal(f.auto.state.currentUs, 1475);
    await f.time.advance(1500);
    await f.time.advance(1500);
    if (pendingWrite) await f.time.advance(0); // ARMED arrived, Arm write still unresolved.
    assert.equal(f.auto.state.stage, "arming");
    assert.equal(f.auto.state.currentUs, 1475);
    assert.equal(f.auto.state.previousUs, 1500);
    f.auto.markNoise();
    assert.equal(f.auto.state.candidateUs, 1500);
    for (const deferred of f.deferred) deferred.resolve();
    await f.time.advance(20000);
    assert.equal(f.pulseWrites().length, 2);
    assert.equal(f.stopWrites().length, 1);
  }
});

test("missing Arm, DONE or write completion aborts with one Stop and no retries", async () => {
  for (const options of [{ armAck: false }, { pulseAck: false },
    { deferWrite: text => text === "arm\n" }, { deferWrite: text => text.startsWith("cal ") }]) {
    const f = await fixture(options);
    assert.equal(f.start(), true);
    await f.time.advance(0);
    await f.time.advance(4500);
    assert.equal(f.auto.state.active, false);
    assert.equal(f.auto.state.candidateUs, null);
    assert.equal(f.stopWrites().length, 1);
    const movements = f.pulseWrites().length;
    await f.time.advance(20000);
    for (const deferred of f.deferred) deferred.resolve();
    await flush();
    assert.equal(f.pulseWrites().length, movements);
    assert.equal(f.stopWrites().length, 1);
  }
});

test("manual Stop invalidates the run before ARMED or unresolved writes can continue it", async () => {
  for (const deferredWrite of [false, true]) {
    const f = await fixture({ armAck: false, deferWrite: text => deferredWrite && text === "arm\n" });
    f.start();
    f.auto.cancel("manual_stop", { sendStop: false });
    await f.controller.stop();
    f.controller.receive(ARMED);
    for (const deferred of f.deferred) deferred.resolve();
    await flush();
    await f.time.advance(20000);
    assert.equal(f.pulseWrites().length, 0);
    assert.equal(f.stopWrites().length, 1);
    assert.equal(f.auto.state.candidateUs, null);
  }
  const f = await fixture({ deferWrite: text => text.startsWith("cal ") });
  await startPulse(f);
  f.auto.cancel("manual_stop", { sendStop: false });
  await f.controller.stop();
  f.deferred.shift().resolve();
  await f.time.advance(20000);
  assert.equal(f.pulseWrites().length, 1);
  assert.equal(f.stopWrites().length, 1);
});

test("faults, disconnection, reset and unrelated commands immediately interrupt all stages", async () => {
  for (const stage of ["arming", "pulsing", "pause"]) {
    for (const event of ["fault", "disconnect", "reset", "stop", "error", "replacement"]) {
      const f = await fixture({ armAck: stage !== "arming" });
      f.start();
      if (stage !== "arming") await f.time.advance(0);
      if (stage === "pause") await f.time.advance(1500);
      assert.equal(f.auto.state.stage, stage);
      if (event === "fault") f.controller.receive("SERVO_BENCH FAULT PWM_OFF_FAILED; cut servo power");
      else if (event === "disconnect") f.controller.disconnected();
      else if (event === "reset") f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; calibration=v2 calibration_min_us=500 calibration_max_us=2400");
      else if (event === "stop") await f.controller.stop();
      else if (event === "error") f.controller.fail("Unexpected transport failure");
      else {
        f.controller.state.pending = stage === "arming" ? "pulse" : "arm";
        f.notify();
      }
      assert.equal(f.auto.state.active, false, `${stage}/${event}`);
      assert.equal(f.auto.state.candidateUs, null);
      const expectedStops = ["fault", "disconnect"].includes(event) ? 0 : 1;
      assert.equal(f.stopWrites().length, expectedStops, `${stage}/${event}`);
      const previous = f.pulseWrites().length;
      await f.time.advance(20000);
      assert.equal(f.pulseWrites().length, previous);
      assert.equal(f.stopWrites().length, expectedStops);
    }
  }
});

test("a completed ACK for a different calibration cannot satisfy the current command", async () => {
  for (const mismatch of [{ channel: 1 }, { pulseWidthUs: 1525 }, { durationMs: 500 }, { calibration: false }]) {
    const f = await fixture({ pulseAck: false });
    await startPulse(f);
    f.controller.state.pending = null;
    f.controller.state.lastCompleted = { channel: 2, pulseWidthUs: 1500, durationMs: 1500,
      calibration: true, ...mismatch };
    f.notify();
    assert.equal(f.auto.state.active, false);
    assert.equal(f.stopWrites().length, 1);
    assert.equal(f.auto.state.candidateUs, null);
  }
});

test("restart does not inherit an old waiter, timer or deferred write", async () => {
  const f = await fixture({ armAck: false, deferWrite: text => text === "arm\n" });
  f.start();
  const old = f.deferred.shift();
  f.auto.cancel();
  await f.time.advance(0);
  assert.equal(f.start({ direction: "min" }), true);
  old.resolve();
  await flush();
  assert.equal(f.pulseWrites().length, 0);
  f.controller.receive(ARMED);
  assert.equal(f.pulseWrites().length, 0);
  f.deferred.shift().resolve();
  await flush();
  assert.equal(f.pulseWrites().length, 1);
  f.auto.cancel();
});

test("bounded runs finish at the endpoint and require fresh Stop proof", async () => {
  for (const [centerUs, direction, expected] of [
    [500, "max", 77], [2400, "min", 77], [525, "min", 2], [2400, "max", 1],
  ]) {
    const f = await fixture();
    await startPulse(f, { centerUs, direction });
    for (let index = 0; index < expected; ++index) {
      await f.time.advance(1500);
      if (index < expected - 1) {
        await f.time.advance(1500);
        await f.time.advance(0);
      }
    }
    assert.equal(f.auto.state.active, false);
    assert.equal(f.auto.state.stage, "completed");
    assert.equal(f.auto.state.reason, "bound");
    assert.equal(f.pulseWrites().length, expected);
    const endpoint = direction === "min" ? 500 : 2400;
    assert.equal(f.auto.state.currentUs, endpoint);
    assert.equal(f.auto.state.candidateUs, centerUs === endpoint ? null : endpoint);
    assert.equal(f.stopWrites().length, 1);
    if (centerUs !== endpoint) {
      assert.equal(f.auto.state.stopConfirmed, false);
      await f.time.advance(0);
      assert.equal(f.auto.state.stopConfirmed, true);
    }
    assert.equal(f.auto.markNoise(), false); // Inactive retrospective proposals are not invented.
    await f.time.advance(20000);
    assert.equal(f.pulseWrites().length, expected);
  }
});

test("a missing candidate Stop ACK never confirms OFF or retries Stop", async () => {
  const f = await fixture({ stopAck: false });
  await startPulse(f, { centerUs: 2375 });
  await f.time.advance(1500);
  await f.time.advance(1500);
  await f.time.advance(0);
  await f.time.advance(1500);
  assert.equal(f.auto.state.candidateUs, 2400);
  await f.time.advance(10000);
  assert.equal(f.auto.state.stopConfirmed, false);
  assert.equal(f.stopWrites().length, 1);
});

test("thrown callbacks are caught and reentrant cancellation cannot send a stale command", async () => {
  const f = await fixture({ onChange: () => { throw new Error("render failed"); } });
  f.auto.arm = () => { throw new Error("callback failed"); };
  assert.equal(f.start(), true);
  await flush();
  assert.equal(f.auto.state.active, false);
  assert.equal(f.stopWrites().length, 1);

  const reentrant = await fixture();
  reentrant.auto.onChange = state => {
    if (state.stage === "arming") reentrant.auto.cancel("reentrant_stop");
  };
  reentrant.start();
  await flush();
  assert.equal(reentrant.writes.filter(item => item.text === "arm\n").length, 0);
  assert.equal(reentrant.stopWrites().length, 1);

  const pulseFailure = await fixture();
  pulseFailure.auto.calibrate = () => { throw new Error("calibrate callback failed"); };
  pulseFailure.start();
  await pulseFailure.time.advance(0);
  assert.equal(pulseFailure.auto.state.active, false);
  assert.equal(pulseFailure.stopWrites().length, 1);

  const observerFailure = await fixture();
  await startPulse(observerFailure);
  await observerFailure.time.advance(1500);
  await observerFailure.time.advance(1500);
  await observerFailure.time.advance(0);
  observerFailure.auto.subscribe = () => { throw new Error("observer failed"); };
  observerFailure.auto.markNoise();
  assert.equal(observerFailure.stopWrites().length, 1);
  assert.equal(observerFailure.auto.state.stopConfirmed, false);

  const stopFailure = await fixture();
  stopFailure.auto.stop = () => Promise.reject(new Error("Stop write failed"));
  stopFailure.start();
  stopFailure.auto.cancel();
  await flush();
  assert.equal(stopFailure.auto.state.active, false);
  assert.equal(stopFailure.auto.state.stopConfirmed, false);
});

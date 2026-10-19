import assert from "node:assert/strict";
import test from "node:test";
import { BenchController, LineParser, parseReadback } from "../controller.mjs";
import { SerialTransport, flushStopBeforeClose } from "../serial.mjs";

function fixture(writeOverride) {
  let now = 100;
  let timerId = 0;
  const timers = new Map();
  const writes = [];
  const controller = new BenchController({
    write: writeOverride ?? (async (text, stop) => { writes.push({ text, stop }); }),
    now: () => now,
    setTimer: (callback, delay) => { timers.set(++timerId, { callback, at: now + delay }); return timerId; },
    clearTimer: id => timers.delete(id),
  });
  return { controller, writes, advance: duration => {
    now += duration;
    for (const [id, timer] of [...timers]) {
      if (timer.at <= now) { timers.delete(id); timer.callback(); }
    }
  } };
}

async function ready(f) {
  await f.controller.connected();
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
}

test("connection sends only emergency Stop; actual ACK gates Arm and no automatic motion", async () => {
  const f = fixture();
  await f.controller.connected();
  assert.deepEqual(f.writes, [{ text: "!\n", stop: true }]);
  assert.equal(f.controller.capabilities.arm, false);
  assert.equal(f.controller.pulse(0, 1500), false);
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; type help");
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  assert.equal(f.controller.capabilities.arm, true);
  assert.equal(f.writes.length, 1);
});

test("pulse consumes Arm, disallows duplicate motion and requires new Arm after DONE", async () => {
  const f = fixture();
  await ready(f);
  await f.controller.arm();
  assert.equal(f.controller.capabilities.pulse, false);
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds; servo must be unloaded");
  assert.equal(f.controller.pulse(6, 1500), false);
  assert.equal(f.controller.pulse(0, 1700), false);
  assert.equal(f.controller.capabilities.pulse, true);
  const pulse = f.controller.pulse(0, 1500);
  assert.equal(f.controller.pulse(0, 1500), false);
  assert.equal(f.controller.arm(), false);
  await pulse;
  f.controller.receive("SERVO_BENCH PULSE nominal 500 ms; timeout/Stop writes PCA FULL_OFF");
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse");
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.controller.capabilities.arm, true);
  assert.deepEqual(f.writes.map(write => write.text), ["!\n", "arm\n", "pulse 0 1500\n"]);
});

test("Stop clears Arm immediately and ignores delayed ARMED ACK", async () => {
  const f = fixture();
  await ready(f);
  await f.controller.arm();
  await f.controller.stop();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  assert.equal(f.controller.capabilities.arm, true);
  assert.equal(f.controller.capabilities.pulse, false);
});

test("Arm expires conservatively and reset clears live authorization", async () => {
  const f = fixture();
  await ready(f);
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  f.advance(9500);
  assert.equal(f.controller.capabilities.pulse, false);
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; type help");
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.controller.capabilities.arm, false);
});

test("late DONE cannot consume a pending emergency Stop acknowledgement", async () => {
  const f = fixture();
  await ready(f);
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  await f.controller.pulse(0, 1500);
  await f.controller.stop();
  f.controller.receive("SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse");
  assert.equal(f.controller.state.pending, "stop");
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  assert.equal(f.controller.capabilities.arm, true);
});

test("disconnect allows an emergency write to flush and bounds a stalled writer", async () => {
  const writes = [];
  const transport = new SerialTransport(null, {});
  transport.writer = { write: async bytes => writes.push(new TextDecoder().decode(bytes)) };
  let finishWait;
  const wait = () => new Promise(resolve => { finishWait = resolve; });
  await flushStopBeforeClose(() => transport.write("!\n", true), wait);
  assert.deepEqual(writes, ["!\n"]);
  const stalled = flushStopBeforeClose(() => new Promise(() => {}), wait);
  finishWait();
  await stalled;
});

test("missing ACK and rejected command require a new Stop baseline", async () => {
  const f = fixture();
  await ready(f);
  await f.controller.arm();
  f.advance(3000);
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.capabilities.pulse, false);
  await f.controller.stop();
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH REJECT arm first; permitted pulse is 1400-1600 us");
  assert.equal(f.controller.capabilities.arm, false);
});

test("fault survives Stop and reconnect starts with no Arm; stale write failure is isolated", async () => {
  const f = fixture();
  await ready(f);
  f.controller.receive("SERVO_BENCH FAULT PWM_OFF_FAILED; cut servo power");
  await f.controller.stop();
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.disconnected();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  await f.controller.connected();
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.capabilities.pulse, false);

  let rejectOldWrite;
  const delayed = fixture(() => new Promise((resolve, reject) => { rejectOldWrite = reject; }));
  const oldWrite = delayed.controller.connected();
  delayed.controller.disconnected();
  rejectOldWrite(new Error("old session"));
  await oldWrite;
  assert.equal(delayed.controller.state.error, "");
  assert.equal(delayed.controller.capabilities.stop, false);
});

test("write failure disables all motion", async () => {
  const f = fixture(async () => { throw new Error("USB write failed"); });
  await f.controller.connected();
  assert.equal(f.controller.capabilities.arm, false);
  assert.equal(f.controller.capabilities.pulse, false);
  assert.match(f.controller.state.error, /USB write failed/);
});

test("fragmented CRLF parser preserves lines and discards overlong lines", () => {
  const lines = [];
  let overflows = 0;
  const parser = new LineParser(line => lines.push(line), () => overflows++);
  parser.push("SERVO_BENCH AR");
  parser.push("MED for one pulse\r");
  assert.equal(lines.length, 0);
  parser.push("\nSERVO_BENCH STOP PWM FULL_OFF\n");
  assert.deepEqual(lines, ["SERVO_BENCH ARMED for one pulse", "SERVO_BENCH STOP PWM FULL_OFF"]);
  parser.push("x".repeat(3000));
  parser.push("SERVO_BENCH ARMED for one pulse\nnext\n");
  assert.equal(overflows, 1);
  assert.equal(lines.at(-1), "next");
  assert.equal(lines.length, 3);
});

test("readback is register evidence; mismatch or read failure latches fault", async () => {
  const line = "SERVO_BENCH PCA_READBACK ch=0 mode1=0x20 mode2=0x04 prescale=121 on=0 off=307 full_on=0 full_off=0 expected_off=307 match=1";
  const readback = parseReadback(line);
  assert.equal(readback.off, 307);
  assert.equal(readback.match, true);
  assert.equal(parseReadback("PCA_READBACK failed"), null);
  const f = fixture();
  await ready(f);
  f.controller.receive(line.replace("match=1", "match=0"));
  assert.equal(f.controller.state.fault, true);
  assert.equal(f.controller.capabilities.arm, false);
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF");
  f.controller.receive("SERVO_BENCH PCA_READBACK failed; stopping and latching fault");
  assert.equal(f.controller.state.fault, true);
});

test("transport Stop cancels queued motion before serial write", async () => {
  const writes = [];
  const transport = new SerialTransport(null, {});
  transport.writer = { write: async bytes => writes.push(new TextDecoder().decode(bytes)) };
  const motion = transport.write("pulse 0 1500\n");
  const stop = transport.write("!\n", true);
  await assert.rejects(motion, /zrušen/);
  await stop;
  assert.deepEqual(writes, ["!\n"]);
});

test("early Arm ACK cannot enable Pulse until the USB write settles", async () => {
  let finishArm;
  const writes = [];
  const f = fixture(text => {
    writes.push(text);
    return text === "arm\n" ? new Promise(resolve => { finishArm = resolve; }) : Promise.resolve();
  });
  await ready(f);
  const arm = f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.controller.pulse(0, 1500), false);
  f.advance(9500);
  finishArm();
  await arm;
  assert.equal(f.controller.capabilities.pulse, false);
  assert.deepEqual(writes, ["!\n", "arm\n"]);
});

test("transport refuses motion behind an unresolved write; Stop remains next", async () => {
  const writes = [];
  let finishArm;
  const transport = new SerialTransport(null, {});
  transport.writer = { write: bytes => {
    const text = new TextDecoder().decode(bytes);
    writes.push(text);
    return text === "arm\n" ? new Promise(resolve => { finishArm = resolve; }) : Promise.resolve();
  } };
  const arm = transport.write("arm\n");
  await new Promise(resolve => setImmediate(resolve));
  await assert.rejects(transport.write("pulse 0 1500\n"), /nebyl zařazen/);
  const stop = transport.write("!\n", true);
  finishArm();
  await Promise.all([arm, stop]);
  assert.deepEqual(writes, ["arm\n", "!\n"]);
});

test("transport uses required serial configuration and drops old-session reads", async () => {
  const lines = [];
  const disconnected = [];
  let nextRead;
  let request;
  let options;
  let signals;
  let readerReleased = false;
  let writerReleased = false;
  let closed = false;
  const reader = {
    read: () => new Promise(resolve => { nextRead = resolve; }),
    cancel: async () => nextRead({ done: true }), releaseLock: () => { readerReleased = true; },
  };
  const writer = { write: async () => {}, abort: async () => {}, releaseLock: () => { writerReleased = true; } };
  const port = {
    open: async value => { options = value; }, setSignals: async value => { signals = value; },
    readable: { getReader: () => reader }, writable: { getWriter: () => writer },
    close: async () => { closed = true; },
  };
  const transport = new SerialTransport({ requestPort: async value => { request = value; return port; } }, {
    onLine: line => lines.push(line), onDisconnect: reason => disconnected.push(reason), onOverflow: () => {},
  });
  await transport.connect();
  assert.deepEqual(request, { filters: [{ usbVendorId: 0x1A86, usbProductId: 0x55D3 }] });
  assert.equal(options.baudRate, 115200);
  assert.equal(options.flowControl, "none");
  assert.deepEqual(signals, { dataTerminalReady: false, requestToSend: false });
  const close = transport.close();
  await close;
  assert.equal(lines.length, 0);
  assert.equal(disconnected.length, 0);
  assert.equal(readerReleased && writerReleased && closed, true);
});

test("throwing disconnect callback still releases writer and closes port", async () => {
  let endRead;
  let released = false;
  let closed = false;
  const reader = { read: () => new Promise(resolve => { endRead = resolve; }),
    cancel: async () => {}, releaseLock: () => {} };
  const writer = { abort: async () => {}, releaseLock: () => { released = true; } };
  const port = { open: async () => {}, setSignals: async () => {},
    readable: { getReader: () => reader }, writable: { getWriter: () => writer },
    close: async () => { closed = true; } };
  const transport = new SerialTransport({ requestPort: async () => port }, {
    onLine: () => {}, onOverflow: () => {}, onDisconnect: () => { throw new Error("render failed"); },
  });
  await transport.connect();
  endRead({ done: true });
  await new Promise(resolve => setImmediate(resolve));
  if (transport.closeTask) await transport.closeTask;
  assert.equal(released && closed, true);
});

async function calibrationReady(f) {
  await f.controller.connected();
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; calibration=v1; type help");
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
}

test("calibration requires advertised support, Arm and strict integer bounds", async () => {
  const f = fixture();
  await ready(f);
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.calibrate(0, 1500, 1500), false);
  await f.controller.stop();
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF calibration=v1");
  f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF");
  assert.equal(f.controller.calibrate(0, 1500, 1500), false);
  await f.controller.arm();
  f.controller.receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
  for (const args of [[-1, 1500, 1500], [6, 1500, 1500], [0, 999, 1500], [0, 2001, 1500],
    [0, 1500, 499], [0, 1500, 3001], [0, 1500.1, 1500], [0, 1500, 500.5]]) {
    assert.equal(f.controller.calibrate(...args), false);
  }
  assert.equal(f.controller.capabilities.calibrate, true);
  await f.controller.calibrate(0, 1000, 500);
  assert.equal(f.writes.at(-1).text, "cal 0 1000 0500\n");
  assert.equal(f.writes.at(-1).text.trim().length, 15);
  assert.equal(f.controller.calibrate(0, 1500, 1500), false);
});

test("maximum calibration duration stays pending through 3000ms and records only actual DONE", async () => {
  const f = fixture();
  await calibrationReady(f);
  await f.controller.calibrate(5, 2000, 3000);
  f.controller.receive("SERVO_BENCH PULSE nominal 3000 ms; calibration; timeout/Stop writes PCA FULL_OFF");
  f.advance(3000);
  assert.equal(f.controller.state.pending, "pulse");
  assert.equal(f.controller.state.lastCompleted, null);
  assert.equal(f.controller.state.error, "");
  f.controller.receive("SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse");
  assert.deepEqual(f.controller.state.lastCompleted, { channel: 5, pulseWidthUs: 2000, durationMs: 3000, calibration: true });
  f.advance(2000);
  assert.equal(f.controller.state.error, "");
});

test("calibration timeout, Stop, fault, restart and disconnect invalidate completed targets", async () => {
  const f = fixture();
  await calibrationReady(f);
  await f.controller.calibrate(0, 1800, 3000);
  f.advance(4499);
  assert.equal(f.controller.state.error, "");
  f.advance(1);
  assert.equal(f.controller.state.pending, null);
  assert.equal(f.controller.state.lastCompleted, null);
  assert.notEqual(f.controller.state.error, "");
  for (const invalidate of [
    () => f.controller.stop(),
    () => f.controller.receive("SERVO_BENCH STOP PWM FULL_OFF"),
    () => f.controller.receive("SERVO_BENCH FAULT PWM_OFF_FAILED"),
    () => f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; calibration=v1"),
    () => f.controller.disconnected(),
  ]) {
    await calibrationReady(f);
    await f.controller.calibrate(0, 1500, 1500);
    f.controller.receive("SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse");
    assert.notEqual(f.controller.state.lastCompleted, null);
    await invalidate();
    assert.equal(f.controller.state.lastCompleted, null);
  }
  assert.equal(f.controller.state.calibrationSupported, false);
  await f.controller.connected();
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; calibration=v1");
  assert.equal(f.controller.state.calibrationSupported, true);
  f.controller.receive("SERVO_BENCH READY PWM FULL_OFF; type help");
  assert.equal(f.controller.state.calibrationSupported, false);
});

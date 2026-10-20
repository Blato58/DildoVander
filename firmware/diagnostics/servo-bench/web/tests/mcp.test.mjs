import assert from "node:assert/strict";
import test from "node:test";
import { registerBenchTools } from "../mcp.mjs";
import { BenchController } from "../controller.mjs";
import { SerialTransport } from "../serial.mjs";

const tick = () => new Promise(resolve => setImmediate(resolve));
const payload = response => JSON.parse(response.content[0].text);

async function fixture(options = {}) {
  const definitions = new Map();
  const listeners = new Set();
  const timers = new Map();
  const writes = [];
  const log = [];
  let nextTimer = 0;
  let connecting = false;
  let closing = false;
  let controller;
  const setTimer = (callback, delay) => { timers.set(++nextTimer, { callback, delay }); return nextTimer; };
  const clearTimer = id => timers.delete(id);
  controller = new BenchController({
    write: async command => { writes.push(command); },
    onChange: () => { for (const listener of [...listeners]) listener(); },
    onLog: line => log.push(line), setTimer, clearTimer,
  });
  const getState = () => ({ ...structuredClone(controller.state), capabilities: { ...controller.capabilities }, connecting, closing });
  const modelContext = options.modelContext ?? {
    async registerTool(definition, { signal }) {
      definitions.set(definition.name, definition);
      signal.addEventListener("abort", () => definitions.delete(definition.name), { once: true });
    },
  };
  const registration = await registerBenchTools({
    modelContext, getState, getLog: () => log,
    subscribe: listener => { listeners.add(listener); return () => listeners.delete(listener); },
    connect: async () => {
      connecting = true;
      controller.changed();
      await controller.connected();
      connecting = false;
      controller.changed();
      return true;
    },
    disconnect: async () => {
      closing = true;
      controller.changed();
      controller.disconnected();
      closing = false;
      controller.changed();
      return true;
    },
    arm: () => controller.arm(), pulse: (channel, width) => controller.pulse(channel, width),
    calibrate: (channel, width, durationMs) => controller.calibrate(channel, width, durationMs),
    stop: () => controller.stop(), setTimer, clearTimer,
  });
  return { registration, definitions, controller, writes, listeners, log,
    tool: (suffix, input = {}, options = {}) => definitions.get(`servo_bench_${suffix}`).execute(input, options),
    ack: line => controller.receive(`SERVO_BENCH ${line}`),
    expire: delay => {
      for (const [id, timer] of [...timers]) {
        if (timer.delay === delay) { timers.delete(id); timer.callback(); }
      }
    } };
}

async function connected(f) {
  const connect = f.tool("connect");
  await tick();
  f.ack("STOP PWM FULL_OFF");
  assert.equal((await connect).isError, false);
}

test("registration exposes eight strict tools and cleanup unregisters by AbortSignal", async () => {
  const f = await fixture();
  assert.equal(f.registration.registered, true);
  assert.equal(f.definitions.size, 8);
  const pulse = f.definitions.get("servo_bench_pulse");
  assert.equal(pulse.inputSchema.additionalProperties, false);
  assert.deepEqual(pulse.inputSchema.properties.pulseWidthUs.enum, [1400, 1500, 1600]);
  assert.equal(pulse.annotations.consequentialHint, true);
  assert.equal(f.definitions.get("servo_bench_get_state").annotations.readOnlyHint, true);
  f.registration.cleanup();
  assert.equal(f.definitions.size, 0);
  assert.equal(f.listeners.size, 0);
});

test("read tools return copied state and bounded logs without UART writes", async () => {
  const f = await fixture();
  f.log.push(...Array.from({ length: 240 }, (_, i) => `line ${i}`));
  const state = payload(await f.tool("get_state"));
  state.state.capabilities.arm = true;
  assert.equal(f.controller.capabilities.arm, false);
  const result = payload(await f.tool("get_log"));
  assert.equal(result.log.length, 200);
  assert.equal(result.log[0], "line 40");
  result.log[0] = "mutated";
  assert.equal(f.log[40], "line 40");
  assert.deepEqual(f.writes, []);
});

test("strict validation and controller gates reject invalid or unarmed motion", async () => {
  const f = await fixture();
  for (const input of [null, [], { channel: "0", pulseWidthUs: 1500 }, { channel: 6, pulseWidthUs: 1500 },
    { channel: 0, pulseWidthUs: 1501 }, { channel: 0, pulseWidthUs: 1500, durationMs: 500 },
    { channel: 0, pulseWidthUs: 1500, command: "arm" }]) {
    assert.equal((await f.tool("pulse", input)).isError, true);
  }
  assert.equal((await f.tool("get_state", { command: "!" })).isError, true);
  assert.equal((await f.tool("arm")).isError, true);
  assert.equal((await f.tool("pulse", { channel: 0, pulseWidthUs: 1500 })).isError, true);
  assert.deepEqual(f.writes, []);
});

test("connect and Arm wait for actual firmware ACK; pulse waits for DONE", async () => {
  const f = await fixture();
  let settled = false;
  const connect = f.tool("connect").then(value => { settled = true; return value; });
  await tick();
  assert.equal(settled, false);
  assert.deepEqual(f.writes, ["!\n"]);
  f.ack("STOP PWM FULL_OFF");
  assert.equal((await connect).isError, false);
  const arm = f.tool("arm");
  await tick();
  assert.equal(f.controller.capabilities.pulse, false);
  f.ack("ARMED for one pulse, 10 seconds");
  assert.equal((await arm).isError, false);
  const pulse = f.tool("pulse", { channel: 2, pulseWidthUs: 1600 });
  let pulseSettled = false;
  pulse.then(() => { pulseSettled = true; });
  await tick();
  f.ack("PULSE nominal 500 ms; timeout/Stop writes PCA FULL_OFF");
  await tick();
  assert.equal(pulseSettled, false);
  f.ack("DONE PWM FULL_OFF; arm again for another pulse");
  const completed = payload(await pulse);
  assert.equal(completed.ok, true);
  assert.match(completed.message, /Poloha serva není měřena/);
  assert.deepEqual(f.writes, ["!\n", "arm\n", "pulse 2 1600\n"]);
  assert.equal((await f.tool("disconnect")).isError, false);
});

test("concurrent mutation is rejected, Stop interrupts and late ACK never queues motion", async () => {
  const f = await fixture();
  await connected(f);
  const arm = f.tool("arm");
  assert.equal((await f.tool("arm")).isError, true);
  assert.equal((await f.tool("pulse", { channel: 0, pulseWidthUs: 1500 })).isError, true);
  const stop = f.tool("stop");
  assert.equal((await arm).isError, true);
  f.ack("ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.capabilities.pulse, false);
  f.ack("STOP PWM FULL_OFF");
  assert.equal((await stop).isError, false);
  assert.equal(f.writes.filter(command => command.startsWith("pulse")).length, 0);
  assert.equal(f.listeners.size, 0);
});

test("firmware faults fail actions and preserve the controller latch", async () => {
  const f = await fixture();
  await connected(f);
  const arm = f.tool("arm");
  f.ack("FAULT PWM_OFF_FAILED; cut servo power");
  assert.equal((await arm).isError, true);
  assert.equal((await f.tool("arm")).isError, true);
  const stop = f.tool("stop");
  f.ack("STOP PWM FULL_OFF");
  assert.equal((await stop).isError, true);
  assert.equal(f.controller.state.fault, true);
  assert.equal(f.controller.capabilities.arm, false);
});

test("4s tool timeout is bounded and does not retry the motion", async () => {
  const f = await fixture();
  await connected(f);
  const arm = f.tool("arm");
  f.expire(4000);
  assert.equal((await arm).isError, true);
  await tick();
  assert.equal(f.writes.filter(command => command === "arm\n").length, 1);
  assert.equal(f.writes.at(-1), "!\n");
  f.ack("ARMED for one pulse, 10 seconds");
  assert.equal(f.controller.capabilities.pulse, false);
  assert.equal(f.listeners.size, 0);
});

test("repeated fault while already faulted never claims PWM off without Stop ACK", async () => {
  const f = await fixture();
  await connected(f);
  f.ack("FAULT PWM_OFF_FAILED; PWM may still be active");
  const stop = f.tool("stop");
  f.ack("FAULT PWM_OFF_FAILED; PWM may still be active");
  const response = payload(await stop);
  assert.equal(response.ok, false);
  assert.doesNotMatch(response.message, /PWM FULL_OFF potvrzeno|Firmware potvrdil Stop/);
  assert.equal(response.state.fault, true);
});

test("manual Stop interrupts pending Arm and Pulse rather than reporting motion completed", async () => {
  for (const action of ["arm", "pulse"]) {
    const f = await fixture();
    await connected(f);
    if (action === "pulse") {
      const arm = f.tool("arm");
      f.ack("ARMED for one pulse, 10 seconds");
      await arm;
    }
    const motion = action === "arm" ? f.tool("arm") : f.tool("pulse", { channel: 0, pulseWidthUs: 1500 });
    await f.controller.stop();
    f.ack("STOP PWM FULL_OFF");
    const response = payload(await motion);
    assert.equal(response.ok, false);
    assert.match(response.message, /přerušen/);
    assert.equal(f.controller.capabilities.pulse, false);
    assert.equal(f.writes.filter(command => command.startsWith("pulse")).length, action === "pulse" ? 1 : 0);
  }
});

test("AbortSignal cancels pending motion and requests only a safety Stop", async () => {
  const f = await fixture();
  await connected(f);
  const abort = new AbortController();
  const arm = f.tool("arm", {}, { signal: abort.signal });
  abort.abort();
  assert.equal((await arm).isError, true);
  await tick();
  assert.equal(f.writes.at(-1), "!\n");
  const before = f.writes.length;
  assert.equal((await f.tool("arm", {}, { signal: abort.signal })).isError, true);
  assert.equal(f.writes.length, before);
});

test("unsupported runtime and partial registration failure degrade without action callbacks", async () => {
  const unsupported = await fixture({ modelContext: {} });
  assert.equal(unsupported.registration.supported, false);
  let calls = 0;
  const signals = [];
  const failed = await fixture({ modelContext: { registerTool: async (_, { signal }) => {
    signals.push(signal);
    if (++calls === 3) throw new Error("registration denied");
  } } });
  assert.equal(failed.registration.registered, false);
  assert.equal(failed.registration.error, "registration denied");
  assert.equal(signals.every(signal => signal.aborted), true);
  assert.deepEqual(failed.writes, []);
});

function fakePort(info = { usbVendorId: 0x1A86, usbProductId: 0x55D3 }) {
  let endRead;
  const port = {
    opens: 0, closes: 0, getInfo: () => info,
    open: async () => { port.opens++; }, setSignals: async () => {},
    readable: { getReader: () => ({ read: () => new Promise(resolve => { endRead = resolve; }),
      cancel: async () => endRead({ done: true }), releaseLock: () => {} }) },
    writable: { getWriter: () => ({ write: async () => {}, abort: async () => {}, releaseLock: () => {} }) },
    close: async () => { port.closes++; },
  };
  return port;
}

test("authorized connection filters CH343 without picker and permits clean reconnect", async () => {
  const target = fakePort();
  const other = fakePort({ usbVendorId: 0x1A86, usbProductId: 0x7523 });
  let pickerCalls = 0;
  const transport = new SerialTransport({ getPorts: async () => [other, target], requestPort: async () => { pickerCalls++; } }, {
    onLine: () => {}, onDisconnect: () => {}, onOverflow: () => {},
  });
  await transport.connect({ authorizedOnly: true });
  await transport.close();
  await transport.connect({ authorizedOnly: true });
  await transport.close();
  assert.equal(target.opens, 2);
  assert.equal(target.closes, 2);
  assert.equal(other.opens, 0);
  assert.equal(pickerCalls, 0);
});

test("authorized connection rejects no or ambiguous ports without picker or opening anything", async () => {
  for (const ports of [[], [fakePort(), fakePort()], [fakePort({ usbVendorId: 123, usbProductId: 456 })]]) {
    let pickerCalls = 0;
    const transport = new SerialTransport({ getPorts: async () => ports, requestPort: async () => { pickerCalls++; } }, {});
    await assert.rejects(transport.connect({ authorizedOnly: true }), /Připojit USB/);
    assert.equal(pickerCalls, 0);
    assert.equal(ports.every(port => port.opens === 0), true);
  }
});

async function calibrationConnected(f) {
  const connect = f.tool("connect");
  await tick();
  f.ack("READY PWM FULL_OFF; calibration=v2 calibration_min_us=500 calibration_max_us=2400");
  f.ack("STOP PWM FULL_OFF");
  assert.equal((await connect).isError, false);
  const arm = f.tool("arm");
  f.ack("ARMED for one pulse, 10 seconds");
  assert.equal((await arm).isError, false);
}

test("calibration MCP schema enforces support and strict bounds without altering legacy pulse", async () => {
  const f = await fixture();
  const schema = f.definitions.get("servo_bench_calibrate").inputSchema;
  assert.equal(schema.additionalProperties, false);
  assert.equal(schema.properties.durationMs.maximum, 3000);
  assert.equal(schema.properties.pulseWidthUs.minimum, 1);
  assert.equal(schema.properties.pulseWidthUs.maximum, 9999);
  assert.equal((await f.tool("calibrate", { channel: 0, pulseWidthUs: 1500, durationMs: 1500 })).isError, true);
  await calibrationConnected(f);
  const before = f.writes.length;
  for (const input of [
    { channel: 0, pulseWidthUs: 499, durationMs: 1500 }, { channel: 0, pulseWidthUs: 2401, durationMs: 1500 },
    { channel: 0, pulseWidthUs: 1500, durationMs: 499 }, { channel: 0, pulseWidthUs: 1500, durationMs: 3001 },
    { channel: 6, pulseWidthUs: 1500, durationMs: 1500 }, { channel: 0, pulseWidthUs: 1500, durationMs: "1500" },
    { channel: 0, pulseWidthUs: 1500, durationMs: 1500, command: "arm" },
  ]) assert.equal((await f.tool("calibrate", input)).isError, true);
  assert.equal(f.writes.length, before);
  f.registration.cleanup();
});

test("maximum calibration waits past legacy deadline and reports only DONE for requested duration", async () => {
  const f = await fixture();
  await calibrationConnected(f);
  const move = f.tool("calibrate", { channel: 5, pulseWidthUs: 2400, durationMs: 3000 });
  await tick();
  assert.equal(f.writes.at(-1), "cal 5 2400 3000\n");
  f.ack("PULSE nominal 3000 ms; calibration; timeout/Stop writes PCA FULL_OFF");
  let settled = false;
  move.then(() => { settled = true; });
  f.expire(4000);
  await tick();
  assert.equal(settled, false);
  assert.equal((await f.tool("pulse", { channel: 0, pulseWidthUs: 1500 })).isError, true);
  f.ack("DONE PWM FULL_OFF; arm again for another pulse");
  const result = payload(await move);
  assert.equal(result.ok, true);
  assert.match(result.message, /3000 ms/);
  assert.equal(result.state.lastCompleted.pulseWidthUs, 2400);
  assert.equal(f.writes.filter(command => command.startsWith("cal ")).length, 1);
});

test("calibration timeout, abort and manual Stop never report a completed target or retry", async () => {
  for (const interruption of ["timeout", "abort", "manualStop"]) {
    const f = await fixture();
    await calibrationConnected(f);
    const abort = new AbortController();
    const move = f.tool("calibrate", { channel: 0, pulseWidthUs: 1200, durationMs: 1500 }, { signal: abort.signal });
    if (interruption === "timeout") f.expire(6000);
    if (interruption === "abort") abort.abort();
    if (interruption === "manualStop") await f.controller.stop();
    assert.equal((await move).isError, true);
    await tick();
    f.ack("DONE PWM FULL_OFF; arm again for another pulse");
    assert.equal(f.controller.state.lastCompleted, null);
    assert.equal(f.controller.capabilities.pulse, false);
    assert.equal(f.writes.filter(command => command.startsWith("cal ")).length, 1);
    assert.equal(f.writes.at(-1), "!\n");
  }
});

test("real controller ACK timeout requests exactly one safety Stop before the 6s tool limit", async () => {
  const f = await fixture();
  await calibrationConnected(f);
  const move = f.tool("calibrate", { channel: 0, pulseWidthUs: 1800, durationMs: 3000 });
  f.ack("PULSE nominal 3000 ms; calibration; timeout/Stop writes PCA FULL_OFF");
  f.expire(4500);
  const response = payload(await move);
  assert.equal(response.ok, false);
  assert.equal(response.state.errorKind, "ack_timeout");
  assert.doesNotMatch(response.message, /Firmware potvrdil|PWM FULL_OFF potvrzeno/);
  await tick();
  assert.equal(f.writes.filter(command => command.startsWith("cal ")).length, 1);
  assert.equal(f.writes.filter(command => command === "!\n").length, 2);
  assert.equal(f.writes.at(-1), "!\n");
  f.expire(6000);
  await tick();
  assert.equal(f.writes.filter(command => command === "!\n").length, 2);
  f.ack("DONE PWM FULL_OFF; arm again for another pulse");
  assert.equal(f.controller.state.lastCompleted, null);
});

test("hardware fault during calibration never triggers a retry or automatic Stop", async () => {
  const f = await fixture();
  await calibrationConnected(f);
  const move = f.tool("calibrate", { channel: 0, pulseWidthUs: 1800, durationMs: 1500 });
  f.ack("FAULT PWM_OFF_FAILED; cut servo power");
  assert.equal((await move).isError, true);
  await tick();
  assert.equal(f.writes.filter(command => command.startsWith("cal ")).length, 1);
  assert.equal(f.writes.filter(command => command === "!\n").length, 1);
  assert.equal(f.controller.state.fault, true);
});

import assert from "node:assert/strict";
import test from "node:test";
import { readFile } from "node:fs/promises";
import { mountBenchUi } from "../panel-ui.mjs";

const ids = ["connection-actions", "pulse-actions", "calibration-actions", "auto-actions", "stop-actions", "calibration-fields", "log-empty", "connect", "disconnect", "connection", "support", "channel", "pulse-width", "arm",
  "pulse", "stop", "state", "error", "readback", "log", "mcp",
  "calibration-support", "cal-width", "cal-duration", "cal-range", "cal-minus", "cal-plus", "cal-1200", "cal-1500", "cal-1800",
  "cal-arm", "cal-move", "cal-validation", "profile-title", "profile-label", "profile-save-label", "profile-values",
  "profile-min", "profile-center", "profile-max", "profile-status", "profile-error", "profile-export", "profile-json", "profile-clear", "cal-state",
  "auto-context", "auto-min", "auto-max", "auto-state", "auto-candidate", "auto-save", "noise", "noise-warning"];

test("shared markup preserves every panel hook and calibrated input boundary", async () => {
  const fragments = [];
  mountBenchUi({ getElementById: () => ({ set innerHTML(markup) { fragments.push(markup); } }) });
  const html = await readFile(new URL("../index.html", import.meta.url), "utf8");
  const rendered = html + fragments.join("");
  for (const id of ids) {
    assert.equal([...rendered.matchAll(new RegExp(`\\bid="${id}"`, "g"))].length, 1, `unique control hook ${id}`);
  }
  assert.match(fragments.join(""), /min="1000" max="2000"/);
  assert.match(fragments.join(""), /min="500" max="3000"/);
  assert.match(fragments.join(""), /id="stop" disabled>Stop<\/button>/);
});

function fakePage(serial, modelContext, localStorage) {
  const elements = Object.fromEntries(ids.map(id => [id, {
    get textContent() { return this._text || ""; },
    set textContent(value) { this._text = value; },
    set innerHTML(value) { this.markup = value; this._text = value.replace(/<[^>]*>/g, ""); },
    disabled: false, value: id === "channel" ? "0" : "1500", hidden: false,
    listeners: {}, addEventListener(event, listener) { this.listeners[event] = listener; },
    async click() { if (!this.disabled) await this.listeners.click?.(); },
  }]));
  const originalNavigator = Object.getOwnPropertyDescriptor(globalThis, "navigator");
  Object.defineProperty(globalThis, "navigator", { configurable: true, value: serial ? { serial } : {} });
  const windowListeners = new Map();
  const documentListeners = new Map();
  globalThis.window = { isSecureContext: true, localStorage, addEventListener: (name, listener) => windowListeners.set(name, listener) };
  globalThis.document = { getElementById: id => elements[id], modelContext, visibilityState: "visible",
    addEventListener: (name, listener) => documentListeners.set(name, listener) };
  return { elements, windowListeners, documentListeners, restore() {
    delete globalThis.window;
    delete globalThis.document;
    if (originalNavigator) Object.defineProperty(globalThis, "navigator", originalNavigator);
    else delete globalThis.navigator;
  } };
}

test("real panel button handlers require explicit Arm and Pulse and disconnect flushes Stop", async () => {
  let nextRead;
  let closed = false;
  const writes = [];
  const reader = {
    read: () => new Promise(resolve => { nextRead = resolve; }),
    cancel: async () => nextRead({ done: true }), releaseLock: () => {},
  };
  const writer = { write: async bytes => writes.push(new TextDecoder().decode(bytes)),
    abort: async () => {}, releaseLock: () => {} };
  const port = { open: async () => {}, setSignals: async () => {},
    readable: { getReader: () => reader }, writable: { getWriter: () => writer },
    close: async () => { closed = true; } };
  const page = fakePage({ requestPort: async () => port });
  const el = page.elements;
  const tick = () => new Promise(resolve => setImmediate(resolve));
  const receive = async line => { nextRead({ value: new TextEncoder().encode(`${line}\n`), done: false }); await tick(); };
  try {
    await import(`../panel.mjs?buttons=${Date.now()}`);
    assert.equal(el.arm.disabled, true);
    assert.equal(el.pulse.disabled, true);
    await el.connect.click();
    assert.deepEqual(writes, ["!\n"]);
    assert.equal(el.arm.disabled, true);
    await receive("SERVO_BENCH STOP PWM FULL_OFF");
    assert.equal(el.arm.disabled, false);
    assert.equal(el.pulse.disabled, true);
    await el.arm.click();
    await tick();
    assert.equal(el.pulse.disabled, true);
    await receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
    assert.equal(el.pulse.disabled, false);
    await el.pulse.click();
    await tick();
    assert.equal(el.pulse.disabled, true);
    assert.deepEqual(writes, ["!\n", "arm\n", "pulse 0 1500\n"]);
    await receive("SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse");
    assert.equal(el.arm.disabled, false);
    await el.disconnect.click();
    assert.equal(writes.at(-1), "!\n");
    assert.equal(closed, true);
    assert.equal(el.arm.disabled && el.pulse.disabled && el.stop.disabled, true);
    assert.equal(el.connect.disabled, false);
  } finally {
    page.restore();
  }
});

test("unsupported Web Serial disables USB entry and explains the browser requirement", async () => {
  const page = fakePage();
  try {
    await import(`../panel.mjs?unsupported=${Date.now()}`);
    assert.equal(page.elements.connect.disabled, true);
    assert.match(page.elements.support.textContent, /Chrome nebo Edge/);
    assert.equal(page.elements.pulse.disabled, true);
  } finally {
    page.restore();
  }
});

test("registered tools use the live panel controller and authorized serial transport", async () => {
  const tools = new Map();
  const writes = [];
  let nextRead;
  let pickerCalls = 0;
  const reader = {
    read: () => new Promise(resolve => { nextRead = resolve; }),
    cancel: async () => nextRead({ done: true }), releaseLock: () => {},
  };
  const writer = { write: async bytes => writes.push(new TextDecoder().decode(bytes)),
    abort: async () => {}, releaseLock: () => {} };
  const port = { getInfo: () => ({ usbVendorId: 0x1A86, usbProductId: 0x55D3 }),
    open: async () => {}, setSignals: async () => {}, close: async () => {},
    readable: { getReader: () => reader }, writable: { getWriter: () => writer } };
  const page = fakePage({ getPorts: async () => [port],
    requestPort: async () => { pickerCalls++; throw new Error("Picker must not run from a tool"); } },
    { registerTool: async tool => { tools.set(tool.name, tool); } });
  const tick = () => new Promise(resolve => setImmediate(resolve));
  const receive = async line => {
    nextRead({ value: new TextEncoder().encode(`${line}\n`), done: false });
    await tick();
  };
  const unpack = result => JSON.parse(result.content[0].text);
  const call = (suffix, input = {}) => tools.get(`servo_bench_${suffix}`).execute(input);
  try {
    await import(`../panel.mjs?mcp-integration=${Date.now()}`);
    await tick();
    assert.equal(tools.size, 8);
    assert.match(page.elements.mcp.textContent, /8/);
    assert.equal(unpack(await call("get_state")).state.powerState, "unknown");
    assert.equal((await call("pulse", { channel: 0, pulseWidthUs: 1600 })).isError, true);
    assert.deepEqual(writes, []);

    const connecting = call("connect");
    await tick();
    assert.deepEqual(writes, ["!\n"]);
    await receive("SERVO_BENCH STOP PWM FULL_OFF");
    assert.equal(unpack(await connecting).ok, true);
    assert.equal(pickerCalls, 0);
    assert.equal(page.elements.arm.disabled, false);

    const arming = call("arm");
    await tick();
    await receive("SERVO_BENCH ARMED for one pulse, 10 seconds");
    assert.equal(unpack(await arming).ok, true);
    const pulsing = call("pulse", { channel: 0, pulseWidthUs: 1600 });
    await tick();
    await receive("SERVO_BENCH PCA_READBACK ch=0 mode1=0x20 mode2=0x04 prescale=121 on=0 off=328 full_on=0 full_off=0 expected_off=328 match=1");
    await receive("SERVO_BENCH PULSE nominal 500 ms; timeout/Stop writes PCA FULL_OFF");
    await receive("SERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse");
    const result = unpack(await pulsing);
    assert.equal(result.ok, true);
    assert.equal(result.state.readback.off, 328);
    assert.equal(result.state.positionMeasured, false);
    assert.equal(unpack(await call("get_log")).log.some(line => line.includes("DONE PWM FULL_OFF")), true);
    assert.equal(unpack(await call("disconnect")).ok, true);
    assert.deepEqual(writes, ["!\n", "arm\n", "pulse 0 1600\n", "!\n"]);
  } finally {
    page.restore();
  }
});

test("calibration edits never move, explicit Arm/move waits DONE, profiles save only tested targets", async () => {
  let nextRead;
  const writes = [];
  const stored = new Map();
  const localStorage = { getItem: key => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, value) };
  const reader = { read: () => new Promise(resolve => { nextRead = resolve; }),
    cancel: async () => nextRead({ done: true }), releaseLock: () => {} };
  const writer = { write: async bytes => writes.push(new TextDecoder().decode(bytes)), abort: async () => {}, releaseLock: () => {} };
  const port = { open: async () => {}, setSignals: async () => {},
    readable: { getReader: () => reader }, writable: { getWriter: () => writer }, close: async () => {} };
  const page = fakePage({ requestPort: async () => port }, undefined, localStorage);
  const el = page.elements;
  const receive = async line => { nextRead({ value: new TextEncoder().encode(`SERVO_BENCH ${line}\n`) }); await new Promise(resolve => setImmediate(resolve)); };
  try {
    await import(`../panel.mjs?calibration-ui=${Date.now()}`);
    await el["cal-1200"].click();
    await el["cal-plus"].click();
    assert.equal(el["cal-width"].value, "1225");
    assert.deepEqual(writes, []);
    assert.equal(el["cal-move"].disabled, true);
    assert.equal(el["profile-min"].disabled, true);
    await el.connect.click();
    await receive("READY PWM FULL_OFF; calibration=v1");
    await receive("STOP PWM FULL_OFF");
    assert.equal(el["cal-arm"].disabled, false);
    await el["cal-1200"].click();
    await el["cal-arm"].click();
    await receive("ARMED for one pulse, 10 seconds");
    assert.equal(el["cal-move"].disabled, false);
    await el["cal-move"].click();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(writes.at(-1), "cal 0 1200 1500\n");
    assert.equal(el["profile-min"].disabled, true);
    await receive("PULSE nominal 1500 ms; calibration; timeout/Stop writes PCA FULL_OFF");
    await receive("DONE PWM FULL_OFF; arm again for another pulse");
    assert.equal(el["profile-min"].disabled, false);
    await el["profile-min"].click();
    assert.match(el["profile-values"].textContent, /Min 1200/);
    await el["profile-export"].click();
    assert.equal(JSON.parse(el["profile-json"].value).channels[0].min, 1200);
    const beforeClear = writes.length;
    await el["profile-clear"].click();
    assert.equal(writes.length, beforeClear);
    assert.match(el["profile-values"].textContent, /Min —/);
    await el["cal-1500"].click();
    assert.equal(el["profile-center"].disabled, true);
    const beforeStop = writes.length;
    await el.stop.click();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(writes.length, beforeStop + 1);
    assert.equal(el["profile-min"].disabled, true);
    await receive("STOP PWM FULL_OFF");
    await el.disconnect.click();
  } finally { page.restore(); }
});

async function assistedPage() {
  let now = 0;
  let timerId = 0;
  const timers = new Map();
  const originalSetTimeout = globalThis.setTimeout;
  const originalClearTimeout = globalThis.clearTimeout;
  const originalPerformance = Object.getOwnPropertyDescriptor(globalThis, "performance");
  globalThis.setTimeout = (callback, delay) => { timers.set(++timerId, { callback, at: now + delay }); return timerId; };
  globalThis.clearTimeout = id => timers.delete(id);
  Object.defineProperty(globalThis, "performance", { configurable: true, value: { now: () => now } });
  let nextRead;
  let initialStop = true;
  const writes = [];
  const tools = new Map();
  const channels = Array.from({ length: 6 }, (_, channel) => ({ label: `Servo ${channel}`, min: null, center: channel === 0 ? 1500 : null, max: null }));
  let saved = JSON.stringify({ version: 1, channels });
  const storage = { getItem: () => saved, setItem: (_, value) => { saved = value; } };
  const feed = text => nextRead({ value: new TextEncoder().encode(text), done: false });
  const reader = { read: () => new Promise(resolve => { nextRead = resolve; }),
    cancel: async () => nextRead({ done: true }), releaseLock: () => {} };
  const writer = { write: async bytes => {
    const command = new TextDecoder().decode(bytes);
    writes.push(command);
    if (command === "!\n") {
      feed(`${initialStop ? "SERVO_BENCH READY PWM FULL_OFF; calibration=v1\n" : ""}SERVO_BENCH STOP PWM FULL_OFF\n`);
      initialStop = false;
    } else if (command === "arm\n") feed("SERVO_BENCH ARMED for one pulse, 10 seconds\n");
    else if (command.startsWith("cal ")) feed("SERVO_BENCH PULSE nominal 1500 ms; calibration\nSERVO_BENCH DONE PWM FULL_OFF; arm again for another pulse\n");
  }, abort: async () => {}, releaseLock: () => {} };
  const port = { open: async () => {}, setSignals: async () => {}, getInfo: () => ({ usbVendorId: 0x1A86, usbProductId: 0x55D3 }),
    readable: { getReader: () => reader }, writable: { getWriter: () => writer }, close: async () => {} };
  const page = fakePage({ requestPort: async () => port, getPorts: async () => [port] }, {
    registerTool: async definition => tools.set(definition.name, definition),
  }, storage);
  const tick = () => new Promise(resolve => setImmediate(resolve));
  const api = {
    ...page, writes, tools, tick,
    profiles: () => JSON.parse(saved).channels,
    receive: async line => { feed(`SERVO_BENCH ${line}\n`); await tick(); },
    state: async () => JSON.parse((await tools.get("servo_bench_get_state").execute({})).content[0].text).state,
    advance: async duration => {
      now += duration;
      for (const [id, timer] of [...timers]) {
        if (timer.at <= now) { timers.delete(id); timer.callback(); }
      }
      await tick();
    },
    cleanup: async () => {
      try { await page.elements.disconnect.click(); }
      finally {
        page.restore();
        globalThis.setTimeout = originalSetTimeout;
        globalThis.clearTimeout = originalClearTimeout;
        Object.defineProperty(globalThis, "performance", originalPerformance);
      }
    },
  };
  await import(`../panel.mjs?assisted=${Date.now()}-${Math.random()}`);
  await tick();
  return api;
}

test("assisted run is explicit and blocks manual and MCP motion while retaining read tools and Stop", async () => {
  const page = await assistedPage();
  const el = page.elements;
  try {
    await el.connect.click();
    await page.tick();
    assert.equal(page.writes.some(command => command.startsWith("cal ")), false);
    assert.equal(el["auto-min"].disabled, false);
    await el["auto-min"].click();
    await page.tick();
    const state = await page.state();
    assert.equal(state.autoCalibration.active, true);
    assert.deepEqual([state.capabilities.arm, state.capabilities.pulse, state.capabilities.calibrate], [false, false, false]);
    const before = page.writes.length;
    for (const name of ["arm", "pulse", "cal-arm", "cal-move", "profile-clear"]) await el[name].listeners.click();
    for (const [name, input] of [["arm", {}], ["pulse", { channel: 0, pulseWidthUs: 1500 }],
      ["calibrate", { channel: 0, pulseWidthUs: 1500, durationMs: 1500 }]]) {
      assert.equal((await page.tools.get(`servo_bench_${name}`).execute(input)).isError, true);
    }
    assert.equal(page.writes.length, before);
    assert.equal(page.profiles()[0].center, 1500);
    const stop = await page.tools.get("servo_bench_stop").execute({});
    assert.equal(stop.isError, false);
    assert.equal((await page.state()).autoCalibration.active, false);
    const stopped = page.writes.length;
    await page.advance(10000);
    assert.equal(page.writes.length, stopped);
  } finally { await page.cleanup(); }
});

test("noise proposes only a prior completed step, waits fresh Stop ACK and requires explicit profile approval", async () => {
  const page = await assistedPage();
  const el = page.elements;
  try {
    await el.connect.click();
    await page.tick();
    await el["auto-min"].click();
    await page.tick();
    await page.advance(3000);
    await page.advance(3000);
    assert.deepEqual(page.writes.filter(command => command.startsWith("cal ")), ["cal 0 1500 1500\n", "cal 0 1475 1500\n", "cal 0 1450 1500\n"]);
    await el.noise.click();
    await page.tick();
    const state = await page.state();
    assert.equal(state.autoCalibration.active, false);
    assert.equal(state.autoCalibration.candidateUs, 1475);
    assert.equal(state.autoCalibration.stopConfirmed, true);
    assert.equal(el["noise-warning"].hidden, false);
    assert.equal(page.profiles()[0].min, null);
    assert.equal(el["auto-save"].disabled, false);
    await el["auto-save"].click();
    assert.equal(page.profiles()[0].min, 1475);
    assert.equal(page.profiles()[0].center, 1500);
    assert.equal((await page.state()).autoCalibration.candidateUs, null);
    assert.match((await page.state()).autoCalibration.message, /Návrh uložen po potvrzení obsluhou/);
  } finally { await page.cleanup(); }
});

test("ordinary Stop, hidden page, pagehide and MCP disconnect cancel assisted work without resuming", async () => {
  for (const mode of ["stop", "hidden", "pagehide", "disconnect"]) {
    const page = await assistedPage();
    const el = page.elements;
    try {
      await el.connect.click();
      await page.tick();
      await el["auto-max"].click();
      await page.tick();
      if (mode === "stop") await el.stop.click();
      if (mode === "hidden") {
        document.visibilityState = "hidden";
        page.documentListeners.get("visibilitychange")();
      }
      if (mode === "pagehide") page.windowListeners.get("pagehide")();
      if (mode === "disconnect") assert.equal((await page.tools.get("servo_bench_disconnect").execute({})).isError, false);
      await page.tick();
      assert.equal((await page.state()).autoCalibration.active, false);
      assert.equal((await page.state()).autoCalibration.candidateUs, null);
      const before = page.writes.length;
      document.visibilityState = "visible";
      page.documentListeners.get("visibilitychange")();
      await page.advance(10000);
      assert.equal(page.writes.length, before);
    } finally { await page.cleanup(); }
  }
});

test("visibility cleanup after a hardware fault never retries firmware I2C", async () => {
  const page = await assistedPage();
  try {
    await page.elements.connect.click();
    await page.tick();
    await page.elements["auto-min"].click();
    await page.tick();
    await page.receive("FAULT PWM_OFF_FAILED; cut servo power");
    const state = await page.state();
    assert.equal(state.fault, true);
    assert.equal(state.autoCalibration.active, false);
    const before = page.writes.length;
    document.visibilityState = "hidden";
    page.documentListeners.get("visibilitychange")();
    page.windowListeners.get("pagehide")();
    await page.advance(10000);
    assert.equal(page.writes.length, before);
  } finally { await page.cleanup(); }
});

test("inactive proposal is discarded on Stop, changed context, manual motion, reset, fault or disconnect", async () => {
  for (const mode of ["stop", "channel", "profile", "manual", "reset", "fault", "disconnect", "hidden"]) {
    const page = await assistedPage();
    const el = page.elements;
    try {
      await el.connect.click();
      await page.tick();
      await el["auto-min"].click();
      await page.tick();
      await page.advance(3000);
      await page.advance(3000);
      await el.noise.click();
      await page.tick();
      assert.equal(el["auto-save"].disabled, false);
      if (mode === "stop") await el.stop.click();
      if (mode === "channel") { el.channel.value = "1"; el.channel.listeners.change(); }
      if (mode === "profile") { el["profile-label"].value = "Changed"; el["profile-label"].listeners.input(); }
      if (mode === "manual") await el["cal-arm"].click();
      if (mode === "reset") await page.receive("READY PWM FULL_OFF; calibration=v1");
      if (mode === "fault") await page.receive("FAULT PWM_OFF_FAILED; cut servo power");
      if (mode === "disconnect") await el.disconnect.click();
      if (mode === "hidden") { document.visibilityState = "hidden"; page.documentListeners.get("visibilitychange")(); }
      await page.tick();
      assert.equal((await page.state()).autoCalibration.candidateUs, null, mode);
      assert.equal(el["auto-save"].disabled, true, mode);
      assert.equal(page.profiles()[0].min, null, mode);
      assert.equal(page.profiles()[0].center, 1500, mode);
    } finally { await page.cleanup(); }
  }
});

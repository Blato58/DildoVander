import assert from "node:assert/strict";
import test from "node:test";

const ids = ["connect", "disconnect", "connection", "support", "channel", "pulse-width", "arm",
  "pulse", "stop", "state", "error", "readback", "log"];

function fakePage(serial) {
  const elements = Object.fromEntries(ids.map(id => [id, {
    disabled: false, value: id === "channel" ? "0" : "1500", textContent: "", hidden: false,
    listeners: {}, addEventListener(event, listener) { this.listeners[event] = listener; },
    async click() { if (!this.disabled) await this.listeners.click?.(); },
  }]));
  const originalNavigator = Object.getOwnPropertyDescriptor(globalThis, "navigator");
  Object.defineProperty(globalThis, "navigator", { configurable: true, value: serial ? { serial } : {} });
  globalThis.window = { isSecureContext: true };
  globalThis.document = { getElementById: id => elements[id] };
  return { elements, restore() {
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

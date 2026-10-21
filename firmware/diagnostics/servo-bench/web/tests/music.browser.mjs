// Repeatable real-media and responsive regression. All UART is an in-page fake.
// Optional SERVO_MUSIC_FILE runs the same checks against a local MP3 without copying it.
import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { mkdtemp, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
const { chromium } = createRequire(new URL("../../../../../mobilni-aplikace/package.json", import.meta.url))("playwright");
const temporary = await mkdtemp(path.join(tmpdir(), "servo-music-"));
const root = fileURLToPath(new URL("../../../../../", import.meta.url));
const server = spawn("python", ["firmware/diagnostics/servo-bench/tools/serve_panel.py", "--port", "18766"], { cwd: root, windowsHide: true });
let browser;
try {
  await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(Error("Panel server did not start")), 10000);
    server.once("error", reject); server.once("exit", code => reject(Error(`Panel server exited ${code}`)));
    server.stdout.on("data", chunk => { if (chunk.toString().includes("Servo bench:")) { clearTimeout(timeout); resolve(); } });
  });
  let file = process.env.SERVO_MUSIC_FILE;
  if (!file) {
    const rate = 16000; const seconds = 8; const samples = rate * seconds;
    const wav = Buffer.alloc(44 + samples * 2);
    wav.write("RIFF"); wav.writeUInt32LE(wav.length - 8, 4); wav.write("WAVEfmt ", 8);
    wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
    wav.writeUInt32LE(rate, 24); wav.writeUInt32LE(rate * 2, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34);
    wav.write("data", 36); wav.writeUInt32LE(samples * 2, 40);
    for (let index = 0; index < samples; index++) {
      const time = index / rate;
      wav.writeInt16LE(Math.round(Math.sin(time * 2 * Math.PI * 220) * (time % 0.5 < 0.08 ? 24000 : 6000)), 44 + index * 2);
    }
    file = path.join(temporary, "pulse.wav"); await writeFile(file, wav);
  }
  browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL || "chrome", args: ["--mute-audio"] });
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 }, reducedMotion: "reduce" });
  const errors = [];
  const failedResources = [];
  page.on("pageerror", error => errors.push(error.message));
  page.on("console", message => { if (message.type() === "error" && /Content Security Policy|Refused to|EvalError|Illegal invocation/i.test(message.text())) errors.push(message.text()); });
  page.on("response", response => { if (response.status() >= 400 && !response.url().endsWith("/favicon.ico")) failedResources.push(response.url()); });
  await page.addInitScript(() => {
    window.benchWrites = [];
    let nextRead; const queue = []; let pulseTimer;
    const feed = text => { const value = { value: new TextEncoder().encode(text), done: false }; if (nextRead) { const resolve = nextRead; nextRead = null; resolve(value); } else queue.push(value); };
    const port = {
      open: async () => {}, close: async () => {}, setSignals: async () => {},
      readable: { getReader: () => ({ read: () => queue.length ? Promise.resolve(queue.shift()) : new Promise(resolve => { nextRead = resolve; }), cancel: async () => { nextRead?.({ done: true }); }, releaseLock: () => {} }) },
      writable: { getWriter: () => ({ write: async bytes => {
        const text = new TextDecoder().decode(bytes); window.benchWrites.push(text);
        if (text.includes("!")) { clearTimeout(pulseTimer); feed("READY PWM FULL_OFF calibration=v2 calibration_min_us=600 calibration_max_us=2300\nSTOP PWM FULL_OFF\n"); }
        else if (text === "arm\n") feed("ARMED for one pulse\n");
        else if (text.startsWith("cal ")) { feed("PULSE nominal 500 ms calibration\n"); pulseTimer = setTimeout(() => feed("DONE PWM FULL_OFF\n"), 500); }
      }, abort: async () => {}, releaseLock: () => {} }) },
    };
    Object.defineProperty(navigator, "serial", { configurable: true, value: { requestPort: async () => port, addEventListener: () => {} } });
  });
  await page.goto("http://127.0.0.1:18766/");
  await page.locator("#music-file").setInputFiles(file);
  try { await page.locator("#music-play:enabled").waitFor({ timeout: 30000 }); }
  catch (error) { console.error("Music load:", await page.locator("#music-status").textContent(), errors); throw error; }
  console.log("Local analysis:", await page.locator("#music-summary").textContent());
  assert.deepEqual(await page.evaluate(() => window.benchWrites), []);
  await page.locator("#music-play").click();
  await page.waitForFunction(() => document.getElementById("music-audio").currentTime > 0.15);
  await page.locator("#music-pause").click();
  const paused = await page.locator("#music-audio").evaluate(audio => ({ paused: audio.paused, time: audio.currentTime }));
  assert(paused.paused && paused.time > 0);
  assert.deepEqual(await page.evaluate(() => window.benchWrites), []);
  await page.locator("#music-stop").click();
  assert.equal(await page.locator("#music-audio").evaluate(audio => audio.currentTime), 0);
  await page.locator("#connect").click();
  await page.locator("#cal-arm:enabled").waitFor();
  await page.locator("#channel").selectOption("2");
  await page.locator("#music-dance").check(); await page.locator("#music-consent").check();
  await page.locator("#music-play").click();
  await page.waitForFunction(() => window.benchWrites.some(value => value.startsWith("cal 2 ")), { timeout: 15000 });
  assert(await page.locator("#channel").isDisabled());
  assert(await page.locator("#cal-arm").isDisabled());
  await page.locator("#music-pause").click();
  assert.equal(await page.evaluate(() => window.benchWrites.at(-1)), "!\n");
  await page.locator("#cal-arm:enabled").waitFor();
  await page.locator("#music-consent").check(); await page.locator("#music-play").click();
  await page.waitForFunction(() => window.benchWrites.filter(value => value.startsWith("cal 2 ")).length >= 2);
  await page.locator("#stop").click();
  assert(await page.locator("#music-audio").evaluate(audio => audio.paused));
  assert.equal(await page.locator("#music-audio").evaluate(audio => audio.currentTime), 0);
  await page.locator("#music-title").scrollIntoViewIfNeeded();
  const screenshots = process.env.SERVO_SCREENSHOTS || temporary;
  await page.locator('section[aria-labelledby="music-title"]').screenshot({ path: path.join(screenshots, "servo-music-desktop.png") });
  await page.setViewportSize({ width: 390, height: 844 });
  for (const theme of ["lord-voldemort", "olivander"]) {
    await page.evaluate(theme => globalThis.DildoVanderTheme.setTheme(theme), theme);
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    await page.locator('section[aria-labelledby="music-title"]').screenshot({ path: path.join(screenshots, `servo-music-mobile-${theme}.png`) });
  }
  assert.deepEqual(errors, []);
  assert.deepEqual(failedResources, []);
  console.log("PASS: real audio decode/listen/pause/stop, fake UART dance/pause/global Stop, 1440x1000 and 390x844, both themes.");
} finally {
  await browser?.close(); server.kill(); await rm(temporary, { recursive: true, force: true });
}

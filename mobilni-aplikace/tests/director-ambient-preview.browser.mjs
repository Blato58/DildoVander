// Real decode/playback graph against an isolated fixture; never calls an AI provider.
import assert from 'node:assert/strict';
import { preview } from 'vite';
import { chromium } from 'playwright';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const server = await preview({ root: fileURLToPath(new URL('..', import.meta.url)), preview: { host: '127.0.0.1', port: 0 } });
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const output = path.join(tmpdir(), 'dildovander-ambient-preview'); await mkdir(output, { recursive: true });
const wav = Buffer.alloc(44 + 6400 * 2);
wav.write('RIFF'); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8);
wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22);
wav.writeUInt32LE(32000, 24); wav.writeUInt32LE(64000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34);
wav.write('data', 36); wav.writeUInt32LE(wav.length - 44, 40);
for (let index = 0; index < 6400; index++) wav.writeInt16LE(Math.round(Math.sin(index * 2 * Math.PI * 440 / 32000) * 3000), 44 + index * 2);
const calls = [], errors = [];
let browser, fail = false;
try {
  try { browser = await chromium.launch(); }
  catch (error) { if (process.platform !== 'win32') throw error; browser = await chromium.launch({ channel: 'chrome' }); }
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' });
    await context.addInitScript(() => {
      window.__ambientPlayback = [];
      const create = AudioContext.prototype.createBufferSource;
      AudioContext.prototype.createBufferSource = function (...args) {
        const source = create.apply(this, args), start = source.start.bind(source), audio = this;
        source.start = (...values) => {
          const pcm = source.buffer?.getChannelData(0);
          window.__ambientPlayback.push({ running: audio.state === 'running', nonzero: pcm?.some(value => Math.abs(value) > .001), duration: source.buffer?.duration });
          return start(...values);
        };
        return source;
      };
    });
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      const json = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
      assert.equal(url.origin, origin, 'preview contacted external service');
      if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'fixture-owner', displayName: 'Obsluha', roles: ['Owner'] }, csrfToken: 'fixture-csrf', origin });
      if (url.pathname === '/app/v1/account') return json({ devices: [{ deviceId: 'fixture-device', canOperate: true }], grants: [], sessions: [], associations: [], passkeys: [], passkeyCount: 1 });
      if (!url.pathname.startsWith('/v1/show/')) return route.continue();
      calls.push({ path: url.pathname, method: request.method() });
      assert.equal(request.headers()['x-dv-device-id'], 'fixture-device');
      if (url.pathname === '/v1/show/devices/fixture-device') return json({ deviceId: 'fixture-device', eveningId: 'fixture-evening', revision: 1, phase: 'idle', mood: { dominant: 'amused', locked: false }, tone: 'dry', tempo: 'standard', quiet: false, ambientEnabled: true, readyMask: 63, selectionLocked: false, updatedAt: new Date().toISOString() });
      if (url.pathname === '/v1/show/devices/fixture-device/ambient-previews') {
        assert.equal(request.method(), 'POST');
        assert.equal(request.headers()['x-dv-csrf'], 'fixture-csrf');
        if (fail) return json({ code: 'preview_unavailable' }, 503);
        const { operationId } = request.postDataJSON();
        return json({ operationId, status: 'pending', text: '', synthetic: true }, 202);
      }
      if (url.pathname.startsWith('/v1/show/devices/fixture-device/ambient-previews/')) {
        assert.equal(request.method(), 'GET');
        return json({ operationId: url.pathname.split('/').at(-1), status: 'ready', text: 'Syntetická zvuková fixture.', synthetic: true, wavBase64: wav.toString('base64') });
      }
      throw new Error(`Unexpected Show request: ${request.method()} ${url.pathname}`);
    });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/suitcase/director`);
    await page.getByRole('button', { name: 'Načíst režii', exact: true }).click();
    const play = page.getByRole('button', { name: 'Přehrát ambient v telefonu', exact: true });
    await play.click();
    await page.getByText('Poslech v telefonu dokončen.', { exact: true }).waitFor();
    const playback = await page.evaluate(() => window.__ambientPlayback);
    assert.equal(playback.length, 1); assert.equal(playback[0].running, true); assert.equal(playback[0].nonzero, true);
    assert.ok(playback[0].duration > .19 && playback[0].duration < .21);
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    await page.locator('section[aria-labelledby="ambient-preview-title"]').screenshot({ path: path.join(output, `ambient-${viewport.width}.png`) });
    fail = true;
    await play.click(); await page.getByText('Hlasový výstup serveru není dostupný.', { exact: true }).waitFor();
    assert.equal((await page.evaluate(() => window.__ambientPlayback)).length, 1);
    fail = false;
    await context.close();
  }
  assert.deepEqual(errors, []);
  assert.equal(calls.some(call => call.path.endsWith('/commands') || call.path.includes('/device-cues')), false);
  console.log(`Ambient preview: real nonzero WAV decoded and naturally completed at desktop/mobile widths; error shown; no device commands, ACKs or provider calls. Screenshots: ${output}`);
} finally { await browser?.close(); await server.close(); }

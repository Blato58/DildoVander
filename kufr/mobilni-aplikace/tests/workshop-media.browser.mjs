/** Real Vue/browser HAL acceptance against labelled fake endpoints/devices only. */
import { chromium } from 'playwright';
import { createServer } from 'vite';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import assert from 'node:assert/strict';

const appRoot = fileURLToPath(new URL('..', import.meta.url));
const server = await createServer({ root: appRoot, server: { host: '127.0.0.1', port: 0, fs: { allow: [appRoot, path.resolve(appRoot, '../mechanika/final'), path.resolve(appRoot, '../design-system')] } } });
await server.listen();
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const BOOT = '00000000-0000-4000-8000-000000000001', SESSION = '00000000-0000-4000-8000-000000000002';
const requests = [], outputs = [], errors = [], external = [];
let sequence = 0, snapshotSequence = 0, active = false, photo = false, drained = false, inputCount = 0;
const frame = (kind, fields = {}) => ({ sessionId: SESSION, bootId: BOOT, epoch: '1', inputEpoch: '1', outputEpoch: '1', sequence: String(++sequence), kind, phase: kind, inputEnabled: ['listening', 'pcm', 'drain'].includes(kind), byteLength: 0, drainedRequested: false, ...fields });
const session = () => ({ sessionId: SESSION, bootId: BOOT, epoch: '1', inputEpoch: '1', outputEpoch: '1', phase: active ? 'listening' : 'completed', inputEnabled: active && photo, active });
function complete() { if (active && photo && drained && inputCount >= 2) { active = false; outputs.push(frame('completed')); } }
const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL, headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
try {
  const context = await browser.newContext({ permissions: ['microphone', 'camera'], viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) { if (url.protocol.startsWith('http')) external.push(request.url()); return route.abort(); }
    const pathname = url.pathname;
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'synthetic-owner', displayName: 'Syntetický testovací vlastník', roles: ['Owner'] }, csrfToken: 'synthetic-csrf', origin });
    if (!pathname.startsWith('/v1/twin/')) return route.continue();
    requests.push({ path: pathname, method: request.method(), body: request.postDataBuffer(), headers: request.headers() });
    if (pathname === '/v1/twin/instances') return json({ instances: [{ instanceId: 'sim-integration', mode: 'simulation', profile: 'integration', canControl: true }] });
    if (pathname === '/v1/twin/instances/sim-integration') return json({ status: 'online', ageMs: 0, receivedAtUtc: new Date().toISOString(), snapshot: { schemaVersion: 1, sequence: String(++snapshotSequence), timeMs: String(snapshotSequence * 750), revision: String(snapshotSequence), source: { instanceId: 'sim-integration', deviceId: 'synthetic', bootId: BOOT, mode: 'simulation', cadManifestRevision: null, hardwareProfile: 'B-effects' }, reported: { physicalVerification: 'unavailable', servo: [] }, model: { evidence: 'simulated', slots: [] }, trace: { events: [] }, capabilities: ['observe'] } });
    if (pathname.endsWith('/settings')) return json({ revision: '1', engine: 'fake', voice: 'marin', speed: 1, photoConsent: true, presentationVariant: 'main', availability: { fake: true, realtime: false, gptLive: false, mic: true, speaker: true, camera: true } });
    if (request.method() !== 'GET') assert.equal(request.headers()['x-dv-csrf'], 'synthetic-csrf');
    if (pathname.endsWith('/media/start')) { active = true; photo = false; drained = false; inputCount = 0; outputs.push(frame('photo_request', { text: 'Syntetická žádost o jednu fotku.' })); return json(session()); }
    if (pathname.endsWith('/media/photo')) {
      const bytes = request.postDataBuffer(); assert.ok(bytes.length > 0 && bytes.length <= 262144); assert.equal(bytes[0], 255); assert.equal(bytes[1], 216);
      photo = true; outputs.push(frame('listening'), frame('pcm', { byteLength: 4800, pcmBase64: Buffer.alloc(4800).toString('base64') }), frame('drain', { pcmBytes: '4800', drainedRequested: true }));
      return json({ accepted: true });
    }
    if (pathname.endsWith('/media/input')) { assert.equal(request.postDataBuffer().length, 9600); inputCount++; complete(); return json({ accepted: true }); }
    if (pathname.endsWith('/media/played')) { drained ||= JSON.parse(request.postData()).drained; complete(); return json({ accepted: true }); }
    if (pathname.endsWith('/media/output')) { const value = outputs.shift(); return value ? json(value) : route.fulfill({ status: 204 }); }
    if (pathname.endsWith('/media/state')) return json(session());
    if (pathname.endsWith('/media/cancel')) { active = false; return json({ accepted: true }); }
    return route.fulfill({ status: 404 });
  });
  await page.goto(origin + '/workshop?fixture=1');
  await page.getByText('SYNTETICKÁ UI FIXTURE', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Pozorování', exact: true }).click();
  await page.getByRole('button', { name: 'Načíst dostupné zdroje', exact: true }).click();
  await page.getByLabel('Konkrétní zdroj').selectOption('sim-integration');
  await page.getByRole('button', { name: 'Pozorovat', exact: true }).click();
  await page.getByRole('heading', { name: 'Rozhovor v integrační simulaci' }).waitFor();
  await page.getByRole('button', { name: 'Aktivovat reproduktor', exact: true }).click();
  await page.getByRole('button', { name: 'Aktivovat mikrofon', exact: true }).click();
  assert.equal(requests.some(request => request.path.endsWith('/media/start')), false);
  await page.getByRole('button', { name: 'Spustit rozhovor', exact: true }).click();
  await page.getByRole('button', { name: 'Aktivovat kameru', exact: true }).click();
  await page.getByRole('button', { name: 'Pořídit náhled', exact: true }).click();
  await page.getByRole('img', { name: 'Lokální náhled fotky před předáním', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Přijmout a předat jednu fotku', exact: true }).click();
  await page.getByText('Nativní workflow skončil.', { exact: true }).waitFor({ timeout: 15000 });
  assert.ok(inputCount >= 2); assert.equal(drained, true); assert.equal(photo, true);
  await page.getByRole('link', { name: /Domov/ }).first().click();
  await page.getByRole('heading', { name: 'Rozhovor v integrační simulaci' }).waitFor({ state: 'detached' });
  assert.equal(await page.getByRole('heading', { name: 'Rozhovor v integrační simulaci' }).count(), 0);
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  console.log(JSON.stringify({ status: 'passed', source: 'synthetic-model+fake-browser-devices+fake-endpoints', microphoneFrames: inputCount, acceptedPhotos: requests.filter(request => request.path.endsWith('/media/photo')).length, softwareDrain: drained, pageErrors: errors.length, externalRequests: external.length }));
  await context.close();
} finally { await browser.close(); await server.close(); }

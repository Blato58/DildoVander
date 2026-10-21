/** Production dist/CSP acceptance; run npm run build first. Fake endpoints/devices only. */
import { chromium } from 'playwright';
import { preview } from 'vite';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { canonicalManifest, sha256 } from '../src/features/workshop/model-bundle.mjs';

const appRoot = fileURLToPath(new URL('..', import.meta.url));
const { captureWorkletUrl } = createRequire(import.meta.url)('../../mechanika/final/twin_media_controller.js');
const packagedAsset = '/assets/twin-capture-worklet-7980af2a1fc08209.js';
assert.equal(captureWorkletUrl(packagedAsset, 'capacitor://localhost/workshop'), 'capacitor://localhost' + packagedAsset);
assert.equal(captureWorkletUrl(packagedAsset, 'https://app.example/workshop'), 'https://app.example' + packagedAsset);
for (const installed of ['capacitor://other-host' + packagedAsset, 'https://other-host' + packagedAsset, 'file:///tmp/worklet.js', 'blob:https://app.example/worklet', '/assets/other.js']) {
  assert.throws(() => captureWorkletUrl(installed, 'capacitor://localhost/workshop'));
}
assert.throws(() => captureWorkletUrl(packagedAsset, 'capacitor://other-host/workshop'));
const compiledHtml = await readFile(new URL('../dist/index.html', import.meta.url), 'utf8');
assert.match(compiledHtml, /kufr-capture-worklet/, 'production build must publish its same-origin worklet');
// Exercise the real production public-loader protocol with labelled synthetic
// geometry, not Vite's development-only fixture shortcut. This is not a CAD receipt.
const fixture = new URL('../src/features/workshop/fixture/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('model-bundle.json', fixture), 'utf8'));
const modelBytes = await readFile(new URL('model.json', fixture));
manifest.provenance.kind = 'cad-export';
manifest.bundleHash = await sha256(new TextEncoder().encode(canonicalManifest(manifest)));
const manifestPath = `/models/bundles/${manifest.bundleHash}/model-bundle.json`;
const server = await preview({ root: appRoot, preview: { host: '127.0.0.1', port: 0 } });
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const BOOT = '00000000-0000-4000-8000-000000000001', SESSION = '00000000-0000-4000-8000-000000000002';
const requests = [], outputs = [], errors = [], external = [], workletRequests = [];
// AudioWorklet fetches are not exposed as page requests by every browser.
// Observe the actual static HTTP request without intercepting or replacing it.
server.httpServer.on('request', request => {
  const url = new URL(request.url, origin);
  if (url.pathname.startsWith('/assets/twin-capture-worklet-')) workletRequests.push(url.href);
});
let sequence = 0, snapshotSequence = 0, active = false, photo = false, drained = false, inputCount = 0;
const frame = (kind, fields = {}) => ({ sessionId: SESSION, bootId: BOOT, epoch: '1', inputEpoch: '1', outputEpoch: '1', sequence: String(++sequence), kind, phase: kind, inputEnabled: ['listening', 'pcm', 'drain'].includes(kind), byteLength: 0, drainedRequested: false, ...fields });
const session = () => ({ sessionId: SESSION, bootId: BOOT, epoch: '1', inputEpoch: '1', outputEpoch: '1', phase: active ? 'listening' : 'completed', inputEnabled: active && photo, active });
function complete() { if (active && photo && drained && inputCount >= 2) { active = false; outputs.push(frame('completed')); } }
const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL, headless: true, args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', '--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
try {
  const context = await browser.newContext({ permissions: ['microphone', 'camera'], viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage();
  await page.addInitScript(() => {
    window.__mediaCspViolations = [];
    document.addEventListener('securitypolicyviolation', event => window.__mediaCspViolations.push({ directive: event.effectiveDirective, blocked: event.blockedURI }));
  });
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/*', async route => {
    const request = route.request(), url = new URL(request.url());
    if (url.origin !== origin) { if (url.protocol.startsWith('http')) external.push(request.url()); return route.abort(); }
    const pathname = url.pathname;
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (pathname === '/models/active.json') return json({ schemaVersion: 1, modelId: manifest.modelId, bundleHash: manifest.bundleHash, manifestPath: manifestPath.slice('/models/'.length) });
    if (pathname === manifestPath) return json(manifest);
    if (pathname === manifestPath.replace('model-bundle.json', 'model.json')) return route.fulfill({ status: 200, contentType: 'application/json', body: modelBytes });
    if (pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'synthetic-owner', displayName: 'Syntetický testovací vlastník', roles: ['Owner'] }, csrfToken: 'synthetic-csrf', origin });
    if (!pathname.startsWith('/v1/twin/') && pathname !== '/app/v1/workshop/sim-integration/engines') return route.continue();
    requests.push({ path: pathname, method: request.method(), body: request.postDataBuffer(), headers: request.headers() });
    if (pathname === '/v1/twin/instances') return json({ instances: [{ instanceId: 'sim-integration', mode: 'simulation', profile: 'integration', canControl: true }] });
    if (pathname === '/v1/twin/instances/sim-integration') return json({ status: 'online', ageMs: 0, receivedAtUtc: new Date().toISOString(), snapshot: { schemaVersion: 1, sequence: String(++snapshotSequence), timeMs: String(snapshotSequence * 750), revision: String(snapshotSequence), source: { instanceId: 'sim-integration', deviceId: 'synthetic', bootId: BOOT, mode: 'simulation', cadManifestRevision: null, hardwareProfile: 'B-effects' }, reported: { physicalVerification: 'unavailable', servo: [] }, model: { evidence: 'simulated', slots: [] }, trace: { events: [] }, capabilities: ['observe'] } });
    if (pathname === '/app/v1/workshop/sim-integration/engines') return json({ schemaVersion: 1, primaryEngine: 'gpt-live', engines: [
      { engine: 'gpt-live', model: 'gpt-live-1', ready: false, unavailableReason: 'synthetic-test-no-provider', voices: ['marin'], speed: { supported: false, min: 1, max: 1, default: 1 }, inputModes: ['audio'], cameraSources: ['phone', 'suitcase'] },
      { engine: 'realtime', model: 'gpt-realtime-2.1', ready: false, unavailableReason: 'synthetic-test-no-provider', voices: ['marin'], speed: { supported: true, min: .8, max: 1.2, default: 1 }, inputModes: ['audio'], cameraSources: ['phone', 'suitcase'] },
    ] });
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
  await page.goto(origin + '/workshop');
  const policy = await page.locator('meta[http-equiv="Content-Security-Policy"]').getAttribute('content');
  const scriptPolicy = policy.split(';').find(directive => directive.trim().startsWith('script-src '));
  assert.ok(scriptPolicy.includes("'self'"));
  assert.doesNotMatch(scriptPolicy, /blob:|'unsafe-inline'|'unsafe-eval'/);
  const worklet = new URL(await page.locator('meta[name="kufr-capture-worklet"]').getAttribute('content'), origin);
  assert.equal(worklet.origin, origin);
  await page.locator('canvas.workshop-canvas').waitFor();
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
  assert.ok(workletRequests.includes(worklet.href), 'the actual AudioWorklet must load the compiled same-origin asset');
  assert.equal(requests.filter(request => request.path === '/app/v1/workshop/sim-integration/engines').length, 1, 'the compiled media panel must read exact-instance canonical capabilities');
  await page.getByRole('link', { name: 'DildoVander, domov', exact: true }).click();
  await page.waitForURL(url => url.origin === origin && url.pathname === '/');
  await page.getByRole('heading', { name: 'Rozhovor v integrační simulaci' }).waitFor({ state: 'detached' });
  assert.equal(await page.getByRole('heading', { name: 'Rozhovor v integrační simulaci' }).count(), 0);
  const violations = await page.evaluate(() => window.__mediaCspViolations);
  assert.deepEqual(errors, []); assert.deepEqual(external, []); assert.deepEqual(violations, []);
  console.log(JSON.stringify({ status: 'passed', source: 'compiled-production-dist+strict-CSP+synthetic-model+fake-browser-devices+fake-endpoints', scriptPolicy: scriptPolicy.trim(), worklet: worklet.pathname, microphoneFrames: inputCount, acceptedPhotos: requests.filter(request => request.path.endsWith('/media/photo')).length, softwareDrain: drained, pageErrors: errors.length, cspViolations: violations.length, externalRequests: external.length }));
  await context.close();
} finally { await browser.close(); await new Promise(resolve => server.httpServer.close(resolve)); }

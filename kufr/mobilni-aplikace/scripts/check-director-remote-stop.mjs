// Isolated loopback fake-mode backend only. Exercises real accepted commands;
// browser transport delays/drops responses to test the operator's uncertainty.
import assert from 'node:assert/strict';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { chromium } from '../../design-system/node_modules/playwright/index.mjs';

const app = process.env.DIRECTOR_TEST_APP ?? 'http://127.0.0.1:5187';
const backend = process.env.DIRECTOR_TEST_BACKEND ?? 'http://localhost:5088';
const deviceId = process.env.DIRECTOR_TEST_DEVICE ?? 'fixture-device';
for (const address of [app, backend]) if (!['localhost', '127.0.0.1'].includes(new URL(address).hostname)) throw new Error('Loopback fixtures only.');
const browser = await chromium.launch({ headless: true, ...(process.platform === 'win32' ? { channel: 'msedge' } : {}) });
let releaseOrdinary;
const ordinaryGate = new Promise(resolve => { releaseOrdinary = resolve; });
let acceptedOrdinary;
const ordinaryAccepted = new Promise(resolve => { acceptedOrdinary = resolve; });
try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const errors = [], stopRequests = [];
  let holdOrdinary = true, dropNextStop = false;
  page.on('pageerror', error => errors.push(error.message));
  await page.route(`${backend}/v1/show/devices/${deviceId}/commands`, async route => {
    const request = route.request();
    if (request.method() !== 'POST') { await route.continue(); return; }
    const body = request.postDataJSON();
    if (body.kind === 'stop') {
      stopRequests.push(body);
      if (dropNextStop) {
        dropNextStop = false;
        await route.fetch(); await route.abort('failed'); return;
      }
    }
    if (holdOrdinary && body.kind === 'mood-evening') {
      holdOrdinary = false;
      const response = await route.fetch(); acceptedOrdinary(await response.json());
      await ordinaryGate; await route.fulfill({ response }); return;
    }
    await route.continue();
  });
  await page.goto(`${app}/#director`);
  await page.getByLabel('Identita kufru', { exact: true }).fill(deviceId);
  await page.getByLabel('HTTPS adresa backendu').fill(backend);
  await page.getByLabel('Administrátorský credential').fill(process.env.DIRECTOR_TEST_CREDENTIAL ?? 'show-local-fixture');
  await page.getByRole('button', { name: 'Ověřit přístup', exact: true }).click();
  await page.locator('[data-director-content]').waitFor({ state: 'visible' });
  assert.equal(await page.locator('[data-director-stop]').isDisabled(), true);
  await page.getByText('Nálada, tón a tempo', { exact: true }).click();
  await page.getByLabel('Rozsah nálady').selectOption('evening');
  await page.getByRole('button', { name: 'Použít náladu', exact: true }).click();
  const earlier = await ordinaryAccepted;
  assert.equal(earlier.status, 'accepted');
  assert.equal(await page.getByRole('button', { name: 'Stop přes backend', exact: true }).isEnabled(), true);
  const stopped = page.waitForResponse(response => response.request().method() === 'POST'
    && response.url().endsWith('/commands') && response.request().postDataJSON().kind === 'stop');
  await page.getByRole('button', { name: 'Stop přes backend', exact: true }).click();
  const stopResponse = await stopped;
  assert.equal(stopResponse.status(), 200);
  const result = await stopResponse.json();
  await page.getByText('Backend přijal Stop. Čekám na aktuální potvrzení stejné revize zařízením; fyzické zastavení není ověřené.', { exact: true }).waitFor();
  assert.ok(stopRequests[0].expectedStateRevision < earlier.state.revision);
  assert.equal(stopRequests.length, 1);
  releaseOrdinary();
  await page.waitForFunction(() => document.querySelector('[data-director-content]').getAttribute('aria-busy') === 'false');
  assert.match(await page.locator('[data-visit]').textContent(), new RegExp(`Revize ${result.state.revision}(?:\\D|$)`));
  assert.equal(await page.locator('[data-visit] h3').textContent(), 'Režie zastavena');
  assert.equal(await page.locator('[data-remote-stop-status]').textContent().then(text => text.includes('Zařízení potvrdilo')), false);
  await page.screenshot({ path: join(tmpdir(), 'director-remote-stop-priority.png'), fullPage: true });
  console.log('remote Stop priority/stale revision/late ordinary result/local separation PASS');

  dropNextStop = true;
  await page.getByRole('button', { name: 'Stop přes backend', exact: true }).click();
  await page.getByText('Výsledek Stop přes backend je neznámý. Povel se automaticky neopakuje; ověřte kufr nebo použijte místní či fyzické zastavení.', { exact: true }).waitFor();
  await page.waitForTimeout(1200);
  assert.equal(stopRequests.length, 2);
  assert.notEqual(stopRequests[0].commandId, stopRequests[1].commandId);
  assert.deepEqual(errors, []);
  console.log('lost accepted Stop response remains unknown/no automatic replay PASS; browser exceptions none');
} finally { releaseOrdinary(); await browser.close(); }

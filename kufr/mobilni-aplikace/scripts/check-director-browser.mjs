// Real app + isolated fake-mode backend. Never use this mutation check on production.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from '../../design-system/node_modules/playwright/index.mjs';

const app = process.env.DIRECTOR_TEST_APP ?? 'http://127.0.0.1:5187';
const backend = process.env.DIRECTOR_TEST_BACKEND ?? 'http://localhost:5088';
if (![app, backend].every(address => ['localhost', '127.0.0.1'].includes(new URL(address).hostname))) {
  throw new Error('Director browser mutation checks require an isolated loopback backend.');
}
const credential = process.env.DIRECTOR_TEST_CREDENTIAL ?? 'show-local-fixture';
const deviceId = process.env.DIRECTOR_TEST_DEVICE ?? 'fixture-device';
const output = process.env.DIRECTOR_TEST_SCREENSHOTS ?? join(tmpdir(), 'dildovander-director-browser');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true,
  args: ['--host-resolver-rules=MAP localhost 127.0.0.1'],
  ...(process.platform === 'win32' ? { channel: process.env.DIRECTOR_TEST_BROWSER ?? 'msedge' } : {}),
});
const errors = [];
try {
  const pages = [];
  for (const [width, height] of [[1280, 900], [390, 844], [320, 740], [844, 390]]) {
    const page = await browser.newPage({ viewport: { width, height }, reducedMotion: 'reduce' });
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${app}/#director`);
    await page.getByRole('heading', { name: 'Director Mode', exact: true }).waitFor();
    assert.equal(await page.locator('[data-director-stop]').isDisabled(), true);
    assert.equal(await page.getByRole('button', { name: 'Stop přes backend', exact: true }).isDisabled(), true);
    assert.equal(await page.locator('[data-director-content]').isVisible(), false);
    assert.equal(await page.locator('[data-director-stop]').count(), 1);
    await page.getByLabel('Identita kufru', { exact: true }).fill(deviceId);
    await page.getByLabel('HTTPS adresa backendu').fill(backend);
    await page.getByLabel('Administrátorský credential').fill(credential);
    await page.getByRole('button', { name: 'Ověřit přístup', exact: true }).click();
    await page.locator('[data-director-content]').waitFor({ state: 'visible' });
    await page.waitForFunction(() => !document.querySelector('[data-director-command="mood-auto"]').disabled);
    assert.equal(await page.locator('#director-credential').inputValue(), '');
    assert.equal(await page.locator('[data-director-access]').isVisible(), false);
    assert.equal(await page.getByRole('button', { name: 'Stop přes backend', exact: true }).isEnabled(), true);
    assert.equal(await page.locator('[data-offline="offline_start"]').isDisabled(), true);
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
    assert.equal(overflow, false, `page overflow at ${width} × ${height}`);
    await page.screenshot({ path: join(output, `director-${width}x${height}.png`), fullPage: true });
    pages.push(page);
  }
  const [first, second] = pages;
  // Both clients refresh the same server revision before their competing gestures.
  await first.getByRole('button', { name: 'Obnovit stav', exact: true }).click();
  await second.getByRole('button', { name: 'Obnovit stav', exact: true }).click();
  await first.getByText('Nálada, tón a tempo', { exact: true }).click();
  await second.getByText('Nálada, tón a tempo', { exact: true }).click();
  await first.getByLabel('Rozsah nálady').selectOption('evening');
  await second.getByLabel('Rozsah nálady').selectOption('evening');
  await first.getByRole('button', { name: 'Použít náladu', exact: true }).click();
  await first.getByText('Nálada večera: serverový výsledek přijat.', { exact: true }).waitFor();
  await second.getByRole('button', { name: 'Použít náladu', exact: true }).click();
  await second.getByText('Konflikt revize. Obnovte stav před dalším povelem.', { exact: true }).waitFor();
  assert.equal(await second.locator('[data-director-command="mood-auto"]').isDisabled(), true);
  await second.getByRole('button', { name: 'Obnovit stav', exact: true }).click();
  await second.waitForFunction(() => !document.querySelector('[data-director-command="mood-auto"]').disabled);
  await second.getByRole('button', { name: 'Vrátit automatickou náladu', exact: true }).click();
  await second.getByText('Automatická nálada: serverový výsledek přijat.', { exact: true }).waitFor();
  await second.getByRole('button', { name: 'Odhlásit režii', exact: true }).click();
  assert.equal(await second.locator('[data-director-content]').isVisible(), false);
  await second.getByRole('button', { name: 'Zpět do Provozu', exact: true }).click();
  assert.equal(await second.locator('#control-panel').isVisible(), true);
  assert.equal(await second.locator('#director-panel').isVisible(), false);
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ passed: true, viewports: [[1280,900],[390,844],[320,740],[844,390]], screenshots: output,
    checks: ['anonymous gate', 'native-only offline gate', 'credential cleared', 'reduced motion', 'no overflow', 'two-operator conflict', 'explicit refresh', 'logout', 'return navigation', 'no browser exceptions'] }));
} finally { await browser.close(); }

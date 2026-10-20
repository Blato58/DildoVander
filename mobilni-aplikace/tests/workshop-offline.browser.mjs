/** Repeatable file:// acceptance: real WebGL, no server and all network disabled. */
import { chromium } from 'playwright';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import assert from 'node:assert/strict';
import { packageOffline } from '../scripts/package-offline-viewer.mjs';
import { canonicalManifest, sha256 } from '../src/features/workshop/model-bundle.mjs';
import { richWorkshopModel } from './fixtures/workshop-rich.mjs';

const output = await mkdtemp(path.join(tmpdir(), 'dildovander-workshop-'));
const artifact = path.join(output, 'workshop.html');
const source = new URL('../src/features/workshop/fixture/', import.meta.url);
const rich = richWorkshopModel(JSON.parse(await readFile(new URL('model.json', source), 'utf8')));
const manifest = JSON.parse(await readFile(new URL('model-bundle.json', source), 'utf8'));
const modelBytes = new TextEncoder().encode(JSON.stringify(rich));
manifest.assets[0] = { ...manifest.assets[0], bytes: modelBytes.length, sha256: await sha256(modelBytes) };
manifest.bundleHash = await sha256(new TextEncoder().encode(canonicalManifest(manifest)));
const release = path.join(output, 'synthetic-model-release'); await mkdir(release);
await writeFile(path.join(release, 'model.json'), modelBytes); await writeFile(path.join(release, 'model-bundle.json'), JSON.stringify(manifest));
await packageOffline({ bundleDirectory: release, output: artifact, allowFixture: true });
const browser = await chromium.launch({ channel: process.env.PLAYWRIGHT_CHANNEL, headless: true, args: ['--use-gl=angle', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'] });
try {
  const context = await browser.newContext({ offline: true, viewport: { width: 1440, height: 1000 } });
  const page = await context.newPage(), requests = [], failures = [];
  await page.route('http://**/*', route => { requests.push(route.request().url()); return route.abort(); });
  await page.route('https://**/*', route => { requests.push(route.request().url()); return route.abort(); });
  page.on('pageerror', error => failures.push(error.message));
  await page.goto(pathToFileURL(artifact).href);
  try { await page.getByText('SYNTETICKÁ UI FIXTURE', { exact: true }).waitFor({ timeout: 10000 }); }
  catch (error) { console.error(JSON.stringify({ failures, body: await page.locator('body').innerText(), artifact })); throw error; }
  await page.getByRole('button', { name: 'Celý model', exact: true }).click();
  await page.getByLabel('Vzhled offline vieweru').selectOption('olivander');
  assert.equal(await page.locator('html').getAttribute('data-theme'), 'olivander');
  await page.getByLabel('Vzhled offline vieweru').selectOption('lord-voldemort');
  await page.getByRole('button', { name: 'Syntetický UI blok', exact: true }).click();
  assert.equal(await page.getByLabel('Barva dílu').count(), 1);
  await page.getByRole('button', { name: 'Montáž', exact: true }).click();
  await page.getByLabel('Krok', { exact: true }).selectOption('1');
  await page.getByRole('button', { name: 'Shora', exact: true }).click();
  await page.getByRole('button', { name: 'Obnovit viditelnost', exact: true }).click();
  assert.equal(await page.getByLabel('Krok', { exact: true }).inputValue(), '1');
  await page.getByLabel('Zobrazení', { exact: true }).selectOption('operation');
  await page.getByText('Víko, kabely a ilustrativní pohyb', { exact: true }).click();
  await page.getByLabel('Korpus', { exact: true }).uncheck();
  await page.getByLabel('Kabeláž', { exact: true }).check();
  await page.getByLabel('Rozměry návrhu', { exact: true }).check();
  await page.getByLabel('Inspect · názvy a výběr vodičů', { exact: true }).check();
  await page.locator('.workshop-overlay [data-wire-id="fixture-lift"]').waitFor();
  assert.equal(await page.locator('.workshop-overlay .workshop-orientation').count(), 4);
  assert.equal(await page.locator('.workshop-overlay text').filter({ hasText: 'Průchozí horní X' }).count(), 1);
  await page.getByRole('button', { name: 'Díly', exact: true }).click();
  await page.getByText('Kabeláž a propojení', { exact: true }).click();
  await page.getByLabel('Vodič', { exact: true }).selectOption('fixture-lift');
  await page.getByLabel('Rozsah zvýraznění').selectOption('bundle');
  assert.ok(await page.locator('.workshop-overlay [data-endpoint="tray_left_1:LED"]').count() > 0);
  const before = await page.locator('.workshop-overlay [data-wire-id="fixture-lift"]').getAttribute('points');
  await page.getByLabel('Přihrádka 1', { exact: true }).evaluate(input => { input.value = '80'; input.dispatchEvent(new Event('input', { bubbles: true })); });
  await page.waitForFunction(previous => document.querySelector('.workshop-overlay [data-wire-id="fixture-lift"]')?.getAttribute('points') !== previous, before);
  const canvas = await page.locator('canvas').elementHandle();
  for (const theme of ['lord-voldemort', 'olivander']) {
    await page.getByLabel('Vzhled offline vieweru').selectOption(theme);
    assert.equal(await page.locator('html').getAttribute('data-theme'), theme);
    assert(await page.locator('canvas').evaluate((element, original) => element === original, canvas), 'theme switching retains the WebGL canvas');
    for (const [name, width, height] of [['desktop', 1440, 1000], ['mobile', 390, 844]]) {
      await page.setViewportSize({ width, height });
      await page.screenshot({ path: path.join(output, `${theme}-${name}.png`), fullPage: true });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
    }
  }
  assert.deepEqual(requests, []); assert.deepEqual(failures, []);
  console.log(JSON.stringify({ status: 'passed', artifact, screenshots: ['lord-voldemort-desktop.png', 'lord-voldemort-mobile.png', 'olivander-desktop.png', 'olivander-mobile.png'].map(name => path.join(output, name)), networkRequests: requests.length, pageErrors: failures.length }));
  await context.close();
} finally { await browser.close(); }

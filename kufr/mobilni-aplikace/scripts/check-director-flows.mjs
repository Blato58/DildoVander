// Mutates only an isolated loopback fake-mode fixture with two completed identities.
import assert from 'node:assert/strict';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { chromium } from '../../design-system/node_modules/playwright/index.mjs';
import { guestFixtureContext } from './director-guest-fixture.mjs';

const app = process.env.DIRECTOR_TEST_APP ?? 'http://127.0.0.1:5187';
const backend = process.env.DIRECTOR_TEST_BACKEND ?? 'http://localhost:5088';
for (const address of [app, backend]) if (!['localhost', '127.0.0.1'].includes(new URL(address).hostname)) throw new Error('Loopback fixtures only.');
const browser = await chromium.launch({ headless: true, args: ['--host-resolver-rules=MAP localhost 127.0.0.1'],
  ...(process.platform === 'win32' ? { channel: 'msedge' } : {}) });
try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(`${app}/#director`);
  await page.getByRole('heading', { name: 'Director Mode', exact: true }).waitFor();
  console.log('Director route loaded');
  await page.getByLabel('Identita kufru', { exact: true }).fill(process.env.DIRECTOR_TEST_DEVICE ?? 'fixture-device');
  await page.getByLabel('HTTPS adresa backendu').fill(backend);
  await page.getByLabel('Administrátorský credential').fill(process.env.DIRECTOR_TEST_CREDENTIAL ?? 'show-local-fixture');
  await page.getByRole('button', { name: 'Ověřit přístup', exact: true }).click();
  await page.locator('[data-director-content]').waitFor({ state: 'visible' });
  console.log('operator access verified');

  await page.getByText('Souboj dvou hostů', { exact: true }).click();
  await page.getByRole('button', { name: 'Načíst identity a souboje', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#director-left').options.length >= 2);
  console.log('two identity snapshots loaded');
  await page.getByLabel('Pravý soupeř', { exact: true }).selectOption({ index: 1 });
  await page.getByLabel('Oba hosté souhlasí se soubojem').check();
  await page.getByRole('button', { name: 'Připravit souboj', exact: true }).click();
  await page.getByText('Příprava souboje: serverový výsledek přijat.', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Spustit připravený souboj', exact: true }).click();
  await page.getByText('Souboj: serverový výsledek přijat.', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Zrušit souboj', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('[data-duel-state]').textContent.includes('Zrušeno'));
  console.log('duel create/start/cancel PASS');

  await page.getByLabel('Způsob souboje', { exact: true }).selectOption('interactive');
  await page.getByRole('button', { name: 'Připravit souboj', exact: true }).click();
  await page.getByText('Příprava souboje: serverový výsledek přijat.', { exact: true }).waitFor();
  assert.equal(await page.locator('[data-duel-start]').isDisabled(), true);
  await page.getByRole('button', { name: 'Vydat odkaz · levý', exact: true }).click();
  await page.locator('[data-duel-play="left"]').waitFor({ state: 'visible' });
  const oldLeft = new URL(await page.locator('[data-duel-play="left"]').getAttribute('href'));
  const revokedToken = new URLSearchParams(oldLeft.hash.slice(1)).get('grant');
  await page.getByRole('button', { name: 'Vydat odkaz · pravý', exact: true }).click();
  await page.locator('[data-duel-play="right"]').waitFor({ state: 'visible' });
  await page.getByRole('button', { name: 'Odvolat odkaz · levý', exact: true }).click();
  await page.getByText('Odvolání odkazu soupeře: serverový výsledek přijat.', { exact: true }).waitFor();
  assert.equal(await page.locator('[data-duel-play="left"]').getAttribute('href'), null);
  assert.equal(await page.locator('[data-duel-play="right"]').isVisible(), true);
  await page.getByRole('button', { name: 'Vydat odkaz · levý', exact: true }).click();
  await page.locator('[data-duel-play="left"]').waitFor({ state: 'visible' });
  const leftUrl = new URL(await page.locator('[data-duel-play="left"]').getAttribute('href'));
  const rightUrl = new URL(await page.locator('[data-duel-play="right"]').getAttribute('href'));
  const publicUrl = new URL(await page.locator('[data-duel-public]').getAttribute('href'));
  assert.equal(publicUrl.hash, '');
  assert.equal(leftUrl.searchParams.has('grant'), false);
  assert.equal(rightUrl.searchParams.has('grant'), false);
  assert.equal(new URLSearchParams(leftUrl.hash.slice(1)).get('side'), 'left');
  assert.equal(new URLSearchParams(rightUrl.hash.slice(1)).get('side'), 'right');
  assert.notEqual(new URLSearchParams(leftUrl.hash.slice(1)).get('grant'), revokedToken);
  await page.screenshot({ path: join(tmpdir(), 'director-interactive-grants.png'), fullPage: true });
  const startResponse = page.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/start'));
  await page.getByRole('button', { name: 'Spustit připravený souboj', exact: true }).click();
  const started = await (await startResponse).json();
  const revoked = await fetch(`${backend}/guest/v1/duels/${publicUrl.searchParams.get('duel')}/choices`, {
    method: 'POST', headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${revokedToken}`,
      Host: publicUrl.host, 'X-Forwarded-Proto': 'https' },
    body: JSON.stringify({ side: 'left', round: 1, revision: started.revision, action: 'trick', operationKey: crypto.randomUUID() }),
  });
  assert.equal(revoked.status, 403);
  console.log('interactive grants/revoke/rotation/fragment-only links PASS');

  // The configured HTTPS origin is intercepted only in this synthetic browser
  // transport. The public files and all game responses remain actual source/API.
  const leftContext = await guestFixtureContext(browser, publicUrl.origin, backend);
  const rightContext = await guestFixtureContext(browser, publicUrl.origin, backend);
  const viewerContext = await guestFixtureContext(browser, publicUrl.origin, backend);
  const leftPage = await leftContext.newPage(), rightPage = await rightContext.newPage();
  for (const guest of [leftPage, rightPage]) guest.on('pageerror', error => errors.push(error.message));
  await Promise.all([leftPage.goto(leftUrl.href), rightPage.goto(rightUrl.href)]);
  await Promise.all([leftPage.locator('#duel-controls').waitFor({ state: 'visible' }), rightPage.locator('#duel-controls').waitFor({ state: 'visible' })]);
  assert.equal(new URL(leftPage.url()).hash, ''); assert.equal(new URL(rightPage.url()).hash, '');
  for (let round = 1; round <= 3; round++) {
    await Promise.all([leftPage, rightPage].map(guest => guest.waitForFunction(currentRound =>
      document.querySelector('#choice-status').textContent.includes(`Kolo ${currentRound} ze 3`), round)));
    const leftAccepted = leftPage.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/choices'));
    await leftPage.getByRole('button', { name: 'Úder', exact: true }).click();
    const leftMove = await leftAccepted; assert.equal(leftMove.status(), 200);
    const leftState = await leftMove.json();
    await rightPage.waitForResponse(async response => response.request().method() === 'GET'
      && /\/guest\/v1\/duels\/[^/]+$/.test(new URL(response.url()).pathname) && (await response.json()).revision >= leftState.revision);
    const rightAccepted = rightPage.waitForResponse(response => response.request().method() === 'POST' && response.url().endsWith('/choices'));
    await rightPage.getByRole('button', { name: 'Štít', exact: true }).click();
    assert.equal((await rightAccepted).status(), 200);
  }
  await leftPage.locator('#duel-result').waitFor({ state: 'visible' });
  const viewer = await viewerContext.newPage(); viewer.on('pageerror', error => errors.push(error.message));
  await viewer.goto(publicUrl.href); await viewer.locator('#duel-result').waitFor({ state: 'visible' });
  assert.equal(await viewer.locator('#duel-controls').isVisible(), false);
  await viewer.screenshot({ path: join(tmpdir(), 'director-public-duel-result.png'), fullPage: true });
  await page.waitForFunction(() => document.querySelector('[data-duel-state]').textContent.includes('Dokončeno'));
  assert.equal(await page.locator('[data-duel-play="left"]').getAttribute('href'), null);
  assert.equal(await page.locator('[data-duel-play="right"]').getAttribute('href'), null);
  console.log('public source + real guest API three-round play/read-only result/terminal token clearing PASS (synthetic HTTPS transport)');
  await Promise.all([leftContext.close(), rightContext.close(), viewerContext.close()]);

  await page.getByText('Proroctví a kronika', { exact: true }).click();
  await page.getByRole('button', { name: 'Načíst proroctví večera', exact: true }).click();
  await page.locator('[data-prophecies] article').first().waitFor();
  assert.equal(await page.locator('[data-prophecies-load]').count(), 1);
  const reveal = page.getByRole('button', { name: 'Odhalit toto proroctví', exact: true });
  if (await reveal.count()) await reveal.first().click();
  await page.getByText('Odhaleno návštěvníkovi. Soukromé znění zůstává v jeho památce.', { exact: true }).first().waitFor();
  assert.equal(await page.getByRole('button', { name: 'Načíst proroctví večera', exact: true }).count(), 1);
  console.log('prophecy reveal/private omission/load action preserved PASS');

  await page.getByRole('button', { name: 'Připravit uzavření večera', exact: true }).click();
  await page.getByRole('button', { name: 'Ponechat večer otevřený', exact: true }).click();
  assert.equal(await page.locator('[data-end-confirm]').isVisible(), false);
  await Promise.all([
    page.waitForResponse(response => response.request().method() === 'GET' && /\/v1\/show\/devices\/[^/]+$/.test(new URL(response.url()).pathname)),
    page.getByRole('button', { name: 'Obnovit stav', exact: true }).click(),
  ]);
  await page.getByRole('button', { name: 'Připravit uzavření večera', exact: true }).click();
  await page.getByRole('button', { name: 'Potvrdit uzavření večera', exact: true }).click();
  await page.getByText('Uzavření večera: serverový výsledek přijat.', { exact: true }).waitFor();
  console.log('end evening confirmation/cancel/close PASS');
  await page.screenshot({ path: join(tmpdir(), 'director-complete-flow.png'), fullPage: true });
  const chronicleContext = await guestFixtureContext(browser, publicUrl.origin, backend);
  for (const width of [1280, 390, 320]) {
    const chronicle = await chronicleContext.newPage();
    await chronicle.setViewportSize({ width, height: width === 1280 ? 900 : 844 });
    chronicle.on('pageerror', error => errors.push(error.message));
    const snapshotResponse = chronicle.waitForResponse(response => response.url().endsWith('/guest/v1/chronicle'));
    await chronicle.goto(`${publicUrl.origin}/keepsake/chronicle.html`);
    await chronicle.locator('#chronicle').waitFor({ state: 'visible' });
    const snapshot = await (await snapshotResponse).json();
    assert.equal(snapshot.retention, 'manual_only');
    assert.ok(snapshot.completedDuels >= 1);
    assert.equal(await chronicle.locator('#completed-duels').textContent(), String(snapshot.completedDuels));
    assert.equal(await chronicle.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth), false);
    await chronicle.screenshot({ path: join(tmpdir(), `director-chronicle-${width}.png`), fullPage: true });
    await chronicle.close();
  }
  await chronicleContext.close();
  console.log('chronicle actual source/ledger manual_only/completed duels/no overflow 1280/390/320 PASS (synthetic HTTPS transport)');
  assert.deepEqual(errors, []);
  console.log('browser exceptions: none');
} finally { await browser.close(); }

// Repeatable isolated checks of the compiled app. All APIs are synthetic and
// non-local network requests are blocked; this suite never controls equipment.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const screenshots = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-application-browser');
await stat(path.join(dist, 'index.html'));
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.json': 'application/json' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (request.method !== 'GET') { response.writeHead(405).end(); return; }
    if (/^\/(?:app\/v1|guest\/v1|v1|models)\//.test(url.pathname)) { response.writeHead(404, { 'Content-Type': 'application/json' }).end('{}'); return; }
    const relative = decodeURIComponent(url.pathname).replace(/^\//, '');
    let file = path.resolve(dist, relative || 'index.html');
    if (!file.startsWith(dist)) { response.writeHead(403).end(); return; }
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); }
    catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
let page;
const errors = [];
try {
  try { browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) }); }
  catch (error) {
    if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  page = await context.newPage();
  page.setDefaultTimeout(10000);
  const requests = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' && /Content Security Policy|unsafe-eval|Refused to|EvalError/i.test(message.text())) errors.push(message.text());
  });
  await context.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin) { await route.abort(); return; }
    requests.push({ path: url.pathname, method: request.method(), url: request.url() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: null, csrfToken: 'isolated-browser-csrf' });
    if (url.pathname.startsWith('/app/v1/')) return json({ code: 'account_required' }, 401);
    if (url.pathname === '/guest/v1/evening') return json({ schemaVersion: 1, evening: null, items: [], latest: null });
    if (url.pathname === '/guest/v1/bootstrap') return json({ schemaVersion: 1, csrfToken: 'isolated-guest-csrf' });
    if (url.pathname === '/guest/v1/claim') return json({ locator: '11111111-1111-4111-8111-111111111111' });
    if (url.pathname.startsWith('/guest/v1/')) return json({ code: 'not_available' }, 404);
    await route.continue();
  });
  const hasHeading = title => page.getByRole('heading', { name: title, exact: true }).waitFor();
  const fits = async () => assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'layout has no horizontal page overflow');

  await page.goto(origin); await hasHeading('Všechno pro jeden večer.');
  await fits(); await page.screenshot({ path: path.join(screenshots, 'home-desktop.png'), fullPage: true });
  await page.getByLabel('Vzhled aplikace').selectOption('olivander'); assert.equal(await page.locator('html').getAttribute('data-theme'), 'olivander');
  await page.reload(); await hasHeading('Všechno pro jeden večer.'); assert.equal(await page.locator('html').getAttribute('data-theme'), 'olivander');
  await page.getByLabel('Vzhled aplikace').selectOption('lord-voldemort');
  const preferenceTab = await context.newPage();
  await preferenceTab.goto(origin + '/settings');
  await preferenceTab.getByLabel('Barevné téma', { exact: true }).selectOption('olivander');
  assert.equal(await preferenceTab.getByLabel('Vzhled aplikace').inputValue(), 'olivander', 'settings and header synchronize in the same tab');
  await page.waitForFunction(() => document.documentElement.dataset.theme === 'olivander');
  assert.equal(await page.getByLabel('Vzhled aplikace').inputValue(), 'olivander', 'a second tab receives the stored preference');
  await preferenceTab.close();
  await page.getByLabel('Vzhled aplikace').selectOption('lord-voldemort');
  const nav = page.getByRole('navigation', { name: 'Sekce aplikace' });
  for (const [label, heading] of [['Kufr', 'Kufr'], ['Dílna', 'Workshop'], ['Archiv', 'Najdi svoji hůlku.'], ['Nastavení', 'Nastavení'], ['Domov', 'Všechno pro jeden večer.']]) {
    await nav.getByRole('link', { name: label, exact: true }).click(); await hasHeading(heading);
    assert.equal(await page.locator('.application-shell').count(), 1);
  }
  for (const route of ['/connections', '/suitcase/diagnostics', '/settings/admin', '/signin']) {
    await page.goto(origin + route); await page.getByRole('main').waitFor(); await fits();
    await page.reload(); await page.getByRole('main').waitFor();
  }
  await page.goto(origin + '/settings'); await hasHeading('Verze aplikace');
  const runningRevision = await page.locator('.application-revision').textContent();
  assert.equal(runningRevision, execFileSync('git', ['rev-parse', 'HEAD'], { cwd: fileURLToPath(new URL('..', import.meta.url)), encoding: 'utf8', windowsHide: true }).trim(), 'display identifies the compiled application source');
  for (const [legacy, destination] of [['/companion/', '/suitcase'], ['/app/', '/suitcase'], ['/keepsake/evening/', '/archive'], ['/workshop/index.html', '/workshop'], ['/simulator/', '/workshop?mode=simulation']]) {
    await page.goto(origin + legacy); await page.waitForURL(origin + destination);
  }
  await page.goto(origin + '/unavailable-page'); await hasHeading('Stránka nebyla nalezena');

  // Explicit local demo only. A view mount must not silently select a target.
  await page.goto(origin + '/suitcase'); await page.getByRole('heading', { name: 'Kufr není připojený' }).waitFor();
  await page.getByRole('button', { name: 'Otevřít místní ukázku' }).click();
  await page.getByRole('button', { name: 'Ruční ovládání', exact: true }).click();
  await page.getByRole('button', { name: 'Převzít ruční řízení' }).click();
  await page.getByRole('button', { name: 'Vybrat přihrádku', exact: true }).first().focus();
  const focus = await page.evaluateHandle(() => document.activeElement);
  await page.waitForTimeout(2200); assert(await page.evaluate(element => document.activeElement === element, focus));
  await page.getByRole('button', { name: 'Vybrat přihrádku', exact: true }).first().click();
  await page.getByRole('heading', { name: /Spustit výběr/ }).waitFor();
  await nav.getByRole('link', { name: 'Archiv', exact: true }).click(); await nav.getByRole('link', { name: 'Kufr', exact: true }).click();
  assert.equal(await page.getByRole('heading', { name: /Spustit výběr/ }).count(), 0, 'navigation invalidates confirmation');
  await page.getByRole('button', { name: 'Stop', exact: true }).click();
  assert.equal(requests.filter(request => request.method !== 'GET' && request.path.startsWith('/v1/')).length, 0, 'demo never emits physical network commands');

  // Real inline capture runs before imports; no claim is exchanged on a GET.
  const claimCount = () => requests.filter(request => request.path === '/guest/v1/claim').length;
  const before = claimCount();
  await page.goto(origin + '/keepsake/#c=' + 'a'.repeat(40));
  await page.getByRole('button', { name: 'Otevřít moji památku', exact: true }).waitFor();
  assert.equal(new URL(page.url()).hash, ''); assert.equal(claimCount(), before);
  assert.match(await page.locator('meta[name=robots]').getAttribute('content'), /noindex/);
  await page.getByRole('button', { name: 'Otevřít moji památku', exact: true }).click();
  await page.waitForURL(/\?k=/); assert.equal(claimCount(), before + 1);
  assert(requests.every(request => !request.url.includes('a'.repeat(40))), 'claim capability never appears in a request URL');

  for (const theme of ['lord-voldemort', 'olivander']) {
    await page.goto(origin);
    await page.getByLabel('Vzhled aplikace').selectOption(theme);
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      for (const route of ['/', '/connections', '/suitcase', '/suitcase/diagnostics', '/suitcase/director', '/workshop', '/archive', '/archive/duel', '/archive/chronicle', '/keepsake/', '/signin', '/settings', '/settings/admin', '/unavailable-page']) {
        await page.goto(origin + route); await page.getByRole('main').waitFor(); await page.waitForTimeout(100); await fits();
        assert.equal(await page.locator('html').getAttribute('data-theme'), theme);
        const installer = page.getByRole('link', { name: 'Aplikace pro iPhone', exact: true });
        assert.equal(await installer.getAttribute('href'), '/ios/');
        assert(await installer.isVisible(), 'the iPhone distribution link remains visible');
        assert(await installer.evaluate(link => link.getBoundingClientRect().height >= 44), 'the iPhone link retains a 44px touch target');
        if (route === '/') {
          const homeInstaller = page.getByRole('link', { name: 'Stáhnout aplikaci pro iPhone', exact: true });
          assert.equal(await homeInstaller.getAttribute('href'), '/ios/');
          assert(await homeInstaller.isVisible(), 'browser home makes iPhone installation discoverable');
          assert(await page.locator('.home-primary-actions .aw-button--primary').evaluate(link => {
            const sample = document.createElement('span');
            sample.style.color = 'var(--aw-on-accent)';
            link.append(sample);
            const expected = getComputedStyle(sample).color;
            sample.remove();
            return getComputedStyle(link).color === expected;
          }), 'primary links retain accessible text on the accent surface');
        }
        const name = route.replace(/^\/|\/$/g, '').replaceAll('/', '-') || 'home';
        await page.screenshot({ path: path.join(screenshots, `${theme}-${name}-${width}.png`), fullPage: true });
      }
      assert(await page.locator('.primary-navigation a').evaluateAll(links => links.every(link => link.getBoundingClientRect().height >= 44)), 'navigation targets retain 44px height');
    }
    await page.goto(origin); await hasHeading('Všechno pro jeden večer.');
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await fits();
    await page.waitForFunction(() => parseFloat(getComputedStyle(document.querySelector('.application-shell')).paddingBottom)
      >= document.querySelector('.primary-navigation').getBoundingClientRect().height - 1);
    assert(await page.getByRole('button', { name: 'Stop', exact: true }).isVisible(), 'Stop remains visible with enlarged text');
    await page.screenshot({ path: path.join(screenshots, `${theme}-home-320-text-200.png`), fullPage: true });
    await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  }
  await page.goto(origin); await page.keyboard.press('Tab'); assert.equal(await page.locator(':focus').textContent(), 'Přejít na obsah');

  // Synthetic owner UI evidence only. This does not authenticate against a
  // backend, create a passkey or permit any management mutation.
  const ownerRequests = [];
  const owner = { id: 'synthetic-owner', displayName: 'SYNTETICKÝ VLASTNÍK · UI FIXTURE', roles: ['Owner'] };
  await context.route('**/app/v1/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    ownerRequests.push({ path: pathname, method: request.method() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (request.method() !== 'GET') return json({ code: 'synthetic_fixture_read_only' }, 405);
    if (pathname === '/app/v1/session') return json({ enabled: true, account: owner, csrfToken: 'synthetic-owner-csrf' });
    if (pathname === '/app/v1/account') return json({
      devices: [], associations: [], grants: [], passkeyCount: 1,
      passkeys: [{ id: 'synthetic-key', name: 'Syntetický přístupový klíč', createdAt: '2026-10-20T00:00:00Z', isBackedUp: true }],
      sessions: [{ id: 'synthetic-session', installationName: 'Syntetický prohlížeč', current: true, trusted: false, expiresAt: 1900000000 }],
    });
    if (pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (pathname === '/app/v1/admin/users') return json({ users: [{ ...owner, disabled: false }], grants: [] });
    if (pathname === '/app/v1/admin/model-releases') return json({ enabled: false, active: null, releases: [], invalidReleases: 0, truncated: false, integrity: 'synthetic-fixture' });
    if (pathname === '/app/v1/admin/operations/status') return json({
      modes: { revision: 'synthetic-fixture', activeConversations: 0, personalityId: 'fixture', personalities: [{ id: 'fixture', name: 'Syntetická osobnost' }], characterEnabled: false, characterTone: 'Reserved', characterFinaleProfile: 'Blocked', characterVersion: 'synthetic-fixture' },
      readiness: { accounts: true, keepsakes: false, keepsakeGeneration: false, passkeyRelyingParty: 'synthetic-fixture', nativeAssociationConfigured: false },
      archive: [],
    });
    return route.fallback();
  });
  for (const theme of ['lord-voldemort', 'olivander']) {
    for (const width of [1440, 390]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await page.goto(origin + '/settings');
      await page.getByLabel('Název přístupového klíče 1', { exact: true }).waitFor();
      await page.getByLabel('Vzhled aplikace').selectOption(theme);
      await fits();
      await page.screenshot({ path: path.join(screenshots, `${theme}-synthetic-owner-settings-${width}.png`), fullPage: true });
      await page.goto(origin + '/settings/admin');
      const resource = page.getByLabel('Identifikátor prostředku', { exact: true });
      await resource.fill('synthetic-unsaved-grant');
      const original = await resource.elementHandle();
      await page.getByLabel('Vzhled aplikace').selectOption(theme === 'olivander' ? 'lord-voldemort' : 'olivander');
      assert.equal(await resource.inputValue(), 'synthetic-unsaved-grant', 'theme switching preserves an unsaved management draft');
      assert(await resource.evaluate((element, previous) => element === previous, original), 'theme switching retains the management control');
      await page.getByLabel('Vzhled aplikace').selectOption(theme);
      await fits();
      await page.screenshot({ path: path.join(screenshots, `${theme}-synthetic-owner-admin-${width}.png`), fullPage: true });
    }
  }
  assert(ownerRequests.every(request => request.method === 'GET'), 'theme checks never send a management mutation');
  assert.deepEqual(errors, [], 'no uncaught application errors');
  console.log(`PASS compiled application: all 14 route views (including unauthenticated/invalid-capability gates), synthetic owner settings/admin, management draft preservation with no writes, iPhone links, navigation, deep links, compatibility, claim capture, demo isolation, focus, both themes, keyboard, 1440/390/320 layouts and 200% text. Screenshots: ${screenshots}`);
  await context.close();
} catch (error) {
  console.error('Application errors:', errors);
  if (page) {
    console.error('Application state on failure:', await page.locator('main').innerText({ timeout: 1000 }).catch(() => 'main unavailable'));
    await page.screenshot({ path: path.join(screenshots, 'failure.png'), fullPage: true, timeout: 3000 }).catch(() => {});
  }
  throw error;
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }

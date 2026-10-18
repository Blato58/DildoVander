/* Synthetic browser fixtures only. No provider requests or production data. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { chromium } = require('playwright');
const repo = path.resolve(__dirname, '../../..');
const ownerId = '11111111-1111-4111-8111-111111111111';
const otherId = '22222222-2222-4222-8222-222222222222';
const artId = '33333333-3333-4333-8333-333333333333';
const editionId = '44444444-4444-4444-8444-444444444444';
const photo = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jC3sAAAAASUVORK5CYII=', 'base64');
const fixture = (name = 'Brk bouřlivého archiváře') => ({
  schemaVersion: 1, locator: ownerId, revision: 3, state: 'active', retentionMode: 'manual_only', readOnly: false,
  snapshot: { officialName: name, catalogId: 'fixture-wand', slot: 2, physicalEvidence: 'synthetic' },
  editions: [{ id: editionId, synthetic: true, artworkAssetId: artId, createdAt: '2026-10-14T12:00:00Z',
    story: { title: 'Majitel posledního slova', text: 'Tato syntetická legenda patří pouze testu.\n\nHůlka zásadně odmítala přímé odpovědi, a proto se zabydlela v archivu.', design: {} } }],
  portraits: [], uploads: [], jobs: [], assets: [], sharedEditionId: null
});
const artwork = '<svg xmlns="http://www.w3.org/2000/svg" width="600" height="760" viewBox="0 0 600 760"><rect width="600" height="760" fill="#171F29"/><circle cx="300" cy="360" r="210" fill="none" stroke="#34424F"/><path d="M180 590L390 150" stroke="#D9B777" stroke-width="18" stroke-linecap="round"/><path d="M174 585L220 491" stroke="#6B7E8E" stroke-width="32" stroke-linecap="round"/><path d="M183 560L220 535M196 533L230 508" stroke="#D9B777" stroke-width="7"/></svg>';

(async () => {
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const file = url.pathname.startsWith('/design-system/')
      ? path.join(repo, 'kufr', url.pathname) : path.join(repo, 'kufr/web', url.pathname.endsWith('/') ? url.pathname + 'index.html' : url.pathname);
    if (!file.startsWith(path.join(repo, 'kufr')) || !fs.existsSync(file)) { res.writeHead(404); return res.end(); }
    const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf' };
    res.setHeader('Content-Type', types[path.extname(file)] || 'application/octet-stream'); res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  let browser;
  try { browser = await chromium.launch({ headless: true, channel: process.env.DV10_BROWSER_CHANNEL || 'msedge' }); }
  catch (error) { server.close(); throw error; }
  const page = await browser.newPage({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  const calls = []; const errors = []; let view = fixture(); let delayed = false; let unavailable = false;
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/guest/v1/**', async route => {
    const request = route.request(); const url = new URL(request.url());
    calls.push({ path: url.pathname, query: url.search, method: request.method(), headers: request.headers(), body: request.postData() });
    const json = body => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.pathname.endsWith('/bootstrap')) return json({ schemaVersion: 1, csrfToken: 'synthetic-csrf' });
    if (/\/(claim|recover|share-claim)$/.test(url.pathname)) return json({ schemaVersion: 1, locator: ownerId, editionId });
    if (url.pathname.includes('/assets/')) return route.fulfill({ status: 200, contentType: 'image/svg+xml', body: artwork });
    if (url.pathname.endsWith('/exports')) return json({ schemaVersion: 1, asset: { id: artId } });
    if (url.pathname.endsWith('/portrait-consents')) return json({ schemaVersion: 1, consentVersion: 1 });
    if (url.pathname.endsWith('/portrait-uploads')) return json({ schemaVersion: 1, upload: { id: 'synthetic-upload' } });
    if (url.pathname.endsWith('/portraits')) return json({ schemaVersion: 1, job: { id: 'synthetic-job' } });
    if (url.pathname.endsWith('/share-links')) return json({ schemaVersion: 1, url: origin + '/keepsake/#s=synthetic-share-key', expiresAt: '2026-10-21T12:00:00Z' });
    if (url.pathname.endsWith('/history')) return json(view);
    if (request.method() === 'DELETE') return json({ schemaVersion: 1, receipt: { id: 'synthetic-receipt', scope: 'all', accessRevoked: true, localState: 'completed', providerState: 'not_confirmed', backupState: 'pending' } });
    if (url.pathname.includes('/deletions/')) return json({ schemaVersion: 1, receipt: { id: 'synthetic-receipt', scope: 'all', localState: 'completed', providerState: 'not_confirmed', backupState: 'pending' } });
    if (unavailable) return route.fulfill({ status: 404, contentType: 'application/json', body: '{"schemaVersion":1,"errorCode":"access_unavailable"}' });
    if (delayed && url.pathname.endsWith(ownerId)) { await new Promise(resolve => setTimeout(resolve, 450)); return json(fixture('STARÝ HOST')); }
    return json(url.pathname.endsWith(otherId) ? fixture('JINÝ HOST') : view);
  });
  try {
    await page.goto(origin + '/keepsake/#c=synthetic-claim-secret');
    assert.equal(new URL(page.url()).hash, '', 'Secret fragment must disappear before user interaction');
    assert.equal(calls.length, 0, 'Landing must not exchange or generate without click');
    async function privacyRoundTrip() {
      const originalUrl = page.url();
      await page.locator('header [data-privacy]').click();
      await page.locator('#privacy').waitFor({ state: 'visible' });
      await page.getByRole('button', { name: 'Zpět k archivu', exact: true }).click();
      assert.equal(page.url(), originalUrl, 'Privacy must preserve locator, scope and in-memory entry');
    }
    await privacyRoundTrip();
    assert.equal(calls.length, 0, 'Reading privacy must not consume the claim');
    await page.locator('#exchange').click();
    await page.locator('#wand-name').filter({ hasText: 'Brk bouřlivého archiváře' }).waitFor();
    await privacyRoundTrip();
    assert.equal(calls.filter(c => c.path.endsWith('/claim')).length, 1);
    for (const call of calls.filter(c => c.method !== 'GET' && !c.path.endsWith('/bootstrap'))) {
      assert.equal(call.headers['x-dv10-csrf'], 'synthetic-csrf');
      assert.match(call.headers['idempotency-key'], /^[0-9a-f-]{36}$/);
    }
    assert.equal(await page.evaluate(() => ({ local: localStorage.length, session: sessionStorage.length })).then(s => s.local + s.session), 0);
    for (const width of [320, 390, 430, 844, 1280]) {
      await page.setViewportSize({ width, height: width === 844 ? 390 : 844 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `Layout must fit ${width}px`);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.locator('[data-theme-select]').selectOption('dark');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark');
    await page.locator('[data-theme-select]').selectOption('light');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'light');
    await page.emulateMedia({ colorScheme: 'dark' });
    await page.locator('[data-theme-select]').selectOption('system');
    assert.equal(await page.locator('html').getAttribute('data-theme'), 'dark', 'System preference drives theme without storage');
    await page.emulateMedia({ colorScheme: 'light' });
    await page.waitForFunction(() => document.documentElement.dataset.theme === 'light');
    await page.evaluate(() => { document.body.style.zoom = '2'; });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, '200% zoom must fit');
    await page.evaluate(() => { document.body.style.zoom = ''; });
    assert.equal(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches), true);
    await page.keyboard.press('Tab');
    assert.notEqual(await page.evaluate(() => document.activeElement.tagName), 'BODY', 'Keyboard focus must reach an interactive control');
    const beforePhoto = calls.length;
    await page.locator('#photo').setInputFiles({ name: 'fixture.png', mimeType: 'image/png', buffer: photo });
    await page.locator('#photo-preview').waitFor({ state: 'visible' });
    assert.equal(calls.length, beforePhoto, 'Local preview must not upload');
    assert.equal(await page.locator('#generate-portrait').isDisabled(), true);
    await page.locator('#photo-consent').check();
    await page.locator('#generate-portrait').click();
    await page.waitForFunction(() => document.querySelector('#photo').value === '');
    assert.equal(calls.filter(c => c.path.endsWith('/portrait-uploads')).length, 1);
    assert.equal(calls.filter(c => c.path.endsWith('/portraits')).length, 1);
    await page.locator('#save-main').click();
    await page.getByRole('heading', { name: 'Soubor je připravený' }).waitFor();
    const exportCall = calls.find(c => c.path.endsWith('/exports'));
    assert.equal(JSON.parse(exportCall.body).portraitId, null, 'Standard ZIP must exclude portrait');
    await page.getByRole('button', { name: 'Zavřít', exact: true }).click();
    view = fixture('<img src=x onerror=alert(1)>');
    await page.goto(origin + `/keepsake/?k=${ownerId}&view=share&editionId=${editionId}`);
    await page.locator('#wand-name').filter({ hasText: '<img src=x' }).waitFor();
    assert.equal(await page.locator('#wand-name img').count(), 0, 'Guest content must remain text');
    assert.equal(await page.locator('#portrait-section').isVisible(), false, 'Share route must hide owner photo controls even with owner DTO');
    assert.equal(await page.locator('#owner-tools').isVisible(), false);
    await privacyRoundTrip();
    assert(calls.some(c => c.query === `?view=share&editionId=${editionId}`), 'Share reads must explicitly constrain edition and scope');
    assert.equal(await page.locator('#save-main').isDisabled(), true, 'Missing shared ZIP cannot trigger an owner mutation');
    const shareMutations = calls.filter(c => c.method !== 'GET' && c.query.includes('view=share'));
    assert.equal(shareMutations.length, 0, 'Read-only share never creates exports');
    view = fixture(); view.editions[0].artworkAssetId = null;
    view.jobs = [{ id: 'synthetic-base-job', kind: 'base', state: 'awaiting_reconciliation' }];
    await page.goto(origin + `/keepsake/?k=${ownerId}`);
    await page.locator('#generation-status').filter({ hasText: 'Příběh už je hotový' }).waitFor();
    assert.equal(await page.locator('#wand-art').isVisible(), false, 'Partial edition must not fabricate artwork');
    assert.equal(await page.locator('[data-export="story"]').isDisabled(), false);
    assert.equal(await page.locator('[data-export="art"]').isDisabled(), true);
    unavailable = true;
    await page.goto(origin + `/keepsake/?k=${ownerId}`);
    await page.getByRole('heading', { name: 'Odkaz není dostupný' }).waitFor();
    assert.equal(await page.locator('#keepsake').isVisible(), false, 'Unavailable grant must remove personal view');
    unavailable = false;
    view = fixture(); delayed = true;
    await page.goto(origin + `/keepsake/?k=${ownerId}`);
    await page.evaluate(id => { history.pushState(null, '', '/keepsake/?k=' + id); dispatchEvent(new PopStateEvent('popstate')); }, otherId);
    await page.locator('#wand-name').filter({ hasText: 'JINÝ HOST' }).waitFor();
    await page.waitForTimeout(600);
    assert.equal(await page.locator('#wand-name').textContent(), 'JINÝ HOST', 'Old route result must not replace current guest');
    delayed = false; view = fixture();
    await page.goto(origin + `/keepsake/?k=${ownerId}`); await page.locator('#wand-name').filter({ hasText: 'Brk bouřlivého archiváře' }).waitFor();
    const screenshot = process.env.DV10_SCREENSHOT;
    if (screenshot) {
      if (process.env.DV10_SCREENSHOT_WIDTH) await page.setViewportSize({ width: Number(process.env.DV10_SCREENSHOT_WIDTH), height: 900 });
      await page.screenshot({ path: screenshot, fullPage: true });
    }
    await page.locator('.delete-panel summary').click(); await page.locator('#delete-all').click();
    await page.locator('#delete-confirm').fill('SMAZAT'); await page.locator('#delete-submit').click();
    await page.locator('#deletion').waitFor({ state: 'visible' });
    assert.equal(await page.locator('#keepsake').isVisible(), false);
    assert.equal(await page.locator('#wand-art').getAttribute('src'), null, 'Deletion must clear old asset buffers');
    assert.match(await page.locator('#deletion-status').textContent(), /nepotvrzeno.*probíhá/);
    assert.deepEqual(errors, [], 'No page errors allowed');
    console.log('PASS synthetic browser: explicit claim, CSRF/idempotency, no storage, 320/390/430/landscape/desktop, 200% zoom/keyboard/reduced motion, local photo opt-in, ZIP exclusion, share scope/XSS, partial/unavailable states, stale route isolation and deletion receipt. No OpenAI or iPhone acceptance claimed.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });

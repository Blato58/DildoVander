/* Repeatable synthetic public-evening browser checks; no provider or server mutation. */
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const zlib = require('node:zlib');
const { chromium } = require('playwright');
const { assertSharedDialog } = require('./test-dialog.cjs');
const repo = path.resolve(__dirname, '../../..');
const guid = number => `00000000-0000-4000-8000-${String(number).padStart(12, '0')}`;

function syntheticPng() {
  const width = 360; const height = 440;
  const pixels = Buffer.alloc((width * 3 + 1) * height);
  for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
    const t = ((x - 108) * 126 + (y - 348) * -264) / (126 ** 2 + 264 ** 2);
    const distance = Math.hypot(x - 108 - t * 126, y - 348 + t * 264);
    const ring = Math.abs(Math.hypot(x - 180, y - 210) - 130) < 1;
    const color = t >= 0 && t <= 1 && distance < (t < .23 ? 10 : 5)
      ? (t < .23 ? [107, 126, 142] : [217, 183, 119]) : ring ? [52, 66, 79] : [23, 31, 41];
    const offset = y * (width * 3 + 1) + 1 + x * 3;
    color.forEach((value, index) => { pixels[offset + index] = value; });
  }
  const crc = buffer => {
    let result = 0xffffffff;
    for (const byte of buffer) { result ^= byte; for (let i = 0; i < 8; i++) result = (result >>> 1) ^ (0xedb88320 & -(result & 1)); }
    return (result ^ 0xffffffff) >>> 0;
  };
  const chunk = (type, bytes) => {
    const name = Buffer.from(type); const size = Buffer.alloc(4); size.writeUInt32BE(bytes.length);
    const checksum = Buffer.alloc(4); checksum.writeUInt32BE(crc(Buffer.concat([name, bytes])));
    return Buffer.concat([size, name, bytes, checksum]);
  };
  const header = Buffer.alloc(13); header.writeUInt32BE(width); header.writeUInt32BE(height, 4); header[8] = 8; header[9] = 2;
  return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', header), chunk('IDAT', zlib.deflateSync(pixels)), chunk('IEND', Buffer.alloc(0))]);
}
const image = syntheticPng();
const item = (number, name) => ({
  wizardIdentity: { visibility: 'public_fiction', wizardAlias: 'Inspektor přestávek ' + number,
    houseOrGuild: 'Cech přestávek', dubiousSpecialization: 'Odkládání formulářů', signatureAbility: 'Přestávka',
    weakness: 'Úklid', ministryAssessment: 'Fiktivní ministerstvo schvaluje.', wandBond: 'Katalogová hůlka',
    motto: 'Zítra.', microStory: 'Komediální fikce.', omen: 'Ještě jedno.', revelation: 'TAJNÝ TEXT NEPATŘÍ VEŘEJNOSTI' },
  locator: guid(number), editionId: guid(number + 1000), officialName: name || `Hůlka číslo ${number}`,
  catalogId: 'synthetic-catalog', slot: 2, createdAt: '2026-10-14T12:00:00Z', synthetic: true,
  story: { title: `Příběh ${number} · poslední slovo kufru`, text: 'Tato syntetická legenda slouží pouze browser testu.\n\nHůlka se usadila v archivu a na každou otázku odpovídala dalším záznamem.' },
  assets: ['base_image', 'export_story', 'export_card', 'export_zip'].map((kind, index) => ({
    id: guid(number * 10 + index), kind, mediaType: kind === 'export_zip' ? 'application/zip' : kind === 'export_story' ? ' Text/Plain; charset=utf-8 ' : 'image/png', length: 100, sha256: 'synthetic'
  }))
});
const eveningA = guid(9001); const eveningB = guid(9002);

(async () => {
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    const file = url.pathname.startsWith('/design-system/')
      ? path.join(repo, 'kufr', url.pathname) : path.join(repo, 'kufr/web', url.pathname.endsWith('/') ? url.pathname + 'index.html' : url.pathname);
    if (!file.startsWith(path.join(repo, 'kufr')) || !fs.existsSync(file)) { res.writeHead(404); return res.end(); }
    res.setHeader('Content-Type', { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf' }[path.extname(file)] || 'application/octet-stream');
    res.end(fs.readFileSync(file));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const origin = `http://127.0.0.1:${server.address().port}`;
  let browser;
  try { browser = await chromium.launch({ headless: true, channel: process.env.DV10_BROWSER_CHANNEL || 'msedge' }); }
  catch (error) { server.close(); throw error; }
  const context = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  await context.addCookies([{ name: 'synthetic-private-cookie', value: 'must-not-reach-public-api', url: origin }]);
  const page = await context.newPage(); const calls = []; const errors = [];
  const removedNumbers = new Set();
  await page.addInitScript(() => {
    window.__revokedPublicUrls = [];
    const originalRevoke = URL.revokeObjectURL;
    URL.revokeObjectURL = function (url) { window.__revokedPublicUrls.push(url); return originalRevoke.call(URL, url); };
  });
  let active = eveningA; let latestNumber = 3; let delayedPage = false; let unavailable = false; let empty = false; let preparing = false;
  let assetDelayMs = 0; let omitCard = false;
  page.on('pageerror', error => errors.push(error.message));
  await page.route('**/guest/v1/**', async route => {
    const request = route.request(); const url = new URL(request.url());
    calls.push({ path: url.pathname, query: url.search, method: request.method(), headers: request.headers() });
    if (unavailable) return route.fulfill({ status: 503, body: '{}' });
    if (url.pathname === '/guest/v1/evening') {
      const hasCursor = url.searchParams.has('cursor');
      if (hasCursor && active === eveningB) return route.fulfill({ status: 409, contentType: 'application/json', body: '{"schemaVersion":1,"errorCode":"evening_changed"}' });
      const response = empty ? { schemaVersion: 1, evening: null, latest: null, items: [], nextCursor: null }
        : { schemaVersion: 1, evening: { id: active, label: 'Aktivní večer' }, latest: preparing ? null : item(latestNumber),
          items: hasCursor ? [item(10, 'Dřívější archivní hůlka')] : [item(latestNumber), item(2, '<img src=x onerror=alert(1)>'), item(1)], nextCursor: hasCursor ? null : 'synthetic-a-page2' };
      if (preparing && response.items.length) response.items = [{ ...item(latestNumber), assets: [] }];
      response.items = response.items.filter(entry => !removedNumbers.has(Number(entry.locator.split('-').at(-1))));
      if (response.latest && removedNumbers.has(Number(response.latest.locator.split('-').at(-1)))) response.latest = null;
      // These deliberately forbidden fields must never enter the DOM or downloads.
      response.portraits = [{ assetId: guid(8888), text: 'PRIVATE-PORTRAIT-SENTINEL' }];
      response.uploads = ['PRIVATE-PHOTO-SENTINEL']; response.prompt = 'PRIVATE-PROMPT-SENTINEL';
      for (const entry of [...response.items, response.latest].filter(Boolean)) {
        if (omitCard) entry.assets = entry.assets.filter(asset => asset.kind !== 'export_card');
        entry.assets.push({ id: guid(7777), kind: 'photo_original', mediaType: 'image/png' });
        ['base_image', 'export_story', 'export_card', 'export_zip'].forEach((kind, index) => {
          entry.assets.unshift({ id: guid(7790 + index), kind, mediaType: kind === 'export_story' ? 'image/png' : 'text/plain' });
        });
      }
      if (hasCursor && delayedPage) await new Promise(resolve => setTimeout(resolve, 500));
      return route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(response) });
    }
    const tail = Number(url.pathname.split('/').at(-1).split('-').at(-1));
    const mediaType = tail % 10 === 1 ? 'text/plain; charset=utf-8' : tail % 10 === 3 ? 'application/zip' : 'image/png';
    if (tail % 10 === 3 && assetDelayMs) await new Promise(resolve => setTimeout(resolve, assetDelayMs));
    return route.fulfill({ status: 200, contentType: mediaType, body: mediaType.startsWith('text/plain') ? 'Syntetický příběh.' : image });
  });
  try {
    await page.goto(origin + '/keepsake/evening/');
    await page.locator('#public-name').filter({ hasText: 'Hůlka číslo 3' }).waitFor();
    assert.match(await page.locator('#public-wizard-identity').textContent(), /Inspektor přestávek 3/);
    assert.equal(await page.locator('body').textContent().then(text => text.includes('TAJNÝ TEXT NEPATŘÍ VEŘEJNOSTI')), false);
    await page.locator('#public-art').waitFor({ state: 'visible' });
    await page.locator('#public-art-open').click();
    await page.locator('#public-art-dialog').waitFor({ state: 'visible' });
    await assertSharedDialog(page, '#public-art-dialog');
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => !document.querySelector('#public-art-dialog').open
      && document.activeElement.id === 'public-art-open');
    assert.equal(calls.filter(c => c.path.includes('/assets/')).length, 1, 'Initial page loads one selected image, no thumbnail burst');
    assert.equal(await page.locator('#evening-list img').count(), 0);
    assert.equal(await page.locator('#evening-list button').count(), 3);
    assert.equal(await page.evaluate(() => !!(document.querySelector('#evening-list-section').compareDocumentPosition(document.querySelector('#public-edition')) & Node.DOCUMENT_POSITION_FOLLOWING)), true, 'Pickup selection precedes long edition content');
    assert.equal(await page.locator('[type="file"]').count(), 0);
    assert.doesNotMatch(await page.locator('body').textContent(), /PRIVATE-(PORTRAIT|PHOTO|PROMPT)-SENTINEL/);
    for (const width of [320, 390, 430, 844, 1280]) {
      await page.setViewportSize({ width, height: width === 844 ? 390 : 844 });
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, `Layout must fit ${width}px`);
    }
    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate(() => { document.body.style.zoom = '2'; });
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, '200% zoom must fit');
    await page.evaluate(() => { document.body.style.zoom = ''; });
    assert.equal(await page.evaluate(() => matchMedia('(prefers-reduced-motion: reduce)').matches), true);
    await page.locator('#evening-list button').first().click();
    assert.equal(await page.locator('#public-name').evaluate(node => node === document.activeElement), true, 'Pointer selection reveals and focuses the selected edition');
    const prior = page.locator('#evening-list button').nth(1);
    await prior.focus(); await page.keyboard.press('Enter');
    await page.locator('#public-name').filter({ hasText: '<img src=x' }).waitFor();
    assert.equal(await prior.evaluate(node => node === document.activeElement), true, 'Selection preserves keyboard focus');
    assert.equal(await page.locator('#public-name img').count(), 0, 'Official name is text, not HTML');
    latestNumber = 4;
    await page.locator('#refresh-evening').click();
    await page.locator('#evening-list strong').filter({ hasText: 'Hůlka číslo 4' }).waitFor();
    assert.match(await page.locator('#public-name').textContent(), /<img src=x/, 'Same-evening refresh preserves a prior selection');
    await page.locator('#select-latest').click();
    await page.locator('#public-name').filter({ hasText: 'Hůlka číslo 4' }).waitFor();
    for (const kind of ['export_story', 'base_image', 'export_card', 'export_zip']) {
      await page.locator(`[data-public-download="${kind}"]`).click();
      await page.getByRole('heading', { name: 'Památka je připravená k uložení' }).waitFor();
      await assertSharedDialog(page, '[data-public-file]');
      assert.match(await page.locator('[data-public-file] a').getAttribute('download'), kind === 'export_story' ? /\.txt$/ : kind === 'export_zip' ? /\.zip$/ : /\.png$/);
      if (kind === 'export_story') {
        const pendingDownload = page.waitForEvent('download');
        await page.locator('[data-public-file] a').click();
        const downloaded = await pendingDownload;
        assert.equal(downloaded.suggestedFilename(), 'story.txt', 'Charset MIME must produce a real TXT download');
        const chunks = []; const stream = await downloaded.createReadStream();
        for await (const chunk of stream) chunks.push(chunk);
        assert.equal(Buffer.concat(chunks).toString('utf8'), 'Syntetický příběh.', 'Downloaded story must preserve UTF-8');
      }
      await page.getByRole('button', { name: 'Zavřít', exact: true }).click();
    }
    omitCard = true;
    await page.locator('#refresh-evening').click();
    await page.waitForFunction(() => document.querySelector('[data-public-download="export_card"]').disabled);
    assetDelayMs = 800;
    const metadataBeforeDownload = calls.filter(call => call.path === '/guest/v1/evening').length;
    await page.locator('[data-public-download="export_zip"]').click();
    omitCard = false;
    await page.locator('#refresh-evening').click();
    await page.waitForFunction(() => !document.querySelector('[data-public-download="export_card"]').disabled);
    assert.equal(await page.locator('[data-public-download="export_zip"]').isDisabled(), true, 'Adding a different export keeps the pending ZIP guarded');
    await page.locator('#refresh-evening').click();
    await page.getByRole('heading', { name: 'Památka je připravená k uložení' }).waitFor();
    assert.equal(await page.locator('[data-public-file] a').getAttribute('download'), 'bundle.zip', 'Slow ZIP survives stable metadata refreshes');
    assert(calls.filter(call => call.path === '/guest/v1/evening').length >= metadataBeforeDownload + 2, 'Slow asset completed across metadata polling and unrelated export publication');
    assetDelayMs = 0;
    await page.getByRole('button', { name: 'Zavřít', exact: true }).click();
    await page.locator('#evening-list button').nth(1).click();
    await page.locator('[data-public-download="export_story"]').click();
    await page.getByRole('heading', { name: 'Památka je připravená k uložení' }).waitFor();
    const removedFileUrl = await page.locator('[data-public-file] a').getAttribute('href');
    removedNumbers.add(2);
    // A background refresh can complete while the native modal blocks pointer taps.
    await page.locator('#refresh-evening').evaluate(button => button.click());
    await page.locator('#public-name').filter({ hasText: 'Hůlka číslo 4' }).waitFor();
    assert.equal(await page.locator('[data-public-file]').count(), 0, 'Removing selected edition closes its prepared-file dialog');
    assert.equal(await page.evaluate(url => window.__revokedPublicUrls.includes(url), removedFileUrl), true, 'Removing selected edition revokes its prepared blob URL');
    removedNumbers.delete(2);
    await page.locator('#load-more').click();
    await page.locator('#evening-list strong').filter({ hasText: 'Dřívější archivní hůlka' }).waitFor();
    assert.equal(await page.locator('#evening-list button').count(), 1, 'Pagination replaces one bounded text page');
    await page.locator('#evening-list button').click();
    await page.locator('#public-name').filter({ hasText: 'Dřívější archivní hůlka' }).waitFor();
    active = eveningB; latestNumber = 101;
    await page.locator('#refresh-evening').click();
    await page.locator('#public-name').filter({ hasText: 'Hůlka číslo 101' }).waitFor();
    assert.doesNotMatch(await page.locator('#public-story').textContent(), /Dřívější archivní/);
    assert(calls.some(c => c.path.includes(eveningB)), 'New asset requests bind to the new evening');
    active = eveningA; latestNumber = 3;
    await page.locator('#refresh-evening').click(); await page.locator('#public-name').filter({ hasText: 'Hůlka číslo 3' }).waitFor();
    delayedPage = true; await page.locator('#load-more').click();
    active = eveningB; latestNumber = 102; await page.locator('#refresh-evening').click();
    await page.locator('#public-name').filter({ hasText: 'Hůlka číslo 102' }).waitFor(); await page.waitForTimeout(600);
    assert.equal(await page.locator('#public-name').textContent(), 'Hůlka číslo 102', 'Stale page cannot restore old evening');
    assert.equal(await page.locator('#load-more').textContent(), 'Načíst další hůlky', 'Cancelled paging must restore its action label');
    assert.equal(await page.locator('#load-more').getAttribute('aria-busy'), null);
    delayedPage = false; preparing = true; await page.locator('#refresh-evening').click();
    await page.locator('#public-art-status').filter({ hasText: 'Obraz ještě není připravený' }).waitFor();
    assert.equal(await page.locator('[data-public-download="export_zip"]').isDisabled(), true);
    preparing = false; empty = true; await page.locator('#refresh-evening').click();
    await page.getByRole('heading', { name: 'Večer ještě nezačal' }).waitFor();
    assert.equal(await page.locator('#public-art').getAttribute('src'), null);
    empty = false; unavailable = true; await page.locator('#refresh-evening').click();
    await page.getByRole('heading', { name: 'Archiv je právě nedostupný' }).waitFor();
    assert.equal(await page.locator('#public-edition').isVisible(), false);
    unavailable = false; await page.locator('#refresh-evening').click(); await page.locator('#public-art').waitFor({ state: 'visible' });
    if (process.env.DV10_EVENING_SCREENSHOT) {
      if (process.env.DV10_SCREENSHOT_WIDTH) await page.setViewportSize({ width: Number(process.env.DV10_SCREENSHOT_WIDTH), height: 900 });
      await page.screenshot({ path: process.env.DV10_EVENING_SCREENSHOT, fullPage: true });
    }
    for (const call of calls) {
      assert.equal(call.method, 'GET', 'Public page must remain read-only');
      assert.equal(call.headers.cookie, undefined, 'Public API must omit an existing private cookie');
      assert(!call.path.includes('/bootstrap') && !call.path.includes('/portraits') && !call.path.includes(guid(7777)));
      for (let index = 0; index < 4; index++) assert(!call.path.includes(guid(7790 + index)), 'Mismatched kind/media metadata must never become a public asset request');
    }
    assert.equal(await page.evaluate(() => localStorage.length + sessionStorage.length), 0);
    assert.deepEqual(errors, []);
    console.log('PASS synthetic public evening: one artwork, latest/prior+focus, bounded pagination, two-evening409/stale isolation, GET-only/no cookies/storage, base exports, real UTF-8 charset TXT download+kind/media rejection, slow ZIP survives stable refresh+new export, removed-selection dialog/blob revocation, private data/XSS exclusion, pending/empty/unavailable, 320/390/430/landscape/desktop, 200% zoom/keyboard/reduced motion. No OpenAI, deployment or iPhone acceptance claimed.');
  } finally { await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });

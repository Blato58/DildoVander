// Production-compiled collection component with synthetic APIs and fresh Chrome.
// Identity, migrations, host integration and signed native delivery need separate evidence.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createServer } from 'node:http';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'vite';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('../', import.meta.url));
const sourceDigests = {};
for (const file of ['src/features/identity/AccountArchiveList.vue', 'src/features/identity/accountCollection.ts']) {
  sourceDigests[file] = createHash('sha256').update(await readFile(path.join(root, file))).digest('hex');
}
const evidence = path.resolve(process.env.COLLECTION_UI_EVIDENCE ?? path.join(tmpdir(), 'dildovander-account-collection'));
const cache = path.join(root, 'node_modules', '.cache');
await mkdir(evidence, { recursive: true });
await mkdir(cache, { recursive: true });
const input = await mkdtemp(path.join(cache, 'collection-browser-'));
const inputUrl = '/' + path.relative(root, input).split(path.sep).join('/');
await writeFile(path.join(input, 'index.html'), `<!doctype html><html lang="cs"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="app"></div><script type="module" src="${inputUrl}/entry.js"></script></body></html>`);
await writeFile(path.join(input, 'harness.css'), '.collection-harness { max-width: 64rem; margin: 0 auto; padding: 1rem; } html.collection-large-text { font-size: 200% !important; } html.collection-large-text, html.collection-large-text * { transition: none !important; animation: none !important; }');
await writeFile(path.join(input, 'entry.js'), `
import { createApp, h, ref } from 'vue';
import { createRouter, createWebHistory } from 'vue-router';
import Widget from '/src/features/identity/AccountArchiveList.vue';
import { account } from '/src/features/identity/session';
import '/src/application/application.css';
import './harness.css';
const locale = ref('cs');
const setAccount = id => { account.value = id ? { id, displayName: 'Synthetic owner', roles: ['Viewer'] } : null; };
setAccount('synthetic-owner');
window.collectionHarness = { setAccount, setLocale(value) { locale.value = value; document.documentElement.lang = value; } };
const router = createRouter({ history: createWebHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { render: () => null } }] });
createApp({ render: () => h('main', { class: 'collection-harness' }, [h(Widget, { locale: locale.value })]) }).use(router).mount('#app');
`);
const dist = path.join(evidence, 'dist');
const compiled = await build({ root, configFile: path.join(root, 'vite.config.mjs'), logLevel: 'error',
  build: { outDir: dist, emptyOutDir: false, rollupOptions: { input: path.join(input, 'index.html') } } });
const outputs = Array.isArray(compiled) ? compiled.flatMap(result => result.output) : compiled.output;
const html = outputs.find(output => output.type === 'asset' && output.fileName.endsWith('.html'));
assert.ok(html, 'production build emitted an HTML entry');

const collectionPath = '/app/v1/archive/collection';
const associationPath = '/app/v1/archive/associations/';
const requests = [];
const errors = [];
const snapshots = [];
const fixture = { mode: 'mixed', empty: false, failure: false, delay: 0, linked: true, paged: false };
const states = {
  pending: ['awaiting_result', false, true, false, false],
  processing: [null, false, true, false, false],
  partial: ['reconciliation_required', false, true, true, false],
  ready: [null, false, false, true, true],
  failed: ['generation_failed', false, false, false, false],
  unavailable: ['result_unavailable', false, false, false, false],
};
const locator = number => `30000000-0000-4000-8000-${String(number).padStart(12, '0')}`;
function item(number, state = 'ready', owned = true) {
  const [reason, canRetry, shouldPoll, hasStory, hasArtwork] = states[state];
  return { locator: locator(number), linkedAt: 1792742400 - number,
    officialName: number === 1 ? 'Hvezdna poutnice' : number === 2 ? 'Propojena pamatka' : `Pamatka ${number}`,
    storyTitle: hasStory ? 'Pribeh pod vecerni oblohou' : null, hasPortrait: number === 1,
    accessKind: owned ? 'originalOwner' : 'linkedViewer', canManage: owned, canReadPrivate: owned, canUnlink: !owned,
    readiness: { state, reason, canRetry, shouldPoll, hasStory, hasArtwork },
    characterName: owned ? 'Aria' : null, eveningAt: owned ? 1792656000 : null };
}
function pageFixture(url) {
  let items = fixture.empty ? [] : fixture.mode === 'mixed'
    ? [item(1), ...(fixture.linked ? [item(2, 'ready', false)] : []), item(3, 'processing'), item(4, 'failed')]
    : [item(1, fixture.mode)];
  const q = (url.searchParams.get('q') ?? '').toLowerCase();
  const access = url.searchParams.get('access') ?? 'all';
  items = items.filter(row => (!q || [row.officialName, row.storyTitle, row.characterName].some(value => value?.toLowerCase().includes(q)))
    && (access === 'all' || access === 'owned' && row.canManage || access === 'linked' && row.canUnlink));
  if (fixture.paged && !q && access === 'all') {
    if (url.searchParams.has('cursor')) return { schemaVersion: 1, items: [item(1), item(5)], nextCursor: null };
    return { schemaVersion: 1, items, nextCursor: 'synthetic-page-2' };
  }
  return { schemaVersion: 1, items, nextCursor: null };
}
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname === collectionPath && request.method === 'GET') {
      requests.push({ method: 'GET', path: url.pathname, query: Object.fromEntries(url.searchParams) });
      const body = pageFixture(url);
      if (fixture.delay) await new Promise(resolve => setTimeout(resolve, fixture.delay));
      response.writeHead(fixture.failure ? 503 : 200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      response.end(JSON.stringify(fixture.failure ? { status: 503, code: 'synthetic_unavailable' } : body));
      return;
    }
    if (url.pathname.startsWith(associationPath) && request.method === 'DELETE') {
      requests.push({ method: 'DELETE', path: url.pathname });
      assert.equal(url.pathname, associationPath + locator(2));
      fixture.linked = false;
      response.writeHead(204).end();
      return;
    }
    if (request.method !== 'GET' || /^\/(app|guest|v1)\//.test(url.pathname)) { response.writeHead(404).end(); return; }
    const file = url.pathname === '/' ? path.join(dist, html.fileName) : path.resolve(dist, '.' + decodeURIComponent(url.pathname));
    const relative = path.relative(dist, file);
    if (relative.startsWith('..') || path.isAbsolute(relative)) { response.writeHead(403).end(); return; }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  try { browser = await chromium.launch(); }
  catch (error) { if (process.platform !== 'win32') throw error; browser = await chromium.launch({ channel: 'chrome' }); }
  const context = await browser.newContext({ reducedMotion: 'reduce' });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('503')) errors.push(message.text()); });
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    for (const mode of [...Object.keys(states), 'mixed']) {
      fixture.mode = mode;
      await page.goto(origin);
      await page.locator('.collection-card').first().waitFor();
      assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, `${mode} at ${width}px does not overflow`);
      assert.match(await page.locator('.collection-card a').first().getAttribute('href'), /^\/keepsake\/\?account=1&k=[a-f0-9-]{36}$/i);
      snapshots.push({ width, mode, cards: await page.locator('.collection-card').count(), overflow: false });
      if (mode === 'mixed') await page.screenshot({ path: path.join(evidence, `collection-${width}.png`), fullPage: true });
    }
  }
  fixture.delay = 300;
  await page.goto(origin);
  await page.getByText('Načítám vaši kolekci…', { exact: true }).waitFor();
  await page.locator('.collection-card').first().waitFor();
  fixture.delay = 0;
  assert.equal(await page.getByRole('button', { name: 'Odpojit od účtu', exact: true }).count(), 1, 'only linked item offers unlink');
  await page.getByLabel('Hledat v kolekci', { exact: true }).fill('no-matching-result');
  await page.getByRole('button', { name: 'Hledat', exact: true }).click();
  await page.getByText('Filtrům neodpovídají žádné výsledky.', { exact: true }).waitFor();
  await page.getByLabel('Hledat v kolekci', { exact: true }).fill('Aria');
  await page.getByRole('button', { name: 'Hledat', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 3);
  assert.equal(requests.at(-1).query.q, 'Aria');
  await page.getByLabel('Hledat v kolekci', { exact: true }).fill('');
  await page.getByLabel('Přístup', { exact: true }).selectOption('linked');
  await page.getByRole('button', { name: 'Hledat', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 1);
  assert.equal(requests.at(-1).query.access, 'linked');
  assert.equal(await page.locator('.collection-card').innerText().then(text => text.includes('Aria')), false);
  page.once('dialog', dialog => dialog.accept());
  await page.getByRole('button', { name: 'Odpojit od účtu', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 0);
  fixture.linked = true;
  fixture.paged = true;
  await page.goto(origin);
  await page.getByRole('button', { name: 'Načíst další', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 5);
  assert.equal(requests.at(-1).query.cursor, 'synthetic-page-2');
  assert.equal(await page.locator(`.collection-card a[href*="${locator(1)}"]`).count(), 1, 'duplicate locator appears once');
  fixture.paged = false;
  fixture.linked = false;
  await page.getByRole('button', { name: 'Obnovit kolekci', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 3);
  fixture.linked = true;
  await page.evaluate(() => window.collectionHarness.setLocale('en'));
  await page.getByLabel('Search your collection', { exact: true }).waitFor();
  await page.keyboard.press('Tab');
  const focused = await page.evaluate(() => {
    const element = document.activeElement;
    const css = getComputedStyle(element);
    return { tag: element.tagName, outline: css.outlineStyle, shadow: css.boxShadow, height: element.getBoundingClientRect().height };
  });
  assert.ok(['BUTTON', 'A', 'INPUT', 'SELECT'].includes(focused.tag));
  assert.ok(focused.outline !== 'none' || focused.shadow !== 'none', 'keyboard focus has a visible indicator');
  assert.ok(focused.height >= 44, 'focused control keeps its minimum touch height');
  const normalFontSize = await page.evaluate(() => Number.parseFloat(getComputedStyle(document.documentElement).fontSize));
  await page.evaluate(() => document.documentElement.classList.add('collection-large-text'));
  await page.waitForFunction(expected => Number.parseFloat(getComputedStyle(document.documentElement).fontSize) >= expected, normalFontSize * 2);
  const largeText = [];
  for (const locale of ['en', 'cs']) {
    await page.evaluate(value => window.collectionHarness.setLocale(value), locale);
    const metrics = await page.evaluate(() => ({ locale: document.documentElement.lang, rootFontSize: getComputedStyle(document.documentElement).fontSize,
      overflow: document.documentElement.scrollWidth > innerWidth + 1 }));
    assert.equal(metrics.overflow, false, 'actual 200% text at390px does not overflow');
    const lastLink = page.locator('.collection-card a').last();
    await lastLink.scrollIntoViewIfNeeded();
    assert.ok(await lastLink.isVisible(), 'last result remains reachable at large text');
    largeText.push(metrics);
    await page.screenshot({ path: path.join(evidence, `large-text-${locale}-390.png`), fullPage: true });
  }
  await page.getByLabel('Hledat v kolekci', { exact: true }).fill('Aria');
  await page.getByRole('button', { name: 'Hledat', exact: true }).click();
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 3);
  await context.setOffline(true);
  await page.waitForFunction(() => document.querySelectorAll('.collection-card').length === 0);
  assert.equal(await page.locator('.collection-card').count(), 0, 'offline clears private results');
  await context.setOffline(false);
  await page.locator('.collection-card').first().waitFor();
  assert.equal(await page.getByLabel('Hledat v kolekci', { exact: true }).inputValue(), '', 'reconnect does not restore a private search term');
  assert.equal(requests.at(-1).query.q, undefined, 'reconnect does not replay a private search term');
  await page.evaluate(() => window.collectionHarness.setAccount(null));
  assert.equal(await page.locator('.collection-card').count(), 0, 'signout clears private results');
  fixture.empty = true;
  await page.goto(origin);
  await page.locator('.account-collection').waitFor();
  await page.getByText('Tento účet zatím nemá žádné dostupné výsledky.', { exact: true }).waitFor();
  fixture.empty = false;
  fixture.failure = true;
  await page.goto(origin);
  await page.getByRole('button', { name: 'Obnovit kolekci', exact: true }).waitFor();
  await page.locator('.aw-notice').first().waitFor();
  assert.equal(await page.locator('.collection-card').count(), 0);
  fixture.failure = false;
  await page.getByRole('button', { name: 'Obnovit kolekci', exact: true }).click();
  await page.locator('.collection-card').first().waitFor();
  const writes = requests.filter(request => request.method !== 'GET');
  assert.deepEqual(writes, [{ method: 'DELETE', path: associationPath + locator(2) }], 'only explicit unlink mutates API');
  assert.equal(errors.length, 0, errors.join('\n'));
  for (const [file, expected] of Object.entries(sourceDigests)) {
    assert.equal(createHash('sha256').update(await readFile(path.join(root, file))).digest('hex'), expected, 'compiled source stayed unchanged');
  }
  await writeFile(path.join(evidence, 'evidence.json'), JSON.stringify({ kind: 'production-compiled isolated component with synthetic APIs', snapshots,
    sourceDigests, focused, largeText, requests, errors, checks: ['six readiness states', 'owned/linked distinction', 'CZ/EN', '390/1440', 'explicit search/filter',
      'cursor append/dedup', 'explicit linked-only unlink', 'revocation on refresh', 'actual 200% text', 'keyboard focus/44px', 'offline/signout clear', 'loading', 'filtered empty', 'empty', '503/recovery'],
    hostIntegration: 'separate owner', identityAndMigrationEvidence: 'separate backend tests', nativeDelivery: 'unverified' }, null, 2));
  console.log(`PASS compiled collection component: ${snapshots.length} state/viewports, filters/paging/unlink, CZ/EN/200% text, offline/signout; ${requests.length} API requests, zero generation, zero runtime errors`);
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}

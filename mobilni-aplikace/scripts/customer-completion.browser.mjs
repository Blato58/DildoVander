// Production-compiled isolated component, synthetic APIs and a fresh browser.
// This does not exercise signed native delivery, production identity or APNs.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'vite';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('../', import.meta.url));
const evidence = path.resolve(process.env.COMPLETION_UI_EVIDENCE ?? path.join(tmpdir(), 'dildovander-completion-ui'));
await mkdir(evidence, { recursive: true });
const cache = path.join(root, 'node_modules', '.cache');
await mkdir(cache, { recursive: true });
const input = await mkdtemp(path.join(cache, 'completion-browser-'));
const inputUrl = '/' + path.relative(root, input).split(path.sep).join('/');
await writeFile(path.join(input, 'index.html'), `<!doctype html><html lang="cs"><head><meta charset="UTF-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head><body><div id="app"></div><script type="module" src="${inputUrl}/entry.js"></script></body></html>`);
await writeFile(path.join(input, 'harness.css'), '.completion-harness-main { max-width: 56rem; margin: 0 auto; padding: 1rem; } html.completion-large-text { font-size: 200% !important; } html.completion-large-text, html.completion-large-text * { transition: none !important; animation: none !important; }');
await writeFile(path.join(input, 'entry.js'), `
import { createApp, h, ref } from 'vue';
import { createRouter, createWebHistory } from 'vue-router';
import Widget from '/src/features/customer/CustomerCompletionUpdates.vue';
import { account } from '/src/features/identity/session';
import '/src/application/application.css';
import './harness.css';
const locale = ref('cs');
account.value = { id: 'synthetic-owner', name: 'Synthetic owner', roles: ['Viewer'] };
window.completionHarness = {
  setLocale(value) { locale.value = value; document.documentElement.lang = value; },
  setAccount(id) { account.value = id ? { id, name: 'Synthetic owner', roles: ['Viewer'] } : null; },
};
const router = createRouter({ history: createWebHistory(), routes: [{ path: '/:pathMatch(.*)*', component: { render: () => null } }] });
createApp({ render: () => h('main', { class: 'completion-harness-main' }, [h(Widget, { locale: locale.value })]) }).use(router).mount('#app');
`);
const dist = path.join(evidence, 'dist');
const compiled = await build({ root, configFile: path.join(root, 'vite.config.mjs'), logLevel: 'error',
  build: { outDir: dist, emptyOutDir: false, rollupOptions: { input: path.join(input, 'index.html') } } });
const outputs = Array.isArray(compiled) ? compiled.flatMap(result => result.output) : compiled.output;
const html = outputs.find(output => output.type === 'asset' && output.fileName.endsWith('/index.html'));
assert.ok(html, 'the isolated component has a production HTML entry');

const eventId = number => `11111111-1111-4111-8111-${number.toString(16).padStart(12, '0')}`;
const resultId = number => `22222222-2222-4222-8222-${number.toString(16).padStart(12, '0')}`;
const fixture = { state: 'ready', empty: false, failure: false, delay: 0, read: new Set() };
const requests = [];
const errors = [];
const snapshots = [];
const completionPath = '/app/v1/customer/completion-updates';
const states = {
  pending: ['pending', 'awaiting_capacity', false, false],
  processing: ['processing', null, false, false],
  partial: ['partial', null, true, false],
  reconciliation: ['partial', 'reconciliation_required', true, false],
  terminal: ['partial', 'generation_failed', true, false],
  ready: ['ready', null, true, true],
  failed: ['failed', 'generation_failed', false, false],
  unavailable: ['unavailable', 'result_unavailable', false, false],
  portrait: ['ready', null, false, true, 'portrait'],
};
function pageFixture() {
  const number = Object.keys(states).indexOf(fixture.state) + 1;
  const [state, reason, hasStory, hasArtwork, kind = 'keepsake'] = states[fixture.state];
  const id = eventId(number);
  return { schemaVersion: 1, items: fixture.empty ? [] : [{ id, sequence: number, kind, state, reason,
    resultLocator: resultId(number), canRetry: false, hasStory, hasArtwork,
    occurredAt: '2026-10-23T12:00:00Z', readAt: fixture.read.has(id) ? '2026-10-23T12:01:00Z' : null }], nextCursor: null };
}
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (url.pathname.startsWith(completionPath)) {
      requests.push({ method: request.method, path: url.pathname });
      response.setHeader('Cache-Control', 'no-store');
      if (request.method === 'GET' && url.pathname === completionPath) {
        if (fixture.delay) await new Promise(resolve => setTimeout(resolve, fixture.delay));
        response.writeHead(fixture.failure ? 503 : 200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify(fixture.failure ? { status: 503, code: 'synthetic_unavailable' } : pageFixture()));
        return;
      }
      const match = new RegExp(`^${completionPath}/([a-f0-9-]{36})/read$`, 'i').exec(url.pathname);
      if (request.method === 'PUT' && match) {
        let body = '';
        for await (const chunk of request) body += chunk;
        assert.deepEqual(JSON.parse(body), { schemaVersion: 1 });
        fixture.read.add(match[1]);
        response.writeHead(204).end();
        return;
      }
      response.writeHead(404).end();
      return;
    }
    if (request.method !== 'GET' || /^\/(app|guest|v1)\//.test(url.pathname)) {
      response.writeHead(404).end();
      return;
    }
    const file = url.pathname === '/' ? path.join(dist, html.fileName)
      : path.resolve(dist, '.' + decodeURIComponent(url.pathname));
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
  catch (error) {
    if (process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }
  const context = await browser.newContext({ reducedMotion: 'reduce' });
  await context.addInitScript(() => {
    window.notificationPermissionRequests = 0;
    Object.defineProperty(window, 'Notification', { configurable: true, value: class {
      static permission = 'denied';
      static requestPermission() { window.notificationPermissionRequests++; return Promise.resolve('denied'); }
    } });
  });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && !message.text().includes('503')) errors.push(message.text()); });
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    for (const state of Object.keys(states)) {
      fixture.state = state;
      await page.goto(origin);
      await page.locator('.completion-updates__item').waitFor();
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1);
      assert.equal(overflow, false, `${state} at ${width}px does not overflow`);
      assert.match(await page.locator('.completion-updates__actions a').getAttribute('href'), /^\/keepsake\/\?account=1&k=[a-f0-9-]{36}$/i);
      assert.equal(await page.evaluate(() => window.notificationPermissionRequests), 0);
      snapshots.push({ width, state, text: await page.locator('.completion-updates__state').innerText(), overflow });
      if (state === 'portrait') await page.getByText('Portrét', { exact: true }).waitFor();
      if (state === 'ready') await page.screenshot({ path: path.join(evidence, `ready-${width}.png`), fullPage: true });
    }
  }
  fixture.state = 'ready';
  fixture.delay = 200;
  await page.goto(origin);
  await page.getByText('Načítám vaše výsledky…', { exact: true }).waitFor();
  await page.locator('.completion-updates__item').waitFor();
  fixture.delay = 0;
  await page.getByRole('button', { name: 'Označit jako přečtené' }).click();
  await page.getByText('Přečteno', { exact: true }).waitFor();
  await page.getByRole('button', { name: 'Obnovit', exact: true }).click();
  await page.getByText('Přečteno', { exact: true }).waitFor();
  await page.evaluate(() => window.completionHarness.setLocale('en'));
  await page.getByRole('heading', { name: 'Your results' }).waitFor();
  await page.keyboard.press('Tab');
  const focused = await page.evaluate(() => {
    const element = document.activeElement;
    const css = getComputedStyle(element);
    return { tag: element.tagName, outline: css.outlineStyle, shadow: css.boxShadow, height: element.getBoundingClientRect().height };
  });
  assert.ok(['BUTTON', 'A'].includes(focused.tag), 'keyboard reaches an interactive control');
  assert.ok(focused.outline !== 'none' || focused.shadow !== 'none', 'keyboard focus has a visible indicator');
  assert.ok(focused.height >= 44, 'keyboard control keeps its minimum touch height');
  const normalFontSize = await page.evaluate(() => Number.parseFloat(getComputedStyle(document.documentElement).fontSize));
  await page.evaluate(() => document.documentElement.classList.add('completion-large-text'));
  await page.waitForFunction(expected => Number.parseFloat(getComputedStyle(document.documentElement).fontSize) >= expected,
    normalFontSize * 2, { timeout: 2000 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, 'large text at mobile width does not overflow');
  const largeText = await page.evaluate(() => ({ fontSize: getComputedStyle(document.documentElement).fontSize,
    height: document.documentElement.scrollHeight, bodyHeight: document.body.scrollHeight,
    cardBottom: document.querySelector('.completion-updates').getBoundingClientRect().bottom,
    rootClass: document.documentElement.className,
    rule: [...document.styleSheets].flatMap(sheet => [...sheet.cssRules]).map(rule => rule.cssText).filter(rule => rule.includes('completion-large-text')) }));
  assert.ok(Number.parseFloat(largeText.fontSize) >= normalFontSize * 2, `the large-text check doubles ${normalFontSize}px: ${JSON.stringify(largeText)}`);
  await page.locator('.completion-updates__end').scrollIntoViewIfNeeded();
  const end = await page.locator('.completion-updates__end').boundingBox();
  assert.ok(end && end.y >= 0 && end.y + end.height <= 1001, 'large text content remains reachable by scrolling');
  const contrast = await page.locator('.completion-updates__state').evaluate(element => {
    const canvas = document.createElement('canvas'); canvas.width = 1; canvas.height = 1;
    const context = canvas.getContext('2d'); context.fillStyle = '#fff'; context.fillRect(0, 0, 1, 1);
    const chain = []; for (let node = element; node; node = node.parentElement) chain.unshift(node);
    for (const node of chain) { context.fillStyle = getComputedStyle(node).backgroundColor; context.fillRect(0, 0, 1, 1); }
    const background = [...context.getImageData(0, 0, 1, 1).data].slice(0, 3);
    context.fillStyle = getComputedStyle(element).color; context.fillRect(0, 0, 1, 1);
    const foreground = [...context.getImageData(0, 0, 1, 1).data].slice(0, 3);
    const luminance = rgb => rgb.map(value => value / 255).map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4)
      .reduce((sum, value, index) => sum + value * [.2126, .7152, .0722][index], 0);
    const values = [luminance(background), luminance(foreground)].sort((a, b) => b - a);
    return { ratio: (values[0] + .05) / (values[1] + .05), foreground, background };
  });
  assert.ok(contrast.ratio >= 4.5, `readiness text contrast is ${contrast.ratio.toFixed(2)}`);
  await page.screenshot({ path: path.join(evidence, 'large-text-en-390.png'), fullPage: true });
  fixture.read.clear();
  await page.getByRole('button', { name: 'Refresh', exact: true }).click();
  await page.getByRole('button', { name: 'Mark as read', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, 'large unread English controls do not overflow');
  await page.evaluate(() => window.completionHarness.setLocale('cs'));
  await page.getByRole('button', { name: 'Označit jako přečtené', exact: true }).waitFor();
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth + 1), false, 'large unread Czech controls do not overflow');
  await page.screenshot({ path: path.join(evidence, 'large-text-cs-unread-390.png'), fullPage: true });
  await page.evaluate(() => window.completionHarness.setLocale('en'));
  await context.setOffline(true);
  await page.getByText('Account results are unavailable in offline mode.', { exact: true }).waitFor();
  assert.equal(await page.locator('.completion-updates__item').count(), 0);
  await context.setOffline(false);
  await page.locator('.completion-updates__item').waitFor();
  await page.evaluate(() => window.completionHarness.setAccount(null));
  await page.getByText('Sign in to see results saved to your account.', { exact: true }).waitFor();
  assert.equal(await page.locator('.completion-updates__item').count(), 0);
  fixture.empty = true;
  await page.goto(origin);
  await page.getByText('Zatím tu nejsou žádné aktualizace výsledků.', { exact: true }).waitFor();
  fixture.empty = false;
  fixture.failure = true;
  await page.goto(origin);
  await page.getByText('Výsledky jsou dočasně nedostupné. Zkuste je obnovit.', { exact: true }).waitFor();
  const writes = requests.filter(request => request.method !== 'GET');
  assert.equal(writes.length, 1, 'only the explicit acknowledgement mutates the synthetic API');
  assert.equal(writes[0].method, 'PUT');
  assert.equal(errors.length, 0, errors.join('\n'));
  await writeFile(path.join(evidence, 'evidence.json'), JSON.stringify({ kind: 'production-compiled isolated component with synthetic APIs', origin, snapshots,
    checks: ['CZ/EN', 'mobile/desktop', 'portrait', 'loading', '200% text', 'visible keyboard focus/44px control', 'explicit read only', 'permission denied without prompt', 'offline clear/reconnect', 'signout clear', 'empty', '503'], focused,
    requests, errors, largeText, contrast, nativeDelivery: 'unverified', rootScreenIntegration: 'separate owner' }, null, 2));
  console.log(`PASS compiled completion component: ${snapshots.length} state/viewports, explicit read, offline/signout, CZ/EN, keyboard/large text; ${requests.length} API requests, zero generation, zero runtime errors`);
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}

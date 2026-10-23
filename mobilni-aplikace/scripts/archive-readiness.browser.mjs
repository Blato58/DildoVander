// Compiled application with isolated synthetic APIs; no provider or hardware calls.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.ARCHIVE_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-archive-readiness');
await stat(path.join(dist, 'index.html'));
await mkdir(output, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (request.method !== 'GET' || /^\/(app\/v1|guest\/v1|v1)\//.test(url.pathname)) {
      response.writeHead(404).end();
      return;
    }
    let file = path.resolve(dist, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
    if (!file.startsWith(dist)) { response.writeHead(403).end(); return; }
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); }
    catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const id = '11111111-1111-4111-8111-111111111111';
const assetId = '22222222-2222-4222-8222-222222222222';
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1sAAAAASUVORK5CYII=', 'base64');
const story = 'Synteticky pribeh zustava dostupny behem pripravy obrazu.';
const requests = [];
const errors = [];
const states = {
  pending: { state: 'pending', reason: 'awaiting_capacity', canRetry: false, shouldPoll: true, hasStory: false, hasArtwork: false },
  processing: { state: 'processing', reason: null, canRetry: false, shouldPoll: true, hasStory: false, hasArtwork: false },
  partial: { state: 'partial', reason: null, canRetry: false, shouldPoll: true, hasStory: true, hasArtwork: false },
  ready: { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true },
  reconciliation: { state: 'partial', reason: 'reconciliation_required', canRetry: false, shouldPoll: true, hasStory: true, hasArtwork: false },
  terminal: { state: 'partial', reason: 'generation_failed', canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: false },
  unavailable: { state: 'unavailable', reason: 'result_unavailable', canRetry: false, shouldPoll: false, hasStory: false, hasArtwork: false },
};

function record(accessKind, readiness) {
  const owner = accessKind === 'originalOwner';
  return {
    schemaVersion: 1, locator: id, revision: 1, state: 'pending', readiness,
    readOnly: !owner, accountLinked: true, accessKind,
    canManage: owner, canReadPrivate: owner, canUnlink: !owner,
    snapshot: { officialName: 'Synteticka hulka', slot: 1 },
    editions: readiness.hasStory ? [{ id, publicationState: 'story_ready', synthetic: true,
      story: { title: 'Pribeh behem pripravy', text: story },
      ...(readiness.hasArtwork ? { artworkAssetId: assetId } : {}) }] : [],
    assets: readiness.hasArtwork ? [{ id: assetId, kind: 'base_image', mediaType: 'image/png', length: png.length }] : [],
    portraits: [], jobs: [], uploads: [], wizardIdentities: [],
    prophecies: owner ? [] : [{ editionId: id, state: 'revealed', unlockAt: 'past', revelation: 'PRIVATE_LINKED_PAYLOAD_MUST_NOT_RENDER' }],
  };
}

let browser;
let page;
try {
  try { browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) }); }
  catch (error) {
    if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') { throw error; }
    browser = await chromium.launch({ channel: 'chrome' });
  }
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  let accessKind = 'originalOwner';
  let readiness = states.pending;
  let permitRecoveryMutation = false;
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== origin) { await route.abort(); return; }
    if (/^\/(app\/v1|guest\/v1|v1)\//.test(url.pathname)) {
      requests.push({ path: url.pathname, method: request.method(), body: request.postData(), key: request.headers()['idempotency-key'] });
      const explicitRecovery = permitRecoveryMutation && request.method() === 'POST'
        && url.pathname === `/app/v1/archive/owned/${id}/recovery-links`;
      assert(request.method() === 'GET' || explicitRecovery, 'only the explicitly clicked recovery action may mutate');
    }
    const json = value => route.fulfill({ contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') {
      return json({ enabled: true, account: { id: 'synthetic-readiness-account', displayName: 'Test', roles: ['Viewer'] }, csrfToken: 'synthetic-csrf' });
    }
    if (url.pathname === `/app/v1/archive/associations/${id}` || url.pathname === `/app/v1/archive/owned/${id}`) {
      return json(record(accessKind, readiness));
    }
    if (url.pathname === `/app/v1/archive/owned/${id}/recovery-links`) {
      return json({ url: `${origin}/keepsake/#r=${'r'.repeat(43)}` });
    }
    if (url.pathname.endsWith(`/assets/${assetId}`)) { return route.fulfill({ contentType: 'image/png', body: png }); }
    if (/^\/(app\/v1|guest\/v1|v1)\//.test(url.pathname)) { return json({}); }
    return route.continue();
  });
  page = await context.newPage();
  page.setDefaultTimeout(12000);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') { errors.push(message.text()); } });
  const notice = () => page.locator('[role="status"][aria-live="polite"][data-readiness-state]');
  const fits = async () => assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'no horizontal overflow');
  const open = async (kind, state) => {
    accessKind = kind;
    readiness = state;
    await page.goto(`${origin}/keepsake/?account=1&k=${id}`, { waitUntil: 'networkidle' });
    await page.getByRole('heading', { name: 'Synteticka hulka' }).waitFor();
    await notice().waitFor();
    assert.equal(await notice().getAttribute('data-readiness-state'), state.state);
    await fits();
  };

  for (const kind of ['originalOwner', 'linkedViewer']) {
    await open(kind, states.partial);
    await page.getByText(story, { exact: true }).first().waitFor();
    const partialText = await notice().innerText();
    assert.match(partialText, /příběh|Příběh|text/i);
    assert.equal(await page.getByText('PRIVATE_LINKED_PAYLOAD_MUST_NOT_RENDER').count(), 0);
    if (kind === 'linkedViewer') {
      assert.equal(await page.getByRole('button', { name: 'Odeslat fotografii a vytvořit portrét' }).count(), 0);
      assert.equal(await page.getByRole('button', { name: 'Vytvořit soukromý návratový odkaz' }).count(), 0);
    }
    const isDetail = request => request.path === `/app/v1/archive/associations/${id}` || request.path === `/app/v1/archive/owned/${id}`;
    const detailCount = requests.filter(isDetail).length;
    readiness = states.ready;
    await page.getByRole('img', { name: 'Osobní digitální interpretace vybrané hůlky' }).waitFor();
    await page.waitForFunction(previous => {
      const node = document.querySelector('[role="status"][aria-live="polite"][data-readiness-state]');
      return node?.textContent && node.textContent.trim() !== previous.trim();
    }, partialText);
    assert(requests.filter(isDetail).length > detailCount,
      'owner and linked viewer readiness transition through a GET refresh');
    await page.screenshot({ path: path.join(output, `${kind}-ready-desktop.png`), fullPage: true });
    await fits();
  }

  await open('originalOwner', states.partial);
  permitRecoveryMutation = true;
  await page.getByRole('button', { name: 'Vytvořit soukromý návratový odkaz', exact: true }).click();
  await page.getByLabel('Soukromý návratový odkaz', { exact: true }).waitFor();
  permitRecoveryMutation = false;
  const recovery = requests.filter(request => request.method === 'POST');
  assert.equal(recovery.length, 1, 'one explicit owner action is sent');
  assert.deepEqual(JSON.parse(recovery[0].body), { schemaVersion: 1 });
  assert.match(recovery[0].key, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i);
  readiness = states.ready;
  await page.getByRole('img', { name: 'Osobní digitální interpretace vybrané hůlky' }).waitFor();
  assert.equal(await notice().getAttribute('data-readiness-state'), 'ready', 'readiness polling resumes after explicit owner action');

  for (const [name, state] of Object.entries(states)) {
    await page.setViewportSize({ width: 390, height: 844 });
    await open('linkedViewer', state);
    const text = await notice().innerText();
    if (state.hasStory) { await page.getByText(story, { exact: true }).first().waitFor(); }
    if (name === 'reconciliation') { assert.match(text, /ověř|potvr|nejist|výsledek/i); }
    if (name === 'terminal') { assert.match(text, /nepodař|selha|zastav|nedokonč/i); }
    if (name === 'unavailable') { assert.match(text, /nedostup|dostupn/i); }
    assert.equal(await page.getByRole('button', { name: /znovu generovat|opakovat generování|vygenerovat znovu/i }).count(), 0);
    const unlink = page.getByRole('button', { name: 'Odpojit tuto památku od účtu', exact: true });
    await unlink.focus();
    assert(await unlink.evaluate(button => document.activeElement === button), 'linked action remains keyboard reachable');
    assert.notEqual(await unlink.evaluate(button => getComputedStyle(button).outlineStyle), 'none', 'focused action has a visible outline');
    await page.evaluate(() => window.scrollTo(0, 0));
    await page.screenshot({ path: path.join(output, `${name}-mobile.png`), fullPage: true });
  }
  assert.equal(errors.length, 0, JSON.stringify(errors));
  assert.equal(requests.filter(request => request.method !== 'GET').length, 1, 'observer adds no mutations beyond the explicit owner click');
  console.log(JSON.stringify({ passed: true, origin, viewports: ['1440x1000', '390x844'], screenshots: output,
    states: Object.keys(states), apiRequests: requests.length, explicitOwnerMutations: 1, observerMutations: 0, errors }));
} catch (error) {
  console.error(JSON.stringify({ requests, errors, body: await page?.locator('body').innerText() }));
  await page?.screenshot({ path: path.join(output, 'failure.png'), fullPage: true });
  throw error;
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}

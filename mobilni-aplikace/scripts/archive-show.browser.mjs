// Compiled shared application, isolated synthetic APIs, no provider/hardware calls.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const output = process.env.ARCHIVE_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-show-archive-browser');
await stat(path.join(dist, 'index.html')); await mkdir(output, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.json': 'application/json' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (request.method !== 'GET' || /^\/(app\/v1|guest\/v1|v1)\//.test(url.pathname)) { response.writeHead(404).end(); return; }
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
const grant = 'x'.repeat(43);
const identity = { editionId: id, visibility: 'public_fiction', wizardAlias: 'Mág syntetické ukázky', houseOrGuild: 'Cech ukázek', dubiousSpecialization: 'Lest', signatureAbility: 'Schopnost', weakness: 'Slabina', ministryAssessment: 'Fikce', wandBond: 'Pouto', motto: 'Motto', microStory: 'Autorská fikce pro syntetické ověření.', omen: 'Veřejná indicie' };
const participant = { displayName: 'Mág ukázky', wandId: 'wand1', strength: 2, cunning: 3, defense: 4 };
let duel = { id, revision: 1, state: 'Running', mode: 'interactive', round: 1, deadline: '2030-01-01T20:00:00Z', narrationMode: 'web_only', narrationStatus: 'web_only_no_spoken_confirmation', left: participant, right: { ...participant, displayName: 'Druhý mág stejné hůlky' }, leftChoiceLocked: false, rightChoiceLocked: false, rounds: [] };
let chronicle = { schemaVersion: 1, eveningId: id, revision: 0, provisional: true, createdAt: '2026-10-19T20:00:00Z', completedVisits: 0, confirmedPresentations: 0, completedDuels: 0, interruptedVisits: 0, unresolvedVisits: 1, wands: [{ slot: 1, officialName: 'Katalogová hůlka A', confirmedPresentations: 0 }, { slot: 2, officialName: 'Katalogová hůlka B', confirmedPresentations: 0 }], mostPopularSlots: [], narrative: 'Syntetický večer teprve začíná.' };
let identityCreated = false; let available = true; let account = null;
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1sAAAAASUVORK5CYII=', 'base64');
const record = () => ({ schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false, snapshot: { officialName: 'Katalogová hůlka A', slot: 1 }, editions: [{ id, publicationState: 'published', story: { title: 'Syntetická edice', text: 'Syntetický příběh bez obrázku.' }, synthetic: true }], jobs: [], portraits: [], assets: [], wizardIdentities: identityCreated ? [identity] : [], prophecies: [{ editionId: id, state: 'locked', unlockAt: '2000-01-01T00:00:00Z', revelation: 'PRIVATE_FUTURE_BODY_MUST_NOT_RENDER' }] });
const requests = []; const errors = [];
let browser; let page;
try {
  try { browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) }); }
  catch (error) { if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') throw error; browser = await chromium.launch({ channel: 'chrome' }); }
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  page = await context.newPage(); page.setDefaultTimeout(10000);
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error' && /Content Security Policy|unsafe-eval|EvalError/i.test(message.text())) errors.push(message.text()); });
  await context.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin) return route.abort();
    requests.push({ path: url.pathname, url: request.url(), method: request.method(), body: request.postData(), headers: request.headers() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account, csrfToken: 'synthetic-account-csrf' });
    if (url.pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (url.pathname === '/app/v1/account') return json({ devices: [{ deviceId: 'synthetic-device', canOperate: true }], associations: [{ deviceId: 'synthetic-device', alias: 'Kufr ukázky' }], grants: [], sessions: [], passkeys: [], passkeyCount: 1 });
    if (url.pathname.startsWith('/app/v1/archive/associations/')) return json({ ...record(), accountLinked: true, readOnly: true, accessKind: 'linkedViewer', canManage: false, canReadPrivate: false, canUnlink: true, uploads: [], jobs: [], wizardIdentities: [identity], prophecies: [{ editionId: id, state: 'revealed', revelation: 'PRIVATE_OWNER_WORDING_MUST_NOT_RENDER' }] });
    if (url.pathname.startsWith('/app/v1/')) return json({}, 401);
    if (url.pathname === '/guest/v1/evening') return json({ schemaVersion: 1, evening: { id, label: 'Syntetický večer' }, latest: null, items: [{ locator: id, editionId: id, officialName: 'Katalogová hůlka A', slot: 1, story: { title: 'Syntetická edice', text: 'Příběh' }, synthetic: true, assets: [], wizardIdentity: { ...identity, revelation: 'PRIVATE_PUBLIC_EXTRA_MUST_NOT_RENDER' } }] });
    if (url.pathname === '/guest/v1/chronicle') return available ? json(chronicle) : json({}, 404);
    if (url.pathname === '/v1/admin/show/chronicle') return json({ snapshot: { ...chronicle, eveningId: url.searchParams.get('eveningId'), narrative: 'Soukromá syntetická evidence' }, archivedEditions: 2 });
    if (url.pathname === '/v1/evenings') return json({ activeEveningId: 'legacy-evening_219', evenings: [{ eveningId: 'legacy-evening_219', name: 'Večer ukázky', createdAt: '2026-10-19T20:00:00Z', exchangeCount: 0, droppedCount: 0 }] });
    if (url.pathname === `/guest/v1/duels/${id}`) return json(duel);
    if (url.pathname === `/guest/v1/duels/${id}/choices`) { assert.equal(request.headers().authorization, `Bearer ${grant}`); duel = { ...duel, revision: 2, leftChoiceLocked: true }; return json(duel); }
    if (url.pathname === '/guest/v1/bootstrap') return json({ schemaVersion: 1, csrfToken: 'synthetic-owner-csrf' });
    if (url.pathname === `/guest/v1/keepsakes/${id}/wizard-identity`) { identityCreated = true; return json({}); }
    if (url.pathname === `/guest/v1/keepsakes/${id}/exports`) return json({ asset: { id: assetId } });
    if (url.pathname === `/guest/v1/keepsakes/${id}/assets/${assetId}`) return route.fulfill({ contentType: 'image/png', body: png });
    if (url.pathname === `/guest/v1/keepsakes/${id}`) return json({ ...record(), ...(url.searchParams.get('view') === 'share' ? { sharedEditionId: id, wizardIdentities: [identity] } : {}) });
    if (/^\/(guest\/v1|v1)\//.test(url.pathname)) return json({}, 404);
    return route.continue();
  });
  const fits = async () => assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'no horizontal overflow');
  await page.goto(`${origin}/keepsake/duel.html?duel=${id}#grant=${grant}&side=left`);
  await page.getByRole('button', { name: 'Úder', exact: true }).waitFor();
  assert.equal(new URL(page.url()).hash, ''); assert.equal(await page.locator('meta[name=robots]').count(), 1); assert.match(await page.locator('meta[name=robots]').getAttribute('content'), /noindex/);
  const strike = page.getByRole('button', { name: 'Úder', exact: true });
  await strike.focus(); assert.equal(await strike.evaluate(button => document.activeElement === button), true, 'the named spell button receives keyboard focus');
  await fits(); await page.screenshot({ path: path.join(output, 'duel-desktop.png'), fullPage: true });
  await page.getByRole('button', { name: 'Úder', exact: true }).click(); await page.getByText('Tah přijat a uzamčen.', { exact: false }).waitFor();
  const moves = requests.filter(request => request.path.endsWith('/choices')); assert.equal(moves.length, 1);
  assert.deepEqual(Object.keys(JSON.parse(moves[0].body)).sort(), ['action', 'operationKey', 'revision', 'round', 'side']);
  assert.equal(JSON.parse(moves[0].body).revision, 1);
  await page.setViewportSize({ width: 390, height: 844 }); await fits(); await page.screenshot({ path: path.join(output, 'duel-mobile.png'), fullPage: true });
  await page.goto(`${origin}/archive/duel?duel=${id}`); await page.getByRole('heading', { name: 'Souboj postav', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Úder', exact: true }).count(), 0);
  await page.goto(`${origin}/archive`); await page.getByRole('heading', { name: identity.wizardAlias }).waitFor(); await fits();
  assert(!(await page.locator('main').innerText()).includes('PRIVATE_PUBLIC_EXTRA')); await page.screenshot({ path: path.join(output, 'identity-mobile.png'), fullPage: true });
  await page.goto(`${origin}/keepsake/?k=${id}`); await page.getByRole('button', { name: 'Vytvořit kouzelnickou identitu této edice' }).waitFor();
  assert.equal(requests.filter(request => request.path.endsWith('/wizard-identity')).length, 0);
  assert(!(await page.locator('main').innerText()).includes('PRIVATE_FUTURE_BODY'));
  await page.getByRole('button', { name: 'Vytvořit kouzelnickou identitu této edice' }).click(); await page.getByRole('heading', { name: identity.wizardAlias }).waitFor();
  await fits(); await page.evaluate(() => window.scrollTo(0, 0));
  await page.screenshot({ path: path.join(output, 'prophecy-owner-mobile.png'), fullPage: true });
  await page.screenshot({ path: path.join(output, 'prophecy-owner-top-mobile.png') });
  await page.getByRole('button', { name: 'Kartička PNG', exact: true }).click(); await page.getByRole('heading', { name: 'Soubor je připravený' }).waitFor();
  const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Stáhnout soubor', exact: true }).click()]);
  assert.deepEqual(await readFile(await download.path()), png, 'downloaded partial PNG preserves fixture bytes');
  await page.goto(`${origin}/keepsake/?k=${id}&view=share&editionId=${id}`); await page.getByRole('heading', { name: identity.wizardAlias }).waitFor();
  assert.equal(await page.getByRole('heading', { name: 'Soukromé proroctví', exact: true }).count(), 0);
  await page.goto(`${origin}/keepsake/chronicle.html`); await page.getByText('První potvrzená prezentace teprve přijde.').waitFor();
  chronicle = { ...chronicle, revision: 2, confirmedPresentations: 2, mostPopularSlots: [1, 2] };
  await page.getByRole('button', { name: 'Obnovit kroniku' }).click(); await page.getByText(/Na prvním místě je remíza/).waitFor(); await fits(); await page.screenshot({ path: path.join(output, 'chronicle-mobile.png'), fullPage: true });
  available = false; await page.getByRole('button', { name: 'Obnovit kroniku' }).click(); await page.getByText(/Kronika zatím není dostupná/).waitFor(); assert.equal(await page.getByRole('table').count(), 0);
  available = true; account = { id: 'synthetic-admin', displayName: 'Správce ukázky', roles: ['Administrator'] };
  await page.goto(`${origin}/archive/chronicle`); await page.getByLabel('Kufr', { exact: true }).selectOption('synthetic-device'); await page.getByLabel('Večer', { exact: true }).selectOption('legacy-evening_219');
  for (const label of ['Kufr', 'Večer']) assert((await page.getByLabel(label, { exact: true }).boundingBox()).height >= 44, 'context selectors keep shared touch target sizing');
  await page.getByRole('button', { name: 'Načíst soukromý přehled' }).click(); await page.getByText('Soukromá syntetická evidence', { exact: true }).waitFor();
  await fits(); await page.evaluate(() => window.scrollTo(0, 0)); await page.screenshot({ path: path.join(output, 'chronicle-admin-mobile.png'), fullPage: true });
  const privateRead = requests.find(request => request.path === '/v1/admin/show/chronicle'); assert.equal(privateRead.headers['x-dv-device-id'], 'synthetic-device');
  await page.goto(`${origin}/keepsake/?k=${id}&account=1`); await page.getByRole('heading', { name: identity.wizardAlias }).waitFor();
  assert(!(await page.locator('main').innerText()).includes('PRIVATE_OWNER_WORDING')); await page.getByText(/Soukromé proroctví je dostupné jen vlastníkovi/).waitFor();
  assert(requests.every(request => !request.url.includes(grant)), 'grant never enters request URLs');
  assert.deepEqual(errors, [], 'no browser exceptions or CSP failures');
  console.log(JSON.stringify({ result: 'PASS', evidence: 'synthetic compiled Vue', widths: [1440, 390], screenshots: output, moveRequests: moves.length, partialPngBytes: png.length }));
} catch (error) {
  console.error(JSON.stringify({ errors, apiRequests: requests.filter(request => /^\/(guest|app|v1)\//.test(request.path)).map(request => ({ path: request.path, method: request.method })) }));
  if (page) { console.error((await page.locator('main').innerText().catch(() => 'No main content')).slice(0, 3000)); await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); }
  throw error;
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }

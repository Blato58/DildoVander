// Compiled-app journeys with synthetic account/provider responses. The loopback
// preview cannot access a provider, guest archive, device, or real account.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = path.resolve(fileURLToPath(new URL('../dist/', import.meta.url)));
const screenshots = process.env.PERSONA_LAB_SCREENSHOTS ?? path.join(tmpdir(), `dildovander-persona-lab-browser-${Date.now()}`);
await stat(path.join(dist, 'index.html'));
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.json': 'application/json', '.png': 'image/png' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (request.method !== 'GET') { response.writeHead(405).end(); return; }
    if (/^\/(?:app\/v1|guest\/v1|v1)\//.test(url.pathname)) { response.writeHead(404).end(); return; }
    let file = path.resolve(dist, decodeURIComponent(url.pathname).replace(/^\//, '') || 'index.html');
    if (file !== dist && !file.startsWith(dist + path.sep)) { response.writeHead(403).end(); return; }
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); }
    catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream', 'Cache-Control': 'no-store' });
    response.end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const metrics = ['overall', 'humor', 'personaConsistency', 'naturalness', 'responsiveness', 'memoryAccuracy', 'technicalReliability'];
const reports = [];
let browser;
let activePage;
const now = '2026-10-20T12:00:00Z';

function fixture(admin) {
  const baseline = { id: 'baseline-browser', parentId: null, name: 'Výchozí persona', instructions: 'Syntetická persona pro prohlížečový test.', engine: 'gpt-live-1', voice: 'cedar', status: 'baseline', sha256: 'a'.repeat(64), sourceVersion: 'browser-fixture', createdAt: now, publishedAt: null, revision: 1 };
  return {
    admin, authenticated: false, versions: [baseline], activeRevisionId: baseline.id, sessions: [], evaluations: [], writes: [], requests: [], blocked: [], unknown: [], sockets: 0,
    scenarios: [{ id: 'first-visit', name: 'První návštěva', description: 'Syntetický host bez produkčního kontextu.', openingText: 'Ahoj, jsem fiktivní tester.' }],
    memoryFixtures: [{ id: 'returning', name: 'Vrácený host', description: 'Výhradně syntetická paměť.', current: 'Fiktivní knihovník Áda.', evening: 'Testovací večer.', returning: 'Fiktivní zachráněná kniha.' }],
  };
}

async function installFixtures(context, state) {
  await context.addInitScript(() => {
    // Exercise the actual sign-in UI without an authenticator or real identity.
    Object.defineProperty(navigator.credentials, 'get', { configurable: true, value: async () => ({
      id: 'synthetic-browser-passkey', rawId: new Uint8Array([1, 2, 3]).buffer, type: 'public-key', authenticatorAttachment: 'platform',
      response: { clientDataJSON: new ArrayBuffer(1), authenticatorData: new ArrayBuffer(1), signature: new ArrayBuffer(1), userHandle: null },
      getClientExtensionResults: () => ({}),
    }) });
    window.__personaLabMediaRequests = 0;
    Object.defineProperty(navigator.mediaDevices, 'getUserMedia', { configurable: true, value: async () => {
      window.__personaLabMediaRequests++;
      throw new DOMException('Synthetic microphone denial.', 'NotAllowedError');
    } });
  });
  // Installed Playwright WebSocketRoute closes locally without connecting.
  await context.routeWebSocket('**/*', async socket => {
    state.sockets++;
    await socket.close({ code: 1008, reason: 'Synthetic test does not open real sockets.' });
  });
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== origin) { state.blocked.push(url.origin); await route.abort(); return; }
    const method = request.method(); const pathname = url.pathname;
    const body = request.postData() ? JSON.parse(request.postData()) : null;
    state.requests.push({ method, path: pathname });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    const account = () => state.authenticated ? { id: state.admin ? 'synthetic-admin' : 'synthetic-viewer', displayName: 'Prohlížečový tester', roles: state.admin ? ['Administrator', 'Viewer'] : ['Viewer'] } : null;
    if (pathname === '/app/v1/session') return json({ enabled: true, account: account(), csrfToken: 'synthetic-browser-csrf' });
    if (pathname === '/app/v1/passkeys/request-options') return json({ challenge: 'AQID', rpId: '127.0.0.1', allowCredentials: [], userVerification: 'preferred' });
    if (pathname === '/app/v1/passkeys/sign-in') { state.authenticated = true; return json({}); }
    if (pathname === '/guest/v1/evening') return json({ schemaVersion: 1, evening: null, items: [], latest: null });
    if (pathname === '/guest/v1/bootstrap') return json({ schemaVersion: 1, csrfToken: 'synthetic-guest-csrf' });
    const base = '/app/v1/persona-lab';
    if (pathname.startsWith(base)) {
      if (!state.authenticated) return json({ code: 'account_required' }, 401);
      if (method !== 'GET') assert.equal(request.headers()['x-dv-csrf'], 'synthetic-browser-csrf', 'mutations use the normal CSRF transport');
      const suffix = pathname.slice(base.length);
      if (suffix === '/catalog') return json({ capabilities: { live: true, realtime: true, voice: true, text: true, simulationOnly: true, imageRealtime: false }, activeRevisionId: state.activeRevisionId, versions: state.versions, scenarios: state.scenarios, memoryFixtures: state.memoryFixtures });
      if (suffix === '/sessions' && method === 'GET') return json(state.sessions);
      if (suffix === '/suites') return json([]);
      if (suffix === '/analytics') return json({ sessions: state.sessions.length, evaluations: state.evaluations.length, metrics: metrics.map(name => ({ name, mean: state.evaluations.length ? 4 : null, count: state.evaluations.length })), comparisons: [] });
      if (suffix === '/sessions' && method === 'POST') {
        const session = { ...body, id: `session-${state.sessions.length + 1}`, ownerId: account().id, createdAt: now, endedAt: null, status: 'open', config: structuredClone(state.versions.find(version => version.id === body.revisionId)), turns: [], events: [], evaluation: null, memorySnapshot: state.memoryFixtures[0], scenarioSnapshot: state.scenarios[0] };
        state.sessions.unshift(session); return json(session);
      }
      const match = suffix.match(/^\/sessions\/([^/]+)(?:\/(turns|evaluation|end|socket-ticket))?$/);
      if (match) {
        const session = state.sessions.find(item => item.id === match[1]);
        if (!session) return json({ code: 'lab_session_not_found' }, 404);
        if (!match[2]) return json(session);
        if (match[2] === 'turns') {
          session.turns.push({ id: `user-${session.turns.length + 1}`, role: 'user', text: body.text, createdAt: now });
          session.turns.push({ id: `assistant-${session.turns.length + 1}`, role: 'assistant', text: 'Jsem fiktivní odpověď. Jakou knihu zachráníme?', createdAt: now, latencyMs: 125, audioBytes: 4800 });
          session.events.push({ id: `event-${session.events.length}`, type: 'provider_turn', code: 'completed', createdAt: now, latencyMs: 125, costMicroUsd: 100 });
          return json(session);
        }
        if (match[2] === 'evaluation') { session.evaluation = body; state.evaluations.push(structuredClone(body)); return json(session); }
        if (match[2] === 'end') { session.status = 'ended'; session.endedAt = now; return json(session); }
        if (match[2] === 'socket-ticket') return json({ url: origin.replace('http:', 'ws:') + `${base}/sessions/${session.id}/live?ticket=synthetic`, path: '', expiresAt: now });
      }
      if (suffix === '/revisions' || suffix.endsWith('/publish') || suffix === '/rollback') {
        if (!state.admin) return json({ code: 'forbidden' }, 403);
        assert.equal(request.headers()['x-dv-confirm'], 'yes', 'sensitive draft/publication uses explicit confirmation header');
        state.writes.push({ suffix, body: structuredClone(body) });
        if (suffix === '/revisions') {
          const revision = { ...body, id: `draft-${state.versions.length}`, status: 'draft', sha256: 'b'.repeat(64), sourceVersion: 'browser-draft', createdAt: now, publishedAt: null, revision: state.versions.length + 1 };
          state.versions.unshift(revision); return json(revision);
        }
        assert.equal(body.expectedActiveRevisionId, state.activeRevisionId, 'publication uses the exact active revision');
        const id = suffix === '/rollback' ? body.revisionId : suffix.split('/')[2];
        const revision = state.versions.find(item => item.id === id);
        assert(revision, 'publication targets an existing revision');
        revision.status = suffix === '/rollback' ? revision.status : 'published';
        state.activeRevisionId = id; return json(revision);
      }
      state.unknown.push(`${method} ${pathname}`); return json({ code: 'synthetic_not_implemented' }, 404);
    }
    if (/^\/(?:app\/v1|guest\/v1|v1)\//.test(pathname)) { state.unknown.push(`${method} ${pathname}`); return json({ code: 'synthetic_not_implemented' }, 404); }
    await route.continue();
  });
}

async function layout(page, label) {
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${label}: no horizontal page overflow`);
  await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
  await page.screenshot({ path: path.join(screenshots, `${label}.png`), fullPage: true });
}

async function signInViaNavigation(page) {
  await page.goto(origin);
  await page.getByRole('link', { name: 'Persona Lab', exact: true }).first().click();
  await page.waitForURL(url => url.pathname === '/signin' && url.searchParams.get('return') === '/persona-lab');
  await page.getByRole('button', { name: 'Přihlásit přístupovým klíčem', exact: true }).click();
  await page.waitForURL('**/persona-lab');
  await page.getByRole('heading', { name: 'Persona Lab', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Připravit sezení', exact: true }).waitFor();
}

async function viewerJourney(width) {
  const state = fixture(false);
  const context = await browser.newContext({ viewport: { width, height: 1000 }, reducedMotion: 'reduce' });
  const errors = [];
  let page;
  try {
    await installFixtures(context, state);
    page = await context.newPage(); activePage = page;
    page.setDefaultTimeout(12000); page.on('pageerror', error => errors.push(error.message));
    await signInViaNavigation(page);
    assert.equal(await page.getByRole('button', { name: 'Konfigurace', exact: true }).count(), 0, 'Viewer has no admin configuration controls');
    await page.getByLabel('Název testu', { exact: true }).fill(`Syntetický test ${width}`);
    await page.getByLabel('Konverzační model', { exact: true }).selectOption('gpt-realtime-2.1');
    await page.getByRole('button', { name: 'Připravit sezení', exact: true }).click();
    await page.getByLabel('Vaše zpráva', { exact: true }).fill('Jsem pouze fiktivní knihovník Áda.');
    await page.getByRole('button', { name: 'Odeslat do modelu', exact: true }).click();
    await page.getByText('Jsem fiktivní odpověď. Jakou knihu zachráníme?', { exact: true }).first().waitFor();
    await page.getByLabel('Vaše zpráva', { exact: true }).focus();
    await page.keyboard.press('Tab');
    assert(await page.evaluate(() => {
      const element = document.activeElement;
      if (element?.tagName !== 'BUTTON' || !element.matches(':focus-visible')) return false;
      const style = getComputedStyle(element);
      return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) > 0;
    }), 'keyboard reaches a button with a rendered focus outline after the message field');
    await layout(page, `viewer-${width}-playground`);
    await page.getByRole('button', { name: 'Hodnocení', exact: true }).click();
    for (const metric of metrics) await page.locator(`input[name="lab-score-${metric}"][value="4"]`).locator('..').click();
    await page.getByLabel('Celkový komentář', { exact: true }).fill('Ruční hodnocení syntetické odpovědi.');
    await page.getByLabel('Komentář k tahu 2', { exact: true }).fill('Tento komentář patří přesně k odpovědi druhého tahu.');
    await page.getByRole('button', { name: 'Uložit hodnocení', exact: true }).click();
    await page.waitForFunction(() => !document.querySelector('.lab-evaluation button[type="submit"]')?.disabled);
    assert.equal(state.evaluations.length, 1, 'one explicit evaluation is saved');
    assert.deepEqual(state.evaluations[0].scores, Object.fromEntries(metrics.map(metric => [metric, 4])));
    assert.deepEqual(state.evaluations[0].turnComments, [{ turnId: 'assistant-2', comment: 'Tento komentář patří přesně k odpovědi druhého tahu.' }]);
    await layout(page, `viewer-${width}-evaluation`);
    await page.getByRole('button', { name: 'Porovnání a přehled', exact: true }).click();
    await page.getByRole('button', { name: 'Obnovit přehled', exact: true }).click();
    await page.getByRole('heading', { name: 'Hodnocení po filtrování', exact: true }).waitFor();
    await page.getByText('4.00 · n=1', { exact: true }).first().waitFor();
    await layout(page, `viewer-${width}-results`);
    await page.getByRole('button', { name: 'Playground', exact: true }).click();
    await page.getByRole('button', { name: 'Spustit hlas', exact: true }).click();
    await page.getByText('Zvuk a obraz: Mikrofon nebyl povolen. Povolte mikrofon v nastavení prohlížeče a potom hlas znovu spusťte.', { exact: false }).waitFor();
    assert.equal(await page.evaluate(() => window.__personaLabMediaRequests), 1, 'actual voice entry handles a deterministic permission denial');
    assert(await page.getByRole('button', { name: 'Spustit hlas', exact: true }).isEnabled(), 'permission denial permits an explicit manual voice restart');
    assert(await page.getByLabel('Vaše zpráva', { exact: true }).isEnabled(), 'permission denial leaves text input available');
    assert.equal(state.requests.filter(request => request.path.endsWith('/socket-ticket')).length, 0, 'permission denial does not create a provider socket ticket');
    assert.equal(state.sockets, 0, 'permission denial creates no WebSocket');
    await page.getByLabel('Vaše zpráva', { exact: true }).fill('Po zamítnutí mikrofonu pokračuji textem.');
    await page.getByRole('button', { name: 'Odeslat do modelu', exact: true }).click();
    await page.getByText('Po zamítnutí mikrofonu pokračuji textem.', { exact: true }).first().waitFor();
    await layout(page, `viewer-${width}-microphone-denied`);
    assert.deepEqual(state.writes, [], 'Viewer sends no admin writes');
    assert.deepEqual(state.blocked, [], 'no nonlocal requests are attempted');
    assert.deepEqual(state.unknown, [], 'all account/provider requests use declared synthetic fixtures');
    assert.deepEqual(errors, [], 'no frontend runtime errors');
    reports.push({ role: 'Viewer', viewport: { width, height: 1000 }, savedEvaluation: state.evaluations[0], requestCount: state.requests.length, microphone: 'deterministic_permission_denial_then_text_recovery', errors });
  } catch (error) {
    if (page && !page.isClosed()) await page.screenshot({ path: path.join(screenshots, `failure-viewer-${width}.png`), fullPage: true }).catch(() => {});
    throw error;
  } finally { await context.close(); }
}

async function adminJourney() {
  const state = fixture(true);
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  let page;
  try {
    await installFixtures(context, state);
    page = await context.newPage(); activePage = page; page.setDefaultTimeout(12000);
    await signInViaNavigation(page);
    await page.getByRole('button', { name: 'Konfigurace', exact: true }).click();
    await page.getByLabel('Název nového draftu', { exact: true }).fill('Prohlížečový draft');
    await page.getByLabel('Instrukce persony', { exact: true }).fill('Výslovně syntetický nový draft. Odpověz jednou krátkou otázkou.');
    await page.getByRole('button', { name: 'Uložit nový draft', exact: true }).click();
    const publish = page.getByRole('button', { name: 'Publikovat vybraný draft', exact: true });
    await publish.waitFor();
    assert.equal(await publish.isDisabled(), true, 'publication requires an explicit confirmation');
    assert.equal(state.activeRevisionId, 'baseline-browser', 'draft leaves production activation unchanged');
    await page.getByLabel('Potvrzuji změnu aktivní konfigurace na vybranou verzi.', { exact: false }).check();
    await publish.click();
    await page.getByText('Verze byla publikována.', { exact: true }).waitFor();
    assert.equal(state.activeRevisionId, 'draft-1');
    await page.getByLabel('Výchozí verze / historie', { exact: true }).selectOption('baseline-browser');
    const rollback = page.getByRole('button', { name: 'Vrátit tuto verzi', exact: true });
    assert.equal(await rollback.isDisabled(), true, 'rollback also requires a fresh confirmation');
    await page.getByLabel('Potvrzuji změnu aktivní konfigurace na vybranou verzi.', { exact: false }).check();
    await rollback.click();
    await page.getByText('Aktivní konfigurace byla vrácena na vybranou verzi.', { exact: true }).waitFor();
    assert.equal(state.activeRevisionId, 'baseline-browser');
    assert.deepEqual(state.writes.map(write => write.suffix), ['/revisions', '/revisions/draft-1/publish', '/rollback']);
    await layout(page, 'administrator-1440-rollback');
    assert.deepEqual(state.unknown, []); assert.deepEqual(state.blocked, []);
    reports.push({ role: 'Administrator', viewport: { width: 1440, height: 1000 }, operations: state.writes.map(write => write.suffix), activeRevisionId: state.activeRevisionId });
  } catch (error) {
    if (page && !page.isClosed()) await page.screenshot({ path: path.join(screenshots, 'failure-administrator.png'), fullPage: true }).catch(() => {});
    throw error;
  } finally { await context.close(); }
}

try {
  try { browser = await chromium.launch(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}); }
  catch (error) {
    if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }
  await viewerJourney(1440);
  await viewerJourney(390);
  await adminJourney();
  await writeFile(path.join(screenshots, 'manifest.json'), JSON.stringify({ evidence: 'synthetic_browser_only', origin, reports, limitations: ['No real passkey, microphone, provider, iPhone, device, or audio-acoustic acceptance.', 'Voice success/audio/interrupt socket flow is covered by backend fake runtime tests; this browser harness covers permission denial and text recovery.'] }, null, 2));
  console.log(JSON.stringify({ passed: true, screenshots, reports: reports.map(({ role, viewport }) => ({ role, viewport })) }));
} catch (error) {
  if (activePage && !activePage.isClosed()) await activePage.screenshot({ path: path.join(screenshots, 'failure.png'), fullPage: true }).catch(() => {});
  console.error(`Persona Lab browser validation failed; screenshots: ${screenshots}`);
  throw error;
} finally {
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}

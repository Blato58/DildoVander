// Compiled customer-data composition smoke test. All account APIs are synthetic;
// this verifies the browser UI, not backend or native transport acceptance.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { readFile, mkdir, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const screenshots = process.env.APP_SCREENSHOTS ?? path.join(tmpdir(), 'dildovander-customer-data-browser');
await stat(path.join(dist, 'index.html'));
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml', '.json': 'application/json' };
const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url, 'http://localhost');
    if (request.method !== 'GET') { response.writeHead(405).end(); return; }
    if (/^\/(?:app\/v1|guest\/v1|v1|models)\//.test(url.pathname)) {
      response.writeHead(404, { 'Content-Type': 'application/json' }).end('{}'); return;
    }
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
let context;
let page;
const failures = [];
const requests = [];
const accountA = { id: 'synthetic-customer-a', displayName: 'Zkušební host', roles: ['Customer'] };
const accountB = { id: 'synthetic-customer-b', displayName: 'Druhý host', roles: ['Customer'] };
let signedInAccount = accountA;
let switchToAccount = null;
let memoryReads = 0;
const memoryReadAccounts = [];
let memoryWrites = [];
let exportStatusReads = 0;
let exportCreateCount = 0;
let exportDeleteCount = 0;
let exportJob = null;
let exportPostJob = null;
let accountDeletionRequested = false;
const memoryItem = {
  id: '30000000-0000-4000-8000-000000000001', revision: 2,
  text: 'Mám rád modrou.', kind: 'UserReportedFact', sourceReference: 'synthetic-answer',
  createdAt: '2026-10-23T00:00:00Z', profileId: '40000000-0000-4000-8000-000000000001',
  customerSessionId: '50000000-0000-4000-8000-000000000001', canCorrect: true, canForget: true,
};
const ids = { export: '60000000-0000-4000-8000-000000000001' };
const iso = value => new Date(value).toISOString();
const json = (route, value, status = 200, headers = {}) => route.fulfill({ status, contentType: 'application/json', headers, body: JSON.stringify(value) });
function makeJob(state, options = {}) {
  const now = Date.now();
  const requested = iso(now - 120000);
  const ready = state === 'ready';
  const expiry = options.expired ? iso(now - 1000) : iso(now + 60000);
  return {
    schemaVersion: 1, id: ids.export, state,
    reason: state === 'failed' ? (options.reason ?? 'export_failed') : null,
    requestedAt: requested,
    readyAt: ready ? iso(now - 90000) : null,
    downloadExpiresAt: ready ? expiry : null,
    downloadPath: ready ? `/app/v1/customer/data/exports/${ids.export}/download` : null,
  };
}

try {
  try { browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) }); }
  catch (error) {
    if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }
  context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  page = await context.newPage();
  page.setDefaultTimeout(10000);
  page.on('pageerror', error => failures.push(`pageerror: ${error.message}`));
  page.on('console', message => { if (message.type() === 'error') failures.push(`console: ${message.text()}`); });
  await context.route('**/*', async route => {
    const request = route.request();
    const url = new URL(request.url());
    if (url.origin !== origin) { failures.push(`blocked non-local request: ${request.method()} ${url.origin}${url.pathname}`); await route.abort(); return; }
    const method = request.method();
    requests.push({ path: url.pathname, method, body: request.postData() });
    if (url.pathname === '/app/v1/session' && method === 'GET') {
      return json(route, { enabled: true, account: signedInAccount, csrfToken: 'synthetic-csrf' });
    }
    if (url.pathname === '/app/v1/session' && method === 'DELETE') {
      if (switchToAccount) signedInAccount = switchToAccount;
      return route.fulfill({ status: 204, body: '' });
    }
    if (signedInAccount && url.pathname === '/app/v1/account' && method === 'GET') {
      return json(route, { sessions: [{ id: 'synthetic-session', installationName: 'Tento prohlížeč', current: true, trusted: false, expiresAt: 1900000000 }], grants: [], associations: [], passkeyCount: 1, passkeys: [{ id: 'synthetic-key', name: 'Testovací klíč', createdAt: '2026-10-20T00:00:00Z', isBackedUp: true }], devices: [] });
    }
    if (signedInAccount && url.pathname === '/app/v1/passkeys/reauthentication-options' && method === 'POST') {
      return json(route, { challenge: 'AQ', allowCredentials: [] });
    }
    if (signedInAccount && url.pathname === '/app/v1/passkeys/reauthenticate' && method === 'POST') return json(route, {});
    if (signedInAccount && url.pathname === '/app/v1/account' && method === 'DELETE') {
      accountDeletionRequested = true;
      return json(route, { status: 'deletion_requested' }, 202);
    }
    if (url.pathname === '/app/v1/account/deletion' && method === 'GET') {
      return json(route, { status: accountDeletionRequested ? 'deletion_requested' : null });
    }
    if (signedInAccount && url.pathname === '/app/v1/customer/character' && method === 'GET') return json(route, { profile: null });
    if (signedInAccount && url.pathname === '/app/v1/customer/sessions' && method === 'GET') return json(route, { items: [] });
    if (signedInAccount && url.pathname === '/app/v1/customer/memory' && method === 'GET') {
      memoryReads++;
      memoryReadAccounts.push(signedInAccount.id);
      return json(route, { revision: 'a'.repeat(64), items: [{
        ...memoryItem,
        text: signedInAccount.id === accountB.id ? 'Druhá syntetická paměť.' : memoryItem.text,
      }], complete: true });
    }
    if (signedInAccount && url.pathname === `/app/v1/customer/memory/${memoryItem.id}` && method === 'PUT') {
      memoryWrites.push({ method, body: request.postDataJSON(), account: signedInAccount.id });
      memoryItem.text = request.postDataJSON().text;
      memoryItem.revision++;
      return route.fulfill({ status: 204, body: '' });
    }
    if (signedInAccount && url.pathname === `/app/v1/customer/memory/${memoryItem.id}/forget` && method === 'POST') {
      memoryWrites.push({ method, body: request.postDataJSON(), account: signedInAccount.id });
      return route.fulfill({ status: 204, body: '' });
    }
    if (signedInAccount && url.pathname === '/app/v1/customer/data/exports' && method === 'POST') {
      exportCreateCount++;
      exportPostJob = makeJob('requested');
      return json(route, exportPostJob, 202, { Location: `/app/v1/customer/data/exports/${ids.export}` });
    }
    if (signedInAccount && url.pathname === `/app/v1/customer/data/exports/${ids.export}` && method === 'GET') {
      exportStatusReads++;
      if (exportJob) return json(route, exportJob);
      if (exportPostJob && exportStatusReads > 0) {
        exportJob = makeJob('ready', { expired: true });
        return json(route, exportJob);
      }
      return json(route, makeJob('failed'));
    }
    if (signedInAccount && url.pathname === `/app/v1/customer/data/exports/${ids.export}` && method === 'DELETE') {
      exportDeleteCount++;
      exportJob = null;
      return route.fulfill({ status: 204, body: '' });
    }
    if (signedInAccount && url.pathname === `/app/v1/customer/data/exports/${ids.export}/download` && method === 'GET') {
      return json(route, { schemaVersion: 1, account: { id: signedInAccount.id }, sourceData: 'synthetic-only' });
    }
    if (url.pathname.startsWith('/app/v1/')) return json(route, { code: 'not_available' }, signedInAccount ? 404 : 401);
    return route.continue();
  });

  signedInAccount = accountA;
  await page.goto(`${origin}/me/account`);
  await page.getByRole('heading', { name: 'Můj účet', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Zobrazit moje data', exact: true }).waitFor();
  // Session identity already selected web transport at module load. Delayed
  assert.equal(memoryReads, 0, 'memory list is not fetched before explicit disclosure');
  assert.equal(requests.some(item => item.path.includes('/customer/data/exports')), false, 'export status is not looked up before disclosure');

  const disclosure = page.getByRole('button', { name: 'Zobrazit moje data', exact: true });
  await disclosure.focus();
  await page.keyboard.press('Enter');
  await page.getByRole('heading', { name: 'Moje paměti', exact: true }).waitFor();
  await page.getByText('Mám rád modrou.', { exact: true }).waitFor();
  await page.getByRole('heading', { name: 'Stažení údajů účtu', exact: true }).waitFor();
  assert.equal(memoryReads, 1, 'opening disclosure mounts and reads memories once');
  assert.equal(requests.filter(item => item.path.startsWith('/app/v1/customer/data/exports/')).length, 0, 'no export status exists when there is no remembered job');

  await page.getByRole('button', { name: 'Opravit', exact: true }).click();
  await page.getByLabel('Opravené znění').fill('Mám rád zelenou.');
  await page.getByRole('button', { name: 'Uložit opravu', exact: true }).click();
  await page.getByText('Mám rád zelenou.', { exact: true }).waitFor();
  assert.equal(memoryWrites.length, 1, 'correction is sent only after explicit save');
  assert.equal(memoryWrites[0].method, 'PUT');
  await page.getByRole('button', { name: 'Zapomenout', exact: true }).click();
  await page.getByRole('button', { name: 'Zrušit', exact: true }).last().click();
  assert.equal(memoryWrites.length, 1, 'canceling confirmed-forget prompt sends no mutation');
  await page.getByRole('button', { name: 'Zapomenout', exact: true }).click();
  await page.getByRole('button', { name: 'Ano, zapomenout tuto paměť', exact: true }).click();
  await page.getByText('Mám rád zelenou.', { exact: true }).waitFor();
  assert.equal(memoryWrites.length, 2, 'forget is issued only after explicit confirmation');
  assert.equal(memoryWrites[1].method, 'POST');

  const prepare = page.getByRole('button', { name: 'Připravit export', exact: true });
  page.on('dialog', dialog => dialog.accept());
  await prepare.click();
  await page.getByRole('status').filter({ hasText: /^Export je připraven\.$/ }).waitFor();
  assert.equal(exportCreateCount, 1, 'one explicit prepare action creates one export request');
  assert.deepEqual(JSON.parse(requests.find(item => item.path === '/app/v1/customer/data/exports' && item.method === 'POST').body), { schemaVersion: 1 });
  await page.getByText('Lhůta pro stažení vypršela.', { exact: false }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Stáhnout JSON', exact: true }).isDisabled(), true, 'expired download cannot be activated');
  await page.screenshot({ path: path.join(screenshots, 'customer-data-expired-export.png'), fullPage: true });

  const toggle = page.getByRole('button', { name: 'Skrýt moje data', exact: true });
  await toggle.click();
  assert.equal(await page.getByRole('heading', { name: 'Moje paměti', exact: true }).count(), 0, 'collapse unmounts memory UI');
  assert.equal(await page.getByRole('heading', { name: 'Stažení údajů účtu', exact: true }).count(), 0, 'collapse unmounts export UI');

  // Re-open for status refresh and job-only deletion, then verify only explicit UI actions write.
  await page.getByRole('button', { name: 'Zobrazit moje data', exact: true }).click();
  await page.getByRole('heading', { name: 'Stažení údajů účtu', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Obnovit stav exportu', exact: true }).click();
  await page.getByRole('button', { name: 'Smazat pouze úlohu exportu', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Smazat pouze úlohu exportu', exact: true }).click();
  await page.getByRole('button', { name: 'Smazat pouze úlohu exportu', exact: true }).waitFor({ state: 'detached' });
  assert.equal(exportDeleteCount, 1, 'confirmed delete removes only synthetic export-job metadata');
  assert.equal(requests.some(item => item.method === 'DELETE' && item.path.includes('/customer/memory/')), false, 'export deletion never mutates memory or source data');

  // Offline/online state disables writes while offline; it does not replay any
  // correction, forget, export creation, or delete operation on reconnection.
  const writesBeforeOffline = memoryWrites.length + exportCreateCount + exportDeleteCount;
  await context.setOffline(true);
  await page.waitForFunction(() => !navigator.onLine);
  await page.getByText(/Jste offline/).first().waitFor();
  await context.setOffline(false);
  await page.waitForFunction(() => navigator.onLine);
  await page.waitForTimeout(150);
  assert.equal(memoryWrites.length + exportCreateCount + exportDeleteCount, writesBeforeOffline, 'network restoration does not replay user mutations');

  for (const theme of ['lord-voldemort', 'olivander']) {
    await page.evaluate(value => window.DildoVanderTheme.setTheme(value), theme);
    assert.equal(await page.locator('html').getAttribute('data-theme'), theme);
    for (const width of [320, 390, 1440]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      const scroll = await page.evaluate(() => ({ document: document.documentElement.scrollWidth, viewport: innerWidth }));
      assert(scroll.document <= scroll.viewport + 1, `${theme} at ${width}px and 200% text has no horizontal overflow`);
      await page.screenshot({ path: path.join(screenshots, `${theme}-${width}-200-text.png`), fullPage: true });
    }
  }

  // Sign out into another synthetic customer. Disclosure and account-private
  // children must reset; account B must issue fresh reads only after opening.
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  await page.setViewportSize({ width: 1440, height: 1000 });
  switchToAccount = accountB;
  const signOut = page.getByRole('button', { name: 'Odhlásit toto zařízení', exact: true });
  assert.equal(await signOut.count(), 1, 'customer account switch starts from an explicit signed-in device action');
  {
    await signOut.click();
    await page.waitForFunction(() => document.body.innerText.includes('Druhý host') || document.body.innerText.includes('Přihlásit se'));
    await page.goto(`${origin}/me/account`);
    await page.getByRole('heading', { name: 'Můj účet', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Zobrazit moje data', exact: true }).count(), 1);
    assert.equal(await page.getByRole('heading', { name: 'Moje paměti', exact: true }).count(), 0, 'new account starts collapsed');
    const readsAtAccountSwitch = memoryReads;
    await page.getByRole('button', { name: 'Zobrazit moje data', exact: true }).click();
    await page.getByRole('heading', { name: 'Moje paměti', exact: true }).waitFor();
    assert.equal(memoryReads, readsAtAccountSwitch + 1, 'new account reads its own memory only after disclosure');
    assert.equal(memoryReadAccounts.at(-1), accountB.id, 'post-switch read is associated with the new account');
    await page.getByText('Druhá syntetická paměť.', { exact: true }).waitFor();
    assert.equal(await page.getByText('Mám rád zelenou.', { exact: true }).count(), 0, 'old account memory is not retained after switch');

    // A synthetic successful passkey assertion and deletion acknowledgement
    // exercise the actual account-deletion state transition without contacting
    // any identity service or making a real deletion request.
    await page.evaluate(() => {
      const bytes = new Uint8Array([1]).buffer;
      const assertion = {
        id: 'synthetic-passkey', rawId: bytes, type: 'public-key', authenticatorAttachment: 'platform',
        response: { clientDataJSON: bytes, authenticatorData: bytes, signature: bytes, userHandle: null },
        getClientExtensionResults: () => ({}),
      };
      Object.defineProperty(navigator, 'credentials', {
        configurable: true, value: { get: async () => assertion, create: async () => assertion },
      });
    });
    await page.getByRole('button', { name: 'Ověřit účet pro smazání', exact: true }).click();
    await page.getByRole('button', { name: 'Trvale smazat můj účet', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Trvale smazat můj účet', exact: true }).click();
    await page.getByText(/Žádost o smazání byla přijata/).waitFor();
    assert.equal(await page.getByRole('heading', { name: 'Moje data', exact: true }).count(), 0, 'confirmed deletion acknowledgement unmounts private My Data controls');
    assert.equal(memoryWrites.length, 2, 'account deletion does not replay pending memory mutations');
  }

  assert.deepEqual(failures, [], 'no page errors, browser console errors, or external network attempts');
  console.log(`PASS compiled synthetic My Data UI; reads=${memoryReads}, memory mutations=${memoryWrites.length}, export creates=${exportCreateCount}, export deletes=${exportDeleteCount}. Backend/native acceptance is not implied. Screenshots: ${screenshots}`);
} catch (error) {
  console.error('Browser failures:', failures);
  console.error('Recent synthetic requests:', requests.slice(-20));
  if (page) {
    console.error('Rendered state:', await page.locator('main').innerText({ timeout: 1000 }).catch(() => 'main unavailable'));
    await page.screenshot({ path: path.join(screenshots, 'failure.png'), fullPage: true, timeout: 3000 }).catch(() => {});
  }
  throw error;
} finally {
  await context?.close();
  await browser?.close();
  await new Promise(resolve => server.close(resolve));
}

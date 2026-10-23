// Compiled My Data UI against an isolated HTTPS fixture and real account APIs.
// A fixture-only stdin command seeds one owned personal memory after enrollment.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const appRoot = resolve(fileURLToPath(new URL('..', import.meta.url)));
const repository = resolve(appRoot, '..');
const assembly = process.env.KUFR_TESTS_DLL;
assert(assembly, 'Set KUFR_TESTS_DLL to the built BackendTests.dll.');
await stat(resolve(repository, assembly));
await stat(resolve(appRoot, 'dist/index.html'));

const fixture = spawn('dotnet', [resolve(repository, assembly), '--customer-data-browser-fixture'], {
  cwd: repository, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'],
});
let fixtureExit;
let output = '';
let lineBuffer = '';
const fixtureLines = [];
const fixtureWaiters = [];
const apiRequests = [];
const browserErrors = [];
const httpFailures = [];
const forbiddenRequests = [];
const bearerRequests = [];
let blockedExternalRequests = 0;
let browser;
let ownerContext;
let otherContext;
let origin;
let exportId;
let correctionObserved = false;
let forgottenObserved = false;

function publishFixtureLine(line) {
  fixtureLines.push(line);
  for (const waiter of [...fixtureWaiters]) {
    if (line.startsWith(waiter.prefix)) {
      fixtureWaiters.splice(fixtureWaiters.indexOf(waiter), 1);
      clearTimeout(waiter.timeout);
      waiter.resolve(line);
    }
  }
}

function waitForFixtureLine(prefix, timeoutMs = 30000) {
  const existing = fixtureLines.find(line => line.startsWith(prefix));
  if (existing) return Promise.resolve(existing);
  return new Promise((resolveLine, rejectLine) => {
    const waiter = {
      prefix,
      resolve: resolveLine,
      timeout: setTimeout(() => {
        fixtureWaiters.splice(fixtureWaiters.indexOf(waiter), 1);
        rejectLine(new Error(`Fixture did not report ${prefix} within ${timeoutMs} ms.`));
      }, timeoutMs),
    };
    fixtureWaiters.push(waiter);
  });
}

function observeCustomerData(page) {
  page.on('request', request => {
    const url = new URL(request.url());
    if (/\/app\/v1\/customer\/(?:start-tickets|character\/correction)|\/guest\/v1\/|\/app\/v1\/devices\//.test(url.pathname)) {
      forbiddenRequests.push(`${request.method()} ${url.pathname}`);
    }
    if (url.origin === origin.origin
      && /^\/app\/v1\/customer\/(?:memory(?:\/|$)|data\/exports(?:\/|$))/.test(url.pathname)) {
      if (request.headers().authorization) bearerRequests.push(request.url());
      apiRequests.push({ method: request.method(), path: url.pathname });
    }
  });
  page.on('response', response => {
    const url = new URL(response.url());
    if (url.origin === origin.origin && response.status() >= 400) {
      httpFailures.push({ status: response.status(), method: response.request().method(), path: url.pathname });
    }
  });
  page.on('pageerror', error => browserErrors.push(`pageerror: ${error.message}`));
  page.on('console', message => {
    if (message.type() === 'error'
      && !message.text().startsWith('Failed to load resource: the server responded with a status of ')) {
      browserErrors.push(`console: ${message.text()} at ${message.location().url}`);
    }
  });
}

async function addVirtualAuthenticator(context, page) {
  const cdp = await context.newCDPSession(page);
  await cdp.send('WebAuthn.enable');
  await cdp.send('WebAuthn.addVirtualAuthenticator', { options: {
    protocol: 'ctap2',
    transport: 'internal',
    hasResidentKey: true,
    hasUserVerification: true,
    isUserVerified: true,
    automaticPresenceSimulation: true,
  } });
  return cdp;
}

async function registerCustomer(page, name) {
  await page.goto(`${origin.origin}/signin`);
  await page.getByRole('button', { name: 'Vytvořit zákaznický účet', exact: true }).click();
  await page.getByLabel('Jak vám máme říkat?', { exact: true }).fill(name);
  await page.getByRole('button', { name: 'Vytvořit účet a přístupový klíč', exact: true }).click();
  await page.waitForURL(url => url.pathname === '/me/character', { timeout: 20000 });
}

async function seedMemory(accountId, profileId, revision) {
  const payload = Buffer.from(JSON.stringify({ accountId, profileId, revision })).toString('base64url');
  const acknowledgement = waitForFixtureLine('CUSTOMER_DATA_MEMORY_SEED');
  fixture.stdin.write(`seed-memory ${payload}\n`);
  const line = await acknowledgement;
  assert.equal(line, 'CUSTOMER_DATA_MEMORY_SEEDED', 'fixture seeds a current profile owned by an active customer account');
}

let primaryFailure;
try {
  const readiness = waitForFixtureLine('CUSTOMER_DATA_BROWSER_FIXTURE ');
  fixture.stderr.on('data', chunk => { output = (output + chunk).slice(-12000); });
  fixture.stdout.on('data', chunk => {
    output = (output + chunk).slice(-12000);
    lineBuffer += chunk;
    const lines = lineBuffer.split(/\r?\n/);
    lineBuffer = lines.pop();
    for (const line of lines) publishFixtureLine(line);
  });
  const fixtureFailure = new Promise((_, reject) => {
    fixture.once('exit', code => {
      fixtureExit = code ?? 1;
      if (!fixtureLines.some(line => line.startsWith('CUSTOMER_DATA_BROWSER_FIXTURE '))) {
        reject(new Error(`Isolated customer-data fixture exited before readiness (${fixtureExit}).`));
      }
    });
    fixture.once('error', reject);
  });
  const readyLine = await Promise.race([readiness, fixtureFailure]);
  origin = new URL(readyLine.slice('CUSTOMER_DATA_BROWSER_FIXTURE '.length).trim());
  assert.equal(origin.protocol, 'https:');
  assert.equal(origin.hostname, 'localhost');

  try {
    browser = await chromium.launch({ ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
  } catch (error) {
    if (process.env.PLAYWRIGHT_CHANNEL || process.platform !== 'win32') throw error;
    browser = await chromium.launch({ channel: 'chrome' });
  }

  ownerContext = await browser.newContext({ ignoreHTTPSErrors: true, acceptDownloads: true, viewport: { width: 1440, height: 1000 } });
  const page = await ownerContext.newPage();
  page.setDefaultTimeout(15000);
  observeCustomerData(page);
  await ownerContext.route('**/*', async route => {
    if (new URL(route.request().url()).origin !== origin.origin) {
      blockedExternalRequests++;
      await route.abort();
      return;
    }
    await route.continue();
  });
  await ownerContext.route('**/__customer-data-browser-text-200.css', route => route.fulfill({
    status: 200,
    contentType: 'text/css',
    body: 'html { font-size: 200% !important; }',
  }));
  await addVirtualAuthenticator(ownerContext, page);

  await registerCustomer(page, 'Skutečný testovací zákazník');
  await page.locator('#character-name').fill('Modrá čarodějka');
  await page.locator('#character-world').fill('Testovací svět');
  const profileSave = page.waitForResponse(response => response.url().endsWith('/app/v1/customer/character')
    && response.request().method() === 'PUT');
  await page.getByRole('button', { name: 'Vytvořit postavu', exact: true }).click();
  assert.equal((await profileSave).status(), 200);
  await page.getByRole('status').filter({ hasText: 'Uloženo bez AI přípravy' }).waitFor();

  const bootstrap = await page.evaluate(async () => {
    const [sessionResponse, profileResponse] = await Promise.all([
      fetch('/app/v1/session', { credentials: 'include' }),
      fetch('/app/v1/customer/character', { credentials: 'include' }),
    ]);
    return { session: await sessionResponse.json(), profile: await profileResponse.json() };
  });
  const ownerAccount = bootstrap.session.account;
  const ownerProfile = bootstrap.profile.profile;
  assert(ownerAccount.roles.includes('Customer'));
  assert.equal(ownerProfile.status, 'saved');
  assert.equal(ownerProfile.userInput.name, 'Modrá čarodějka');
  await seedMemory(ownerAccount.id, ownerProfile.profileId, ownerProfile.revision);

  await page.goto(`${origin.origin}/me/account`);
  await page.getByRole('heading', { name: 'Moje data', exact: true }).waitFor();
  assert.equal(apiRequests.length, 0, 'opening account settings does not eagerly read memory or export data');
  const expand = page.locator('.customer-data-panel__toggle');
  await expand.focus();
  await page.keyboard.press('Space');
  assert.equal(await expand.getAttribute('aria-expanded'), 'true', 'the disclosure opens from the keyboard');
  const originalFact = 'Moje oblíbená barva je modrá.';
  await page.getByText(originalFact, { exact: true }).waitFor();
  assert(apiRequests.some(request => request.method === 'GET' && request.path === '/app/v1/customer/memory'),
    'expanded My Data uses the real authenticated memory endpoint');
  assert(!apiRequests.some(request => request.path.startsWith('/app/v1/customer/data/exports')),
    'a missing export is not fetched until there is a saved job to inspect');

  await page.getByRole('button', { name: 'Opravit', exact: true }).click();
  const correctedFact = 'Moje oblíbená barva je modrá a zelená.';
  await page.getByLabel('Opravené znění', { exact: true }).fill(correctedFact);
  const correction = page.waitForResponse(response => response.url().includes('/app/v1/customer/memory/')
    && response.request().method() === 'PUT');
  await page.getByRole('button', { name: 'Uložit opravu', exact: true }).click();
  assert.equal((await correction).status(), 204);
  await page.getByText(correctedFact, { exact: true }).waitFor();
  const memoryAfterCorrection = await page.evaluate(async () => (await (await fetch('/app/v1/customer/memory')).json()).items);
  assert.equal(memoryAfterCorrection.length, 1);
  assert.equal(memoryAfterCorrection[0].text, correctedFact);
  correctionObserved = true;

  page.on('dialog', dialog => dialog.accept());
  const createExport = page.waitForResponse(response => response.url().endsWith('/app/v1/customer/data/exports')
    && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Připravit export', exact: true }).click();
  const created = await createExport;
  assert.equal(created.status(), 202, 'the real owner endpoint accepted an explicit export request');
  const location = created.headers().location;
  assert(location && /^\/app\/v1\/customer\/data\/exports\/[0-9a-f-]{36}$/i.test(location));
  exportId = location.split('/').at(-1);
  await page.waitForFunction(() => document.body.innerText.includes('Export je připraven.'), null, { timeout: 60000 });
  assert(apiRequests.some(request => request.method === 'GET' && request.path === `${location}`),
    'the browser polls the real export worker status endpoint until ready');

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('button', { name: 'Stáhnout JSON', exact: true }).click(),
  ]);
  const downloadedPath = await download.path();
  assert(downloadedPath);
  const exportDocument = JSON.parse(await readFile(downloadedPath, 'utf8'));
  assert.equal(exportDocument.schemaVersion, 1);
  assert.equal(exportDocument.account.id, ownerAccount.id);
  assert(exportDocument.memoryFacts.some(item => item.text === correctedFact), 'the verified JSON contains the corrected permitted fact');

  await page.getByRole('button', { name: 'Zapomenout', exact: true }).click();
  await page.getByRole('button', { name: 'Ano, zapomenout tuto paměť', exact: true }).click();
  await page.getByText('Momentálně nejsou dostupná žádná čitelná osobní fakta.', { exact: false }).waitFor();
  const memoriesAfterForget = await page.evaluate(async () => (await (await fetch('/app/v1/customer/memory')).json()).items);
  assert.equal(memoriesAfterForget.length, 0);
  forgottenObserved = true;
  const changedSourceDownload = await page.evaluate(async path => (await fetch(path)).status, `${location}/download`);
  assert.equal(changedSourceDownload, 409, 'forgetting a source fact invalidates the earlier export proof');

  // A separate customer passkey proves the real account boundary without
  // sharing cookies, session IDs, storage, or credentials between contexts.
  otherContext = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 } });
  const otherPage = await otherContext.newPage();
  otherPage.setDefaultTimeout(15000);
  observeCustomerData(otherPage);
  await otherContext.route('**/*', async route => {
    if (new URL(route.request().url()).origin !== origin.origin) {
      blockedExternalRequests++;
      await route.abort();
      return;
    }
    await route.continue();
  });
  await addVirtualAuthenticator(otherContext, otherPage);
  await registerCustomer(otherPage, 'Oddělený testovací zákazník');
  const otherAccount = await otherPage.evaluate(async () => (await (await fetch('/app/v1/session')).json()).account);
  assert.notEqual(otherAccount.id, ownerAccount.id);
  const foreignState = await otherPage.evaluate(async id => {
    const [memory, status] = await Promise.all([
      fetch('/app/v1/customer/memory'),
      fetch(`/app/v1/customer/data/exports/${id}`),
    ]);
    return { memories: await memory.json(), status: status.status };
  }, exportId);
  assert.equal(foreignState.memories.items.length, 0);
  assert.equal(foreignState.status, 404, 'a different account cannot inspect the first account\'s export');
  await otherPage.goto(`${origin.origin}/me/account`);
  await otherPage.getByRole('button', { name: 'Zobrazit moje data', exact: true }).click();
  await otherPage.getByText('Momentálně nejsou dostupná žádná čitelná osobní fakta.', { exact: false }).waitFor();
  const otherAccountFactCount = await otherPage.locator('body').getByText(originalFact, { exact: true }).count();
  assert.equal(otherAccountFactCount, 0, 'the second account does not render the first account\'s fact');

  await page.setViewportSize({ width: 320, height: 900 });
  await page.waitForTimeout(100);
  const narrowLayout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
  assert(narrowLayout.scrollWidth <= narrowLayout.width, 'the actual account page fits a 320px viewport');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(100);
  const phoneLayout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
  assert(phoneLayout.scrollWidth <= phoneLayout.width, 'the actual account page fits a 390px viewport');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.addStyleTag({ url: '/__customer-data-browser-text-200.css' });
  const enlargedLayout = await page.evaluate(() => ({ width: innerWidth, scrollWidth: document.documentElement.scrollWidth }));
  assert(enlargedLayout.scrollWidth <= enlargedLayout.width, 'the real account controls do not overflow at 200% text size');

  assert.equal(blockedExternalRequests, 0, 'the real browser journey made no external/provider requests');
  assert.deepEqual(bearerRequests, [], 'customer data uses the account cookie transport, not bearer credentials');
  assert.deepEqual(forbiddenRequests, [], 'opening My Data did not start a conversation or issue a guest/device command');
  assert.deepEqual(browserErrors, [], `the real browser journey has no page or console errors: ${JSON.stringify(browserErrors)}`);
  assert.deepEqual(httpFailures.map(({ status, method, path }) => `${status} ${method} ${path}`).sort(), [
    `404 GET ${location}`,
    `409 GET ${location}/download`,
  ].sort(), 'only the explicit foreign-account and stale-export rejection requests fail');
  assert(correctionObserved && forgottenObserved);
  const methods = apiRequests.map(request => request.method);
  assert(methods.includes('PUT') && methods.includes('POST') && methods.includes('DELETE') === false,
    'memory corrections and export creation were explicit; no job deletion or unrelated write occurred');
  console.log('PASS real compiled My Data: HTTPS passkey customer, saved profile, lazy memory read, correction, export worker/download, forget, source invalidation, and separate-account isolation');
} catch (error) {
  primaryFailure = error;
} finally {
  await otherContext?.close().catch(() => undefined);
  await ownerContext?.close().catch(() => undefined);
  await browser?.close().catch(() => undefined);
  if (fixture.exitCode === null) {
    fixture.stdin.write('stop\n');
    fixture.stdin.end();
    await new Promise(resolveExit => {
      if (fixtureExit !== undefined) { resolveExit(); return; }
      const timeout = setTimeout(() => { fixture.kill(); resolveExit(); }, 10000);
      fixture.once('exit', () => { clearTimeout(timeout); resolveExit(); });
    });
  }
}

if (primaryFailure) {
  if (output) console.error(output.replace(/#(?:invite|recovery)=[A-Za-z0-9_-]+/g, '#redacted'));
  throw primaryFailure;
}
assert.equal(fixtureExit, 0, `Isolated customer-data fixture exited with ${fixtureExit}.`);
assert(fixtureLines.includes('CUSTOMER_DATA_FIXTURE_STOPPED'), 'the test fixture stopped gracefully and disposed its temporary services');

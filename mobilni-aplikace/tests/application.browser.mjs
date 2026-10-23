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
  let sessionAccount = null;
  let customerSessions = [];
  let failedCustomerPolls = 0;
  let customerStatusReads = 0;
  const customerEndRequests = [];
  let customerProfile = { profileId: 'browser-profile', revision: 2, userInput: { name: 'Luna', world: 'Moon Saga' }, status: 'ready', enrichment: { summary: 'A prepared character summary.', sources: ['Public character guide'] } };
  const characterWrites = [];
  const resultLocator = 'b1dfd34f-5362-4e60-8e31-a7fac4b326c8';
  let sessionResult = {
    schemaVersion: 1, sessionId: 'browser-recovering-session', resultLocator: null,
    readiness: { state: 'pending', reason: 'awaiting_result', canRetry: false, shouldPoll: true, hasStory: false, hasArtwork: false },
    accessKind: null, canReadPrivate: false, canManage: false, canUnlink: false,
  };
  const ownerAccount = { id: 'browser-owner', displayName: 'Testovací vlastník', roles: ['Owner'] };
  const homeHeading = () => sessionAccount ? `Vítejte, ${sessionAccount.displayName}` : 'Kufr plný příběhů.';
  await context.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin) { await route.abort(); return; }
    requests.push({ path: url.pathname, method: request.method(), url: request.url() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: sessionAccount, csrfToken: 'isolated-browser-csrf' });
    if (sessionAccount && url.pathname === '/app/v1/account') return json({ sessions: [{ id: 'browser-session', installationName: 'Tento prohlížeč', current: true, trusted: false, expiresAt: 1900000000 }], grants: [], associations: [], passkeyCount: 1, passkeys: [{ id: 'browser-passkey', name: 'Můj klíč', createdAt: '2026-10-20T00:00:00Z', isBackedUp: true }], devices: [] });
    if (sessionAccount && url.pathname === '/app/v1/customer/character') {
      if (request.method() === 'PUT') {
        const body = request.postDataJSON();
        characterWrites.push(body);
        customerProfile = { profileId: body.newCharacter ? 'browser-new-profile' : customerProfile.profileId, revision: body.newCharacter ? 1 : customerProfile.revision + 1, userInput: body.userInput, status: body.openAiConsent ? 'queued' : 'saved' };
      }
      return json({ profile: customerProfile });
    }
    if (sessionAccount && url.pathname === '/app/v1/customer/sessions') {
      customerStatusReads += 1;
      if (failedCustomerPolls > 0) {
        failedCustomerPolls -= 1;
        return json({ code: 'temporarily_unavailable' }, 503);
      }
      return json({ items: customerSessions });
    }
    if (sessionAccount && url.pathname === '/app/v1/customer/sessions/browser-recovering-session/result') {
      assert.equal(request.method(), 'GET', 'result observation is read-only');
      return json(sessionResult);
    }
    if (sessionAccount && request.method() === 'POST' && url.pathname === '/app/v1/customer/sessions/browser-recovering-session/end') {
      customerEndRequests.push(url.pathname);
      customerSessions = customerSessions.map(session => ({ ...session, endRequested: true }));
      return json({}, 202);
    }
    if (sessionAccount && url.pathname === '/app/v1/customer/admin/sessions') return json({ items: [] });
    if (sessionAccount && url.pathname === '/app/v1/customer/admin/characters') return json({ items: [] });
    if (sessionAccount && url.pathname === '/app/v1/admin/users') return json({ users: [], grants: [] });
    if (sessionAccount && url.pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (url.pathname.startsWith('/app/v1/')) return sessionAccount
      ? json({ code: 'not_available' }, 404)
      : json({ code: 'account_required' }, 401);
    if (url.pathname === '/guest/v1/evening') return json({ schemaVersion: 1, evening: null, items: [], latest: null });
    if (url.pathname === '/guest/v1/bootstrap') return json({ schemaVersion: 1, csrfToken: 'isolated-guest-csrf' });
    if (url.pathname === '/guest/v1/claim') return json({ locator: '11111111-1111-4111-8111-111111111111' });
    if (url.pathname.startsWith('/guest/v1/')) return json({ code: 'not_available' }, 404);
    await route.continue();
  });
  const hasHeading = title => page.getByRole('heading', { name: title, exact: true }).waitFor();
  const fits = async () => {
    await page.evaluate(async () => {
      await document.fonts.ready;
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'layout has no horizontal page overflow');
  };

  await page.goto(origin); await hasHeading(homeHeading());
  await fits(); await page.screenshot({ path: path.join(screenshots, 'home-desktop.png'), fullPage: true });
  await page.getByLabel('Vzhled aplikace').selectOption('olivander'); assert.equal(await page.locator('html').getAttribute('data-theme'), 'olivander');
  await page.reload(); await hasHeading(homeHeading()); assert.equal(await page.locator('html').getAttribute('data-theme'), 'olivander');
  await page.getByLabel('Vzhled aplikace').selectOption('lord-voldemort');
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits();
    assert(await page.getByRole('link', { name: 'Stáhnout aplikaci pro iPhone', exact: true }).isVisible());
    assert.equal(await page.locator('.home-readiness').count(), 0, 'public landing has no device status panel');
    await page.screenshot({ path: path.join(screenshots, `public-landing-${width}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  for (const [route, title] of [['/support', 'S čím potřebujete pomoct?'], ['/privacy', 'Ochrana soukromí']]) {
    await page.goto(origin + route);
    await hasHeading(title);
    assert.equal(new URL(page.url()).pathname, route, 'support and privacy remain public without an account');
    assert(await page.getByRole('main').getByRole('link', { name: 'vojtech.petrik58@gmail.com', exact: true }).isVisible());
    for (const width of [1440, 390, 320]) {
      await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
      await fits();
      await page.screenshot({ path: path.join(screenshots, `${route.slice(1)}-${width}.png`), fullPage: true });
    }
  }
  await page.goto(origin);
  await hasHeading(homeHeading());
  await page.setViewportSize({ width: 1440, height: 1000 });
  // Anonymous deep links must not render pairing or operational controls.
  assert.equal(await page.locator('.home-readiness').count(), 0);
  assert.equal(await page.getByRole('button', { name: 'Stop', exact: true }).count(), 0);
  for (const protectedPath of ['/suitcase', '/connections', '/suitcase/diagnostics', '/suitcase/director', '/settings/admin']) {
    await page.goto(origin + protectedPath);
    await page.waitForURL(url => url.pathname === '/signin');
    assert.equal(new URL(page.url()).searchParams.get('return'), protectedPath);
    assert.equal(await page.getByRole('button', { name: 'Vyhledat Bluetooth kufry' }).count(), 0);
  }
  for (const entry of ['/me', '/me/']) {
    await page.goto(origin + entry);
    await page.waitForURL(url => url.pathname === '/signin' && url.searchParams.get('return') === '/me/character');
  }
  await page.goto(origin + '/signin'); await page.getByRole('heading', { name: 'Přihlásit se', exact: true }).waitFor();
  const lostAccess = page.locator('.lost-access-help');
  const lostAccessSummary = lostAccess.locator('summary');
  assert.equal(await lostAccess.getAttribute('open'), null);
  await lostAccessSummary.focus();
  await page.keyboard.press('Enter');
  assert.notEqual(await lostAccess.getAttribute('open'), null);
  assert(await lostAccess.getByRole('link', { name: 'kontaktujte podporu' }).isVisible());
  assert.match(await lostAccess.innerText(), /pouze vlastník služby/);
  for (const theme of ['lord-voldemort', 'olivander']) {
    await page.getByLabel('Vzhled aplikace').selectOption(theme);
    await page.setViewportSize({ width: 320, height: 900 });
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    assert(await lostAccess.getByRole('link', { name: 'kontaktujte podporu' }).isVisible());
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'lost-access guidance must not overflow at narrow width and enlarged text');
    await lostAccess.screenshot({ path: path.join(screenshots, `lost-access-${theme}-320.png`) });
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  await page.getByLabel('Vzhled aplikace').selectOption('lord-voldemort');
  await page.goto(origin + '/signin');
  await page.getByRole('button', { name: 'Vytvořit zákaznický účet', exact: true }).click();
  assert.equal(await page.locator('.lost-access-help').count(), 0, 'enrollment keeps its own recovery guidance');
  assert(await page.getByLabel('Jak vám máme říkat?').isVisible());
  assert.equal(await page.locator('#account-invite').count(), 0, 'customer registration does not ask for an invite');
  // Customer pages use isolated API fixtures. The end request below changes only
  // synthetic session state; browser mode never requests BLE or a start ticket.
  sessionAccount = { id: 'browser-customer', displayName: 'Testovací host', roles: ['Customer'] };
  for (const entry of ['/me', '/me/']) {
    await page.goto(origin + entry);
    await page.waitForURL(url => url.pathname === '/me/character');
    await hasHeading('Moje postava');
  }
  await page.goto(origin + '/me/character'); await hasHeading('Moje postava');
  assert.equal(await page.getByRole('navigation', { name: 'Sekce aplikace' }).getByRole('link').count(), 4);
  assert.equal(await page.getByRole('button', { name: 'Stop', exact: true }).count(), 0);
  await page.getByRole('heading', { name: 'Luna', exact: true }).waitFor();
  assert.equal(await page.getByText('A prepared character summary.', { exact: true }).isVisible(), false, 'preparation detail starts collapsed');
  await page.getByText('Upravit nebo upřesnit postavu', { exact: true }).click();
  assert.equal(await page.locator('#character-name').inputValue(), 'Luna');
  assert.equal(await page.locator('#character-openai-consent').isChecked(), false, 'character enrichment requires an unchecked OpenAI opt-in');
  assert(await page.getByText(/společnosti OpenAI/).first().isVisible());
  assert.equal(await page.getByText('Public character guide').count(), 1);
  await page.getByText('Upravit nebo upřesnit postavu', { exact: true }).click();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits();
    await page.screenshot({ path: path.join(screenshots, `customer-character-${width}.png`), fullPage: true });
  }
  await page.getByText('Upravit nebo upřesnit postavu', { exact: true }).click();
  await page.locator('#character-name').fill('Neuložená oprava Luny');
  await page.locator('#character-openai-consent').check();
  await page.getByRole('button', { name: 'Nová postava', exact: true }).click();
  const newCharacterDialog = page.getByRole('dialog', { name: 'Vytvořit novou postavu', exact: true });
  await newCharacterDialog.waitFor();
  assert.equal(await page.locator('#new-character-name').inputValue(), '');
  assert.equal(await page.locator('#new-character-openai-consent').isChecked(), false, 'new identity never inherits existing AI consent');
  await page.locator('#new-character-name').fill('Zrušený koncept');
  await page.keyboard.press('Escape');
  await newCharacterDialog.waitFor({ state: 'hidden' });
  assert.equal(await page.locator('#character-name').inputValue(), 'Neuložená oprava Luny');
  assert.equal(characterWrites.length, 0, 'canceling a new identity never saves');
  assert.equal(await page.getByRole('button', { name: 'Nová postava', exact: true }).evaluate(button => button === document.activeElement), true, 'dialog restores focus to its opener');
  await page.getByRole('button', { name: 'Nová postava', exact: true }).click();
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits();
    await page.keyboard.press('Tab');
    assert(await newCharacterDialog.evaluate(dialog => dialog.contains(document.activeElement)), 'keyboard focus stays in the new-identity dialog');
    await page.screenshot({ path: path.join(screenshots, `customer-new-character-${width}.png`), fullPage: true });
  }
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
  const confirmNewCharacter = newCharacterDialog.getByRole('button', { name: 'Vytvořit novou postavu', exact: true });
  await confirmNewCharacter.scrollIntoViewIfNeeded();
  await fits();
  assert(await confirmNewCharacter.isVisible(), 'new identity confirmation remains reachable with enlarged text');
  assert(await newCharacterDialog.getByRole('button', { name: 'Zrušit', exact: true }).isVisible());
  await page.screenshot({ path: path.join(screenshots, 'customer-new-character-320-text-200.png'), fullPage: true });
  await page.evaluate(() => { document.documentElement.style.fontSize = ''; });
  await page.locator('#new-character-name').fill('Mira');
  await page.locator('#new-character-world').fill('Star Book');
  await newCharacterDialog.getByRole('button', { name: 'Vytvořit novou postavu', exact: true }).click();
  await newCharacterDialog.waitFor({ state: 'hidden' });
  await page.getByRole('heading', { name: 'Mira', exact: true }).waitFor();
  assert.equal(characterWrites.length, 1);
  assert.equal(characterWrites[0].newCharacter, true);
  assert.equal(characterWrites[0].expectedProfileId, 'browser-profile');
  assert.equal(characterWrites[0].expectedRevision, 2);
  assert.equal(characterWrites[0].openAiConsent, false);
  assert(await page.getByText('Postava je uložená bez AI přípravy. Můžete pokračovat k rozhovoru.', { exact: true }).isVisible());
  await page.goto(origin);
  await page.getByRole('link', { name: 'Pokračovat k rozhovoru', exact: true }).waitFor();
  assert(await page.getByRole('link', { name: 'Moje památky', exact: true }).last().isVisible());
  // Exercise the capability fallback used by supported pre-15.4 iOS browsers.
  // This is a desktop regression fixture, not an actual iPhone acceptance gate.
  const fallbackPage = await context.newPage();
  fallbackPage.on('pageerror', error => errors.push(error.message));
  await fallbackPage.addInitScript(() => {
    Object.defineProperty(HTMLDialogElement.prototype, 'showModal', { configurable: true, value: undefined });
    Object.defineProperty(HTMLDialogElement.prototype, 'close', { configurable: true, value: undefined });
  });
  await fallbackPage.setViewportSize({ width: 390, height: 844 });
  await fallbackPage.goto(origin + '/me/character');
  const fallbackOpener = fallbackPage.getByRole('button', { name: 'Nová postava', exact: true });
  await fallbackOpener.click();
  const inlineCharacter = fallbackPage.getByRole('region', { name: 'Vytvořit novou postavu', exact: true });
  await inlineCharacter.waitFor();
  assert.equal(await fallbackPage.locator('dialog').count(), 0);
  assert.equal(await fallbackPage.locator('#new-character-name').evaluate(input => input === document.activeElement), true);
  await inlineCharacter.getByRole('button', { name: 'Zrušit', exact: true }).click();
  await inlineCharacter.waitFor({ state: 'hidden' });
  assert.equal(await fallbackOpener.evaluate(button => button === document.activeElement), true);
  await fallbackOpener.click();
  await fallbackPage.locator('#new-character-name').fill('Sora');
  await fallbackPage.locator('#new-character-world').fill('Sky Book');
  await fallbackPage.screenshot({ path: path.join(screenshots, 'customer-inline-character-390.png'), fullPage: true });
  await inlineCharacter.getByRole('button', { name: 'Vytvořit novou postavu', exact: true }).click();
  await inlineCharacter.waitFor({ state: 'hidden' });
  await fallbackPage.getByRole('heading', { name: 'Sora', exact: true }).waitFor();
  assert.equal(characterWrites.length, 2);
  assert.equal(characterWrites[1].newCharacter, true);
  assert.equal(characterWrites[1].openAiConsent, false);
  await fallbackPage.close();
  const customerNav = page.getByRole('navigation', { name: 'Sekce aplikace' });
  await customerNav.getByRole('link', { name: 'Rozhovor', exact: true }).click(); await hasHeading('Rozhovor s kufrem');
  assert.equal(await page.locator('#conversation-openai-consent').isChecked(), false, 'voice consent is separate and unchecked');
  assert(await page.getByRole('button', { name: 'Promluvit s kufrem', exact: true }).isDisabled());
  assert(await page.getByText(/Bluetooth připojení je dostupné v instalované aplikaci/).isVisible());
  assert.equal(requests.filter(request => request.path.includes('customerBle') || request.path.includes('start-tickets')).length, 0);
  customerSessions = [{ id: 'browser-recovering-session', operationId: 'browser-original-operation', deviceId: 'browser-suitcase', profileId: 'browser-profile', profileRevision: 2, state: 'recovering', endRequested: false, createdAt: 1792710000, characterName: 'Obnovený rozhovor' }];
  failedCustomerPolls = 1;
  const readsBeforeRecovery = customerStatusReads;
  await page.reload();
  await page.getByText('Stav: Obnovuje se', { exact: true }).waitFor({ timeout: 10000 });
  assert(customerStatusReads >= readsBeforeRecovery + 2, 'a failed initial poll automatically retries the authoritative restored session');
  const refreshConversation = page.getByRole('button', { name: 'Obnovit stav rozhovoru', exact: true });
  assert(await refreshConversation.isVisible(), 'restored sessions can refresh without a local pending operation or native BLE');
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits();
    await page.screenshot({ path: path.join(screenshots, `customer-recovering-${width}.png`), fullPage: true });
  }
  await page.getByRole('button', { name: 'Ukončit rozhovor', exact: true }).click();
  await page.getByRole('button', { name: 'Konec požádán', exact: true }).waitFor();
  assert(await page.getByRole('button', { name: 'Konec požádán', exact: true }).isDisabled());
  assert.equal(customerEndRequests.length, 1, 'one user request creates only one end command');
  assert(await page.getByText('Stav: Obnovuje se', { exact: true }).isVisible(), 'an accepted end request does not claim physical completion');
  customerSessions = customerSessions.map(session => ({ ...session, state: 'completed' }));
  await refreshConversation.click();
  await page.getByText('Stav: Dokončen', { exact: true }).waitFor();
  await page.getByText('Výsledek se připravuje.', { exact: true }).waitFor();
  assert.equal(await page.getByRole('link', { name: 'Otevřít památku', exact: true }).count(), 0, 'a completed session does not fabricate a result locator');
  const refreshResult = page.getByRole('button', { name: 'Obnovit stav památky', exact: true });
  sessionResult = {
    ...sessionResult, resultLocator, accessKind: 'originalOwner', canManage: true, canReadPrivate: true,
    readiness: { state: 'partial', reason: 'reconciliation_required', canRetry: false, shouldPoll: true, hasStory: true, hasArtwork: false },
  };
  await refreshResult.click();
  await page.getByText('Výsledek čeká na ověření. Aplikace ho automaticky znovu nevytváří.', { exact: true }).waitFor();
  assert(await page.getByText('Text památky je dostupný.', { exact: true }).isVisible());
  for (const width of [1440, 390, 320]) {
    await page.setViewportSize({ width, height: width === 1440 ? 1000 : 844 });
    await fits();
    await page.screenshot({ path: path.join(screenshots, `customer-result-partial-${width}.png`), fullPage: true });
  }
  sessionResult = { ...sessionResult, readiness: { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true } };
  await refreshResult.click();
  await page.getByText('Památka je připravená.', { exact: true }).waitFor();
  assert.equal(await page.getByRole('link', { name: 'Otevřít památku', exact: true }).getAttribute('href'), `/keepsake/?account=1&k=${resultLocator}`);
  await page.goto(origin);
  const latestResultLink = page.getByRole('link', { name: 'Otevřít poslední památku', exact: true });
  await latestResultLink.waitFor();
  assert.equal(await latestResultLink.getAttribute('href'), `/keepsake/?account=1&k=${resultLocator}`);
  await page.goto(origin + '/me/conversation');
  await page.getByText('Památka je připravená.', { exact: true }).waitFor();
  sessionResult = {
    ...sessionResult, resultLocator: null, accessKind: null, canManage: false, canReadPrivate: false,
    readiness: { state: 'unavailable', reason: 'result_unavailable', canRetry: false, shouldPoll: false, hasStory: false, hasArtwork: false },
  };
  await page.getByRole('button', { name: 'Obnovit stav památky', exact: true }).click();
  await page.getByText('Tato památka už není dostupná.', { exact: true }).waitFor();
  assert.equal(await page.getByRole('link', { name: 'Otevřít památku', exact: true }).count(), 0);
  assert.equal(customerEndRequests.length, 1, 'result polling never repeats the conversation end');
  assert.equal(requests.filter(request => request.path.includes('customerBle') || request.path.includes('start-tickets')).length, 0, 'recovery never issues a replacement start or controls BLE');
  customerSessions = [];
  await page.goto(origin + '/me/account'); await hasHeading('Můj účet');
  await page.getByRole('heading', { name: 'Přístupové klíče' }).waitFor();
  await page.getByRole('heading', { name: 'Přihlášená zařízení' }).waitFor();
  assert.equal(await page.getByLabel('Název přístupového klíče 1').count(), 1);
  assert.equal(await page.getByRole('heading', { name: 'Verze aplikace' }).count(), 0);
  assert.equal(await page.getByRole('heading', { name: 'Přidělené prostředky' }).count(), 0);
  assert.equal(await page.locator('h1').count(), 1, 'embedded account security view does not add a second page heading');
  for (const protectedPath of ['/persona-lab', '/suitcase', '/connections', '/suitcase/diagnostics', '/suitcase/director', '/settings', '/settings/admin', '/admin/customer-sessions']) {
    await page.goto(origin + protectedPath);
    await page.waitForURL(url => url.pathname === '/me/character');
    assert.equal(await page.getByRole('heading', { name: 'Moje postava' }).count(), 1, `customer denied ${protectedPath}`);
  }
  // The remainder exercises existing operations with a labelled synthetic owner.
  sessionAccount = ownerAccount;
  await page.goto(origin); await hasHeading(homeHeading());
  await page.goto(origin + '/admin/customer-sessions'); await hasHeading('Zákaznické relace');
  await page.getByRole('heading', { name: 'Příprava postav' }).waitFor();
  assert.equal(await page.getByText('Žádná příprava postavy není v evidenci.').count(), 1);
  const preferenceTab = await context.newPage();
  await preferenceTab.goto(origin + '/settings');
  await preferenceTab.getByLabel('Barevné téma', { exact: true }).selectOption('olivander');
  assert.equal(await preferenceTab.getByLabel('Vzhled aplikace').inputValue(), 'olivander', 'settings and header synchronize in the same tab');
  await page.waitForFunction(() => document.documentElement.dataset.theme === 'olivander');
  assert.equal(await page.getByLabel('Vzhled aplikace').inputValue(), 'olivander', 'a second tab receives the stored preference');
  await preferenceTab.close();
  await page.getByLabel('Vzhled aplikace').selectOption('lord-voldemort');
  const nav = page.getByRole('navigation', { name: 'Sekce aplikace' });
  for (const [label, heading] of [['Kufr', 'Kufr'], ['Dílna', 'Workshop'], ['Archiv', 'Najdi svoji hůlku.'], ['Správa', 'Uživatelé a oprávnění']]) {
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
    await page.goto(origin); await hasHeading(homeHeading());
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
  let ownerSessionActive = true;
  await context.route('**/app/v1/**', async route => {
    const request = route.request();
    const pathname = new URL(request.url()).pathname;
    ownerRequests.push({ path: pathname, method: request.method() });
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (request.method() !== 'GET') return json({ code: 'synthetic_fixture_read_only' }, 405);
    if (pathname === '/app/v1/session') return json({ enabled: true, account: ownerSessionActive ? owner : null, csrfToken: 'synthetic-owner-csrf' });
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
  await page.goto(origin + '/connections');
  await hasHeading('Připojení kufru');
  sessionAccount = null;
  ownerSessionActive = false;
  await page.evaluate(() => window.dispatchEvent(new Event('online')));
  await page.waitForURL(url => url.pathname === '/signin');
  assert.equal(await page.locator('.connections-view').count(), 0, 'logout unmounts cached private operations');
  await page.goBack();
  assert.notEqual(new URL(page.url()).pathname, '/connections', 'history cannot reopen logged-out operations');
  assert.deepEqual(errors, [], 'no uncaught application errors');
  console.log(`PASS compiled application: all 14 route views (including unauthenticated/invalid-capability gates), synthetic owner settings/admin, management draft preservation with no writes, iPhone links, navigation, deep links, compatibility, claim capture, demo isolation, focus, both themes, keyboard, 1440/390/320 layouts and 200% text. Screenshots: ${screenshots}`);
  await context.close();
} catch (error) {
  console.error('Application errors:', errors);
  if (page) {
    console.error('Application state on failure:', await page.locator('main').innerText({ timeout: 1000 }).catch(() => 'main unavailable'));
    console.error('Application layout on failure:', await page.evaluate(() => ({
      viewport: innerWidth, width: document.documentElement.scrollWidth,
      overflow: [...document.querySelectorAll('body *')].map(element => ({
        tag: element.tagName, classes: typeof element.className === 'string' ? element.className : '',
        right: element.getBoundingClientRect().right,
      })).filter(element => element.right > innerWidth + 1).slice(0, 12),
    })).catch(() => 'layout unavailable'));
    await page.screenshot({ path: path.join(screenshots, 'failure.png'), fullPage: true, timeout: 3000 }).catch(() => {});
  }
  throw error;
} finally { await browser?.close(); await new Promise(resolve => server.close(resolve)); }

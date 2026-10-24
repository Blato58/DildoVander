// The compiled visitor flow against a synthetic API; no provider or device calls.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';
const dist = fileURLToPath(new URL('../dist/', import.meta.url));
const screenshots = path.join(tmpdir(), 'dildovander-personal-duel');
await mkdir(screenshots, { recursive: true });
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.ttf': 'font/ttf', '.svg': 'image/svg+xml' };
const server = createServer(async (request, response) => {
  try {
    let file = path.resolve(dist, new URL(request.url, 'http://localhost').pathname.slice(1) || 'index.html');
    if (!file.startsWith(dist)) return response.writeHead(403).end();
    try { if (!(await stat(file)).isFile()) file = path.join(dist, 'index.html'); } catch { file = path.join(dist, 'index.html'); }
    response.writeHead(200, { 'Content-Type': mime[path.extname(file)] ?? 'application/octet-stream' }); response.end(await readFile(file));
  } catch { response.writeHead(500).end(); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
const id = '11111111-1111-4111-8111-111111111111';
const imageAssetId = '22222222-2222-4222-8222-222222222222';
const imageBytes = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aN1sAAAAASUVORK5CYII=', 'base64');
const spells = side => ['strike', 'trick', 'shield'].map((action, index) => ({ action, name: `${side === 'left' ? 'Lunární' : 'Sluneční'} ${['zásah', 'klam', 'štít'][index]}`, description: 'Fiktivní kouzlo pro syntetickou zkoušku.' }));
const tokens = { left: 'a'.repeat(43), right: 'b'.repeat(43) };
const view = {
  id, revision: 1, rulesVersion: 'synthetic-fixture', state: 'AwaitingParticipants', mode: 'interactive', round: 1, deadline: null,
  requiresParticipantConsent: true, leftJoined: false, rightJoined: false,
  left: { displayName: 'Luna', wandId: 'personal-left', wandName: 'Měsíční ozvěna', strength: 3, cunning: 3, defense: 3, spells: spells('left'), physicalSlot: 2 },
  right: { displayName: 'Sol', wandId: 'personal-right', wandName: 'Jiskra poledne', strength: 3, cunning: 3, defense: 3, spells: spells('right'), physicalSlot: 2 },
  narrationMode: 'web_only', narrationStatus: 'web_only_no_spoken_confirmation', rounds: [], result: null, leftChoiceLocked: false, rightChoiceLocked: false,
};
const choices = {}; const posts = []; const errors = [];
let browser;
try {
  browser = await chromium.launch();
  async function client(side, viewport) {
    const context = await browser.newContext({ viewport, reducedMotion: 'reduce' });
    await context.route('**/*', async route => {
      const request = route.request(); const url = new URL(request.url());
      if (url.origin !== origin) return route.abort();
      const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
      if (url.pathname === '/app/v1/session') return json({ enabled: true, account: null });
      if (url.pathname === '/guest/v1/evening') return json({ schemaVersion: 1, evening: null, latest: null, items: [] });
      if (url.pathname.startsWith(`/guest/v1/duels/${id}`)) {
        if (request.method() === 'POST') {
          const body = request.postDataJSON(); posts.push({ side, body, path: url.pathname });
          assert.equal(request.headers().authorization, `Bearer ${tokens[side]}`); assert.equal(body.side, side);
          assert.equal(request.headers().cookie, undefined);
          if (url.pathname.endsWith('/join')) {
            assert.equal(body.consentConfirmed, true); view[`${side}Joined`] = true;
            if (view.leftJoined && view.rightJoined) { // Synthetic operator start after both consent.
              view.state = 'Running'; view.deadline = new Date(Date.now() + 15000).toISOString();
            }
          } else {
            assert.equal(url.pathname.endsWith('/choices'), true); choices[side] = body.action; view[`${side}ChoiceLocked`] = true;
            if (choices.left && choices.right) {
              view.rounds.push({ number: view.round, leftAction: choices.left, rightAction: choices.right, leftScore: 3, rightScore: 3, leftAutomatic: false, rightAutomatic: false, quip: 'Syntetické kolo potvrzené serverovou fixture.', mishap: 'Fiktivní komentář.' });
              delete choices.left; delete choices.right; view.leftChoiceLocked = false; view.rightChoiceLocked = false;
              if (view.round === 3) { view.state = 'Completed'; view.deadline = null; view.result = { ending: 'Remíza ze syntetické fixture.', leftPoints: 1.5, rightPoints: 1.5, exhibition: false }; }
              else { view.round++; view.deadline = new Date(Date.now() + 15000).toISOString(); }
            }
          }
          view.revision++;
        }
        // Closed public projection: choices are deliberately never included.
        return json(view);
      }
      if (/^\/(?:app\/v1|guest\/v1|v1)\//.test(url.pathname)) return json({}, 404);
      return route.continue();
    });
    const page = await context.newPage(); page.on('pageerror', error => errors.push(error.message));
    await page.goto(`${origin}/archive/duel?duel=${id}#grant=${tokens[side]}&side=${side}`);
    await page.getByRole('heading', { name: 'Souboj postav' }).waitFor();
    assert.equal(new URL(page.url()).hash, '');
    await page.getByText('Měsíční ozvěna', { exact: true }).first().waitFor();
    return page;
  }
  const left = await client('left', { width: 390, height: 844 });
  const right = await client('right', { width: 1440, height: 1000 });
  assert.equal(posts.length, 0, 'opening a participant URL does not silently consent');
  for (const page of [left, right]) {
    await page.getByRole('checkbox', { name: 'Souhlasím s účastí v tomto souboji.' }).check();
    await page.getByRole('button', { name: 'Potvrdit účast', exact: true }).click();
  }
  await left.getByRole('timer').waitFor();
  await left.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await right.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await left.screenshot({ path: path.join(screenshots, 'mobile-choice.png'), fullPage: true });
  await right.screenshot({ path: path.join(screenshots, 'desktop-choice.png'), fullPage: true });
  for (let round = 1; round <= 3; round++) {
    const leftAction = left.getByRole('button', { name: 'Lunární zásah Úder' });
    const rightAction = right.getByRole('button', { name: 'Sluneční štít Štít' });
    await leftAction.click();
    await left.getByText('Tah přijat a uzamčen. Protivníkův tah zůstává skrytý.', { exact: true }).waitFor();
    assert.equal(await leftAction.isDisabled(), true);
    await rightAction.click();
    await left.getByRole('heading', { name: `Kolo ${round} · 3 : 3`, exact: true }).waitFor();
    await right.getByRole('heading', { name: `Kolo ${round} · 3 : 3`, exact: true }).waitFor();
  }
  await left.getByRole('heading', { name: 'Výsledek', exact: true }).waitFor();
  assert.match(await left.locator('body').innerText(), /Remíza ze syntetické fixture/);
  await left.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await right.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await left.screenshot({ path: path.join(screenshots, 'mobile-result.png'), fullPage: true });
  await right.screenshot({ path: path.join(screenshots, 'desktop-result.png'), fullPage: true });
  for (const page of [left, right]) assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false, 'no horizontal page overflow');
  assert.equal(posts.filter(post => post.path.endsWith('/join')).length, 2); assert.equal(posts.filter(post => post.path.endsWith('/choices')).length, 6);
  // Account history remains private and read-only. Edition publication requires a reviewed owner action.
  const ownedContext = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  let publicationApproved = false; let publicationWrites = 0;
  const ownedWand = { wandId: 'personal-left', physicalSlot: 2, name: 'Měsíční ozvěna', material: 'Fiktivní tis', core: 'Světelná nit', temperament: 'Klidná', lore: 'Syntetický příběh hůlky.', designDescription: 'Fiktivní digitální kresba.', affinity: 'Světlo', weakness: 'Hluk', spells: spells('left') };
  const keepsake = () => ({ schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false, accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false, wandPublicationApproved: publicationApproved, snapshot: { officialName: ownedWand.name, slot: 2, generatedWand: ownedWand }, editions: [{ id, publicationState: 'ready', synthetic: true, story: { title: 'Syntetická edice', text: 'Úplný syntetický příběh ke kontrole před zveřejněním.' } }], portraits: [], assets: [], jobs: [], wizardIdentities: [], prophecies: [] });
  await ownedContext.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin) return route.abort();
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'fixture-owner', displayName: 'Luna', roles: ['Customer'] }, csrfToken: 'fixture-csrf' });
    if (url.pathname === '/app/v1/customer/character') return json({ profile: { profileId: 'fixture-profile', revision: 1, status: 'ready', userInput: { name: 'Luna', world: 'Syntetický svět' } }, generatedWand: ownedWand, artwork: { locator: id, assetId: imageAssetId } });
    if (url.pathname === `/app/v1/archive/owned/${id}/assets/${imageAssetId}`) return route.fulfill({ status: 200, contentType: 'image/png', body: imageBytes });
    if (url.pathname === '/app/v1/customer/sessions') return json({ items: [] });
    if (url.pathname === '/app/v1/customer/completion-updates') return json({ schemaVersion: 1, items: [], nextCursor: null });
    if (url.pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (url.pathname === '/app/v1/customer/duels') return json({ items: [view] });
    if (url.pathname === `/app/v1/customer/duels/${id}`) return json(view);
    if ([`/app/v1/archive/associations/${id}`, `/app/v1/archive/owned/${id}`].includes(url.pathname)) return json(keepsake());
    if (url.pathname === `/app/v1/customer/keepsakes/${id}/wand-publication`) {
      assert.equal(request.method(), 'POST'); const body = request.postDataJSON();
      assert.deepEqual(body, { approved: !publicationApproved, editionId: id, revision: 1 });
      publicationApproved = body.approved; publicationWrites++; return json({ approved: publicationApproved });
    }
    if (/^\/(?:app\/v1|guest\/v1|v1)\//.test(url.pathname)) return json({}, 404);
    return route.continue();
  });
  const owned = await ownedContext.newPage(); owned.on('pageerror', error => errors.push(error.message));
  await owned.goto(`${origin}/me/character`);
  const originalArtwork = owned.locator('.archive-personal-wand-art img');
  await originalArtwork.waitFor();
  await originalArtwork.evaluate(img => new Promise((resolve, reject) => {
    if (img.complete) return img.naturalWidth > 0 ? resolve() : reject(new Error('Generated artwork did not load'));
    img.addEventListener('load', () => resolve(), { once: true });
    img.addEventListener('error', () => reject(new Error('Generated artwork failed to load')), { once: true });
  }));
  assert.match(await originalArtwork.getAttribute('alt'), /Měsíční ozvěna/);
  await owned.screenshot({ path: path.join(screenshots, 'mobile-character-artwork.png'), fullPage: true });
  await owned.goto(`${origin}/me/keepsakes`);
  await owned.getByRole('link', { name: 'Otevřít uložený souboj', exact: true }).click();
  await owned.getByRole('heading', { name: 'Výsledek', exact: true }).waitFor();
  assert.equal(await owned.locator('.archive-duel-actions').count(), 0);
  await owned.goto(`${origin}/keepsake/?account=1&k=${id}`);
  await owned.getByText('Úplný syntetický příběh ke kontrole před zveřejněním.', { exact: true }).last().waitFor();
  assert.equal(publicationWrites, 0, 'reading private fiction does not publish');
  await owned.getByRole('checkbox', { name: 'Zkontroloval jsem hůlku, kouzla, příběh a kartu postavy a schvaluji je pro veřejný přehled večera a souboje.' }).check();
  await owned.getByRole('button', { name: 'Schválit tuto edici', exact: true }).click();
  await owned.getByRole('button', { name: 'Odvolat souhlas se zveřejněním', exact: true }).waitFor();
  await owned.screenshot({ path: path.join(screenshots, 'mobile-owner-approval.png'), fullPage: true });
  await owned.getByRole('button', { name: 'Odvolat souhlas se zveřejněním', exact: true }).click();
  await owned.getByRole('button', { name: 'Schválit tuto edici', exact: true }).waitFor();
  assert.equal(publicationWrites, 2); assert.equal(publicationApproved, false);
  assert.deepEqual(errors, []);
  console.log(`PASS: two isolated browser clients consent, lock hidden choices and complete three named-spell rounds in web-only mode. Screenshots: ${screenshots}`);
} finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }

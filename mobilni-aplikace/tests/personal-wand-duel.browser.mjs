// The compiled visitor flow against a synthetic API; no provider or device calls.
import assert from 'node:assert/strict';
import { readFile, stat, mkdir, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
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
async function capture(page, name, fullPage = true, preserveScroll = false) {
  const originalScroll = await page.evaluate(() => ({ x: scrollX, y: scrollY }));
  await page.evaluate(async preserve => {
    document.activeElement?.blur(); if (!preserve) window.scrollTo({ top: 0, behavior: 'instant' });
    await document.fonts.ready;
    await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame);
  }, preserveScroll);
  await page.screenshot({ path: path.join(screenshots, name), fullPage, animations: 'disabled' });
  if (!preserveScroll) await page.evaluate(scroll => window.scrollTo({ left: scroll.x, top: scroll.y, behavior: 'instant' }), originalScroll);
}
async function decisionFitsViewport(page, side) {
  const viewport = page.viewportSize();
  const controls = [page.getByRole('timer'), ...['strike', 'trick', 'shield'].map(action => page.getByRole('button', { name: buttonName(side, action), exact: true }))];
  for (const control of controls) {
    await control.waitFor(); const box = await control.boundingBox();
    assert(box && box.x >= 0 && box.y >= 0 && box.x + box.width <= viewport.width + 1 && box.y + box.height <= viewport.height + 1,
      `timer and every spell must fit the ${viewport.width}×${viewport.height} timed-choice viewport without scrolling: ${JSON.stringify(box)}`);
    assert.equal(await control.evaluate(element => { const box = element.getBoundingClientRect(); const target = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2); return target === element || element.contains(target); }), true, 'fixed navigation must not cover the timer or spell hit targets');
  }
}
const id = '11111111-1111-4111-8111-111111111111';
const imageAssetId = '22222222-2222-4222-8222-222222222222';
const otherImageAssetId = '33333333-3333-4333-8333-333333333333';
// Authored offline illustrations are test fixtures only. They exercise actual
// image decoding and distinct silhouettes without a paid provider request.
const imageBytes = await readFile(new URL('./fixtures/wand-moon.svg', import.meta.url));
const otherImageBytes = await readFile(new URL('./fixtures/wand-sun.svg', import.meta.url));
assert.notDeepEqual(imageBytes, otherImageBytes);
const artworkPaths = {
  left: `/guest/v1/evenings/${id}/keepsakes/${id}/editions/${id}/assets/${imageAssetId}`,
  right: `/guest/v1/evenings/${id}/keepsakes/${otherImageAssetId}/editions/${otherImageAssetId}/assets/${otherImageAssetId}`,
};
const illustrationHash = bytes => createHash('sha256').update(bytes).digest('hex');
const originalImageHash = illustrationHash(imageBytes);
async function decodedArtwork(image, expectedBytes = imageBytes) {
  await image.waitFor();
  await image.evaluate(async (img, fixtureBytes) => {
    await img.decode();
    if (img.naturalWidth !== 600 || img.naturalHeight !== 800) throw new Error('Wrong or undecodable canonical illustration');
    const pixels = source => { const canvas = document.createElement('canvas'); canvas.width = 600; canvas.height = 800; const context = canvas.getContext('2d'); context.drawImage(source, 0, 0); return context.getImageData(0, 0, 600, 800).data; };
    const expected = new Image(); const fixtureUrl = URL.createObjectURL(new Blob([new Uint8Array(fixtureBytes)], { type: 'image/svg+xml' }));
    try {
      expected.src = fixtureUrl; await expected.decode();
      const actual = pixels(img); const wanted = pixels(expected);
      if (actual.length !== wanted.length || !actual.every((value, index) => value === wanted[index])) throw new Error('Rendered illustration differs from canonical fixture pixels');
    } finally { URL.revokeObjectURL(fixtureUrl); }
  }, [...expectedBytes]);
  return illustrationHash(expectedBytes);
}
async function exportedCard(page, name, leftArtwork = true) {
  await page.getByRole('button', { name: 'Uložit kartu souboje PNG', exact: true }).click();
  const link = page.getByRole('link', { name: 'Stáhnout kartu PNG', exact: true }); await link.waitFor();
  const [download] = await Promise.all([page.waitForEvent('download'), link.click()]);
  const bytes = await readFile(await download.path());
  assert.equal(bytes.subarray(0, 8).toString('hex'), '89504e470d0a1a0a', 'download is a real PNG');
  const decoded = await page.evaluate(async fixture => {
    const bitmap = await createImageBitmap(new Blob([new Uint8Array(fixture)], { type: 'image/png' }));
    const canvas = document.createElement('canvas'); canvas.width = bitmap.width; canvas.height = bitmap.height; const ctx = canvas.getContext('2d'); ctx.drawImage(bitmap, 0, 0); bitmap.close();
    const colors = x => { const pixels = ctx.getImageData(x, 320, 300, 390).data; const values = new Set(); for (let offset = 0; offset < pixels.length; offset += 32) values.add(`${pixels[offset]},${pixels[offset + 1]},${pixels[offset + 2]}`); return values.size; };
    return { width: canvas.width, height: canvas.height, leftColors: colors(115), rightColors: colors(585) };
  }, [...bytes]);
  assert.equal(decoded.width, 1000); assert.equal(decoded.height, 1400);
  assert(decoded.rightColors > 100, 'right illustration has meaningful decoded pixels');
  if (leftArtwork) assert(decoded.leftColors > 100, 'left illustration has meaningful decoded pixels');
  else assert(decoded.leftColors <= 2, 'withdrawn left artwork never appears in the freshly exported card');
  await writeFile(path.join(screenshots, name), bytes);
}
const spells = side => ['strike', 'trick', 'shield'].map((action, index) => ({ action, name: `${side === 'left' ? 'Lunární' : 'Sluneční'} ${['zásah', 'klam', 'štít'][index]}`, description: 'Fiktivní kouzlo pro syntetickou zkoušku.' }));
const tokens = { left: 'a'.repeat(43), right: 'b'.repeat(43) };
const view = {
  id, revision: 1, rulesVersion: 'duel-personalized-v3', state: 'AwaitingParticipants', mode: 'interactive', round: 1, deadline: null,
  requiresParticipantConsent: true, leftJoined: false, rightJoined: false,
  left: { displayName: 'Luna · syntetická ukázka', wandId: 'personal-left', wandName: 'Měsíční ozvěna', strength: 2, cunning: 3, defense: 4, combatArchetype: 'defensive', spells: spells('left'), artworkUrl: artworkPaths.left, artworkSynthetic: true, physicalSlot: 2 },
  right: { displayName: 'Sol · syntetická ukázka', wandId: 'personal-right', wandName: 'Jiskra poledne', strength: 4, cunning: 2, defense: 3, combatArchetype: 'offensive', spells: spells('right'), artworkUrl: artworkPaths.right, artworkSynthetic: true, physicalSlot: 2 },
  narrationMode: 'web_only', narrationStatus: 'web_only_no_spoken_confirmation', rounds: [], result: null, leftChoiceLocked: false, rightChoiceLocked: false,
};
const choices = {}; const posts = []; const errors = [];
// Server-event fixtures use the documented v3 profiles and scoring. Backend
// integration and balance tests validate the rules; this test consumes results.
const rounds = [
  { left: 'strike', right: 'shield', leftScore: 10, rightScore: 24 },
  { left: 'trick', right: 'strike', leftScore: 12, rightScore: 26 },
  { left: 'shield', right: 'strike', leftScore: 26, rightScore: 14 },
];
const actionLabels = { strike: 'Úder', trick: 'Lest', shield: 'Štít' };
function buttonName(side, action) { return `${spells(side).find(spell => spell.action === action).name} ${actionLabels[action]}`; }
const assetReads = [];
let publicAvailable = true;
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
      if (Object.values(artworkPaths).includes(url.pathname)) {
        assetReads.push(url.pathname);
        return publicAvailable || url.pathname === artworkPaths.right ? route.fulfill({ status: 200, contentType: 'image/svg+xml', body: url.pathname === artworkPaths.left ? imageBytes : otherImageBytes }) : json({}, 404);
      }
      if (url.pathname.startsWith(`/guest/v1/duels/${id}`)) {
        if (!publicAvailable) return json({}, 404);
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
              const event = rounds[view.round - 1];
              assert.equal(choices.left, event.left); assert.equal(choices.right, event.right);
              view.rounds.push({ number: view.round, leftAction: choices.left, rightAction: choices.right, leftScore: event.leftScore, rightScore: event.rightScore, leftAutomatic: false, rightAutomatic: false, quip: `Syntetické kolo ${view.round}: ${spells('left').find(spell => spell.action === choices.left).name} se střetlo s kouzlem ${spells('right').find(spell => spell.action === choices.right).name}.`, mishap: 'Fiktivní komentář bez vlivu na výsledek.' });
              delete choices.left; delete choices.right; view.leftChoiceLocked = false; view.rightChoiceLocked = false;
              if (view.round === 3) { view.state = 'Completed'; view.deadline = null; view.result = { winner: 'right', ending: 'Sol vítězí v syntetickém souboji. Luna zachraňuje čest posledním štítem.', leftPoints: 1, rightPoints: 2, exhibition: false }; }
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
    const art = page.locator('.archive-duelist-art img');
    assert.equal(await decodedArtwork(art.nth(0)), originalImageHash);
    assert.equal(await decodedArtwork(art.nth(1), otherImageBytes), illustrationHash(otherImageBytes));
    return page;
  }
  const left = await client('left', { width: 390, height: 844 });
  const right = await client('right', { width: 1440, height: 900 });
  assert.equal(posts.length, 0, 'opening a participant URL does not silently consent');
  await left.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await right.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await capture(left, 'mobile-versus.png');
  await capture(right, 'desktop-versus.png');
  for (const page of [left, right]) {
    await page.getByRole('checkbox', { name: 'Souhlasím s účastí v tomto souboji.' }).check();
    await page.getByRole('button', { name: 'Potvrdit účast', exact: true }).click();
  }
  await left.getByRole('timer').waitFor();
  await decisionFitsViewport(left, 'left'); await decisionFitsViewport(right, 'right');
  await capture(left, 'mobile-choice-viewport.png', false, true);
  await capture(right, 'desktop-choice-viewport.png', false, true);
  await capture(left, 'mobile-choice.png');
  await capture(right, 'desktop-choice.png');
  for (let round = 1; round <= 3; round++) {
    await decisionFitsViewport(left, 'left'); await decisionFitsViewport(right, 'right');
    const event = rounds[round - 1];
    const leftAction = left.getByRole('button', { name: buttonName('left', event.left), exact: true });
    const rightAction = right.getByRole('button', { name: buttonName('right', event.right), exact: true });
    await leftAction.click();
    await left.getByText('Tah přijat a uzamčen. Protivníkův tah zůstává skrytý.', { exact: true }).waitFor();
    assert.equal(await leftAction.isDisabled(), true);
    // No premature reveal appears while only one phone has committed.
    assert.equal(await left.locator('.archive-duel-reveal').count(), round - 1);
    await right.getByText('Soupeř je připraven', { exact: true }).waitFor();
    await rightAction.click();
    const heading = `Kolo ${round} · ${event.leftScore} : ${event.rightScore}`;
    await left.getByRole('heading', { name: heading, exact: true }).waitFor();
    await right.getByRole('heading', { name: heading, exact: true }).waitFor();
    const revealed = left.locator('.archive-duel-reveal').filter({ has: left.getByRole('heading', { name: heading, exact: true }) });
    await revealed.getByText(spells('left').find(spell => spell.action === event.left).name, { exact: true }).waitFor();
    await revealed.getByText(spells('right').find(spell => spell.action === event.right).name, { exact: true }).waitFor();
    assert.equal(await revealed.locator('.archive-revealed-spell[data-spell]').count(), 2, 'both spell effects reveal together');
    assert.equal(await revealed.evaluate(element => getComputedStyle(element).animationName), 'none', 'reduced-motion users receive an immediate readable reveal');
  }
  await left.getByRole('heading', { name: 'Výsledek', exact: true }).waitFor();
  assert.match(await left.locator('body').innerText(), /Sol vítězí v syntetickém souboji/);
  assert.equal(await left.locator('.archive-duel-result').getAttribute('data-result'), 'defeat');
  assert.equal(await right.locator('.archive-duel-result').getAttribute('data-result'), 'victory');
  await left.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await right.evaluate(() => { document.activeElement?.blur(); window.scrollTo(0, 0); });
  await capture(left, 'mobile-result.png');
  await capture(right, 'desktop-result.png');
  await left.getByRole('button', { name: 'Připravit shrnutí ke stažení', exact: true }).click();
  const [summaryDownload] = await Promise.all([left.waitForEvent('download'), left.getByRole('link', { name: 'Stáhnout shrnutí', exact: true }).click()]);
  const summary = await readFile(await summaryDownload.path(), 'utf8');
  assert.match(summary, /Měsíční ozvěna/); assert.match(summary, /Jiskra poledne/);
  assert.match(summary, /Lunární štít/); assert.match(summary, /Sluneční zásah/);
  for (const privateValue of [id, imageAssetId, otherImageAssetId, tokens.left, tokens.right, '/guest/', '/app/', 'fixture-owner']) assert(!summary.includes(privateValue), 'summary omits identifiers, access routes and grants');
  await exportedCard(left, 'battle-summary.png');
  for (const page of [left, right]) assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth), false, 'no horizontal page overflow');
  assert.equal(posts.filter(post => post.path.endsWith('/join')).length, 2); assert.equal(posts.filter(post => post.path.endsWith('/choices')).length, 6);
  // Account history remains private and read-only. Edition publication requires a reviewed owner action.
  const ownedContext = await browser.newContext({ viewport: { width: 390, height: 844 }, reducedMotion: 'reduce' });
  let publicationApproved = false; let publicationWrites = 0;
  let artworkState = 'ready'; let retries = 0;
  const ownedWand = { wandId: 'personal-left', physicalSlot: 2, name: 'Měsíční ozvěna', material: 'Fiktivní tis se stříbrnými žilkami', core: 'Světelná nit', temperament: 'Klidná, ale pomstu si zapisuje.', lore: 'Syntetický příběh hůlky: pamatuje si každý dluh měsíčnímu svitu.', designDescription: 'Zakřivený tis, stříbrné objímky a srpek v hrotu.', affinity: 'Měsíční světlo', weakness: 'Polední žár', combatArchetype: 'defensive', spells: spells('left') };
  const artwork = () => ({ locator: id, assetId: artworkState === 'ready' ? imageAssetId : null, status: artworkState, canRetry: artworkState === 'failed', synthetic: true, version: 1 });
  const historyView = () => publicAvailable ? view : { ...view, left: { ...view.left, artworkUrl: null } };
  const readiness = () => ({ state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: artworkState === 'ready' });
  const keepsake = () => ({ schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false, accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false, artwork: artwork(), readiness: readiness(), wandPublicationApproved: publicationApproved, snapshot: { officialName: ownedWand.name, slot: 2, generatedWand: ownedWand }, editions: [{ id, publicationState: 'ready', artworkAssetId: artworkState === 'ready' ? imageAssetId : null, synthetic: true, story: { title: 'Syntetická edice', text: 'Úplný syntetický příběh ke kontrole před zveřejněním.' } }], portraits: [], assets: [{ id: imageAssetId, kind: 'wand_image', contentType: 'image/svg+xml' }], jobs: [], wizardIdentities: [], prophecies: [] });
  await ownedContext.route('**/*', async route => {
    const request = route.request(); const url = new URL(request.url());
    if (url.origin !== origin) return route.abort();
    const json = (value, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(value) });
    if (url.pathname === '/app/v1/session') return json({ enabled: true, account: { id: 'fixture-owner', displayName: 'Luna', roles: ['Customer'] }, csrfToken: 'fixture-csrf' });
    if (url.pathname === '/app/v1/customer/character') return json({ profile: { profileId: 'fixture-profile', revision: 1, status: 'ready', userInput: { name: 'Luna', world: 'Syntetický svět' } }, generatedWand: ownedWand, artwork: artwork() });
    if (url.pathname === `/app/v1/archive/owned/${id}/assets/${imageAssetId}`) { assetReads.push(url.pathname); return route.fulfill({ status: 200, contentType: 'image/svg+xml', body: imageBytes }); }
    if (Object.values(artworkPaths).includes(url.pathname)) return publicAvailable || url.pathname === artworkPaths.right ? route.fulfill({ status: 200, contentType: 'image/svg+xml', body: url.pathname === artworkPaths.left ? imageBytes : otherImageBytes }) : json({}, 404);
    if (url.pathname === '/app/v1/customer/sessions') return json({ items: [{ id: 'fixture-session', state: 'completed', createdAt: 1792836000, characterName: 'Luna' }] });
    if (url.pathname === '/app/v1/customer/sessions/fixture-session/result') return json({ schemaVersion: 1, sessionId: 'fixture-session', resultLocator: id, readiness: readiness(), accessKind: 'originalOwner', canReadPrivate: true, canManage: true, canUnlink: false, generatedWand: ownedWand, artwork: artwork(), artworkAssetId: imageAssetId });
    if (url.pathname === `/app/v1/customer/keepsakes/${id}/wand-artwork/retry`) {
      assert.equal(request.method(), 'POST'); assert.match(request.headers()['idempotency-key'], /^[a-f0-9-]{36}$/i);
      assert.deepEqual(request.postDataJSON(), {}); assert.equal(artworkState, 'failed');
      retries++; artworkState = 'ready'; return json(artwork());
    }
    if (url.pathname === '/app/v1/customer/completion-updates') return json({ schemaVersion: 1, items: [], nextCursor: null });
    if (url.pathname === '/app/v1/archive/associations') return json({ items: [] });
    if (url.pathname === '/app/v1/customer/duels') return json({ items: [historyView()] });
    if (url.pathname === `/app/v1/customer/duels/${id}`) return json(historyView());
    if ([`/app/v1/archive/associations/${id}`, `/app/v1/archive/owned/${id}`].includes(url.pathname)) return json(keepsake());
    if (url.pathname === `/app/v1/customer/keepsakes/${id}/wand-publication`) {
      assert.equal(request.method(), 'POST'); const body = request.postDataJSON();
      assert.deepEqual(body, { approved: !publicationApproved, editionId: id, revision: 1 });
      publicationApproved = body.approved; publicationWrites++; if (!publicationApproved) publicAvailable = false; return json({ approved: publicationApproved });
    }
    if (/^\/(?:app\/v1|guest\/v1|v1)\//.test(url.pathname)) return json({}, 404);
    return route.continue();
  });
  const owned = await ownedContext.newPage(); owned.on('pageerror', error => errors.push(error.message));
  await owned.goto(`${origin}/me/character`);
  const originalArtwork = owned.locator('.archive-personal-wand-art img');
  await originalArtwork.waitFor();
  assert.equal(await decodedArtwork(originalArtwork), originalImageHash);
  assert.match(await originalArtwork.getAttribute('alt'), /Měsíční ozvěna/);
  await capture(owned, 'mobile-character-artwork.png');
  await owned.setViewportSize({ width: 1440, height: 1000 });
  await capture(owned, 'desktop-character-artwork.png');
  await owned.setViewportSize({ width: 390, height: 844 });
  // A failed image never hides the selected textual identity or automatically
  // sends a second generation request. Recovery is an explicit owner action.
  artworkState = 'failed';
  await owned.goto(`${origin}/me/character`);
  await owned.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).waitFor();
  assert.equal(retries, 0);
  await owned.getByRole('heading', { name: ownedWand.name, exact: true }).waitFor();
  assert.equal(await owned.locator('.archive-personal-wand-art img').count(), 0);
  await capture(owned, 'mobile-artwork-failure.png');
  await owned.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).click();
  assert.equal(await decodedArtwork(owned.locator('.archive-personal-wand-art img')), originalImageHash, 'explicit retry returns canonical asset');
  assert.equal(retries, 1);
  await owned.reload();
  assert.equal(await decodedArtwork(owned.locator('.archive-personal-wand-art img')), originalImageHash);
  assert.equal(await owned.getByRole('button', { name: 'Zkusit ilustraci znovu', exact: true }).count(), 0, 'published artwork has no regeneration action');
  assert.equal(retries, 1, 'GET/reload cannot start a second paid job');
  await owned.goto(`${origin}/me/conversation`);
  assert.equal(await decodedArtwork(owned.locator('.archive-personal-wand-art img')), originalImageHash, 'conversation result reuses canonical bytes');
  await capture(owned, 'mobile-session-result-artwork.png');
  await owned.goto(`${origin}/me/keepsakes`);
  await owned.getByRole('link', { name: 'Otevřít uložený souboj', exact: true }).click();
  await owned.getByRole('heading', { name: 'Výsledek', exact: true }).waitFor();
  assert.equal(await owned.locator('.archive-duel-actions').count(), 0);
  assert.equal(await decodedArtwork(owned.locator('.archive-duelist-art img').nth(0)), originalImageHash, 'history reuses canonical artwork');
  const history = await ownedContext.newPage(); history.on('pageerror', error => errors.push(error.message));
  await history.goto(`${origin}/archive/duel?account=1&duel=${id}`);
  assert.equal(await decodedArtwork(history.locator('.archive-duelist-art img').nth(0)), originalImageHash);
  await exportedCard(history, 'history-battle-summary.png');
  await owned.goto(`${origin}/keepsake/?account=1&k=${id}`);
  await owned.getByText('Úplný syntetický příběh ke kontrole před zveřejněním.', { exact: true }).last().waitFor();
  assert.equal(await decodedArtwork(owned.locator('.archive-art-button img')), originalImageHash, 'keepsake reuses canonical artwork');
  await capture(owned, 'mobile-keepsake-artwork.png');
  assert.equal(publicationWrites, 0, 'reading private fiction does not publish');
  await owned.getByRole('checkbox', { name: 'Zkontroloval jsem hůlku, kouzla, příběh a kartu postavy a schvaluji je pro veřejný přehled večera a souboje.' }).check();
  await owned.getByRole('button', { name: 'Schválit tuto edici', exact: true }).click();
  await owned.getByRole('button', { name: 'Odvolat souhlas se zveřejněním', exact: true }).waitFor();
  await capture(owned, 'mobile-owner-approval.png');
  await owned.getByRole('button', { name: 'Odvolat souhlas se zveřejněním', exact: true }).click();
  await owned.getByRole('button', { name: 'Schválit tuto edici', exact: true }).waitFor();
  assert.equal(publicationWrites, 2); assert.equal(publicationApproved, false);
  await history.getByRole('button', { name: 'Obnovit souboj', exact: true }).click();
  await history.waitForFunction(() => document.querySelectorAll('.archive-duelist-art img').length === 1);
  assert.equal(await history.getByRole('link', { name: 'Stáhnout kartu PNG', exact: true }).count(), 0, 'cached derivative download is revoked when the source illustration is withdrawn');
  assert.equal(await decodedArtwork(history.locator('.archive-duelist-art img'), otherImageBytes), illustrationHash(otherImageBytes), 'only the still-approved opponent illustration remains');
  assert.equal(await history.getByRole('heading', { name: 'Výsledek', exact: true }).count(), 1, 'saved combat events remain while revoked public art is cleared');
  await exportedCard(history, 'battle-summary-withdrawn.png', false);
  await left.getByRole('button', { name: 'Obnovit souboj', exact: true }).click();
  await left.getByText('Souboj není veřejně dostupný. Obsluha může ověřit zveřejnění obou identit.', { exact: true }).waitFor();
  assert.equal(await left.locator('.archive-duelist-art img').count(), 0, 'withdrawn source removes derivative public battle illustrations');
  // Withdrawal affects public projections, while the original owner's saved
  // image remains privately available and unchanged.
  await owned.goto(`${origin}/me/character`);
  assert.equal(await decodedArtwork(owned.locator('.archive-personal-wand-art img')), originalImageHash);
  assert(assetReads.includes(artworkPaths.left) && assetReads.includes(artworkPaths.right), 'both public wand illustrations actually requested');
  assert.deepEqual(errors, []);
  console.log(`PASS: two isolated browser clients consent, lock hidden choices, reveal three named-spell rounds, render distinct decoded illustrations, reuse canonical bytes across profile/result/keepsake/history, explicitly retry failure and clear withdrawn public art. Synthetic API only. Screenshots: ${screenshots}`);
} finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }

/** Compiled Vue journey with a synthetic native bridge. No providers or hardware. */
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { preview } from 'vite';
import { mkdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const server = await preview({ root: fileURLToPath(new URL('..', import.meta.url)), preview: { host: '127.0.0.1', port: 0 } });
const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
const screenshots = path.join(tmpdir(), 'dildovander-debug-vue-browser'); await mkdir(screenshots, { recursive: true });
let browser;
try {
  try { browser = await chromium.launch(); } catch (error) { if (process.platform !== 'win32') throw error; browser = await chromium.launch({ channel: 'chrome' }); }
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, reducedMotion: 'reduce' });
  await context.addInitScript(() => {
    const engines = { schemaVersion: 1, primaryEngine: 'gpt-live', engines: ['gpt-live', 'realtime'].map(engine => ({ engine,
      model: engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1', ready: true, voices: engine === 'gpt-live' ? ['cedar'] : ['marin', 'coral'],
      speed: { supported: engine === 'realtime', min: engine === 'realtime' ? .8 : 1, max: engine === 'realtime' ? 1.2 : 1, default: 1 }, inputModes: ['audio'], cameraSources: ['phone'] })) };
    const modes = { revision: 'synthetic-revision', engines, activeConversations: 0, lifetime: 'until_restart', personalityId: 'ollivander-trunk',
      personalities: [{ id: 'ollivander-trunk', name: 'DildoVander' }], characterEnabled: true, characterTone: 'Adult', characterFinaleProfile: 'Blocked', characterVersion: 'dv01-v1' };
    const account = { id: 'synthetic-owner', displayName: 'Synthetic owner', roles: ['Owner'] };
    window.__debugCalls = [];
    const state = { configured: true, running: false, session: false, phase: 'idle', mode: 'openai', accessMode: 'account', deviceId: 'synthetic-kufr', engines, server: modes };
    window.webkit = { messageHandlers: { bridge: {} } };
    const headers = {
      KufrDebug: ['status', 'stop', 'configure', 'forgetProfile', 'useAccount', 'inspectServer', 'inspectBackend', 'changeServerModes', 'startConversation',
        'captureCostumePhoto', 'attachCostumePhoto', 'discardCostumePhoto', 'inspectMemory', 'deleteConversationMemory', 'speakSystem', 'readServerLogs'],
      KufrDeviceTransport: ['savedDevice'], KufrIdentity: ['request', 'cancelAccountRequest'], KufrProvisioning: [],
    };
    window.Capacitor = { PluginHeaders: Object.entries(headers).map(([name, methods]) => ({ name, methods: methods.map(name => ({ name, rtype: 'promise' })) })),
      nativePromise: async (plugin, method, options = {}) => {
        window.__debugCalls.push({ plugin, method, options: structuredClone(options) });
        if (plugin === 'KufrDeviceTransport') return { paired: true, deviceId: 'synthetic-kufr', host: '192.0.2.1' };
        if (plugin === 'KufrIdentity') {
          if (method === 'cancelAccountRequest') return { cancelled: true };
          if (method !== 'request') throw new Error('Unexpected synthetic identity method');
          let value;
          if (options.path === '/app/v1/session') value = { enabled: true, account, csrfToken: 'synthetic-csrf', origin: 'https://application.example.invalid' };
          else if (options.path === '/app/v1/account') value = { grants: [], associations: [], sessions: [], passkeys: [] };
          else if (options.path === '/app/v1/admin/operations/status') value = { modes, readiness: { accounts: true, keepsakes: false, keepsakeGeneration: false, nativeAssociationConfigured: true }, archive: null };
          else if (options.path === '/app/v1/admin/operations/modes') value = modes;
          else return { status: 503, body: '{"code":"synthetic_unconfigured"}' };
          return { status: 200, body: JSON.stringify(value) };
        }
        if (method === 'status') return structuredClone(state);
        if (method === 'startConversation') { Object.assign(state, { session: true, running: true, phase: 'listening', effectiveEngine: options.engine, effectiveModel: options.engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1', conversationId: 'synthetic-conversation' }); return; }
        if (method === 'stop') { Object.assign(state, { session: false, running: false, phase: 'cancelled' }); return; }
        if (method === 'captureCostumePhoto') return { draftId: 'synthetic-photo' };
        if (method === 'attachCostumePhoto') return { status: 'attached', imageId: 'synthetic-image', revision: 1 };
        if (method === 'inspectMemory') return { identity: { guestId: 'Guest-042', identityVerified: false, longTermAllowed: false, cloudExportAllowed: false, version: 3 }, epoch: 7, deletionEpoch: 2,
          records: [{ memoryId: 'fact-1', text: 'Synthetic private memory', sourceReference: 'synthetic-event', revision: 1 }], totalRecords: 1, hasMore: false,
          memoryStatus: { configured: false, configurationStatus: 'synthetic', degraded: false, pending: 0, failed: 0, deleting: 0 } };
        if (method === 'deleteConversationMemory') return { localDeletion: 'complete', remoteDeletion: 'pending' };
        if (method === 'readServerLogs') return { storeId: 'synthetic', nextCursor: 0, hasMore: false, truncated: false, droppedWrites: 0, entries: [] };
      } };
  });
  const page = await context.newPage(); page.setDefaultTimeout(10000); const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('dialog', dialog => dialog.accept());
  await page.route('**/*', route => route.request().url().startsWith(origin) ? route.continue() : route.abort());
  await page.goto(origin + '/suitcase/diagnostics'); await page.getByRole('heading', { name: 'Test hlasu na telefonu', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Spustit AI rozhovor', exact: true }).waitFor({ state: 'visible' });
  assert.deepEqual(await page.locator('[data-engine] option').evaluateAll(items => items.map(item => item.value)), ['', 'gpt-live', 'realtime']);
  await page.locator('[data-engine]').selectOption('realtime'); await page.locator('[data-speed]').fill('1.1');
  await page.getByRole('button', { name: 'Spustit AI rozhovor', exact: true }).click();
  await page.getByText('Aktivní engine realtime', { exact: false }).waitFor();
  const start = (await page.evaluate(() => window.__debugCalls)).find(call => call.method === 'startConversation');
  assert.equal(start.options.engine, 'realtime'); assert.equal(start.options.voice, 'marin'); assert.equal(start.options.speed, 1.1);
  await page.getByRole('button', { name: 'Ukončit testovací rozhovor', exact: true }).click();
  await page.locator('[data-memory-conversation]').fill('synthetic-conversation'); await page.getByRole('button', { name: 'Načíst paměť rozhovoru', exact: true }).click();
  await page.getByText('Synthetic private memory', { exact: true }).waitFor();
  assert.match(await page.locator('.memory-panel').textContent(), /Neověřená nebo odvolaná vazba/);
  assert(await page.getByRole('button', { name: 'Odstranit paměť tohoto rozhovoru', exact: true }).isDisabled());
  await page.locator('[data-memory-confirm]').check(); await page.getByRole('button', { name: 'Odstranit paměť tohoto rozhovoru', exact: true }).click();
  await page.getByText('Místní paměť odstraněna. Vzdálený úklid čeká na dokončení.', { exact: true }).waitFor();
  for (const width of [1440, 390]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.keyboard.press('Escape');
    await page.evaluate(() => { document.activeElement?.blur(); window.getSelection()?.removeAllRanges(); window.scrollTo(0, 0); });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1), 'diagnostics fits viewport');
    await page.screenshot({ path: path.join(screenshots, `diagnostics-${width}.png`), fullPage: true });
    for (const [selector, name] of [['section[aria-labelledby="phone-heading"]', 'phone'], ['.memory-panel', 'memory']]) {
      await page.locator(selector).evaluate(element => {
        const header = document.querySelector('.application-header').getBoundingClientRect().height;
        window.scrollTo(0, element.getBoundingClientRect().top + window.scrollY - header - 16);
      });
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      // Viewport context preserves fixed shell positions. Element clipping of a
      // taller-than-viewport card composites sticky headers across its middle.
      await page.screenshot({ path: path.join(screenshots, `${name}-${width}.png`) });
    }
  }
  await page.getByRole('link', { name: 'Otevřít správu aplikace', exact: true }).click();
  await page.getByRole('heading', { name: 'Provoz serveru', exact: true }).waitFor();
  assert.equal(await page.locator('#operations-dialog,#operations-voice').count(), 0);
  await page.locator('#operations-finale').selectOption('Limited'); await page.getByRole('button', { name: 'Potvrdit změnu osobnosti a postavy', exact: true }).click();
  const calls = await page.evaluate(() => window.__debugCalls);
  const identityRequests = calls.filter(call => call.plugin === 'KufrIdentity' && call.method === 'request');
  assert(identityRequests.length > 0 && identityRequests.every(call => /^[0-9a-f-]{36}$/i.test(call.options.requestId)), 'native account calls carry cancellation request ids');
  assert(calls.filter(call => call.plugin === 'KufrIdentity' && call.method === 'cancelAccountRequest')
    .every(call => identityRequests.some(request => request.options.requestId === call.options.requestId)), 'native cancellation names only a dispatched request');
  const mutation = calls.find(call => call.plugin === 'KufrIdentity' && call.options.path === '/app/v1/admin/operations/modes');
  const body = JSON.parse(mutation.options.body); assert.equal(body.characterFinaleProfile, 'Limited');
  assert(!('dialogMode' in body)); assert(!('voiceMode' in body));
  assert(!calls.some(call => ['runOpeningSpeech', 'answerConversation', 'startRecording', 'finishRecording'].includes(call.method)));
  assert.deepEqual(errors, []);
  console.log(`PASS compiled Vue/native-stub engines, tempo/voices, Stop, scoped memory confirmation, truthful revoked consent, supported admin mutations and 1440/390 layout. Synthetic only. Screenshots: ${screenshots}`);
  await context.close();
} finally { await browser?.close(); await new Promise(resolve => server.httpServer.close(resolve)); }

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const clientSource = await readFile(new URL('../src/features/provisioning/ProvisioningClient.ts', import.meta.url), 'utf8');
const clientUrl = `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(clientSource, { mode: 'transform' })).toString('base64')}`;
const panelSource = await readFile(new URL('../src/features/provisioning/ProvisioningPanel.ts', import.meta.url), 'utf8');
const panelCode = stripTypeScriptTypes(panelSource, { mode: 'transform' })
  .replace(/'\.\.\/\.\.\/\.\.\/\.\.\/design-system\/ui\.js'/, JSON.stringify(new URL('../../design-system/ui.js', import.meta.url).href))
  .replace(/import \{ registerPlugin \} from '@capacitor\/core';/, 'const registerPlugin = () => ({});')
  .replace(/'\.\/ProvisioningClient'/, JSON.stringify(clientUrl));
const { ProvisioningPanel, bridge } = await import(`data:text/javascript;base64,${Buffer.from(panelCode + '\nexport { bridge };').toString('base64')}`);
const { ProvisioningClient } = await import(clientUrl);

test('a queued network action retains the profile selected at the tap', async () => {
  for (const action of ['configure', 'test', 'delete']) {
    let release;
    const slot = new Promise(resolve => { release = resolve; });
    let submitted;
    bridge.configure = bridge.command = async options => { submitted = options; };
    const profile = { value: 'hotspot' };
    // Exercise the panel action boundary without a native dialog or a browser DOM.
    const panel = Object.create(ProvisioningPanel.prototype);
    panel.nativeMode = true;
    panel.busy = false;
    panel.element = { querySelector: () => profile };
    panel.client = { perform: async operation => { await slot; await operation(); } };
    const pending = panel.action(action);
    profile.value = 'fallback';
    release();
    await pending;
    assert.equal(submitted.profile, 'hotspot', action);
  }
});

function nestedConnectionView(context) {
  const previousDocument = globalThis.document;
  const node = () => ({ children: [], dataset: {}, append(child) { this.children.push(child); },
    replaceChildren() { this.children = []; } });
  globalThis.document = { createElement: node };
  context.after(() => { globalThis.document = previousDocument; });
  const lanStatus = { textContent: 'LAN je připojena; uložený kufr je ověřený.' };
  const messages = [];
  let bleText = '';
  const bleStatus = { get textContent() { return bleText; },
    set textContent(value) { bleText = value; messages.push(value); } };
  // main.ts nests LAN settings before BLE. A broad role selector therefore
  // resolves to LAN, while the provisioning-owned hook resolves only to BLE.
  const statusesInDocumentOrder = [lanStatus, bleStatus];
  const elements = new Map([
    ['[role="status"]', statusesInDocumentOrder[0]],
    ['[data-provisioning-status]', bleStatus],
    ['.ble-devices', node()], ['.ble-metrics', { hidden: true }],
    ['[data-profile]', { value: 'hotspot', disabled: false }],
  ]);
  const panel = Object.create(ProvisioningPanel.prototype);
  Object.assign(panel, { nativeMode: true, busy: false, connected: false,
    element: { querySelector(selector) {
      if (!elements.has(selector)) elements.set(selector, { textContent: '' });
      return elements.get(selector);
    }, querySelectorAll() { return []; } } });
  panel.client = new ProvisioningClient(bridge, (...args) => panel.update(...args));
  panel.onPaired = async () => {};
  bridge.disconnect = async () => {};
  return { panel, lanStatus, bleStatus, messages, elements };
}

const connectedStatus = {
  v: 1, ok: true, deviceId: 'kufr-test', firmwareVersion: 'synthetic-test',
  network: { phase: 'connected', result: 'ok', activeProfile: 'hotspot', lastSuccessfulProfile: 'hotspot',
    hotspot: { configured: true, ssid: 'Test hotspot' }, fallback: { configured: false, ssid: '' } },
  backendConnected: false, ota: { state: 'idle' },
};

test('BLE discovery and retry feedback stays in BLE status when LAN status appears first', async context => {
  const view = nestedConnectionView(context);
  const lanText = view.lanStatus.textContent;
  bridge.discover = async () => ({ devices: [{ name: 'Test kufr' }] });
  await view.panel.action('discover');
  assert.match(view.messages.join('\n'), /Hledám BLE kufry/);
  assert.match(view.bleStatus.textContent, /Vyberte svůj kufr/);
  assert.equal(view.elements.get('.ble-devices').children.length, 1);
  assert.equal(view.lanStatus.textContent, lanText);

  bridge.discover = async () => ({ devices: [] });
  await view.panel.action('discover');
  assert.match(view.bleStatus.textContent, /Žádný BLE kufr/);
  assert.equal(view.lanStatus.textContent, lanText);
});

test('BLE pairing and network acceptance or refusal never overwrite LAN connection feedback', async context => {
  const view = nestedConnectionView(context);
  const lanText = view.lanStatus.textContent;
  bridge.connect = async () => connectedStatus;
  bridge.pairLocal = async () => ({ deviceId: 'kufr-test', host: '192.0.2.1' });
  let refreshedPairing = 0;
  view.panel.onPaired = async () => { refreshedPairing++; };
  await view.panel.action('connect', 'Test kufr');
  assert.match(view.bleStatus.textContent, /Zabezpečené BLE spojení/);
  await view.panel.action('pair-local');
  assert.equal(refreshedPairing, 1);
  assert.match(view.messages.join('\n'), /Páruji kufr/);
  assert.match(view.bleStatus.textContent, /uložený v Keychain/);

  bridge.configure = async () => connectedStatus;
  await view.panel.action('configure');
  assert.match(view.bleStatus.textContent, /Požadavek přijat/);
  bridge.command = async () => ({ ...connectedStatus, ok: false, error: 'wifi_failed' });
  await view.panel.action('test');
  assert.match(view.bleStatus.textContent, /Kufr odmítl požadavek: wifi_failed/);
  assert.equal(view.lanStatus.textContent, lanText);
});

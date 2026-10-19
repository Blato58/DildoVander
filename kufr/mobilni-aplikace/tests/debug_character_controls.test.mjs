import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

async function moduleUrl(name) {
  const source = await readFile(new URL(`../src/features/debug/${name}.ts`, import.meta.url), 'utf8');
  return `data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`;
}

const source = await readFile(new URL('../src/features/debug/DebugPanel.ts', import.meta.url), 'utf8');
const code = stripTypeScriptTypes(source, { mode: 'transform' })
  .replace(/'\.\.\/\.\.\/\.\.\/\.\.\/design-system\/ui\.js'/, JSON.stringify(new URL('../../design-system/ui.js', import.meta.url).href))
  .replace(/import \{ nativeDebugBridge \} from '\.\/NativeDebugBridge';/, 'const nativeDebugBridge = () => ({});')
  .replace(/'\.\/DebugJournal'/, JSON.stringify(await moduleUrl('DebugJournal')))
  .replace(/'\.\/ServerLogs'/, JSON.stringify(await moduleUrl('ServerLogs')));
const { DebugPanel, bridge } = await import(`data:text/javascript;base64,${Buffer.from(code + '\nexport { bridge };').toString('base64')}`);

function element() {
  return {
    disabled: false, value: '', checked: false, textContent: '', options: [{}, {}],
    replaceChildren() { this.options = []; },
    append(option) { this.options.push(option); },
  };
}

function setup(context) {
  const originalDocument = globalThis.document;
  globalThis.document = { hidden: false, createElement: () => element() };
  context.after(() => { globalThis.document = originalDocument; });
  const elements = new Map();
  const find = selector => {
    if (!elements.has(selector)) { elements.set(selector, element()); }
    return elements.get(selector);
  };
  const controls = ['enabled', 'tone', 'finale'].map(name => find(`[data-character="${name}"]`));
  // Exercise the actual rendering and status-refresh boundary. Unrelated views
  // use doubles, as existing panel tests do; no native app or browser is claimed.
  const panel = Object.create(DebugPanel.prototype);
  Object.assign(panel, {
    native: true, pending: false, statusSequence: 0,
    voice: { configured: false, running: false, phase: 'idle', mode: 'unknown' },
    element: { querySelector: find, querySelectorAll: () => controls },
    renderServerLogs() {}, renderEvenings() {}, renderConversation() {},
  });
  const server = {
    revision: 'synthetic-revision', dialogMode: 'fake', voiceMode: 'fake',
    dialogAvailable: true, voiceAvailable: true, activeConversations: 0,
    characterEnabled: false, characterTone: 'Adult', characterFinaleProfile: 'Blocked',
  };
  const ready = { configured: true, running: false, session: false, phase: 'idle', mode: 'fake', server };
  return { panel, controls, server, ready, button: name => find(`[data-debug="${name}"]`) };
}

function assertCharacterButtons(view, disabled) {
  for (const name of ['character-apply', 'character-reset']) {
    assert.equal(view.button(name).disabled, disabled, name);
  }
}

test('DV01 actions enable after initial unknown server state and recover after a busy refresh', async context => {
  const view = setup(context);
  view.panel.renderVoice(); // Constructor's initial state has no server snapshot.
  assertCharacterButtons(view, true);
  bridge.status = async () => view.ready;
  await view.panel.refreshVoiceAsync();
  assertCharacterButtons(view, false);
  assert.ok(view.controls.every(control => !control.disabled));

  view.panel.pending = true;
  view.panel.renderVoice();
  assertCharacterButtons(view, true);
  view.panel.pending = false;
  await view.panel.refreshVoiceAsync(); // Same server revision must still restore both actions.
  assertCharacterButtons(view, false);
});

test('DV01 actions preserve native, administrator, busy and local session gates on each render', context => {
  const view = setup(context);
  for (const restrict of [
    panel => { panel.native = false; },
    panel => { panel.voice.configured = false; },
    panel => { panel.pending = true; },
    panel => { panel.voice.running = true; },
    panel => { panel.voice.session = true; },
    panel => { panel.voice.server = null; },
    panel => { panel.voice.server = { ...view.server, characterEnabled: undefined }; },
  ]) {
    Object.assign(view.panel, { native: true, pending: false, voice: { ...view.ready } });
    restrict(view.panel);
    view.panel.renderVoice();
    assertCharacterButtons(view, true);
    assert.ok(view.controls.every(control => control.disabled));

    Object.assign(view.panel, { native: true, pending: false, voice: { ...view.ready } });
    view.panel.renderVoice();
    assertCharacterButtons(view, false);
  }
  // Next-session character policy remains separate from changing the server's
  // dialog/voice mode while another conversation uses its frozen snapshot.
  view.panel.voice.server = { ...view.server, activeConversations: 1 };
  view.panel.renderVoice();
  assertCharacterButtons(view, false);
  assert.equal(view.button('server-apply').disabled, true);
});

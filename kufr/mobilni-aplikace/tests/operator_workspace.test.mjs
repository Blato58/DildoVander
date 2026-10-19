import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = await readFile(new URL('../src/main.ts', import.meta.url), 'utf8');
const start = source.indexOf('function showWorkspace(');
const end = source.indexOf('const debugPanel', start);
assert.ok(start >= 0 && end > start);
const compiled = stripTypeScriptTypes(source.slice(start, end), { mode: 'transform' });
const install = new Function('context', `with (context) { ${compiled}; return { showWorkspace, openLiveAction }; }`);

function setup(snapshot) {
  const selectedModes = [];
  const manual = { hidden: true };
  const buttons = ['live', 'manual'].map(workspace => ({ dataset: { workspace },
    setAttribute(key, value) { this[key] = value; } }));
  const performanceTools = { hidden: false, scrollIntoView() {} };
  const context = { operatorWorkspace: 'live', performanceTools, currentSnapshot: snapshot,
    app: { querySelector: () => manual, querySelectorAll: () => buttons },
    liveTabs: { querySelector(selector) { return {
      click() { selectedModes.push(selector); }, focus() {},
    }; } } };
  return { ...install(context), context, buttons, manual, selectedModes };
}

test('workspace navigation changes visibility without re-creating panels or touching active work', () => {
  const snapshot = { source: 'native-device', conversation: { pending: true, draft: 'Rozepsaná odpověď' } };
  const ui = setup(snapshot);
  ui.showWorkspace('manual');
  assert.equal(ui.manual.hidden, false);
  assert.equal(ui.context.performanceTools.hidden, true);
  assert.equal(ui.buttons[1]['aria-pressed'], 'true');
  ui.showWorkspace('live');
  assert.equal(ui.manual.hidden, true);
  assert.equal(ui.context.performanceTools.hidden, false);
  assert.equal(ui.buttons[0]['aria-pressed'], 'true');
  assert.equal(ui.context.currentSnapshot, snapshot);
  assert.equal(snapshot.conversation.draft, 'Rozepsaná odpověď');
});

test('next live action reveals the running feature and its existing cancel control', () => {
  for (const [snapshot, mode] of [
    [{ voice: { canStop: true } }, 'voice'],
    [{ dance: { canStop: true } }, 'dance'],
    [{ conversation: { pending: true } }, 'text'],
    [{ source: 'e2-simulator' }, 'text'],
  ]) {
    const ui = setup(snapshot);
    ui.showWorkspace('manual');
    ui.openLiveAction();
    assert.equal(ui.context.performanceTools.hidden, false);
    assert.equal(ui.manual.hidden, true);
    assert.deepEqual(ui.selectedModes, [`#live-tab-${mode}`]);
  }
});

test('snapshot updates do not reveal the text panel over a different selected live tab', () => {
  const renderStart = source.indexOf('function renderConversation(');
  const renderEnd = source.indexOf('function render(snapshot:', renderStart);
  const code = stripTypeScriptTypes(source.slice(renderStart, renderEnd), { mode: 'transform' });
  const values = new Map();
  const element = selector => {
    if (!values.has(selector)) values.set(selector, { textContent: '', querySelector: element });
    return values.get(selector);
  };
  const panel = { hidden: true, querySelector: element };
  const context = { conversationPanel: panel, visitorText: { value: '' }, sendTextButton: {}, cancelTurnButton: {} };
  const render = new Function('context', `with (context) { ${code}; return renderConversation; }`)(context);
  for (const source of ['native-device', 'e2-simulator', 'local-fake']) {
    render({ source, phase: 'ready', controlMode: 'observer' });
    assert.equal(panel.hidden, true);
  }
  panel.hidden = false; // Shared tabs select Text.
  render({ source: 'local-fake', phase: 'ready', controlMode: 'observer' });
  assert.equal(panel.hidden, false);
  assert.equal(context.sendTextButton.disabled, true);
  assert.match(element('[role="status"]').textContent, /není dostupný/);
});

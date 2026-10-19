import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
import { bindTabs } from '../../design-system/ui.js';

// Execute the app's integration with the real shared keyboard controller.
// The DOM doubles keep navigation independent of native transports and hardware.
const source = await readFile(new URL('../src/main.ts', import.meta.url), 'utf8');
const start = source.indexOf('function selectTab(');
const end = source.indexOf('const conversationForm', start);
assert.ok(start >= 0 && end > start);
const code = stripTypeScriptTypes(source.slice(start, end), { mode: 'transform' });
const install = new Function('context', `with (context) { ${code}; return selectTab; }`);

function setup() {
  const panels = new Map(['control', 'provisioning', 'debug'].map(name => [
    `${name}-panel`, { hidden: name !== 'control', setAttribute() {} },
  ]));
  const listeners = {};
  const skip = {};
  const shown = [];
  const scrolls = [];
  let focused;
  let guarded = 0;
  const tablist = { getAttribute: () => null };
  const buttons = [...panels.keys()].map((panelId, index) => {
    const attributes = { 'aria-controls': panelId, 'aria-selected': String(index === 0) };
    const tab = {
      id: `tab-${panelId.replace('-panel', '')}`,
      getAttribute: key => attributes[key],
      setAttribute: (key, value) => { attributes[key] = value; },
      closest: selector => selector === '[role="tablist"]' ? tablist : tab,
      click: () => listeners.click({ target: tab }),
      focus: () => { focused = tab; },
    };
    return tab;
  });
  const tabs = {
    ownerDocument: { getElementById: id => panels.get(id) },
    querySelectorAll: () => buttons,
    querySelector: selector => selector === '.skip-link'
      ? skip : buttons.find(button => `#${button.id}` === selector),
    addEventListener: (name, listener) => { listeners[name] = listener; },
  };
  const context = {
    tabs, bindTabs, controlPanel: panels.get('control-panel'),
    debugPanel: { show: visible => shown.push(['debug', visible]) },
    provisioningPanel: { show: visible => shown.push(['provisioning', visible]) },
    currentSnapshot: { phase: 'running' },
    updateStopControls: () => { guarded++; },
    window: { scrollTo: options => scrolls.push(options) },
  };
  const selectTab = install(context);
  return { buttons, panels, listeners, skip, shown, scrolls, selectTab,
    focused: () => focused, guarded: () => guarded };
}

test('programmatic navigation uses shared selection and preserves native panel lifecycle', () => {
  const ui = setup();
  ui.selectTab('tab-provisioning');
  assert.equal(ui.buttons[1].getAttribute('aria-selected'), 'true');
  assert.deepEqual(ui.buttons.map(button => button.tabIndex), [-1, 0, -1]);
  assert.equal(ui.panels.get('control-panel').hidden, true);
  assert.equal(ui.panels.get('provisioning-panel').hidden, false);
  assert.equal(ui.skip.href, '#provisioning-panel');
  assert.deepEqual(ui.shown, [['debug', false], ['provisioning', true]]);
  assert.equal(ui.guarded(), 1);
  assert.deepEqual(ui.scrolls, [{ top: 0, behavior: 'auto' }]);
});

test('keyboard tabs wrap, update focus and retain the shell Stop guard', () => {
  const ui = setup();
  let prevented = false;
  ui.listeners.keydown({ target: ui.buttons[0], key: 'ArrowLeft', preventDefault() { prevented = true; } });
  assert.equal(prevented, true);
  assert.equal(ui.focused(), ui.buttons[2]);
  assert.equal(ui.panels.get('debug-panel').hidden, false);
  assert.equal(ui.skip.href, '#debug-panel');
  assert.equal(ui.guarded(), 1);
  ui.listeners.keydown({ target: ui.buttons[2], key: 'Home', preventDefault() {} });
  assert.equal(ui.focused(), ui.buttons[0]);
  assert.equal(ui.panels.get('control-panel').hidden, false);
  assert.equal(ui.guarded(), 2);
});

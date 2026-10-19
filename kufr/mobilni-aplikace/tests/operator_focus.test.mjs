import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

// Execute the production focus boundary across replacement snapshots. Native
// adapters are irrelevant here; no browser or iPhone acceptance is claimed.
const source = await readFile(new URL('../src/main.ts', import.meta.url), 'utf8');
const captureStart = source.indexOf('  const focusedAction =');
const captureEnd = source.indexOf("  if (snapshot.phase", captureStart);
const restoreStart = source.indexOf('  if (focusedAction) {');
const restoreEnd = source.indexOf('  updateStopControls(snapshot);', restoreStart);
assert.ok(captureEnd > captureStart && restoreEnd > restoreStart);
const captureCode = stripTypeScriptTypes(source.slice(captureStart, captureEnd), { mode: 'transform' });
const restoreCode = stripTypeScriptTypes(source.slice(restoreStart, restoreEnd), { mode: 'transform' });
const install = new Function('context', `with (context) {
  return { capture() { ${captureCode}; return focusedAction; },
    restore(focusedAction) { ${restoreCode} } };
}`);

class ActionButton {
  constructor(dataset = {}) { this.dataset = dataset; this.disabled = false; }
  hasAttribute(name) { return name === 'data-next-action' && this.dataset.nextAction !== undefined; }
  focus(options) { this.focused = options; }
}

function setup(active) {
  const controls = [];
  const status = { focus(options) { this.focused = options; } };
  const context = { HTMLButtonElement: ActionButton, document: { activeElement: active },
    app: { contains: element => element.owned !== false,
      querySelectorAll: () => controls, querySelector: () => status } };
  return { ...install(context), controls, status };
}

test('a heartbeat restores the same compartment action without scrolling or stealing another draft focus', () => {
  const ui = setup(new ActionButton({ action: 'select', compartment: '4' }));
  const focus = ui.capture();
  ui.controls.push(new ActionButton({ action: 'select', compartment: '3' }),
    new ActionButton({ action: 'select', compartment: '4' }));
  ui.restore(focus);
  assert.equal(ui.controls[0].focused, undefined);
  assert.deepEqual(ui.controls[1].focused, { preventScroll: true });
  assert.equal(ui.status.focused, undefined);
  const outside = new ActionButton();
  outside.owned = false;
  assert.equal(setup(outside).capture(), null);
  assert.equal(setup({ tagName: 'TEXTAREA' }).capture(), null);
});

test('an invalidated confirmation moves focus to current state instead of another available command', () => {
  const ui = setup(new ActionButton({ action: 'confirm' }));
  const focus = ui.capture();
  ui.controls.push(new ActionButton({ action: 'select', compartment: '4' }));
  ui.restore(focus);
  assert.deepEqual(ui.status.focused, { preventScroll: true });
  assert.equal(ui.controls[0].focused, undefined);
});

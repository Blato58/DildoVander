import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

const source = readFileSync(new URL('../theme.js', import.meta.url), 'utf8');
const tokens = JSON.parse(readFileSync(new URL('../tokens.json', import.meta.url), 'utf8'));
const original = JSON.parse(readFileSync(new URL('../themes/olivander.original.json', import.meta.url), 'utf8'));

function boot(saved = {}, denied = false) {
  const storage = new Map(Object.entries(saved));
  const events = new Map();
  const meta = {};
  const document = {
    documentElement: { dataset: {}, style: {} }, head: { append() {} },
    querySelector: () => meta, createElement: () => meta,
  };
  const context = vm.createContext({ document, localStorage: {
    getItem(key) { if (denied) throw Error('denied'); return storage.get(key) ?? null; },
    setItem(key, value) { if (denied) throw Error('denied'); storage.set(key, value); },
  }, addEventListener: (name, handler) => events.set(name, handler) });
  vm.runInContext(source, context);
  return { runtime: context.DildoVanderTheme, document, storage, events, meta };
}

test('synchronous default, invalid preference fallback and denied storage are safe', () => {
  for (const saved of [{}, { 'dildovander.theme': 'unknown' }]) {
    const { runtime, document, meta, storage } = boot(saved);
    assert.equal(runtime.getTheme(), 'lord-voldemort');
    assert.equal(document.documentElement.dataset.theme, 'lord-voldemort');
    assert.equal(meta.content, '#0f0f11');
    assert.equal(storage.get('dildovander.theme'), saved['dildovander.theme'], 'startup does not write storage');
  }
  const { runtime } = boot({}, true);
  assert.equal(runtime.setTheme('olivander'), 'olivander');
});

test('original display names preserve stored theme identities', () => {
  const { runtime } = boot();
  assert.deepEqual(Array.from(runtime.themes, theme => ({ id: theme.id, label: theme.label })), [
    { id: 'lord-voldemort', label: 'Stínový čaroděj' },
    { id: 'olivander', label: 'Mistr hůlek' },
  ]);
});

test('explicit preference wins, legacy explicit choices migrate to the craftsman theme', () => {
  for (const old of ['dark', 'light', 'system']) assert.equal(boot({ 'aw-theme': old }).runtime.getTheme(), 'olivander');
  assert.equal(boot({ 'dildovander.theme': 'lord-voldemort', 'aw-theme': 'dark' }).runtime.getTheme(), 'lord-voldemort');
  assert.equal(boot({ 'dildovander.theme': 'invalid', 'aw-theme': 'dark' }).runtime.getTheme(), 'lord-voldemort');
});

test('switches synchronously, persists, updates browser chrome and notifies without remounting', () => {
  const { runtime, document, storage, meta } = boot();
  const root = document.documentElement;
  const updates = [];
  const unsubscribe = runtime.subscribe(value => updates.push(value));
  runtime.setTheme('olivander');
  assert.equal(document.documentElement, root);
  assert.equal(root.dataset.theme, 'olivander');
  assert.equal(meta.content, '#1B2430');
  assert.equal(storage.get('dildovander.theme'), 'olivander');
  runtime.setTheme('olivander');
  assert.deepEqual(updates, ['olivander']);
  unsubscribe();
  runtime.setTheme('invalid');
  assert.equal(runtime.getTheme(), 'lord-voldemort');
  assert.deepEqual(updates, ['olivander']);
  assert.equal(boot(Object.fromEntries(storage)).runtime.getTheme(), 'lord-voldemort');
});

test('cross-tab preference changes apply without rewriting storage or loops', () => {
  const { runtime, events, storage } = boot();
  events.get('storage')({ key: 'dildovander.theme', newValue: 'olivander' });
  assert.equal(runtime.getTheme(), 'olivander');
  assert.equal(storage.size, 0);
  events.get('storage')({ key: 'device-token', newValue: 'secret' });
  assert.equal(runtime.getTheme(), 'olivander');
  events.get('storage')({ key: null, newValue: null });
  assert.equal(runtime.getTheme(), 'lord-voldemort');
});

test('the craftsman theme retains all original palettes, typography, assets and base geometry tokens', () => {
  for (const theme of ['dark', 'light']) {
    for (const [role, value] of Object.entries(original.themes[theme])) assert.equal(tokens.themes[theme][role], value);
  }
  for (const group of ['primitives', 'fonts', 'typography', 'layout', 'motion', 'reducedMotion']) assert.deepEqual(tokens[group], original[group]);
  assert.equal(tokens.themeRegistry.olivander.palette, 'dark');
});

function luminance(hex) {
  const values = hex.slice(1).match(/../g).map(value => parseInt(value, 16) / 255)
    .map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4);
  return values.reduce((sum, value, index) => sum + value * [.2126, .7152, .0722][index], 0);
}
test('Codex source colors are exact; text and semantic states meet contrast independently', () => {
  const palette = tokens.themes['lord-voldemort'];
  for (const [role, value] of Object.entries({ accent: '#606acc', void: '#0f0f11', ivory: '#e3e4e6', success: '#69c967', danger: '#ff7e78', skill: '#c2a1ff' })) assert.equal(palette[role], value);
  const contrast = (foreground, background) => {
    const values = [luminance(foreground), luminance(background)].sort((a, b) => b - a);
    return (values[0] + .05) / (values[1] + .05);
  };
  for (const background of ['void', 'surface', 'elevated']) {
    for (const role of ['ivory', 'muted', 'link', 'success', 'danger', 'warning', 'skill']) {
      assert(contrast(palette[role], palette[background]) >= 4.5, `${role} on ${background}`);
    }
  }
  assert(contrast(palette['on-accent'], palette.accent) >= 4.5, 'primary button label');
});

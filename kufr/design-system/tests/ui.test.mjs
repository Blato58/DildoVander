import assert from 'node:assert/strict';
import { test } from 'node:test';
import { button, escapeHtml, field, notice, statusBadge, emptyState, wandCard, bindTabs, bindDialog } from '../ui.js';
import { primitives, themes, motion } from '../tokens.mjs';

test('persistent recovery notices keep actions and preserved progress without repeated live announcements', () => {
  const html = notice({ title: 'Disconnected', message: 'Reconnect to refresh.', detail: '<draft is preserved>', action: button({ label: 'Reconnect' }), tone: 'warning', live: false });
  assert.ok(!html.includes('role='));
  assert.ok(html.includes('&lt;draft is preserved&gt;'));
  assert.ok(html.includes('aw-notice-action'));
  assert.ok(html.includes('Reconnect</button>'));
  assert.ok(notice({ message: 'Failed', tone: 'danger' }).includes('role="alert"'));
  assert.ok(notice({ message: 'Saved' }).includes('role="status"'));
});

test('renderers escape untrusted text and reject executable attribute names and URLs', () => {
  const attack = '<img src=x onerror="alert(1)">&\'';
  assert.equal(escapeHtml(attack), '&lt;img src=x onerror=&quot;alert(1)&quot;&gt;&amp;&#39;');
  for (const output of [button({ label: attack }), statusBadge({ label: attack }), notice({ title: attack, message: attack }), emptyState({ title: attack, message: attack }), wandCard({ title: attack })]) {
    assert.ok(!output.includes('<img src=x'));
    assert.ok(output.includes('&lt;img'));
  }
  const action = button({ label: 'Save', attributes: { class: 'local" onclick="bad', onclick: 'bad()', style: 'display:none', 'data-id': '"><script>', 'aria-expanded': false } });
  assert.match(action, /local&quot; onclick=&quot;bad/);
  assert.ok(!action.includes(' onclick="'));
  assert.ok(!action.includes(' style='));
  assert.match(action, /aria-expanded="false"/);
  assert.ok(!wandCard({ title: 'Safe', imageUrl: 'javascript:alert(1)' }).includes('<img'));
  assert.ok(!wandCard({ title: 'Safe', imageUrl: 'java\nscript:alert(1)' }).includes('<img'));
  assert.match(wandCard({ title: 'Local', imageUrl: '/images/wand.png' }), /src="\/images\/wand.png"/);
  const metadata = wandCard({ title: 'Metadata', selected: true, disabled: true, interactive: false });
  assert.match(metadata, /^<article.*data-selected="true" data-disabled="true"/);
  assert.ok(!metadata.includes('aria-pressed'));
});

test('loading and disabled buttons retain native semantics', () => {
  assert.match(button({ label: 'Saving', loading: true }), / disabled aria-busy="true"/);
  assert.match(button({ label: 'Stop', variant: 'danger', disabled: true }), /aw-button--danger.* disabled/);
  assert.match(button({ label: 'Save', type: 'submit', id: 'save' }), /type="submit" id="save"/);
  assert.match(notice({ message: 'Failure', tone: 'danger' }), /role="alert"/);
  assert.match(notice({ message: 'Connected' }), /role="status"/);
});

test('field connects visible label, help and error with its owning control', () => {
  const html = field({ id: 'name', label: 'Name', control: '<input class="aw-input" id="old" aria-describedby="existing">', description: 'Help', error: 'Required' });
  assert.match(html, /label for="name"/);
  assert.match(html, /aria-describedby="existing name-description name-error"/);
  assert.match(html, /aria-invalid="true"/);
  assert.equal((html.match(/ id="name"/g) ?? []).length, 1);
  assert.ok(!html.includes('id="old"'));
  assert.ok(!field({ id: 'new', label: 'New', control: '<input id=old>' }).includes('id=old'));
});

class Node extends EventTarget {
  constructor(attributes = {}) {
    super();
    this.attributes = { ...attributes };
    this.id = attributes.id;
    this.disabled = false;
    this.tabIndex = 0;
    this.isConnected = true;
  }
  setAttribute(key, value) { this.attributes[key] = value; }
  getAttribute(key) { return this.attributes[key] ?? null; }
  closest(selector) { return selector === '[role="tab"]' ? this : this.root; }
  focus() { this.ownerDocument.activeElement = this; }
}

function tabsFixture({ disabledAll = false, vertical = false } = {}) {
  const root = new Node({ 'aria-orientation': vertical ? 'vertical' : 'horizontal' });
  const tabs = ['a', 'b', 'c'].map((id) => new Node({ id, 'aria-controls': `panel-${id}`, 'aria-selected': id === 'a' ? 'true' : 'false' }));
  const panels = Object.fromEntries(tabs.map((tab) => [`panel-${tab.id}`, new Node()]));
  const document = { getElementById: (id) => panels[id], activeElement: null };
  root.ownerDocument = document;
  root.querySelectorAll = () => tabs;
  for (const tab of tabs) { tab.ownerDocument = document; tab.root = root; tab.disabled = disabledAll; }
  function dispatch(tab, key, type = 'keydown') {
    const event = new Event(type, { cancelable: true });
    Object.defineProperties(event, { target: { value: tab }, key: { value: key } });
    root.dispatchEvent(event);
    return event;
  }
  return { root, tabs, panels, document, dispatch };
}

test('tabs use roving focus, skip disabled controls, select sibling panels and clean up', () => {
  const { root, tabs, panels, document, dispatch } = tabsFixture();
  tabs[1].setAttribute('aria-disabled', 'true');
  const selected = [];
  const cleanup = bindTabs(root, { onSelect: (tab) => selected.push(tab.id) });
  assert.deepEqual(tabs.map((tab) => tab.tabIndex), [0, -1, -1]);
  assert.equal(panels['panel-a'].hidden, false);
  assert.equal(panels['panel-c'].hidden, true);
  assert.equal(dispatch(tabs[0], 'ArrowRight').defaultPrevented, true);
  assert.equal(document.activeElement, tabs[2]);
  assert.equal(panels['panel-c'].hidden, false);
  assert.equal(panels['panel-c'].getAttribute('aria-labelledby'), 'c');
  dispatch(tabs[2], 'ArrowRight');
  assert.equal(document.activeElement, tabs[0]);
  dispatch(tabs[0], 'End');
  assert.equal(document.activeElement, tabs[2]);
  dispatch(tabs[2], 'Home');
  assert.equal(document.activeElement, tabs[0]);
  dispatch(tabs[1], undefined, 'click');
  assert.deepEqual(selected, ['c', 'a', 'c', 'a']);
  cleanup();
  dispatch(tabs[0], 'End');
  assert.equal(document.activeElement, tabs[0]);
});

test('tabs support vertical orientation and all-disabled groups', () => {
  const vertical = tabsFixture({ vertical: true });
  bindTabs(vertical.root);
  vertical.dispatch(vertical.tabs[0], 'ArrowDown');
  assert.equal(vertical.document.activeElement, vertical.tabs[1]);
  vertical.dispatch(vertical.tabs[1], 'ArrowRight');
  assert.equal(vertical.document.activeElement, vertical.tabs[1]);
  const disabled = tabsFixture({ disabledAll: true });
  bindTabs(disabled.root);
  assert.deepEqual(disabled.tabs.map((tab) => tab.tabIndex), [-1, -1, -1]);
  assert.ok(disabled.tabs.every((tab) => tab.getAttribute('aria-selected') === 'false'));
});

test('native dialog cancels, restores focus and removes listeners on destroy', () => {
  const document = { activeElement: null };
  const invoker = new Node();
  const cancel = new Node();
  invoker.ownerDocument = cancel.ownerDocument = document;
  document.activeElement = invoker;
  const dialog = new Node();
  dialog.ownerDocument = document;
  dialog.querySelector = () => cancel;
  dialog.showModal = () => { dialog.open = true; };
  dialog.close = (value = '') => { dialog.open = false; dialog.returnValue = value; dialog.dispatchEvent(new Event('close')); };
  const closes = [];
  const behavior = bindDialog(dialog, { onClose: (value) => closes.push(value) });
  behavior.open();
  assert.equal(document.activeElement, cancel);
  const escape = new Event('cancel', { cancelable: true });
  dialog.dispatchEvent(escape);
  assert.equal(escape.defaultPrevented, true);
  assert.equal(dialog.open, false);
  assert.equal(document.activeElement, invoker);
  behavior.open();
  behavior.destroy();
  assert.equal(document.activeElement, invoker);
  assert.deepEqual(closes, ['', '']);
  const after = new Event('cancel', { cancelable: true });
  dialog.dispatchEvent(after);
  assert.equal(after.defaultPrevented, false);
});

test('generated tokens preserve canonical primitives and semantic aliases', () => {
  assert.deepEqual(primitives, { mist: '#EEF1F5', midnight: '#1B2430', steel: '#8A94A6', periwinkle: '#B8C0FF' });
  assert.equal(themes.dark.brass, 'var(--aw-accent)');
  assert.equal(themes.light.accent, '#4E5594');
  assert.equal(motion['motion-fast'], '150ms');
  assert.ok(Object.isFrozen(primitives));
  assert.ok(Object.isFrozen(themes.dark));
});

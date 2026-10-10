import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';

const script = await readFile(new URL('./dashboard.js', import.meta.url), 'utf8');

function createPage(staticPreview) {
  const nodes = new Map();
  for (const id of ['token', 'connect', 'disconnect', 'connection-status', 'connection-error', 'slots', 'timeline', 'token-help', 'static-preview-help']) {
    nodes.set(`#${id}`, {
      value: '', textContent: '', disabled: id === 'disconnect', hidden: true,
      listeners: {}, focused: false,
      addEventListener(type, handler) { this.listeners[type] = handler; },
      focus() { this.focused = true; },
    });
  }
  const requests = [];
  const context = {
    document: { body: { hasAttribute: () => staticPreview }, querySelector: (selector) => nodes.get(selector) },
    window: { addEventListener() {} },
    AbortController,
    fetch: async (path, options) => {
      requests.push({ path, options });
      return { ok: false, status: 401 };
    },
  };
  runInNewContext(script, context);
  return { nodes, requests };
}

const preview = createPage(true);
assert.equal(preview.nodes.get('#token').disabled, true);
assert.equal(preview.nodes.get('#connect').disabled, true);
assert.match(preview.nodes.get('#connection-status').textContent, /Veřejný náhled/);
assert.equal(preview.nodes.get('#static-preview-help').hidden, false);
// The public guard also blocks synthetic keyboard/click events.
preview.nodes.get('#token').value = 'must-never-be-sent';
await preview.nodes.get('#connect').listeners.click();
preview.nodes.get('#token').listeners.keydown({ key: 'Enter' });
assert.equal(preview.requests.length, 0);

const local = createPage(false);
await local.nodes.get('#connect').listeners.click();
assert.equal(local.nodes.get('#token').focused, true);
assert.equal(local.requests.length, 0);
local.nodes.get('#token').value = 'test-token';
await local.nodes.get('#connect').listeners.click();
assert.equal(local.nodes.get('#token').value, '');
assert.equal(local.requests.length, 1);
assert.equal(local.requests[0].path, '/v1/observe');
assert.equal(local.requests[0].options.headers.Authorization, 'Bearer test-token');
assert.match(local.nodes.get('#connection-error').textContent, /Neplatný testovací token/);
assert.equal(local.nodes.get('#connect').disabled, false);
assert.equal(local.nodes.get('#disconnect').disabled, true);
console.log('Dashboard public preview guard and local token privacy: PASS');

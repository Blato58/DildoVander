import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { runInNewContext } from 'node:vm';
import { mountDashboardUi, statusBadge, notice, emptyState } from './dashboard-ui.js';

const script = (await readFile(new URL('./dashboard.js', import.meta.url), 'utf8')).replace(/^import .*;\r?\n/, '');

function createPage(staticPreview, fetchResponse) {
  const nodes = new Map();
  function createNode() {
    return {
      value: '', _text: '',
      get textContent() { return this._text; },
      set textContent(value) { this._text = value; },
      set innerHTML(value) { this.markup = value; this._text = value.replace(/<[^>]*>/g, ''); },
      disabled: false, hidden: true, children: [], attributes: {},
      listeners: {}, focused: false,
      addEventListener(type, handler) { this.listeners[type] = handler; },
      focus() { this.focused = true; },
      setAttribute(name, value) { this.attributes[name] = value; },
      append(...children) { this.children.push(...children); },
      replaceChildren(...children) { this.children = children; },
    };
  }
  for (const id of [
    'token', 'connect', 'disconnect', 'connection-status', 'connection-error', 'slots',
    'timeline', 'token-help', 'static-preview-help', 'event-filter', 'event-count',
    'clear-events', 'timeline-empty', 'model-freshness', 'theme-toggle', 'mode',
    'phase', 'journal', 'revision', 'event-announcement', 'connection-controls', 'timeline-tools', 'theme-control',
  ]) {
    nodes.set(`#${id}`, createNode());
  }
  nodes.get('#disconnect').disabled = true;
  const requests = [];
  const body = { hasAttribute: () => staticPreview, dataset: {} };
  const documentElement = { dataset: { theme: 'dark' } };
  const context = {
    document: { body, documentElement, querySelector: (selector) => nodes.get(selector), createElement: createNode },
    window: { addEventListener() {} },
    AbortController, TextDecoder, mountDashboardUi, statusBadge, notice, emptyState,
    fetch: async (path, options) => {
      requests.push({ path, options });
      return fetchResponse ? fetchResponse(options) : { ok: false, status: 401 };
    },
  };
  runInNewContext(script, context);
  return { nodes, requests, body, documentElement };
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
assert.match(local.nodes.get('#connection-controls').markup, /type="password"/);
assert.match(local.nodes.get('#connection-controls').markup, /id="disconnect" disabled/);
assert.match(local.nodes.get('#timeline-tools').markup, /for="event-filter"/);
assert.equal(local.nodes.get('#slots').children.length, 6);
assert.ok(local.nodes.get('#slots').children.every((slot) => slot.className === 'unknown'));
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

// A streamed snapshot is current only while the observer remains connected.
let finishStream;
let observerSignal;
const messages = [
  { kind: 'snapshot', snapshot: { controlMode: 'standalone', phase: 'ready', revision: 42, readyMask: 5 }, journalPending: false },
  ...Array.from({ length: 105 }, (_, index) => ({
    kind: 'timeline', event: { timeMs: index * 1000, event: index % 2 ? 'release_latch' : 'search', compartment: 3 },
  })),
];
let firstRead = true;
const streamed = createPage(false, async ({ signal }) => {
  observerSignal = signal;
  return {
    ok: true,
    body: {
      getReader: () => ({
        read: () => {
          if (firstRead) {
            firstRead = false;
            return Promise.resolve({ value: new TextEncoder().encode(messages.map((message) => JSON.stringify(message)).join('\n') + '\n'), done: false });
          }
          return new Promise((resolve, reject) => {
            finishStream = () => resolve({ done: true });
            signal.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')));
          });
        },
      }),
    },
  };
});
streamed.nodes.get('#token').value = 'local-only';
const observing = streamed.nodes.get('#connect').listeners.click();
await new Promise((resolve) => setImmediate(resolve));
assert.equal(streamed.body.dataset.connection, 'connected');
assert.equal(streamed.nodes.get('#token').disabled, true);
assert.equal(streamed.nodes.get('#phase').textContent, 'Připraveno');
assert.equal(streamed.nodes.get('#journal').textContent, 'Uzavřený');
assert.equal(streamed.nodes.get('#slots').children[1].className, 'unavailable');
assert.equal(streamed.nodes.get('#timeline').children.length, 100);
assert.equal(streamed.nodes.get('#event-announcement').children.length, 1);
assert.match(streamed.nodes.get('#event-announcement').children[0].textContent, /104.00 s/);
assert.equal(streamed.nodes.get('#timeline').children[0].children[0].textContent, '104.00 s');
streamed.nodes.get('#event-filter').value = 'západku';
streamed.nodes.get('#event-filter').listeners.input();
assert.equal(streamed.nodes.get('#timeline').children.length, 50);
assert.match(streamed.nodes.get('#event-count').textContent, /50 z 100/);
streamed.nodes.get('#event-filter').value = 'žádná shoda';
streamed.nodes.get('#event-filter').listeners.input();
assert.equal(streamed.nodes.get('#timeline-empty').hidden, false);
assert.match(streamed.nodes.get('#timeline-empty').textContent, /Filtru neodpovídá/);
streamed.nodes.get('#clear-events').listeners.click();
assert.equal(streamed.nodes.get('#timeline').children.length, 0);
assert.equal(streamed.nodes.get('#clear-events').disabled, true);
assert.equal(observerSignal.aborted, false, 'Clearing the local timeline must not change the model connection');
assert.equal(streamed.requests.length, 1, 'Local filters and clearing must not issue model commands');
streamed.nodes.get('#theme-toggle').listeners.click();
assert.equal(streamed.documentElement.dataset.theme, 'light');
streamed.nodes.get('#theme-toggle').listeners.click();
assert.equal(streamed.documentElement.dataset.theme, 'dark');
finishStream();
await observing;
assert.equal(streamed.body.dataset.connection, 'disconnected');
assert.ok(streamed.nodes.get('#slots').children.every((slot) =>
  slot.children[2].markup.includes('aw-badge--neutral')), 'Historical availability must not retain a current success badge');
assert.match(streamed.nodes.get('#model-freshness').textContent, /není aktuální/);
assert.match(streamed.nodes.get('#phase').textContent, /Poslední:/);
assert.equal(streamed.nodes.get('#token').disabled, false);
console.log('Dashboard preview guard, token privacy, snapshot freshness, bounded history, filter and themes: PASS');

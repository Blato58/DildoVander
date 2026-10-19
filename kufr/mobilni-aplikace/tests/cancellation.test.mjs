import assert from 'node:assert/strict';
import test from 'node:test';
import { throwIfAborted } from '../src/application/cancellation.mjs';

test('cancellation supports signals without throwIfAborted or reason', () => {
  const controller = new AbortController();
  Object.defineProperties(controller.signal, { throwIfAborted: { value: undefined }, reason: { value: undefined } });
  assert.doesNotThrow(() => throwIfAborted(controller.signal));
  controller.abort();
  assert.throws(() => throwIfAborted(controller.signal), { name: 'AbortError' });
});

test('cancellation preserves a supplied reason and accepts an optional signal', () => {
  const controller = new AbortController(), reason = new Error('source retired');
  controller.abort(reason);
  assert.throws(() => throwIfAborted(controller.signal), error => error === reason);
  assert.doesNotThrow(() => throwIfAborted());
  assert.doesNotThrow(() => throwIfAborted(null));
});

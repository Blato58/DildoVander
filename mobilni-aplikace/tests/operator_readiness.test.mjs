import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

const source = await readFile(new URL('../src/features/device_connection/OperatorReadiness.ts', import.meta.url), 'utf8');
const compiled = stripTypeScriptTypes(source, { mode: 'transform' });
const { operatorReadiness, liveActionBlocker } = await import(`data:text/javascript;base64,${Buffer.from(compiled).toString('base64')}`);
const ready = { source: 'native-device', phase: 'ready', controlMode: 'observer',
  readyMask: 63, activeCompartment: null, busy: false, status: 'Přijatý stav.' };

test('disconnected and stopped snapshots take priority over stale activity and lease', () => {
  for (const [phase, action] of [['disconnected', 'connection'], ['stopped', 'recovery']]) {
    const state = { ...ready, phase, controlMode: 'manual', busy: true,
      voice: { canStop: true }, dance: { canStop: true }, conversation: { pending: true } };
    assert.equal(operatorReadiness(state).action, action);
    assert.notEqual(operatorReadiness(state).tone, 'neutral');
  }
});

test('active voice, music and text lead to live cancellation while actuator commands lead to manual state', () => {
  for (const activity of [ { voice: { canStop: true } }, { dance: { canStop: true } },
    { conversation: { pending: true } } ]) {
    assert.equal(operatorReadiness({ ...ready, ...activity, busy: true }).action, 'live');
  }
  assert.equal(operatorReadiness({ ...ready, phase: 'running', busy: true }).action, 'manual');
  assert.equal(operatorReadiness({ ...ready, controlMode: 'manual' }).action, 'manual');
});

test('simulation and connected device never imply measured physical or audio readiness', () => {
  assert.equal(operatorReadiness({ ...ready, source: 'local-fake' }).action, 'manual');
  assert.equal(operatorReadiness({ ...ready, source: 'e2-simulator' }).action, 'live');
  assert.match(operatorReadiness({ ...ready, source: 'local-fake' }).message, /simulaci/);
  assert.match(operatorReadiness({ ...ready, source: 'e2-simulator' }).message, /neprokazuje fyzický/);
  assert.match(operatorReadiness(ready).message, /nepotvrzuje připravené audio/);
  assert.deepEqual(ready, { source: 'native-device', phase: 'ready', controlMode: 'observer',
    readyMask: 63, activeCompartment: null, busy: false, status: 'Přijatý stav.' });
});

test('disabled live actions explain connection, recovery, lease, settings and firmware availability', () => {
  assert.match(liveActionBlocker({ ...ready, phase: 'disconnected' }, 'voice'), /odpojený/);
  assert.match(liveActionBlocker({ ...ready, phase: 'stopped' }, 'dance'), /místní kontrola/);
  assert.match(liveActionBlocker({ ...ready, controlMode: 'manual' }, 'voice'), /uvolněte řízení/);
  assert.match(liveActionBlocker({ ...ready, voice: { available: true, mode: 'off' } }, 'voice'), /vypnutý/);
  assert.match(liveActionBlocker(ready, 'dance'), /SD kartu/);
  assert.equal(liveActionBlocker({ ...ready, voice: { canStop: true } }, 'voice'), '');
  assert.equal(liveActionBlocker({ ...ready, dance: { available: true, canStart: true } }, 'dance'), '');
});

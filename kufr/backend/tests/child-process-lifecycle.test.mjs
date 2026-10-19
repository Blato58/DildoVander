import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import test from 'node:test';
import { stopChildProcess } from './child-process-lifecycle.mjs';

test('cleanup of an already signaled child does not wait for another exit event', { timeout: 2000 }, async () => {
  const child = new EventEmitter();
  child.exitCode = null;
  child.signalCode = 'SIGTERM';
  child.kill = () => assert.fail('an exited child must not be killed again');
  await stopChildProcess(child);
  await stopChildProcess(child);
  assert.equal(child.listenerCount('exit'), 0);
});

test('a real listener child can be stopped repeatedly', { timeout: 5000 }, async () => {
  const child = spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000); process.stdout.write("ready");'],
    { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
  try {
    await once(child.stdout, 'data', { signal: AbortSignal.timeout(2000) });
    await stopChildProcess(child);
    assert.ok(child.exitCode !== null || child.signalCode !== null, 'the child exited before cleanup returned');
    if (process.platform !== 'win32') {
      assert.equal(child.exitCode, null);
      assert.equal(child.signalCode, 'SIGTERM');
    }
    await stopChildProcess(child);
    assert.equal(child.listenerCount('exit'), 0);
  } finally {
    await stopChildProcess(child);
  }
});

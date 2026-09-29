import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';
import { appendFile, mkdtemp, rm, unlink } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { NativeBridge } from './server.mjs';

const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;
if (!bridgeExecutable) {
  throw new Error('KUFR_SIMULATOR_BRIDGE must name the compiled native bridge');
}

const bootId = randomUUID();
const leaseId = randomUUID();
const commandId = randomUUID();
const temporaryDirectory = await mkdtemp(join(tmpdir(), 'kufr-bridge-test-'));
const journalPath = join(temporaryDirectory, 'journal.log');
let bridge = new NativeBridge(bridgeExecutable, bootId, journalPath);

try {
  assert.equal((await bridge.send('SNAPSHOT', 0)).state.success, true);
  const competingBridge = new NativeBridge(bridgeExecutable, randomUUID(), journalPath);
  await assert.rejects(competingBridge.send('SNAPSHOT', 0), /native simulator exited/);
  await competingBridge.close();

  const acquired = await bridge.send('ACQUIRE', leaseId, 0);
  assert.equal(acquired.state.success, true);
  const accepted = await bridge.send(
    'SELECT', commandId, 'kufr-001', bootId, leaseId,
    1, acquired.state.revision, 0, 2000, 4, 'manual', 0,
  );
  assert.equal(accepted.state.outcome, 'accepted');
  assert.equal((await bridge.send('BEGIN', 0)).state.success, true);

  const expired = await bridge.send('RENEW', leaseId, 6001);
  assert.equal(expired.state.success, false);
  assert.equal(expired.state.leaseId, '');
  assert.equal(expired.state.activeCommandId, '');
  assert.equal(expired.state.phase, 'stopped');
  assert.equal(expired.state.lastOutcome, 'outcome_unknown');
  assert.equal(expired.state.journalPending, true);

  const snapshot = await bridge.send('SNAPSHOT', 6001);
  assert.equal(snapshot.state.activeCommandId, '');
  assert.equal(snapshot.state.lastOutcome, 'outcome_unknown');
  await bridge.close();

  bridge = new NativeBridge(bridgeExecutable, randomUUID(), journalPath);
  const restarted = await bridge.send('SNAPSHOT', 0);
  assert.equal(restarted.state.needsRecovery, true);
  assert.equal(restarted.state.journalPending, true);
  assert.equal(restarted.state.phase, 'recoveryRequired');
  assert.equal((await bridge.send('ACQUIRE', randomUUID(), 0)).state.success, false);
  const recovered = await bridge.send('RECOVER', 0);
  assert.equal(recovered.state.success, true);
  assert.equal(recovered.state.readyMask, 55);
  assert.equal(recovered.state.journalPending, false);
  await bridge.close();

  bridge = new NativeBridge(bridgeExecutable, randomUUID(), journalPath);
  const stable = await bridge.send('SNAPSHOT', 0);
  assert.equal(stable.state.readyMask, 55);
  assert.equal(stable.state.needsRecovery, false);
  await bridge.close();

  await appendFile(journalPath, 'B incomplete');
  bridge = new NativeBridge(bridgeExecutable, randomUUID(), journalPath);
  const corrupted = await bridge.send('SNAPSHOT', 0);
  assert.equal(corrupted.state.journalFault, true);
  assert.equal(corrupted.state.readyMask, 0);
  assert.equal(corrupted.state.needsRecovery, true);
  assert.equal((await bridge.send('RECOVER', 0)).state.success, false);
  await bridge.close();
  await unlink(journalPath);
  bridge = new NativeBridge(bridgeExecutable, randomUUID(), journalPath);
  const lostJournal = await bridge.send('SNAPSHOT', 0);
  assert.equal(lostJournal.state.journalFault, true);
  assert.equal(lostJournal.state.readyMask, 0);
  console.log('Native bridge: exclusive writer, late lease, restart recovery and journal loss fail closed');
} finally {
  await bridge.close();
  await rm(temporaryDirectory, { recursive: true, force: true });
}

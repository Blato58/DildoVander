import assert from 'node:assert/strict';
import { randomUUID } from 'node:crypto';

import { NativeBridge } from './server.mjs';

const bridgeExecutable = process.env.KUFR_SIMULATOR_BRIDGE;
if (!bridgeExecutable) {
  throw new Error('KUFR_SIMULATOR_BRIDGE must name the compiled native bridge');
}

const bootId = randomUUID();
const leaseId = randomUUID();
const commandId = randomUUID();
const bridge = new NativeBridge(bridgeExecutable, bootId);

try {
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
  console.log('Native bridge: late lease renewal stops selection and preserves unknown outcome');
} finally {
  bridge.close();
}

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

// Execute the actual UI tap boundary with native adapters replaced, keeping
// the reconnect/lifecycle orchestration independent of a browser or iPhone.
const source = await readFile(new URL('../src/main.ts', import.meta.url), 'utf8');
const start = source.indexOf("  connectButton.addEventListener('click', () => {");
const end = source.indexOf("  disconnectButton.addEventListener", start);
assert.ok(start >= 0 && end > start);
const handler = stripTypeScriptTypes(source.slice(start, end), { mode: 'transform' });
const install = new Function('context', `with (context) { ${handler} }`);

class NativeDeviceConnection {
  uncertainCommand = 'selection-in-flight';
  connects = 0;
  connect() { this.connects++; }
}
function setup() {
  let tap;
  let pending;
  const connection = new NativeDeviceConnection();
  const context = {
    document: { hidden: false }, controlConnectGeneration: 0,
    connectButton: { addEventListener: (_, action) => { tap = action; } },
    provisioningPanel: { refreshLocalAddress: async () => true },
    nativeBridge: { savedDevice: async () => ({ paired: true, deviceId: 'kufr-1', host: '192.168.0.62' }) },
    savedDeviceId: 'kufr-1', addressInput: {}, status: {}, connection, NativeDeviceConnection,
    applySavedDevice: () => { throw new Error('Same device must preserve its connection state'); },
    performAsync: action => { pending = action(); return pending; },
  };
  install(context);
  return { context, connection, tap: () => { tap(); return pending; } };
}

test('explicit connect refreshes the route and preserves same-device uncertain-command state', async () => {
  const flow = setup();
  await flow.tap();
  assert.equal(flow.context.connection, flow.connection);
  assert.equal(flow.connection.uncertainCommand, 'selection-in-flight');
  assert.equal(flow.connection.connects, 1);
  assert.equal(flow.context.addressInput.value, '192.168.0.62');
});

test('background/foreground cancels an old connect tap at either async boundary', async () => {
  for (const boundary of ['refresh', 'savedDevice']) {
    const flow = setup();
    let finish;
    let entered;
    const entry = new Promise(resolve => { entered = resolve; });
    const wait = () => { entered(); return new Promise(resolve => { finish = resolve; }); };
    if (boundary === 'refresh') flow.context.provisioningPanel.refreshLocalAddress = wait;
    else flow.context.nativeBridge.savedDevice = wait;
    const connecting = flow.tap();
    await entry;
    flow.context.document.hidden = true;
    ++flow.context.controlConnectGeneration;
    flow.context.document.hidden = false;
    ++flow.context.controlConnectGeneration;
    finish({ paired: true, deviceId: 'kufr-1', host: '192.168.0.62' });
    await connecting;
    assert.equal(flow.connection.connects, 0, boundary);
    assert.equal(flow.connection.uncertainCommand, 'selection-in-flight');
  }
});


test('a skipped or failed BLE refresh cannot start a stale LAN connection', async () => {
  const flow = setup();
  flow.context.provisioningPanel.refreshLocalAddress = async () => false;
  flow.context.nativeBridge.savedDevice = async () => { throw new Error('Must not consume stale address'); };
  await flow.tap();
  assert.equal(flow.connection.connects, 0);
});

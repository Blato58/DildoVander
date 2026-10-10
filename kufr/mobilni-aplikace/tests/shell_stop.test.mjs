import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';

// Execute the actual shared UI boundary, as the native connect-flow tests do.
// Adapters publish snapshots; neither returning from stop() nor an ACK is a
// physical shutdown result. Minimal view doubles keep this test offline.
const source = await readFile(new URL('../src/main.ts', import.meta.url), 'utf8');
const start = source.indexOf('function updateStopControls(');
const end = source.indexOf('function selectTab(', start);
assert.ok(start >= 0 && end > start);
const handlers = stripTypeScriptTypes(source.slice(start, end), { mode: 'transform' });
const install = new Function('context', `with (context) { ${handlers}; return { stopDevice, updateStopControls }; }`);

function setup(source = 'native-device') {
  let announcements = 0;
  let status = '';
  const controlStop = {};
  const context = {
    currentSnapshot: { source, phase: 'ready', busy: false, status: 'Připojeno.' },
    stopPending: false, stopSubmitting: false, stopStatus: '', pendingAction: { compartment: 2 },
    shellStop: { setAttribute(name, value) { this[name] = value; } },
    shellStatus: { get textContent() { return status; },
      set textContent(value) { status = value; announcements++; } },
    debugStopGuard: {}, app: { querySelector: () => controlStop },
    connection: { stop() {} },
  };
  const ui = install(context);
  function publish(snapshot) {
    context.currentSnapshot = { ...context.currentSnapshot, ...snapshot };
    ui.updateStopControls(context.currentSnapshot);
  }
  publish({});
  return { context, ui, controlStop, publish, announcements: () => announcements };
}

test('one Stop stays guarded across shell/control/debug taps until an authoritative result', () => {
  const view = setup();
  let commands = 0;
  view.context.connection.stop = () => {
    commands++;
    // Conversation invalidation can publish before the adapter marks Stop busy.
    view.publish({ busy: false, status: 'Rozhovor zneplatněn.' });
    view.ui.stopDevice();
    view.publish({ busy: true, status: 'Odesílám Stop; čekám na potvrzený stav…' });
  };
  view.ui.stopDevice();
  assert.equal(view.context.pendingAction, null);
  assert.equal(view.context.shellStop.disabled, true);
  assert.equal(view.controlStop.disabled, true);
  assert.equal(view.context.debugStopGuard.disabled, true);
  assert.equal(view.context.shellStop['aria-busy'], 'true');
  assert.equal(view.context.shellStatus.textContent, 'Odesílám Stop; čekám na potvrzený stav…');
  view.ui.stopDevice(); // A second route invokes the same guarded handler.
  assert.equal(commands, 1);
  view.publish({ phase: 'stopped', busy: false, status: 'Výsledek je nejistý; nutná místní kontrola.' });
  assert.equal(view.context.stopPending, false);
  assert.equal(view.context.shellStop.disabled, true);
  assert.equal(view.context.shellStatus.textContent, 'Výsledek je nejistý; nutná místní kontrola.');
});

test('failure remains visible on another tab and unchanged snapshots do not repeat live announcements', () => {
  const view = setup();
  view.context.connection.stop = () => view.publish({ busy: true, status: 'Čekám na potvrzený stav.' });
  view.ui.stopDevice();
  const announcements = view.announcements();
  view.publish({}); // Heartbeat or tab refresh.
  assert.equal(view.announcements(), announcements);
  view.publish({ phase: 'disconnected', busy: false, status: 'Spojení ztraceno; výsledek povelu je nejistý.' });
  assert.equal(view.context.shellStatus.textContent, 'Spojení ztraceno; výsledek povelu je nejistý.');
  assert.equal(view.context.shellStop.disabled, true);
  assert.equal(view.context.debugStopGuard.disabled, true);
  assert.equal(view.context.shellStop['aria-busy'], 'false');
});

test('fake Stop resolves synchronously and a subsequent simulated inspection allows a fresh Stop', () => {
  const view = setup('local-fake');
  let commands = 0;
  view.context.connection.stop = () => {
    commands++;
    view.publish({ phase: 'stopped', busy: false, status: 'Stop v simulaci; výsledek je neznámý.' });
  };
  view.ui.stopDevice();
  assert.equal(view.context.stopPending, false);
  assert.equal(view.context.shellStop.textContent, 'Stop simulace');
  assert.equal(view.context.shellStatus.textContent, 'Stop v simulaci; výsledek je neznámý.');
  view.ui.stopDevice();
  assert.equal(commands, 1);
  view.publish({ phase: 'ready', status: 'Místní kontrola byla simulována.' });
  assert.equal(view.context.shellStop.disabled, false);
  view.ui.stopDevice();
  assert.equal(commands, 2);
});

test('a device busy with another operation still permits the first emergency Stop', () => {
  const view = setup();
  view.publish({ phase: 'running', busy: true, status: 'Probíhá výběr.' });
  let commands = 0;
  view.context.connection.stop = () => { commands++; view.publish({ status: 'Odesílám Stop…' }); };
  assert.equal(view.context.shellStop.disabled, false);
  view.ui.stopDevice();
  view.ui.stopDevice();
  assert.equal(commands, 1);
  assert.equal(view.context.shellStatus.textContent, 'Odesílám Stop…');
});

test('queued ordinary ready/running snapshots cannot unlock or hide an unconfirmed Stop', () => {
  for (const phase of ['ready', 'running']) {
    const view = setup();
    let commands = 0;
    view.context.connection.stop = () => {
      commands++;
      view.publish({ busy: true, status: 'Odesílám Stop; čekám na potvrzený stav…' });
    };
    view.ui.stopDevice();
    const announcements = view.announcements();
    // ProtocolDeviceConnection recalculates busy without tracking safety.stop,
    // and accepts equal-sequence queued snapshots. Neither is a Stop result.
    view.publish({ phase, busy: false, status: 'Ruční lease je aktivní.', diagnostics: { eventSequence: 7 } });
    view.publish({ phase, busy: false, status: 'Běžný heartbeat.', diagnostics: { eventSequence: 7 } });
    view.ui.stopDevice();
    assert.equal(commands, 1, phase);
    assert.equal(view.context.stopPending, true, phase);
    assert.equal(view.controlStop.disabled, true, phase);
    assert.equal(view.context.debugStopGuard.disabled, true, phase);
    assert.equal(view.context.shellStop['aria-busy'], 'true', phase);
    assert.equal(view.context.shellStatus.textContent, 'Odesílám Stop; čekám na potvrzený stav…', phase);
    assert.equal(view.announcements(), announcements, phase);
  }
});

test('disconnect ends the uncertain Stop and explicit reconnection permits one fresh attempt', () => {
  const view = setup();
  let commands = 0;
  view.context.connection.stop = () => {
    commands++;
    view.publish({ busy: true, status: 'Odesílám Stop; čekám na potvrzený stav…' });
  };
  view.ui.stopDevice();
  view.publish({ phase: 'ready', busy: false, status: 'Běžný stav bez výsledku Stop.' });
  view.publish({ phase: 'disconnected', busy: false, status: 'Spojení ztraceno; výsledek povelu je nejistý.' });
  assert.equal(view.context.stopPending, false);
  assert.equal(view.context.shellStatus.textContent, 'Spojení ztraceno; výsledek povelu je nejistý.');
  view.ui.stopDevice();
  assert.equal(commands, 1);
  view.publish({ phase: 'ready', busy: false, status: 'Nové připojení v režimu pozorování.' });
  assert.equal(view.context.shellStop.disabled, false);
  view.ui.stopDevice();
  view.ui.stopDevice();
  assert.equal(commands, 2);
  assert.equal(view.context.stopPending, true);
});

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';

const executable = process.argv[2];
if (!executable) {
  throw new Error('Usage: node verify-scenarios.mjs <simulator executable>');
}

const fixture = JSON.parse(readFileSync(new URL('../../contracts/fixtures/valid/selection-start.json', import.meta.url), 'utf8'));
assert.equal(fixture.type, 'selection.start');
assert.equal(fixture.protocolVersion, 1);
assert.ok(Number.isInteger(fixture.payload.compartment));
assert.ok(fixture.payload.compartment >= 1 && fixture.payload.compartment <= 6);
assert.equal(typeof fixture.commandId, 'string');
assert.ok(fixture.commandId.length > 0);

function runScenario(name) {
  const result = spawnSync(executable, [name, String(fixture.payload.compartment), fixture.commandId], { encoding: 'utf8' });
  assert.equal(result.status, 0, `${name}: ${result.stderr || result.stdout}`);
  const lines = result.stdout.trim().split(/\r?\n/);
  assert.ok(lines.every((line) => /^SIMULACE t=\d+ event=[a-z_]+(?: value=\d+)?$/.test(line)), `${name}: malformed timeline`);
  const events = lines.map((line) => {
    const match = /^SIMULACE t=(\d+) event=([a-z_]+)(?: value=(\d+))?$/.exec(line);
    return { time: Number(match[1]), name: match[2], value: match[3] === undefined ? undefined : Number(match[3]) };
  });
  assert.ok(events.every((event, index) => index === 0 || event.time >= events[index - 1].time), `${name}: time went backwards`);
  assert.ok(events.filter((event) => event.name === 'release_latch').length <= 1, `${name}: latch released twice`);
  return events;
}

function count(events, name) {
  return events.filter((event) => event.name === name).length;
}

function value(events, name) {
  const matches = events.filter((event) => event.name === name);
  assert.equal(matches.length, 1, `${name}: expected one event`);
  return matches[0].value;
}

const expectedMask = 63 & ~(1 << (fixture.payload.compartment - 1));

for (const scenario of ['normal', 'lost-ack']) {
  const events = runScenario(scenario);
  assert.equal(count(events, 'journal_begin'), 1);
  assert.equal(count(events, 'journal_resolve'), 1);
  assert.equal(count(events, 'release_latch'), 1);
  assert.equal(count(events, 'completed'), 1);
  assert.equal(value(events, 'journal_pending'), 0);
  assert.equal(value(events, 'ready_mask'), expectedMask);
  assert.equal(count(events, 'ack_duplicate'), scenario === 'lost-ack' ? 2 : 0);
}

for (const scenario of ['stop', 'lease-loss']) {
  const events = runScenario(scenario);
  assert.equal(count(events, 'journal_begin'), 1);
  assert.equal(count(events, 'journal_resolve'), 0);
  assert.equal(count(events, 'release_latch'), 0);
  assert.equal(count(events, 'stopped'), 1);
  assert.equal(value(events, 'journal_pending'), 1);
  const vibrationIndex = events.findIndex((event) => event.name === 'vibration_on');
  const stopIndex = events.findIndex((event) => event.name === 'stopped');
  assert.ok(vibrationIndex >= 0 && vibrationIndex < stopIndex);
  assert.ok(events.slice(vibrationIndex + 1, stopIndex).some((event) => event.name === 'actuators_off'), `${scenario}: output not disabled before Stop`);
}

{
  const events = runScenario('restart');
  assert.equal(count(events, 'journal_begin'), 1);
  assert.equal(count(events, 'release_latch'), 1);
  assert.equal(count(events, 'power_reset'), 1);
  assert.equal(count(events, 'recovery_required'), 1);
  assert.equal(count(events, 'inspection_complete'), 1);
  assert.equal(count(events, 'journal_resolve'), 1);
  assert.equal(value(events, 'ready_mask'), expectedMask);
}

{
  const events = runScenario('journal-failure');
  assert.equal(count(events, 'ack_rejected_journal_failed'), 1);
  assert.equal(count(events, 'journal_begin'), 0);
  assert.equal(count(events, 'vibration_on'), 0);
  assert.equal(count(events, 'release_latch'), 0);
  assert.equal(value(events, 'ready_mask'), 63);
}

console.log('SIMULACE: 6 deterministic scenarios passed');

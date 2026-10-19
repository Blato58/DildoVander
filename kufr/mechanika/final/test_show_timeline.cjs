const {test} = require('node:test');
const assert = require('node:assert/strict');
const {parseTrace, compatibility, diffTraces, mount, MAX_BYTES, MAX_EVENTS} = require('./show_timeline.js');

function fixture() {
  return {
    schemaVersion:1, source:'firmware-core', coreVersion:'dv02-phase-1', bundleHash:'bundle', sourceHash:'source', compiledHash:'compiled',
    sceneId:'legacy-presentation', sceneVersion:1, runtimeProfile:'calibrated-local', calibrationId:'fixture-1', bootId:'synthetic-host',
    operationId:'test', generation:'1', deviceRevision:'1', slot:2, quality:'full', state:'completed', evidence:'timed-complete',
    firstSequence:1, lastSequence:2, droppedEvents:0,
    events:[
      {sequence:1, kind:'accepted', phaseIndex:0, phaseId:'prepare', clockDomain:'device-monotonic', timeMs:'100', dueAt:'100', latenessMs:0, evidence:'accepted', reason:'', slot:2},
      {sequence:2, kind:'completed', phaseIndex:4, phaseId:'rest', clockDomain:'device-monotonic', timeMs:'9100', dueAt:'9100', latenessMs:0, evidence:'timed-complete', reason:'', slot:2},
    ],
  };
}
const parsed = value => parseTrace(JSON.stringify(value));

test('import preserves missing port and measurement times without inventing evidence', () => {
  const result = parsed(fixture());
  assert.equal(result.incomplete, false);
  assert.equal(result.events[0].acceptedAt, undefined);
  assert.equal(result.events[1].measuredAt, undefined);
  assert.deepEqual(compatibility(result, {bundleHash:'other',sourceHash:'source',compiledHash:'compiled'}), ['bundleHash']);
  const accent = fixture(); accent.sceneId = 'consider'; accent.slot = 0; accent.state = 'skipped'; accent.quality = 'none'; accent.evidence = 'unknown';
  for (const event of accent.events) event.slot = 0;
  assert.equal(parsed(accent).quality, 'none');
});

test('drop and sequence gaps remain visible despite a completed terminal snapshot', () => {
  const trace = fixture(); trace.events[1].sequence = trace.lastSequence = 4; trace.droppedEvents = 2;
  const result = parsed(trace);
  assert.deepEqual(result.gaps, [[2,3]]); assert.equal(result.incomplete, true); assert.equal(result.state, 'completed');
  trace.events = [trace.events[1]]; trace.firstSequence = 4; trace.droppedEvents = 0;
  assert.equal(parsed(trace).incomplete, true);
});

test('matching hashes cannot hide mismatched core, scene versions or unmapped phase IDs', () => {
  const trace = parsed(fixture());
  const manifest = {bundleHash:'bundle',sourceHash:'source',compiledHash:'compiled',semanticsVersion:'dv02-phase-1'};
  const plan = {scenes:[{sceneId:trace.sceneId,sceneVersion:1,phases:[{id:'prepare'},{id:'rest'}]}]};
  assert.deepEqual(compatibility(trace, manifest, plan), []);
  assert.deepEqual(compatibility({...trace,coreVersion:'future',sceneVersion:999}, manifest, plan), ['coreVersion','sceneVersion']);
  trace.events[1].phaseId = 'unknown-phase';
  assert.deepEqual(compatibility(trace, manifest, plan), ['phaseIds']);
});

test('selected scene hash and variant must identify the same versioned plan despite matching bundle', () => {
  const trace = {...fixture(),sceneHash:'main-hash',variant:'main'};
  const manifest = {bundleHash:'bundle',sourceHash:'source',compiledHash:'compiled',semanticsVersion:'dv02-phase-1'};
  const plan = {scenes:[
    {sceneId:trace.sceneId,sceneVersion:1,sceneHash:'main-hash',variant:'main',phases:[{id:'prepare'},{id:'rest'}]},
    {sceneId:trace.sceneId,sceneVersion:1,sceneHash:'short-hash',variant:'short',phases:[{id:'prepare'},{id:'rest'}]},
  ]};
  assert.deepEqual(compatibility(parsed(trace), manifest, plan), []);
  assert.deepEqual(compatibility(parsed({...trace,sceneHash:'corrupt'}), manifest, plan), ['sceneHash']);
  assert.deepEqual(compatibility(parsed({...trace,variant:'unknown'}), manifest, plan), ['variant']);
  assert.deepEqual(compatibility(parsed({...trace,variant:'short'}), manifest, plan), ['sceneHash']);
  assert.ok(diffTraces(parsed(trace), parsed({...trace,variant:'short'})).changes.some(change => change.startsWith('variant:')));
});

test('imports reject executable fields, wrong schema, cross-clock data and unsafe numeric times', () => {
  for (const mutate of [
    trace => {trace.commandUrl = 'https://device/raise';},
    trace => {trace.events[0].html = '<script>fetch()</script>';},
    trace => {trace.schemaVersion = 2;},
    trace => {trace.events[0].clockDomain = 'wall-clock';},
    trace => {trace.events[0].timeMs = '9007199254740992';},
    trace => {trace.events[1].timeMs = '99';},
    trace => {trace.events[1].sequence = 1;},
    trace => {trace.lastSequence = 5;},
    trace => {trace.slot = 7;},
  ]) { const trace = fixture(); mutate(trace); assert.throws(() => parsed(trace)); }
  assert.throws(() => parseTrace(' '.repeat(MAX_BYTES + 1)), /1 MiB/);
  const trace = fixture(); trace.events = Array.from({length:MAX_EVENTS + 1}, () => trace.events[0]);
  assert.throws(() => parsed(trace), /4096 events/);
});

test('baseline diff uses ordered significant outputs and relative device time, preserving incomplete status', () => {
  const baseline = parsed(fixture()), shifted = fixture();
  for (const event of shifted.events) { event.timeMs = String(Number(event.timeMs) + 500); event.dueAt = String(Number(event.dueAt) + 500); }
  assert.deepEqual(diffTraces(baseline, parsed(shifted)).changes, []);
  shifted.events[1].timeMs = '9601'; shifted.events[1].slot = 3; shifted.events[1].red = 64; shifted.events[1].reason = 'Stop'; shifted.droppedEvents = 1;
  const result = diffTraces(baseline, parsed(shifted));
  assert.equal(result.incomplete, true); assert.match(result.changes[0], /slot 3.*RGB input 64.*Stop/);
  const fullLevel = fixture(), dimmer = fixture();
  fullLevel.events[1].level = 64; dimmer.events[1].level = 32;
  assert.match(diffTraces(parsed(fullLevel), parsed(dimmer)).changes[0], /level 64.*level 32/);
});

test('Dance media clocks require one generation and never correlate with device-monotonic events', () => {
  const dance = fixture(); dance.sceneId = 'dance'; dance.slot = 0;
  dance.audioGeneration = 2; dance.audioPositionMs = 9100; dance.beatIndex = '18'; dance.track = 'synthetic.wav';
  for (const event of dance.events) { event.clockDomain = 'audio-media'; event.slot = 0; }
  const result = parsed(dance);
  assert.equal(result.audioGeneration, 2);
  const diff = diffTraces(parsed(fixture()), result);
  assert.match(diff.changes.at(-1), /cannot be correlated/);
  assert.equal(diff.unavailable, true); assert.equal(diff.incomplete, false);
  dance.events[1].clockDomain = 'device-monotonic';
  assert.throws(() => parsed(dance), /Mixed clock/);
  dance.events[1].clockDomain = 'audio-media'; delete dance.audioGeneration;
  assert.throws(() => parsed(dance), /generation identity/);
});

class Element {
  constructor() { this.children = []; this.value = ''; this.max = '0'; this.hidden = true; this.attributes = {}; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  setAttribute(key, value) { this.attributes[key] = value; }
  addEventListener(key, callback) { this[key] = callback; }
  showModal() { this.open = true; }
  close(value) { this.open = false; this.returnValue = value; this.closeEvent?.(); }
  focus() { this.focused = true; }
  set innerHTML(_) { throw new Error('Untrusted HTML rendering'); }
}
test('real panel handlers import safely, seek locally, display clock/missing evidence and restore focus', async () => {
  const elements = new Map();
  const get = id => { if (!elements.has(id)) elements.set(id, new Element()); return elements.get(id); };
  get('show-speed').value = '1';
  const document = {getElementById:get,createElement:() => new Element()};
  require('./ui_test_runtime.cjs')(globalThis);
  get('show-inspector').ownerDocument = document;
  get('show-inspector').addEventListener = function(key, callback) { this[key === 'close' ? 'closeEvent' : key] = callback; };
  get('show-open').isConnected = true;
  const plan = {semanticsVersion:'dv02-phase-1', scenes:[{sceneId:'legacy-presentation',sceneVersion:1,sceneHash:'main-hash',variant:'main',maxDurationMs:15000,phases:[{id:'prepare',label:'prepare',illustrativeStartMs:0,illustrativeDurationMs:100,completion:{kind:'elapsed',durationMs:100},cues:[{id:'led-0',kind:'led',timing:{offsetMs:0}}]},{id:'rest',illustrativeStartMs:100,illustrativeDurationMs:100,completion:{kind:'elapsed',durationMs:100},cues:[]}]}]};
  mount(document, plan, {bundleHash:'bundle',sourceHash:'source',compiledHash:'compiled'});
  assert.match(get('show-mode').textContent, /PLÁN/);
  get('show-open').onclick(); assert.equal(get('show-inspector').open, true); assert.equal(get('show-close').focused, true);
  const trace = fixture(); trace.events[0].reason = '<img src=x onerror=alert(1)>';
  const input = get('show-baseline'); input.files = [{size:1000,text:async () => JSON.stringify(trace)}];
  await input.onchange({target:input});
  assert.match(get('show-mode').textContent, /HISTORICKÝ/);
  assert.equal(input.value, '');
  assert.match(get('show-events').children[0].textContent, /<img src=x onerror=alert\(1\)>/);
  assert.match(get('show-events').children[0].textContent, /acceptedAt unknown.*measuredAt unknown/);
  assert.match(get('show-warning').textContent, /ČÁSTEČNÁ IDENTITA/);
  trace.sceneHash = 'main-hash'; trace.variant = 'main';
  await input.onchange({target:input});
  assert.match(get('show-warning').textContent, /Identita plánu odpovídá exportu/);
  assert.doesNotMatch(get('show-warning').textContent, /ČÁSTEČNÁ IDENTITA/);
  assert.match(get('show-identity').textContent, /variant main.*scene main-hash/);
  get('show-seek').value = '0'; get('show-seek').oninput(); assert.equal(get('show-events').children.length, 1);
  const previousFrame = globalThis.requestAnimationFrame, previousFetch = globalThis.fetch, previousSocket = globalThis.WebSocket;
  const frames = [];
  globalThis.requestAnimationFrame = callback => frames.push(callback);
  globalThis.fetch = globalThis.WebSocket = () => { throw new Error('Replay attempted a network/hardware action'); };
  try {
    get('show-speed').value = '4'; get('show-play').onclick();
    assert.equal(frames.length, 1);
    frames.shift()(performance.now() + 1000);
    assert.ok(Number(get('show-seek').value) >= 4000);
    get('show-play').onclick();
    assert.equal(get('show-play').textContent, 'Přehrát záznam');
    get('show-play').onclick();
    assert.equal(frames.length, 2);
    frames.shift()(performance.now() + 1100);
    assert.equal(frames.length, 1, 'old queued callback must not join the new playback');
    frames.shift()(performance.now() + 1200);
    assert.equal(frames.length, 1, 'only the current playback schedules a continuation');
    get('show-play').onclick();
  } finally {
    globalThis.requestAnimationFrame = previousFrame; globalThis.fetch = previousFetch; globalThis.WebSocket = previousSocket;
  }
  get('show-inspector').cancel({preventDefault(){}}); assert.equal(get('show-inspector').open, false); assert.equal(get('show-open').focused, true);
  get('show-reset').onclick(); assert.match(get('show-mode').textContent, /PLÁN/);
});

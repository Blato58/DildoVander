import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { loadBundle, loadFileBundle, loadPublishedBundle, validateManifest, validateModel, safeAssetPath, sha256, canonicalManifest } from '../src/features/workshop/model-bundle.mjs';
import { createViewerEngine, defaultSceneState, partVisible, movement, uploadGeometry } from '../src/features/workshop/engine.mjs';
import { mappingCompatible, projectObservation, validateObserverBase } from '../src/features/workshop/observation.mjs';
import { readModelBundle } from '../scripts/validate-model-bundle.mjs';
import { graphics } from '../src/features/workshop/graphics.mjs';
import { overlaySnapshot } from '../src/features/workshop/overlays.mjs';
import { richWorkshopModel } from './fixtures/workshop-rich.mjs';
import { safeModelResources, validateDistributionIndex } from '../src/features/workshop/downloads.mjs';
const fixtureRoot = new URL('../src/features/workshop/fixture/', import.meta.url);
const manifest = JSON.parse(await readFile(new URL('model-bundle.json', fixtureRoot), 'utf8'));
const bytes = new Uint8Array(await readFile(new URL('model.json', fixtureRoot)));
const model = JSON.parse(new TextDecoder().decode(bytes));
const clone = value => structuredClone(value);

test('fixture is an explicit dev/test choice and its hashes survive the release loader', async () => {
  assert.throws(() => validateManifest(manifest), /development\/test only/);
  const loaded = await loadBundle(manifest, async () => bytes, { allowFixture: true });
  assert.equal(loaded.model.meshes[0].id, 'ui_fixture_block');
  assert.equal(await sha256(bytes), manifest.assets[0].sha256);
  assert.equal(await sha256(new TextEncoder().encode(canonicalManifest(manifest))), manifest.bundleHash);
  assert.equal((await readModelBundle(fixtureRoot, { allowFixture: true })).manifest.bundleHash, manifest.bundleHash);
});
test('asset paths, budgets, versions, duplicates and integrity fail closed', async () => {
  for (const path of ['../model.json', '/model.json', 'C:/model.json', 'a\\b', 'a//b', 'a/%2e%2e/b', 'https://host/model.json', 'a/./b']) assert.throws(() => safeAssetPath(path));
  for (const mutate of [m => m.schemaVersion = 2, m => m.compatibility.viewerSchema = 20, m => m.assets.push(m.assets[0]), m => m.assets[0].bytes = 101_000_001, m => m.assets[0].sha256 = 'bad']) {
    const altered = clone(manifest); mutate(altered); assert.throws(() => validateManifest(altered, { allowFixture: true }));
  }
  const corrupt = bytes.slice(); corrupt[4] ^= 1;
  await assert.rejects(loadBundle(manifest, async () => corrupt, { allowFixture: true }), /integrity/);
  await assert.rejects(loadBundle(manifest, async () => bytes.slice(1), { allowFixture: true }), /integrity/);
  const forged = clone(manifest); forged.provenance.sourceRevision = 'different';
  await assert.rejects(loadBundle(forged, async () => bytes, { allowFixture: true }), /manifest integrity/);
});
test('local directory import validates inventory/hash and never interprets model metadata as code', async () => {
  const file = (name, contents) => ({ name, size: contents.length, webkitRelativePath: 'release/' + name, arrayBuffer: async () => contents.buffer.slice(contents.byteOffset, contents.byteOffset + contents.byteLength) });
  const files = [file('model-bundle.json', new TextEncoder().encode(JSON.stringify(manifest))), file('model.json', bytes)];
  assert.equal((await loadFileBundle(files, { allowFixture: true })).manifest.bundleHash, manifest.bundleHash);
  await assert.rejects(loadFileBundle(files), /development\/test only/);
  await assert.rejects(loadFileBundle([...files, file('application.js', new Uint8Array([1]))], { allowFixture: true }), /undeclared/);
  files[1].webkitRelativePath = 'release/../model.json';
  await assert.rejects(loadFileBundle(files, { allowFixture: true }), /unsafe asset/);
});
test('model semantic validation rejects identity, triangle, hierarchy and kinematic corruption', () => {
  for (const mutate of [m => m.modelId = 'other', m => m.meshes.push(clone(m.meshes[0])), m => m.meshes[0].triangles[0] = 999999, m => m.meshes[0].parentId = m.meshes[0].id, m => m.meshes[0].transform[12] = 100, m => m.mechanism.travel_degrees = 0, m => m.meshes[0].vertices[0] = NaN, m => m.meshes[0].bounds[3] = -1]) {
    const altered = clone(model); mutate(altered); assert.throws(() => validateModel(altered, manifest));
  }
});
test('published active reference pins model identity and bytes without any CAD source', async () => {
  const published = clone(manifest); published.provenance.kind = 'cad-export';
  published.bundleHash = await sha256(new TextEncoder().encode(canonicalManifest(published)));
  const active = { schemaVersion: 1, modelId: published.modelId, bundleHash: published.bundleHash, manifestPath: `bundles/${published.bundleHash}/model-bundle.json` };
  const requested = [];
  const fetcher = async (url, options) => {
    requested.push(String(url)); assert.equal(options.credentials, 'omit'); assert.equal(options.redirect, 'error');
    return new Response(String(url).endsWith('active.json') ? JSON.stringify(active) : String(url).endsWith('model-bundle.json') ? JSON.stringify(published) : bytes);
  };
  assert.equal((await loadPublishedBundle('https://model.example/models/', { fetcher })).model.modelId, model.modelId);
  assert.equal(requested.length, 3);
  active.modelId = 'different';
  await assert.rejects(loadPublishedBundle('https://model.example/models/', { fetcher }), /identity mismatch/);
});
test('verified provenance requires a matching successful report tied to exact model/source identity', async () => {
  const report = { result: 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE', physical_test_performed: false, sources: { 'MONT\u00c1\u017d.md': 'a'.repeat(64) }, modelEvidence: { schemaVersion: 1, modelId: manifest.modelId, modelAssetSha256: manifest.assets[0].sha256, sourceRevision: manifest.provenance.sourceRevision, sourceHashes: { 'MONT\u00c1\u017d.md': 'a'.repeat(64) } } };
  async function loadReport(value) {
    const proof = new TextEncoder().encode(JSON.stringify(value));
    const published = clone(manifest); published.provenance = { ...published.provenance, kind: 'cad-export', cadVerification: 'verified' };
    published.assets.push({ path: 'cad-verification.json', kind: 'verification-json', bytes: proof.length, sha256: await sha256(proof) });
    published.bundleHash = await sha256(new TextEncoder().encode(canonicalManifest(published)));
    return loadBundle(published, async path => path === 'model.json' ? bytes : proof);
  }
  assert.equal((await loadReport(report)).manifest.provenance.cadVerification, 'verified');
  for (const mutate of [r => r.result = 'FAIL', r => r.physical_test_performed = true, r => r.modelEvidence.modelAssetSha256 = 'b'.repeat(64), r => r.modelEvidence.sourceHashes = {}, r => r.sources['MONT\u00c1\u017d.md'] = 'b'.repeat(64)]) {
    const altered = clone(report); mutate(altered); await assert.rejects(loadReport(altered), /CAD/);
  }
  for (const source of ['../MONT\u00c1\u017d.md', '/MONT\u00c1\u017d.md', 'a//b.md', 'a\\b.md', 'a/<b>.md', 'MONT.md\n']) {
    const altered = clone(report);
    altered.sources = altered.modelEvidence.sourceHashes = { [source]: 'a'.repeat(64) };
    await assert.rejects(loadReport(altered), /source evidence path/);
  }
  const flipped = clone(manifest); flipped.provenance = { ...flipped.provenance, kind: 'cad-export', cadVerification: 'verified' };
  assert.throws(() => validateManifest(flipped), /certificate count/);
});
test('download links bind immutable data identity and distinguish historical archives without executable URLs', () => {
  const published = clone(manifest); published.provenance.kind = 'cad-export';
  const origin = 'https://app.example';
  const bundle = { manifest: published, resourceManifestUrl: `${origin}/models/bundles/${published.bundleHash}/model-bundle.json` };
  assert.equal(safeModelResources(bundle, origin).length, 2);
  assert.equal(safeModelResources({ ...bundle, resourceManifestUrl: 'https://attacker.example/manifest.json' }, origin).length, 0);
  const item = { kind: 'manufacturing', modelId: published.modelId, modelAssetSha256: published.assets[0].sha256, releaseId: 'b'.repeat(64), sha256: 'b'.repeat(64), label: 'Archiv', version: 'F2', path: `releases/${'b'.repeat(64)}/F2.zip`, bytes: 1000, cadVerification: 'verified', physicalAcceptance: false };
  const items = validateDistributionIndex({ schemaVersion: 1, artifacts: [item, { ...item, modelAssetSha256: null, label: 'Původní archiv' }] }, published, origin);
  assert.equal(items.length, 2); assert.equal(items[0].historical, false); assert.equal(items[1].historical, true);
  assert.equal(new URL(items[0].href).origin, origin);
  for (const mutate of [i => i.path = 'https://attacker.example/a.zip', i => i.path = `releases/${i.releaseId}/x.html`, i => i.path = `releases/${i.releaseId}/x.svg`, i => i.releaseId = 'c'.repeat(64), i => i.physicalAcceptance = true, i => i.cadVerification = 'not-verified']) {
    const altered = clone(item); mutate(altered); assert.throws(() => validateDistributionIndex({ schemaVersion: 1, artifacts: [altered] }, published, origin));
  }
  assert.deepEqual(validateDistributionIndex({ schemaVersion: 1, artifacts: [item] }, manifest, origin), []);
});
test('GPU expansion preserves facets, model input and stable selection independent of mesh ordering', () => {
  const original = JSON.stringify(model); const packed = uploadGeometry(model.meshes[0]);
  assert.equal(packed.length, model.meshes[0].triangles.length * 6);
  assert.ok([...packed].every(Number.isFinite)); assert.equal(JSON.stringify(model), original);
  const flat = clone(model.meshes[0]); flat.triangles = [0, 0, 0]; assert.equal(uploadGeometry(flat).length, 0);
});
test('visibility recovery respects assembly isolation and operation/transport semantic parts', () => {
  const state = defaultSceneState(), mesh = model.meshes[0];
  assert.equal(partVisible(mesh, state, model.assemblyGuide), true);
  state.hidden.push(mesh.id); assert.equal(partVisible(mesh, state, model.assemblyGuide), false);
  state.hidden = []; state.mode = 'assembly'; state.assemblyIndex = 1;
  assert.equal(partVisible(mesh, state, model.assemblyGuide), true);
  state.assemblyIndex = 0; assert.equal(partVisible(mesh, state, model.assemblyGuide), false);
  state.mode = 'operation'; const tray = { ...mesh, name: 'tray_1' }; state.angles[0] = 160;
  assert.equal(movement(tray, model, state)[14], 16);
});
function envelope() {
  return { status: 'online', ageMs: 0, receivedAtUtc: '2026-10-19T00:00:00Z', snapshot: { schemaVersion: 1, source: { instanceId: 'sim', deviceId: 'sim', bootId: 'boot', mode: 'simulation', cadManifestRevision: 'cad-1', hardwareProfile: 'B-effects' }, sequence: '1', timeMs: '0', revision: '1', reported: { physicalVerification: 'unavailable', servo: [{ slot: 1, estimate: 'rest' }], ledGrb: [10,20,30] }, model: { evidence: 'simulated', slots: [{ slot: 1, position: .5, jammed: false }] }, trace: { events: [] } } };
}
test('incompatible and stale telemetry disables mapping; compatible replay stays historical', () => {
  const runtimeModel = clone(model); runtimeModel.meshes[0].name = 'tray_1';
  runtimeModel.twinManifest = { revision: 'cad-1', hardwareProfile: 'B-effects', kinematics: { liftMaxMm: 16, angleMinDegrees: 0, angleMaxDegrees: 160, samples: [{ angleDegrees: 0, liftMm: 0 }, { angleDegrees: 80, liftMm: 8 }, { angleDegrees: 160, liftMm: 16 }] }, slots: [] };
  const value = envelope(); assert.equal(mappingCompatible(runtimeModel, value.snapshot), true);
  assert.equal(projectObservation(runtimeModel, value, 0, 0).angles[0], 80);
  assert.equal(projectObservation(runtimeModel, value, 3000, 0).angles[0], null);
  assert.equal(projectObservation(runtimeModel, value, 3000, 0, true).status, 'replay');
  value.snapshot.source.cadManifestRevision = 'unknown';
  assert.equal(projectObservation(runtimeModel, value, 0, 0).angles[0], null);
  assert.deepEqual(projectObservation(runtimeModel, value, 0, 0).uncertain, [runtimeModel.meshes[0].id]);
});
test('observer destinations reject credentials and active schemes', () => {
  assert.equal(validateObserverBase('http://127.0.0.1:8080'), 'http://127.0.0.1:8080');
  for (const value of ['javascript:alert(1)', 'http://remote.example', 'https://secret@server', 'https://server?token=private', 'https://server/path']) assert.throws(() => validateObserverBase(value));
});
test('renderer disposes GPU resources, animation, resize and pointer handlers exactly once', () => {
  const previous = Object.fromEntries(['requestAnimationFrame', 'cancelAnimationFrame', 'ResizeObserver'].map(key => [key, globalThis[key]]));
  const calls = [], frames = new Map(); let nextFrame = 0;
  globalThis.requestAnimationFrame = fn => { frames.set(++nextFrame, fn); return nextFrame; };
  globalThis.cancelAnimationFrame = id => frames.delete(id);
  globalThis.ResizeObserver = class { observe() {} disconnect() { calls.push('disconnect'); } };
  const events = new Map();
  const gl = new Proxy({}, { get(_, name) {
    if (name === 'getShaderParameter' || name === 'getProgramParameter') return () => true;
    if (name === 'getAttribLocation') return () => 0;
    if (String(name).startsWith('create') || name === 'getUniformLocation') return () => ({});
    if (String(name).toUpperCase() === name) return 1;
    return () => calls.push(String(name));
  } });
  const canvas = { getContext: () => gl, addEventListener: (name, handler) => events.set(name, handler), removeEventListener: name => events.delete(name), hasPointerCapture: () => false, clientWidth: 800, clientHeight: 600, width: 0, height: 0 };
  try {
    const renderer = createViewerEngine(canvas, model);
    renderer.update(defaultSceneState()); assert.equal(frames.size, 1);
    for (const [id, callback] of frames) { frames.delete(id); callback(); }
    assert.ok(calls.includes('drawArrays')); renderer.dispose(); renderer.dispose();
    assert.equal(calls.filter(name => name === 'deleteBuffer').length, model.meshes.length + model.routes.length);
    assert.equal(calls.filter(name => name === 'deleteProgram').length, 1);
    assert.equal(calls.filter(name => name === 'deleteFramebuffer').length, 1);
    assert.equal(calls.filter(name => name === 'disconnect').length, 1);
    assert.equal(events.size, 0); assert.equal(frames.size, 0);
  } finally { for (const [key, value] of Object.entries(previous)) globalThis[key] = value; }
});
test('extracted legacy cam polynomial, lid pivot and dynamic cable law preserve exact known values', () => {
  const rich = richWorkshopModel(model), state = defaultSceneState();
  const original = JSON.stringify(rich); state.angles[0] = 80;
  assert.deepEqual([20,50,80,110,140].map(angle => graphics.lift(angle, rich)), [0,1.65625,8,14.34375,16]);
  const lid = rich.meshes.find(mesh => mesh.name === 'lid_panel');
  const pivot = graphics.transform(graphics.movement(lid, rich, state), [...rich.hinge,1]);
  assert.ok(pivot.every((value, index) => Math.abs(value - [...rich.hinge,1][index]) < 1e-10));
  const lift = graphics.routePoints(rich.routes[1], rich, state);
  assert.deepEqual(lift.map(point => point[2]), [30,32,36,38]);
  assert.equal(graphics.routePoints(rich.routes[2], rich, state).at(-1)[2], 38);
  state.mode = 'transport'; assert.deepEqual(graphics.routePoints(rich.routes[0], rich, state), rich.routes[0].transport_points);
  assert.equal(graphics.physicalEndpoint(rich.routes[0], 'from', state), 'parked:USB');
  assert.equal(JSON.stringify(rich), original, 'projection never mutates immutable model data');
});
test('original fit and projection frame transformed geometry in each retained camera at desktop/mobile widths', () => {
  const rich = richWorkshopModel(model), state = defaultSceneState(); state.case = false; state.angles[0] = 160;
  for (const width of [1440,390,320]) for (const preset of Object.values(graphics.cameraViews)) {
    const [yaw,pitch] = preset.angles, visible = rich.meshes.filter(mesh => partVisible(mesh,state,rich.assemblyGuide));
    const fitted = graphics.fitScene(visible,rich,state,yaw,pitch,width,600);
    const eye = [fitted.target[0]+fitted.distance*Math.cos(pitch)*Math.cos(yaw),fitted.target[1]+fitted.distance*Math.cos(pitch)*Math.sin(yaw),fitted.target[2]+fitted.distance*Math.sin(pitch)];
    const view = graphics.look(eye,fitted.target), projection = graphics.perspective(width/600,fitted.distance,430);
    for (const point of graphics.corners(visible,rich,state)) {
      const screen = graphics.projectPoint(point,projection,view,width,600);
      assert.ok(screen && screen[0] >= 0 && screen[0] <= width && screen[1] >= 0 && screen[1] <= 600, `${preset.label} width=${width}`);
    }
  }
});
test('Vue overlay snapshots preserve original routes, pins, flexible ends, dimensions and orientation', () => {
  const rich = richWorkshopModel(model), state = defaultSceneState();
  state.case = false; state.wires = true; state.inspect = true; state.dimensions = true; state.selectedWire = 'fixture-lift'; state.wireScope = 'bundle';
  const [yaw,pitch] = graphics.cameraViews.overview.angles;
  const fitted = graphics.fitScene(rich.meshes,rich,state,yaw,pitch,1440,900);
  const eye = [fitted.target[0]+fitted.distance*Math.cos(pitch)*Math.cos(yaw),fitted.target[1]+fitted.distance*Math.cos(pitch)*Math.sin(yaw),fitted.target[2]+fitted.distance*Math.sin(pitch)];
  const overlay = overlaySnapshot(rich,state,rich.meshes,graphics.perspective(1440/900,fitted.distance,430),graphics.look(eye,fitted.target),1440,900);
  assert.equal(overlay.routes.length,3); assert.equal(overlay.routes.filter(route => route.active).length,2);
  assert.equal(overlay.pins.length,6); assert.equal(overlay.flex.length,1); assert.equal(overlay.dimensions.length,3); assert.equal(overlay.orientation.length,4);
  state.mode = 'assembly'; state.assemblyIndex = 0;
  assert.equal(overlaySnapshot(rich,state,rich.meshes,graphics.perspective(1440/900,fitted.distance,430),graphics.look(eye,fitted.target),1440,900).routes.length,0);
});

test('electrical route polarity survives validation without widening resource identifiers', () => {
  const candidate = richWorkshopModel(model);
  candidate.routes[0].transport_points = null;
  candidate.routes[0].disconnected_points = null;
  for (const id of ['P12+', 'F_LED+-01', 'S1-+', 'P12-']) {
    candidate.routes[0].id = id;
    assert.equal(validateModel(candidate, manifest), candidate);
  }
  for (const id of ['P12/+', '<P12+>', 'P12+\\escape', 'P12+\n', null, 12]) {
    candidate.routes[0].id = id;
    assert.throws(() => validateModel(candidate, manifest), /invalid cable route/);
  }
  candidate.routes[0].id = 'P12+';
  for (const value of [[], [[0, 0, 0]], 'missing']) {
    candidate.routes[0].transport_points = value;
    assert.throws(() => validateModel(candidate, manifest), /invalid parked cable route/);
  }
  assert.throws(() => safeAssetPath('P12+.json'), /unsafe asset path/);
});

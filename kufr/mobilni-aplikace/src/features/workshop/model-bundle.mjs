/** ModelBundle v1. Data only: no CAD modules, DOM or network side effects on import. */
export const MAX_MANIFEST_BYTES = 128 * 1024;
export const MAX_MODEL_BYTES = 96 * 1024 * 1024;
export const VIEWER_VERSION = 1;
const hashPattern = /^[a-f0-9]{64}$/;
const idPattern = /^[a-zA-Z0-9][a-zA-Z0-9_.:-]{0,159}$/;
const fail = message => { throw new Error(`ModelBundle: ${message}`); };
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
const vector = (value, length) => Array.isArray(value) && value.length === length && value.every(Number.isFinite);
function fields(value, allowed) {
  if (Object.keys(value).some(key => !allowed.includes(key))) fail('unknown manifest field');
}
function text(value, label, max = 1000) {
  if (typeof value !== 'string' || !value.length || value.length > max || /[\u0000-\u001f]/.test(value)) fail(`invalid ${label}`);
  return value;
}
export function safeAssetPath(value) {
  text(value, 'asset path', 240);
  if (!/^[a-zA-Z0-9_-]+(?:[.][a-zA-Z0-9_-]+)*(?:\/[a-zA-Z0-9_-]+(?:[.][a-zA-Z0-9_-]+)*)*$/.test(value)) fail('unsafe asset path');
  return value;
}
export function validateManifest(manifest, { allowFixture = false } = {}) {
  if (!record(manifest) || manifest.schemaVersion !== 1) fail('unsupported schema version');
  fields(manifest, ['schemaVersion', 'modelId', 'revision', 'bundleHash', 'units', 'coordinates', 'compatibility', 'provenance', 'assets']);
  for (const key of ['modelId', 'revision']) if (!idPattern.test(manifest[key])) fail(`invalid ${key}`);
  if (!hashPattern.test(manifest.bundleHash)) fail('invalid bundle hash');
  if (manifest.units !== 'mm' || manifest.coordinates !== 'right-handed-z-up') fail('unsupported coordinates');
  if (!record(manifest.compatibility) || manifest.compatibility.viewerSchema !== VIEWER_VERSION) fail('incompatible viewer schema');
  fields(manifest.compatibility, ['viewerSchema']);
  if (!record(manifest.provenance) || !['cad-export', 'synthetic-fixture'].includes(manifest.provenance.kind)) fail('missing provenance');
  fields(manifest.provenance, ['kind', 'sourceRevision', 'cadVerification']);
  text(manifest.provenance.sourceRevision, 'model source revision', 160);
  if (!['not-verified', 'verified'].includes(manifest.provenance.cadVerification)) fail('invalid CAD verification');
  if (manifest.provenance.kind === 'synthetic-fixture' && (!allowFixture || manifest.provenance.cadVerification !== 'not-verified')) fail('fixture models are development/test only');
  if (!Array.isArray(manifest.assets) || manifest.assets.length < 1 || manifest.assets.length > 32) fail('invalid assets');
  const paths = new Set(); let total = 0;
  for (const asset of manifest.assets) {
    if (!record(asset)) fail('invalid asset');
    fields(asset, ['path', 'sha256', 'bytes', 'kind']);
    safeAssetPath(asset.path);
    if (paths.has(asset.path.toLowerCase())) fail('duplicate asset path');
    paths.add(asset.path.toLowerCase());
    if (!hashPattern.test(asset.sha256) || !Number.isSafeInteger(asset.bytes) || asset.bytes < 1) fail('invalid asset integrity');
    total += asset.bytes;
    if (total > MAX_MODEL_BYTES) fail('model exceeds size limit');
    if (!['model-json', 'bom-json', 'verification-json'].includes(asset.kind)) fail('unsupported asset kind');
  }
  const models = manifest.assets.filter(asset => asset.kind === 'model-json');
  if (models.length !== 1) fail('exactly one model asset is required');
  if (manifest.assets.filter(asset => asset.kind === 'verification-json').length !== (manifest.provenance.cadVerification === 'verified' ? 1 : 0)) fail('CAD verification certificate count disagrees with provenance');
  return manifest;
}

function validateMesh(mesh, names) {
  if (!record(mesh) || !idPattern.test(mesh.id) || !idPattern.test(mesh.name) || names.has(mesh.id)) fail('invalid/duplicate stable part id');
  names.add(mesh.id);
  text(mesh.group, 'part group', 100);
  if (!vector(mesh.color, 3) || mesh.color.some(v => v < 0 || v > 1) || !vector(mesh.bounds, 6)) fail('invalid part appearance/bounds');
  if ([0, 1, 2].some(i => mesh.bounds[i] > mesh.bounds[i + 3])) fail('inverted part bounds');
  if (!vector(mesh.transform, 16) || mesh.transform.some((v, i) => v !== [1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1][i])) fail('v1 tessellations require identity transforms in global coordinates');
  if (mesh.parentId !== null) fail('v1 requires null parent ids and global-coordinate tessellations; local hierarchy transforms are unsupported');
  if (!Array.isArray(mesh.vertices) || !Array.isArray(mesh.triangles) || !mesh.vertices.length || !mesh.triangles.length || mesh.vertices.length % 3 || mesh.triangles.length % 3 || mesh.vertices.length > 9_000_000 || mesh.triangles.length > 9_000_000) fail('invalid geometry');
  if (!mesh.vertices.every(Number.isFinite) || !mesh.triangles.every(i => Number.isInteger(i) && i >= 0 && i < mesh.vertices.length / 3)) fail('invalid triangle index/vertex');
  for (let i = 0; i < mesh.vertices.length; i++) {
    const axis = i % 3;
    if (mesh.vertices[i] < mesh.bounds[axis] - .01 || mesh.vertices[i] > mesh.bounds[axis + 3] + .01) fail('vertices outside declared bounds');
  }
}
export function validateModel(model, manifest) {
  if (!record(model) || model.modelId !== manifest.modelId || model.revision !== manifest.revision || !Array.isArray(model.meshes) || !model.meshes.length || model.meshes.length > 10000) fail('invalid model identity/parts');
  const ids = new Set(); const names = new Set();
  for (const mesh of model.meshes) {
    validateMesh(mesh, ids);
    if (names.has(mesh.name)) fail('duplicate part name');
    names.add(mesh.name);
  }
  const parents = new Map(model.meshes.map(mesh => [mesh.id, mesh.parentId]));
  for (const mesh of model.meshes) {
    const seen = new Set([mesh.id]); let parent = mesh.parentId;
    while (parent !== null) {
      if (!parents.has(parent) || seen.has(parent)) fail('invalid/cyclic hierarchy');
      seen.add(parent); parent = parents.get(parent);
    }
  }
  if (!vector(model.hinge, 3) || !record(model.mechanism)) fail('missing kinematics');
  for (const key of ['origin_y', 'origin_z', 'cam_y', 'cam_z', 'dwell_degrees', 'travel_degrees', 'lift']) if (!Number.isFinite(model.mechanism[key])) fail('invalid kinematics');
  if (model.mechanism.travel_degrees <= model.mechanism.dwell_degrees * 2 || model.mechanism.dwell_degrees < 0 || model.mechanism.lift < 0 || !Number.isInteger(model.mechanism.count) || model.mechanism.count < 1 || model.mechanism.count > 6) fail('invalid kinematic range');
  if (!record(model.assemblyGuide) || !Array.isArray(model.assemblyGuide.steps) || model.assemblyGuide.steps.length > 500) fail('invalid assembly guide');
  const stepIds = new Set();
  for (const step of model.assemblyGuide.steps) {
    if (!record(step) || !idPattern.test(step.id) || stepIds.has(step.id)) fail('invalid assembly step id');
    stepIds.add(step.id); text(step.title, 'assembly step title');
    for (const key of ['parts', 'caseParts', 'benchParts', 'preparedParts']) if (step[key] !== undefined && (!Array.isArray(step[key]) || !step[key].every(p => typeof p === 'string' && /^[a-zA-Z0-9_*.-]{1,160}$/.test(p)))) fail('invalid assembly parts');
  }
  for (const key of ['routes', 'sources', 'connections', 'bomReferences']) if (!Array.isArray(model[key]) || model[key].length > 20000) fail(`invalid ${key}`);
  const routeIds = new Set();
  const polyline = points => Array.isArray(points) && points.length >= 2 && points.length <= 10000 && points.every(p => vector(p, 3));
  for (const route of model.routes) {
    if (!record(route) || !idPattern.test(route.id) || routeIds.has(route.id) || !polyline(route.points)) fail('invalid cable route');
    routeIds.add(route.id); text(route.from, 'route origin', 160); text(route.to, 'route destination', 160);
    for (const key of ['transport_points', 'disconnected_points']) if (route[key] !== undefined && !polyline(route[key])) fail('invalid parked cable route');
    if (route.point_lift_weights !== undefined) {
      if (!record(route.point_lift_weights) || Object.entries(route.point_lift_weights).some(([slot, weights]) => !/^[1-6]$/.test(slot) || !Array.isArray(weights) || weights.length !== route.points.length || !weights.every(value => Number.isFinite(value) && value >= 0 && value <= 1))) fail('invalid flexible cable weights');
    }
    if (route.moving_points !== undefined && (!record(route.moving_points) || Object.entries(route.moving_points).some(([index, endpoint]) => !/^\d{1,5}$/.test(index) || Number(index) >= route.points.length || typeof endpoint !== 'string' || endpoint.length > 160))) fail('invalid moving cable endpoint');
    if (route.moving_endpoints !== undefined && (!Array.isArray(route.moving_endpoints) || route.moving_endpoints.length > 32 || route.moving_endpoints.some(entry => !record(entry) || typeof entry.endpoint !== 'string' || ![route.from, route.to].includes(entry.endpoint)))) fail('invalid flexible endpoint marker');
  }
  if (!record(model.presentation) || !vector(model.presentation.target, 3)) fail('invalid presentation metadata');
  if (model.twinManifest !== null) validateTelemetryMapping(model.twinManifest, names);
  return model;
}

function validateTelemetryMapping(mapping, names) {
  if (!record(mapping) || !hashPattern.test(mapping.revision) || mapping.schemaVersion !== 1 || mapping.hardwareProfile !== 'B-effects' || !Array.isArray(mapping.slots) || mapping.slots.length > 6 || !record(mapping.kinematics)) fail('invalid telemetry mapping');
  const used = new Set();
  for (const entry of mapping.slots) {
    if (!record(entry) || !Number.isInteger(entry.slot) || entry.slot < 1 || entry.slot > 6 || used.has(entry.slot)) fail('invalid telemetry slot');
    used.add(entry.slot);
    if (!record(entry.components) || !Object.values(entry.components).every(name => names.has(name))) fail('unmapped telemetry component');
    for (const key of ['liftComponents', 'rotationComponents']) if (!Array.isArray(entry[key]) || !entry[key].every(name => names.has(name))) fail('unmapped moving component');
    const channel = entry.channels;
    if (!record(channel) || !['servoPca', 'vibrationPca', 'ledStart', 'ledCount'].every(key => Number.isInteger(channel[key]) && channel[key] >= 0) || channel.ledCount < 1 || channel.ledStart + channel.ledCount > 36 || channel.servoPca > 15 || channel.vibrationPca > 15) fail('invalid telemetry channel');
  }
  const kinematics = mapping.kinematics;
  for (const key of ['liftMaxMm', 'angleMinDegrees', 'angleMaxDegrees']) if (!Number.isFinite(kinematics[key])) fail('invalid telemetry kinematics');
  if (kinematics.liftMaxMm <= 0 || kinematics.angleMaxDegrees <= kinematics.angleMinDegrees || !Array.isArray(kinematics.samples) || kinematics.samples.length < 2 || kinematics.samples.length > 2000) fail('invalid telemetry law');
  let lastAngle = -Infinity, lastLift = -Infinity;
  for (const sample of kinematics.samples) {
    if (!record(sample) || !Number.isFinite(sample.angleDegrees) || !Number.isFinite(sample.liftMm) || sample.angleDegrees <= lastAngle || sample.liftMm < lastLift) fail('non-monotonic telemetry law');
    lastAngle = sample.angleDegrees; lastLift = sample.liftMm;
  }
  if (Math.abs(kinematics.samples[0].liftMm) > .001 || Math.abs(lastLift - kinematics.liftMaxMm) > .001) fail('incomplete telemetry law');
}

export async function sha256(bytes) {
  const digest = await globalThis.crypto.subtle.digest('SHA-256', bytes);
  return [...new Uint8Array(digest)].map(v => v.toString(16).padStart(2, '0')).join('');
}
export function canonicalManifest(manifest) {
  const ordered = value => Array.isArray(value) ? value.map(ordered) : record(value) ? Object.fromEntries(Object.keys(value).sort().map(key => [key, ordered(value[key])])) : value;
  const { bundleHash, ...identity } = manifest;
  return JSON.stringify(ordered(identity));
}
async function boundedBytes(response, max, signal) {
  if (!response.ok) fail(`asset unavailable (HTTP ${response.status})`);
  if (Number(response.headers.get('content-length')) > max) fail('response exceeds size limit');
  const reader = response.body?.getReader();
  if (!reader) { const bytes = new Uint8Array(await response.arrayBuffer()); if (bytes.length > max) fail('response exceeds size limit'); return bytes; }
  const chunks = []; let length = 0;
  try {
    for (;;) {
      signal?.throwIfAborted();
      const { value, done } = await reader.read(); if (done) break;
      length += value.length; if (length > max) fail('response exceeds size limit'); chunks.push(value);
    }
  } finally { await reader.cancel().catch(() => {}); }
  const bytes = new Uint8Array(length); let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  return bytes;
}
function parseJson(bytes) {
  try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); } catch { fail('invalid JSON/UTF-8'); }
}
/** readAsset is also implemented by the offline packager; all entry points share validation. */
export async function loadBundle(manifest, readAsset, options = {}) {
  validateManifest(manifest, options); let model, certificate;
  if (await sha256(new TextEncoder().encode(canonicalManifest(manifest))) !== manifest.bundleHash) fail('manifest integrity failure');
  for (const asset of manifest.assets) {
    const bytes = await readAsset(asset.path, asset.bytes);
    if (!(bytes instanceof Uint8Array) || bytes.length !== asset.bytes || await sha256(bytes) !== asset.sha256) fail(`integrity failure: ${asset.path}`);
    if (asset.kind === 'model-json') model = parseJson(bytes);
    if (asset.kind === 'verification-json') certificate = parseJson(bytes);
  }
  validateModel(model, manifest);
  if (manifest.provenance.cadVerification === 'verified') {
    const evidence = certificate?.modelEvidence, modelAsset = manifest.assets.find(asset => asset.kind === 'model-json');
    if (!record(certificate) || certificate.result !== 'PASS_CAD_NOT_PHYSICAL_ACCEPTANCE' || certificate.physical_test_performed !== false || !record(evidence) || evidence.schemaVersion !== 1 || evidence.modelId !== manifest.modelId || evidence.modelAssetSha256 !== modelAsset.sha256 || evidence.sourceRevision !== manifest.provenance.sourceRevision || !record(evidence.sourceHashes) || !Object.keys(evidence.sourceHashes).length || Object.keys(evidence.sourceHashes).length > 512 || !record(certificate.sources)) fail('invalid CAD verification certificate');
    for (const [source, hash] of Object.entries(evidence.sourceHashes)) {
      safeAssetPath(source);
      if (!hashPattern.test(hash) || certificate.sources[source] !== hash) fail('CAD report source evidence disagrees');
    }
  }
  return { manifest, model };
}
/** Directory uploads stay in memory; no HTML, script, zip expansion or publication. */
export async function loadFileBundle(files, options = {}) {
  if (!Array.isArray(files) || files.length < 2 || files.length > 33) fail('invalid model directory inventory');
  const prefix = files[0].webkitRelativePath?.split('/')[0] + '/';
  const assets = new Map(); let total = 0;
  for (const file of files) {
    const source = file.webkitRelativePath || file.name;
    const relative = file.webkitRelativePath ? source.startsWith(prefix) ? source.slice(prefix.length) : fail('mixed model directories') : source;
    safeAssetPath(relative);
    if (assets.has(relative.toLowerCase()) || !Number.isSafeInteger(file.size) || file.size < 1) fail('invalid model file');
    total += file.size; if (total > MAX_MODEL_BYTES + MAX_MANIFEST_BYTES) fail('model directory exceeds size limit');
    assets.set(relative.toLowerCase(), file);
  }
  const entry = assets.get('model-bundle.json');
  if (!entry || entry.size > MAX_MANIFEST_BYTES) fail('missing/oversized manifest');
  const manifest = parseJson(new Uint8Array(await entry.arrayBuffer()));
  validateManifest(manifest, options);
  if (assets.size !== manifest.assets.length + 1) fail('undeclared model files');
  return loadBundle(manifest, async (path, max) => {
    const file = assets.get(path.toLowerCase());
    if (!file || file.size !== max) fail('missing/oversized model asset');
    return new Uint8Array(await file.arrayBuffer());
  }, options);
}
/** @param {string} base @param {{signal?: AbortSignal, fetcher?: typeof fetch}} options */
export async function loadPublishedBundle(base = '/models/', { signal, fetcher = globalThis.fetch } = {}) {
  const baseUrl = new URL(base, globalThis.location?.href || 'https://localhost/');
  const controller = new AbortController();
  const abort = () => controller.abort(signal?.reason);
  signal?.throwIfAborted(); signal?.addEventListener('abort', abort, { once: true });
  const deadline = setTimeout(() => controller.abort(new Error('Model load exceeded 30 seconds')), 30000);
  const request = async (url, max) => {
    const response = await fetcher(url, { signal: controller.signal, credentials: 'omit', redirect: 'error', cache: 'no-cache' });
    return boundedBytes(response, max, controller.signal);
  };
  try {
    const active = parseJson(await request(new URL('active.json', baseUrl), MAX_MANIFEST_BYTES));
    if (!record(active) || active.schemaVersion !== 1 || !idPattern.test(active.modelId) || !hashPattern.test(active.bundleHash)) fail('invalid active reference');
    safeAssetPath(active.manifestPath);
    if (active.manifestPath !== `bundles/${active.bundleHash}/model-bundle.json`) fail('invalid immutable manifest path');
    const manifestUrl = new URL(active.manifestPath, baseUrl);
    const manifest = parseJson(await request(manifestUrl, MAX_MANIFEST_BYTES));
    if (manifest.modelId !== active.modelId || manifest.bundleHash !== active.bundleHash) fail('active reference identity mismatch');
    validateManifest(manifest);
    const bundle = await loadBundle(manifest, (path, max) => request(new URL(path, manifestUrl), max));
    return { ...bundle, resourceManifestUrl: manifestUrl.href };
  } finally { clearTimeout(deadline); signal?.removeEventListener('abort', abort); }
}

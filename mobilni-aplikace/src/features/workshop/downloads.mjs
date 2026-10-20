const hash = /^[a-f0-9]{64}$/;
const record = value => value !== null && typeof value === 'object' && !Array.isArray(value);
export function safeModelResources(bundle, origin) {
  if (!bundle.resourceManifestUrl || bundle.manifest.provenance.kind === 'synthetic-fixture') return [];
  const url = new URL(bundle.resourceManifestUrl);
  if (url.origin !== origin || url.pathname !== `/models/bundles/${bundle.manifest.bundleHash}/model-bundle.json` || url.username || url.password || url.search || url.hash) return [];
  return [
    { label: 'Manifest ModelBundle', href: url.href, sha256: bundle.manifest.bundleHash, bytes: null, canonical: true },
    ...bundle.manifest.assets.map(asset => ({ label: asset.kind === 'model-json' ? 'Data 3D modelu' : asset.kind === 'verification-json' ? 'Doklad CAD ověření' : 'Kusovníkové podklady', href: new URL(asset.path, url).href, sha256: asset.sha256, bytes: asset.bytes, canonical: false })),
  ];
}
export function validateDistributionIndex(index, manifest, origin) {
  if (!record(index) || index.schemaVersion !== 1 || !Array.isArray(index.artifacts) || index.artifacts.length > 256) throw new Error('Nepodporovaný přehled distribučních souborů.');
  const entries = [];
  for (const item of index.artifacts) {
    const historical = item?.kind === 'manufacturing' && item.modelAssetSha256 === null;
    if (!record(item) || !['model-archive','offline-viewer','manufacturing'].includes(item.kind) || !hash.test(item.releaseId) || !hash.test(item.sha256) || item.releaseId !== item.sha256 || !historical && !hash.test(item.modelAssetSha256) || !Number.isSafeInteger(item.bytes) || item.bytes <= 0 || item.bytes > 2_147_483_648 || item.physicalAcceptance !== false || !['verified','not-verified'].includes(item.cadVerification)) throw new Error('Neplatná distribuční metadata.');
    for (const key of ['label','version','modelId']) if (typeof item[key] !== 'string' || !item[key].length || item[key].length > 160 || /[\u0000-\u001f]/.test(item[key])) throw new Error('Neplatný popis distribuce.');
    if (typeof item.path !== 'string' || !new RegExp(`^releases/${item.releaseId}/[a-zA-Z0-9_-]+(?:[.][a-zA-Z0-9_-]+)*[.](zip|pdf|step|stp|stl|3mf|dxf|txt)$`, 'i').test(item.path)) throw new Error('Neplatná cesta distribuce.');
    if (item.bundleHash !== undefined && !hash.test(item.bundleHash)) throw new Error('Neplatná identita distribučního modelu.');
    if (item.kind === 'manufacturing' && item.cadVerification !== 'verified') throw new Error('Výrobní archiv nemá doklad CAD ověření.');
    const asset = manifest.assets.find(asset => asset.kind === 'model-json');
    if (manifest.provenance.kind === 'synthetic-fixture' || item.modelId !== manifest.modelId || !historical && (item.modelAssetSha256 !== asset.sha256 || item.bundleHash && item.bundleHash !== manifest.bundleHash)) continue;
    entries.push({ ...item, historical, href: new URL('/downloads/' + item.path, origin).href });
  }
  return entries;
}

import { open, realpath, readdir } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { loadBundle, MAX_MANIFEST_BYTES, validateManifest } from '../src/features/workshop/model-bundle.mjs';

export async function readModelBundle(directory, { allowFixture = false } = {}) {
  async function readBounded(file, max) {
    const handle = await open(file, 'r');
    try {
      const info = await handle.stat();
      if (!info.isFile() || info.size > max) throw new Error('Model file exceeds size limit');
      const bytes = await handle.readFile();
      if (bytes.length > max) throw new Error('Model file exceeds size limit');
      return bytes;
    } finally { await handle.close(); }
  }
  const root = await realpath(directory);
  const manifestPath = await realpath(path.join(root, 'model-bundle.json'));
  if (path.dirname(manifestPath) !== root) throw new Error('Manifest escapes bundle directory');
  const encoded = await readBounded(manifestPath, MAX_MANIFEST_BYTES);
  const manifest = JSON.parse(encoded.toString('utf8'));
  validateManifest(manifest, { allowFixture });
  const allowed = new Set(['model-bundle.json', ...manifest.assets.map(asset => asset.path)]);
  let inventoryNodes = 0;
  async function checkInventory(folder, prefix = '') {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
      const relative = prefix + entry.name;
      if (++inventoryNodes > 128 || relative.length > 240) throw new Error('Model directory exceeds inventory limit');
      if (entry.isSymbolicLink()) throw new Error('Symbolic links are forbidden in model releases');
      if (entry.isDirectory()) {
        if (![...allowed].some(value => value.startsWith(relative + '/'))) throw new Error('Undeclared model directory');
        await checkInventory(path.join(folder, entry.name), relative + '/');
      }
      else if (!entry.isFile() || !allowed.has(relative)) throw new Error(`Undeclared file in model release: ${relative}`);
    }
  }
  await checkInventory(root);
  return loadBundle(manifest, async (assetPath, max) => {
    const absolute = await realpath(path.join(root, assetPath));
    const relative = path.relative(root, absolute);
    if (relative.startsWith('..') || path.isAbsolute(relative)) throw new Error('Asset escapes bundle directory');
    const bytes = await readBounded(absolute, max);
    if (bytes.length > max) throw new Error('Asset exceeds declared size');
    return new Uint8Array(bytes);
  }, { allowFixture });
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try {
    const index = process.argv.indexOf('--bundle');
    if (index < 0 || !process.argv[index + 1]) throw new Error('Usage: node scripts/validate-model-bundle.mjs --bundle DIRECTORY [--allow-fixture]');
    const { manifest } = await readModelBundle(process.argv[index + 1], { allowFixture: process.argv.includes('--allow-fixture') });
    process.stdout.write(JSON.stringify({ modelId: manifest.modelId, bundleHash: manifest.bundleHash, provenance: manifest.provenance }) + '\n');
  } catch (error) { process.stderr.write(error.message + '\n'); process.exitCode = 1; }
}

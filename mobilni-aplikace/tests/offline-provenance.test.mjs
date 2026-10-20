import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { viewerSourceHash } from '../scripts/package-offline-viewer.mjs';

test('offline viewer provenance changes with every consumed theme bootstrap input', async () => {
  const root = await mkdtemp(path.join(tmpdir(), 'dildovander-provenance-'));
  const appRoot = path.join(root, 'app');
  try {
    for (const directory of ['app/src', 'design-system/tokens', 'design-system/components', 'design-system/fonts']) {
      await mkdir(path.join(root, directory), { recursive: true });
    }
    const files = [
      'app/package-lock.json', 'app/scripts/package-offline-viewer.mjs', 'app/vite.config.mjs',
      'design-system/arcane-workshop.css', 'design-system/theme.js', 'design-system/theme.mjs',
      ...['viewer_engine_core.js', 'twin_core.js', 'timeline_core.js', 'twin_media_core.js', 'twin_media_controller.js'].map(name => `mechanika/final/${name}`),
    ];
    for (const name of files) {
      await mkdir(path.dirname(path.join(root, name)), { recursive: true });
      await writeFile(path.join(root, name), 'original input');
    }
    let previous = await viewerSourceHash(appRoot);
    for (const name of ['design-system/theme.js', 'design-system/theme.mjs', 'app/vite.config.mjs']) {
      await writeFile(path.join(root, name), 'changed theme behavior');
      const next = await viewerSourceHash(appRoot);
      assert.notEqual(next, previous, `${name} must invalidate the offline source hash`);
      previous = next;
    }
  } finally { await rm(root, { recursive: true, force: true }); }
});

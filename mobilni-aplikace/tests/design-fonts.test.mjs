import assert from 'node:assert/strict';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { createServer } from 'vite';

test('development serves self-hosted shared fonts without opening the repository root', async () => {
  const root = fileURLToPath(new URL('..', import.meta.url));
  const server = await createServer({
    root,
    configFile: fileURLToPath(new URL('../vite.config.mjs', import.meta.url)),
    server: { host: '127.0.0.1', port: 0 },
    logLevel: 'silent',
  });
  try {
    await server.listen();
    const origin = `http://127.0.0.1:${server.httpServer.address().port}`;
    const requestFile = path => fetch(origin + '/@fs/' + path.replaceAll('\\', '/'));
    for (const font of ['inter.ttf', 'ibmplexmono.ttf']) {
      const path = fileURLToPath(new URL(`../../design-system/fonts/${font}`, import.meta.url));
      const response = await requestFile(path);
      assert.equal(response.status, 200, `${font} is delivered in development`);
      assert.ok((await response.arrayBuffer()).byteLength > 100_000);
    }
    const outside = fileURLToPath(new URL('../../AGENTS.md', import.meta.url));
    assert.equal((await requestFile(outside)).status, 403, 'Repository-only files stay outside the allow list');
  } finally {
    await server.close();
  }
});

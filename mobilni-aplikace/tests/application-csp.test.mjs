import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { test } from 'node:test';
import { applicationCsp } from '../vite.config.mjs';

test('early capability capture CSP uses HTML parser line endings on every checkout', () => {
  const script = '\nwindow.capture = true;\n';
  const expected = `sha256-${createHash('sha256').update(script).digest('base64')}`;
  for (const ending of ['\n', '\r\n', '\r']) {
    const html = `<script>${script.replaceAll('\n', ending)}</script><script src="/entry.js"></script>`;
    const [meta] = applicationCsp().transformIndexHtml.handler(html);
    assert.ok(meta.attrs.content.includes(expected));
    assert.equal((meta.attrs.content.match(/sha256-/g) ?? []).length, 1);
    assert.ok(!meta.attrs.content.includes('unsafe-inline'));
  }
});

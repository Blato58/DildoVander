import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { stripTypeScriptTypes } from 'node:module';
import { test } from 'node:test';
let source = await readFile(new URL('../src/features/keepsake/KeepsakePanel.ts', import.meta.url), 'utf8');
source = source.replace("import QRCode from 'qrcode';", 'const QRCode = {};');
const { validKeepsake } = await import(`data:text/javascript;base64,${Buffer.from(stripTypeScriptTypes(source, { mode: 'transform' })).toString('base64')}`);
const snapshot = () => ({ source: 'native-device', phase: 'ready', busy: false, conversationGeneration: 7,
  keepsake: { outcomeId: '22222222-2222-4222-8222-222222222222', sessionEpoch: 7,
    physicalOperationId: '33333333-3333-4333-8333-333333333333', expiresAtEpochMs: 10000,
    url: 'https://2.28.202.220/keepsake/#c=' + 'a'.repeat(43) } });
test('QR uses only exact native timed outcome and disappears on next epoch, Stop and expiry', () => {
  const valid = snapshot(); assert.equal(validKeepsake(valid, 5000), valid.keepsake);
  for (const change of [{ source: 'local-fake' }, { source: 'e2-simulator' }, { phase: 'disconnected' },
    { phase: 'stopped' }, { phase: 'running' }, { busy: true }, { conversationGeneration: 8 }]) {
    assert.equal(validKeepsake({ ...valid, ...change }, 5000), null);
  }
  assert.equal(validKeepsake(valid, 10000), null);
});
test('QR accepts only HTTPS claim fragment with no query, credentials or alternate route', () => {
  for (const url of ['http://2.28.202.220/keepsake/#c=' + 'a'.repeat(43),
    'https://2.28.202.220/keepsake/?c=secret', 'https://user@2.28.202.220/keepsake/#c=' + 'a'.repeat(43),
    'https://2.28.202.220/v1/#c=' + 'a'.repeat(43), 'https://2.28.202.220/keepsake/#s=' + 'a'.repeat(43)]) {
    const value = snapshot(); value.keepsake.url = url; assert.equal(validKeepsake(value, 5000), null);
  }
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createConnection } from 'node:net';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

// Checks the real wire fixture without requiring an Apple runtime. Swift tests
// separately exercise URLSessionWebSocketTask and the production ACK ledger.
test('audio socket fixture accepts ordered PCM while ACKs exceed frame duration', async () => {
  const directory = await mkdtemp(join(tmpdir(), 'kufr-native-socket-'));
  const address = join(directory, 'address');
  const peer = spawn(process.execPath, [fileURLToPath(new URL('./http_peer.mjs', import.meta.url)), address], { stdio: 'pipe' });
  let diagnostics = '';
  peer.stderr.on('data', value => { diagnostics += value; });
  let socket;
  try {
    let base;
    for (let attempt = 0; attempt < 100 && !base; attempt++) {
      try { base = await readFile(address, 'utf8'); } catch { await delay(20); }
    }
    assert.ok(base, diagnostics || 'Peer did not start');
    const url = new URL(base);
    socket = createConnection({ host: url.hostname, port: Number(url.port) });
    let bytes = Buffer.alloc(0);
    let upgraded = false;
    const acknowledgements = [];
    socket.on('data', chunk => {
      bytes = Buffer.concat([bytes, chunk]);
      if (!upgraded) {
        const end = bytes.indexOf('\r\n\r\n');
        if (end < 0) return;
        assert.match(bytes.subarray(0, end).toString(), /^HTTP\/1.1 101/);
        bytes = bytes.subarray(end + 4); upgraded = true;
      }
      while (bytes.length >= 2 && bytes.length >= 2 + (bytes[1] & 127)) {
        const count = bytes[1] & 127;
        acknowledgements.push(JSON.parse(bytes.subarray(2, 2 + count).toString()));
        bytes = bytes.subarray(2 + count);
      }
    });
    await new Promise((resolve, reject) => { socket.once('connect', resolve); socket.once('error', reject); });
    socket.write(`GET /test/audio-stream HTTP/1.1\r\nHost: ${url.host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nAuthorization: Bearer access\r\n\r\n`);
    for (let attempt = 0; attempt < 100 && !upgraded; attempt++) await delay(10);
    assert.ok(upgraded);
    for (let sequence = 0; sequence < 20; sequence++) {
      const body = Buffer.from(JSON.stringify({ inputId: 'socket-regression', sequence, audio: Buffer.alloc(9600, sequence).toString('base64') }));
      const mask = Buffer.from([17, 29, 53, 71]);
      for (let index = 0; index < body.length; index++) body[index] ^= mask[index % 4];
      socket.write(Buffer.concat([Buffer.from([0x81, 0xfe, body.length >> 8, body.length & 255]), mask, body]));
      await delay(25);
    }
    for (let attempt = 0; attempt < 100 && acknowledgements.length < 20; attempt++) await delay(20);
    assert.deepEqual(acknowledgements, Array.from({ length: 20 }, (_, sequence) => ({ type: 'ack', inputId: 'socket-regression', sequence })));
    const metrics = await (await fetch(`${base}/test/metrics`)).json();
    assert.equal(metrics.regressionUploads, 20);
    assert.equal(diagnostics, '');
  } finally {
    socket?.destroy();
    peer.kill();
    await new Promise(resolve => { if (peer.exitCode !== null) resolve(); else peer.once('exit', resolve); });
    assert.equal(dirname(directory), tmpdir());
    assert.ok(basename(directory).startsWith('kufr-native-socket-'));
    await rm(directory, { recursive: true, force: true });
  }
});

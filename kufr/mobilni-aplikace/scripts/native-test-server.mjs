// Synthetic loopback-only peer for macOS native transport tests. No provider,
// device or real credential is used; its certificate is generated per run.
import https from 'node:https';
import { readFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
const require = createRequire(new URL('../../simulator/package.json', import.meta.url));
const { WebSocketServer } = require('ws');
const [directory, port] = process.argv.slice(2);
const token = 'b'.repeat(64);
const server = https.createServer({
  key: await readFile(`${directory}/ec256.key`),
  cert: await readFile(`${directory}/ec256.pem`),
}, (request, response) => {
  if (request.method !== 'POST' || request.url !== '/v1/pair') { response.writeHead(404).end(); return; }
  let bytes = '';
  request.on('data', (chunk) => { bytes += chunk; if (bytes.length > 192) { request.destroy(); } });
  request.on('end', () => {
    const { protocolVersion, pairingSecret } = JSON.parse(bytes);
    if (protocolVersion !== 1) { response.writeHead(400).end(); return; }
    if (pairingSecret === 'c'.repeat(32)) {
      response.writeHead(302, { Location: `https://127.0.0.1:${port}/redirected` }).end();
    } else if (pairingSecret === 'd'.repeat(32)) {
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(' '.repeat(1024));
    } else if (pairingSecret === 'e'.repeat(32)) {
      response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({ protocolVersion: true, token }));
    } else if (pairingSecret !== 'a'.repeat(32)) {
      response.writeHead(401).end();
    } else {
      response.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' })
        .end(JSON.stringify({ protocolVersion: 1, token }));
    }
  });
});
const wss = new WebSocketServer({ noServer: true, maxPayload: 8192 });
server.on('upgrade', (request, socket, head) => {
  if (request.url !== '/v1/control' || request.headers.authorization !== `Bearer ${token}`) {
    socket.end('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n');
    return;
  }
  wss.handleUpgrade(request, socket, head, (websocket) => {
    websocket.on('message', (frame) => {
      const message = frame.toString('utf8');
      if (message === 'binary') { websocket.send(Buffer.from('binary')); }
      else if (message === 'large') { websocket.send('x'.repeat(8193)); }
      else { websocket.send(message); }
    });
  });
});
server.listen(Number(port), '127.0.0.1', () => process.stdout.write('native-test-peer-ready\n'));

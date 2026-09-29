import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, sep } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import WebSocket, { WebSocketServer } from 'ws';
import { validateControlMessage } from '../contracts/control-validator.mjs';

const contentTypes = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.svg', 'image/svg+xml'],
]);

export async function createMobileDevServer({
  certificate,
  token,
  distDirectory,
  simulatorPort = 8443,
  port = 5174,
}) {
  if (!certificate || !token || !distDirectory) {
    throw new Error('Simulator certificate, token and built mobile directory are required');
  }

  const root = resolve(distDirectory);
  const browserSockets = new WebSocketServer({
    noServer: true,
    perMessageDeflate: false,
    maxPayload: 8192,
  });
  let browserSocket = null;
  let upstreamSocket = null;
  let controlSlotTaken = false;

  const server = createServer(async (request, response) => {
    if (request.method !== 'GET') {
      response.writeHead(405).end();
      return;
    }
    const path = new URL(request.url, 'http://127.0.0.1').pathname;
    if (path === '/dev-mode') {
      response.writeHead(200, {
        'Content-Type': 'application/json; charset=utf-8',
        'Cache-Control': 'no-store',
      }).end(JSON.stringify({ simulator: true }));
      return;
    }
    const target = resolve(root, `.${path === '/' ? '/index.html' : path}`);
    if (target !== root && !target.startsWith(`${root}${sep}`)) {
      response.writeHead(404).end();
      return;
    }
    try {
      const body = await readFile(target);
      const extension = target.slice(target.lastIndexOf('.'));
      response.writeHead(200, {
        'Content-Type': contentTypes.get(extension) || 'application/octet-stream',
        'Cache-Control': 'no-store',
        'X-Content-Type-Options': 'nosniff',
      }).end(body);
    } catch {
      response.writeHead(404).end();
    }
  });

  server.on('upgrade', (request, networkSocket, head) => {
    const expectedOrigin = `http://127.0.0.1:${server.address().port}`;
    if (request.url !== '/dev/control' || request.headers.origin !== expectedOrigin ||
        controlSlotTaken) {
      networkSocket.write('HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n');
      networkSocket.destroy();
      return;
    }
    controlSlotTaken = true;
    browserSockets.handleUpgrade(request, networkSocket, head, (client) => {
      browserSocket = client;
      const queuedMessages = [];
      let browserClosed = false;
      let upstreamClosed = false;
      const releaseSlot = () => {
        if (browserClosed && upstreamClosed) {
          controlSlotTaken = false;
        }
      };
      const upstream = new WebSocket(`wss://127.0.0.1:${simulatorPort}/v1/control`, {
        headers: { Authorization: `Bearer ${token}` },
        ca: certificate,
        rejectUnauthorized: true,
        perMessageDeflate: false,
        maxPayload: 8192,
      });
      upstreamSocket = upstream;

      client.on('message', (bytes, binary) => {
        if (binary || upstream.readyState === WebSocket.CLOSED ||
            upstream.readyState === WebSocket.CLOSING) {
          client.close(1008, 'control channel unavailable');
          return;
        }
        try {
          validateControlMessage(bytes);
          if (upstream.readyState === WebSocket.OPEN) {
            upstream.send(bytes.toString('utf8'));
          } else if (queuedMessages.length < 16) {
            queuedMessages.push(bytes.toString('utf8'));
          } else {
            client.close(1008, 'control queue full');
          }
        } catch {
          client.close(1008, 'invalid control message');
        }
      });
      upstream.on('open', () => {
        for (const message of queuedMessages) {
          upstream.send(message);
        }
        queuedMessages.length = 0;
      });
      upstream.on('message', (bytes, binary) => {
        if (binary || client.readyState !== WebSocket.OPEN) {
          client.close(1008, 'invalid simulator response');
          return;
        }
        client.send(bytes.toString('utf8'));
      });
      upstream.on('unexpected-response', (_upstreamRequest, response) => {
        response.resume();
        client.close(1011, `simulator HTTP ${response.statusCode}`);
      });
      upstream.on('error', (error) => {
        client.close(1011, `simulator connection failed: ${error.code || 'unknown'}`);
      });
      upstream.on('close', (code, reason) => {
        upstreamClosed = true;
        client.close(1011, `simulator disconnected: ${code} ${reason}`.slice(0, 120));
        releaseSlot();
      });
      client.on('error', () => upstream.terminate());
      client.on('close', () => {
        browserClosed = true;
        upstream.terminate();
        if (browserSocket === client) {
          browserSocket = null;
        }
        if (upstreamSocket === upstream) {
          upstreamSocket = null;
        }
        releaseSlot();
      });
    });
  });

  await new Promise((fulfill, reject) => {
    server.once('error', reject);
    server.listen(port, '127.0.0.1', fulfill);
  });

  return {
    port: server.address().port,
    close: async () => {
      browserSocket?.terminate();
      upstreamSocket?.terminate();
      browserSockets.close();
      await new Promise((fulfill) => server.close(fulfill));
    },
  };
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const certificatePath = process.env.KUFR_SIMULATOR_CERT;
  const token = process.env.KUFR_SIMULATOR_TOKEN;
  if (!certificatePath || !token) {
    throw new Error('Set KUFR_SIMULATOR_CERT and KUFR_SIMULATOR_TOKEN');
  }
  const server = await createMobileDevServer({
    certificate: await readFile(certificatePath),
    token,
    distDirectory: fileURLToPath(new URL('../mobilni-aplikace/dist', import.meta.url)),
  });
  console.log(`SIMULACE mobile UI ready at http://127.0.0.1:${server.port}`);
}

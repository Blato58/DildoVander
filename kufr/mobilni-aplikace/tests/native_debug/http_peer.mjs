import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';

const addressFile = process.argv[2];
if (!addressFile) throw new Error('Address file required');

const correlation = {
  deviceId: 'debug-test', bootId: '00000000-0000-4000-8000-000000000001',
  conversationId: '00000000-0000-4000-8000-000000000002',
  clientTurnId: '00000000-0000-4000-8000-000000000003', generation: 0, deviceRevision: 0,
};
const operationId = '00000000-0000-4000-8000-000000000004';
function realtime(response, variant = 'valid') {
  const received = variant === 'correlation' ? { ...correlation, generation: 1 } : correlation;
  const events = [
    { type: 'started', operationId, correlation: received, audioFormat: 'pcm16-mono-24000' },
    { type: 'audio', delta: variant === 'oversize' ? Buffer.alloc(49_154).toString('base64') : Buffer.from([0, 0, 1, 0]).toString('base64') },
    { type: 'visitor', text: 'Ahoj' },
    { type: 'completed', operationId, correlation: received, result: { reply: 'synthetic', decision: 'ask', reason: '', proposal: null } },
  ];
  response.writeHead(200, { 'Content-Type': 'application/x-ndjson', 'Cache-Control': 'no-store', 'X-Accel-Buffering': 'no' });
  const body = events.map(event => `${JSON.stringify(event)}\n`);
  response.write(body[0].slice(0, 31));
  setTimeout(() => response.write(body[0].slice(31) + body[1]), 5);
  setTimeout(() => response.end(body.slice(2).join('')), 10);
}

const server = createServer((request, response) => {
  switch (request.url) {
    case '/realtime':
      realtime(response);
      break;
    case '/realtime-correlation':
      realtime(response, 'correlation');
      break;
    case '/realtime-oversize':
      realtime(response, 'oversize');
      break;
    case '/realtime-mime':
      response.writeHead(200, { 'Content-Type': 'text/plain' });
      response.end('not a stream');
      break;
    case '/realtime-hang':
      response.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
      response.write(`${JSON.stringify({ type: 'started', operationId, correlation, audioFormat: 'pcm16-mono-24000' })}\n`);
      break;
    case '/chunks':
      response.writeHead(200, { 'Content-Type': 'text/plain' });
      response.write('hello ');
      setTimeout(() => response.end('world'), 10);
      break;
    case '/known-oversize':
      response.writeHead(200, { 'Content-Length': '64', 'Content-Type': 'text/plain' });
      response.end('x'.repeat(64));
      break;
    case '/chunked-oversize':
      response.writeHead(200, { 'Content-Type': 'text/plain' });
      response.write('xxxxxx');
      setTimeout(() => response.end('yyyyyy'), 10);
      break;
    case '/redirect':
      response.writeHead(302, { Location: '/redirect-target' });
      response.end();
      break;
    case '/redirect-target':
      response.writeHead(200, { 'Content-Type': 'text/plain' });
      response.end('followed');
      break;
    case '/hang':
      // Leave the response open until URLSession cancels the socket.
      break;
    default:
      response.writeHead(404);
      response.end();
  }
});

server.listen(0, '127.0.0.1', () => {
  const address = server.address();
  writeFileSync(addressFile, `http://127.0.0.1:${address.port}`);
});

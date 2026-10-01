import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';

const addressFile = process.argv[2];
if (!addressFile) throw new Error('Address file required');
const ids = {
  conversation: '00000000-0000-4000-8000-000000000002', boot: '00000000-0000-4000-8000-000000000001',
  opening: '00000000-0000-4000-8000-000000000003', input1: '00000000-0000-4000-8000-000000000005',
  input2: '00000000-0000-4000-8000-000000000006', stream: '00000000-0000-4000-8000-000000000007',
  operation0: '00000000-0000-4000-8000-000000000010', operation1: '00000000-0000-4000-8000-000000000011',
  operation2: '00000000-0000-4000-8000-000000000012',
};
let stream; let played = 0; let uploads = 0; let cleanup = 0;
let regressionUploads = 0; let redirected = 0;
const regressionSockets = new Set();
function correlation(clientTurnId) { return { deviceId: 'debug-test', bootId: ids.boot, conversationId: ids.conversation, clientTurnId, generation: 0, deviceRevision: 0 }; }
function event(value) { stream.write(`${JSON.stringify(value)}\n`); }
function answer(input, operation, terminal) {
  event({ type: 'started', operationId: operation, correlation: correlation(input), audioFormat: 'pcm16-mono-24000' });
  event({ type: 'audio', delta: Buffer.from([0, 0, 1, 0]).toString('base64') });
  if (uploads > 0) event({ type: 'visitor', text: `visitor-${uploads}` });
  event({ type: 'completed', operationId: operation, correlation: correlation(input), costMicroUsd: 1,
    result: { reply: `reply-${uploads}`, decision: terminal ? 'no_selection' : 'ask', reason: '', proposal: null } });
}
const server = createServer((request, response) => {
  if (request.url === '/test/upload') {
    const chunks = [];
    request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      const body = Buffer.concat(chunks);
      if (body.length !== 9600 || request.headers['input-id'] !== 'upload-regression'
          || request.headers['input-sequence'] !== String(regressionUploads)) throw new Error('invalid batched upload');
      for (let index = 0; index < body.length; index++) {
        if (body[index] !== regressionUploads * 4 + Math.floor(index / 2400)) throw new Error('PCM order changed');
      }
      regressionSockets.add(request.socket);
      regressionUploads++;
      setTimeout(() => response.writeHead(204).end(), regressionUploads === 1 ? 550 : 150);
    });
    return;
  }
  if (request.url === '/test/redirect') { response.writeHead(302, { Location: '/test/redirected' }).end(); return; }
  if (request.url === '/test/redirected') { redirected++; response.writeHead(204).end(); return; }
  if (request.url === '/test/finish') { setTimeout(() => response.writeHead(204).end(), 150); return; }
  if (request.url === '/test/stall') { return; }
  if (request.url.startsWith('/failure/')) {
    if (request.method === 'DELETE') { response.writeHead(204).end(); return; }
    request.resume();
    if (request.url.startsWith('/failure/problem/')) {
      response.writeHead(503, { 'Content-Type': 'application/problem+json' }).end(JSON.stringify({ code: 'realtime_unavailable' }));
    } else if (request.url.startsWith('/failure/mime/')) {
      response.writeHead(200, { 'Content-Type': 'text/html' }).end('<html>proxy response</html>');
    } else {
      response.writeHead(200, { 'Content-Type': 'application/x-ndjson' }).end(`${JSON.stringify({ type: 'session', streamId: ids.stream, audioFormat: 'pcm16-mono-24000' })}\n`);
    }
    return;
  }
  if (request.method === 'POST' && request.url === `/v1/conversations/${ids.conversation}/realtime-live`) {
    const chunks = [];
    request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      ids.opening = JSON.parse(Buffer.concat(chunks).toString()).clientTurnId;
      stream = response; response.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
      event({ type: 'session', streamId: ids.stream, audioFormat: 'pcm16-mono-24000' }); answer(ids.opening, ids.operation0, false);
    }); return;
  }
  if (request.method === 'POST' && request.url.endsWith('/played')) {
    played += 1;
    if (played === 1) { event({ type: 'listening', inputId: ids.input1 }); setTimeout(() => response.writeHead(204).end(), 30); return; }
    if (played === 2) { event({ type: 'listening', inputId: ids.input2 }); setTimeout(() => response.writeHead(204).end(), 30); return; }
    event({ type: 'closed', reason: 'conversation_terminal' }); stream.end(); response.writeHead(204).end(); return;
  }
  if (request.method === 'POST' && request.url.endsWith('/audio')) {
    const chunks = []; request.on('data', chunk => { chunks.push(chunk); }); request.on('end', () => {
      const body = Buffer.concat(chunks);
      if (body.length === 0 || body.length > 9600 || request.headers['input-sequence'] !== '0') throw new Error('invalid audio frame');
      uploads += 1; const input = request.headers['input-id'];
      event({ type: 'speech_started', inputId: input });
      setTimeout(() => { event({ type: 'speech_stopped', inputId: input }); answer(input, uploads === 1 ? ids.operation1 : ids.operation2, uploads === 2); }, 5);
      response.writeHead(204).end();
    }); return;
  }
  if (request.method === 'DELETE' && request.url === `/v1/conversations/${ids.conversation}`) { cleanup += 1; response.writeHead(204).end(); return; }
  if (request.method === 'GET' && request.url === '/test/metrics') {
    response.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify({
      played, uploads, cleanup, regressionUploads, regressionSockets: regressionSockets.size, redirected,
    })); return;
  }
  response.writeHead(404).end();
});
server.listen(0, '127.0.0.1', () => writeFileSync(addressFile, `http://127.0.0.1:${server.address().port}`));
process.on('exit', () => { if (played !== 3 || uploads !== 2 || cleanup !== 1) process.exitCode = 1; });

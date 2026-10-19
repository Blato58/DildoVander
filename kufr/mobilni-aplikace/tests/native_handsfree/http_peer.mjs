import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { PassThrough } from 'node:stream';

const addressFile = process.argv[2];
if (!addressFile) throw new Error('Address file required');
const ids = {
  conversation: '00000000-0000-4000-8000-000000000002', boot: '00000000-0000-4000-8000-000000000001',
  opening: '00000000-0000-4000-8000-000000000003', input1: '00000000-0000-4000-8000-000000000005',
  input2: '00000000-0000-4000-8000-000000000006', stream: '00000000-0000-4000-8000-000000000007',
  operation0: '00000000-0000-4000-8000-000000000010', operation1: '00000000-0000-4000-8000-000000000011',
  operation2: '00000000-0000-4000-8000-000000000012',
  checkpoint: '00000000-0000-4000-8000-000000000020',
};
let stream; let played = 0; let uploads = 0; let cleanup = 0;
let livePlayed = 0; let liveUploads = 0;
let actorActive = false; let actorCheckpoints = 0;
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
  const actor = request.url.startsWith('/actor/');
  if (actor) request.url = request.url.slice('/actor'.length);
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
  if (request.method === 'POST' && request.url === `/v1/conversations/${ids.conversation}/gpt-live`) {
    const chunks = []; request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      const start = JSON.parse(Buffer.concat(chunks).toString());
      if (start.speed !== 1) throw new Error('Live must use native speed');
      actorActive = actor;
      ids.opening = start.clientTurnId;
      stream = response; response.writeHead(200, { 'Content-Type': 'application/x-ndjson' });
      event({ type: 'session', streamId: ids.stream, audioFormat: 'pcm16-mono-24000' });
      event({ type: 'started', operationId: ids.operation0, correlation: correlation(ids.opening) });
      event({ type: 'listening', inputId: ids.opening });
      event({ type: 'audio', delta: Buffer.from([0, 0, 1, 0]).toString('base64') });
      event({ type: 'pause' });
    }); return;
  }
  if (request.method === 'POST' && request.url.includes('/gpt-live/') && request.url.endsWith('/audio')) {
    request.resume(); request.on('end', () => {
      if (request.headers['input-id'] !== ids.opening || request.headers['input-sequence'] !== '0') throw new Error('Invalid Live input');
      liveUploads++;
      event({ type: 'visitor', text: 'Live odpověď' });
      if (actorActive) {
        event({ type: 'audio_cancel', responseGeneration: 1 });
        event({ type: 'audio_cancel', responseGeneration: 1 }); // duplicate cannot cancel the fresh response
      }
      event({ type: 'audio', delta: Buffer.from([0, 0, 1, 0]).toString('base64') });
      event({ type: 'pause' });
      if (actorActive) {
        event({ type: 'playback_checkpoint', playbackId: ids.checkpoint, pcmBytes: 8, decisionRevision: 2 });
      } else {
        event({ type: 'speech_stopped', inputId: ids.opening });
        event({ type: 'completed', operationId: ids.operation0, correlation: correlation(ids.opening), costMicroUsd: 50000,
          result: { reply: 'Live závěr', decision: 'no_selection', reason: '', proposal: null } });
      }
      response.writeHead(204).end();
    }); return;
  }
  if (request.method === 'POST' && request.url.includes('/gpt-live/') && request.url.endsWith('/playback')) {
    const chunks = []; request.on('data', chunk => chunks.push(chunk));
    request.on('end', () => {
      const body = JSON.parse(Buffer.concat(chunks).toString());
      if (!actor || request.headers.authorization !== 'Bearer access' || Object.keys(body).length !== 3
          || body.playbackId !== ids.checkpoint || body.pcmBytes !== 8 || body.drained !== true) throw new Error('Invalid checkpoint ACK');
      actorCheckpoints++;
      if (actorCheckpoints === 1) { request.socket.destroy(); return; } // accepted ACK, lost response
      if (actorCheckpoints !== 2) throw new Error('Checkpoint replay was not bounded');
      event({ type: 'speech_stopped', inputId: ids.opening });
      event({ type: 'completed', operationId: ids.operation0, correlation: correlation(ids.opening), costMicroUsd: 50000,
        result: { reply: 'Actor závěr', decision: 'no_selection', reason: '', proposal: null } });
      response.writeHead(204).end();
    }); return;
  }
  if (request.method === 'POST' && request.url.includes('/gpt-live/') && request.url.endsWith('/played')) {
    livePlayed++;
    event({ type: 'closed', reason: 'conversation_terminal' }); stream.end(); response.writeHead(204).end(); return;
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
      played, uploads, cleanup, livePlayed, liveUploads, actorCheckpoints, regressionUploads, regressionSockets: regressionSockets.size, redirected,
    })); return;
  }
  response.writeHead(404).end();
});
// Real native URLSessionWebSocketTask peer. Send callbacks never wait for ACKs.
server.on('upgrade', (request, socket, head) => {
  if (!request.url.endsWith('/audio-stream') || request.headers.authorization !== 'Bearer access') {
    socket.end('HTTP/1.1 401 Unauthorized\r\nConnection: close\r\n\r\n'); return;
  }
  const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  let pending = Buffer.alloc(0);
  const send = value => {
    const body = Buffer.from(JSON.stringify(value));
    const header = body.length < 126 ? Buffer.from([0x81, body.length]) : Buffer.from([0x81, 126, body.length >> 8, body.length & 255]);
    if (!socket.destroyed) socket.write(Buffer.concat([header, body]));
  };
  socket.on('error', () => {});
  socket.on('data', chunk => {
    pending = Buffer.concat([pending, chunk]);
    while (pending.length >= 2) {
      let length = pending[1] & 127; let offset = 2;
      if (length === 126) { if (pending.length < 4) return; length = pending.readUInt16BE(2); offset = 4; }
      if (length === 127 || length > 14000 || !(pending[1] & 128)) { socket.destroy(); return; }
      if (pending.length < offset + 4 + length) return;
      const opcode = pending[0] & 15;
      const mask = pending.subarray(offset, offset + 4); offset += 4;
      const body = Buffer.from(pending.subarray(offset, offset + length));
      for (let index = 0; index < body.length; index++) body[index] ^= mask[index % 4];
      pending = pending.subarray(offset + length);
      if (opcode === 8) { socket.end(); return; }
      if (opcode !== 1) { socket.destroy(); return; }
      const frame = JSON.parse(body.toString());
      const pcm = Buffer.from(frame.audio, 'base64');
      if (request.url === '/test/audio-stream') {
        if (pcm.length !== 9600 || frame.sequence !== regressionUploads || frame.inputId !== 'socket-regression') throw new Error('Invalid socket sequence');
        if (!pcm.every(value => value === frame.sequence % 251)) throw new Error('Socket PCM changed');
        regressionUploads++;
        setTimeout(() => send({ type: 'ack', inputId: frame.inputId, sequence: frame.sequence }), 350);
        continue;
      }
      const audio = new PassThrough();
      audio.url = request.url.replace(/audio-stream$/, 'audio');
      audio.headers = { authorization: 'Bearer access', 'input-id': frame.inputId, 'input-sequence': String(frame.sequence) };
      audio.method = 'POST'; audio.socket = socket;
      const response = { statusCode: 200, writeHead(code) { this.statusCode = code; return this; },
        end() { if (this.statusCode !== 204) throw new Error('Audio upload failed'); send({ type: 'ack', inputId: frame.inputId, sequence: frame.sequence }); } };
      server.emit('request', audio, response);
      audio.end(pcm);
    }
  });
  if (head.length) socket.emit('data', head);
});
server.listen(0, '127.0.0.1', () => writeFileSync(addressFile, `http://127.0.0.1:${server.address().port}`));
process.on('exit', () => { if (played !== 3 || uploads !== 2 || cleanup !== 3 || livePlayed !== 2 || liveUploads !== 2 || actorCheckpoints !== 2) process.exitCode = 1; });

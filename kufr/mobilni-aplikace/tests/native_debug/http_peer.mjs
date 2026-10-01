import { createServer } from 'node:http';
import { writeFileSync } from 'node:fs';

const addressFile = process.argv[2];
if (!addressFile) throw new Error('Address file required');

const server = createServer((request, response) => {
  switch (request.url) {
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

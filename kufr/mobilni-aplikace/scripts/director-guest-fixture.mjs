// Test-only transport: real public source files and the isolated backend's real
// guest API, served under its configured synthetic public origin. No fake DTOs.
import { readFile } from 'node:fs/promises';
import { extname, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const webRoot = fileURLToPath(new URL('../../web/', import.meta.url));
const designRoot = fileURLToPath(new URL('../../design-system/', import.meta.url));
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.ttf': 'font/ttf' };

export async function guestFixtureContext(browser, publicOrigin, backend) {
  const origin = new URL(publicOrigin).origin;
  if (!['localhost', '127.0.0.1'].includes(new URL(backend).hostname)) throw new Error('Guest fixture requires loopback backend.');
  const context = await browser.newContext({ viewport: { width: 390, height: 844 } });
  await context.route(`${origin}/**`, async route => {
    const request = route.request();
    const url = new URL(request.url());
    const pathname = decodeURIComponent(url.pathname);
    if (/^\/guest\/v1\/duels\/[0-9a-f-]{36}(\/choices)?$/i.test(pathname)
      || ['/guest/v1/chronicle', '/v1/public/chronicle'].includes(pathname)) {
      // Match the production trusted-loopback TLS-termination boundary. The
      // fixture backend retains its HTTPS/Host and public device checks.
      const headers = { 'Content-Type': 'application/json', Host: new URL(origin).host, 'X-Forwarded-Proto': 'https' };
      const authorization = request.headers().authorization;
      if (authorization) headers.Authorization = authorization;
      const response = await fetch(`${backend}${url.pathname}${url.search}`, {
        method: request.method(), headers, redirect: 'manual',
        ...(request.method() === 'POST' ? { body: request.postData() } : {}),
      });
      await route.fulfill({ status: response.status, contentType: 'application/json',
        headers: { 'Cache-Control': 'no-store' }, body: await response.text() });
      return;
    }
    if (request.method() !== 'GET' || pathname.includes('..') || pathname.includes('\\')) {
      await route.abort(); return;
    }
    const source = pathname.startsWith('/design-system/') ? { root: designRoot, relative: pathname.slice('/design-system/'.length) }
      : pathname.startsWith('/keepsake/') ? { root: webRoot, relative: pathname.slice(1) } : null;
    if (!source) { await route.fulfill({ status: 404, body: '' }); return; }
    const file = resolve(source.root, source.relative);
    if (!file.startsWith(resolve(source.root) + sep)) { await route.abort(); return; }
    try { await route.fulfill({ status: 200, contentType: mime[extname(file)] ?? 'application/octet-stream', body: await readFile(file) }); }
    catch { await route.fulfill({ status: 404, body: '' }); }
  });
  return context;
}

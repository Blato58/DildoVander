import { Capacitor, registerPlugin } from '@capacitor/core';
import { ArchiveClient, GuestError, downloadName, mediaEssence, parseClaim } from './client';

export interface NativeGuestBridge {
  guestCapabilities(): Promise<{ available: boolean; origin?: string }>;
  guestRequest(options: { path: string; method: string; headers: Record<string, string>; bodyBase64?: string }): Promise<{ status: number; headers: Record<string, string>; bodyBase64: string }>;
  shareGuestFile(options: { filename: string; mediaType: string; bodyBase64: string }): Promise<void>;
  shareGuestLink(options: { url: string }): Promise<void>;
}
const bridge = registerPlugin<NativeGuestBridge>('KufrIdentity');
const maximumUpload = 8 * 1024 * 1024;
const maximumDownload = 32 * 1024 * 1024;
const uuidPattern = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
const keepsakePath = new RegExp(`^/guest/v1/keepsakes/${uuidPattern}(?:/(?:history|assets/${uuidPattern}|recovery-links|share-links|revoke-access|portrait-consents|portrait-uploads|portraits|(?:portraits|generation)/${uuidPattern}/cancel|exports|portrait|account-associations))?$`);
const publicAsset = new RegExp(`^/guest/v1/evenings/${uuidPattern}/keepsakes/${uuidPattern}/editions/${uuidPattern}/assets/${uuidPattern}$`);
export function validateGuestRequest(path: string, method: string) {
  if (path.length > 4096 || !['GET', 'POST', 'DELETE'].includes(method)) throw new GuestError(400);
  const parsed = new URL(path, 'https://guest.invalid');
  if (parsed.origin !== 'https://guest.invalid' || parsed.hash || parsed.pathname !== path.split('?')[0]) throw new GuestError(400);
  if (parsed.pathname === '/guest/v1/evening') {
    if (method !== 'GET' || [...parsed.searchParams.keys()].some(key => !['cursor', 'limit'].includes(key))) throw new GuestError(400);
  } else if (publicAsset.test(parsed.pathname)) {
    if (method !== 'GET' || parsed.search) throw new GuestError(400);
  } else if (/^\/guest\/v1\/(bootstrap|claim|recover|share-claim)$/.test(parsed.pathname)) {
    if (method !== 'POST' || parsed.search) throw new GuestError(400);
  } else if (new RegExp(`^/guest/v1/deletions/${uuidPattern}$`).test(parsed.pathname)) {
    if (method !== 'GET' || parsed.search) throw new GuestError(400);
  } else if (keepsakePath.test(parsed.pathname)) {
    const suffix = parsed.pathname.slice('/guest/v1/keepsakes/'.length + 36);
    const allowedMethods = suffix === '' ? ['GET', 'DELETE'] : suffix === '/history' || suffix.startsWith('/assets/') ? ['GET']
      : suffix === '/share-links' || suffix === '/recovery-links' ? ['POST', 'DELETE'] : suffix === '/portrait' ? ['DELETE'] : ['POST'];
    if (!allowedMethods.includes(method)) throw new GuestError(400);
    if (parsed.search && (method !== 'GET' || parsed.searchParams.get('view') !== 'share'
      || !new RegExp(`^${uuidPattern}$`).test(parsed.searchParams.get('editionId') ?? '')
      || parsed.searchParams.size !== 2 || [...parsed.searchParams.keys()].some(key => !['view', 'editionId'].includes(key)))) throw new GuestError(400);
  } else throw new GuestError(400);
  return path;
}
export function bytesToBase64(bytes: Uint8Array) {
  let encoded = '';
  for (let offset = 0; offset < bytes.length; offset += 8192) encoded += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
  return btoa(encoded);
}
export function base64ToBytes(value: string, limit = maximumDownload) {
  if (typeof value !== 'string' || value.length > Math.ceil(limit / 3) * 4 || !/^[A-Za-z0-9+/]*={0,2}$/.test(value)) throw new GuestError(413);
  let decoded: string;
  try { decoded = atob(value); } catch { throw new GuestError(503); }
  if (decoded.length > limit) throw new GuestError(413);
  return Uint8Array.from(decoded, character => character.charCodeAt(0));
}
export function createNativeGuestTransport(native: NativeGuestBridge): typeof fetch {
  return async (input, init = {}) => {
    const path = validateGuestRequest(String(input), init.method ?? 'GET');
    const check = () => { if (init.signal?.aborted) throw new DOMException('Route changed', 'AbortError'); };
    check();
    const headers: Record<string, string> = {};
    new Headers(init.headers).forEach((value, key) => {
      const name = key.toLowerCase();
      if (!['accept', 'content-type', 'x-dv10-csrf', 'idempotency-key', 'x-dv10-consent'].includes(name)) throw new GuestError(400);
      headers[name] = value;
    });
    let bytes: Uint8Array | undefined;
    if (typeof init.body === 'string') bytes = new TextEncoder().encode(init.body);
    else if (init.body instanceof Blob) bytes = new Uint8Array(await init.body.arrayBuffer());
    else if (init.body !== undefined && init.body !== null) throw new GuestError(400);
    check(); if (bytes && bytes.length > maximumUpload) throw new GuestError(413);
    const response = await native.guestRequest({ path, method: init.method ?? 'GET', headers, bodyBase64: bytes ? bytesToBase64(bytes) : undefined });
    check();
    if (!Number.isInteger(response.status) || response.status < 200 || response.status > 599) throw new GuestError(503);
    const body = base64ToBytes(response.bodyBase64); check();
    const responseHeaders: Record<string, string> = {};
    for (const [name, value] of Object.entries(response.headers)) {
      if (['content-type', 'retry-after', 'content-length'].includes(name.toLowerCase())) responseHeaders[name] = value;
    }
    return new Response([204, 205, 304].includes(response.status) ? null : body, { status: response.status, headers: responseHeaders });
  };
}
let sessionQueue: Promise<void> = Promise.resolve();
export function createArchiveClient() {
  if (!Capacitor.isNativePlatform()) return new ArchiveClient();
  const lock = (action: () => Promise<void>) => { const next = sessionQueue.then(action, action); sessionQueue = next.catch(() => {}); return next; };
  return new ArchiveClient(createNativeGuestTransport(bridge), lock);
}
export async function guestOrigin() {
  if (!Capacitor.isNativePlatform()) return location.origin;
  const capabilities = await bridge.guestCapabilities();
  if (!capabilities.available || !capabilities.origin) throw new GuestError(503);
  const origin = new URL(capabilities.origin);
  if (origin.protocol !== 'https:' || origin.username || origin.password || origin.pathname !== '/' || origin.search || origin.hash) throw new GuestError(503);
  return origin.origin;
}
export async function shareNativeGuestFile(file: File, check: () => void = () => {}) {
  if (file.size > maximumDownload) throw new GuestError(413);
  const filename = downloadName(file.name, file.type);
  if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,100}$/.test(filename)) throw new GuestError(400);
  const bytes = new Uint8Array(await file.arrayBuffer()); check();
  await bridge.shareGuestFile({ filename, mediaType: mediaEssence(file.type), bodyBase64: bytesToBase64(bytes) });
}
export async function shareGuestLink(url: string) {
  const parsed = new URL(url);
  if (parsed.protocol !== 'https:' || parsed.pathname !== '/keepsake/' || parsed.search || parseClaim(parsed.hash)?.kind !== 's') throw new GuestError(400);
  if (Capacitor.isNativePlatform()) await bridge.shareGuestLink({ url });
  else await navigator.share({ title: 'Památka z kufru', url });
}

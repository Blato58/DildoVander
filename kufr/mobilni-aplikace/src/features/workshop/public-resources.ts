import { Capacitor, registerPlugin } from '@capacitor/core';
import { boundedBytes, MAX_MANIFEST_BYTES, MAX_MODEL_BYTES, safeAssetPath } from './model-bundle.mjs';
import { throwIfAborted } from '../../application/cancellation.mjs';

export interface PublicResourceBridge {
  publicOrigin(): Promise<{ origin: string }>;
  publicResource(options: { path: string; maxBytes: number; requestId: string }): Promise<{ status: number; headers: Record<string, string>; bodyBase64: string }>;
  cancelPublicResource(options: { requestId: string }): Promise<void>;
}
const bridge = registerPlugin<PublicResourceBridge>('KufrIdentity');
const indexPath = '/downloads/index.json';
let nativeQueue: Promise<unknown> = Promise.resolve();

function publicRequestId(): string {
  if (typeof crypto.randomUUID === 'function') return crypto.randomUUID();
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6] & 0x0f) | 0x40;
  bytes[8] = (bytes[8] & 0x3f) | 0x80;
  const hex = Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}

export function validatePublicOrigin(value: string): string {
  const url = new URL(value);
  if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash) throw new Error('Veřejný server nemá platný HTTPS původ.');
  return url.origin;
}
export function publicResourcePath(value: string | URL, origin: string, maxBytes: number): string {
  const raw = String(value), url = new URL(raw, origin);
  if (url.origin !== origin || url.username || url.password || url.search || url.hash || /[%\\]/.test(raw) || raw.split('/').some(segment => segment === '.' || segment === '..')) throw new Error('Neplatná cesta veřejného modelu.');
  const match = /^\/models\/bundles\/[a-f0-9]{64}\/(.+)$/.exec(url.pathname);
  if (match) safeAssetPath(match[1]);
  else if (!['/models/active.json', indexPath].includes(url.pathname)) throw new Error('Neplatná cesta veřejného modelu.');
  const limit = url.pathname === indexPath ? 262144 : !match || match[1] === 'model-bundle.json' ? MAX_MANIFEST_BYTES : MAX_MODEL_BYTES;
  if (!Number.isSafeInteger(maxBytes) || maxBytes < 1 || maxBytes > limit) throw new Error('Neplatná velikost veřejného souboru.');
  return url.pathname;
}
export function nativePublicReader(native: PublicResourceBridge, origin: string) {
  origin = validatePublicOrigin(origin);
  return async (url: string | URL, maxBytes: number, signal: AbortSignal): Promise<Uint8Array> => {
    const path = publicResourcePath(url, origin, maxBytes);
    throwIfAborted(signal);
    const operation = nativeQueue.then(async () => {
      throwIfAborted(signal);
      const requestId = publicRequestId();
      let cancelled: Promise<void> | undefined;
      let transfer: ReturnType<PublicResourceBridge['publicResource']> | undefined;
      let aborted!: (reason: unknown) => void;
      const cancellation = new Promise<never>((_, reject) => { aborted = reject; });
      const cancel = () => {
        cancelled = native.cancelPublicResource({ requestId }).catch(async () => { await transfer?.catch(() => {}); });
        aborted(signal.reason ?? new DOMException('Načítání bylo zrušeno.', 'AbortError'));
      };
      signal.addEventListener('abort', cancel, { once: true });
      try {
        transfer = native.publicResource({ path, maxBytes, requestId });
        const response = await Promise.race([transfer, cancellation]);
        throwIfAborted(signal);
        if (!Number.isInteger(response.status) || response.status < 200 || response.status > 599 || typeof response.bodyBase64 !== 'string' || response.bodyBase64.length > Math.ceil(maxBytes / 3) * 4) throw new Error('Veřejná odpověď překročila povolený formát nebo velikost.');
        const bytes = Uint8Array.from(atob(response.bodyBase64), char => char.charCodeAt(0));
        throwIfAborted(signal);
        const headers = new Headers();
        for (const [name, value] of Object.entries(response.headers)) if (['content-type', 'content-length'].includes(name.toLowerCase())) headers.set(name, value);
        return boundedBytes(new Response(bytes, { status: response.status, headers }), maxBytes, signal);
      } finally { signal.removeEventListener('abort', cancel); await cancelled; }
    });
    nativeQueue = operation.catch(() => {});
    return operation;
  };
}
export async function publicResourceClient(signal: AbortSignal) {
  throwIfAborted(signal);
  if (Capacitor.isNativePlatform()) {
    const capabilities = await bridge.publicOrigin();
    throwIfAborted(signal);
    const origin = validatePublicOrigin(capabilities.origin);
    return { origin, read: nativePublicReader(bridge, origin) };
  }
  const origin = location.origin;
  return { origin, read: async (url: string | URL, max: number, requestSignal: AbortSignal) => {
    publicResourcePath(url, origin, max);
    return boundedBytes(await fetch(url, { credentials: 'omit', redirect: 'error', cache: 'no-cache', signal: requestSignal }), max, requestSignal);
  } };
}

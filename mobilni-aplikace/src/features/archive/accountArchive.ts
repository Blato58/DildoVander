import { accountTransport } from '../identity/session';
import { secureUuid } from '../../application/identifiers.mjs';
import { GuestError, mediaEssence } from './client';

const uuid = '[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const ownedRoot = new RegExp(`^/app/v1/archive/owned/${uuid}$`, 'i');
const accountAssociation = new RegExp(`^/app/v1/archive/associations/${uuid}$`, 'i');
const readPath = new RegExp(`^(?:/app/v1/archive/owned/${uuid}(?:/assets/${uuid})?|/app/v1/archive/owned/deletions/${uuid}|/app/v1/archive/associations/${uuid}(?:/assets/${uuid})?)$`, 'i');
const mutationPath = new RegExp(`^/app/v1/archive/owned/${uuid}(?:/(?:recovery-links|share-links|revoke-access|portrait-consents|portrait-uploads|portraits|exports|portrait|wizard-identity|account-associations|portraits/${uuid}/cancel|generation/${uuid}/cancel))?$`, 'i');

type MutationOptions = { method?: 'GET' | 'POST' | 'DELETE'; data?: object; bytes?: Blob; consentVersion?: number; accept?: () => void };

/** The account route remains bound to one account and one resolved keepsake at a time. */
export class AccountArchiveClient {
  private generation = 0;
  private controller = new AbortController();
  private keys = new Map<string, string>();
  private uploadKeys = new WeakMap<Blob, string>();

  constructor(private currentAccountId: () => string) {}

  invalidate() {
    this.generation++;
    this.controller.abort();
    this.controller = new AbortController();
    this.keys.clear();
    this.uploadKeys = new WeakMap();
  }

  capture() {
    const generation = this.generation;
    const accountId = this.currentAccountId();
    return () => {
      if (!accountId || generation !== this.generation || this.controller.signal.aborted || accountId !== this.currentAccountId()) {
        throw new DOMException('Account context changed', 'AbortError');
      }
    };
  }

  private async send(path: string, options: MutationOptions = {}) {
    const method = options.method ?? 'GET';
    const allowed = method === 'GET'
      ? readPath.test(path)
      : method === 'DELETE'
        ? mutationPath.test(path) && (ownedRoot.test(path) || /\/(?:recovery-links|share-links|portrait)$/.test(path))
          || accountAssociation.test(path)
        : method === 'POST' && mutationPath.test(path) && !ownedRoot.test(path) && !path.endsWith('/portrait');
    if (!allowed) throw new GuestError(400);

    const check = this.capture();
    check();
    const headers = new Headers({ Accept: 'application/json' });
    let body: BodyInit | undefined;
    let identity = '';
    if (method !== 'GET') {
      if (options.bytes) {
        if (!this.uploadKeys.has(options.bytes)) this.uploadKeys.set(options.bytes, secureUuid());
        identity = `${method}:${path}:${this.uploadKeys.get(options.bytes)}:${options.consentVersion ?? ''}`;
        body = options.bytes;
        headers.set('Content-Type', options.bytes.type || 'application/octet-stream');
        if (options.consentVersion !== undefined) headers.set('X-DV10-Consent', String(options.consentVersion));
      } else {
        body = JSON.stringify({ schemaVersion: 1, ...options.data });
        identity = `${method}:${path}:${body}`;
        headers.set('Content-Type', 'application/json');
      }
      if (!this.keys.has(identity)) this.keys.set(identity, secureUuid());
      headers.set('Idempotency-Key', this.keys.get(identity)!);
    }

    const response = await accountTransport(path, {
      method,
      headers,
      body,
      signal: this.controller.signal,
      redirect: 'error',
    });
    check();
    if (!response.ok) {
      if (method !== 'GET' && response.status < 500) this.keys.delete(identity);
      throw new GuestError(response.status);
    }
    return { response, check, complete: () => { if (method !== 'GET') this.keys.delete(identity); } };
  }

  async json<T>(path: string, options: MutationOptions = {}) {
    const { response, check, complete } = await this.send(path, options);
    const value = response.status === 204 ? {} : await response.json();
    check();
    if ((options.method ?? 'GET') !== 'GET') options.accept?.();
    complete();
    return value as T;
  }

  async blob(path: string, allowedMedia: readonly string[]) {
    const { response, check } = await this.send(path);
    const value = await response.blob();
    check();
    if (!allowedMedia.includes(mediaEssence(value.type))) throw new GuestError(415);
    return value;
  }
}

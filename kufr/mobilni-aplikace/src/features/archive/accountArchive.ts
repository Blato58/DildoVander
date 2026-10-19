import { accountTransport } from '../identity/session';
import { GuestError, mediaEssence } from './client';

export class AccountArchiveClient {
  private generation = 0;
  private controller = new AbortController();
  constructor(private currentAccountId: () => string) {}
  invalidate() { this.generation++; this.controller.abort(); this.controller = new AbortController(); }
  capture() {
    const generation = this.generation; const accountId = this.currentAccountId();
    return () => {
      if (!accountId || generation !== this.generation || this.controller.signal.aborted || accountId !== this.currentAccountId()) throw new DOMException('Account context changed', 'AbortError');
    };
  }
  private async read(path: string) {
    if (!/^\/app\/v1\/archive\/associations\/[0-9a-f-]{36}(?:\/assets\/[0-9a-f-]{36})?$/i.test(path)) throw new GuestError(400);
    const check = this.capture(); check();
    const response = await accountTransport(path, { method: 'GET', signal: this.controller.signal, redirect: 'error' }); check();
    if (!response.ok) throw new GuestError(response.status);
    return { response, check };
  }
  async json<T>(path: string) {
    const { response, check } = await this.read(path); const value = await response.json(); check(); return value as T;
  }
  async blob(path: string, allowedMedia: readonly string[]) {
    const { response, check } = await this.read(path); const value = await response.blob(); check();
    if (!allowedMedia.includes(mediaEssence(value.type))) throw new GuestError(415);
    return value;
  }
}

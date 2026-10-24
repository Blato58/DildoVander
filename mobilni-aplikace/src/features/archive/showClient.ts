import { GuestError, isUuid } from './client';
import { secureUuid } from '../../application/identifiers.mjs';
import { projectDuel, type DuelAccess, type DuelAction, type Duel } from './showContracts';

/** Duel grants never use owner cookies, account credentials or bootstrap. */
export class DuelClient {
  private generation = 0;
  private controller = new AbortController();
  private operations = new Map<string, string>();
  constructor(private transport: typeof fetch = (input, init) => fetch(input, init), private accountHistory: () => boolean = () => false) {}
  invalidate() { this.generation++; this.controller.abort(); this.controller = new AbortController(); this.operations.clear(); }
  capture() {
    const generation = this.generation;
    return () => { if (generation !== this.generation || this.controller.signal.aborted) throw new DOMException('Duel changed', 'AbortError'); };
  }
  private async request(id: string, options: RequestInit = {}, operation: '' | '/choices' | '/join' = '') {
    if (this.accountHistory() && (operation || options.method === 'POST')) throw new GuestError(403);
    if (!isUuid(id)) throw new GuestError(400);
    const check = this.capture(); check();
    const response = await this.transport(`${this.accountHistory() ? '/app/v1/customer/duels' : '/guest/v1/duels'}/${id}${operation}`, {
      ...options, credentials: 'omit', cache: 'no-store', redirect: 'error', signal: this.controller.signal,
    }); check();
    if (!response.ok) throw new GuestError(response.status);
    const value = await response.json(); check(); return projectDuel(value, id);
  }
  read(id: string) { return this.request(id); }
  async join(view: Duel, access: DuelAccess) {
    if (!/^[A-Za-z0-9_-]{43}$/.test(access.token) || !['left', 'right'].includes(access.side)) throw new GuestError(400);
    const key = `${view.id}:join:${access.side}`;
    if (!this.operations.has(key)) this.operations.set(key, secureUuid());
    const result = await this.request(view.id, { method: 'POST', headers: { Accept: 'application/json', 'Content-Type': 'application/json', Authorization: `Bearer ${access.token}` },
      body: JSON.stringify({ side: access.side, consentConfirmed: true, operationKey: this.operations.get(key) }) }, '/join');
    this.operations.delete(key); return result;
  }
  async choose(view: Duel, access: DuelAccess, action: DuelAction) {
    if (!/^[A-Za-z0-9_-]{43}$/.test(access.token) || !['left', 'right'].includes(access.side)
      || !['strike', 'trick', 'shield'].includes(action)) throw new GuestError(400);
    const key = `${view.id}:${view.revision}:${view.round}:${access.side}:${action}`;
    if (!this.operations.has(key)) this.operations.set(key, secureUuid());
    const result = await this.request(view.id, { method: 'POST', headers: { Accept: 'application/json', 'Content-Type': 'application/json', Authorization: `Bearer ${access.token}` },
      body: JSON.stringify({ side: access.side, round: view.round, revision: view.revision, action, operationKey: this.operations.get(key) }) }, '/choices');
    this.operations.delete(key); return result;
  }
}

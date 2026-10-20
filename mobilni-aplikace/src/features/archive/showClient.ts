import { GuestError, isUuid } from './client';
import { secureUuid } from '../../application/identifiers.mjs';
import { projectDuel, type DuelAccess, type DuelAction, type Duel } from './showContracts';

/** Duel grants never use owner cookies, account credentials or bootstrap. */
export class DuelClient {
  private generation = 0;
  private controller = new AbortController();
  private operations = new Map<string, string>();
  constructor(private transport: typeof fetch = (input, init) => fetch(input, init)) {}
  invalidate() { this.generation++; this.controller.abort(); this.controller = new AbortController(); this.operations.clear(); }
  capture() {
    const generation = this.generation;
    return () => { if (generation !== this.generation || this.controller.signal.aborted) throw new DOMException('Duel changed', 'AbortError'); };
  }
  private async request(id: string, options: RequestInit = {}) {
    if (!isUuid(id)) throw new GuestError(400);
    const check = this.capture(); check();
    const response = await this.transport(`/guest/v1/duels/${id}${options.method === 'POST' ? '/choices' : ''}`, {
      ...options, credentials: 'omit', cache: 'no-store', redirect: 'error', signal: this.controller.signal,
    }); check();
    if (!response.ok) throw new GuestError(response.status);
    const value = await response.json(); check(); return projectDuel(value, id);
  }
  read(id: string) { return this.request(id); }
  async choose(view: Duel, access: DuelAccess, action: DuelAction) {
    if (!/^[A-Za-z0-9_-]{43}$/.test(access.token) || !['left', 'right'].includes(access.side)
      || !['strike', 'trick', 'shield'].includes(action)) throw new GuestError(400);
    const key = `${view.id}:${view.revision}:${view.round}:${access.side}:${action}`;
    if (!this.operations.has(key)) this.operations.set(key, secureUuid());
    const result = await this.request(view.id, { method: 'POST', headers: { Accept: 'application/json', 'Content-Type': 'application/json', Authorization: `Bearer ${access.token}` },
      body: JSON.stringify({ side: access.side, round: view.round, revision: view.revision, action, operationKey: this.operations.get(key) }) });
    this.operations.delete(key); return result;
  }
}

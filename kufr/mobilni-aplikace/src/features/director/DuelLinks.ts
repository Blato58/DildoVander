export type DuelSide = 'left' | 'right';
export interface DuelGrant { token: string; expiresAt: string }

// Origin comes from configured backend state, never the current page or Host header.
export function duelPageUrl(publicOrigin: string | undefined, duelId: string,
  play?: { side: DuelSide; token: string }): string | null {
  if (!publicOrigin || !/^[A-Za-z0-9_-]{1,128}$/.test(duelId)) return null;
  try {
    const origin = new URL(publicOrigin);
    const loopback = ['localhost', '127.0.0.1', '[::1]'].includes(origin.hostname);
    if ((origin.protocol !== 'https:' && !(origin.protocol === 'http:' && loopback))
      || origin.username || origin.password || origin.search || origin.hash || !['', '/'].includes(origin.pathname)) return null;
    const url = new URL('/keepsake/duel.html', origin);
    url.searchParams.set('duel', duelId);
    if (play) {
      if (!['left', 'right'].includes(play.side) || !/^[A-Za-z0-9_-]{43}$/.test(play.token)) return null;
      url.hash = new URLSearchParams({ grant: play.token, side: play.side }).toString();
    }
    return url.href;
  } catch { return null; }
}

// Ephemeral operator copies. Clearing these does not claim to revoke a server grant.
export class DuelGrantMemory {
  private scope = '';
  private grants: Partial<Record<DuelSide, DuelGrant>> = {};

  bind(eveningId: string, duelId: string): void {
    const scope = `${eveningId}\u0000${duelId}`;
    if (scope !== this.scope) { this.clear(); this.scope = scope; }
  }
  remember(side: DuelSide, grant: DuelGrant): void {
    if (!this.scope || !['left', 'right'].includes(side) || !/^[A-Za-z0-9_-]{43}$/.test(grant.token)
      || !Number.isFinite(Date.parse(grant.expiresAt)) || Date.parse(grant.expiresAt) <= Date.now()) {
      throw new Error('Server nevrátil platný časově omezený odkaz soupeře.');
    }
    this.grants[side] = { ...grant };
  }
  get(side: DuelSide): DuelGrant | undefined {
    const grant = this.grants[side];
    if (grant && Date.parse(grant.expiresAt) <= Date.now()) { delete this.grants[side]; return undefined; }
    return grant ? { ...grant } : undefined;
  }
  remove(side: DuelSide): void { delete this.grants[side]; }
  clear(): void { this.grants = {}; this.scope = ''; }
}

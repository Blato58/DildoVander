export type DirectorTransport = (path: string, init?: RequestInit) => Promise<Response>;
const uuid = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}';
function listPath(suffix:string):boolean {
 if(!/^\/(identities|prophecies|duels)\?/.test(suffix))return false;
 const query=new URLSearchParams(suffix.slice(suffix.indexOf('?')+1));
 const entries=Array.from(query);const value=query.get('eveningId');
 return entries.length===1 && entries[0]?.[0]==='eveningId' && !!value && value.length<=240 && !/[\x00-\x1f\x7f]/.test(value);
}
export function sensitiveDirectorPath(suffix: string): boolean {
  return ['/end-evening', '/offline-outcomes'].includes(suffix)
    || /^\/prophecies\/[A-Za-z0-9._-]{1,160}\/reveal$/.test(suffix)
    || new RegExp(`^/duels/${uuid}/grants(?:/revoke)?$`).test(suffix);
}
function allowed(suffix: string, post: boolean): boolean {
  return post ? ['/commands', '/end-evening', '/offline-outcomes', '/duels'].includes(suffix)
    || /^\/prophecies\/[A-Za-z0-9._-]{1,160}\/reveal$/.test(suffix)
    || new RegExp(`^/duels/${uuid}/(?:start|cancel|grants|grants/revoke)$`).test(suffix)
    : suffix === '' || listPath(suffix)
      || new RegExp(`^/duels/${uuid}$`).test(suffix);
}
const messages: Record<string, string> = {
  recent_passkey_required: 'Potvrďte znovu svůj passkey v Nastavení.',
  confirmation_required: 'Tato změna vyžaduje výslovné potvrzení.',
  stale_session: 'Návštěva se změnila. Obnovte stav; zásah se neopakuje.',
  selection_locked_or_unavailable: 'Návrh je přijatý nebo hůlka není dostupná.',
  state_conflict_or_visit_active: 'Návštěva běží nebo se změnila revize. Obnovte stav.',
  session_active: 'Návštěva stále běží. Nejprve ji bezpečně dokončete.',
  director_recovery_required: 'Kufr vyžaduje místní bezpečnostní zotavení.',
};
export class DirectorRequestError extends Error {
  constructor(readonly status: number, readonly code?: string) {
    super(code && messages[code] || (status === 401 || status === 403 ? 'Účet nemá požadované oprávnění.'
      : status === 409 ? 'Konflikt revize. Obnovte stav před dalším povelem.'
        : status === 410 ? 'Povel již vypršel.' : `Server odmítl požadavek (${status}).`));
  }
}
/** Same-origin account transport only. Device/account changes abort and fence even noncooperative responses. */
export class DirectorClient {
  private generation = 0;
  private requests = new Set<AbortController>();
  deviceId = '';
  constructor(private transport: DirectorTransport, deviceId = '') { if (deviceId) this.bind(deviceId); }
  bind(deviceId: string): void {
    if (!/^[A-Za-z0-9._-]{1,128}$/.test(deviceId) || ['.', '..'].includes(deviceId)) throw new Error('Neplatná identita kufru.');
    this.forget(); this.deviceId = deviceId;
  }
  forget(): void { ++this.generation; this.requests.forEach(request => request.abort()); this.requests.clear(); this.deviceId = ''; }
  async request<T>(suffix = '', body?: unknown, confirmed = false): Promise<T> {
    if (!this.deviceId || !allowed(suffix, body !== undefined)) throw new Error('Neplatná cesta režie.');
    if (body !== undefined && sensitiveDirectorPath(suffix) && !confirmed) throw new DirectorRequestError(403, 'confirmation_required');
    const generation = this.generation, deviceId = this.deviceId;
    const controller = new AbortController(); this.requests.add(controller);
    const timer = setTimeout(() => controller.abort(), 15000);
    const current = () => { if (generation !== this.generation || deviceId !== this.deviceId || controller.signal.aborted) throw new DOMException('Kontext režie se změnil.', 'AbortError'); };
    try {
      const response = await this.transport(`/v1/show/devices/${encodeURIComponent(deviceId)}${suffix}`, {
        method: body === undefined ? 'GET' : 'POST', signal: controller.signal, cache: 'no-store', redirect: 'error',
        headers: { 'X-DV-Device-Id': deviceId, Accept: 'application/json', ...(body === undefined ? {} : {'Content-Type': 'application/json'}),
          ...(confirmed && sensitiveDirectorPath(suffix) ? {'X-DV-Confirm': 'yes'} : {}) },
        ...(body === undefined ? {} : {body: JSON.stringify(body)}),
      });
      current(); const text = await response.text(); current();
      if (text.length > 1024 * 1024) throw new Error('Odpověď je příliš velká.');
      const result = text ? JSON.parse(text) : null;
      if (!response.ok) throw new DirectorRequestError(response.status, result?.code ?? result?.errorCode);
      return result as T;
    } finally { clearTimeout(timer); this.requests.delete(controller); }
  }
}

export interface ShowState {
  deviceId: string; eveningId: string; revision: number; sessionId?: string | null; sessionGeneration?: string | null;
  phase: string; mood: { dominant: string; locked: boolean }; tone: string; tempo: string; quiet: boolean;
  ambientEnabled: boolean; sceneId?: string | null; pendingCue?: unknown; occupiedBy?: string | null; updatedAt: string;
  sessionStartedAt?: string | null; moodScope?: 'automatic' | 'session' | 'evening';
  readyMask?: number; selectionLocked?: boolean; selectedSlot?: number | null;
  publicOrigin?: string | null;
  deviceStopRevision?: number;
  deviceSettings?: { stopRevision: number; observedAt: string } | null;
}

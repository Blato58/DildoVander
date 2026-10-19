import { nativeDebugBridge } from '../debug/NativeDebugBridge';

interface NativeShowBridge {
  configure(): Promise<void>;
  showRequest(options: { deviceId: string; suffix: string; method: string; body?: string }): Promise<{ status: number; body: unknown }>;
}
const bridge = nativeDebugBridge<NativeShowBridge>();

const rejectionMessages: Record<string, string> = {
  stale_session: 'Návštěva se změnila. Zásah nebyl proveden; obnovte stav.',
  selection_locked_or_unavailable: 'Návrh je již přijatý nebo hůlka není dostupná. Override nebyl proveden.',
  session_active: 'Návštěva stále běží. Nejprve ji bezpečně dokončete a obnovte stav.',
  state_conflict_or_visit_active: 'Návštěva stále běží nebo se změnil stav. Nejprve ji bezpečně dokončete a obnovte stav.',
  ambient_busy_or_disabled: 'Ambient vyžaduje povolený klidový stav kufru.',
  director_recovery_required: 'Kufr vyžaduje místní bezpečnostní zotavení.',
};
function rejectionMessage(status: number, code?: string): string {
  if (status === 401 || status === 403) return 'Oprávnění bylo odmítnuto.';
  if (code && rejectionMessages[code]) return rejectionMessages[code];
  if (status === 409) return 'Konflikt revize. Obnovte stav před dalším povelem.';
  if (status === 410) return 'Povel již vypršel.';
  return `Server odmítl požadavek (${status}).`;
}

export class DirectorRequestError extends Error {
  constructor(readonly status: number, readonly code?: string) { super(rejectionMessage(status, code)); }
}

// Web credentials live only in this object. Native credentials never leave Keychain.
export class DirectorClient {
  private address = '';
  private credential = '';
  constructor(readonly native: boolean, public deviceId = '') {}

  async configure(address: string, credential: string, deviceId: string): Promise<void> {
    if (!/^[A-Za-z0-9_-]{1,128}$/.test(deviceId)) throw new Error('Zadejte platnou identitu kufru.');
    if (this.native) { await bridge.configure(); }
    else {
      const url = new URL(address);
      if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && ['127.0.0.1', 'localhost'].includes(url.hostname)))
        || url.username || url.password || url.search || url.hash || !['', '/'].includes(url.pathname) || !credential) {
        throw new Error('Použijte HTTPS adresu serveru a operátorský credential.');
      }
      this.address = url.origin;
      this.credential = credential;
    }
    this.deviceId = deviceId;
  }

  forget(): void { this.credential = ''; this.address = ''; this.deviceId = ''; }

  async request<T>(suffix = '', body?: unknown): Promise<T> {
    if (!this.deviceId) throw new Error('Nejdříve ověřte operátorský přístup.');
    const method = body === undefined ? 'GET' : 'POST';
    let result: { status: number; body: unknown };
    if (this.native) {
      result = await bridge.showRequest({ deviceId: this.deviceId, suffix, method,
        ...(body === undefined ? {} : { body: JSON.stringify(body) }) });
    } else {
      const controller = new AbortController();
      const timeout = window.setTimeout(() => controller.abort(), 15000);
      try {
        const response = await fetch(`${this.address}/v1/show/devices/${encodeURIComponent(this.deviceId)}${suffix}`, {
          method, mode: 'cors', credentials: 'omit', cache: 'no-store', redirect: 'error', signal: controller.signal,
          headers: { Authorization: `Bearer ${this.credential}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
          ...(body === undefined ? {} : { body: JSON.stringify(body) }),
        });
        const text = await response.text();
        if (text.length > 1024 * 1024) throw new Error('Odpověď serveru je příliš velká.');
        result = { status: response.status, body: text ? JSON.parse(text) : null };
      } finally { window.clearTimeout(timeout); }
    }
    if (result.status < 200 || result.status >= 300) {
      const error = result.body as { code?: string; errorCode?: string } | null;
      throw new DirectorRequestError(result.status, error?.code ?? error?.errorCode);
    }
    return result.body as T;
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

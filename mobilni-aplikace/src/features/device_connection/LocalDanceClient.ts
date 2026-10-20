export interface DanceSettings {
  available: boolean;
  phase: 'unavailable' | 'idle' | 'starting' | 'playing' | 'stopping' | 'completed' | 'failed';
  track: string | null;
  positionMs: number;
  durationMs: number;
  bpm: number;
  error: string | null;
}

export interface LocalDanceState extends DanceSettings {
  pending: boolean;
  canStart: boolean;
  canStop: boolean;
  status: string;
}

interface Context {
  deviceId: string; bootId: string; revision: number; controlMode: string;
  phase: string; activeCommandId: string | null;
  conversation?: { activeTurnId: string | null };
  voice?: { phase: string };
  dance?: DanceSettings;
}

export const danceActive = (phase: DanceSettings['phase']): boolean =>
  ['starting', 'playing', 'stopping'].includes(phase);

const labels: Record<DanceSettings['phase'], string> = {
  unavailable: 'Tanec není připravený. Ověřte SD kartu, reproduktor a efekty kufru.',
  idle: 'Kufr vybere jednu ze skladeb na SD kartě.',
  starting: 'Kufr vybírá skladbu a připravuje tanec…',
  playing: 'Kufr přehrává skladbu a tančí do jejího rytmu.',
  stopping: 'Zastavuji hudbu a efekty…',
  completed: 'Skladba dohrála. Kufr je připravený na další tanec.',
  failed: 'Tanec se přerušil. Ověřte skladby na SD a stav kufru.',
};

const errors: Record<string, string> = {
  dance_manifest_unavailable: 'Na SD chybí seznam skladeb. Připravte kartu podle návodu.',
  dance_manifest_invalid: 'Seznam skladeb na SD není platný. Připravte kartu znovu.',
  dance_track_invalid: 'Na SD je nepodporovaná nebo poškozená skladba. Připravte zvukové soubory znovu.',
  dance_sd_unavailable: 'SD karta není dostupná. Ověřte kartu a její zapojení.',
  dance_sd_read_failed: 'Čtení SD karty se přerušilo. Ověřte kartu a skladby.',
  dance_audio_failed: 'Přehrávání se přerušilo. Ověřte reproduktor a stav kufru.',
  dance_audio_underrun: 'SD nestačila plynulému přehrávání. Ověřte kartu a spusťte nový tanec.',
  dance_effects_unavailable: 'Efekty nejsou připravené. Je potřeba místní kontrola a zotavení kufru.',
  dance_effects_failed: 'Efekty selhaly. Je potřeba místní kontrola a zotavení kufru.',
  dance_request_history_full: 'Kufr dosáhl počtu spuštění pro tento běh. Před dalším tancem jej restartujte.',
};

// A start is sent once. An ambiguous ACK never causes music to replay after
// reconnect. A subsequent snapshot establishes what the device is doing.
export class LocalDanceClient {
  private context: Context | null = null;
  private pending: string | null = null;
  private timer: number | null = null;
  private message = '';

  constructor(private readonly send: (message: Record<string, unknown>) => boolean,
    private readonly changed: () => void, private readonly close: () => void) {}

  update(context: Context): void {
    if (this.context?.bootId !== context.bootId) this.invalidate();
    this.context = context;
  }

  invalidate(): void {
    if (this.timer !== null) window.clearTimeout(this.timer);
    this.timer = null; this.pending = null; this.context = null; this.message = '';
  }

  state(): LocalDanceState {
    const dance: DanceSettings = this.context?.dance ?? {
      available: false, phase: 'unavailable', track: null, positionMs: 0, durationMs: 0, bpm: 0, error: null,
    };
    const connected = this.context?.dance != null;
    const voiceIdle = !this.context?.voice ||
      ['unavailable', 'idle', 'completed', 'failed'].includes(this.context.voice.phase);
    return { ...dance, pending: this.pending !== null,
      canStart: connected && dance.available && this.pending === null && !danceActive(dance.phase) &&
        this.context?.controlMode === 'standalone' && this.context.phase === 'ready' &&
        this.context.activeCommandId === null && !this.context.conversation?.activeTurnId && voiceIdle,
      canStop: connected && (danceActive(dance.phase) || this.pending !== null),
      status: this.message || (dance.error ? errors[dance.error] ?? labels[dance.phase] : labels[dance.phase]),
    };
  }

  start(): boolean {
    return this.state().canStart && this.request('dance.start', { expectedRevision: this.context!.revision });
  }

  stop(): boolean {
    return this.state().canStop && this.request('dance.stop', {});
  }

  receive(message: Record<string, unknown>): void {
    if (!this.context || message.bootId !== this.context.bootId || message.requestMessageId !== this.pending) return;
    if (message.type !== 'dance.ack' || typeof message.accepted !== 'boolean' || !message.dance) {
      this.close(); return;
    }
    if (this.timer !== null) window.clearTimeout(this.timer);
    this.timer = null; this.pending = null;
    this.context = { ...this.context, dance: message.dance as DanceSettings };
    this.message = message.accepted ? '' : 'Kufr tanec odmítl. Obnovte stav a uvolněte ruční řízení.';
    this.changed();
  }

  private request(type: string, fields: Record<string, unknown>): boolean {
    if (!this.context) return false;
    if (this.timer !== null) window.clearTimeout(this.timer);
    const messageId = crypto.randomUUID();
    this.pending = messageId;
    this.message = 'Čekám na potvrzení kufru…';
    const sent = this.send({ type, messageId, deviceId: this.context.deviceId, bootId: this.context.bootId, ...fields });
    if (!sent) { this.pending = null; this.message = 'Požadavek nebyl odeslán.'; }
    else this.timer = window.setTimeout(() => {
      this.message = 'Potvrzení nepřišlo. Připojte kufr znovu a obnovte stav.';
      this.changed(); this.close();
    }, 3000);
    this.changed(); return sent;
  }
}

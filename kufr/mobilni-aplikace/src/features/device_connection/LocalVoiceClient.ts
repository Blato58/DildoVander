export interface VoiceSettings {
  available: boolean;
  cameraAvailable?: boolean;
  photoWarning?: 'photo_unavailable' | 'photo_failed' | null;
  mode: 'off' | 'realtime' | 'gpt-live';
  voice: 'cedar' | 'marin' | 'coral';
  phase: 'unavailable' | 'idle' | 'starting' | 'photo_capture' | 'photo_upload' | 'photo_wait' | 'listening' | 'thinking' | 'speaking' | 'stopping' | 'completed' | 'failed';
  error: 'ai_unavailable' | 'audio_failed' | 'cost_limit' | null;
}
export interface LocalVoiceState extends VoiceSettings {
  pending: boolean;
  canConfigure: boolean;
  canStart: boolean;
  canStop: boolean;
  status: string;
}
interface Context {
  deviceId: string; bootId: string; revision: number; controlMode: string;
  phase: string; activeCommandId: string | null; voice?: VoiceSettings;
}
const active = (phase: VoiceSettings['phase']) =>
  ['starting', 'photo_capture', 'photo_upload', 'photo_wait', 'listening', 'thinking', 'speaking', 'stopping'].includes(phase);
const labels: Record<VoiceSettings['phase'], string> = {
  unavailable: 'Audio kufru není připravené. Je potřeba servisně ověřit zapojení mikrofonu a reproduktoru.',
  idle: 'Hlasový rozhovor je připravený.', starting: 'Spouštím hlasový rozhovor na kufru…',
  photo_capture: 'Pořizuji schválenou fotku kostýmu…', photo_upload: 'Předávám fotku serveru…',
  photo_wait: 'Zpracovávám fotku před rozhovorem…',
  listening: 'Kufr poslouchá. Odpovídejte nahlas.', thinking: 'Kufr připravuje odpověď…',
  speaking: 'Kufr mluví.', stopping: 'Zastavuji hlasový rozhovor…',
  completed: 'Hlasový rozhovor skončil.', failed: 'Hlasový rozhovor selhal.',
};

// Confirmation is correlated to one request and boot. Never replay an ambiguous
// start/audio request after reconnect; only snapshots establish device settings.
export class LocalVoiceClient {
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
  state(): LocalVoiceState {
    const voice: VoiceSettings = this.context?.voice ?? { available: false, mode: 'off', voice: 'marin', phase: 'unavailable', error: null };
    const connected = this.context?.voice != null;
    const changing = this.pending !== null;
    const error = voice.error === 'cost_limit' ? 'OpenAI účet omezil dostupnost AI. Ověřte jeho kredit a limity.'
      : voice.error === 'audio_failed' ? 'Audio nebo spojení se přerušilo. Spusťte nový rozhovor.'
      : voice.error === 'ai_unavailable' ? 'AI není dostupná. Ověřte síť a režimy serveru.' : '';
    return { ...voice, pending: changing,
      canConfigure: connected && !changing && !active(voice.phase),
      canStart: connected && !changing && voice.available && voice.mode !== 'off' && !active(voice.phase)
        && this.context?.controlMode === 'standalone' && this.context?.phase === 'ready'
        && this.context?.activeCommandId === null,
      canStop: connected && (active(voice.phase) || changing),
      status: this.message || error || (voice.photoWarning ? 'Fotka není dostupná; rozhovor pokračuje bez ní. ' : '') + labels[voice.phase],
    };
  }
  configure(mode: VoiceSettings['mode'], voice: VoiceSettings['voice']): boolean {
    if (!this.state().canConfigure || !['off', 'realtime', 'gpt-live'].includes(mode) ||
      !['cedar', 'marin', 'coral'].includes(voice) || mode !== 'off' && !this.state().available) return false;
    return this.request('voice.configure', { mode, voice });
  }
  start(imageConsent = false): boolean {
    if (imageConsent && this.context?.voice?.cameraAvailable !== true) return false;
    return this.state().canStart && this.request('voice.start', { expectedRevision: this.context!.revision,
      ...(imageConsent ? { imageConsent: true } : {}) });
  }
  stop(): boolean {
    if (!this.state().canStop) return false;
    // Stop may supersede an in-flight start/configure, without waiting for ACK.
    return this.request('voice.stop', {});
  }
  receive(message: Record<string, unknown>): void {
    if (!this.context || message.bootId !== this.context.bootId || message.requestMessageId !== this.pending) return;
    if (message.type !== 'voice.ack' || typeof message.accepted !== 'boolean' || !message.voice) { this.close(); return; }
    if (this.timer !== null) window.clearTimeout(this.timer);
    this.timer = null; this.pending = null;
    this.context = { ...this.context, voice: message.voice as VoiceSettings };
    this.message = message.accepted ? '' : 'Kufr požadavek odmítl. Obnovte stav; pro spuštění uvolněte ruční řízení.';
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

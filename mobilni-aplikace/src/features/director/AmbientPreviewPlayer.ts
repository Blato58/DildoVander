import { secureUuid } from '../../application/identifiers.mjs';

export interface AmbientPreviewState {
  busy: boolean;
  message: string;
  error: string;
  text: string;
  synthetic: boolean;
}
interface PreviewResult {
  operationId: string;
  status: 'pending' | 'ready' | 'failed';
  text: string;
  synthetic: boolean;
  wavBase64?: string;
  code?: string;
}
type Transport = (path: string, init: RequestInit) => Promise<Response>;
const errors: Record<string, string> = {
  speech_unavailable: 'Hlasový výstup serveru není dostupný.',
  preview_unavailable: 'Hlasový výstup serveru není dostupný.',
  preview_busy: 'Hlasový výstup je obsazený. Zkuste poslech později.',
  preview_expired: 'Zvukový náhled vypršel. Přehrávání nebylo potvrzené.',
};

/** Phone-only playback. It never sends a Show command or a device playback ACK. */
export class AmbientPreviewPlayer {
  state: AmbientPreviewState = { busy: false, message: '', error: '', text: '', synthetic: false };
  private generation = 0;
  private lifetime?: AbortController;
  private audio?: AudioContext;
  private source?: AudioBufferSourceNode;
  private playbackDeadline?: ReturnType<typeof setTimeout>;

  constructor(private deviceId: string, private transport: Transport,
    private changed: (state: AmbientPreviewState) => void) {}

  private publish(update: Partial<AmbientPreviewState>): void {
    this.state = { ...this.state, ...update };
    this.changed(this.state);
  }

  stop(message = 'Poslech v telefonu zastaven.'): void {
    ++this.generation;
    clearTimeout(this.playbackDeadline); this.playbackDeadline = undefined;
    this.lifetime?.abort(); this.lifetime = undefined;
    if (this.source) {
      this.source.onended = null;
      try { this.source.stop(); } catch { /* Playback may already have ended. */ }
      this.source.disconnect(); this.source = undefined;
    }
    if (this.audio) {
      this.audio.onstatechange = null;
      void this.audio.close().catch(() => {}); this.audio = undefined;
    }
    this.publish({ busy: false, message, text: '', error: '', synthetic: false });
  }

  async play(): Promise<void> {
    if (this.state.busy) return;
    const own = ++this.generation;
    const lifetime = new AbortController(); this.lifetime = lifetime;
    const current = () => own === this.generation && !lifetime.signal.aborted;
    const operationId = secureUuid();
    const root = `/v1/show/devices/${encodeURIComponent(this.deviceId)}/ambient-previews`;
    let deadline: ReturnType<typeof setTimeout> | undefined;
    this.publish({ busy: true, message: 'Aktivuji zvuk telefonu…', error: '', text: '', synthetic: false });
    try {
      // Unlock inside the click, before awaiting authentication/network work.
      // The preview must not finish while iOS is still resuming the graph.
      this.audio = new AudioContext();
      const audio = this.audio;
      await audio.resume();
      if (!current()) return;
      if (audio.state !== 'running') throw new Error('Zvuk telefonu se nepodařilo aktivovat.');
      audio.onstatechange = () => {
        if (current() && audio.state !== 'running') {
          this.stop(); this.publish({ error: 'Zvuk byl přerušen. Nový poslech spusťte ručně.' });
        }
      };
      deadline = setTimeout(() => {
        if (current()) {
          this.stop(); this.publish({ error: 'Příprava zvuku vypršela. Požadavek se automaticky neopakuje.' });
        }
      }, 15000);
      const request = async (path: string, post = false): Promise<PreviewResult> => {
        const response = await this.transport(path, {
          method: post ? 'POST' : 'GET', signal: lifetime.signal, cache: 'no-store', redirect: 'error',
          headers: { 'X-DV-Device-Id': this.deviceId, ...(post ? { 'Content-Type': 'application/json' } : {}) },
          ...(post ? { body: JSON.stringify({ operationId }) } : {}),
        });
        if (!current()) throw new DOMException('Poslech zrušen.', 'AbortError');
        const text = await response.text();
        if (text.length > 750000) throw new Error('Zvukový náhled překročil povolenou velikost.');
        const result = JSON.parse(text);
        if (!response.ok) throw new Error(errors[result.code] || `Zvukový náhled není dostupný (${response.status}).`);
        if (result.operationId !== operationId || !['pending', 'ready', 'failed'].includes(result.status)) throw new Error('Neplatná odpověď zvukového náhledu.');
        return result;
      };
      this.publish({ message: 'Připravuji ambient pro poslech v telefonu…' });
      let result = await request(root, true);
      while (current() && result.status === 'pending') {
        await new Promise<void>(resolve => {
          const finish = () => { clearTimeout(timer); lifetime.signal.removeEventListener('abort', finish); resolve(); };
          const timer = setTimeout(finish, 250);
          lifetime.signal.addEventListener('abort', finish, { once: true });
        });
        if (!current()) return;
        result = await request(`${root}/${operationId}`);
      }
      if (!current()) return;
      if (result.status !== 'ready' || typeof result.wavBase64 !== 'string') throw new Error(errors[result.code ?? ''] || 'Server zvuk nepřipravil. Požadavek se automaticky neopakuje.');
      if (result.wavBase64.length > 683000 || typeof result.text !== 'string' || result.text.length > 500 || typeof result.synthetic !== 'boolean') throw new Error('Neplatný zvukový náhled.');
      const bytes = Uint8Array.from(atob(result.wavBase64), character => character.charCodeAt(0));
      const buffer = await audio.decodeAudioData(bytes.buffer);
      if (!current()) return;
      if (audio.state !== 'running' || buffer.duration <= 0 || buffer.duration > 8 || buffer.numberOfChannels !== 1) throw new Error('Zvukový náhled nelze bezpečně přehrát.');
      clearTimeout(deadline); deadline = undefined;
      const source = audio.createBufferSource(); this.source = source;
      source.buffer = buffer; source.connect(audio.destination);
      source.onended = () => {
        if (!current()) return;
        this.stop('Poslech v telefonu dokončen.');
      };
      this.publish({ message: 'Přehrávám ambient v telefonu.', text: result.text, synthetic: result.synthetic });
      source.start();
      this.playbackDeadline = setTimeout(() => {
        if (current()) {
          this.stop(); this.publish({ error: 'Dokončení poslechu nebylo potvrzené.' });
        }
      }, 9000);
    } catch (cause) {
      if (!current()) return;
      this.stop(); this.publish({ error: cause instanceof Error ? cause.message : String(cause) });
    } finally {
      if (deadline) clearTimeout(deadline);
    }
  }
}

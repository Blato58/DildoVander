import type { LabSession, SocketTicket, Turn } from './contracts';
import { mediaController } from '../workshop/media.mjs';
import { classifyLabFailure } from './errors';

export type VoicePhase = 'idle' | 'connecting' | 'listening' | 'speaking' | 'ended' | 'error';
export interface VoiceObserver {
  phase(value: VoicePhase): void;
  transcript(role: 'user' | 'assistant', delta: string): void;
  turn(value: Turn): void;
  session(value: LabSession): void;
  error(code: string, recoverable: boolean): void;
  closed(): void;
  interrupted?(): void;
}

export function socketUrl(ticket: SocketTicket, sessionId: string, origin: string): string {
  const expected = new URL(origin);
  const url = new URL(ticket.url, expected);
  const secureOrigin = url.protocol === 'wss:' ? 'https:' : url.protocol === 'ws:' ? 'http:' : '';
  const expectedPath = `/app/v1/persona-lab/sessions/${encodeURIComponent(sessionId)}/live`;
  if (!secureOrigin || expected.protocol !== secureOrigin || expected.host !== url.host || url.pathname !== expectedPath
    || url.username || url.password || url.hash || !url.searchParams.get('ticket'))
    throw Object.assign(new Error('Server vrátil neplatnou adresu hlasového spojení.'), { code: 'invalid_socket_address' });
  return url.href;
}

export class PcmResampler {
  private remainder = new Float32Array(0);
  private position = 0;
  constructor(private sourceRate: number) {}
  convert(input: Float32Array): Int16Array {
    const samples = new Float32Array(this.remainder.length + input.length);
    samples.set(this.remainder); samples.set(input, this.remainder.length);
    const step = this.sourceRate / 24000;
    const result: number[] = [];
    while (this.position + 1 < samples.length) {
      const index = Math.floor(this.position); const fraction = this.position - index;
      const value = samples[index] * (1 - fraction) + samples[index + 1] * fraction;
      result.push(Math.round(Math.max(-1, Math.min(1, value)) * (value < 0 ? 32768 : 32767)));
      this.position += step;
    }
    const consumed = Math.min(Math.floor(this.position), samples.length);
    this.remainder = samples.slice(consumed); this.position -= consumed;
    return Int16Array.from(result);
  }
}

/** One owned browser/WebView audio pipeline; it never acquires a device session. */
export class LabVoice {
  private phase: VoicePhase = 'idle';
  private generation = 0;
  private socket: WebSocket | null = null;
  private context: AudioContext | null = null;
  private stream: MediaStream | null = null;
  private source: MediaStreamAudioSourceNode | null = null;
  private processor: AudioWorkletNode | null = null;
  private silentGain: GainNode | null = null;
  private playback = new Set<AudioBufferSourceNode>();
  private nextAudioTime = 0;
  private muted = false;
  private speakingInput = false;
  private quietSamples = 0;
  private ready = false;
  private terminal = false;
  private awaitingInterrupt = false;
  private responseStartedAt: number | null = null;
  private responseComplete = true;
  private connectionTimeout: ReturnType<typeof setTimeout> | null = null;
  constructor(private observer: VoiceObserver) {}
  private transition(next: VoicePhase, generation = this.generation): void {
    if (generation !== this.generation || next === this.phase) return;
    const allowed: Record<VoicePhase, VoicePhase[]> = {
      idle: ['connecting', 'ended', 'error'], connecting: ['listening', 'error', 'ended'],
      listening: ['speaking', 'error', 'ended'], speaking: ['listening', 'error', 'ended'],
      ended: ['connecting'], error: ['connecting', 'ended'],
    };
    if (!allowed[this.phase].includes(next)) return;
    this.phase = next; this.observer.phase(next);
  }

  async connect(ticket: () => Promise<SocketTicket>, sessionId: string, origin: string): Promise<void> {
    this.dispose();
    const generation = this.generation;
    this.terminal = false; this.transition('connecting', generation);
    const current = () => generation === this.generation;
    try {
      if (!navigator.mediaDevices?.getUserMedia || typeof AudioContext === 'undefined')
        throw new Error('microphone_unavailable');
      // Called directly from a click so iOS can unlock audio before permission awaits.
      const context = new AudioContext({ sampleRate: 24000 }); this.context = context;
      await context.resume(); if (!current()) return;
      context.onstatechange = () => {
        if (current() && this.ready && ['suspended', 'interrupted', 'closed'].includes(context.state as string))
          this.fail('audio_interrupted', false);
      };
      const stream = await navigator.mediaDevices.getUserMedia({ audio: {
        channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true,
      } });
      if (!current()) { stream.getTracks().forEach(track => track.stop()); return; }
      this.stream = stream;
      stream.getTracks().forEach(track => track.addEventListener('ended', () => { if (current()) this.fail('microphone_disconnected', false); }));
      const installed = document.querySelector<HTMLMetaElement>('meta[name="kufr-capture-worklet"]')?.content;
      if (!context.audioWorklet) throw new Error('microphone_unavailable');
      if (installed) await context.audioWorklet.addModule(mediaController.captureWorkletUrl(installed, document.baseURI));
      else {
        const url = URL.createObjectURL(new Blob([mediaController.CAPTURE_WORKLET_SOURCE], { type: 'text/javascript' }));
        try { await context.audioWorklet.addModule(url); } finally { URL.revokeObjectURL(url); }
      }
      if (!current()) return;
      const credentials = await ticket(); if (!current()) return;
      const socket = new WebSocket(socketUrl(credentials, sessionId, origin));
      socket.binaryType = 'arraybuffer'; this.socket = socket;
      const resampler = new PcmResampler(context.sampleRate);
      const source = context.createMediaStreamSource(stream); this.source = source;
      // Reuse the application's published capture worklet. The laboratory owns
      // only its authenticated test socket, never the simulator's session owner.
      const processor = new AudioWorkletNode(context, 'twin-capture', { channelCount: 1, channelCountMode: 'explicit' }); this.processor = processor;
      const silentGain = context.createGain(); this.silentGain = silentGain; silentGain.gain.value = 0;
      source.connect(processor); processor.connect(silentGain); silentGain.connect(context.destination);
      let pendingPcm = new Int16Array(0);
      processor.port.onmessage = event => {
        if (!current() || !this.ready || this.muted || socket.readyState !== WebSocket.OPEN) return;
        const samples = event.data as Float32Array;
        const energy = Math.sqrt(samples.reduce((sum, value) => sum + value * value, 0) / samples.length);
        if (energy > 0.035) {
          if (!this.speakingInput && this.playback.size) this.interrupt();
          this.speakingInput = true; this.quietSamples = 0;
        } else if ((this.quietSamples += samples.length) > context.sampleRate * .3) this.speakingInput = false;
        if (socket.bufferedAmount > 262144) { this.fail('connection_backpressure', true); return; }
        const pcm = resampler.convert(samples);
        const combined = new Int16Array(pendingPcm.length + pcm.length); combined.set(pendingPcm); combined.set(pcm, pendingPcm.length);
        let offset = 0;
        while (combined.length - offset >= 480) { socket.send(combined.slice(offset, offset + 480).buffer); offset += 480; }
        pendingPcm = combined.slice(offset);
      };
      this.connectionTimeout = setTimeout(() => { if (current() && !this.ready) this.fail('connection_timeout', true); }, 15000);
      socket.onmessage = event => {
        if (!current()) return;
        if (event.data instanceof ArrayBuffer) { this.play(event.data); return; }
        try {
          const value = JSON.parse(String(event.data));
          if (value.type === 'ready' && !this.ready && this.phase === 'connecting') { this.clearTimeout(); this.ready = true; this.transition('listening', generation); }
          else if (value.type === 'transcript' && ['user', 'assistant'].includes(value.role) && typeof value.delta === 'string') {
            if (!(this.awaitingInterrupt && value.role === 'assistant')) this.observer.transcript(value.role, value.delta);
          }
          else if (value.type === 'turn' && value.turn) { if (value.turn.role === 'assistant') this.responseComplete = true; this.observer.turn(value.turn); }
          else if (value.type === 'interrupted') { this.clearTimeout(); this.awaitingInterrupt = false; this.responseComplete = true; this.responseStartedAt = null; this.observer.interrupted?.(); }
          else if (value.type === 'session' && value.session) this.observer.session(value.session);
          else if (value.type === 'error') {
            const code = typeof value.code === 'string' ? value.code : 'lab_provider_error';
            this.fail(code, classifyLabFailure(code).recoverable);
          }
          else if (value.type === 'ended') { this.terminal = true; this.dispose(); this.transition('ended'); this.observer.closed(); }
        } catch { this.fail('invalid_server_event', false); }
      };
      socket.onerror = () => { if (current()) this.fail('connection_failed', false); };
      socket.onclose = event => {
        if (!current()) return;
        const terminal = this.terminal; this.dispose();
        this.transition(terminal ? 'ended' : 'error');
        const code = event.code === 1008 ? 'connection_policy_rejected'
          : [1002, 1003, 1007, 1009].includes(event.code) ? 'connection_protocol_rejected' : 'connection_closed';
        if (!terminal) this.observer.error(code, classifyLabFailure(code).recoverable);
        this.observer.closed();
      };
    } catch (error) {
      if (!current()) return;
      const code = error instanceof DOMException && ['NotAllowedError', 'PermissionDeniedError'].includes(error.name)
        ? 'microphone_permission' : error && typeof error === 'object' && 'code' in error && typeof error.code === 'string'
          ? error.code : error instanceof Error && error.message === 'microphone_unavailable' ? 'microphone_unavailable' : 'audio_setup_failed';
      this.fail(code, classifyLabFailure(code).recoverable);
    }
  }

  private clearTimeout(): void { if (this.connectionTimeout) clearTimeout(this.connectionTimeout); this.connectionTimeout = null; }
  private fail(code: string, recoverable: boolean): void {
    this.dispose(); this.transition('error'); this.observer.error(code, recoverable && classifyLabFailure(code).recoverable); this.observer.closed();
  }
  private play(bytes: ArrayBuffer): void {
    const context = this.context;
    if (!context || !this.ready || this.awaitingInterrupt || !bytes.byteLength || bytes.byteLength % 2 || bytes.byteLength > 1048576) return;
    if (this.nextAudioTime - context.currentTime > 15) { this.fail('audio_backpressure', true); return; }
    const samples = new Int16Array(bytes); const buffer = context.createBuffer(1, samples.length, 24000);
    const channel = buffer.getChannelData(0);
    for (let index = 0; index < samples.length; index++) channel[index] = samples[index] / 32768;
    const node = context.createBufferSource(); node.buffer = buffer; node.connect(context.destination);
    const generation = this.generation;
    this.playback.add(node); this.transition('speaking', generation);
    node.onended = () => {
      this.playback.delete(node); node.disconnect();
      if (!this.playback.size && this.ready) this.transition('listening', generation);
    };
    const start = Math.max(context.currentTime, this.nextAudioTime);
    if (this.responseComplete) { this.responseStartedAt = start; this.responseComplete = false; }
    this.nextAudioTime = start + buffer.duration; node.start(start);
  }
  private stopPlayback(): void {
    for (const node of this.playback) { node.onended = null; try { node.stop(); } catch {} node.disconnect(); }
    this.playback.clear(); this.nextAudioTime = 0;
  }
  setMuted(value: boolean): void { this.muted = value; this.stream?.getAudioTracks().forEach(track => { track.enabled = !value; }); }
  interrupt(): void {
    if (!this.ready || this.socket?.readyState !== WebSocket.OPEN) { this.stopPlayback(); return; }
    const playedAudioMs = this.context && this.responseStartedAt !== null
      ? Math.max(0, Math.min(180000, Math.floor((Math.min(this.context.currentTime, this.nextAudioTime) - this.responseStartedAt) * 1000))) : 0;
    this.awaitingInterrupt = true;
    this.clearTimeout();
    const generation = this.generation;
    this.connectionTimeout = setTimeout(() => { if (generation === this.generation && this.awaitingInterrupt) this.fail('interruption_timeout', true); }, 5000);
    this.stopPlayback();
    this.socket.send(JSON.stringify({ type: 'cancel', playedAudioMs }));
    if (this.ready) this.transition('listening');
  }
  sendText(text: string): boolean {
    if (!this.ready || this.socket?.readyState !== WebSocket.OPEN) return false;
    this.socket.send(JSON.stringify({ type: 'text', text })); return true;
  }
  end(): void {
    if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify({ type: 'end' }));
    this.terminal = true; this.dispose(); this.transition('ended');
  }
  dispose(): void {
    ++this.generation; this.clearTimeout(); this.ready = false; this.muted = false;
    const socket = this.socket; this.socket = null;
    if (socket) { socket.onmessage = socket.onclose = socket.onerror = null; socket.close(); }
    this.stopPlayback();
    if (this.processor) { this.processor.port.onmessage = null; this.processor.port.close(); this.processor.disconnect(); }
    this.source?.disconnect(); this.silentGain?.disconnect();
    this.processor = null; this.source = null; this.silentGain = null;
    this.stream?.getTracks().forEach(track => track.stop()); this.stream = null;
    if (this.context) { this.context.onstatechange = null; void this.context.close().catch(() => {}); this.context = null; }
    this.speakingInput = false; this.quietSamples = 0;
    this.awaitingInterrupt = false; this.responseComplete = true; this.responseStartedAt = null;
    this.phase = 'idle';
  }
}

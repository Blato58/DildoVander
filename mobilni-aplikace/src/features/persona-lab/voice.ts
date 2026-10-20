import type { LabSession, SocketTicket, Turn } from './contracts';
import { mediaController } from '../workshop/media.mjs';
import { classifyLabFailure } from './errors';

export type VoicePhase = 'idle' | 'connecting' | 'waiting' | 'listening' | 'speaking' | 'ended' | 'error';
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

const audioRate = 24000;
const maximumPendingSamples = audioRate * 190;
const maximumRealtimeSamples = audioRate * 30;
const playbackBlockSamples = audioRate / 4;
interface ScheduledAudio { start: number; samples: number; channel: Float32Array }
interface AcknowledgedPlayback { id: string; received: number; ended: boolean }

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
  private playback = new Map<AudioBufferSourceNode, ScheduledAudio>();
  private pcm: Int16Array | null = null;
  private pcmRead = 0;
  private queuedSamples = 0;
  private completedSamples = 0;
  private playbackMode: 'acknowledged' | 'stream' | null = null;
  private streamComplete = true;
  private activePlayback: AcknowledgedPlayback | null = null;
  private retiredPlaybackIds: string[] = [];
  private playbackTimer: ReturnType<typeof setInterval> | null = null;
  private lastAudioClock = 0;
  private lastAudioProgress = 0;
  private nextAudioTime = 0;
  private muted = false;
  private speakingInput = false;
  private quietSamples = 0;
  private ready = false;
  private terminal = false;
  private awaitingInterrupt = false;
  private connectionTimeout: ReturnType<typeof setTimeout> | null = null;
  constructor(private observer: VoiceObserver) {}
  private transition(next: VoicePhase, generation = this.generation): void {
    if (generation !== this.generation || next === this.phase) return;
    const allowed: Record<VoicePhase, VoicePhase[]> = {
      idle: ['connecting', 'ended', 'error'], connecting: ['listening', 'error', 'ended'],
      waiting: ['listening', 'speaking', 'error', 'ended'],
      listening: ['waiting', 'speaking', 'error', 'ended'], speaking: ['waiting', 'listening', 'error', 'ended'],
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
          this.failPlayback('audio_interrupted');
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
      const address = new URL(socketUrl(credentials, sessionId, origin));
      address.searchParams.set('playback', '1');
      const socket = new WebSocket(address.href);
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
        if (!current() || !this.ready || this.awaitingInterrupt || this.muted || socket.readyState !== WebSocket.OPEN) return;
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
          if (value.type === 'ready' && !this.ready && this.phase === 'connecting') {
            if (!['acknowledged', 'stream'].includes(value.playbackMode)) { this.fail('invalid_server_event', false); return; }
            this.playbackMode = value.playbackMode;
            this.clearTimeout(); this.ready = true; this.transition('listening', generation);
          }
          else if (value.type === 'playback_started') this.startAcknowledgedPlayback(value.playbackId);
          else if (value.type === 'playback_end') this.completeAcknowledgedPlayback(value);
          else if (value.type === 'listening') {
            if (!this.ready || this.playbackMode !== 'acknowledged' || this.activePlayback || this.awaitingInterrupt) { this.fail('invalid_server_event', false); return; }
            this.transition('listening', generation);
          }
          else if (value.type === 'speech' && value.state === 'speech_stopped' && this.playbackMode === 'acknowledged') {
            if (!this.activePlayback) this.transition('waiting', generation);
          }
          else if (value.type === 'transcript' && ['user', 'assistant'].includes(value.role) && typeof value.delta === 'string') {
            if (!(this.awaitingInterrupt && value.role === 'assistant')) this.observer.transcript(value.role, value.delta);
          }
          else if (value.type === 'turn' && value.turn) {
            if (value.turn.role === 'assistant' && this.playbackMode === 'stream') { this.streamComplete = true; this.finishPlayback(); }
            this.observer.turn(value.turn);
          }
          else if (value.type === 'interrupted') {
            this.clearTimeout(); this.awaitingInterrupt = false;
            this.stopPlayback();
            if (this.activePlayback) { this.retirePlayback(this.activePlayback.id); this.activePlayback = null; }
            this.transition(this.playbackMode === 'acknowledged' ? 'waiting' : 'listening', generation);
            this.observer.interrupted?.();
          }
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
  private startAcknowledgedPlayback(id: unknown): void {
    if (!this.ready || this.playbackMode !== 'acknowledged' || typeof id !== 'string' || !id.length || id.length > 128
      || this.activePlayback || this.retiredPlaybackIds.includes(id) || this.awaitingInterrupt) {
      this.fail('invalid_server_event', false); return;
    }
    this.activePlayback = { id, received: 0, ended: false };
    this.completedSamples = 0;
    this.transition('waiting');
  }
  private completeAcknowledgedPlayback(value: { playbackId?: unknown; totalSamples?: unknown; final?: unknown }): void {
    if (typeof value.playbackId !== 'string') { this.fail('invalid_server_event', false); return; }
    if (this.retiredPlaybackIds.includes(value.playbackId)) return;
    const active = this.activePlayback;
    if (!active || active.id !== value.playbackId || active.ended || !Number.isSafeInteger(value.totalSamples)
      || value.totalSamples !== active.received || typeof value.final !== 'boolean') {
      this.fail('invalid_server_event', false); return;
    }
    active.ended = true;
    this.finishPlayback();
  }
  private retirePlayback(id: string): void {
    this.retiredPlaybackIds.push(id);
    if (this.retiredPlaybackIds.length > 100) this.retiredPlaybackIds.shift();
  }
  private failPlayback(code: 'audio_backpressure' | 'audio_stalled' | 'audio_interrupted'): void {
    if (this.activePlayback && this.socket?.readyState === WebSocket.OPEN) {
      try { this.socket.send(JSON.stringify({ type: 'playback_error', playbackId: this.activePlayback.id, code })); } catch {}
    }
    this.fail(code, classifyLabFailure(code).recoverable);
  }
  private playedSamples(): number {
    const now = this.context?.currentTime ?? 0;
    let samples = this.completedSamples;
    for (const scheduled of this.playback.values())
      samples += Math.max(0, Math.min(scheduled.samples, Math.floor((now - scheduled.start) * audioRate + 1e-6)));
    return samples;
  }
  private play(bytes: ArrayBuffer): void {
    if (!this.context || !this.ready || this.awaitingInterrupt) return;
    if (!bytes.byteLength || bytes.byteLength % 2 || bytes.byteLength > 1048576
      || this.playbackMode === 'acknowledged' && (!this.activePlayback || this.activePlayback.ended)) {
      this.fail('invalid_server_event', false); return;
    }
    const samples = new Int16Array(bytes);
    if (this.playbackMode === 'stream') this.streamComplete = false;
    const pending = this.queuedSamples + [...this.playback.values()].reduce((sum, value) => sum + value.samples, 0);
    const capacity = this.playbackMode === 'acknowledged' ? maximumRealtimeSamples : maximumPendingSamples;
    if (pending + samples.length > capacity
      || this.activePlayback && this.activePlayback.received + samples.length > maximumRealtimeSamples) {
      this.failPlayback('audio_backpressure'); return;
    }
    // Store PCM16 in a fixed ring; tiny provider deltas cannot grow a list of
    // chunks or create an unbounded number of native audio nodes.
    if (!this.pcm) this.pcm = new Int16Array(capacity);
    const write = (this.pcmRead + this.queuedSamples) % this.pcm.length;
    const first = Math.min(samples.length, this.pcm.length - write);
    this.pcm.set(samples.subarray(0, first), write);
    this.pcm.set(samples.subarray(first), 0);
    this.queuedSamples += samples.length;
    if (this.activePlayback) this.activePlayback.received += samples.length;
    if (!this.playbackTimer) {
      this.lastAudioClock = this.playedSamples(); this.lastAudioProgress = performance.now();
      const generation = this.generation;
      this.playbackTimer = setInterval(() => {
        if (generation !== this.generation || !this.context) return;
        if (this.context.state !== 'running') { this.failPlayback('audio_interrupted'); return; }
        const played = this.playedSamples();
        if (played > this.lastAudioClock) { this.lastAudioClock = played; this.lastAudioProgress = performance.now(); }
        else if (performance.now() - this.lastAudioProgress >= 5000) { this.failPlayback('audio_stalled'); return; }
        this.pumpPlayback();
      }, 100);
    }
    this.pumpPlayback();
  }
  private pumpPlayback(): void {
    const context = this.context;
    if (!context || !this.pcm) return;
    const generation = this.generation;
    try {
      // The ring can hold a whole response, but Web Audio owns at most one
      // second. A fast provider response is independent of playback progress.
      while (this.queuedSamples && this.playback.size < 4 && this.nextAudioTime - context.currentTime < .75) {
        const length = Math.min(playbackBlockSamples, this.queuedSamples);
        const buffer = context.createBuffer(1, length, audioRate);
        const channel = buffer.getChannelData(0);
        for (let index = 0; index < length; index++) {
          channel[index] = this.pcm[this.pcmRead] / 32768;
          this.pcm[this.pcmRead] = 0;
          this.pcmRead = (this.pcmRead + 1) % this.pcm.length;
        }
        this.queuedSamples -= length;
        const node = context.createBufferSource(); node.buffer = buffer; node.connect(context.destination);
        const start = Math.max(context.currentTime, this.nextAudioTime);
        this.playback.set(node, { start, samples: length, channel });
        this.nextAudioTime = start + buffer.duration;
        node.onended = () => {
          if (generation !== this.generation || !this.playback.delete(node)) return;
          node.onended = null;
          this.completedSamples += length;
          channel.fill(0); node.disconnect();
          this.pumpPlayback(); this.finishPlayback();
        };
        this.transition('speaking', generation);
        if (generation !== this.generation) return;
        node.start(start);
      }
    } catch { this.fail('audio_setup_failed', false); }
  }
  private finishPlayback(): void {
    if (this.queuedSamples || this.playback.size || this.awaitingInterrupt) return;
    if (this.playbackTimer) clearInterval(this.playbackTimer);
    this.playbackTimer = null;
    const active = this.activePlayback;
    if (this.playbackMode === 'acknowledged') {
      if (!active?.ended) { this.transition('waiting'); return; }
      try { this.socket?.send(JSON.stringify({ type: 'playback_done', playbackId: active.id, playedSamples: this.completedSamples })); }
      catch { this.fail('connection_closed', true); return; }
      this.retirePlayback(active.id); this.activePlayback = null;
      this.transition('waiting');
    } else if (this.ready) {
      if (this.streamComplete) this.completedSamples = 0;
      this.transition(this.streamComplete ? 'listening' : 'waiting');
    }
  }
  private stopPlayback(): void {
    if (this.playbackTimer) clearInterval(this.playbackTimer);
    this.playbackTimer = null;
    for (const [node, scheduled] of this.playback) {
      node.onended = null; try { node.stop(); } catch {} node.disconnect(); scheduled.channel.fill(0);
    }
    this.playback.clear(); this.nextAudioTime = 0;
    this.pcm?.fill(0); this.pcm = null; this.pcmRead = 0; this.queuedSamples = 0; this.completedSamples = 0;
  }
  setMuted(value: boolean): void { this.muted = value; this.stream?.getAudioTracks().forEach(track => { track.enabled = !value; }); }
  interrupt(): void {
    if (!this.ready || this.awaitingInterrupt || this.socket?.readyState !== WebSocket.OPEN) return;
    const active = this.activePlayback;
    if (this.playbackMode === 'acknowledged' && !active) return;
    const playedAudioMs = Math.min(active ? 30000 : 180000, Math.floor(this.playedSamples() * 1000 / audioRate));
    this.awaitingInterrupt = true;
    this.clearTimeout();
    const generation = this.generation;
    this.connectionTimeout = setTimeout(() => { if (generation === this.generation && this.awaitingInterrupt) this.fail('interruption_timeout', true); }, 5000);
    this.stopPlayback();
    if (active) { this.retirePlayback(active.id); this.activePlayback = null; }
    this.socket.send(JSON.stringify({ type: 'cancel', ...(active ? { playbackId: active.id } : {}), playedAudioMs }));
    this.transition('waiting');
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
    this.awaitingInterrupt = false; this.playbackMode = null; this.streamComplete = true; this.activePlayback = null; this.retiredPlaybackIds = [];
    this.phase = 'idle';
  }
}

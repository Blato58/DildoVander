import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { LabVoice, PcmResampler, socketUrl, type VoiceObserver } from './voice';
vi.mock('../workshop/media.mjs', () => ({ mediaController: { CAPTURE_WORKLET_SOURCE: '', captureWorkletUrl: (value: string) => value } }));

describe('PCM and ticket boundaries', () => {
  it('maintains resampling phase across callback chunks and clamps PCM16', () => {
    const whole = new PcmResampler(44100).convert(Float32Array.from({ length: 1000 }, (_, index) => Math.sin(index)));
    const chunks = new PcmResampler(44100);
    const input = Float32Array.from({ length: 1000 }, (_, index) => Math.sin(index));
    const split = [...chunks.convert(input.slice(0, 177)), ...chunks.convert(input.slice(177, 601)), ...chunks.convert(input.slice(601))];
    expect(split).toEqual([...whole]);
    expect([...new PcmResampler(24000).convert(new Float32Array([-2, 2, 0]))]).toEqual([-32768, 32767]);
  });
  it('rejects another origin, path, protocol and embedded credentials', () => {
    const ticket = (url: string) => ({ url, path: '', expiresAt: '' });
    expect(socketUrl(ticket('wss://lab.example/app/v1/persona-lab/sessions/test/live?ticket=once'), 'test', 'https://lab.example')).toContain('wss://lab.example');
    for (const url of ['wss://other.example/app/v1/persona-lab/sessions/test/live?ticket=x', 'wss://lab.example/other?ticket=x',
      'https://lab.example/app/v1/persona-lab/sessions/test/live?ticket=x', 'wss://user:pass@lab.example/app/v1/persona-lab/sessions/test/live?ticket=x'])
      expect(() => socketUrl(ticket(url), 'test', 'https://lab.example')).toThrow();
  });
});

interface FakeBuffer { channel: Float32Array; duration: number; getChannelData(): Float32Array }
interface FakePlaybackNode {
  buffer: FakeBuffer | null; onended: (() => void) | null; started: number; ended: boolean;
  stop: ReturnType<typeof vi.fn>; disconnect: ReturnType<typeof vi.fn>; start: ReturnType<typeof vi.fn>;
}

describe('bounded audio playback', () => {
  let voice: LabVoice;
  let observer: VoiceObserver;
  let context: { currentTime: number; state: string; onstatechange: (() => void) | null };
  let message: (data: unknown) => void;
  let capture: (samples: Float32Array) => void;
  let send: ReturnType<typeof vi.fn>;
  let socketClose: ReturnType<typeof vi.fn>;
  let stopTrack: ReturnType<typeof vi.fn>;
  let sources: FakePlaybackNode[];
  let scheduled: number[];
  let connectedAddress: string;
  let maximumNodes: number;
  let maximumHorizon: number;
  let meta: HTMLMetaElement;
  const event = (value: object) => message(JSON.stringify(value));
  const start = (id = 'a') => event({ type: 'playback_started', playbackId: id });
  const end = (samples: number, id = 'a', final = true) => event({ type: 'playback_end', playbackId: id, totalSamples: samples, final });
  const controls = (type: string) => send.mock.calls.filter(([value]) => typeof value === 'string').map(([value]) => JSON.parse(value)).filter(value => value.type === type);
  function advance(seconds: number): void {
    const target = context.currentTime + seconds;
    while (context.currentTime < target - 1e-8) {
      context.currentTime = Math.min(target, context.currentTime + .05);
      vi.advanceTimersByTime(50);
      for (const source of [...sources]) {
        if (!source.ended && source.started + source.buffer!.duration <= context.currentTime + 1e-8) {
          source.ended = true; source.onended?.();
        }
      }
    }
  }
  beforeEach(async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval', 'performance'] });
    observer = { phase: vi.fn(), transcript: vi.fn(), turn: vi.fn(), session: vi.fn(), error: vi.fn(), closed: vi.fn(), interrupted: vi.fn() };
    stopTrack = vi.fn(); sources = []; scheduled = []; maximumNodes = 0; maximumHorizon = 0;
    const tracks = [{ stop: stopTrack, addEventListener: vi.fn(), enabled: true }];
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: async () => ({ getTracks: () => tracks }) } });
    const node = () => ({ connect: vi.fn(), disconnect: vi.fn() });
    class FakeContext {
      sampleRate = 24000; currentTime = 0; state = 'running'; onstatechange: (() => void) | null = null;
      destination = {}; audioWorklet = { addModule: async () => {} };
      constructor() { context = this; }
      resume = async () => {}; close = async () => {};
      createMediaStreamSource = node;
      createGain = () => ({ ...node(), gain: { value: 1 } });
      createBuffer = (_channels: number, length: number, rate: number): FakeBuffer => {
        const channel = new Float32Array(length);
        return { channel, getChannelData: () => channel, duration: length / rate };
      };
      createBufferSource = () => {
        const source: FakePlaybackNode = { ...node(), buffer: null, onended: null, started: 0, ended: false,
          stop: vi.fn(() => { source.ended = true; }),
          start: vi.fn((at: number) => {
            source.started = at;
            for (const value of source.buffer!.channel) scheduled.push(value * 32768);
            maximumNodes = Math.max(maximumNodes, sources.filter(value => !value.ended).length);
            maximumHorizon = Math.max(maximumHorizon, at + source.buffer!.duration - context.currentTime);
          }) };
        sources.push(source); return source;
      };
    }
    class FakeSocket {
      static OPEN = 1; readyState = 1; binaryType = ''; bufferedAmount = 0;
      onmessage: ((event: { data: unknown }) => void) | null = null; onclose = null; onerror = null;
      send = vi.fn(); close = vi.fn();
      constructor(address: string) { connectedAddress = address; send = this.send; socketClose = this.close; message = data => this.onmessage?.({ data }); }
    }
    vi.stubGlobal('AudioContext', FakeContext); vi.stubGlobal('WebSocket', FakeSocket);
    vi.stubGlobal('AudioWorkletNode', class {
      port: { onmessage: ((event: { data: Float32Array }) => void) | null; close: ReturnType<typeof vi.fn> } = { onmessage: null, close: vi.fn() };
      constructor() { capture = data => this.port.onmessage?.({ data }); }
      connect = vi.fn(); disconnect = vi.fn();
    });
    meta = document.createElement('meta'); meta.name = 'kufr-capture-worklet'; meta.content = '/assets/capture.js'; document.head.append(meta);
    voice = new LabVoice(observer);
    await voice.connect(async () => ({ url: 'wss://lab.example/app/v1/persona-lab/sessions/test/live?ticket=one', path: '', expiresAt: '' }), 'test', 'https://lab.example');
  });
  afterEach(() => { voice.dispose(); meta.remove(); vi.unstubAllGlobals(); vi.useRealTimers(); });
  const ready = (playbackMode = 'acknowledged') => event({ type: 'ready', playbackMode });
  it('accepts thirty seconds produced faster than a healthy playback clock and drains every ordered sample', () => {
    ready(); start();
    const expected: number[] = [];
    for (let index = 0; index < 150; index++) {
      context.currentTime += .001;
      const pcm = new Int16Array(4800).fill(index + 1);
      expected.push(...pcm); message(pcm.buffer);
    }
    expect(observer.error).not.toHaveBeenCalled();
    expect(observer.phase).toHaveBeenLastCalledWith('speaking');
    end(720000);
    expect(controls('playback_done')).toEqual([]);
    advance(29);
    expect(controls('playback_done')).toEqual([]);
    advance(1.2);
    expect(scheduled).toEqual(expected);
    expect(controls('playback_done')).toEqual([{ type: 'playback_done', playbackId: 'a', playedSamples: 720000 }]);
    expect(maximumNodes).toBeLessThanOrEqual(4); expect(maximumHorizon).toBeLessThanOrEqual(1.000001);
    expect(observer.error).not.toHaveBeenCalled();
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    event({ type: 'listening' }); expect(observer.phase).toHaveBeenLastCalledWith('listening');
  });
  it('waits for both provider completion and native drain, then accepts a distinct tool segment', () => {
    ready(); start(); message(new Int16Array(4800).buffer); advance(.2);
    expect(controls('playback_done')).toEqual([]);
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    end(4800, 'a', false);
    expect(controls('playback_done')).toHaveLength(1);
    start('b'); message(new Int16Array(2400).buffer); end(2400, 'b');
    advance(.1);
    expect(controls('playback_done')[1]).toEqual({ type: 'playback_done', playbackId: 'b', playedSamples: 2400 });
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    end(4800, 'a', false); expect(observer.error).not.toHaveBeenCalled();
  });
  it('measures cancellation from the currently audible response after an earlier response drained', () => {
    ready(); start(); message(new Int16Array(24000).buffer); end(24000); advance(1);
    event({ type: 'listening' }); start('b'); message(new Int16Array(24000).buffer); end(24000, 'b');
    context.currentTime += .08; voice.interrupt();
    expect(controls('cancel')).toEqual([{ type: 'cancel', playbackId: 'b', playedAudioMs: 80 }]);
    expect(controls('playback_done')).toEqual([{ type: 'playback_done', playbackId: 'a', playedSamples: 24000 }]);
  });
  it.each([false, true])('cancels the heard cutoff and zeroes buffers when provider completion is %s', completed => {
    ready(); start(); message(new Int16Array(24000).fill(17).buffer);
    if (completed) end(24000);
    const ring = (voice as unknown as { pcm: Int16Array }).pcm;
    const retiredCallback = sources[0].onended;
    context.currentTime = .08; voice.interrupt();
    expect(controls('cancel')).toEqual([{ type: 'cancel', playbackId: 'a', playedAudioMs: 80 }]);
    expect(ring.every(value => value === 0)).toBe(true);
    expect(sources.every(value => value.buffer!.channel.every(sample => sample === 0))).toBe(true);
    expect(sources.every(value => value.stop.mock.calls.length === 1)).toBe(true);
    message(new Int16Array(4800).buffer); end(24000); expect(controls('playback_done')).toEqual([]);
    event({ type: 'interrupted' }); expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    event({ type: 'listening' }); start('b'); message(new Int16Array(4800).buffer); end(4800, 'b');
    retiredCallback?.();
    expect(observer.phase).toHaveBeenLastCalledWith('speaking'); advance(.2);
    expect(controls('playback_done')).toEqual([{ type: 'playback_done', playbackId: 'b', playedSamples: 4800 }]);
    expect(observer.error).not.toHaveBeenCalled();
  });
  it('distinguishes a frozen playback clock from fast provider output and releases all resources', () => {
    ready(); start(); message(new Int16Array(48000).buffer);
    const ring = (voice as unknown as { pcm: Int16Array }).pcm;
    vi.advanceTimersByTime(4999); expect(observer.error).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(observer.error).toHaveBeenCalledWith('audio_stalled', false);
    expect(controls('playback_error')).toEqual([{ type: 'playback_error', playbackId: 'a', code: 'audio_stalled' }]);
    expect(ring.every(value => value === 0)).toBe(true);
    expect(socketClose).toHaveBeenCalledOnce(); expect(stopTrack).toHaveBeenCalledOnce();
    expect(vi.getTimerCount()).toBe(0);
  });
  it('bounds response bytes and zeroes the ring on a genuine overflow', () => {
    ready(); start();
    message(new Int16Array(480000).buffer);
    const ring = (voice as unknown as { pcm: Int16Array }).pcm;
    expect(ring.byteLength).toBe(1440000);
    message(new Int16Array(240001).buffer);
    expect(observer.error).toHaveBeenCalledWith('audio_backpressure', true);
    expect(controls('playback_error')).toEqual([{ type: 'playback_error', playbackId: 'a', code: 'audio_backpressure' }]);
    expect(ring.every(value => value === 0)).toBe(true); expect(vi.getTimerCount()).toBe(0);
  });
  it('keeps tiny incoming chunks within the native node bound', () => {
    ready(); start();
    for (let index = 0; index < 5000; index++) message(new Int16Array([index]).buffer);
    end(5000); expect(sources.length).toBeLessThanOrEqual(4);
    advance(.5); expect(scheduled).toEqual(Array.from({ length: 5000 }, (_, index) => index));
    expect(controls('playback_done')[0].playedSamples).toBe(5000);
  });
  it('forwards the first PCM after the final drain acknowledgment before SDK listening resumes', () => {
    ready(); start(); message(new Int16Array(4800).buffer); end(4800); advance(.2);
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    const acknowledgment = send.mock.calls.findIndex(([value]) => typeof value === 'string' && JSON.parse(value).type === 'playback_done');
    capture(new Float32Array(960));
    const microphone = send.mock.calls.findIndex(([value]) => value instanceof ArrayBuffer);
    expect(microphone).toBeGreaterThan(acknowledgment);
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    event({ type: 'listening' }); capture(new Float32Array(960));
    expect(observer.phase).toHaveBeenLastCalledWith('listening');
  });
  it('shows waiting after speech stops without claiming SDK listening', () => {
    ready(); event({ type: 'speech', state: 'speech_stopped' });
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    start(); message(new Int16Array(4800).buffer);
    expect(observer.phase).toHaveBeenLastCalledWith('speaking');
  });
  it('negotiates after validated addressing and fails closed without a known mode', () => {
    expect(new URL(connectedAddress).searchParams.get('playback')).toBe('1');
    event({ type: 'ready' }); expect(observer.error).toHaveBeenCalledWith('invalid_server_event', false);
  });
  it.each(['unknown', null])('rejects unknown ready mode %s', playbackMode => {
    event({ type: 'ready', playbackMode }); expect(observer.error).toHaveBeenCalledWith('invalid_server_event', false);
  });
  it.each(['unowned', 'overlap', 'wrong_count', 'post_end'])('fails closed for invalid output ownership: %s', kind => {
    ready();
    if (kind === 'unowned') message(new Int16Array(100).buffer);
    else {
      start();
      if (kind === 'overlap') start('b');
      if (kind === 'wrong_count') end(1);
      if (kind === 'post_end') { message(new Int16Array(100).buffer); end(100); message(new Int16Array(100).buffer); }
    }
    expect(observer.error).toHaveBeenCalledWith('invalid_server_event', false);
  });
  it('preserves bounded Live stream playback without sending a heard-history acknowledgment', () => {
    ready('stream');
    for (let index = 0; index < 150; index++) message(new Int16Array(4800).buffer);
    event({ type: 'turn', turn: { role: 'assistant' } });
    advance(30.1);
    expect(observer.error).not.toHaveBeenCalled(); expect(scheduled).toHaveLength(720000);
    expect(controls('playback_done')).toEqual([]); expect(observer.phase).toHaveBeenLastCalledWith('listening');
    message(new Int16Array(4800).buffer); context.currentTime += .08; voice.interrupt();
    expect(controls('cancel')[0]).toEqual({ type: 'cancel', playedAudioMs: 80 });
    event({ type: 'interrupted' }); expect(observer.phase).toHaveBeenLastCalledWith('listening');
  });
  it('retains the existing Live technical capacity with a fixed ring and fails cleanly above it', () => {
    ready('stream');
    for (let index = 0; index < 9; index++) message(new Int16Array(480000).buffer);
    const ring = (voice as unknown as { pcm: Int16Array }).pcm;
    expect(ring.byteLength).toBe(9120000); expect(maximumNodes).toBeLessThanOrEqual(4);
    message(new Int16Array(240001).buffer);
    expect(observer.error).toHaveBeenCalledWith('audio_backpressure', true);
    expect(ring.every(value => value === 0)).toBe(true);
    expect(controls('playback_error')).toEqual([]); expect(vi.getTimerCount()).toBe(0);
  });
  it.each(['end', 'dispose'] as const)('clears queued PCM, callbacks and timers on %s', action => {
    ready(); start(); message(new Int16Array(48000).fill(12).buffer);
    const ring = (voice as unknown as { pcm: Int16Array }).pcm;
    const callback = sources[0].onended;
    voice[action](); callback?.(); message(new Int16Array(4800).buffer);
    expect(ring.every(value => value === 0)).toBe(true); expect(vi.getTimerCount()).toBe(0);
    expect(controls('playback_done')).toEqual([]); expect(stopTrack).toHaveBeenCalledOnce();
  });
  it('fences retired callbacks and server events after reconnecting a new generation', async () => {
    ready(); start(); message(new Int16Array(48000).fill(12).buffer);
    const ring = (voice as unknown as { pcm: Int16Array }).pcm;
    const callback = sources[0].onended;
    const retiredSources = [...sources];
    const oldMessage = message;
    await voice.connect(async () => ({ url: 'wss://lab.example/app/v1/persona-lab/sessions/test/live?ticket=two', path: '', expiresAt: '' }), 'test', 'https://lab.example');
    ready(); start('new'); message(new Int16Array(4800).buffer); end(4800, 'new');
    callback?.(); oldMessage(JSON.stringify({ type: 'playback_started', playbackId: 'old' }));
    expect(observer.error).not.toHaveBeenCalled(); expect(ring.every(value => value === 0)).toBe(true);
    expect(retiredSources.every(source => source.onended === null)).toBe(true);
    advance(.2); expect(controls('playback_done')).toEqual([{ type: 'playback_done', playbackId: 'new', playedSamples: 4800 }]);
  });
  it('reports a genuinely interrupted context and clears playback', () => {
    ready(); start(); message(new Int16Array(48000).buffer);
    context.state = 'suspended'; context.onstatechange?.();
    expect(observer.error).toHaveBeenCalledWith('audio_interrupted', false);
    expect(controls('playback_error')).toEqual([{ type: 'playback_error', playbackId: 'a', code: 'audio_interrupted' }]);
    expect(vi.getTimerCount()).toBe(0);
  });
  it('waits across Live audio gaps until the assistant turn has completed', () => {
    ready('stream'); message(new Int16Array(4800).buffer); advance(.2);
    expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    message(new Int16Array(4800).buffer); expect(observer.phase).toHaveBeenLastCalledWith('speaking');
    advance(.2); expect(observer.phase).toHaveBeenLastCalledWith('waiting');
    event({ type: 'turn', turn: { role: 'assistant' } });
    expect(observer.phase).toHaveBeenLastCalledWith('listening');
  });
});
describe('voice lifecycle', () => {
  let observer: VoiceObserver;
  const stop = vi.fn(); const close = vi.fn();
  beforeEach(() => {
    vi.restoreAllMocks(); stop.mockReset(); close.mockReset();
    observer = { phase: vi.fn(), transcript: vi.fn(), turn: vi.fn(), session: vi.fn(), error: vi.fn(), closed: vi.fn() };
  });
  it('stops late microphone permission results after disposal without minting a ticket', async () => {
    let complete!: (stream: MediaStream) => void;
    const getUserMedia = vi.fn(() => new Promise<MediaStream>(resolve => { complete = resolve; }));
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia } });
    vi.stubGlobal('AudioContext', class { resume = async () => {}; close = close.mockResolvedValue(undefined); });
    const voice = new LabVoice(observer); const ticket = vi.fn();
    const connecting = voice.connect(ticket, 'test', 'https://lab.example');
    await Promise.resolve(); voice.dispose();
    complete({ getTracks: () => [{ stop }] } as unknown as MediaStream); await connecting;
    expect(stop).toHaveBeenCalledOnce(); expect(ticket).not.toHaveBeenCalled(); expect(close).toHaveBeenCalledOnce();
    vi.unstubAllGlobals();
  });
  it('reports denied permission without claiming listening or starting the provider', async () => {
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: vi.fn().mockRejectedValue(new DOMException('Denied', 'NotAllowedError')) } });
    vi.stubGlobal('AudioContext', class { resume = async () => {}; close = close.mockResolvedValue(undefined); });
    const voice = new LabVoice(observer); const ticket = vi.fn(); await voice.connect(ticket, 'test', 'https://lab.example');
    expect(observer.error).toHaveBeenCalledWith('microphone_permission', false);
    expect(observer.phase).not.toHaveBeenCalledWith('listening'); expect(ticket).not.toHaveBeenCalled();
    vi.unstubAllGlobals();
  });
  it('interrupts local playback, fences late audio until acknowledgment and releases all media', async () => {
    const tracks = [{ stop, addEventListener: vi.fn(), enabled: true }];
    Object.defineProperty(navigator, 'mediaDevices', { configurable: true, value: { getUserMedia: async () => ({ getTracks: () => tracks }) } });
    const node = () => ({ connect: vi.fn(), disconnect: vi.fn() });
    const sources: { stop: ReturnType<typeof vi.fn>; start: ReturnType<typeof vi.fn> }[] = [];
    let context!: FakeContext; let socket!: FakeSocket;
    class FakeContext {
      sampleRate = 24000; currentTime = 0; state = 'running'; onstatechange: (() => void) | null = null;
      destination = {}; audioWorklet = { addModule: async () => {} };
      constructor() { context = this; }
      resume = async () => {}; close = close.mockResolvedValue(undefined);
      createMediaStreamSource = node;
      createGain = () => ({ ...node(), gain: { value: 1 } });
      createBuffer = (_channels: number, length: number, rate: number) => ({ getChannelData: () => new Float32Array(length), duration: length / rate });
      createBufferSource = () => { const source = { ...node(), buffer: null, onended: null, stop: vi.fn(), start: vi.fn() }; sources.push(source); return source; };
    }
    class FakeSocket {
      static OPEN = 1; readyState = 1; binaryType = ''; bufferedAmount = 0;
      onmessage: ((event: { data: unknown }) => void) | null = null; onclose = null; onerror = null;
      send = vi.fn(); close = vi.fn(); constructor() { socket = this; }
    }
    vi.stubGlobal('AudioContext', FakeContext); vi.stubGlobal('WebSocket', FakeSocket);
    vi.stubGlobal('AudioWorkletNode', class { port = { onmessage: null, close: vi.fn() }; connect = vi.fn(); disconnect = vi.fn(); });
    const meta = document.createElement('meta'); meta.name = 'kufr-capture-worklet'; meta.content = '/assets/capture.js'; document.head.append(meta);
    try {
      const voice = new LabVoice(observer);
      await voice.connect(async () => ({ url: 'wss://lab.example/app/v1/persona-lab/sessions/test/live?ticket=one', path: '', expiresAt: '' }), 'test', 'https://lab.example');
      socket.onmessage?.({ data: '{"type":"ready","playbackMode":"stream"}' });
      socket.onmessage?.({ data: new Int16Array(4800).buffer });
      context.currentTime = .08; voice.interrupt();
      expect(sources[0].stop).toHaveBeenCalledOnce();
      expect(socket.send).toHaveBeenCalledWith('{"type":"cancel","playedAudioMs":80}');
      socket.onmessage?.({ data: new Int16Array(4800).buffer }); expect(sources).toHaveLength(1);
      socket.onmessage?.({ data: '{"type":"interrupted"}' });
      socket.onmessage?.({ data: new Int16Array(4800).buffer }); expect(sources).toHaveLength(2);
      const retiredMessage = socket.onmessage;
      context.state = 'suspended'; context.onstatechange?.();
      expect(observer.error).toHaveBeenCalledWith('audio_interrupted', false);
      voice.end(); retiredMessage?.({ data: '{"type":"ready","playbackMode":"stream"}' });
      expect(observer.phase).toHaveBeenLastCalledWith('ended');
      expect(stop).toHaveBeenCalledOnce(); expect(socket.close).toHaveBeenCalledOnce(); expect(close).toHaveBeenCalledOnce();
    } finally { meta.remove(); vi.unstubAllGlobals(); }
  });
});

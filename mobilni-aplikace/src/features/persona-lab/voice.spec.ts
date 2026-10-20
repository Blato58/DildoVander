import { beforeEach, describe, expect, it, vi } from 'vitest';
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
      socket.onmessage?.({ data: '{"type":"ready"}' });
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
      voice.end(); retiredMessage?.({ data: '{"type":"ready"}' });
      expect(observer.phase).toHaveBeenLastCalledWith('ended');
      expect(stop).toHaveBeenCalledOnce(); expect(socket.close).toHaveBeenCalledOnce(); expect(close).toHaveBeenCalledOnce();
    } finally { meta.remove(); vi.unstubAllGlobals(); }
  });
});

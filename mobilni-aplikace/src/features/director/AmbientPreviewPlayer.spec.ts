import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import { AmbientPreviewPlayer } from './AmbientPreviewPlayer';

let resume: () => Promise<void>;
let audio: any, source: any;
const players: AmbientPreviewPlayer[] = [];
beforeEach(() => {
  resume = async () => {};
  source = { connect: vi.fn(), disconnect: vi.fn(), start: vi.fn(), stop: vi.fn(), onended: null };
  vi.stubGlobal('AudioContext', class {
    state = 'running'; destination = {}; onstatechange = null;
    resume = vi.fn(() => resume()); close = vi.fn(async () => {});
    decodeAudioData = vi.fn(async () => ({ duration: .1, numberOfChannels: 1 }));
    createBufferSource = vi.fn(() => source);
    constructor() { audio = this; }
  });
});
afterEach(() => {
  players.splice(0).forEach(player => player.stop());
  vi.unstubAllGlobals(); vi.useRealTimers();
});
const create = (transport: any) => {
  const player = new AmbientPreviewPlayer('case.s3', transport, () => {}); players.push(player); return player;
};
const ready = (operationId: string) => ({ operationId, status: 'ready', text: 'Ambient.', synthetic: false, wavBase64: btoa('wav-fixture') });
const success = () => vi.fn(async (_path: string, init: RequestInit) => Response.json(ready(JSON.parse(String(init.body)).operationId)));

describe('phone ambient preview playback', () => {
  it('waits for audio activation before generating or playing and sends no device command/ACK', async () => {
    let activate!: () => void;
    resume = () => new Promise(resolve => { activate = resolve; });
    const transport = success(), player = create(transport);
    const work = player.play();
    expect(transport).not.toHaveBeenCalled(); expect(source.start).not.toHaveBeenCalled();
    await player.play(); // A second tap cannot start a second paid request.
    activate(); await work;
    expect(transport).toHaveBeenCalledTimes(1);
    expect(transport.mock.calls[0][0]).toBe('/v1/show/devices/case.s3/ambient-previews');
    expect(transport.mock.calls[0][1].headers).toEqual(expect.objectContaining({ 'X-DV-Device-Id': 'case.s3' }));
    expect(source.start).toHaveBeenCalledTimes(1); expect(player.state.busy).toBe(true);
    source.onended(); expect(player.state.busy).toBe(false); expect(player.state.message).toContain('dokončen');
  });
  it('does not start provider work if Stop wins delayed activation', async () => {
    let activate!: () => void;
    resume = () => new Promise(resolve => { activate = resolve; });
    const transport = success(), player = create(transport);
    const work = player.play(); player.stop(); activate(); await work;
    expect(transport).not.toHaveBeenCalled(); expect(source.start).not.toHaveBeenCalled(); expect(audio.close).toHaveBeenCalled();
  });
  it('rejects late responses after context cancellation', async () => {
    let finish!: (response: Response) => void, operationId = '';
    const transport = vi.fn((_path: string, init: RequestInit) => {
      operationId = JSON.parse(String(init.body)).operationId;
      return new Promise<Response>(resolve => { finish = resolve; });
    });
    const player = create(transport), work = player.play(); await flushPromises();
    const signal = transport.mock.calls[0][1].signal!;
    player.stop(); finish(Response.json(ready(operationId))); await work;
    expect(signal.aborted).toBe(true); expect(source.start).not.toHaveBeenCalled();
  });
  it('polls only GET after admission and stops polling when cancelled', async () => {
    vi.useFakeTimers();
    const transport = vi.fn(async (_path: string, init: RequestInit) => Response.json({ operationId: JSON.parse(String(init.body)).operationId, status: 'pending' }, { status: 202 }));
    const player = create(transport), work = player.play(); await flushPromises();
    player.stop(); await work; await vi.advanceTimersByTimeAsync(20000);
    expect(transport).toHaveBeenCalledTimes(1); expect(source.start).not.toHaveBeenCalled();
  });
  it('does not regenerate after failed response or playback interruption', async () => {
    const failed = vi.fn(async () => Response.json({ code: 'preview_unavailable' }, { status: 503 }));
    const player = create(failed); await player.play();
    expect(failed).toHaveBeenCalledTimes(1); expect(player.state.error).not.toBe(''); expect(player.state.busy).toBe(false);
    const transport = success(), playing = create(transport); await playing.play();
    const change = audio.onstatechange; audio.state = 'suspended'; change();
    expect(source.stop).toHaveBeenCalled(); expect(playing.state.error).toContain('přerušen'); expect(transport).toHaveBeenCalledTimes(1);
  });
  it('rejects mismatched operation and overlong decoded sound', async () => {
    const mismatch = create(vi.fn(async () => Response.json(ready('wrong-id')))); await mismatch.play();
    expect(mismatch.state.error).toContain('Neplatná odpověď'); expect(source.start).not.toHaveBeenCalled();
    const player = create(success());
    const work = player.play(); audio.decodeAudioData.mockResolvedValue({ duration: 8.1, numberOfChannels: 1 }); await work;
    expect(player.state.error).toContain('bezpečně'); expect(source.start).not.toHaveBeenCalled();
  });
});

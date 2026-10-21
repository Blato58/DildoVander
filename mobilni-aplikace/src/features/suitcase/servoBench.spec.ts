import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
const usb = vi.hoisted(() => ({ connect: vi.fn(), write: vi.fn(), close: vi.fn(),
  callbacks: null as null | { onLine(line: string): void; onDisconnect(reason: string): void } }));
vi.mock('../../../../firmware/diagnostics/servo-bench/web/serial.mjs', () => ({
  SerialTransport: class {
    writer: unknown = null;
    constructor(_serial: unknown, callbacks: typeof usb.callbacks) { usb.callbacks = callbacks; }
    async connect(options: unknown) { await usb.connect(options); this.writer = {}; }
    write(text: string, stop = false) { return usb.write(text, stop); }
    async close() { this.writer = null; await usb.close(); }
  },
  flushStopBeforeClose: async (stop: () => unknown) => { await stop(); },
}));
let bench: (typeof import('./servoBench'))['servoBench'];
beforeEach(async () => {
  vi.resetModules(); vi.clearAllMocks(); vi.useFakeTimers();
  const storage = new Map<string, string>();
  vi.stubGlobal('localStorage', { getItem: (key: string) => storage.get(key) ?? null,
    setItem: (key: string, value: string) => storage.set(key, value) });
  vi.stubGlobal('isSecureContext', true); Object.defineProperty(window, 'isSecureContext', { value: true, configurable: true });
  Object.defineProperty(navigator, 'serial', { value: {}, configurable: true });
  usb.connect.mockResolvedValue(undefined); usb.write.mockResolvedValue(undefined); usb.close.mockResolvedValue(undefined);
  bench = (await import('./servoBench')).servoBench;
});
afterEach(async () => { bench?.show(false); await bench?.disconnect(); vi.useRealTimers(); vi.unstubAllGlobals(); });
async function connectBaseline(): Promise<void> {
  bench.show(true); await bench.connect();
  usb.callbacks!.onLine('READY PWM FULL_OFF calibration=v1');
  usb.callbacks!.onLine('STOP PWM FULL_OFF');
}
describe('integrated USB service protocol boundary', () => {
  it('mounting creates no connection, Arm, or movement; explicit connection sends only baseline Stop', async () => {
    bench.show(true); expect(usb.connect).not.toHaveBeenCalled(); expect(usb.write).not.toHaveBeenCalled();
    await bench.connect(); expect(usb.connect).toHaveBeenCalledWith({ authorizedOnly: false });
    expect(usb.write).toHaveBeenCalledExactlyOnceWith('!\n', true); expect(bench.capabilities.value.arm).toBe(false);
    usb.callbacks!.onLine('STOP PWM FULL_OFF'); expect(bench.capabilities.value.arm).toBe(true);
  });
  it('retains the actual Arm/ACK/calibration completion gates before saving a local point', async () => {
    await connectBaseline(); expect(await bench.calibrate(0, 1500, 1500)).toBe(false);
    await bench.arm(); usb.callbacks!.onLine('ARMED for one pulse');
    await bench.calibrate(0, 1500, 1500); expect(usb.write).toHaveBeenLastCalledWith('cal 0 1500 1500\n', false);
    expect(bench.canSavePoint(0, 1500)).toBe(false);
    usb.callbacks!.onLine('DONE PWM FULL_OFF'); expect(bench.canSavePoint(0, 1500)).toBe(true);
    bench.savePoint(0, 'center', 1500); expect(bench.profiles.value[0].center).toBe(1500);
    expect(JSON.parse(bench.exportProfiles())).toMatchObject({ positionMeasured: false, productionCalibration: false });
    await bench.stop(); expect(bench.canSavePoint(0, 1500)).toBe(false);
  });
  it('leaving the service screen attempts Stop before closing and never reconnects automatically', async () => {
    await connectBaseline(); usb.write.mockClear();
    bench.show(false); await bench.disconnect();
    expect(usb.write).toHaveBeenCalledWith('!\n', true); expect(usb.close).toHaveBeenCalled();
    expect(bench.connected.value).toBe(false); bench.show(true); expect(usb.connect).toHaveBeenCalledOnce();
  });
  it('a connection picker resolving after route departure cannot send any firmware command', async () => {
    let resolve!: () => void; usb.connect.mockReturnValueOnce(new Promise<void>(finish => { resolve = finish; }));
    bench.show(true); const connecting = bench.connect(); bench.show(false); resolve(); await connecting;
    expect(usb.write).not.toHaveBeenCalled(); expect(bench.connected.value).toBe(false);
  });
  it('music owns motion until main Stop cancels playback and invalidates late acknowledgements', async () => {
    await connectBaseline();
    usb.callbacks!.onLine('READY PWM FULL_OFF calibration=v2 calibration_min_us=600 calibration_max_us=2300');
    await bench.stop(); usb.callbacks!.onLine('STOP PWM FULL_OFF');
    const audio = Object.assign(new EventTarget(), { paused: true, currentTime: 0, readyState: 4, playbackRate: 1,
      play: vi.fn(async function (this: { paused: boolean }) { this.paused = false; }),
      pause: vi.fn(function (this: { paused: boolean }) { this.paused = true; }) });
    const music = bench.createMusic(audio as unknown as HTMLAudioElement, () => {});
    music.analysis = { duration: 8, bpm: 120, beatSeconds: 0.5, offset: 0, energy: Array(400).fill(1) };
    usb.write.mockClear();
    expect(await music.play({ dance: true, consent: true, channel: 2 })).toBe(true);
    expect(bench.dancing.value).toBe(true);
    expect(await bench.arm()).toBe(false); expect(await bench.pulse(1, 1500)).toBe(false);
    expect(bench.canStartAutomatic.value).toBe(false);
    usb.callbacks!.onLine('ARMED for one pulse'); await vi.advanceTimersByTimeAsync(25);
    expect(usb.write).toHaveBeenCalledWith('cal 2 1790 0500\n', false);
    audio.currentTime = 1.2; await bench.stop();
    expect(audio.paused).toBe(true); expect(audio.currentTime).toBe(0); expect(bench.dancing.value).toBe(false);
    const commands = usb.write.mock.calls.length;
    usb.callbacks!.onLine('ARMED for one pulse'); usb.callbacks!.onLine('DONE PWM FULL_OFF');
    await vi.advanceTimersByTimeAsync(1000); expect(usb.write.mock.calls.length).toBe(commands);
    bench.releaseMusic();
  });
});

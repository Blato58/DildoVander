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
});

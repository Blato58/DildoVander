import { computed, ref, shallowRef } from 'vue';
import { BenchController } from '../../../../firmware/diagnostics/servo-bench/web/controller.mjs';
import { SerialTransport, flushStopBeforeClose } from '../../../../firmware/diagnostics/servo-bench/web/serial.mjs';
import { ManualProfiles } from '../../../../firmware/diagnostics/servo-bench/web/profiles.mjs';
import { AutoCalibration } from '../../../../firmware/diagnostics/servo-bench/web/auto-calibration.mjs';
import { registerBenchTools } from '../../../../firmware/diagnostics/servo-bench/web/mcp.mjs';
import { MusicDance } from '../../../../firmware/diagnostics/servo-bench/web/music.mjs';

export interface BenchState {
  connected: boolean; baseline: boolean; fault: boolean; pending: string | null; writing: boolean;
  armedUntil: number; calibrationSupported: boolean; message: string; error: string; errorKind: string;
  calibrationMinimumUs?: number | null; calibrationMaximumUs?: number | null;
  lastCompleted: { channel: number; pulseWidthUs: number; durationMs: number; calibration: boolean } | null;
  readback: { channel: number; mode1: string; mode2: string; prescale: number; on: number; off: number;
    fullOn: boolean; fullOff: boolean; expectedOff: number; match: boolean } | null;
}
interface Capabilities { arm: boolean; pulse: boolean; calibrate: boolean; stop: boolean }
interface Profile { label: string; min: number | null; center: number | null; max: number | null }
interface AutoState {
  active: boolean; stage: string; channel: number | null; direction: 'min' | 'max' | null;
  centerUs: number | null; currentUs: number | null; previousUs: number | null; candidateUs: number | null;
  candidateEvidence: unknown; stopConfirmed: boolean; reason: string; message: string;
}
interface Controller {
  state: BenchState; capabilities: Capabilities;
  connected(): Promise<boolean>; disconnected(message?: string): void; receive(line: string): void;
  fail(message: string): void; arm(): Promise<boolean> | false; pulse(channel: number, width: number): Promise<boolean> | false;
  calibrate(channel: number, width: number, duration: number): Promise<boolean> | false; stop(): Promise<boolean> | false;
}
interface Transport { writer: unknown; connect(options?: { authorizedOnly: boolean }): Promise<void>; write(text: string, stop?: boolean): Promise<void>; close(): Promise<void> }
interface Profiles {
  channels: Profile[]; error: string; savePoint(channel: number, point: string, width: number, state: BenchState): void;
  saveLabel(channel: number, label: string): void; clear(channel: number): void; exportJson(): string;
  canSaveCandidate(channel: number, proposal: object, state: BenchState): boolean;
  saveCandidate(channel: number, proposal: object, state: BenchState): void;
}
interface Automatic {
  state: AutoState; start(options: { channel: number; direction: 'min' | 'max'; centerUs: number }): boolean;
  cancel(reason: string, options?: { sendStop: boolean }): boolean; markNoise(): boolean; discardCandidate(): void;
}
interface Snapshot extends BenchState { capabilities: Capabilities; connecting: boolean; closing: boolean; powerState: 'unknown'; positionMeasured: false }
interface ToolRegistration { supported: boolean; registered: boolean; names: string[]; error?: string; cleanup(): void }
const connected = ref(false);
const connecting = ref(false);
const closing = ref(false);
const state = shallowRef<BenchState>({ connected: false, baseline: false, fault: false, pending: null, writing: false,
  armedUntil: 0, calibrationSupported: false, lastCompleted: null, message: 'USB není připojeno.', error: '', errorKind: '', readback: null });
const capabilities = shallowRef<Capabilities>({ arm: false, pulse: false, calibrate: false, stop: false });
const automatic = shallowRef<AutoState | null>(null);
const profiles = shallowRef<Profile[]>(Array.from({ length: 6 }, () => ({ label: '', min: null, center: null, max: null })));
const log = shallowRef<string[]>([]);
const profileError = ref('');
const toolMessage = ref('WebMCP není zapnuté.');
const toolsEnabled = ref(false);
const toolsPending = ref(false);
const dancing = ref(false);
let music: MusicDance | null = null;
const supported = typeof window !== 'undefined' && window.isSecureContext && 'serial' in navigator;
const rawListeners = new Set<(value: Snapshot) => void>();
const listeners = new Set<(value: Snapshot) => void>();
let controller: Controller | null = null;
let transport: Transport | null = null;
let calibration: Automatic | null = null;
let storedProfiles: Profiles | null = null;
let tools: ToolRegistration | null = null;
let profileSignature: string | null = null;
let visible = false;
let epoch = 0;
let disconnecting: Promise<void> | null = null;
let toolGeneration = 0;

function disableTools(): void {
  ++toolGeneration; tools?.cleanup(); tools = null;
  toolsEnabled.value = false; toolsPending.value = false; toolMessage.value = 'WebMCP vypnuté.';
}

function snapshot(): Snapshot {
  return { ...structuredClone(controller?.state ?? state.value), capabilities: { ...(controller?.capabilities ?? capabilities.value) },
    connecting: connecting.value, closing: closing.value, powerState: 'unknown', positionMeasured: false };
}
function publish(): void {
  if (!controller) return;
  const next = snapshot();
  state.value = next; connected.value = next.connected; capabilities.value = { ...next.capabilities };
  if (calibration?.state.candidateUs != null && (!next.connected || next.fault || next.error ||
      !next.calibrationSupported || next.message.startsWith('Firmware připraven po startu'))) discardProposal();
  for (const listener of [...rawListeners]) listener(next);
  automatic.value = calibration ? structuredClone(calibration.state) : null;
  if (automatic.value?.active || dancing.value) capabilities.value = { ...next.capabilities, arm: false, pulse: false, calibrate: false };
  for (const listener of [...listeners]) listener({ ...next, capabilities: capabilities.value });
}
function addLog(line: string): void { log.value = [...log.value.slice(-199), `${new Date().toISOString()} ${line}`]; }
function proposal(): object { return { ...(calibration?.state ?? {}), profileSignature }; }
function discardProposal(): void { profileSignature = null; calibration?.discardCandidate(); }
function ensure(): void {
  if (controller) return;
  // These are the existing tested UART protocol classes; no port opens here.
  transport = new SerialTransport((navigator as Navigator & { serial?: unknown }).serial, {
    onLine: (line: string) => controller?.receive(line),
    onDisconnect: (reason: string) => { calibration?.cancel('disconnected', { sendStop: false }); discardProposal(); controller?.disconnected(reason); },
    onOverflow: () => controller?.fail('Příliš dlouhý řádek ze zařízení. Vyžaduje nové potvrzení Stop.'),
  }) as Transport;
  const Constructor = BenchController as unknown as new (options: {
    write(text: string, stop?: boolean): Promise<void>; onChange(): void; onLog(line: string): void;
  }) => Controller;
  controller = new Constructor({ write: (text, stop) => transport!.write(text, stop), onChange: publish, onLog: addLog });
  const AutomaticConstructor = AutoCalibration as unknown as new (options: {
    getState(): Snapshot; subscribe(listener: (value: Snapshot) => void): () => void;
    arm(): Promise<boolean> | false; calibrate(channel: number, width: number, duration: number): Promise<boolean> | false;
    stop(): Promise<boolean> | false; onChange(): void;
  }) => Automatic;
  calibration = new AutomaticConstructor({ getState: snapshot,
    subscribe: listener => { rawListeners.add(listener); return () => rawListeners.delete(listener); },
    arm: () => controller!.arm(), calibrate: (channel, width, duration) => controller!.calibrate(channel, width, duration),
    stop: () => controller!.stop(), onChange: publish });
  let storage: Storage | null = null;
  try { storage = window.localStorage; } catch { /* The profile adapter reports unavailable storage when saving. */ }
  storedProfiles = new ManualProfiles(storage) as Profiles;
  profiles.value = structuredClone(storedProfiles.channels); profileError.value = storedProfiles.error;
  publish();
}
async function connect(authorizedOnly = false): Promise<boolean> {
  if (!supported || connecting.value || closing.value || connected.value) return false;
  ensure(); discardProposal(); connecting.value = true;
  const requested = ++epoch;
  try {
    await transport!.connect({ authorizedOnly });
    if (requested !== epoch || !visible || document.hidden || !transport!.writer) { await transport!.close(); return false; }
    addLog('USB otevřeno. Odesílám pouze Stop pro potvrzení výchozího stavu.');
    await controller!.connected(); return true;
  } catch (error) {
    await transport!.close(); controller!.disconnected();
    controller!.fail(error instanceof Error ? error.message : 'Připojení USB selhalo.'); return false;
  } finally { connecting.value = false; publish(); }
}
function stop(): Promise<boolean> | false {
  music?.stop(false);
  if (!controller?.state.connected) return false;
  calibration?.cancel('manual_stop', { sendStop: false }); discardProposal();
  return controller.stop();
}
function disconnect(): Promise<void> {
  ++epoch;
  if (disconnecting) return disconnecting;
  if (!controller) return Promise.resolve();
  music?.pause('USB odpojeno; hudba i tanec pozastaveny.', false);
  disableTools();
  closing.value = true;
  calibration?.cancel('disconnected', { sendStop: false }); discardProposal();
  disconnecting = (async () => {
    try {
      if (controller!.state.connected) await flushStopBeforeClose(stop);
      controller!.disconnected('USB odpojeno. Stav PWM ani napájení nejsou potvrzené.');
      await transport?.close();
    } finally { closing.value = false; disconnecting = null; publish(); }
  })();
  return disconnecting;
}
function pagehide(): void {
  ++epoch;
  music?.pause('Stránka skryta; hudba i tanec pozastaveny.');
  if (state.value.connected && !state.value.fault && state.value.pending !== 'stop') void stop();
}
function visibility(): void { if (document.hidden) pagehide(); }
function manual(action: (value: Controller) => Promise<boolean> | false): Promise<boolean> | false {
  if (!controller || calibration?.state.active || dancing.value || !visible || document.hidden || closing.value) return false;
  discardProposal(); return action(controller);
}
function updateProfile(action: (value: Profiles) => void): void {
  if (dancing.value) return;
  ensure();
  try { action(storedProfiles!); profiles.value = structuredClone(storedProfiles!.channels); profileError.value = ''; discardProposal(); }
  catch (error) { profileError.value = error instanceof Error ? error.message : 'Profil se nepodařilo uložit.'; }
}

export const servoBench = {
  state, capabilities, automatic, profiles, log, profileError, connected, connecting, closing, supported, toolsEnabled, toolsPending, toolMessage, dancing,
  createMusic(audio: HTMLAudioElement, onChange: () => void): MusicDance {
    ensure(); music?.dispose();
    music = new MusicDance({ audio,
      getState: () => ({ ...snapshot(), automatic: !!calibration?.state.active || !visible || document.hidden }),
      arm: () => controller!.arm(), calibrate: (channel: number, width: number, duration: number) => controller!.calibrate(channel, width, duration),
      stop: () => controller!.stop(), onChange: () => {
        const started = !!music?.state.dancing && !dancing.value;
        dancing.value = !!music?.state.dancing;
        if (started) discardProposal();
        publish(); onChange();
      },
    });
    return music;
  },
  releaseMusic(): void { music?.dispose(); music = null; dancing.value = false; publish(); },
  canStartAutomatic: computed(() => capabilities.value.arm && !automatic.value?.active && state.value.calibrationSupported && !state.value.error && !connecting.value && !closing.value),
  connect, disconnect, stop,
  show(value: boolean): void {
    visible = value;
    if (value) { ensure(); document.addEventListener('visibilitychange', visibility); window.addEventListener('pagehide', pagehide); }
    else { music?.stop(false); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', pagehide); void disconnect(); }
  },
  arm: () => manual(value => value.arm()), pulse: (channel: number, width: number) => manual(value => value.pulse(channel, width)),
  calibrate: (channel: number, width: number, duration: number) => manual(value => value.calibrate(channel, width, duration)),
  noise(): void { if (calibration?.state.active) calibration.markNoise(); else void stop(); },
  startAutomatic(channel: number, direction: 'min' | 'max'): boolean {
    const profile = profiles.value[channel];
    if (!visible || document.hidden || !this.canStartAutomatic.value || !profile || profile.center === null || !calibration) return false;
    discardProposal(); profileSignature = JSON.stringify(profile);
    return calibration.start({ channel, direction, centerUs: profile.center });
  },
  changeContext: discardProposal,
  canSavePoint: (channel: number, width: number) => !dancing.value && !!state.value.lastCompleted?.calibration && state.value.calibrationSupported &&
    state.value.connected && state.value.baseline && !state.value.fault && !state.value.pending && !state.value.writing &&
    state.value.lastCompleted.channel === channel && state.value.lastCompleted.pulseWidthUs === width,
  savePoint: (channel: number, point: 'min' | 'center' | 'max', width: number) => updateProfile(value => value.savePoint(channel, point, width, state.value)),
  saveLabel: (channel: number, label: string) => updateProfile(value => value.saveLabel(channel, label)),
  clearProfile: (channel: number) => updateProfile(value => value.clear(channel)),
  canSaveCandidate: (channel: number) => storedProfiles?.canSaveCandidate(channel, proposal(), state.value) ?? false,
  saveCandidate: (channel: number) => updateProfile(value => value.saveCandidate(channel, proposal(), state.value)),
  exportProfiles(): string { ensure(); return storedProfiles!.exportJson(); },
  async enableTools(): Promise<void> {
    if (toolsEnabled.value || toolsPending.value || !visible || !supported) return;
    ensure();
    const requested = ++toolGeneration;
    toolsPending.value = true;
    const register = registerBenchTools as unknown as (options: Record<string, unknown>) => Promise<ToolRegistration>;
    try {
      const registration = await register({ modelContext: (document as Document & { modelContext?: unknown }).modelContext,
        getState: () => ({ ...snapshot(), capabilities: capabilities.value }), getLog: () => [...log.value],
        subscribe: (listener: (value: Snapshot) => void) => { listeners.add(listener); return () => listeners.delete(listener); },
        connect: () => connect(true), disconnect, arm: this.arm, pulse: this.pulse, calibrate: this.calibrate, stop });
      if (requested !== toolGeneration || !visible || document.hidden) { registration.cleanup(); return; }
      tools = registration; toolsEnabled.value = tools.registered;
      toolMessage.value = tools.registered ? `WebMCP: ${tools.names.length} místních nástrojů. Stejné brány a potvrzení jako tlačítka.`
        : tools.supported ? `Registrace WebMCP selhala: ${tools.error ?? 'neznámá chyba'}` : 'WebMCP tento prohlížeč nepodporuje. Ruční ovládání zůstává dostupné.';
    } catch (error) { if (requested === toolGeneration) toolMessage.value = error instanceof Error ? error.message : 'WebMCP registrace selhala.'; }
    finally { if (requested === toolGeneration) toolsPending.value = false; }
  },
  disableTools,
};

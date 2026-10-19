import { computed, ref, shallowRef } from 'vue';
import { registerPlugin } from '@capacitor/core';
import { ServerLogBuffer, type ServerLogPage } from '../debug/ServerLogs';
import { deviceSession } from './deviceSession';

export interface ServerModes {
  revision: string; dialogMode: 'fake' | 'responses'; voiceMode: 'fake' | 'openai';
  dialogAvailable: boolean; voiceAvailable: boolean; activeConversations: number;
  lifetime: 'until_restart'; personalityId?: string;
  characterEnabled?: boolean; characterTone?: 'Reserved' | 'Dry' | 'Adult' | 'Chaos';
  characterFinaleProfile?: 'Limited' | 'Blocked';
  personalities?: Array<{ id: string; name: string; description: string; promptSha256: string }>;
}
export interface DebugStatus {
  configured: boolean; running: boolean; session?: boolean; recording?: boolean; handsFree?: boolean;
  phase: string; mode: 'unknown' | 'fake' | 'openai'; errorCode?: string | null; costumeStatus?: string | null;
  accessMode?: 'none' | 'account' | 'legacy-service'; deviceId?: string | null; dialogMode?: 'fake' | 'responses' | null;
  previousAccountCleanupPending?: boolean;
  transcript?: Array<{ role: 'kufr' | 'visitor'; text: string }>; selected?: number | null;
  audioOutput?: { route: string; volume: number; receivedSeconds: number; renderedSeconds: number; engineRunning: boolean } | null;
  timings?: { dialogMs?: number; speechMs?: number; downloadMs?: number; firstAudioMs?: number; totalMs: number };
  server?: ServerModes | null;
  evenings?: { activeEveningId: string; evenings: Array<{ eveningId: string; name: string; createdAt: string; exchangeCount: number; droppedCount: number }> } | null;
}
export interface DebugBridge {
  status(): Promise<DebugStatus>; configure(): Promise<void>; forgetProfile(): Promise<void>;
  useAccount(options: { deviceId: string }): Promise<void>;
  readServerLogs(options: { after?: number; level: string }): Promise<ServerLogPage>;
  inspectEvenings(): Promise<void>; createEvening(options: { name: string; expectedEveningId: string }): Promise<void>;
  activateEvening(options: { eveningId: string; expectedEveningId: string }): Promise<void>;
  inspectBackend(): Promise<void>; inspectServer(): Promise<void>;
  changeServerModes(options: { expectedRevision: string; dialogMode: string; voiceMode: string; personalityId?: string;
    characterEnabled?: boolean; characterTone?: string; characterFinaleProfile?: string; resetCharacterVariety?: boolean }): Promise<void>;
  runOpeningSpeech(options: { expectedMode: 'fake' | 'openai' }): Promise<void>;
  captureCostumePhoto(): Promise<{ draftId: string | null }>; discardCostumePhoto(): Promise<void>;
  startConversation(options: { expectedMode: 'fake' | 'openai'; readyMask: number; photoDraftId?: string; costumeDescription?: string;
    voice: 'cedar' | 'marin' | 'onyx' | 'coral'; speed: number; engine: 'realtime' | 'gpt-live' | 'realtime-ptt' | 'chained' }): Promise<void>;
  answerConversation(options: { text: string }): Promise<void>; startRecording(): Promise<void>; finishRecording(): Promise<void>;
  speakSystem(): Promise<void>; stop(): Promise<void>; shareLog(options: { json: string }): Promise<void>;
}
export const debugBridge = registerPlugin<DebugBridge>('KufrDebug');
const status = shallowRef<DebugStatus>({ configured: false, running: false, phase: 'unknown', mode: 'unknown' });
const pending = ref(false);
const cancellationPending = ref(false);
const message = ref('Nativní testovací přístup je oddělený od přihlášení a párování kufru.');
const logs = new ServerLogBuffer();
const logEntries = shallowRef(logs.entries);
const logGap = ref(false);
const hasMore = ref(false);
const droppedWrites = ref(0);
let generation = 0;
let logGeneration = 0;
let cancelling: Promise<void> | null = null;
let listening = false;

function redactGuest(value: DebugStatus): DebugStatus {
  return { ...value, transcript: [], selected: null, timings: undefined, evenings: null, costumeStatus: null };
}
function visibility(): void {
  if (!document.hidden || !deviceSession.native) return;
  clearLogs(); status.value = redactGuest(status.value);
  void cancel();
}
function startLifecycle(): void {
  if (listening || !deviceSession.native) return;
  document.addEventListener('visibilitychange', visibility); listening = true;
}

async function refresh(): Promise<void> {
  if (!deviceSession.native) return;
  startLifecycle();
  const requested = generation;
  try { const next = await debugBridge.status(); if (requested === generation) status.value = document.hidden ? redactGuest(next) : next; }
  catch (error) { if (requested === generation) message.value = error instanceof Error ? error.message : 'Stav není dostupný.'; }
}

async function run(action: () => Promise<unknown>): Promise<void> {
  if (!deviceSession.native || pending.value) return;
  startLifecycle();
  const requested = generation;
  pending.value = true;
  message.value = 'Čekám na potvrzení…';
  try { await action(); if (requested === generation) message.value = 'Požadavek dokončen. Výsledek ověřte ve stavu.'; }
  catch (error) { if (requested === generation) message.value = error instanceof Error ? error.message : 'Požadavek selhal.'; }
  finally { if (requested === generation) { pending.value = false; await refresh(); } }
}

function clearLogs(): void { ++logGeneration; logs.clear(); logEntries.value = []; logGap.value = false; hasMore.value = false; droppedWrites.value = 0; }
function cancel(): Promise<void> {
  if (!deviceSession.native) return Promise.resolve();
  if (cancelling) return cancelling;
  const requested = ++generation;
  cancellationPending.value = true; pending.value = true;
  cancelling = (async () => {
    try { await debugBridge.stop(); await refresh(); }
    catch (error) { message.value = error instanceof Error ? error.message : 'Zastavení není potvrzené.'; }
    finally { if (requested === generation) { cancellationPending.value = false; pending.value = false; } cancelling = null; }
  })();
  return cancelling;
}

async function revokeUserGrants(): Promise<void> {
  if (!deviceSession.native) return;
  await cancel();
  ++generation; pending.value = true;
  status.value = { configured: false, running: false, phase: 'idle', mode: 'unknown' };
  clearLogs();
  try { await debugBridge.forgetProfile(); message.value = 'Testovací přístup odebraný. Párování kufru zůstává v Keychain.'; }
  finally { pending.value = false; }
}

export const nativeDebug = {
  status, pending, cancellationPending, message, logEntries, logGap, hasMore, droppedWrites, refresh, run, cancel,
  useAccount: (deviceId: string) => run(() => debugBridge.useAccount({ deviceId })),
  idle: computed(() => deviceSession.native && status.value.configured && !pending.value && !status.value.running && !status.value.session),
  clearLogs, revokeUserGrants,
  dispose(): void { document.removeEventListener('visibilitychange', visibility); listening = false; },
  async readLogs(level: string): Promise<void> {
    const requested = generation;
    const requestedLogs = logGeneration;
    await run(async () => {
      let page = await debugBridge.readServerLogs({ after: logs.cursor, level });
      if (requested !== generation || requestedLogs !== logGeneration) return;
      if (!logs.accept(page)) {
        page = await debugBridge.readServerLogs({ level });
        if (requested !== generation || requestedLogs !== logGeneration) return;
        logs.accept(page);
      }
      logEntries.value = [...logs.entries]; logGap.value = logs.gap; hasMore.value = logs.hasMore; droppedWrites.value = logs.droppedWrites;
    });
  },
  async forget(): Promise<void> {
    try { await revokeUserGrants(); }
    catch (error) { message.value = error instanceof Error ? error.message : 'Odebrání přístupu selhalo.'; }
  },
  exportLogs: () => debugBridge.shareLog({ json: JSON.stringify({ format: 'kufr-server-log-v1', storeId: logs.storeId,
    cursor: logs.cursor, gap: logs.gap, droppedWrites: logs.droppedWrites, entries: logs.entries }, null, 2) }),
};

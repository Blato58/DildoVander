export type ConversationEngine = 'gpt-live' | 'realtime';
export interface EngineCapability {
  engine: ConversationEngine; model: string; ready: boolean; unavailableReason?: string | null;
  voices: string[]; speed: { supported: boolean; min: number; max: number; default: number };
  inputModes: string[]; cameraSources: string[];
}
export interface EngineCapabilities { schemaVersion: 1; primaryEngine: ConversationEngine; engines: EngineCapability[] }
export interface GuestMemoryBinding { guestId: string; identityVerified: boolean; longTermAllowed: boolean; cloudExportAllowed: boolean }
export type MemoryStorageStatus = 'available' | 'warning' | 'full';
export interface MemoryStorageSnapshot {
  provider: string; configured: boolean; degraded: boolean; configurationStatus: string;
  pending: number; failed: number; deleting: number;
  storedBytes: number; capacityBytes: number; rejectedWrites: number;
  storageStatus?: MemoryStorageStatus; storageIncomplete?: boolean;
  lastStorageErrorCode?: string | null; lastStorageErrorAt?: string | null;
}
export interface MemoryInspection {
  identity: (GuestMemoryBinding & { version: number; conversationId: string; deviceId: string }) | null;
  epoch: number; deletionEpoch: number;
  records: Array<{ memoryId: string; text: string; sourceReference: string; revision: number; isActive: boolean }>;
  totalRecords: number; hasMore: boolean;
  memoryStatus: MemoryStorageSnapshot;
}
export const enginePreferenceKey = 'kufr.conversation.engine.v1';
export const engineLabels: Record<ConversationEngine, string> = { 'gpt-live': 'GPT Live · gpt-live-1', realtime: 'GPT Realtime 2.1 · gpt-realtime-2.1' };
export function validGuestMemoryId(value: string): boolean { return /^[A-Za-z0-9._-]{1,80}$/.test(value); }
export function migrateConversationEngine(value: string | null): { engine?: ConversationEngine; message?: string } {
  if (value === null) return { engine: 'gpt-live' };
  if (value === 'gpt-live' || value === 'realtime') return { engine: value };
  if (value === 'realtime-ptt') return { engine: 'realtime', message: 'Původní Realtime PTT bylo převedeno na GPT Realtime 2.1. Ověřte způsob vstupu.' };
  return { message: 'Uložený režim už není podporován. Vyberte GPT Live nebo GPT Realtime 2.1.' };
}
export function parseEngineCapabilities(value: unknown): EngineCapabilities | null {
  if (!value || typeof value !== 'object') return null;
  const data = value as EngineCapabilities;
  if (data.schemaVersion !== 1 || !['gpt-live', 'realtime'].includes(data.primaryEngine)
    || !Array.isArray(data.engines) || data.engines.length !== 2) return null;
  const seen = new Set<string>();
  const strings = (items: unknown): items is string[] => Array.isArray(items) && items.length <= 64
    && items.every(item => typeof item === 'string' && item.length > 0 && item.length <= 160);
  for (const item of data.engines) {
    if (!item || seen.has(item.engine) || !['gpt-live', 'realtime'].includes(item.engine)
      || item.model !== (item.engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1')
      || typeof item.ready !== 'boolean' || !strings(item.voices) || !strings(item.inputModes) || !strings(item.cameraSources)
      || !item.speed || typeof item.speed.supported !== 'boolean' || ![item.speed.min, item.speed.max, item.speed.default].every(Number.isFinite)
      || item.speed.min <= 0 || item.speed.min > item.speed.default || item.speed.default > item.speed.max) return null;
    seen.add(item.engine);
  }
  return data;
}

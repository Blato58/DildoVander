import { accountRequest } from '../identity/session';

export type CompletionUpdateKind = 'keepsake' | 'portrait';
export type CompletionUpdateState = 'pending' | 'processing' | 'partial' | 'ready' | 'failed' | 'unavailable';
export type CompletionUpdateReason = 'awaiting_capacity' | 'paused_preparing' | 'reconciliation_required' | 'generation_failed' | 'result_unavailable' | null;

export interface CompletionUpdate {
  id: string;
  sequence: number;
  kind: CompletionUpdateKind;
  state: CompletionUpdateState;
  reason: CompletionUpdateReason;
  resultLocator: string;
  canRetry: false;
  hasStory: boolean;
  hasArtwork: boolean;
  occurredAt: string;
  readAt: string | null;
}

export interface CompletionUpdatePage {
  schemaVersion: 1;
  items: CompletionUpdate[];
  nextCursor: string | null;
}

const uuidPattern = '[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const updateIdPattern = new RegExp(`^${uuidPattern}$`, 'i');
const locatorPattern = new RegExp(`^${uuidPattern}$`, 'i');
const timestampPattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$/;
const reasons = new Set<CompletionUpdateReason>([
  null, 'awaiting_capacity', 'paused_preparing', 'reconciliation_required', 'generation_failed', 'result_unavailable',
]);
const states = new Set<CompletionUpdateState>(['pending', 'processing', 'partial', 'ready', 'failed', 'unavailable']);
const kinds = new Set<CompletionUpdateKind>(['keepsake', 'portrait']);
const validReasonForState: Record<CompletionUpdateState, readonly CompletionUpdateReason[]> = {
  pending: ['awaiting_capacity', 'paused_preparing'],
  processing: [null, 'reconciliation_required'],
  partial: [null, 'reconciliation_required', 'generation_failed'],
  ready: [null],
  failed: ['generation_failed'],
  unavailable: ['result_unavailable'],
};

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.length && expected.every(key => Object.prototype.hasOwnProperty.call(value, key));
}

function validTimestamp(value: unknown): value is string {
  if (typeof value !== 'string' || !timestampPattern.test(value) || !Number.isFinite(Date.parse(value))) return false;
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})/.exec(value);
  if (!match) return false;
  const [year, month, day, hour, minute, second] = match.slice(1).map(Number);
  const date = new Date(Date.UTC(year, month - 1, day));
  return date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day
    && hour <= 23 && minute <= 59 && second <= 59;
}

function parseItem(value: unknown): CompletionUpdate | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const item = value as Record<string, unknown>;
  if (!exactKeys(item, [
    'id', 'sequence', 'kind', 'state', 'reason', 'resultLocator', 'canRetry', 'hasStory', 'hasArtwork', 'occurredAt', 'readAt',
  ])) return null;
  if (typeof item.id !== 'string' || !updateIdPattern.test(item.id)
    || !Number.isSafeInteger(item.sequence) || (item.sequence as number) <= 0
    || typeof item.kind !== 'string' || !kinds.has(item.kind as CompletionUpdateKind)
    || typeof item.state !== 'string' || !states.has(item.state as CompletionUpdateState)
    || !reasons.has(item.reason as CompletionUpdateReason)
    || typeof item.resultLocator !== 'string' || !locatorPattern.test(item.resultLocator)
    || item.canRetry !== false || typeof item.hasStory !== 'boolean' || typeof item.hasArtwork !== 'boolean'
    || !validTimestamp(item.occurredAt)
    || !(item.readAt === null || validTimestamp(item.readAt))) return null;

  const kind = item.kind as CompletionUpdateKind;
  const state = item.state as CompletionUpdateState;
  if (!validReasonForState[state].includes(item.reason as CompletionUpdateReason)) return null;
  if (state === 'ready' && (kind === 'keepsake'
    ? item.hasStory !== true || item.hasArtwork !== true
    : item.hasArtwork !== true)) return null;
  return item as unknown as CompletionUpdate;
}

/** Accepts only the closed server contract so untrusted fields never enter the UI. */
export function parseCompletionUpdatePage(value: unknown): CompletionUpdatePage | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const page = value as Record<string, unknown>;
  if (!exactKeys(page, ['schemaVersion', 'items', 'nextCursor']) || page.schemaVersion !== 1
    || !Array.isArray(page.items) || page.items.length > 50
    || !(page.nextCursor === null || (typeof page.nextCursor === 'string' && page.nextCursor.length > 0 && page.nextCursor.length <= 2048))) return null;

  const items: CompletionUpdate[] = [];
  const ids = new Set<string>();
  for (const raw of page.items) {
    const item = parseItem(raw);
    if (!item || ids.has(item.id)) return null;
    ids.add(item.id);
    items.push(item);
  }
  return { schemaVersion: 1, items, nextCursor: page.nextCursor as string | null };
}

export const completionUpdatePath = '/app/v1/customer/completion-updates';

export class CompletionUpdatesClient {
  private epoch = 0;
  private controller = new AbortController();
  private knownRows = new Map<string, CompletionUpdate>();
  private acknowledgedRows = new Map<string, string>();

  constructor(private readonly currentAccountId: () => string | null, private readonly canRead: () => boolean) {}

  invalidate(): void {
    this.epoch++;
    this.controller.abort();
    this.controller = new AbortController();
    this.knownRows.clear();
    this.acknowledgedRows.clear();
  }

  private capture() {
    const epoch = this.epoch;
    const accountId = this.currentAccountId();
    const signal = this.controller.signal;
    const check = () => {
      if (!accountId || !this.canRead() || epoch !== this.epoch || signal.aborted || accountId !== this.currentAccountId()) {
        throw new DOMException('Account context changed.', 'AbortError');
      }
    };
    check();
    return { accountId, check, signal };
  }

  async readPage(cursor: string | null = null): Promise<CompletionUpdatePage> {
    const { check, signal } = this.capture();
    const query = new URLSearchParams({ limit: '25' });
    if (cursor !== null) query.set('cursor', cursor);
    const page = parseCompletionUpdatePage(await accountRequest<unknown>(`${completionUpdatePath}?${query}`, 'GET', undefined, signal));
    check();
    if (!page) throw new Error('Invalid completion update response.');
    if (cursor === null) {
      const currentIds = new Set(page.items.map(item => item.id));
      for (const id of this.acknowledgedRows.keys()) {
        if (!currentIds.has(id)) this.acknowledgedRows.delete(id);
      }
      this.knownRows.clear();
    }
    const items = page.items.map(item => {
      const acknowledgedAt = this.acknowledgedRows.get(item.id);
      const readAt = acknowledgedAt ?? item.readAt;
      if (readAt !== null) this.acknowledgedRows.set(item.id, readAt);
      const merged = readAt === item.readAt ? item : { ...item, readAt };
      this.knownRows.set(item.id, merged);
      return merged;
    });
    // A client can be used independently of the component. Keep its acknowledgement
    // memory bounded even if a caller walks many cursor pages.
    while (this.acknowledgedRows.size > 200) {
      const oldestId = this.acknowledgedRows.keys().next().value as string | undefined;
      if (!oldestId) break;
      this.acknowledgedRows.delete(oldestId);
    }
    return { ...page, items };
  }

  async markRead(id: string): Promise<void> {
    const { check, signal } = this.capture();
    const item = this.knownRows.get(id);
    if (!item || !updateIdPattern.test(id) || item.readAt !== null) throw new Error('Completion update is not available to mark read.');
    check();
    await accountRequest<void>(`${completionUpdatePath}/${encodeURIComponent(id)}/read`, 'PUT', { schemaVersion: 1 }, signal);
    check();
    const current = this.knownRows.get(id);
    if (!current) return;
    const readAt = current.readAt ?? new Date().toISOString();
    this.acknowledgedRows.set(id, readAt);
    this.knownRows.set(id, { ...current, readAt });
  }
}

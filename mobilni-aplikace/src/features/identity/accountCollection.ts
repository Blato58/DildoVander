import { accountRequest } from './session';

export type CollectionAccessKind = 'originalOwner' | 'linkedViewer';
export type CollectionAccessFilter = 'all' | 'owned' | 'linked';
export type CollectionReadinessState = 'pending' | 'processing' | 'partial' | 'ready' | 'failed' | 'unavailable';
export type CollectionReadinessReason = 'awaiting_result' | 'awaiting_capacity' | 'paused_preparing'
  | 'reconciliation_required' | 'generation_failed' | 'result_unavailable' | null;

export interface CollectionReadiness {
  state: CollectionReadinessState;
  reason: CollectionReadinessReason;
  canRetry: boolean;
  shouldPoll: boolean;
  hasStory: boolean;
  hasArtwork: boolean;
}

export interface AccountCollectionItem {
  locator: string;
  linkedAt: number;
  officialName: string | null;
  storyTitle: string | null;
  hasPortrait: boolean;
  accessKind: CollectionAccessKind;
  canManage: boolean;
  canReadPrivate: boolean;
  canUnlink: boolean;
  readiness: CollectionReadiness;
  characterName: string | null;
  eveningAt: number | null;
}

export interface AccountCollectionPage {
  schemaVersion: 1;
  items: AccountCollectionItem[];
  nextCursor: string | null;
}

export interface AccountCollectionQuery {
  search: string;
  access: CollectionAccessFilter;
  cursor?: string | null;
}

const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;
const accessKinds = new Set<CollectionAccessKind>(['originalOwner', 'linkedViewer']);
const states = new Set<CollectionReadinessState>(['pending', 'processing', 'partial', 'ready', 'failed', 'unavailable']);
const reasons = new Set<CollectionReadinessReason>([
  null, 'awaiting_result', 'awaiting_capacity', 'paused_preparing', 'reconciliation_required', 'generation_failed', 'result_unavailable',
]);

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.length && expected.every(key => Object.prototype.hasOwnProperty.call(value, key));
}

function isUnixSeconds(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0
    && Number.isFinite(new Date(value * 1000).getTime());
}

function parseReadiness(value: unknown): CollectionReadiness | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const readiness = value as Record<string, unknown>;
  if (!exactKeys(readiness, ['state', 'reason', 'canRetry', 'shouldPoll', 'hasStory', 'hasArtwork'])
    || typeof readiness.state !== 'string' || !states.has(readiness.state as CollectionReadinessState)
    || !reasons.has(readiness.reason as CollectionReadinessReason)
    || typeof readiness.canRetry !== 'boolean' || typeof readiness.shouldPoll !== 'boolean'
    || typeof readiness.hasStory !== 'boolean' || typeof readiness.hasArtwork !== 'boolean') return null;
  return readiness as unknown as CollectionReadiness;
}

function parseItem(value: unknown): AccountCollectionItem | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const item = value as Record<string, unknown>;
  if (!exactKeys(item, [
    'locator', 'linkedAt', 'officialName', 'storyTitle', 'hasPortrait', 'accessKind', 'canManage',
    'canReadPrivate', 'canUnlink', 'readiness', 'characterName', 'eveningAt',
  ]) || typeof item.locator !== 'string' || !uuidPattern.test(item.locator)
    || !isUnixSeconds(item.linkedAt)
    || !(item.officialName === null || typeof item.officialName === 'string')
    || !(item.storyTitle === null || typeof item.storyTitle === 'string')
    || typeof item.hasPortrait !== 'boolean'
    || typeof item.accessKind !== 'string' || !accessKinds.has(item.accessKind as CollectionAccessKind)
    || typeof item.canManage !== 'boolean' || typeof item.canReadPrivate !== 'boolean' || typeof item.canUnlink !== 'boolean'
    || !(item.characterName === null || typeof item.characterName === 'string')
    || !(item.eveningAt === null || isUnixSeconds(item.eveningAt))) return null;

  const readiness = parseReadiness(item.readiness);
  if (!readiness) return null;
  // The linked projection must never carry owner-only session details.
  if (item.accessKind === 'linkedViewer' && (item.characterName !== null || item.eveningAt !== null)) return null;
  return { ...item, readiness } as AccountCollectionItem;
}

/** Parses only the closed collection contract; unknown fields never reach the view. */
export function parseAccountCollectionPage(value: unknown): AccountCollectionPage | null {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
  const page = value as Record<string, unknown>;
  if (!exactKeys(page, ['schemaVersion', 'items', 'nextCursor']) || page.schemaVersion !== 1
    || !Array.isArray(page.items) || page.items.length > 50
    || !(page.nextCursor === null || (typeof page.nextCursor === 'string' && page.nextCursor.length > 0 && page.nextCursor.length <= 2048))) return null;

  const items: AccountCollectionItem[] = [];
  const locators = new Set<string>();
  for (const raw of page.items) {
    const item = parseItem(raw);
    if (!item || locators.has(item.locator)) return null;
    locators.add(item.locator);
    items.push(item);
  }
  return { schemaVersion: 1, items, nextCursor: page.nextCursor as string | null };
}

export function isOwnedCollectionItem(item: AccountCollectionItem): boolean {
  return item.accessKind === 'originalOwner' && item.canManage && item.canReadPrivate && !item.canUnlink;
}

export function isLinkedCollectionItem(item: AccountCollectionItem): boolean {
  return item.accessKind === 'linkedViewer' && !item.canManage && !item.canReadPrivate && item.canUnlink;
}

export const accountCollectionPath = '/app/v1/archive/collection';

export class AccountCollectionClient {
  private epoch = 0;
  private controller = new AbortController();
  private knownRows = new Map<string, AccountCollectionItem>();

  constructor(private readonly currentAccountId: () => string | null, private readonly canRead: () => boolean) {}

  invalidate(): void {
    this.epoch++;
    this.controller.abort();
    this.controller = new AbortController();
    this.knownRows.clear();
  }

  private capture() {
    const epoch = this.epoch;
    const accountId = this.currentAccountId();
    const signal = this.controller.signal;
    const check = () => {
      if (!accountId || !this.canRead() || signal.aborted || epoch !== this.epoch || accountId !== this.currentAccountId()) {
        throw new DOMException('Account context changed.', 'AbortError');
      }
    };
    check();
    return { accountId, check, signal };
  }

  async readPage(query: AccountCollectionQuery): Promise<AccountCollectionPage> {
    const search = query.search.trim();
    if (search.length > 80 || !['all', 'owned', 'linked'].includes(query.access)
      || (query.cursor !== undefined && query.cursor !== null && (query.cursor.length === 0 || query.cursor.length > 2048))) {
      throw new Error('Invalid account collection query.');
    }
    const { check, signal } = this.capture();
    const params = new URLSearchParams({ limit: '25', access: query.access });
    if (search) params.set('q', search);
    if (query.cursor) params.set('cursor', query.cursor);
    const page = parseAccountCollectionPage(await accountRequest<unknown>(`${accountCollectionPath}?${params}`, 'GET', undefined, signal));
    check();
    if (!page) throw new Error('Invalid account collection response.');
    if (!query.cursor) this.knownRows.clear();
    for (const item of page.items) this.knownRows.set(item.locator, item);
    while (this.knownRows.size > 200) {
      const oldest = [...this.knownRows.values()].reduce((a, b) => a.linkedAt <= b.linkedAt ? a : b);
      this.knownRows.delete(oldest.locator);
    }
    return page;
  }

  async unlink(locator: string): Promise<void> {
    const { check, signal } = this.capture();
    const item = this.knownRows.get(locator);
    if (!item || !uuidPattern.test(locator) || !isLinkedCollectionItem(item)) {
      throw new Error('This collection item cannot be unlinked.');
    }
    await accountRequest<void>(`${accountCollectionPath.replace('/collection', '/associations')}/${encodeURIComponent(locator)}`, 'DELETE', undefined, signal);
    check();
  }
}

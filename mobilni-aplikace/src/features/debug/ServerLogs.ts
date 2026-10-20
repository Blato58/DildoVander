export interface ServerLogEntry {
  id: number;
  timestamp: string;
  level: 'info' | 'warning' | 'error';
  eventCode: string;
  traceId?: string | null;
  route?: string | null;
  method?: string | null;
  status?: number | null;
  durationMs?: number | null;
  operationId?: string | null;
  conversationId?: string | null;
  code?: string | null;
}

export interface ServerLogPage {
  storeId: string;
  oldestCursor: number;
  latestCursor: number;
  nextCursor: number;
  hasMore: boolean;
  truncated: boolean;
  droppedWrites: number;
  entries: ServerLogEntry[];
}

export class ServerLogBuffer {
  entries: ServerLogEntry[] = [];
  cursor?: number;
  storeId?: string;
  gap = false;
  hasMore = false;
  droppedWrites = 0;

  clear(): void {
    this.entries = []; this.cursor = undefined; this.storeId = undefined;
    this.gap = false; this.hasMore = false; this.droppedWrites = 0;
  }

  accept(page: ServerLogPage): boolean {
    // A recreated database has a different cursor namespace. Ask for a fresh
    // tail before displaying it, even if its new IDs exceed the old cursor.
    if (this.storeId && this.storeId !== page.storeId) { this.clear(); return false; }
    if (page.truncated) { this.entries = []; this.gap = true; }
    this.storeId = page.storeId;
    const merged = new Map(this.entries.map(entry => [entry.id, entry]));
    for (const entry of page.entries) { merged.set(entry.id, entry); }
    this.entries = [...merged.values()].filter(entry => entry.id >= page.oldestCursor)
      .sort((a, b) => a.id - b.id).slice(-200);
    this.cursor = page.nextCursor;
    this.hasMore = page.hasMore;
    this.droppedWrites = page.droppedWrites;
    return true;
  }
}

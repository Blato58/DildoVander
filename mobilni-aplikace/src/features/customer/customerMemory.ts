import { accountTransport } from '../identity/session';

export interface CustomerMemoryItem {
  readonly id: string;
  readonly revision: number;
  readonly text: string;
  readonly kind: 'UserReportedFact' | 'ConfirmedSelection';
  readonly sourceReference: string;
  readonly createdAt: string;
  readonly profileId: string;
  readonly customerSessionId: string;
  readonly canCorrect: boolean;
  readonly canForget: boolean;
}

export interface CustomerMemoryList {
  readonly revision: string;
  readonly items: readonly CustomerMemoryItem[];
  readonly complete: true;
}

export class CustomerMemoryError extends Error {
  constructor(readonly code: string, readonly status = 0) {
    super(code);
    this.name = 'CustomerMemoryError';
  }
}

type Transport = (path: string, init?: RequestInit) => Promise<Response>;
const root = '/app/v1/customer/memory';
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const profileId = /^(?:[0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$/i;
const maximumBytes = 32 * 1024 * 1024;
const encoder = new TextEncoder();

function record(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function parseCustomerMemories(value: unknown): CustomerMemoryList | null {
  if (!record(value) || typeof value.revision !== 'string'
    || !/^[0-9a-f]{64}$/i.test(value.revision) || value.complete !== true
    || !Array.isArray(value.items) || value.items.length > 4096) return null;
  const versions = new Set<string>();
  for (const item of value.items) {
    if (!record(item) || typeof item.id !== 'string' || !uuid.test(item.id)
      || !Number.isSafeInteger(item.revision) || Number(item.revision) < 1
      || typeof item.text !== 'string' || encoder.encode(item.text).length > maximumBytes
      || !['UserReportedFact', 'ConfirmedSelection'].includes(String(item.kind))
      || typeof item.sourceReference !== 'string' || item.sourceReference.length > 256
      || typeof item.createdAt !== 'string' || !Number.isFinite(Date.parse(item.createdAt))
      || typeof item.profileId !== 'string' || !profileId.test(item.profileId)
      || typeof item.customerSessionId !== 'string' || !uuid.test(item.customerSessionId)
      || typeof item.canCorrect !== 'boolean' || typeof item.canForget !== 'boolean'
      || (item.kind === 'ConfirmedSelection' && item.canCorrect)) return null;
    const version = `${item.id.toLowerCase()}:${item.revision}`;
    if (versions.has(version)) return null;
    versions.add(version);
  }
  return value as unknown as CustomerMemoryList;
}

function checkCurrent(signal?: AbortSignal, current: () => boolean = () => true): void {
  if (signal?.aborted || !current()) throw new DOMException('Context changed.', 'AbortError');
}

async function requireSuccess(response: Response): Promise<void> {
  if (response.ok) return;
  let code = 'memory_request_failed';
  // Error documents have a separate small bound; never render arbitrary server text.
  const reader = response.body?.getReader();
  let text = '';
  if (reader) {
    const decoder = new TextDecoder();
    let length = 0;
    try {
      while (true) {
        const part = await reader.read();
        if (part.done) break;
        length += part.value.length;
        if (length > 8192) { text = ''; break; }
        text += decoder.decode(part.value, { stream: true });
      }
    } finally {
      await reader.cancel().catch(() => undefined);
      reader.releaseLock();
    }
  }
  if (text) {
    try {
      const problem: unknown = JSON.parse(text);
      if (record(problem) && typeof problem.code === 'string' && /^[a-z_]{1,80}$/.test(problem.code)) code = problem.code;
    } catch { /* A non-JSON error still retains its HTTP status. */ }
  }
  throw new CustomerMemoryError(code, response.status);
}

export async function readCustomerMemories(
  signal?: AbortSignal, current?: () => boolean, transport: Transport = accountTransport,
): Promise<CustomerMemoryList> {
  checkCurrent(signal, current);
  const response = await transport(root, { signal });
  checkCurrent(signal, current);
  await requireSuccess(response);
  if (response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json') {
    await response.body?.cancel().catch(() => undefined);
    throw new CustomerMemoryError('invalid_memory_response');
  }
  const reader = response.body?.getReader();
  if (!reader) throw new CustomerMemoryError('invalid_memory_response');
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const part = await reader.read();
      checkCurrent(signal, current);
      if (part.done) break;
      length += part.value.length;
      if (length > maximumBytes) throw new CustomerMemoryError('memory_response_too_large');
      chunks.push(part.value);
    }
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
  const bytes = new Uint8Array(length);
  let offset = 0;
  for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
  let parsed: CustomerMemoryList | null = null;
  try { parsed = parseCustomerMemories(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes))); }
  catch { /* Malformed content cannot authorize any action. */ }
  checkCurrent(signal, current);
  if (!parsed) throw new CustomerMemoryError('invalid_memory_response');
  return parsed;
}

/** Mutations are explicit once-only sends. The caller must refresh after any uncertain outcome. */
export async function mutateCustomerMemory(
  item: CustomerMemoryItem, change: { text: string } | { confirmed: true },
  signal?: AbortSignal, current?: () => boolean, transport: Transport = accountTransport,
): Promise<void> {
  if (!uuid.test(item.id) || !Number.isSafeInteger(item.revision) || item.revision < 1) {
    throw new CustomerMemoryError('invalid_memory_request');
  }
  const correcting = 'text' in change;
  if (correcting ? (!item.canCorrect || item.kind !== 'UserReportedFact'
    || !change.text.trim() || encoder.encode(change.text).length > 2000)
    : (!item.canForget || change.confirmed !== true)) throw new CustomerMemoryError('invalid_memory_request');
  const body = JSON.stringify({ expectedRevision: item.revision, ...change });
  checkCurrent(signal, current);
  const response = await transport(`${root}/${item.id}${correcting ? '' : '/forget'}`, {
    method: correcting ? 'PUT' : 'POST', signal,
    headers: { 'Content-Type': 'application/json' }, body,
  });
  checkCurrent(signal, current);
  await requireSuccess(response);
  checkCurrent(signal, current);
  if (response.status !== 204) throw new CustomerMemoryError('memory_mutation_outcome_unknown');
}

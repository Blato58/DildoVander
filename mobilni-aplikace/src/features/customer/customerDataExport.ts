import { secureUuid } from '../../application/identifiers.mjs';
import { accountTransport } from '../identity/session';

export type CustomerDataExportState = 'requested' | 'preparing' | 'ready' | 'failed';
export type CustomerDataExportReason =
  | 'inventory_unavailable'
  | 'memory_unavailable'
  | 'source_changed'
  | 'payload_too_large'
  | 'preparation_interrupted'
  | 'download_expired'
  | 'export_failed';

export interface CustomerDataExportJob {
  readonly schemaVersion: 1;
  readonly id: string;
  readonly state: CustomerDataExportState;
  readonly reason: CustomerDataExportReason | null;
  readonly requestedAt: string;
  readonly readyAt: string | null;
  readonly downloadExpiresAt: string | null;
  readonly downloadPath: string | null;
}

export interface CustomerDataExportMetadata {
  readonly jobId: string | null;
  readonly pendingIdempotencyKey: string | null;
}

export interface CustomerDataExportStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export type CustomerAccountTransport = (path: string, init?: RequestInit) => Promise<Response>;

export class CustomerDataExportError extends Error {
  constructor(readonly kind: 'unauthenticated' | 'invalid-response' | 'http' | 'expired' | 'too-large' | 'unavailable', readonly status?: number) {
    super(kind);
    this.name = 'CustomerDataExportError';
  }
}

const exportRoot = '/app/v1/customer/data/exports';
const storagePrefix = 'dildovander.customer-data-export.v1';
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
export const customerDataExportMaxDownloadBytes = 32 * 1024 * 1024;
const knownReasons: readonly CustomerDataExportReason[] = [
  'inventory_unavailable', 'memory_unavailable', 'source_changed', 'payload_too_large',
  'preparation_interrupted', 'download_expired', 'export_failed',
];
const allowedDtoKeys = new Set([
  'schemaVersion', 'id', 'state', 'reason', 'requestedAt', 'readyAt', 'downloadExpiresAt', 'downloadPath',
]);

export const customerDataExportReasonCopy: Readonly<Record<CustomerDataExportReason, string>> = {
  inventory_unavailable: 'Údaje účtu se nepodařilo ověřit. Zkuste stav obnovit později.',
  memory_unavailable: 'Údaje paměti se nepodařilo bezpečně načíst. Zkuste stav obnovit později.',
  source_changed: 'Podkladová data se změnila. Připravte nový export.',
  payload_too_large: 'Export překročil bezpečný limit velikosti. Zkuste to později.',
  preparation_interrupted: 'Příprava exportu byla přerušena. Obnovte jeho stav později.',
  download_expired: 'Export už vypršel. Připravte nový export.',
  export_failed: 'Přípravu exportu se nepodařilo dokončit. Obnovte stav nebo požádejte o nový export.',
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

async function readExportStatus(response: Response, check: () => void): Promise<unknown> {
  if (response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json') {
    await response.body?.cancel().catch(() => undefined);
    throw new CustomerDataExportError('invalid-response');
  }
  const reader = response.body?.getReader();
  if (!reader) throw new CustomerDataExportError('invalid-response');
  const chunks: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const part = await reader.read();
      check();
      if (part.done) break;
      length += part.value.length;
      if (length > 64 * 1024) throw new CustomerDataExportError('invalid-response');
      chunks.push(part.value);
    }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    check();
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch (error) {
    check();
    if (error instanceof CustomerDataExportError) throw error;
    throw new CustomerDataExportError('invalid-response');
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

function parseIso(value: unknown): number | null {
  if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value)) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

export function customerDataExportPath(id: string): string {
  if (!uuidPattern.test(id)) throw new CustomerDataExportError('invalid-response');
  return `${exportRoot}/${id}`;
}

export function parseCustomerDataExportJob(value: unknown): CustomerDataExportJob | null {
  if (!isRecord(value) || Object.keys(value).some((key) => !allowedDtoKeys.has(key))) return null;
  if (Object.keys(value).length !== allowedDtoKeys.size || value.schemaVersion !== 1 || typeof value.id !== 'string' || !uuidPattern.test(value.id)) return null;
  if (value.state !== 'requested' && value.state !== 'preparing' && value.state !== 'ready' && value.state !== 'failed') return null;
  if (value.reason !== null && (typeof value.reason !== 'string' || !knownReasons.includes(value.reason as CustomerDataExportReason))) return null;
  const requestedAt = parseIso(value.requestedAt);
  if (requestedAt === null) return null;

  if (value.state === 'ready') {
    const readyAt = parseIso(value.readyAt);
    const expiresAt = parseIso(value.downloadExpiresAt);
    const canonicalPath = customerDataExportPath(value.id);
    if (value.reason !== null || readyAt === null || expiresAt === null || readyAt < requestedAt || expiresAt <= readyAt || value.downloadPath !== `${canonicalPath}/download`) return null;
  } else if (value.readyAt !== null || value.downloadExpiresAt !== null || value.downloadPath !== null) {
    return null;
  }
  if (value.state === 'failed' && value.reason === null) return null;

  return value as unknown as CustomerDataExportJob;
}

export function customerDataExportLocationMatches(location: string | null, id: string): boolean {
  try { return location === customerDataExportPath(id); }
  catch { return false; }
}

function browserStorage(): CustomerDataExportStorage | undefined {
  try { return typeof window === 'undefined' ? undefined : window.localStorage; }
  catch { return undefined; }
}

function metadataKey(accountId: string): string {
  return `${storagePrefix}.${encodeURIComponent(accountId)}`;
}

function parseMetadata(raw: string | null): CustomerDataExportMetadata {
  if (!raw) return { jobId: null, pendingIdempotencyKey: null };
  try {
    const value: unknown = JSON.parse(raw);
    if (!isRecord(value) || Object.keys(value).length !== 3 || value.schemaVersion !== 1
      || !Object.prototype.hasOwnProperty.call(value, 'jobId')
      || !Object.prototype.hasOwnProperty.call(value, 'pendingIdempotencyKey')) {
      return { jobId: null, pendingIdempotencyKey: null };
    }
    const jobId = value.jobId === null || (typeof value.jobId === 'string' && uuidPattern.test(value.jobId)) ? value.jobId : null;
    const pendingIdempotencyKey = value.pendingIdempotencyKey === null
      || (typeof value.pendingIdempotencyKey === 'string' && uuidPattern.test(value.pendingIdempotencyKey))
      ? value.pendingIdempotencyKey : null;
    return { jobId, pendingIdempotencyKey };
  } catch { return { jobId: null, pendingIdempotencyKey: null }; }
}

function safeReadMetadata(storage: CustomerDataExportStorage | undefined, accountId: string): CustomerDataExportMetadata {
  try { return parseMetadata(storage?.getItem(metadataKey(accountId)) ?? null); }
  catch { return { jobId: null, pendingIdempotencyKey: null }; }
}

export interface CustomerDataExportClientOptions {
  currentAccountId: () => string | null | undefined;
  transport?: CustomerAccountTransport;
  storage?: CustomerDataExportStorage;
  createIdempotencyKey?: () => string;
  now?: () => number;
}

/** Account-bound export operations. Invalidation aborts requests but never drops an uncertain POST key. */
export class CustomerDataExportClient {
  private generation = 0;
  private accountId: string | null = null;
  private rejectedPendingKey: string | null = null;
  private metadata: CustomerDataExportMetadata = { jobId: null, pendingIdempotencyKey: null };
  private readonly metadataByAccount = new Map<string, CustomerDataExportMetadata>();
  private readonly controllers = new Set<AbortController>();
  private disposed = false;
  private readonly transport: CustomerAccountTransport;
  private readonly storage: CustomerDataExportStorage | undefined;
  private readonly createIdempotencyKey: () => string;
  private readonly now: () => number;

  constructor(private readonly options: CustomerDataExportClientOptions) {
    this.transport = options.transport ?? accountTransport;
    this.storage = options.storage ?? browserStorage();
    this.createIdempotencyKey = options.createIdempotencyKey ?? secureUuid;
    this.now = options.now ?? Date.now;
  }

  get recoveryMetadata(): CustomerDataExportMetadata { return { ...this.metadata }; }

  setAccount(accountId: string | null | undefined): void {
    const next = accountId || null;
    if (next === this.accountId) return;
    this.invalidateRequests();
    this.rejectedPendingKey = null;
    this.accountId = next;
    if (!next) { this.metadata = { jobId: null, pendingIdempotencyKey: null }; return; }
    const cached = this.metadataByAccount.get(next);
    this.metadata = cached ? { ...cached } : safeReadMetadata(this.storage, next);
    this.metadataByAccount.set(next, { ...this.metadata });
  }

  invalidateRequests(): void {
    this.generation++;
    for (const controller of this.controllers) controller.abort();
    this.controllers.clear();
  }

  dispose(): void {
    this.disposed = true;
    this.invalidateRequests();
  }

  private capture() {
    const generation = this.generation;
    const accountId = this.accountId;
    if (this.disposed || !accountId) throw new CustomerDataExportError('unauthenticated');
    return () => {
      if (this.disposed || generation !== this.generation || accountId !== this.accountId
        || accountId !== (this.options.currentAccountId() || null)) {
        throw new DOMException('Export account context changed.', 'AbortError');
      }
    };
  }

  private storeMetadata(next: CustomerDataExportMetadata): void {
    this.metadata = { ...next };
    if (!this.accountId) return;
    this.metadataByAccount.set(this.accountId, { ...next });
    try {
      this.storage?.setItem(metadataKey(this.accountId), JSON.stringify({ schemaVersion: 1, ...next }));
    } catch { /* Keep job recovery metadata in memory if browser storage is denied. */ }
  }

  private async send(path: string, method: 'GET' | 'POST' | 'DELETE', key?: string): Promise<{ response: Response; check: () => void; release: () => void }> {
    const check = this.capture();
    check();
    const controller = new AbortController();
    this.controllers.add(controller);
    const headers = new Headers({ Accept: 'application/json' });
    let body: string | undefined;
    if (method === 'POST') {
      if (!key || !uuidPattern.test(key)) {
        this.controllers.delete(controller);
        throw new CustomerDataExportError('invalid-response');
      }
      body = JSON.stringify({ schemaVersion: 1 });
      headers.set('Content-Type', 'application/json');
      headers.set('Idempotency-Key', key);
    }
    try {
      const response = await this.transport(path, { method, headers, body, signal: controller.signal, redirect: 'error' });
      check();
      return { response, check, release: () => this.controllers.delete(controller) };
    } catch (error) {
      this.controllers.delete(controller);
      throw error;
    }
  }

  async refresh(): Promise<CustomerDataExportJob | null> {
    const id = this.metadata.jobId;
    if (!id) return null;
    const request = await this.send(customerDataExportPath(id), 'GET');
    try {
      if (request.response.status !== 200) throw new CustomerDataExportError('http', request.response.status);
      const value = await readExportStatus(request.response, request.check);
      request.check();
      const job = parseCustomerDataExportJob(value);
      if (!job || job.id !== id) throw new CustomerDataExportError('invalid-response');
      return job;
    } finally { request.release(); }
  }

  async createOrRetry(): Promise<CustomerDataExportJob> {
    if (!this.accountId) throw new CustomerDataExportError('unauthenticated');
    let key = this.metadata.pendingIdempotencyKey;
    if (!key) {
      key = this.createIdempotencyKey();
      if (!uuidPattern.test(key)) throw new CustomerDataExportError('invalid-response');
      this.storeMetadata({ jobId: null, pendingIdempotencyKey: key });
    }
    return this.submitWithKey(key);
  }

  /** Explicitly replace a key the server has definitively rejected with 409. */
  async createNewAttempt(): Promise<CustomerDataExportJob> {
    if (!this.accountId) throw new CustomerDataExportError('unauthenticated');
    if (!this.metadata.pendingIdempotencyKey || this.rejectedPendingKey !== this.metadata.pendingIdempotencyKey) {
      throw new CustomerDataExportError('invalid-response');
    }
    const key = this.createIdempotencyKey();
    if (!uuidPattern.test(key)) throw new CustomerDataExportError('invalid-response');
    this.rejectedPendingKey = null;
    this.storeMetadata({ jobId: null, pendingIdempotencyKey: key });
    return this.submitWithKey(key);
  }

  private async submitWithKey(key: string): Promise<CustomerDataExportJob> {
    let request: Awaited<ReturnType<CustomerDataExportClient['send']>> | undefined;
    try {
      request = await this.send(exportRoot, 'POST', key);
      if (request.response.status !== 202) throw new CustomerDataExportError('http', request.response.status);
      const value = await readExportStatus(request.response, request.check);
      request.check();
      const job = parseCustomerDataExportJob(value);
      if (!job || !customerDataExportLocationMatches(request.response.headers.get('Location'), job.id)) {
        throw new CustomerDataExportError('invalid-response');
      }
      this.rejectedPendingKey = null;
      this.storeMetadata({ jobId: job.id, pendingIdempotencyKey: null });
      return job;
    } catch (error) {
      if (error instanceof CustomerDataExportError && error.status === 409
        && this.metadata.pendingIdempotencyKey === key) {
        this.rejectedPendingKey = key;
      }
      throw error;
    } finally { request?.release(); }
  }

  async deleteJob(): Promise<void> {
    const id = this.metadata.jobId;
    if (!id) return;
    const request = await this.send(customerDataExportPath(id), 'DELETE');
    try {
      if (request.response.status !== 204) throw new CustomerDataExportError('http', request.response.status);
      request.check();
      this.storeMetadata({ jobId: null, pendingIdempotencyKey: null });
    } finally { request.release(); }
  }

  async download(job: CustomerDataExportJob): Promise<Blob> {
    const requestedAccountId = this.accountId;
    const id = this.metadata.jobId;
    if (!id || job.id !== id || job.state !== 'ready' || !job.downloadExpiresAt
      || !job.downloadPath || job.downloadPath !== `${customerDataExportPath(id)}/download`) {
      throw new CustomerDataExportError('invalid-response');
    }
    if (Date.parse(job.downloadExpiresAt) <= this.now()) throw new CustomerDataExportError('expired');
    const request = await this.send(job.downloadPath, 'GET');
    try {
      if (!request.response.ok) throw new CustomerDataExportError('http', request.response.status);
      const contentType = request.response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase();
      if (contentType !== 'application/json') throw new CustomerDataExportError('invalid-response');
      const lengthHeader = request.response.headers.get('Content-Length');
      if (lengthHeader !== null && (!/^\d+$/.test(lengthHeader) || Number(lengthHeader) > customerDataExportMaxDownloadBytes)) {
        throw new CustomerDataExportError('too-large');
      }
      const body = request.response.body;
      if (!body) throw new CustomerDataExportError('invalid-response');
      const reader = body.getReader();
      const chunks: Uint8Array[] = [];
      let total = 0;
      try {
        while (true) {
          const result = await reader.read();
          request.check();
          if (result.done) break;
          total += result.value.byteLength;
          if (total > customerDataExportMaxDownloadBytes) {
            throw new CustomerDataExportError('too-large');
          }
          chunks.push(result.value);
        }
      } catch (error) {
        if (error instanceof CustomerDataExportError) throw error;
        request.check();
        throw error;
      } finally {
        await reader.cancel().catch(() => undefined);
        reader.releaseLock();
      }
      request.check();
      const bytes = new Uint8Array(total);
      let offset = 0;
      for (const chunk of chunks) {
        bytes.set(chunk, offset);
        offset += chunk.byteLength;
      }
      let document: unknown;
      try { document = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes)); }
      catch { throw new CustomerDataExportError('invalid-response'); }
      request.check();
      if (!isRecord(document) || document.schemaVersion !== 1 || !isRecord(document.account)
        || document.account.id !== requestedAccountId) throw new CustomerDataExportError('invalid-response');
      if (Date.parse(job.downloadExpiresAt) <= this.now()) throw new CustomerDataExportError('expired');
      return new Blob([bytes.buffer], { type: 'application/json' });
    } finally { request.release(); }
  }
}

export function exportErrorCopy(error: unknown): string {
  if (error instanceof CustomerDataExportError) {
    if (error.kind === 'unauthenticated') return 'Přihlaste se ke svému účtu a export otevřete znovu.';
    if (error.kind === 'expired' || error.status === 410) return 'Stažení už vypršelo. Obnovte stav nebo připravte nový export.';
    if (error.kind === 'too-large' || error.status === 413) return 'Soubor překročil bezpečný limit pro stažení.';
    if (error.status === 404) return 'Tento export není pro aktuální přihlášení dostupný. Můžete výslovně připravit nový.';
    if (error.status === 409) return 'Server odmítl neaktuální požadavek. Obnovte stav nebo výslovně požádejte o nový export.';
    if (error.status === 503) return 'Služba je dočasně zaneprázdněná. Obnovte stav později.';
    if (error.kind === 'invalid-response') return 'Server vrátil neplatný stav exportu. Zkontrolujte jej znovu.';
  }
  return 'Export se nepodařilo ověřit. Zkontrolujte připojení a zkuste to znovu.';
}

export async function deliverBrowserDownload(blob: Blob, filename: string): Promise<void> {
  const url = URL.createObjectURL(blob);
  try {
    const link = document.createElement('a');
    link.href = url;
    link.download = filename;
    link.hidden = true;
    document.body.append(link);
    link.click();
    link.remove();
  } finally {
    window.setTimeout(() => URL.revokeObjectURL(url), 0);
  }
}

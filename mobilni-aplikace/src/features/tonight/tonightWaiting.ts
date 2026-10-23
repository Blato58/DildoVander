import { secureUuid } from '../../application/identifiers.mjs';

export type TonightWaitingAvailability = 'available' | 'busy' | 'recovering' | 'paused' | 'offline' | 'unknown';
export type TonightWaitingEventState = 'open' | 'paused' | 'closed';
export type TonightWaitingHeadState = 'waiting' | 'claimed';
export type TonightWaitingCommandKind = 'open' | 'state' | 'skip';

export interface TonightWaitingAvailabilityView {
  readonly state: TonightWaitingAvailability;
  readonly reason: string;
  readonly fresh: boolean;
}

export interface TonightWaitingEvent {
  readonly id: string;
  readonly deviceId: string;
  readonly eveningId: string;
  readonly state: TonightWaitingEventState;
  readonly revision: number;
}

export interface TonightWaitingHead {
  readonly entryId: string;
  readonly revision: number;
  readonly state: TonightWaitingHeadState;
}

export interface TonightWaitingSnapshot {
  readonly schemaVersion: 1;
  readonly availability: TonightWaitingAvailabilityView;
  readonly event: TonightWaitingEvent | null;
  readonly head: TonightWaitingHead | null;
  readonly count: number;
}

export interface TonightWaitingTransport {
  (path: string, init?: RequestInit): Promise<Response>;
}

export interface TonightWaitingStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem(key: string): void;
}

export interface TonightWaitingCommandMetadata {
  readonly requestId: string;
  readonly kind: TonightWaitingCommandKind;
  readonly fingerprint: string;
  readonly body: Readonly<Record<string, unknown>>;
}

export type TonightWaitingErrorKind = 'invalid-device' | 'invalid-response' | 'unauthenticated'
  | 'forbidden' | 'not-found' | 'conflict' | 'busy' | 'request-failed' | 'pending-command'
  | 'metadata-invalid';

export class TonightWaitingError extends Error {
  constructor(readonly kind: TonightWaitingErrorKind, readonly status?: number) {
    super(kind);
    this.name = 'TonightWaitingError';
  }
}

export interface TonightWaitingClientOptions {
  readonly accountId: string;
  readonly transport: TonightWaitingTransport;
  readonly storage?: TonightWaitingStorage;
  readonly createRequestId?: () => string;
}

const apiRoot = '/app/v1/admin/devices';
const storagePrefix = 'dildovander.tonight-waiting.v1';
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const availabilityStates: readonly TonightWaitingAvailability[] = [
  'available', 'busy', 'recovering', 'paused', 'offline', 'unknown',
];
const availabilityReasons = new Set([
  'lease_unresolved', 'lease_occupied', 'event_closed', 'customer_mode_disabled', 'admission_paused',
  'runtime_identity_unverified', 'runtime_observation_stale', 'runtime_ready', 'runtime_offline',
  'runtime_unknown', 'lease_unknown',
]);
const availabilityCopy: Readonly<Record<string, string>> = {
  lease_unresolved: 'Probíhá obnova předchozího rozhovoru.',
  lease_occupied: 'Kufr právě obsluhuje jiný rozhovor.',
  event_closed: 'Událost čekacího seznamu je uzavřená.',
  customer_mode_disabled: 'Zákaznický režim není povolen.',
  admission_paused: 'Přijímání do čekacího seznamu je pozastaveno.',
  runtime_identity_unverified: 'Stav kufru se nepodařilo ověřit.',
  runtime_observation_stale: 'Hlášení kufru je zastaralé.',
  runtime_ready: 'Kufr hlásí připravenost.',
  runtime_offline: 'Kufr hlásí, že je offline.',
  runtime_unknown: 'Stav kufru se ověřuje.',
  lease_unknown: 'Dostupnost kufru se nepodařilo ověřit.',
};
const maximumResponseBytes = 64 * 1024;
const maximumMetadataBytes = 1024;

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.length && expected.every(key => Object.hasOwn(value, key));
}

function isGuid(value: unknown): value is string {
  return typeof value === 'string' && guidPattern.test(value)
    && value.toLowerCase() !== '00000000-0000-0000-0000-000000000000';
}

function isRevision(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) > 0;
}

export function isTonightWaitingDeviceId(value: unknown): value is string {
  return typeof value === 'string' && /^[A-Za-z0-9_.-]{1,128}$/.test(value)
    && value !== '.' && value !== '..';
}

export function tonightWaitingPath(deviceId: string): string {
  if (!isTonightWaitingDeviceId(deviceId)) throw new TonightWaitingError('invalid-device');
  return `${apiRoot}/${encodeURIComponent(deviceId)}/waiting`;
}

export function parseTonightWaitingSnapshot(value: unknown, deviceId: string): TonightWaitingSnapshot {
  if (!isTonightWaitingDeviceId(deviceId) || !isRecord(value)
      || !exactKeys(value, ['schemaVersion', 'availability', 'event', 'head', 'count'])
      || value.schemaVersion !== 1 || !Number.isSafeInteger(value.count) || (value.count as number) < 0) {
    throw new TonightWaitingError('invalid-response');
  }
  const availability = value.availability;
  if (!isRecord(availability) || !exactKeys(availability, ['state', 'reason', 'fresh'])
      || !availabilityStates.includes(availability.state as TonightWaitingAvailability)
      || typeof availability.reason !== 'string' || !availabilityReasons.has(availability.reason)
      || typeof availability.fresh !== 'boolean') {
    throw new TonightWaitingError('invalid-response');
  }

  let event: TonightWaitingEvent | null = null;
  if (value.event !== null) {
    const raw = value.event;
    if (!isRecord(raw) || !exactKeys(raw, ['id', 'deviceId', 'eveningId', 'state', 'revision'])
        || !isGuid(raw.id) || raw.deviceId !== deviceId || !isGuid(raw.eveningId)
        || !['open', 'paused', 'closed'].includes(String(raw.state)) || !isRevision(raw.revision)) {
      throw new TonightWaitingError('invalid-response');
    }
    event = raw as unknown as TonightWaitingEvent;
  }

  let head: TonightWaitingHead | null = null;
  if (value.head !== null) {
    const raw = value.head;
    if (!isRecord(raw) || !exactKeys(raw, ['entryId', 'revision', 'state'])
        || !isGuid(raw.entryId) || !isRevision(raw.revision)
        || !['waiting', 'claimed'].includes(String(raw.state))) {
      throw new TonightWaitingError('invalid-response');
    }
    head = raw as unknown as TonightWaitingHead;
  }

  return {
    schemaVersion: 1,
    availability: availability as unknown as TonightWaitingAvailabilityView,
    event,
    head,
    count: value.count as number,
  };
}

export function tonightWaitingAvailabilityText(value: TonightWaitingAvailabilityView): string {
  return availabilityCopy[value.reason] ?? 'Dostupnost kufru se nepodařilo ověřit.';
}

export function tonightWaitingErrorText(error: unknown): string {
  if (!(error instanceof TonightWaitingError)) {
    return 'Stav čekacího seznamu se nepodařilo ověřit. Obnovte jej.';
  }
  switch (error.kind) {
    case 'invalid-device': return 'Vybraný kufr není platný.';
    case 'invalid-response': return 'Server vrátil neplatný stav čekacího seznamu. Obnovte jej.';
    case 'unauthenticated': return 'Přihlášení vypršelo. Přihlaste se a obnovte stav.';
    case 'forbidden': return 'Pro tento kufr nemáte oprávnění čekací seznam spravovat.';
    case 'not-found': return 'Čekací seznam už není dostupný. Obnovte stav.';
    case 'conflict': return 'Stav se změnil. Obnovte jej před dalším zásahem.';
    case 'busy': return 'Server je dočasně zaneprázdněný. Obnovte stav.';
    case 'request-failed': return 'Požadavek se nepodařilo ověřit. Obnovte stav.';
    case 'pending-command': return 'Předchozí požadavek čeká na ověření. Nejprve obnovte stav.';
    case 'metadata-invalid': return 'Uložený požadavek nelze ověřit. Obnovte stav a vyžádejte nový zásah.';
  }
}

async function readJson(response: Response, signal?: AbortSignal): Promise<unknown> {
  const contentType = response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase();
  if (contentType !== 'application/json' && contentType !== 'application/problem+json') {
    throw new TonightWaitingError('invalid-response');
  }
  if (!response.body) {
    const text = await response.text();
    if (new TextEncoder().encode(text).length > maximumResponseBytes) throw new TonightWaitingError('invalid-response');
    return JSON.parse(text) as unknown;
  }

  const reader = response.body.getReader();
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      if (signal?.aborted) throw new DOMException('Cancelled', 'AbortError');
      const { done, value } = await reader.read();
      if (done) break;
      size += value.byteLength;
      if (size > maximumResponseBytes) throw new TonightWaitingError('invalid-response');
      chunks.push(value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.byteLength; }
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    return JSON.parse(text) as unknown;
  } catch (error) {
    if (signal?.aborted || (error instanceof DOMException && error.name === 'AbortError')) throw error;
    if (error instanceof TonightWaitingError) throw error;
    throw new TonightWaitingError('invalid-response');
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

type CommandBody =
  | { readonly schemaVersion: 1; readonly previousEventId: string | null; readonly expectedRevision: number | null }
  | { readonly schemaVersion: 1; readonly eventId: string; readonly expectedRevision: number; readonly state: 'open' | 'paused' | 'closed' }
  | { readonly schemaVersion: 1; readonly eventId: string; readonly entryId: string; readonly expectedRevision: number };

function fingerprint(kind: TonightWaitingCommandKind, body: CommandBody): string {
  return JSON.stringify([kind, body]);
}

function commandPath(deviceId: string, kind: TonightWaitingCommandKind): string {
  const root = tonightWaitingPath(deviceId);
  return kind === 'open' ? `${root}/open` : kind === 'state' ? `${root}/state` : `${root}/skip`;
}

function validateBody(kind: unknown, body: unknown): body is CommandBody {
  if (!isRecord(body) || body.schemaVersion !== 1) return false;
  if (kind === 'open') {
    return exactKeys(body, ['schemaVersion', 'previousEventId', 'expectedRevision'])
      && (body.previousEventId === null && body.expectedRevision === null
        || isGuid(body.previousEventId) && isRevision(body.expectedRevision));
  }
  if (kind === 'state') {
    return exactKeys(body, ['schemaVersion', 'eventId', 'expectedRevision', 'state'])
      && isGuid(body.eventId) && isRevision(body.expectedRevision)
      && ['open', 'paused', 'closed'].includes(String(body.state));
  }
  if (kind === 'skip') {
    return exactKeys(body, ['schemaVersion', 'eventId', 'entryId', 'expectedRevision'])
      && isGuid(body.eventId) && isGuid(body.entryId) && isRevision(body.expectedRevision);
  }
  return false;
}

function parseMetadata(value: string): TonightWaitingCommandMetadata {
  if (value.length > maximumMetadataBytes) throw new TonightWaitingError('metadata-invalid');
  let parsed: unknown;
  try { parsed = JSON.parse(value); } catch { throw new TonightWaitingError('metadata-invalid'); }
  if (!isRecord(parsed) || !exactKeys(parsed, ['schemaVersion', 'requestId', 'kind', 'fingerprint', 'body'])
      || parsed.schemaVersion !== 1 || !isGuid(parsed.requestId)
      || !['open', 'state', 'skip'].includes(String(parsed.kind))
      || typeof parsed.fingerprint !== 'string' || !validateBody(parsed.kind, parsed.body)) {
    throw new TonightWaitingError('metadata-invalid');
  }
  if (parsed.fingerprint !== fingerprint(parsed.kind as TonightWaitingCommandKind, parsed.body)) {
    throw new TonightWaitingError('metadata-invalid');
  }
  return {
    requestId: parsed.requestId,
    kind: parsed.kind as TonightWaitingCommandKind,
    fingerprint: parsed.fingerprint,
    body: parsed.body,
  };
}

function browserStorage(): TonightWaitingStorage | undefined {
  try { return typeof window === 'undefined' ? undefined : window.localStorage; } catch { return undefined; }
}

export class TonightWaitingClient {
  private readonly storage: TonightWaitingStorage | undefined;
  private readonly createRequestId: () => string;
  private readonly memoryPending = new Map<string, TonightWaitingCommandMetadata>();
  private readonly inFlight = new Map<string, { fingerprint: string; promise: Promise<TonightWaitingSnapshot> }>();

  constructor(private readonly options: TonightWaitingClientOptions) {
    this.storage = options.storage ?? browserStorage();
    this.createRequestId = options.createRequestId ?? secureUuid;
  }

  getPendingCommand(deviceId: string): TonightWaitingCommandMetadata | null {
    const key = this.storageKey(deviceId);
    const memory = this.memoryPending.get(key);
    if (memory) return memory;
    try {
      const stored = this.storage?.getItem(key);
      if (!stored) return null;
      const parsed = parseMetadata(stored);
      this.memoryPending.set(key, parsed);
      return parsed;
    } catch {
      throw new TonightWaitingError('metadata-invalid');
    }
  }

  getSnapshot(deviceId: string, signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const path = tonightWaitingPath(deviceId);
    return this.sendRequest(path, { method: 'GET', signal }, deviceId, signal);
  }

  open(deviceId: string, previousEventId: string | null, expectedRevision: number | null,
    signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const body: CommandBody = { schemaVersion: 1, previousEventId, expectedRevision };
    if (!validateBody('open', body)) throw new TonightWaitingError('invalid-response');
    return this.execute(deviceId, 'open', body, signal);
  }

  changeState(deviceId: string, eventId: string, expectedRevision: number,
    state: 'open' | 'paused' | 'closed', signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const body: CommandBody = { schemaVersion: 1, eventId, expectedRevision, state };
    if (!validateBody('state', body)) throw new TonightWaitingError('invalid-response');
    return this.execute(deviceId, 'state', body, signal);
  }

  skipHead(deviceId: string, eventId: string, entryId: string, expectedRevision: number,
    signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const body: CommandBody = { schemaVersion: 1, eventId, entryId, expectedRevision };
    if (!validateBody('skip', body)) throw new TonightWaitingError('invalid-response');
    return this.execute(deviceId, 'skip', body, signal);
  }

  retryPending(deviceId: string, signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const pending = this.getPendingCommand(deviceId);
    if (!pending) return Promise.reject(new TonightWaitingError('pending-command'));
    return this.dispatch(deviceId, pending, signal);
  }

  /** Only a fresh GET projection can resolve an uncertain command. */
  reconcile(deviceId: string, snapshot: TonightWaitingSnapshot): 'none' | 'applied' | 'pending' {
    const pending = this.getPendingCommand(deviceId);
    if (!pending) return 'none';
    let applied = false;
    if (pending.kind === 'open') {
      const body = pending.body as Extract<CommandBody, { previousEventId: string | null }>;
      applied = !!snapshot.event && snapshot.event.state === 'open'
        && (body.previousEventId === null || snapshot.event.id !== body.previousEventId);
    } else if (pending.kind === 'state') {
      const body = pending.body as Extract<CommandBody, { state: 'open' | 'paused' | 'closed' }>;
      const current = snapshot.event;
      applied = !current || current.id !== body.eventId
        || current.state === body.state && current.revision > body.expectedRevision;
    } else {
      const body = pending.body as Extract<CommandBody, { entryId: string }>;
      applied = snapshot.event?.id !== body.eventId || snapshot.head?.entryId !== body.entryId;
    }
    if (!applied) return 'pending';
    this.clearPending(deviceId, pending);
    return 'applied';
  }

  private execute(deviceId: string, kind: TonightWaitingCommandKind, body: CommandBody,
    signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const key = this.storageKey(deviceId);
    const nextFingerprint = fingerprint(kind, body);
    const inFlight = this.inFlight.get(key);
    if (inFlight) {
      if (inFlight.fingerprint !== nextFingerprint) return Promise.reject(new TonightWaitingError('pending-command'));
      return inFlight.promise;
    }
    const existing = this.getPendingCommand(deviceId);
    if (existing && (existing.kind !== kind || existing.fingerprint !== nextFingerprint)) {
      return Promise.reject(new TonightWaitingError('pending-command'));
    }
    const pending = existing ?? this.createPending(kind, body);
    if (!existing) this.storePending(deviceId, pending);
    return this.dispatch(deviceId, pending, signal);
  }

  private dispatch(deviceId: string, pending: TonightWaitingCommandMetadata,
    signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    const key = this.storageKey(deviceId);
    const existing = this.inFlight.get(key);
    if (existing) {
      if (existing.fingerprint !== pending.fingerprint) return Promise.reject(new TonightWaitingError('pending-command'));
      return existing.promise;
    }
    const operation = this.sendRequest(commandPath(deviceId, pending.kind), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ ...pending.body, requestId: pending.requestId }),
      signal,
    }, deviceId, signal);
    this.inFlight.set(key, { fingerprint: pending.fingerprint, promise: operation });
    void operation.finally(() => {
      if (this.inFlight.get(key)?.promise === operation) this.inFlight.delete(key);
    }).catch(() => undefined);
    return operation;
  }

  private async sendRequest(path: string, init: RequestInit, deviceId: string,
    signal?: AbortSignal): Promise<TonightWaitingSnapshot> {
    try {
      const response = await this.options.transport(path, init);
      if (signal?.aborted) throw new DOMException('Cancelled', 'AbortError');
      if (response.status !== 200) throw this.httpError(response.status);
      return parseTonightWaitingSnapshot(await readJson(response, signal), deviceId);
    } catch (error) {
      if (signal?.aborted || error instanceof DOMException && error.name === 'AbortError') throw error;
      if (error instanceof TonightWaitingError) throw error;
      throw new TonightWaitingError('request-failed');
    }
  }

  private createPending(kind: TonightWaitingCommandKind, body: CommandBody): TonightWaitingCommandMetadata {
    const requestId = this.createRequestId();
    if (!isGuid(requestId)) throw new TonightWaitingError('metadata-invalid');
    return { requestId, kind, fingerprint: fingerprint(kind, body), body };
  }

  private storePending(deviceId: string, pending: TonightWaitingCommandMetadata): void {
    const key = this.storageKey(deviceId);
    this.memoryPending.set(key, pending);
    try {
      const value = JSON.stringify({ schemaVersion: 1, ...pending });
      if (value.length <= maximumMetadataBytes) this.storage?.setItem(key, value);
    } catch {
      // In-memory retention still preserves the original key for this view.
    }
  }

  private clearPending(deviceId: string, pending: TonightWaitingCommandMetadata): void {
    const key = this.storageKey(deviceId);
    if (this.memoryPending.get(key)?.requestId === pending.requestId) this.memoryPending.delete(key);
    try {
      const current = this.storage?.getItem(key);
      if (current && parseMetadata(current).requestId === pending.requestId) this.storage?.removeItem(key);
    } catch {
      // A corrupt or unavailable value is not trusted or surfaced to the user.
    }
  }

  private storageKey(deviceId: string): string {
    if (!isTonightWaitingDeviceId(deviceId)) throw new TonightWaitingError('invalid-device');
    return `${storagePrefix}.${encodeURIComponent(this.options.accountId)}.${encodeURIComponent(deviceId)}`;
  }

  private httpError(status: number): TonightWaitingError {
    if (status === 401) return new TonightWaitingError('unauthenticated', status);
    if (status === 403) return new TonightWaitingError('forbidden', status);
    if (status === 404) return new TonightWaitingError('not-found', status);
    if (status === 409 || status === 429) return new TonightWaitingError('conflict', status);
    if (status === 503) return new TonightWaitingError('busy', status);
    return new TonightWaitingError('request-failed', status);
  }
}

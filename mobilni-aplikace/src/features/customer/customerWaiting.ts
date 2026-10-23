import { secureUuid } from '../../application/identifiers.mjs';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { accountTransport } from '../identity/session';

export type CustomerWaitingAvailability = 'available' | 'busy' | 'recovering' | 'paused' | 'offline' | 'unknown';
export type CustomerWaitingEntryState = 'waiting' | 'claimed' | 'accepted';
export type CustomerWaitingCommandKind = 'join' | 'leave' | 'claim';
export type CustomerWaitingMetadataStatus = 'saved' | 'memory-only';

export interface CustomerWaitingAvailabilityView {
  readonly state: CustomerWaitingAvailability;
  readonly reason: string;
  readonly fresh: boolean;
}

export interface CustomerWaitingEvent {
  readonly id: string;
  readonly deviceId: string;
  readonly eveningId: string;
  readonly state: 'open' | 'paused' | 'closed';
  readonly revision: number;
}

export interface CustomerWaitingClaim {
  readonly id: string;
  readonly expiresAt: number;
}

export interface CustomerWaitingEntry {
  readonly id: string;
  readonly profileId: string;
  readonly sequence: number;
  readonly revision: number;
  readonly state: CustomerWaitingEntryState;
  readonly claim: CustomerWaitingClaim | null;
}

export interface CustomerWaitingSnapshot {
  readonly schemaVersion: 1;
  readonly availability: CustomerWaitingAvailabilityView;
  readonly event: CustomerWaitingEvent | null;
  readonly entry: CustomerWaitingEntry | null;
  readonly position: number | null;
  readonly count: number;
}

export interface CustomerWaitingProximity {
  readonly bootId: string;
  readonly challenge: string;
}

export interface CustomerWaitingStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
  removeItem?(key: string): void;
}

export type CustomerWaitingTransport = (path: string, init?: RequestInit) => Promise<Response>;

export type CustomerWaitingErrorKind =
  | 'invalid-device'
  | 'invalid-response'
  | 'invalid-proximity'
  | 'unauthenticated'
  | 'not-found'
  | 'conflict'
  | 'capacity'
  | 'busy'
  | 'offline'
  | 'pending-command'
  | 'metadata-invalid'
  | 'request-failed';

export class CustomerWaitingError extends Error {
  constructor(readonly kind: CustomerWaitingErrorKind, readonly status?: number) {
    super(kind);
    this.name = 'CustomerWaitingError';
  }
}

export interface CustomerWaitingCommandMetadata {
  readonly requestId: string;
  readonly kind: CustomerWaitingCommandKind;
  readonly fingerprint: string;
}

export interface CustomerWaitingClientOptions {
  readonly accountId: string;
  readonly transport?: CustomerWaitingTransport;
  readonly storage?: CustomerWaitingStorage;
  readonly createRequestId?: () => string;
}

const apiRoot = '/app/v1/customer/waiting';
const storagePrefix = 'dildovander.customer-waiting.v1';
const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const devicePattern = /^[A-Za-z0-9_.-]{1,128}$/;
const availabilityStates: readonly CustomerWaitingAvailability[] = [
  'available', 'busy', 'recovering', 'paused', 'offline', 'unknown',
];
const availabilityReasons = new Set([
  'lease_unresolved', 'lease_occupied', 'event_closed', 'customer_mode_disabled', 'admission_paused',
  'runtime_identity_unverified', 'runtime_observation_stale', 'runtime_ready', 'runtime_offline',
  'runtime_unknown', 'lease_unknown',
]);
const maxResponseBytes = 64 * 1024;
const maxMetadataBytes = 1024;

function isRecord(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === 'object' && !Array.isArray(value);
}

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.length && expected.every(key => Object.prototype.hasOwnProperty.call(value, key));
}

function isGuid(value: unknown): value is string {
  return typeof value === 'string' && guidPattern.test(value) && value.toLowerCase() !== '00000000-0000-0000-0000-000000000000';
}

function isProfileId(value: unknown): value is string {
  return isGuid(value) || typeof value === 'string' && /^[0-9a-f]{32}$/i.test(value) && !/^0{32}$/i.test(value);
}

export function isCustomerWaitingDeviceId(value: unknown): value is string {
  return typeof value === 'string' && devicePattern.test(value) && value !== '.' && value !== '..';
}

function isRevision(value: unknown): value is number {
  return Number.isSafeInteger(value) && (value as number) >= 0;
}

function parseSnapshot(value: unknown, deviceId: string): CustomerWaitingSnapshot {
  if (!isRecord(value) || !exactKeys(value, ['schemaVersion', 'availability', 'event', 'entry', 'position', 'count']) || value.schemaVersion !== 1) {
    throw new CustomerWaitingError('invalid-response');
  }
  const availability = value.availability;
  if (!isRecord(availability)
      || !exactKeys(availability, ['state', 'reason', 'fresh'])
      || !availabilityStates.includes(availability.state as CustomerWaitingAvailability)
      || typeof availability.reason !== 'string'
      || !availabilityReasons.has(availability.reason)
      || typeof availability.fresh !== 'boolean') {
    throw new CustomerWaitingError('invalid-response');
  }

  let event: CustomerWaitingEvent | null = null;
  if (value.event !== null) {
    const raw = value.event;
    if (!isRecord(raw)
        || !exactKeys(raw, ['id', 'deviceId', 'eveningId', 'state', 'revision'])
        || !isGuid(raw.id)
        || raw.deviceId !== deviceId
        || !isGuid(raw.eveningId)
        || !['open', 'paused', 'closed'].includes(String(raw.state))
        || !isRevision(raw.revision)) {
      throw new CustomerWaitingError('invalid-response');
    }
    event = raw as unknown as CustomerWaitingEvent;
  }

  let entry: CustomerWaitingEntry | null = null;
  if (value.entry !== null) {
    const raw = value.entry;
    if (!isRecord(raw)
        || !exactKeys(raw, ['id', 'profileId', 'sequence', 'revision', 'state', 'claim'])
        || !isGuid(raw.id)
        || !isProfileId(raw.profileId)
        || !Number.isSafeInteger(raw.sequence) || (raw.sequence as number) < 1
        || !isRevision(raw.revision)
        || !['waiting', 'claimed', 'accepted'].includes(String(raw.state))) {
      throw new CustomerWaitingError('invalid-response');
    }
    let claim: CustomerWaitingClaim | null = null;
    if (raw.claim !== null) {
      if (!isRecord(raw.claim)
          || !exactKeys(raw.claim, ['id', 'expiresAt'])
          || !isGuid(raw.claim.id)
          || !Number.isSafeInteger(raw.claim.expiresAt)
          || (raw.claim.expiresAt as number) <= 0) {
        throw new CustomerWaitingError('invalid-response');
      }
      claim = raw.claim as unknown as CustomerWaitingClaim;
    }
    entry = { ...raw, claim } as unknown as CustomerWaitingEntry;
  }

  const position = value.position;
  const count = value.count;
  if (!(position === null || (Number.isSafeInteger(position) && (position as number) >= 1))
      || !Number.isSafeInteger(count) || (count as number) < 0
      || (entry !== null && event === null)
      || (entry?.state === 'accepted' && position !== null)
      || (entry && entry.state !== 'accepted' && position === null)) {
    throw new CustomerWaitingError('invalid-response');
  }

  return {
    schemaVersion: 1,
    availability: availability as unknown as CustomerWaitingAvailabilityView,
    event,
    entry,
    position: position as number | null,
    count: count as number,
  };
}

async function readJson(response: Response, signal?: AbortSignal): Promise<unknown> {
  if (response.headers.get('Content-Type')?.split(';', 1)[0].trim().toLowerCase() !== 'application/json') {
    await response.body?.cancel().catch(() => undefined);
    throw new CustomerWaitingError('invalid-response');
  }
  const reader = response.body?.getReader();
  if (!reader) throw new CustomerWaitingError('invalid-response');
  const chunks: Uint8Array[] = [];
  let size = 0;
  try {
    while (true) {
      if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
      const part = await reader.read();
      if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
      if (part.done) break;
      size += part.value.length;
      if (size > maxResponseBytes) throw new CustomerWaitingError('invalid-response');
      chunks.push(part.value);
    }
    const bytes = new Uint8Array(size);
    let offset = 0;
    for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
    if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
    return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch (error) {
    if (signal?.aborted || (error instanceof DOMException && error.name === 'AbortError')) throw error;
    if (error instanceof CustomerWaitingError) throw error;
    throw new CustomerWaitingError('invalid-response');
  } finally {
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

function browserStorage(): CustomerWaitingStorage | undefined {
  try {
    return typeof window === 'undefined' ? undefined : window.localStorage;
  } catch {
    return undefined;
  }
}

function storageKey(accountId: string, deviceId: string): string {
  return `${storagePrefix}.${encodeURIComponent(accountId)}.${encodeURIComponent(deviceId)}`;
}

function parseMetadata(value: string): CustomerWaitingCommandMetadata {
  if (value.length > maxMetadataBytes) throw new CustomerWaitingError('metadata-invalid');
  let parsed: unknown;
  try { parsed = JSON.parse(value); } catch { throw new CustomerWaitingError('metadata-invalid'); }
  if (!isRecord(parsed)
      || !exactKeys(parsed, ['schemaVersion', 'requestId', 'kind', 'fingerprint'])
      || parsed.schemaVersion !== 1
      || !isGuid(parsed.requestId)
      || !['join', 'leave', 'claim'].includes(String(parsed.kind))
      || typeof parsed.fingerprint !== 'string'
      || parsed.fingerprint.length < 1 || parsed.fingerprint.length > 512) {
    throw new CustomerWaitingError('metadata-invalid');
  }
  let fingerprint: unknown;
  try { fingerprint = JSON.parse(parsed.fingerprint); } catch { throw new CustomerWaitingError('metadata-invalid'); }
  if (!Array.isArray(fingerprint)
      || fingerprint[0] !== parsed.kind
      || !fingerprint.slice(1).every(value => typeof value === 'string' || Number.isSafeInteger(value))) {
    throw new CustomerWaitingError('metadata-invalid');
  }
  const expectedValueCount = parsed.kind === 'join' ? 2 : 3;
  const identifiers = fingerprint.slice(1).filter((value): value is string => typeof value === 'string');
  const validIdentifiers = parsed.kind === 'join'
    ? isGuid(identifiers[0]) && isProfileId(identifiers[1])
    : identifiers.every(isGuid);
  if (fingerprint.length !== expectedValueCount + 1 || !validIdentifiers) {
    throw new CustomerWaitingError('metadata-invalid');
  }
  return { requestId: parsed.requestId, kind: parsed.kind as CustomerWaitingCommandKind, fingerprint: parsed.fingerprint };
}

function commandFingerprint(kind: CustomerWaitingCommandKind, values: readonly (string | number)[]): string {
  return JSON.stringify([kind, ...values]);
}

function requireProximity(proximity: CustomerWaitingProximity): void {
  if (!isGuid(proximity.bootId)
      || typeof proximity.challenge !== 'string'
      || !/^[\x21-\x7e]{16,128}$/.test(proximity.challenge)) {
    throw new CustomerWaitingError('invalid-proximity');
  }
}

function httpError(status: number): CustomerWaitingError {
  if (status === 401) return new CustomerWaitingError('unauthenticated', status);
  if (status === 404) return new CustomerWaitingError('not-found', status);
  if (status === 409) return new CustomerWaitingError('conflict', status);
  if (status === 429) return new CustomerWaitingError('capacity', status);
  if (status === 503) return new CustomerWaitingError('busy', status);
  return new CustomerWaitingError('request-failed', status);
}

export function customerWaitingErrorCopyKey(error: unknown): CustomerCopyKey {
  if (!(error instanceof CustomerWaitingError)) return 'waiting.error.generic';
  switch (error.kind) {
    case 'invalid-device': return 'waiting.error.invalid-device';
    case 'invalid-response': return 'waiting.error.invalid-response';
    case 'invalid-proximity': return 'waiting.error.invalid-proximity';
    case 'unauthenticated': return 'waiting.error.unauthenticated';
    case 'not-found': return 'waiting.error.not-found';
    case 'conflict': return 'waiting.error.conflict';
    case 'capacity': return 'waiting.error.capacity';
    case 'busy': return 'waiting.error.busy';
    case 'offline': return 'waiting.error.offline';
    case 'pending-command': return 'waiting.error.pending-command';
    case 'metadata-invalid': return 'waiting.error.metadata-invalid';
    case 'request-failed': return 'waiting.error.request-failed';
  }
}

export function customerWaitingErrorText(error: unknown): string {
  return customerCopy.text(customerWaitingErrorCopyKey(error));
}

export function customerWaitingAvailabilityCopyKey(availability: CustomerWaitingAvailabilityView): CustomerCopyKey {
  const reasonKeys: Readonly<Record<string, CustomerCopyKey>> = {
    lease_unresolved: 'waiting.availability.lease-unresolved',
    lease_occupied: 'waiting.availability.lease-occupied',
    event_closed: 'waiting.availability.event-closed',
    customer_mode_disabled: 'waiting.availability.mode-disabled',
    admission_paused: 'waiting.availability.paused',
    runtime_identity_unverified: 'waiting.availability.identity-unverified',
    runtime_observation_stale: 'waiting.availability.stale',
    runtime_ready: 'waiting.availability.ready',
    runtime_offline: 'waiting.availability.offline',
    runtime_unknown: 'waiting.availability.checking',
    lease_unknown: 'waiting.availability.unknown',
  };
  return reasonKeys[availability.reason] ?? 'waiting.availability.unknown';
}

export function customerWaitingAvailabilityText(availability: CustomerWaitingAvailabilityView): string {
  return customerCopy.text(customerWaitingAvailabilityCopyKey(availability));
}

export function parseCustomerWaitingSnapshot(value: unknown, deviceId: string): CustomerWaitingSnapshot {
  if (!isCustomerWaitingDeviceId(deviceId)) throw new CustomerWaitingError('invalid-device');
  return parseSnapshot(value, deviceId);
}

export class CustomerWaitingClient {
  private readonly transport: CustomerWaitingTransport;
  private readonly storage: CustomerWaitingStorage | undefined;
  private readonly createRequestId: () => string;
  private readonly memoryPending = new Map<string, CustomerWaitingCommandMetadata>();
  private readonly inFlight = new Map<string, { fingerprint: string; promise: Promise<CustomerWaitingSnapshot> }>();
  private durable = false;

  constructor(private readonly options: CustomerWaitingClientOptions) {
    if (!options.accountId || options.accountId.length > 128) throw new CustomerWaitingError('unauthenticated');
    this.transport = options.transport ?? accountTransport;
    this.storage = options.storage ?? browserStorage();
    this.createRequestId = options.createRequestId ?? secureUuid;
  }

  get metadataStatus(): CustomerWaitingMetadataStatus {
    return this.durable ? 'saved' : 'memory-only';
  }

  getPendingCommand(deviceId: string): CustomerWaitingCommandMetadata | null {
    const key = this.key(deviceId);
    const memory = this.memoryPending.get(key);
    if (memory) return memory;
    if (!this.storage) return null;
    try {
      const raw = this.storage.getItem(key);
      this.durable = true;
      if (!raw) return null;
      const parsed = parseMetadata(raw);
      this.memoryPending.set(key, parsed);
      return parsed;
    } catch (error) {
      if (error instanceof CustomerWaitingError) throw error;
      this.durable = false;
      return this.memoryPending.get(key) ?? null;
    }
  }

  async getSnapshot(deviceId: string, signal?: AbortSignal): Promise<CustomerWaitingSnapshot> {
    const path = this.path(deviceId);
    const response = await this.transport(path, { method: 'GET', signal });
    if (response.status !== 200) throw httpError(response.status);
    return parseCustomerWaitingSnapshot(await readJson(response, signal), deviceId);
  }

  join(deviceId: string, eventId: string, profileId: string, proximity: CustomerWaitingProximity, signal?: AbortSignal): Promise<CustomerWaitingSnapshot> {
    if (!isGuid(eventId) || !isProfileId(profileId)) return Promise.reject(new CustomerWaitingError('invalid-response'));
    requireProximity(proximity);
    return this.send(deviceId, 'join', [eventId, profileId], {
      schemaVersion: 1,
      eventId,
      profileId,
      bootId: proximity.bootId,
      challenge: proximity.challenge,
    }, signal);
  }

  leave(deviceId: string, eventId: string, entryId: string, expectedRevision: number, signal?: AbortSignal): Promise<CustomerWaitingSnapshot> {
    if (!isGuid(eventId) || !isGuid(entryId) || !isRevision(expectedRevision)) return Promise.reject(new CustomerWaitingError('invalid-response'));
    return this.send(deviceId, 'leave', [eventId, entryId, expectedRevision], {
      schemaVersion: 1,
      eventId,
      entryId,
      expectedRevision,
    }, signal);
  }

  claim(deviceId: string, eventId: string, entryId: string, expectedRevision: number, proximity: CustomerWaitingProximity, signal?: AbortSignal): Promise<CustomerWaitingSnapshot> {
    if (!isGuid(eventId) || !isGuid(entryId) || !isRevision(expectedRevision)) return Promise.reject(new CustomerWaitingError('invalid-response'));
    requireProximity(proximity);
    return this.send(deviceId, 'claim', [eventId, entryId, expectedRevision], {
      schemaVersion: 1,
      eventId,
      entryId,
      expectedRevision,
      bootId: proximity.bootId,
      challenge: proximity.challenge,
    }, signal);
  }

  /** Clear an uncertain command only after a fresh authoritative GET resolves its outcome. */
  reconcile(deviceId: string, snapshot: CustomerWaitingSnapshot): 'none' | 'applied' | 'pending' {
    const key = this.key(deviceId);
    const pending = this.getPendingCommand(deviceId);
    if (!pending) return 'none';
    const fingerprint = JSON.parse(pending.fingerprint) as [CustomerWaitingCommandKind, ...Array<string | number>];
    const [kind, eventId, subject, revision] = fingerprint;
    let applied = false;
    if (kind === 'join') {
      applied = snapshot.entry?.profileId === subject && snapshot.event?.id === eventId
        || snapshot.event?.id !== eventId || snapshot.event?.state === 'closed';
    } else if (kind === 'leave') {
      applied = snapshot.event?.id !== eventId || snapshot.event?.state === 'closed'
        || snapshot.entry?.id !== subject;
    } else {
      applied = snapshot.event?.id !== eventId || snapshot.event?.state === 'closed'
        || snapshot.entry?.id !== subject
        || snapshot.entry.revision > Number(revision) && snapshot.entry.claim === null;
      if (snapshot.entry?.id === subject && snapshot.entry.claim) applied = true;
    }
    if (!applied) return 'pending';
    this.clearPending(deviceId, pending);
    return 'applied';
  }

  private async send(
    deviceId: string,
    kind: CustomerWaitingCommandKind,
    values: readonly (string | number)[],
    body: Readonly<Record<string, unknown>>,
    signal?: AbortSignal,
  ): Promise<CustomerWaitingSnapshot> {
    const root = this.path(deviceId);
    const fingerprint = commandFingerprint(kind, values);
    const requestKey = `${this.options.accountId}:${deviceId}`;
    const existingRequest = this.inFlight.get(requestKey);
    if (existingRequest) {
      if (existingRequest.fingerprint !== fingerprint) throw new CustomerWaitingError('pending-command');
      return existingRequest.promise;
    }
    const pending = this.getPendingCommand(deviceId);
    if (pending && (pending.kind !== kind || pending.fingerprint !== fingerprint)) {
      throw new CustomerWaitingError('pending-command');
    }
    const metadata = pending ?? { requestId: this.newRequestId(), kind, fingerprint };
    if (!pending) this.storePending(deviceId, metadata);
    const payload = JSON.stringify({ ...body, requestId: metadata.requestId });
    const operation = (async () => {
      const response = await this.transport(`${root}/${kind}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: payload,
        signal,
      });
      if (response.status !== 200) throw httpError(response.status);
      return parseCustomerWaitingSnapshot(await readJson(response, signal), deviceId);
    })();
    this.inFlight.set(requestKey, { fingerprint, promise: operation });
    try {
      return await operation;
    } finally {
      if (this.inFlight.get(requestKey)?.promise === operation) this.inFlight.delete(requestKey);
    }
  }

  private path(deviceId: string): string {
    if (!isCustomerWaitingDeviceId(deviceId)) throw new CustomerWaitingError('invalid-device');
    return `${apiRoot}/${encodeURIComponent(deviceId)}`;
  }

  private key(deviceId: string): string {
    this.path(deviceId);
    return storageKey(this.options.accountId, deviceId);
  }

  private newRequestId(): string {
    const id = this.createRequestId();
    if (!isGuid(id)) throw new CustomerWaitingError('metadata-invalid');
    return id;
  }

  private storePending(deviceId: string, pending: CustomerWaitingCommandMetadata): void {
    const key = this.key(deviceId);
    this.memoryPending.set(key, pending);
    if (!this.storage) {
      this.durable = false;
      return;
    }
    try {
      this.storage.setItem(key, JSON.stringify({ schemaVersion: 1, ...pending }));
      this.durable = true;
    } catch {
      this.durable = false;
    }
  }

  private clearPending(deviceId: string, pending: CustomerWaitingCommandMetadata): void {
    const key = this.key(deviceId);
    const current = this.getPendingCommand(deviceId);
    if (current?.requestId !== pending.requestId) return;
    this.memoryPending.delete(key);
    try { this.storage?.removeItem?.(key); } catch { this.durable = false; }
  }
}

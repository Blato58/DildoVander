import { describe, expect, it, vi } from 'vitest';
import {
  CustomerWaitingClient,
  CustomerWaitingError,
  customerWaitingErrorText,
  isCustomerWaitingDeviceId,
  parseCustomerWaitingSnapshot,
  type CustomerWaitingSnapshot,
  type CustomerWaitingStorage,
} from './customerWaiting';

const deviceId = 'case-7';
const accountId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const eventId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const eveningId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const profileId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const entryId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
const claimId = 'ffffffff-ffff-4fff-8fff-ffffffffffff';
const requestId = '11111111-1111-4111-8111-111111111111';
const bootId = '22222222-2222-4222-8222-222222222222';

function snapshot(overrides: Partial<CustomerWaitingSnapshot> = {}): CustomerWaitingSnapshot {
  return {
    schemaVersion: 1,
    availability: { state: 'available', reason: 'runtime_ready', fresh: true },
    event: { id: eventId, deviceId, eveningId, state: 'open', revision: 3 },
    entry: null,
    position: null,
    count: 0,
    ...overrides,
  };
}

function waitingSnapshot(overrides: Partial<CustomerWaitingSnapshot> = {}): CustomerWaitingSnapshot {
  return snapshot({
    entry: { id: entryId, profileId, sequence: 2, revision: 4, state: 'waiting', claim: null },
    position: 1,
    count: 2,
    ...overrides,
  });
}

function response(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json; charset=utf-8' } });
}

function memoryStorage(): CustomerWaitingStorage & { values: Map<string, string> } {
  const values = new Map<string, string>();
  return {
    values,
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => { values.set(key, value); },
    removeItem: key => { values.delete(key); },
  };
}

function client(transport: (path: string, init?: RequestInit) => Promise<Response>, storage: CustomerWaitingStorage = memoryStorage(), makeId = () => requestId) {
  return new CustomerWaitingClient({ accountId, transport, storage, createRequestId: makeId });
}

describe('customer waiting client', () => {
  it('parses the server contract and rejects mismatched, unknown, or path-shaped identifiers', () => {
    expect(parseCustomerWaitingSnapshot(snapshot(), deviceId).availability.state).toBe('available');
    expect(parseCustomerWaitingSnapshot(waitingSnapshot({
      event: { id: eventId, deviceId, eveningId, state: 'paused', revision: 4 },
      availability: { state: 'paused', reason: 'admission_paused', fresh: true },
    }), deviceId).event?.state).toBe('paused');
    expect(parseCustomerWaitingSnapshot(waitingSnapshot({
      entry: { id: entryId, profileId: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', sequence: 2, revision: 4, state: 'waiting', claim: null },
    }), deviceId).entry?.profileId).toBe('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
    expect(isCustomerWaitingDeviceId('unit_2.A-')).toBe(true);
    expect(isCustomerWaitingDeviceId('.')).toBe(false);
    expect(isCustomerWaitingDeviceId('..')).toBe(false);
    expect(isCustomerWaitingDeviceId('a%2f..')).toBe(false);
    expect(() => parseCustomerWaitingSnapshot(snapshot(), 'another-device')).toThrow(CustomerWaitingError);
    expect(() => parseCustomerWaitingSnapshot({ ...snapshot(), schemaVersion: 2 }, deviceId)).toThrow(CustomerWaitingError);
    expect(() => parseCustomerWaitingSnapshot({ ...snapshot(), availability: { state: 'future', reason: 'secret', fresh: false } }, deviceId)).toThrow(CustomerWaitingError);
  });

  it('uses the bounded account transport endpoint and rejects oversized status JSON', async () => {
    const transport = vi.fn(async (_path: string, _init?: RequestInit) => response(snapshot()));
    const service = client(transport);
    await service.getSnapshot(deviceId);
    expect(transport.mock.calls[0][0]).toBe('/app/v1/customer/waiting/case-7');
    expect(transport.mock.calls[0][1]).toMatchObject({ method: 'GET' });

    const tooLarge = client(async () => new Response(' '.repeat(64 * 1024 + 1), {
      status: 200,
      headers: { 'Content-Type': 'application/json' },
    }));
    await expect(tooLarge.getSnapshot(deviceId)).rejects.toMatchObject({ kind: 'invalid-response' });
  });

  it('reuses an uncertain join key only for the same logical command and excludes proximity from metadata', async () => {
    const storage = memoryStorage();
    let postCount = 0;
    const transport = vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'POST' && postCount++ === 0) throw new TypeError('network failure with secret text');
      return response(waitingSnapshot());
    });
    const service = client(transport, storage);
    await expect(service.join(deviceId, eventId, profileId, { bootId, challenge: 'first-secret-challenge-012345' })).rejects.toThrow();
    const stored = [...storage.values.values()][0];
    expect(stored).toContain(requestId);
    expect(stored).not.toContain('first-secret-challenge');
    expect(stored).not.toContain(bootId);

    await service.join(deviceId, eventId, profileId, { bootId, challenge: 'fresh-secret-challenge-987654' });
    const firstBody = JSON.parse(String(transport.mock.calls[0][1]?.body));
    const secondBody = JSON.parse(String(transport.mock.calls[1][1]?.body));
    expect(firstBody.requestId).toBe(requestId);
    expect(secondBody.requestId).toBe(requestId);
    expect(secondBody.challenge).toBe('fresh-secret-challenge-987654');
  });

  it('does not rotate an unresolved key for a different action until fresh GET reconciliation', async () => {
    const storage = memoryStorage();
    const transport = vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'POST') throw new TypeError('offline');
      return response(waitingSnapshot());
    });
    const service = client(transport, storage);
    await expect(service.join(deviceId, eventId, profileId, { bootId, challenge: 'fresh-secret-challenge-123456' })).rejects.toThrow();
    await expect(service.leave(deviceId, eventId, entryId, 4)).rejects.toMatchObject({ kind: 'pending-command' });
    expect(transport).toHaveBeenCalledTimes(1);

    const reconciled = await service.getSnapshot(deviceId);
    expect(service.reconcile(deviceId, reconciled)).toBe('applied');
    expect(service.getPendingCommand(deviceId)).toBeNull();
    await service.leave(deviceId, eventId, entryId, 4).catch(() => undefined);
    expect(transport).toHaveBeenCalledTimes(3);
  });

  it('retains a claim request after reload without sending any automatic POST', async () => {
    const storage = memoryStorage();
    const transport = vi.fn(async () => response(waitingSnapshot()));
    const first = client(transport, storage);
    await first.claim(deviceId, eventId, entryId, 4, { bootId, challenge: 'fresh-secret-challenge-123456' });
    expect(transport).toHaveBeenCalledTimes(1);

    const reloaded = client(transport, storage, () => '33333333-3333-4333-8333-333333333333');
    expect(reloaded.getPendingCommand(deviceId)?.requestId).toBe(requestId);
    expect(transport).toHaveBeenCalledTimes(1);
  });

  it('clears an uncertain claim only when a fresh snapshot confirms the current claim', async () => {
    const storage = memoryStorage();
    const claimed = waitingSnapshot({
      entry: { id: entryId, profileId, sequence: 2, revision: 5, state: 'claimed', claim: { id: claimId, expiresAt: 2_000_000_000 } },
    });
    const service = client(async () => response(claimed), storage);
    await service.claim(deviceId, eventId, entryId, 4, { bootId, challenge: 'fresh-secret-challenge-123456' });
    expect(service.reconcile(deviceId, waitingSnapshot())).toBe('pending');
    expect(service.reconcile(deviceId, claimed)).toBe('applied');
    expect(service.getPendingCommand(deviceId)).toBeNull();
  });

  it('isolates keys by account and maps raw transport errors to safe user-facing guidance', async () => {
    const storage = memoryStorage();
    const first = client(async () => { throw new Error('raw bearer=super-secret'); }, storage);
    await expect(first.join(deviceId, eventId, profileId, { bootId, challenge: 'fresh-secret-challenge-123456' })).rejects.toThrow('raw bearer=super-secret');
    const otherAccount = new CustomerWaitingClient({
      accountId: '99999999-9999-4999-8999-999999999999',
      transport: async () => response(snapshot()),
      storage,
      createRequestId: () => '44444444-4444-4444-8444-444444444444',
    });
    expect(otherAccount.getPendingCommand(deviceId)).toBeNull();
    expect(customerWaitingErrorText(new Error('raw bearer=super-secret'))).not.toContain('super-secret');
    expect(customerWaitingErrorText(new CustomerWaitingError('conflict', 409))).toContain('se změnil');
  });

  it('preserves pending intent in memory when storage is blocked and fails closed on malformed metadata', async () => {
    const invalidStorage = memoryStorage();
    invalidStorage.values.set(`dildovander.customer-waiting.v1.${encodeURIComponent(accountId)}.${deviceId}`, '{bad');
    const invalid = client(async () => response(snapshot()), invalidStorage);
    expect(() => invalid.getPendingCommand(deviceId)).toThrow(CustomerWaitingError);

    const blockedStorage: CustomerWaitingStorage = {
      getItem: () => { throw new Error('blocked'); },
      setItem: () => { throw new Error('blocked'); },
    };
    const memoryOnly = client(async () => { throw new Error('offline'); }, blockedStorage);
    expect(memoryOnly.metadataStatus).toBe('memory-only');
    await expect(memoryOnly.join(deviceId, eventId, profileId, { bootId, challenge: 'fresh-secret-challenge-123456' })).rejects.toThrow();
    expect(memoryOnly.metadataStatus).toBe('memory-only');
    expect(memoryOnly.getPendingCommand(deviceId)?.requestId).toBe(requestId);
  });
});

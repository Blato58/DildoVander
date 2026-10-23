import { describe, expect, it, vi } from 'vitest';
import {
  TonightWaitingClient,
  TonightWaitingError,
  isTonightWaitingDeviceId,
  parseTonightWaitingSnapshot,
  tonightWaitingPath,
  type TonightWaitingStorage,
} from './tonightWaiting';

const deviceId = 'case_7-west';
const eventId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const eveningId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const entryId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const requestId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';

function snapshot(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 1,
    availability: { state: 'available', reason: 'runtime_ready', fresh: true },
    event: { id: eventId, deviceId, eveningId, state: 'open', revision: 4 },
    head: { entryId, revision: 2, state: 'waiting' },
    count: 2,
    ...overrides,
  };
}

function response(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function memoryStorage(): TonightWaitingStorage {
  const values = new Map<string, string>();
  return {
    getItem: key => values.get(key) ?? null,
    setItem: (key, value) => { values.set(key, value); },
    removeItem: key => { values.delete(key); },
  };
}

describe('tonight waiting model', () => {
  it('validates the operator DTO and rejects unknown fields or unsafe device segments', () => {
    expect(isTonightWaitingDeviceId(deviceId)).toBe(true);
    expect(isTonightWaitingDeviceId('../case')).toBe(false);
    expect(isTonightWaitingDeviceId('case/7')).toBe(false);
    expect(tonightWaitingPath(deviceId)).toBe('/app/v1/admin/devices/case_7-west/waiting');
    expect(() => tonightWaitingPath('case/7')).toThrowError(TonightWaitingError);
    expect(parseTonightWaitingSnapshot(snapshot(), deviceId).head?.entryId).toBe(entryId);
    expect(() => parseTonightWaitingSnapshot({ ...snapshot(), ownerId: 'private' }, deviceId))
      .toThrowError(TonightWaitingError);
    expect(() => parseTonightWaitingSnapshot(snapshot({ event: { ...snapshot().event, deviceId: 'case-8' } }), deviceId))
      .toThrowError(TonightWaitingError);
  });

  it('sends no mutation automatically and retries the same stored open body only on explicit request', async () => {
    const storage = memoryStorage();
    const newEventId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
    const transport = vi.fn()
      .mockRejectedValueOnce(new Error('secret raw transport detail'))
      .mockResolvedValueOnce(response(snapshot()))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, id: newEventId, revision: 1 }, head: null, count: 0 })));
    const first = new TonightWaitingClient({
      accountId: 'operator-a', storage, transport, createRequestId: () => requestId,
    });
    expect(transport).not.toHaveBeenCalled();
    await expect(first.open(deviceId, eventId, 4)).rejects.toThrowError(TonightWaitingError);
    expect(transport).toHaveBeenCalledTimes(1);
    expect(transport.mock.calls[0][0]).toBe(`${tonightWaitingPath(deviceId)}/open`);

    const second = new TonightWaitingClient({ accountId: 'operator-a', storage, transport });
    expect(second.getPendingCommand(deviceId)?.requestId).toBe(requestId);
    const status = await second.getSnapshot(deviceId);
    expect(status.event?.id).toBe(eventId);
    expect(transport.mock.calls).toHaveLength(2);
    expect(transport.mock.calls[1][1]?.method).toBe('GET');
    expect(second.reconcile(deviceId, status)).toBe('pending');

    const retried = await second.retryPending(deviceId);
    expect(retried.event?.id).toBe(newEventId);
    const firstBody = JSON.parse(String(transport.mock.calls[0][1]?.body)) as Record<string, unknown>;
    const retriedBody = JSON.parse(String(transport.mock.calls[2][1]?.body)) as Record<string, unknown>;
    expect(retriedBody).toEqual(firstBody);
    expect(transport.mock.calls[2][1]?.method).toBe('POST');
    expect(second.reconcile(deviceId, retried)).toBe('applied');
    expect(second.getPendingCommand(deviceId)).toBeNull();
  });

  it('blocks retargeting an unresolved command and reconciles a state change from a fresh projection', async () => {
    const storage = memoryStorage();
    const transport = vi.fn().mockRejectedValueOnce(new Error('timeout'))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'paused', revision: 5 } })));
    const client = new TonightWaitingClient({ accountId: 'operator-a', storage, transport, createRequestId: () => requestId });
    await expect(client.changeState(deviceId, eventId, 4, 'paused')).rejects.toThrowError(TonightWaitingError);
    await expect(client.skipHead(deviceId, eventId, entryId, 2)).rejects.toMatchObject({ kind: 'pending-command' });
    const current = await client.getSnapshot(deviceId);
    expect(client.reconcile(deviceId, current)).toBe('applied');
    expect(client.getPendingCommand(deviceId)).toBeNull();
  });

  it('maps account status failures without exposing arbitrary response details', async () => {
    const transport = vi.fn().mockResolvedValue(new Response(JSON.stringify({ detail: 'secret data' }), {
      status: 403, headers: { 'Content-Type': 'application/problem+json' },
    }));
    const client = new TonightWaitingClient({ accountId: 'operator-a', transport });
    await expect(client.getSnapshot(deviceId)).rejects.toMatchObject({ kind: 'forbidden' });
  });
});

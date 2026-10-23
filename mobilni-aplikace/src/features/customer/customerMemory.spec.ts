import { describe, expect, it, vi } from 'vitest';
import { mutateCustomerMemory, parseCustomerMemories, readCustomerMemories } from './customerMemory';

const id = '10000000-0000-4000-8000-000000000001';
const item = {
  id, revision: 1, text: 'Mám rád modrou.', kind: 'UserReportedFact' as const,
  sourceReference: 'answer-1', createdAt: '2026-10-23T00:00:00Z',
  profileId: '20000000000040008000000000000001',
  customerSessionId: '30000000-0000-4000-8000-000000000001', canCorrect: true, canForget: true,
};
const list = { revision: 'a'.repeat(64), items: [item], complete: true };
const json = (value: unknown) => new Response(JSON.stringify(value), { headers: { 'Content-Type': 'application/json' } });

describe('customer memory wire contract', () => {
  it('accepts complete readable facts and a complete empty projection', () => {
    expect(parseCustomerMemories(list)).toEqual(list);
    expect(parseCustomerMemories({ ...list, items: [{ ...item, profileId: '20000000-0000-4000-8000-000000000001' }] })).not.toBeNull();
    expect(parseCustomerMemories({ ...list, items: [] })?.items).toEqual([]);
    // Read length follows the bounded archive, not the smaller correction input limit.
    expect(parseCustomerMemories({ ...list, items: [{ ...item, text: 'x'.repeat(3000) }] })).not.toBeNull();
  });

  it.each([
    { ...list, complete: false }, { ...list, revision: 'credentials' },
    { ...list, items: [item, item] }, { ...list, items: Array(4097).fill(item) },
    { ...list, items: [{ ...item, id: '../account' }] },
    { ...list, items: [{ ...item, revision: Number.MAX_SAFE_INTEGER + 1 }] },
    { ...list, items: [{ ...item, kind: 'ConfirmedSelection', canCorrect: true }] },
    { ...list, items: [{ ...item, kind: 'Unknown' }] },
    { ...list, items: [{ ...item, canForget: 'true' }] },
    { ...list, items: [{ ...item, profileId: 'not-a-profile-id' }] },
  ])('rejects malformed or contradictory authority %#', value => {
    expect(parseCustomerMemories(value)).toBeNull();
  });

  it('discards a response after account change, including during body parsing', async () => {
    let current = true;
    const transport = vi.fn(async () => { current = false; return json(list); });
    await expect(readCustomerMemories(undefined, () => current, transport)).rejects.toMatchObject({ name: 'AbortError' });
    const body = new ReadableStream({ start(controller) {
      controller.enqueue(new TextEncoder().encode(JSON.stringify(list)));
      current = false; controller.close();
    } });
    current = true;
    await expect(readCustomerMemories(undefined, () => current, async () => {
      current = false;
      return new Response(body, { headers: { 'Content-Type': 'application/json' } });
    })).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('bounds streamed bytes and rejects HTML without authorizing actions', async () => {
    const cancelled = vi.fn();
    const large = new ReadableStream({ start(controller) { controller.enqueue(new Uint8Array(32 * 1024 * 1024 + 1)); }, cancel: cancelled });
    await expect(readCustomerMemories(undefined, undefined, async () => new Response(large, {
      headers: { 'Content-Type': 'application/json' },
    }))).rejects.toMatchObject({ code: 'memory_response_too_large' });
    expect(cancelled).toHaveBeenCalledOnce();
    await expect(readCustomerMemories(undefined, undefined, async () => new Response('<html>'))).rejects.toMatchObject({ code: 'invalid_memory_response' });
  });

  it('sends one explicit correction with the displayed revision and never retries 409', async () => {
    const transport = vi.fn(async (_path: string, _init?: RequestInit) => new Response(JSON.stringify({ code: 'memory_mutation_outcome_unknown' }), { status: 409 }));
    await expect(mutateCustomerMemory(item, { text: 'Zelená.' }, undefined, undefined, transport)).rejects.toMatchObject({ code: 'memory_mutation_outcome_unknown', status: 409 });
    expect(transport).toHaveBeenCalledOnce();
    expect(transport.mock.calls[0]?.[0]).toBe(`/app/v1/customer/memory/${id}`);
    expect(JSON.parse(String(transport.mock.calls[0]?.[1]?.body))).toEqual({ expectedRevision: 1, text: 'Zelená.' });
  });

  it('requires the displayed capability, UTF-8 limit and explicit forget confirmation before dispatch', async () => {
    const transport = vi.fn(async () => new Response(null, { status: 204 }));
    for (const [value, change] of [
      [{ ...item, kind: 'ConfirmedSelection' as const }, { text: 'Rewritten outcome' }],
      [item, { text: 'ě'.repeat(1001) }],
      [{ ...item, canForget: false }, { confirmed: true as const }],
    ] as const) {
      await expect(mutateCustomerMemory(value, change, undefined, undefined, transport)).rejects.toMatchObject({ code: 'invalid_memory_request' });
    }
    expect(transport).not.toHaveBeenCalled();
    await mutateCustomerMemory({ ...item, kind: 'ConfirmedSelection', canCorrect: false }, { confirmed: true }, undefined, undefined, transport);
    expect(transport).toHaveBeenCalledWith(`/app/v1/customer/memory/${id}/forget`, expect.objectContaining({ method: 'POST', body: JSON.stringify({ expectedRevision: 1, confirmed: true }) }));
  });

  it('does not dispatch stale actions and treats unexpected success as uncertain', async () => {
    const transport = vi.fn(async () => json({}));
    await expect(mutateCustomerMemory(item, { confirmed: true }, undefined, () => false, transport)).rejects.toMatchObject({ name: 'AbortError' });
    expect(transport).not.toHaveBeenCalled();
    await expect(mutateCustomerMemory(item, { confirmed: true }, undefined, () => true, transport)).rejects.toMatchObject({ code: 'memory_mutation_outcome_unknown' });
    expect(transport).toHaveBeenCalledOnce();
  });
});

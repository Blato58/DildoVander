import { beforeEach, describe, expect, it, vi } from 'vitest';

const request = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', () => ({ accountRequest: request }));
import { CompletionUpdatesClient, completionUpdatePath, parseCompletionUpdatePage } from './completionUpdates';

const locator = '11111111-1111-4111-8111-111111111111';
const eventId = locator;
const baseItem: Record<string, unknown> = {
  id: eventId, sequence: 4, kind: 'keepsake', state: 'ready', reason: null, resultLocator: locator,
  canRetry: false, hasStory: true, hasArtwork: true, occurredAt: '2026-10-22T09:10:11Z', readAt: null,
};
const page = (items = [baseItem], nextCursor: string | null = null) => ({ schemaVersion: 1, items, nextCursor });

beforeEach(() => vi.resetAllMocks());

describe('completion update contract parser', () => {
  it('parses on supported runtimes without Object.hasOwn', () => {
    const descriptor = Object.getOwnPropertyDescriptor(Object, 'hasOwn');
    Object.defineProperty(Object, 'hasOwn', { configurable: true, value: undefined });
    let parsed: ReturnType<typeof parseCompletionUpdatePage> = null;
    try { parsed = parseCompletionUpdatePage(page()); }
    finally {
      if (descriptor) Object.defineProperty(Object, 'hasOwn', descriptor);
      else Reflect.deleteProperty(Object, 'hasOwn');
    }
    expect(parsed).toEqual(page());
  });
  it('accepts the exact server contract and rejects unknown or malformed fields', () => {
    expect(parseCompletionUpdatePage(page())).toEqual(page());
    for (const malformed of [
      { ...baseItem, id: 'not-a-uuid' },
      { ...baseItem, state: 'almost_ready' },
      { ...baseItem, reason: 'needs_retry' },
      { ...baseItem, canRetry: true },
      { ...baseItem, occurredAt: '2026-02-31T09:10:11Z' },
      { ...baseItem, readAt: 'yesterday' },
      { ...baseItem, unexpected: 'private field' },
      { ...baseItem, state: 'ready', reason: 'result_unavailable' },
      { ...baseItem, hasStory: false },
      { ...baseItem, hasArtwork: false },
      { ...baseItem, kind: 'portrait', hasArtwork: false },
    ]) expect(parseCompletionUpdatePage(page([malformed]))).toBeNull();
    expect(parseCompletionUpdatePage({ ...page(), extra: true })).toBeNull();
  });

  it('rejects page overflow, duplicate event ids, bad cursors, and malformed result locators', () => {
    const oversizedPage = Array.from({ length: 51 }, (_, index) => ({
      ...baseItem,
      sequence: index + 1,
      id: `${String(index + 1).padStart(8, '0')}-1111-4111-8111-111111111111`,
    }));
    expect(parseCompletionUpdatePage(page(oversizedPage))).toBeNull();
    expect(parseCompletionUpdatePage(page([baseItem, baseItem]))).toBeNull();
    expect(parseCompletionUpdatePage(page([baseItem], 'x'.repeat(2049)))).toBeNull();
    expect(parseCompletionUpdatePage(page([baseItem], ''))).toBeNull();
    expect(parseCompletionUpdatePage(page([{ ...baseItem, resultLocator: 'not-a-uuid' }]))).toBeNull();
  });
});

describe('completion updates account client', () => {
  it('passes reads and explicit idempotent acknowledgements through the account request boundary', async () => {
    const client = new CompletionUpdatesClient(() => 'account-a', () => true);
    request.mockResolvedValueOnce(page());
    expect(await client.readPage()).toEqual(page());
    expect(request).toHaveBeenCalledWith(`${completionUpdatePath}?limit=25`, 'GET', undefined, expect.any(AbortSignal));

    request.mockResolvedValueOnce(undefined);
    await client.markRead(eventId);
    expect(request).toHaveBeenLastCalledWith(`${completionUpdatePath}/${eventId}/read`, 'PUT', { schemaVersion: 1 }, expect.any(AbortSignal));
  });

  it('keeps keyset cursors opaque and rejects malformed data before exposing it', async () => {
    const client = new CompletionUpdatesClient(() => 'account-a', () => true);
    request.mockResolvedValueOnce(page([baseItem], 'opaque+/cursor'));
    expect((await client.readPage()).nextCursor).toBe('opaque+/cursor');
    request.mockResolvedValueOnce(page([baseItem], null));
    await client.readPage('opaque+/cursor');
    expect(request).toHaveBeenLastCalledWith(`${completionUpdatePath}?limit=25&cursor=opaque%2B%2Fcursor`, 'GET', undefined, expect.any(AbortSignal));
    request.mockResolvedValueOnce({ ...page(), items: [{ ...baseItem, state: 'private' }] });
    await expect(client.readPage()).rejects.toThrow('Invalid completion update response.');
  });

  it('fences late reads and writes after account invalidation or a context change', async () => {
    let currentAccount: string | null = 'account-a';
    const client = new CompletionUpdatesClient(() => currentAccount, () => currentAccount !== null);
    let finishRead!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishRead = resolve));
    const pendingRead = client.readPage();
    client.invalidate();
    finishRead(page());
    await expect(pendingRead).rejects.toMatchObject({ name: 'AbortError' });

    request.mockResolvedValueOnce(page());
    await client.readPage();
    let finishWrite!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishWrite = resolve));
    const pendingWrite = client.markRead(eventId);
    currentAccount = 'account-b';
    client.invalidate();
    finishWrite(undefined);
    await expect(pendingWrite).rejects.toMatchObject({ name: 'AbortError' });
  });

  it('requires an existing unread row before dispatching an acknowledgement', async () => {
    const client = new CompletionUpdatesClient(() => 'account-a', () => true);
    await expect(client.markRead(eventId)).rejects.toThrow();
    expect(request).not.toHaveBeenCalled();
  });

  it('replaces acknowledgement authority after a fresh first-page read', async () => {
    const client = new CompletionUpdatesClient(() => 'account-a', () => true);
    const replacement = { ...baseItem, id: '22222222-2222-4222-8222-222222222222', resultLocator: '33333333-3333-4333-8333-333333333333' };
    request.mockResolvedValueOnce(page([baseItem], 'older-cursor'));
    await client.readPage();
    request.mockResolvedValueOnce(page([replacement], null));
    await client.readPage();

    await expect(client.markRead(eventId)).rejects.toThrow();
    expect(request).toHaveBeenCalledTimes(2);
    expect(request.mock.calls.some(call => call[1] === 'PUT')).toBe(false);
  });

  it('keeps a successful acknowledgement when a GET captured before its PUT completes returns stale data', async () => {
    const client = new CompletionUpdatesClient(() => 'account-a', () => true);
    request.mockResolvedValueOnce(page());
    await client.readPage();

    let finishWrite!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishWrite = resolve));
    const acknowledgement = client.markRead(eventId);

    let finishStaleRead!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishStaleRead = resolve));
    const staleRead = client.readPage();
    finishWrite(undefined);
    await acknowledgement;
    finishStaleRead(page([baseItem]));

    const result = await staleRead;
    expect(result.items[0].readAt).toEqual(expect.any(String));
    await expect(client.markRead(eventId)).rejects.toThrow();
    expect(request.mock.calls.filter(call => call[1] === 'PUT')).toHaveLength(1);
  });
});

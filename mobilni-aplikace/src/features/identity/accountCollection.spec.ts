import { beforeEach, describe, expect, it, vi } from 'vitest';

const request = vi.hoisted(() => vi.fn());
vi.mock('./session', () => ({ accountRequest: request }));
import {
  AccountCollectionClient,
  accountCollectionPath,
  isLinkedCollectionItem,
  isOwnedCollectionItem,
  parseAccountCollectionPage,
} from './accountCollection';

const ownerId = '11111111-1111-4111-8111-111111111111';
const linkedId = '22222222-2222-4222-8222-222222222222';
const baseReadiness = { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true };
const owner = {
  locator: ownerId, linkedAt: 1_761_200_000, officialName: 'Moje památka', storyTitle: 'Večerní příběh', hasPortrait: true,
  accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false, readiness: baseReadiness,
  characterName: 'Eliška', eveningAt: 1_761_100_000,
};
const linked = {
  locator: linkedId, linkedAt: 1_761_200_010, officialName: null, storyTitle: null, hasPortrait: false,
  accessKind: 'linkedViewer', canManage: false, canReadPrivate: false, canUnlink: true,
  readiness: { ...baseReadiness, state: 'processing', reason: 'reconciliation_required', hasStory: false, hasArtwork: false },
  characterName: null, eveningAt: null,
};
const page = (items: unknown[] = [owner, linked], nextCursor: string | null = null) => ({ schemaVersion: 1, items, nextCursor });

beforeEach(() => vi.resetAllMocks());

describe('account collection contract', () => {
  it('parses required nullable metadata and keeps only the closed DTO shape', () => {
    const result = parseAccountCollectionPage(page());
    expect(result?.items[0]).toMatchObject({ officialName: 'Moje památka', characterName: 'Eliška', eveningAt: owner.eveningAt });
    expect(result?.items[1]).toMatchObject({ officialName: null, storyTitle: null, characterName: null, eveningAt: null });
    expect(parseAccountCollectionPage({ ...page(), privateContent: 'must not cross the boundary' })).toBeNull();
  });

  it('rejects malformed ids, dates, readiness values, cursor overflow, page overflow and linked private metadata', () => {
    for (const malformed of [
      { ...owner, locator: 'not-a-guid' },
      { ...owner, linkedAt: 0 },
      { ...owner, eveningAt: '2026-10-22T12:00:00Z' },
      { ...owner, officialName: undefined },
      { ...owner, readiness: { ...baseReadiness, state: 'unknown' } },
      { ...owner, readiness: { ...baseReadiness, extra: true } },
      { ...linked, characterName: 'should never be projected' },
    ]) expect(parseAccountCollectionPage(page([malformed]))).toBeNull();

    expect(parseAccountCollectionPage(page([owner, owner]))).toBeNull();
    expect(parseAccountCollectionPage(page([owner], 'x'.repeat(2049)))).toBeNull();
    expect(parseAccountCollectionPage(page(Array.from({ length: 51 }, (_, index) => ({ ...owner, locator: `${String(index + 1).padStart(8, '0')}-1111-4111-8111-111111111111` }))))).toBeNull();
  });

  it('fails closed for contradictory role projections without granting destructive capabilities', () => {
    const parsed = parseAccountCollectionPage(page([
      { ...owner, canUnlink: true },
      { ...linked, canManage: true },
    ]));
    expect(parsed).not.toBeNull();
    expect(isOwnedCollectionItem(parsed!.items[0])).toBe(false);
    expect(isLinkedCollectionItem(parsed!.items[1])).toBe(false);
  });
});

describe('account collection client', () => {
  it('sends bounded search, access and opaque cursor parameters through accountRequest', async () => {
    const client = new AccountCollectionClient(() => 'account-a', () => true);
    request.mockResolvedValueOnce(page([owner], 'opaque+/cursor'));
    await client.readPage({ search: '  hůlka  ', access: 'owned' });
    expect(request).toHaveBeenCalledWith(`${accountCollectionPath}?limit=25&access=owned&q=h%C5%AFlka`, 'GET', undefined, expect.any(AbortSignal));

    request.mockResolvedValueOnce(page([linked]));
    await client.readPage({ search: 'hůlka', access: 'owned', cursor: 'opaque+/cursor' });
    expect(request).toHaveBeenLastCalledWith(`${accountCollectionPath}?limit=25&access=owned&q=h%C5%AFlka&cursor=opaque%2B%2Fcursor`, 'GET', undefined, expect.any(AbortSignal));
  });

  it('requires a current linked-viewer row before dispatching the existing unlink route', async () => {
    const client = new AccountCollectionClient(() => 'account-a', () => true);
    await expect(client.unlink(linkedId)).rejects.toThrow();
    request.mockResolvedValueOnce(page([owner, linked]));
    await client.readPage({ search: '', access: 'all' });
    await expect(client.unlink(ownerId)).rejects.toThrow();
    request.mockResolvedValueOnce(undefined);
    await client.unlink(linkedId);
    expect(request).toHaveBeenLastCalledWith(`${accountCollectionPath.replace('/collection', '/associations')}/${linkedId}`, 'DELETE', undefined, expect.any(AbortSignal));
  });

  it('aborts late responses across account invalidation and replaces stale row capabilities on first-page refresh', async () => {
    let accountId: string | null = 'account-a';
    const client = new AccountCollectionClient(() => accountId, () => accountId !== null);
    let finishRead!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishRead = resolve));
    const pending = client.readPage({ search: '', access: 'all' });
    const signal = request.mock.calls[0][3] as AbortSignal;
    accountId = 'account-b';
    client.invalidate();
    expect(signal.aborted).toBe(true);
    finishRead(page());
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });

    request.mockResolvedValueOnce(page([linked]));
    await client.readPage({ search: '', access: 'all' });
    request.mockResolvedValueOnce(page([owner]));
    await client.readPage({ search: 'new filter', access: 'owned' });
    await expect(client.unlink(linkedId)).rejects.toThrow();
    expect(request.mock.calls.filter(call => call[1] === 'DELETE')).toHaveLength(0);
  });
});

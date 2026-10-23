import { beforeEach, describe, expect, it, vi } from 'vitest';
const transport = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', () => ({ accountTransport: transport }));
import { AccountArchiveClient } from './accountArchive';
const id = '11111111-1111-4111-8111-111111111111';
const path = `/app/v1/archive/associations/${id}`;
const ownedPath = `/app/v1/archive/owned/${id}`;
beforeEach(() => { vi.resetAllMocks(); });
describe('account archive read authority', () => {
  it('keeps reads on the exact association path and uses account transport with no redirects', async () => {
    transport.mockResolvedValue(Response.json({ locator: id }));
    const client = new AccountArchiveClient(() => 'account-a');
    expect(await client.json(path)).toEqual({ locator: id });
    expect(transport).toHaveBeenCalledWith(path, expect.objectContaining({ method: 'GET', redirect: 'error' }));
    await expect(client.json('/guest/v1/keepsakes/' + id)).rejects.toThrow();
    await expect(client.json(path + '?account=other')).rejects.toThrow();
  });
  it('allows only the owned mutation routes for recovery and share links and preserves retry keys', async () => {
    const client = new AccountArchiveClient(() => 'account-a');
    const shareResponse = Response.json({ url: 'https://example.test/keepsake/#s=synthetic-token' });
    const recoveryResponse = Response.json({ url: 'https://example.test/keepsake/#r=synthetic-token' });
    transport.mockResolvedValueOnce(shareResponse).mockResolvedValueOnce(recoveryResponse);
    await client.json(`${ownedPath}/share-links`, { method: 'POST', data: { editionId: id } });
    await client.json(`${ownedPath}/recovery-links`, { method: 'POST' });
    expect(transport.mock.calls.map(([requestPath, init]) => [requestPath, init.method])).toEqual([
      [`${ownedPath}/share-links`, 'POST'], [`${ownedPath}/recovery-links`, 'POST'],
    ]);
    for (const [, init] of transport.mock.calls) {
      expect(new Headers(init.headers).get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/i);
    }

    transport.mockReset();
    transport.mockImplementation(() => Promise.resolve(Response.json({ accepted: true })));
    await client.json(`${ownedPath}/wizard-identity`, { method: 'POST', data: { editionId: id } });
    await client.json(`${ownedPath}/account-associations`, { method: 'POST', data: { targetAccountId: 'account-b' } });
    expect(transport.mock.calls.map(([requestPath, init]) => [requestPath, init.method])).toEqual([
      [`${ownedPath}/wizard-identity`, 'POST'], [`${ownedPath}/account-associations`, 'POST'],
    ]);

    transport.mockReset();
    transport.mockResolvedValueOnce(Response.json({}, { status: 503 })).mockResolvedValueOnce(Response.json({ url: 'synthetic' }));
    await expect(client.json(`${ownedPath}/share-links`, { method: 'POST', data: { editionId: id } })).rejects.toThrow();
    await client.json(`${ownedPath}/share-links`, { method: 'POST', data: { editionId: id } });
    expect(new Headers(transport.mock.calls[0][1].headers).get('Idempotency-Key'))
      .toBe(new Headers(transport.mock.calls[1][1].headers).get('Idempotency-Key'));
  });
  it('rejects unsupported account paths and method combinations before transport', async () => {
    const client = new AccountArchiveClient(() => 'account-a');
    for (const [requestPath, method] of [
      [`${ownedPath}/share-links`, 'GET'],
      [ownedPath, 'POST'],
      [`${ownedPath}/portrait`, 'POST'],
      [`${ownedPath}/history`, 'GET'],
      [`/guest/v1/keepsakes/${id}/share-links`, 'POST'],
    ] as const) {
      await expect(client.json(requestPath, { method })).rejects.toThrow();
    }
    expect(transport).not.toHaveBeenCalled();
  });
  it('a late native-style nonabortable response cannot release private bytes after switching accounts', async () => {
    let current = 'account-a'; let complete!: (response: Response) => void;
    transport.mockReturnValue(new Promise(resolve => complete = resolve));
    const client = new AccountArchiveClient(() => current);
    const download = client.blob(path + '/assets/' + id, ['image/png']);
    current = 'account-b'; complete(new Response(new Blob(['private result'], { type: 'image/png' })));
    await expect(download).rejects.toMatchObject({ name: 'AbortError' });
  });
  it('body decoding remains fenced and unknown media never becomes a download', async () => {
    let current = 'account-a'; let finish!: (blob: Blob) => void;
    transport.mockResolvedValue({ ok: true, blob: () => new Promise(resolve => finish = resolve) });
    const client = new AccountArchiveClient(() => current); const download = client.blob(path + '/assets/' + id, ['image/png']);
    await new Promise(resolve => setTimeout(resolve, 0)); current = ''; finish(new Blob(['private result'], { type: 'image/png' }));
    await expect(download).rejects.toMatchObject({ name: 'AbortError' });
    current = 'account-a'; transport.mockResolvedValue(new Response('<html>', { headers: { 'Content-Type': 'text/html' } }));
    await expect(client.blob(path + '/assets/' + id, ['image/png'])).rejects.toThrow();
  });
});

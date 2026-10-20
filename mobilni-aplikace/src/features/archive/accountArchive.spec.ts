import { beforeEach, describe, expect, it, vi } from 'vitest';
const transport = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', () => ({ accountTransport: transport }));
import { AccountArchiveClient } from './accountArchive';
const id = '11111111-1111-4111-8111-111111111111';
const path = `/app/v1/archive/associations/${id}`;
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

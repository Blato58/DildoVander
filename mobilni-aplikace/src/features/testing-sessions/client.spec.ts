import { afterEach, describe, expect, it, vi } from 'vitest';
const transport=vi.hoisted(()=>vi.fn());
vi.mock('../identity/session',()=>({accountTransport:transport}));
import { TestingSessionsClient } from './client';
afterEach(()=>vi.clearAllMocks());
describe('testing account transport boundaries',()=>{
  it('retains the server trace for JSON and non-JSON failures without exposing the response body', async () => {
    const requestId = '01234567-89ab-4cde-8f01-23456789abcd';
    const client = new TestingSessionsClient(() => 'owner');
    transport.mockResolvedValueOnce(new Response('<html>gateway detail</html>', { status: 502, headers: { 'X-Request-Id': requestId } }));
    await expect(client.characters()).rejects.toMatchObject({ code: 'testing_request_failed', status: 502, requestId });
    transport.mockResolvedValueOnce(Response.json({ code: 'account_required', traceId: requestId }, { status: 401 }));
    await expect(client.characters()).rejects.toMatchObject({ code: 'account_required', status: 401, requestId });
  });
  it('fences a response that arrives after account replacement even when transport ignores cancellation',async()=>{
    let owner='first';let complete!:(response:Response)=>void;
    transport.mockImplementation(()=>new Promise<Response>(resolve=>{complete=resolve;}));
    const client=new TestingSessionsClient(()=>owner);const response=client.session('owned-session');
    owner='second';client.invalidate();complete(Response.json({private:'first-account'}));
    await expect(response).rejects.toMatchObject({name:'AbortError'});
    expect(transport.mock.calls[0][1].signal.aborted).toBe(true);
  });
  it('keeps virtual scope identifiers on explicit account routes and retains backend failure codes',async()=>{
    transport.mockResolvedValue(Response.json({code:'test_resource_unavailable'},{status:404}));
    const client=new TestingSessionsClient(()=>'owner');
    await expect(client.selectWand('unowned')).rejects.toMatchObject({code:'test_resource_unavailable',status:404});
    expect(transport).toHaveBeenCalledWith('/app/v1/testing-sessions/unowned/select',expect.objectContaining({method:'POST',body:'{}',redirect:'error'}));
    expect(transport.mock.calls[0][1].headers).not.toHaveProperty('Authorization');
  });
  it('refuses to render a successful non-image response as generated artwork',async()=>{
    transport.mockResolvedValue(new Response('fake-success',{headers:{'Content-Type':'text/plain'}}));
    await expect(new TestingSessionsClient(()=>'owner').asset('test','keepsake','asset')).rejects.toMatchObject({code:'testing_asset_unavailable'});
  });
});

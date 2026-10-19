import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
const bridge = vi.hoisted(() => ({native: false, request: vi.fn(), passkey: vi.fn()}));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => bridge.native }, registerPlugin: () => bridge }));
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(complete => { resolve = complete; }); return {promise, resolve}; }
const user = (id: string | null, token = id ?? 'anonymous') => ({ enabled: true, account: id ? {id, displayName: id, roles: ['Viewer']} : null, csrfToken: token, origin: 'https://accounts.invalid' });
let request: ReturnType<typeof vi.fn>;
beforeEach(() => { vi.resetModules(); bridge.native = false; bridge.request.mockReset(); bridge.passkey.mockReset(); request = vi.fn(); vi.stubGlobal('fetch', request); });
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('authentication generation fences', () => {
  it.each([false, true])('rejects a cancelled observer response without clearing the current account (native=%s)', async native => {
    bridge.native = native;
    const identity = await import('./session');
    const late = deferred<any>();
    if (native) bridge.request.mockResolvedValueOnce({ status: 200, body: JSON.stringify(user('current')) });
    else request.mockResolvedValueOnce(Response.json(user('current')));
    await identity.refreshIdentity();
    if (native) bridge.request.mockReturnValueOnce(late.promise);
    else request.mockReturnValueOnce(late.promise);
    const controller = new AbortController();
    Object.defineProperties(controller.signal, { throwIfAborted: { value: undefined }, reason: { value: undefined } });
    const pending = identity.accountRequest('/v1/twin/instances', 'GET', undefined, controller.signal);
    const rejected = expect(pending).rejects.toMatchObject({ name: 'AbortError' });
    if (!native) expect(request.mock.calls.at(-1)![1].signal).toBe(controller.signal);
    controller.abort();
    const denied = { code: 'session_revoked' };
    late.resolve(native ? { status: 401, body: JSON.stringify(denied) } : Response.json(denied, { status: 401 }));
    await rejected;
    expect(identity.account.value?.id).toBe('current');
  });

  it('does not send an observer request whose signal is already aborted', async () => {
    const identity = await import('./session');
    const controller = new AbortController(); controller.abort();
    await expect(identity.accountRequest('/v1/twin/instances', 'GET', undefined, controller.signal))
      .rejects.toMatchObject({ name: 'AbortError' });
    expect(request).not.toHaveBeenCalled(); expect(bridge.request).not.toHaveBeenCalled();
  });

  it('clears immediately on logout and ignores a noncooperative older session response and CSRF token', async () => {
    const identity = await import('./session'); const old = deferred<Response>(); const deletion = deferred<Response>();
    let reads = 0;
    request.mockImplementation(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') return deletion.promise;
      if (init?.method === 'POST') return new Response(null, {status: 204});
      ++reads; return reads === 1 ? Response.json(user('old')) : reads === 2 ? old.promise : Response.json(user(null, 'signed-out-csrf'));
    });
    await identity.refreshIdentity(); const obsoleteRefresh = identity.refreshIdentity();
    const logout = identity.signOut(); expect(identity.account.value).toBeNull();
    old.resolve(Response.json(user('old', 'obsolete-csrf'))); await obsoleteRefresh;
    expect(identity.account.value).toBeNull();
    deletion.resolve(new Response(null, {status: 204})); await logout;
    expect(reads).toBe(3); expect(identity.account.value).toBeNull(); expect(identity.loading.value).toBe(false);
    await identity.accountRequest('/app/v1/enrollment/options', 'POST', {});
    expect(request.mock.calls.at(-1)![1].headers['X-DV-CSRF']).toBe('signed-out-csrf');
  });

  it('keeps a new real authentication result when an older account request returns 401', async () => {
    const identity = await import('./session'); const old = deferred<Response>(); let reads = 0;
    Object.defineProperty(window, 'isSecureContext', {configurable: true, value: true});
    Object.defineProperty(window, 'PublicKeyCredential', {configurable: true, value: class {}});
    vi.stubGlobal('AuthenticatorAttestationResponse', class {});
    Object.defineProperty(navigator, 'credentials', {configurable: true, value: {get: vi.fn(async () => ({id: 'key', rawId: new Uint8Array([1]).buffer, type: 'public-key', authenticatorAttachment: 'platform', getClientExtensionResults: () => ({}), response: {clientDataJSON: new ArrayBuffer(1), authenticatorData: new ArrayBuffer(1), signature: new ArrayBuffer(1), userHandle: null}}))}});
    request.mockImplementation(async (path: string, init?: RequestInit) => {
      if (path === '/app/v1/account') return old.promise;
      if (path === '/app/v1/session') return Response.json(user(++reads < 3 ? 'old' : 'new'));
      if (path === '/app/v1/passkeys/request-options') return Response.json({challenge: 'AQ', allowCredentials: []});
      if (init?.method === 'POST') return new Response(null, {status: 204});
      throw new Error('Unexpected request');
    });
    await identity.refreshIdentity(); const obsolete = identity.accountRequest('/app/v1/account');
    const denied = expect(obsolete).rejects.toMatchObject({status: 401});
    await identity.authenticatePasskey('signin'); expect(identity.account.value?.id).toBe('new');
    old.resolve(Response.json({code: 'session_revoked'}, {status: 401})); await denied;
    expect(identity.account.value?.id).toBe('new');
    await identity.accountRequest('/app/v1/passkeys/options', 'POST');
    expect(request.mock.calls.at(-1)![1].headers['X-DV-CSRF']).toBe('new');
  });

  it('does not mask or retry a current unauthorized response', async () => {
    const identity = await import('./session');
    request.mockResolvedValueOnce(Response.json(user('current'))); await identity.refreshIdentity();
    request.mockResolvedValueOnce(Response.json({code: 'session_revoked'}, {status: 401}));
    await expect(identity.refreshIdentity()).rejects.toMatchObject({status: 401});
    expect(identity.account.value).toBeNull(); expect(identity.initialized.value).toBe(false);
    expect(request).toHaveBeenCalledTimes(2); expect(identity.loading.value).toBe(false);
  });

  it('waits for a late logout cookie deletion before starting a new authentication or lifecycle refresh', async () => {
    bridge.native = true; const identity = await import('./session');
    const deletion = deferred<void>(); let cookie: string | null = 'old';
    bridge.request.mockImplementation(async ({method}: {method: string}) => {
      if (method === 'DELETE') { await deletion.promise; cookie = null; return {status: 204, body: ''}; }
      return {status: 200, body: JSON.stringify(user(cookie))};
    });
    bridge.passkey.mockImplementation(async () => { expect(cookie).toBeNull(); cookie = 'new'; });
    await identity.refreshIdentity();
    const logout = identity.signOut(); const signin = identity.authenticatePasskey('signin'); const lifecycle = identity.refreshIdentity();
    expect(identity.account.value).toBeNull(); expect(identity.signingOut.value).toBe(true);
    await Promise.resolve(); await Promise.resolve();
    expect(bridge.passkey).not.toHaveBeenCalled(); expect(bridge.request).toHaveBeenCalledTimes(2);
    deletion.resolve(); await Promise.all([logout, signin, lifecycle]);
    expect(cookie).toBe('new'); expect(identity.account.value?.id).toBe('new'); expect(identity.signingOut.value).toBe(false);
    expect(bridge.request.mock.calls.filter(call => call[0].method === 'DELETE')).toHaveLength(1);
  });

  it('waits for an older assertion cookie write before revoking the resulting session', async () => {
    bridge.native = true; const identity = await import('./session'); const assertion = deferred<void>();
    let cookie: string | null = 'old';
    bridge.request.mockImplementation(async ({method}: {method: string}) => {
      if (method === 'DELETE') { cookie = null; return {status: 204, body: ''}; }
      return {status: 200, body: JSON.stringify(user(cookie))};
    });
    bridge.passkey.mockImplementation(async () => { await assertion.promise; cookie = 'asserted'; });
    await identity.refreshIdentity(); const signin = identity.authenticatePasskey('signin');
    const superseded = expect(signin).rejects.toMatchObject({name: 'AbortError'});
    const logout = identity.signOut(); expect(identity.account.value).toBeNull();
    expect(bridge.request.mock.calls.filter(call => call[0].method === 'DELETE')).toHaveLength(0);
    assertion.resolve(); await superseded; await logout;
    expect(cookie).toBeNull(); expect(identity.account.value).toBeNull(); expect(identity.signingOut.value).toBe(false);
  });

  it('reports failed logout and releases the authentication lane only after that request settles', async () => {
    bridge.native = true; const identity = await import('./session'); const deletion = deferred<void>();
    bridge.request.mockImplementation(async ({method}: {method: string}) => {
      if (method === 'DELETE') { await deletion.promise; throw new Error('Network disconnected'); }
      return {status: 200, body: JSON.stringify(user('new'))};
    });
    const logout = identity.signOut(); const failed = expect(logout).rejects.toThrow('Network disconnected');
    const signin = identity.authenticatePasskey('signin'); await Promise.resolve(); expect(bridge.passkey).not.toHaveBeenCalled();
    deletion.resolve(); await failed; await signin;
    expect(bridge.passkey).toHaveBeenCalledOnce(); expect(identity.signingOut.value).toBe(false);
  });
});

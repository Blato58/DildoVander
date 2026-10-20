import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
const bridge = vi.hoisted(() => ({native: false, request: vi.fn(), passkey: vi.fn(), accountTransport: vi.fn(), cancelAccountRequest: vi.fn()}));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => bridge.native }, registerPlugin: () => bridge }));
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(complete => { resolve = complete; }); return {promise, resolve}; }
const user = (id: string | null, token = id ?? 'anonymous') => ({ enabled: true, account: id ? {id, displayName: id, roles: ['Viewer']} : null, csrfToken: token, origin: 'https://accounts.invalid' });
let request: ReturnType<typeof vi.fn>;
beforeEach(() => { vi.resetModules(); bridge.native = false; bridge.request.mockReset(); bridge.passkey.mockReset(); bridge.accountTransport.mockReset(); bridge.cancelAccountRequest.mockReset().mockResolvedValue({cancelled: true}); request = vi.fn(); vi.stubGlobal('fetch', request); });
afterEach(() => { vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('authentication generation fences', () => {
  it.each(['request', 'transport'])('cancels the exact native %s before its pending result settles', async kind => {
    bridge.native = true;
    const identity = await import('./session');
    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    await identity.refreshIdentity();
    let rejectNative!: (error: unknown) => void;
    const queued = new Promise<any>((_resolve, reject) => { rejectNative = reject; });
    const method = kind === 'request' ? bridge.request : bridge.accountTransport;
    method.mockReturnValueOnce(queued);
    const controller = new AbortController();
    const operation = kind === 'request'
      ? identity.accountRequest('/app/v1/account', 'GET', undefined, controller.signal)
      : identity.accountTransport('/v1/show/devices/kufr-1/commands', {
        method: 'POST', body: JSON.stringify({kind: 'scene'}), signal: controller.signal,
        headers: {'X-DV-Device-Id': 'kufr-1'},
      });
    const rejected = expect(operation).rejects.toMatchObject({name: 'AbortError'});
    const requestId = method.mock.calls.at(-1)![0].requestId;
    expect(requestId).toMatch(/^[0-9a-f-]{36}$/i);
    bridge.cancelAccountRequest.mockImplementationOnce(async () => {
      rejectNative(Object.assign(new Error('Queued native request cancelled'), {code: 'ACCOUNT_REQUEST_CANCELLED'}));
      return {cancelled: true};
    });
    controller.abort();
    expect(bridge.cancelAccountRequest).toHaveBeenCalledExactlyOnceWith({requestId});
    await rejected;
    expect(identity.account.value?.id).toBe('current');
  });

  it.each(['request', 'transport'])('removes the abort listener after a completed native %s', async kind => {
    bridge.native = true;
    const identity = await import('./session');
    const controller = new AbortController();
    if (kind === 'request') {
      bridge.request.mockResolvedValueOnce({status: 200, body: '{}'});
      await identity.accountRequest('/app/v1/account', 'GET', undefined, controller.signal);
    } else {
      bridge.accountTransport.mockResolvedValueOnce({status: 200, headers: {'content-type': 'application/json'}, bodyBase64: btoa('{}')});
      await identity.accountTransport('/v1/show/devices/kufr-1', {signal: controller.signal});
    }
    controller.abort();
    expect(bridge.cancelAccountRequest).not.toHaveBeenCalled();
  });

  it('uses separate native request ids and never cancels a different device command', async () => {
    bridge.native = true;
    const identity = await import('./session');
    const first = deferred<any>();
    const second = deferred<any>();
    bridge.accountTransport.mockReturnValueOnce(first.promise).mockReturnValueOnce(second.promise);
    const controller = new AbortController();
    const obsolete = identity.accountTransport('/v1/show/devices/kufr-1/commands', {method: 'POST', signal: controller.signal});
    const current = identity.accountTransport('/v1/show/devices/kufr-2/commands', {method: 'POST'});
    const rejected = expect(obsolete).rejects.toMatchObject({name: 'AbortError'});
    const [firstId, secondId] = bridge.accountTransport.mock.calls.map(call => call[0].requestId);
    expect(firstId).not.toBe(secondId);
    controller.abort();
    expect(bridge.cancelAccountRequest).toHaveBeenCalledExactlyOnceWith({requestId: firstId});
    first.resolve({status: 204, headers: {}, bodyBase64: ''});
    second.resolve({status: 204, headers: {}, bodyBase64: ''});
    await rejected;
    expect((await current).status).toBe(204);
  });

  it.each(['account-change', 'abort'])('never dispatches a delayed native Blob body after %s', async reason => {
    bridge.native = true;
    const identity = await import('./session');
    let cookie: string | null = 'old';
    bridge.request.mockImplementation(async ({method}: {method: string}) => {
      if (method === 'DELETE') { cookie = null; return {status: 204, body: ''}; }
      return {status: 200, body: JSON.stringify(user(cookie))};
    });
    bridge.passkey.mockImplementation(async () => { cookie = 'new'; });
    await identity.refreshIdentity();
    const delayedBytes = deferred<ArrayBuffer>();
    const payload = JSON.stringify({kind: 'stop'});
    const body = new Blob([payload]);
    vi.spyOn(body, 'arrayBuffer').mockReturnValue(delayedBytes.promise);
    const controller = new AbortController();
    const pending = identity.accountTransport('/v1/show/devices/kufr-1/commands', {
      method: 'POST', body, signal: controller.signal, headers: {'X-DV-Device-Id': 'kufr-1'},
    });
    const rejected = expect(pending).rejects.toMatchObject({name: 'AbortError'});
    if (reason === 'account-change') {
      await identity.signOut();
      await identity.authenticatePasskey('signin');
      expect(identity.account.value?.id).toBe('new');
    } else controller.abort();
    delayedBytes.resolve(new TextEncoder().encode(payload).buffer);
    await rejected;
    expect(bridge.accountTransport).not.toHaveBeenCalled();
    expect(identity.account.value?.id).toBe(reason === 'account-change' ? 'new' : 'old');
  });

  it.each([false, true])('rejects a cancelled observer response without clearing the current account (native=%s)', async native => {
    bridge.native = native;
    const identity = await import('./session');
    const late = deferred<any>();
    if (native) bridge.request.mockResolvedValueOnce({ status: 200, body: JSON.stringify(user('current')) });
    else request.mockResolvedValueOnce(Response.json(user('current')));
    await identity.refreshIdentity();
    if (native) {
      bridge.request.mockReturnValueOnce(late.promise);
      bridge.cancelAccountRequest.mockRejectedValueOnce(new Error('Cancellation bridge unavailable'));
    }
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

  it.each([false, true])('does not send an observer request whose signal is already aborted (native=%s)', async native => {
    bridge.native = native;
    const identity = await import('./session');
    const controller = new AbortController(); controller.abort();
    await expect(identity.accountRequest('/v1/twin/instances', 'GET', undefined, controller.signal))
      .rejects.toMatchObject({ name: 'AbortError' });
    expect(request).not.toHaveBeenCalled(); expect(bridge.request).not.toHaveBeenCalled(); expect(bridge.cancelAccountRequest).not.toHaveBeenCalled();
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

  it.each([false, true])('does not mask or retry a current unauthorized response (native=%s)', async native => {
    bridge.native = native;
    const identity = await import('./session');
    if (native) bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    else request.mockResolvedValueOnce(Response.json(user('current')));
    await identity.refreshIdentity();
    if (native) bridge.request.mockResolvedValueOnce({status: 401, body: JSON.stringify({code: 'session_revoked'})});
    else request.mockResolvedValueOnce(Response.json({code: 'session_revoked'}, {status: 401}));
    await expect(identity.refreshIdentity()).rejects.toMatchObject({status: 401});
    expect(identity.account.value).toBeNull(); expect(identity.initialized.value).toBe(false);
    expect(native ? bridge.request : request).toHaveBeenCalledTimes(2); expect(identity.loading.value).toBe(false);
  });

  it.each(['reauth', 'add'] as const)('keeps the authenticated context when a queued refresh retires before native %s completes', async mode => {
    bridge.native = true;
    const identity = await import('./session');
    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    await identity.refreshIdentity();
    const ceremony = deferred<void>();
    bridge.passkey.mockReturnValueOnce(ceremony.promise);
    const authentication = identity.authenticatePasskey(mode);
    expect(bridge.passkey).toHaveBeenCalledOnce();

    let retireRefresh!: (error: unknown) => void;
    bridge.request.mockReturnValueOnce(new Promise((_resolve, reject) => { retireRefresh = reject; }));
    const refresh = identity.refreshIdentity();
    retireRefresh(Object.assign(new Error('Native request scope retired'), {code: 'ACCOUNT_CONTEXT_CHANGED'}));
    await refresh;
    expect(identity.account.value?.id).toBe('current');
    expect(identity.accountsEnabled.value).toBe(true);
    expect(identity.initialized.value).toBe(true);
    expect(identity.loading.value).toBe(false);
    expect(bridge.request).toHaveBeenCalledTimes(2);

    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current', 'renewed-csrf'))});
    ceremony.resolve();
    await authentication;
    expect(identity.account.value?.id).toBe('current');
    expect(bridge.request).toHaveBeenCalledTimes(3);
  });

  it('clears the account when the current native cookie scope disappears outside a ceremony', async () => {
    bridge.native = true;
    const identity = await import('./session');
    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('expired'))});
    await identity.refreshIdentity();
    bridge.request.mockRejectedValueOnce(Object.assign(new Error('Native cookie was cleared'), {code: 'ACCOUNT_CONTEXT_CHANGED'}));
    await expect(identity.refreshIdentity()).rejects.toMatchObject({name: 'AbortError'});
    expect(identity.account.value).toBeNull();
    expect(identity.accountsEnabled.value).toBe(false);
    expect(identity.initialized.value).toBe(false);
    expect(identity.loading.value).toBe(false);
    expect(bridge.request).toHaveBeenCalledTimes(2);
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

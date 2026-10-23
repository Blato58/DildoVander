import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';
const bridge = vi.hoisted(() => ({native: false, request: vi.fn(), passkey: vi.fn(), accountTransport: vi.fn(), shareAccountDataFile: vi.fn(), cancelAccountRequest: vi.fn()}));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => bridge.native }, registerPlugin: () => bridge }));
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(complete => { resolve = complete; }); return {promise, resolve}; }
const user = (id: string | null, token = id ?? 'anonymous') => ({ enabled: true, account: id ? {id, displayName: id, roles: ['Viewer']} : null, csrfToken: token, origin: 'https://accounts.invalid' });
let request: ReturnType<typeof vi.fn>;
beforeEach(() => { vi.resetModules(); bridge.native = false; bridge.request.mockReset(); bridge.passkey.mockReset(); bridge.accountTransport.mockReset(); bridge.shareAccountDataFile.mockReset(); bridge.cancelAccountRequest.mockReset().mockResolvedValue({cancelled: true}); request = vi.fn(); vi.stubGlobal('fetch', request); });
afterEach(() => { interfaceLanguagePreference.setScope(null); interfaceLanguagePreference.setLanguage('cs'); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('native account archive transport limits', () => {
  const archiveId = '123e4567-e89b-42d3-a456-426614174000';
  const uploadPath = `/app/v1/archive/owned/${archiveId}/portrait-uploads`;
  const uploadResponse = {status: 201, headers: {'content-type': 'application/json'}, bodyBase64: btoa('{}')};

  it('limits expanded roles to exact methods, identifiers, media types and paths', async () => {
    const identity = await import('./session');
    for (const path of [uploadPath + '\n', uploadPath + '\u2028', uploadPath + '/', uploadPath + '#x',
      uploadPath.replace(archiveId, 'not-a-uuid'), uploadPath.replace('/owned/', '//owned/')]) {
      expect(identity.accountTransportBodyLimit(path, 'POST', 'image/jpeg')).toBe(262144);
    }
    for (const type of ['image/jpeg', 'image/png', 'image/webp']) {
      expect(identity.accountTransportBodyLimit(uploadPath, 'POST', type)).toBe(8 * 1024 * 1024);
    }
    expect(identity.accountTransportBodyLimit(uploadPath, 'GET', 'image/jpeg')).toBe(262144);
    expect(identity.accountTransportBodyLimit(uploadPath, 'POST', 'application/json')).toBe(262144);
    const assetPath = `/app/v1/archive/owned/${archiveId}/assets/${archiveId}`;
    for (const path of [assetPath + '\n', assetPath + '?x=1', assetPath.replace('/assets/', '/assets/not-a-uuid/')]) {
      expect(identity.accountTransportResponseLimit(path, 'GET')).toBe(1024 * 1024);
    }
    expect(identity.accountTransportResponseLimit(assetPath, 'POST')).toBe(1024 * 1024);
  });

  it('sends an original portrait larger than 256 KiB with scoped numeric consent', async () => {
    bridge.native = true;
    bridge.accountTransport.mockResolvedValueOnce(uploadResponse);
    const identity = await import('./session');
    const original = new Blob([new Uint8Array(300 * 1024)], {type: 'image/jpeg'});
    await identity.accountTransport(uploadPath, {
      method: 'POST', body: original, headers: {'Content-Type': 'image/jpeg', 'X-DV10-Consent': '7'},
    });
    const request = bridge.accountTransport.mock.calls[0]![0];
    expect(atob(request.bodyBase64).length).toBe(300 * 1024);
    expect(request.headers).toMatchObject({'Content-Type': 'image/jpeg', 'X-DV10-Consent': '7'});
  });

  it('rejects the upload cap plus one and rejects consent outside the upload role', async () => {
    bridge.native = true;
    const identity = await import('./session');
    await expect(identity.accountTransport(uploadPath, {
      method: 'POST', body: new Blob([new Uint8Array(8 * 1024 * 1024 + 1)], {type: 'image/jpeg'}),
      headers: {'Content-Type': 'image/jpeg', 'X-DV10-Consent': '7'},
    })).rejects.toThrow('povolenou velikost');
    await expect(identity.accountTransport('/app/v1/account', {
      method: 'GET', headers: {'X-DV10-Consent': '7'},
    })).rejects.toThrow('Neplatná cesta účtu');
    expect(bridge.accountTransport).not.toHaveBeenCalled();
  });

  it('keeps the generic body cap for unsupported MIME and malformed upload paths', async () => {
    bridge.native = true;
    const identity = await import('./session');
    await expect(identity.accountTransport(uploadPath, {
      method: 'POST', body: new Blob([new Uint8Array(262145)], {type: 'image/svg+xml'}),
      headers: {'Content-Type': 'image/svg+xml', 'X-DV10-Consent': '7'},
    })).rejects.toThrow('povolenou velikost');
    await expect(identity.accountTransport(`${uploadPath}?extra=1`, {
      method: 'POST', body: new Blob([new Uint8Array(262145)], {type: 'image/jpeg'}),
      headers: {'Content-Type': 'image/jpeg', 'X-DV10-Consent': '7'},
    })).rejects.toThrow('Neplatná cesta účtu');
    expect(bridge.accountTransport).not.toHaveBeenCalled();
  });

  it('admits large owned asset responses while keeping the generic response cap', async () => {
    bridge.native = true;
    const identity = await import('./session');
    expect(identity.accountTransportResponseAllowed(`/app/v1/archive/owned/${archiveId}/assets/${archiveId}`, 'GET', 32 * 1024 * 1024)).toBe(true);
    expect(identity.accountTransportResponseAllowed(`/app/v1/archive/owned/${archiveId}/assets/${archiveId}`, 'GET', 32 * 1024 * 1024 + 1)).toBe(false);
    const oversizedGeneric = btoa('x'.repeat(1024 * 1024 + 1));
    bridge.accountTransport.mockResolvedValueOnce({status: 200, headers: {}, bodyBase64: oversizedGeneric});
    await expect(identity.accountTransport('/app/v1/account')).rejects.toThrow('příliš velká');

    bridge.accountTransport.mockResolvedValueOnce({status: 200, headers: {}, bodyBase64: oversizedGeneric});
    const asset = await identity.accountTransport(`/app/v1/archive/owned/${archiveId}/assets/${archiveId}`);
    expect((await asset.arrayBuffer()).byteLength).toBe(1024 * 1024 + 1);
  });
});

describe('native account data export', () => {
  const filename = 'customer-data-export.json';
  async function signedIn() {
    bridge.native = true;
    const identity = await import('./session');
    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    await identity.refreshIdentity();
    return identity;
  }

  it('extends only the exact customer memory and export download GET responses', async () => {
    bridge.native = true;
    const identity = await import('./session');
    const exportId = 'A23E4567-E89B-42D3-A456-426614174000';
    const roles = [
      ['/app/v1/customer/memory', 'GET'],
      [`/app/v1/customer/data/exports/${exportId}/download`, 'GET'],
    ] as const;
    for (const [path, method] of roles) {
      expect(identity.accountTransportResponseLimit(path, method)).toBe(32 * 1024 * 1024);
      expect(identity.accountTransportResponseAllowed(path, method, 32 * 1024 * 1024)).toBe(true);
      expect(identity.accountTransportResponseAllowed(path, method, 32 * 1024 * 1024 + 1)).toBe(false);
    }
    for (const path of [
      '/app/v1/customer/memory?x=1', '/app/v1/customer/memory#x', '/app/v1/customer/memory/',
      '/app/v1/customer//memory', '/app/v1/customer/MEMORY', '/app/v1/customer/memory%2f',
      '/app/v1/customer/memory/extra', '/app/v1/customer/data/exports/not-a-guid/download',
      '/app/v1/customer/data/exports/123e4567e89b42d3a456426614174000/download',
    ]) expect(identity.accountTransportResponseLimit(path, 'GET')).toBe(1024 * 1024);
    expect(identity.accountTransportResponseLimit('/app/v1/customer/memory', 'POST')).toBe(1024 * 1024);
    expect(identity.accountTransportResponseLimit('/app/v1/customer/data/exports/123e4567-e89b-42d3-a456-426614174000/download', 'DELETE')).toBe(1024 * 1024);

    const largerThanGeneric = btoa('x'.repeat(1024 * 1024 + 1));
    bridge.accountTransport.mockResolvedValueOnce({status: 200, headers: {}, bodyBase64: largerThanGeneric});
    bridge.accountTransport.mockResolvedValueOnce({status: 200, headers: {}, bodyBase64: largerThanGeneric});
    for (const [path, method] of roles) {
      const response = await identity.accountTransport(path, {method});
      expect((await response.arrayBuffer()).byteLength).toBe(1024 * 1024 + 1);
    }
    expect(request).not.toHaveBeenCalled();
  });

  it('keeps cap plus one rejected on generic, wrong-method and malformed roles', async () => {
    bridge.native = true;
    const identity = await import('./session');
    const largerThanGeneric = btoa('x'.repeat(1024 * 1024 + 1));
    for (const [path, method] of [
      ['/app/v1/account', 'GET'], ['/app/v1/customer/memory', 'POST'],
      ['/app/v1/customer/memory/extra', 'GET'],
    ]) {
      bridge.accountTransport.mockResolvedValueOnce({status: 200, headers: {}, bodyBase64: largerThanGeneric});
      await expect(identity.accountTransport(path, {method})).rejects.toThrow('příliš velká');
    }
    expect(request).not.toHaveBeenCalled();
  });

  it('shares only a bounded, complete UTF-8 JSON object through the native bridge', async () => {
    const identity = await signedIn();
    const content = '{"schemaVersion":1,"label":"Příběh"}';
    bridge.shareAccountDataFile.mockResolvedValueOnce({completed: true});
    const check = vi.fn();
    const result = await identity.shareNativeAccountDataFile(
      new Blob([content], {type: 'application/json'}), filename, {check},
    );
    const call = bridge.shareAccountDataFile.mock.calls[0]![0];
    expect(call).toMatchObject({filename, mediaType: 'application/json'});
    expect(call.requestId).toMatch(/^[0-9a-f-]{36}$/i);
    expect(new TextDecoder().decode(Uint8Array.from(atob(call.bodyBase64), char => char.charCodeAt(0)))).toBe(content);
    expect(result).toEqual({completed: true});
    expect(check.mock.calls.length).toBeGreaterThanOrEqual(4);
    bridge.shareAccountDataFile.mockResolvedValueOnce({completed: false});
    await expect(identity.shareNativeAccountDataFile(new Blob([content], {type: 'application/json'}), filename))
      .resolves.toEqual({completed: false});
    expect(bridge.accountTransport).not.toHaveBeenCalled();
    expect(request).not.toHaveBeenCalled();
  });

  it('rejects unauthenticated, web, unsupported, and invalid file inputs without fallback requests', async () => {
    bridge.native = true;
    const identity = await import('./session');
    const validBlob = new Blob(['{}'], {type: 'application/json'});
    await expect(identity.shareNativeAccountDataFile(validBlob, filename)).rejects.toMatchObject({name: 'AbortError'});
    expect(bridge.shareAccountDataFile).not.toHaveBeenCalled();
    expect(request).not.toHaveBeenCalled();

    await signedIn();
    const invalidInputs: Array<[Blob, string]> = [
      [new Blob([], {type: 'application/json'}), filename],
      [new Blob(['{}'], {type: 'text/plain'}), filename],
      [validBlob, 'other.json'],
      [new Blob(['{'], {type: 'application/json'}), filename],
      [new Blob(['{} trailing'], {type: 'application/json'}), filename],
      [new Blob(['[]'], {type: 'application/json'}), filename],
      [new Blob(['null'], {type: 'application/json'}), filename],
      [new Blob([new Uint8Array([0xff])], {type: 'application/json'}), filename],
      [new Blob([new Uint8Array(32 * 1024 * 1024 + 1)], {type: 'application/json'}), filename],
    ];
    for (const [blob, selectedFilename] of invalidInputs) {
      await expect(identity.shareNativeAccountDataFile(blob, selectedFilename)).rejects.toThrow();
    }
    const mismatched = new Blob(['{}'], {type: 'application/json'});
    vi.spyOn(mismatched, 'arrayBuffer').mockResolvedValue(new TextEncoder().encode('{').buffer);
    await expect(identity.shareNativeAccountDataFile(mismatched, filename)).rejects.toThrow('Velikost exportního souboru se změnila');
    expect(bridge.shareAccountDataFile).not.toHaveBeenCalled();

    const registeredShare = bridge.shareAccountDataFile;
    (bridge as unknown as Record<string, unknown>).shareAccountDataFile = undefined;
    await expect(identity.shareNativeAccountDataFile(validBlob, filename)).rejects.toThrow('není dostupné');
    (bridge as unknown as Record<string, unknown>).shareAccountDataFile = registeredShare;

    bridge.native = false;
    vi.resetModules();
    const webIdentity = await import('./session');
    request.mockResolvedValueOnce(Response.json(user('web-account')));
    await webIdentity.refreshIdentity();
    await expect(webIdentity.shareNativeAccountDataFile(validBlob, filename)).rejects.toThrow('pouze v nativní aplikaci');
    expect(request).toHaveBeenCalledTimes(1);
  });

  it('checks the caller context after reading and immediately before dispatch', async () => {
    const identity = await signedIn();
    const delayedBytes = deferred<ArrayBuffer>();
    const blob = new Blob(['{}'], {type: 'application/json'});
    vi.spyOn(blob, 'arrayBuffer').mockReturnValue(delayedBytes.promise);
    const check = vi.fn().mockImplementationOnce(() => {}).mockImplementationOnce(() => {
      throw new DOMException('Owner view changed', 'AbortError');
    });
    const pending = identity.shareNativeAccountDataFile(blob, filename, {check});
    const rejected = expect(pending).rejects.toMatchObject({name: 'AbortError'});
    delayedBytes.resolve(new TextEncoder().encode('{}').buffer);
    await rejected;
    expect(check).toHaveBeenCalledTimes(2);
    expect(bridge.shareAccountDataFile).not.toHaveBeenCalled();
  });

  it('does not dispatch if cancellation arrives while Blob bytes are being read', async () => {
    const identity = await signedIn();
    const delayedBytes = deferred<ArrayBuffer>();
    const blob = new Blob(['{}'], {type: 'application/json'});
    vi.spyOn(blob, 'arrayBuffer').mockReturnValue(delayedBytes.promise);
    const controller = new AbortController();
    const pending = identity.shareNativeAccountDataFile(blob, filename, {signal: controller.signal});
    const rejected = expect(pending).rejects.toMatchObject({name: 'AbortError'});
    controller.abort();
    delayedBytes.resolve(new TextEncoder().encode('{}').buffer);
    await rejected;
    expect(bridge.shareAccountDataFile).not.toHaveBeenCalled();
    expect(bridge.cancelAccountRequest).not.toHaveBeenCalled();
  });

  it('checks caller ownership at final bridge dispatch and rejects malformed outcomes without retry', async () => {
    const identity = await signedIn();
    let checks = 0;
    const check = () => {
      if (++checks === 4) throw new DOMException('Owner job changed', 'AbortError');
    };
    await expect(identity.shareNativeAccountDataFile(new Blob(['{}'], {type: 'application/json'}), filename, {check}))
      .rejects.toMatchObject({name: 'AbortError'});
    expect(bridge.shareAccountDataFile).not.toHaveBeenCalled();

    bridge.shareAccountDataFile.mockResolvedValueOnce({completed: 'yes'});
    await expect(identity.shareNativeAccountDataFile(new Blob(['{}'], {type: 'application/json'}), filename))
      .rejects.toThrow('neplatný výsledek');
    expect(bridge.shareAccountDataFile).toHaveBeenCalledTimes(1);
    expect(request).not.toHaveBeenCalled();
  });

  it('fences an authentication change away and back while Blob preparation is pending', async () => {
    const identity = await signedIn();
    bridge.passkey.mockResolvedValue(undefined);
    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('other'))})
      .mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    const delayedBytes = deferred<ArrayBuffer>();
    const blob = new Blob(['{}'], {type: 'application/json'});
    vi.spyOn(blob, 'arrayBuffer').mockReturnValue(delayedBytes.promise);
    const pending = identity.shareNativeAccountDataFile(blob, filename);
    const rejected = expect(pending).rejects.toMatchObject({name: 'AbortError'});
    await identity.authenticatePasskey('signin');
    await identity.authenticatePasskey('signin');
    expect(identity.account.value?.id).toBe('current');
    delayedBytes.resolve(new TextEncoder().encode('{}').buffer);
    await rejected;
    expect(bridge.shareAccountDataFile).not.toHaveBeenCalled();
  });

  it('cancels native sharing, rejects late account changes, and surfaces bridge failures', async () => {
    const identity = await signedIn();
    const delayedShare = deferred<{completed: boolean}>();
    bridge.shareAccountDataFile.mockReturnValueOnce(delayedShare.promise);
    const controller = new AbortController();
    const pending = identity.shareNativeAccountDataFile(new Blob(['{}'], {type: 'application/json'}), filename, {signal: controller.signal});
    await vi.waitFor(() => expect(bridge.shareAccountDataFile).toHaveBeenCalledTimes(1));
    const requestId = bridge.shareAccountDataFile.mock.calls[0]![0].requestId;
    controller.abort();
    expect(bridge.cancelAccountRequest).toHaveBeenCalledExactlyOnceWith({requestId});
    delayedShare.resolve({completed: true});
    await expect(pending).rejects.toMatchObject({name: 'AbortError'});

    let refreshCount = 0;
    bridge.request.mockImplementation(async ({method}: {method: string}) => {
      if (method === 'DELETE') return {status: 204, body: ''};
      refreshCount++;
      return {status: 200, body: JSON.stringify(user(refreshCount === 1 ? 'current' : null))};
    });
    await identity.refreshIdentity();
    const lateAccountChange = deferred<{completed: boolean}>();
    bridge.shareAccountDataFile.mockReturnValueOnce(lateAccountChange.promise);
    const accountChange = identity.shareNativeAccountDataFile(new Blob(['{}'], {type: 'application/json'}), filename);
    const accountChangeRejected = expect(accountChange).rejects.toMatchObject({name: 'AbortError'});
    await vi.waitFor(() => expect(bridge.shareAccountDataFile).toHaveBeenCalledTimes(2));
    await identity.signOut();
    lateAccountChange.resolve({completed: true});
    await accountChangeRejected;
    expect(bridge.accountTransport).not.toHaveBeenCalled();
    expect(request).not.toHaveBeenCalled();

    bridge.request.mockReset().mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    await signedIn();
    bridge.shareAccountDataFile.mockRejectedValueOnce(new Error('native share failed'));
    await expect(identity.shareNativeAccountDataFile(new Blob(['{}'], {type: 'application/json'}), filename))
      .rejects.toThrow('native share failed');
  });
});

describe('durable account deletion', () => {
  async function signedIn() {
    const identity = await import('./session');
    request.mockResolvedValueOnce(new Response(JSON.stringify(user('current'))));
    await identity.refreshIdentity();
    return identity;
  }

  it('requires a deletion-specific confirmation before sending a mutation', async () => {
    const identity = await signedIn();
    const confirmation = vi.fn().mockReturnValue(false); vi.stubGlobal('confirm', confirmation);
    await expect(identity.deleteAccount('current')).rejects.toThrow('nebyla potvrzena');
    expect(confirmation).toHaveBeenCalledWith(expect.stringContaining('Trvale smazat můj účet?'));
    expect(request).toHaveBeenCalledTimes(1);
    expect(identity.account.value?.id).toBe('current');
  });

  it('uses the selected language for the final native deletion confirmation', async () => {
    interfaceLanguagePreference.setScope(null);
    interfaceLanguagePreference.setLanguage('en');
    const identity = await signedIn();
    const confirmation = vi.fn().mockReturnValue(false); vi.stubGlobal('confirm', confirmation);
    await expect(identity.deleteAccount('current')).rejects.toThrow('not confirmed');
    expect(confirmation).toHaveBeenCalledWith(expect.stringContaining('Permanently delete my account?'));
    expect(identity.account.value?.id).toBe('current');
  });

  it.each([409, 500, 200])('retains identity for an unaccepted deletion response %s', async status => {
    const identity = await signedIn();
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    request.mockResolvedValueOnce(new Response(JSON.stringify({code: 'last_owner_required', status: 'deletion_requested'}), {status}));
    await expect(identity.deleteAccount('current')).rejects.toThrow();
    expect(identity.account.value?.id).toBe('current');
  });

  it('preserves identity on an unknown network outcome', async () => {
    const identity = await signedIn();
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    request.mockRejectedValueOnce(new TypeError('Connection lost'));
    await expect(identity.deleteAccount('current')).rejects.toThrow('Connection lost');
    expect(identity.account.value?.id).toBe('current');
  });

  it('clears local account authority and fences private requests only after accepted202', async () => {
    const identity = await signedIn();
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    const old = deferred<Response>();
    request.mockReturnValueOnce(old.promise);
    const pending = identity.accountRequest('/app/v1/account');
    const rejected = expect(pending).rejects.toMatchObject({name: 'AbortError'});
    request.mockResolvedValueOnce(new Response('{"status":"deletion_requested"}', {status: 202}));
    await identity.deleteAccount('current');
    expect(request.mock.calls.at(-1)?.[1]).toMatchObject({ method: 'DELETE', headers: {'X-DV-CSRF': 'current', 'X-DV-Confirm': 'yes'} });
    expect(identity.account.value).toBeNull();
    expect(identity.accountDeletionRequested.value).toBe(true);
    expect(identity.offlineSession.value).toBe(false);
    old.resolve(new Response('{}'));
    await rejected;
  });

  it('does not delete a different current account or clear a later sign-in', async () => {
    const identity = await signedIn();
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    await expect(identity.deleteAccount('other')).rejects.toMatchObject({name: 'AbortError'});
    expect(request).toHaveBeenCalledTimes(1);
    const deletion = deferred<Response>();
    request.mockReturnValueOnce(deletion.promise);
    const pending = identity.deleteAccount('current');
    const rejected = expect(pending).rejects.toMatchObject({name: 'AbortError'});
    request.mockResolvedValueOnce(new Response(JSON.stringify(user('other'))));
    await identity.refreshIdentity();
    deletion.resolve(new Response('{"status":"deletion_requested"}', {status: 202}));
    await rejected;
    expect(identity.account.value?.id).toBe('other');
    expect(identity.accountDeletionRequested.value).toBe(false);
  });

  it('reads an anonymous status receipt and silently accepts its absence', async () => {
    const identity = await import('./session');
    request.mockResolvedValueOnce(new Response('{"status":"deletion_requested"}'));
    expect(await identity.getAccountDeletionStatus()).toBe('deletion_requested');
    request.mockResolvedValueOnce(new Response('{"status":"deleted"}'));
    expect(await identity.getAccountDeletionStatus()).toBe('deleted');
    request.mockResolvedValueOnce(new Response('{}', {status: 404}));
    expect(await identity.getAccountDeletionStatus()).toBeNull();
    expect(request.mock.calls.every(call => call[0] === '/app/v1/account/deletion')).toBe(true);
    expect(identity.account.value).toBeNull();
  });

  it.each([202, 409])('uses the exact native deletion bridge and retains state unless accepted (%s)', async status => {
    bridge.native = true;
    const identity = await import('./session');
    bridge.request.mockResolvedValueOnce({status: 200, body: JSON.stringify(user('current'))});
    await identity.refreshIdentity();
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    bridge.request.mockResolvedValueOnce({status, body: JSON.stringify({status: 'deletion_requested', code: 'last_owner_required'})});
    const deletion = identity.deleteAccount('current');
    if (status === 202) await deletion;
    else await expect(deletion).rejects.toThrow('aktivní vlastník');
    expect(bridge.request.mock.calls.at(-1)![0]).toMatchObject({path: '/app/v1/account', method: 'DELETE', confirmed: true});
    expect(identity.account.value?.id ?? null).toBe(status === 202 ? null : 'current');
  });

  it('rejects an accepted response after the account changes away and back', async () => {
    const identity = await signedIn();
    vi.stubGlobal('confirm', vi.fn().mockReturnValue(true));
    const response = deferred<Response>();
    request.mockReturnValueOnce(response.promise);
    const operation = identity.deleteAccount('current');
    const rejected = expect(operation).rejects.toMatchObject({name: 'AbortError'});
    const previous = identity.account.value;
    identity.account.value = {id: 'other', displayName: 'Other', roles: ['Customer']};
    identity.account.value = previous;
    response.resolve(new Response('{"status":"deletion_requested"}', {status: 202}));
    await rejected;
    expect(identity.account.value?.id).toBe('current');
    expect(identity.accountDeletionRequested.value).toBe(false);
  });
});

describe('customer passkey enrollment', () => {
  it('uses the native customer-enroll contract without an invite token', async () => {
    bridge.native = true;
    bridge.passkey.mockResolvedValue(undefined);
    bridge.request.mockResolvedValue({ status: 200, body: JSON.stringify(user('new-customer')) });
    const identity = await import('./session');
    await identity.authenticatePasskey('customer-enroll', '', 'Guest');
    expect(bridge.passkey).toHaveBeenCalledExactlyOnceWith({ mode: 'customer-enroll', token: '', displayName: 'Guest' });
    expect(identity.account.value?.id).toBe('new-customer');
  });
});

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

  it('admits a bounded native offline snapshot and expires it without a network request', async () => {
    vi.useFakeTimers();
    try {
      bridge.native = true;
      const now = Date.now();
      bridge.request.mockResolvedValue({status: 200, body: JSON.stringify({ ...user('native-owner'), offline: true, expiresAt: (now + 5000) / 1000 })});
      const identity = await import('./session');
      await identity.refreshIdentity();
      expect(identity.offlineSession.value).toBe(true);
      expect(identity.account.value?.id).toBe('native-owner');
      vi.advanceTimersByTime(5001);
      expect(identity.account.value).toBeNull();
      expect(identity.offlineSession.value).toBe(false);
      expect(bridge.request).toHaveBeenCalledTimes(1);
    } finally { vi.useRealTimers(); }
  });

  it('never trusts an offline snapshot supplied to the browser', async () => {
    request.mockResolvedValue(new Response(JSON.stringify({ ...user('browser'), offline: true, expiresAt: (Date.now() + 5000) / 1000 })));
    const identity = await import('./session'); await identity.refreshIdentity();
    expect(identity.offlineSession.value).toBe(false);
  });

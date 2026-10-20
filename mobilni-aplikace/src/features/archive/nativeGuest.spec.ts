import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
const nativeBridge = vi.hoisted(() => ({ guestRequest: vi.fn(), guestCapabilities: vi.fn(), shareGuestFile: vi.fn(), shareGuestLink: vi.fn(), request: vi.fn(), accountTransport: vi.fn() }));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => true }, registerPlugin: () => nativeBridge }));
import { base64ToBytes, bytesToBase64, createArchiveClient, createNativeGuestTransport, guestOrigin, shareNativeGuestFile, validateGuestRequest } from './nativeGuest';
import KeepsakeView from './KeepsakeView.vue';
import { account } from '../identity/session';
const id = '11111111-1111-4111-8111-111111111111';
const encode = (value: object) => bytesToBase64(new TextEncoder().encode(JSON.stringify(value)));
beforeEach(() => { vi.resetAllMocks(); account.value = null; nativeBridge.guestCapabilities.mockResolvedValue({ available: true, origin: 'https://archive.example.test' }); });
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals(); });
describe('native guest transport shares real SFC contracts without account credentials', () => {
  it('bridges JSON and csrf/idempotency headers, then decodes a typed same-contract response', async () => {
    nativeBridge.guestRequest.mockImplementation(async ({ path }) => ({ status: 200, headers: { 'Content-Type': 'application/json' }, bodyBase64: encode(path.endsWith('/bootstrap') ? { schemaVersion: 1, csrfToken: 'guest-csrf' } : { locator: id }) }));
    const client = createArchiveClient();
    const result = await client.mutate<{ locator: string }>('/guest/v1/claim', { data: { token: 'a'.repeat(40) } });
    expect(result.locator).toBe(id);
    const claim = nativeBridge.guestRequest.mock.calls[1][0];
    expect(claim.headers).toMatchObject({ 'x-dv10-csrf': 'guest-csrf', 'content-type': 'application/json' });
    expect(claim.headers['idempotency-key']).toMatch(/^[a-f0-9-]{36}$/);
    expect(new TextDecoder().decode(base64ToBytes(claim.bodyBase64))).toContain('a'.repeat(40));
    expect(claim.path).toBe('/guest/v1/claim'); expect(JSON.stringify(claim.headers)).not.toContain('a'.repeat(40));
  });
  it('keeps uploads as bytes and preserves explicit portrait consent version', async () => {
    nativeBridge.guestRequest.mockResolvedValue({ status: 200, headers: { 'Content-Type': 'application/json' }, bodyBase64: encode({}) });
    const transport = createNativeGuestTransport(nativeBridge);
    await transport(`/guest/v1/keepsakes/${id}/portrait-uploads`, { method: 'POST', body: new Blob(['synthetic image bytes'], { type: 'image/png' }), headers: { 'X-DV10-Consent': '3', 'Content-Type': 'image/png' } });
    const request = nativeBridge.guestRequest.mock.calls[0][0];
    expect(request.headers['x-dv10-consent']).toBe('3'); expect(new TextDecoder().decode(base64ToBytes(request.bodyBase64))).toBe('synthetic image bytes');
  });
  it('rejects account, external, traversal, query capabilities and wrong-method requests before native calls', async () => {
    const transport = createNativeGuestTransport(nativeBridge);
    for (const path of ['/app/v1/me', 'https://evil.test/guest/v1/bootstrap', '/guest/v1/../app/v1/me', '/guest/v1/evening?token=private']) await expect(transport(path)).rejects.toThrow();
    expect(() => validateGuestRequest(`/guest/v1/keepsakes/${id}/portrait-uploads`, 'GET')).toThrow();
    expect(validateGuestRequest(`/guest/v1/keepsakes/${id}/account-associations`, 'POST')).toBe(`/guest/v1/keepsakes/${id}/account-associations`);
    expect(() => validateGuestRequest(`/guest/v1/keepsakes/${id}/account-associations`, 'GET')).toThrow();
    expect(() => validateGuestRequest(`/guest/v1/keepsakes/${id}/account-associations`, 'DELETE')).toThrow();
    await expect(transport('/guest/v1/bootstrap', { method: 'POST', headers: { Authorization: 'Bearer device-secret' } })).rejects.toThrow();
    expect(nativeBridge.guestRequest).not.toHaveBeenCalled();
  });
  it('allows only the exact public Show reads, owner identity action and isolated scoped duel choice header', async () => {
    nativeBridge.guestRequest.mockResolvedValue({ status: 200, headers: { 'content-type': 'application/json' }, bodyBase64: encode({}) });
    const transport = createNativeGuestTransport(nativeBridge);
    await transport('/guest/v1/chronicle', { credentials: 'omit' });
    await transport(`/guest/v1/duels/${id}`, { credentials: 'omit' });
    await transport(`/guest/v1/duels/${id}/choices`, { method: 'POST', credentials: 'omit', headers: { Authorization: 'Bearer ' + 'x'.repeat(43), 'Content-Type': 'application/json' }, body: JSON.stringify({ side: 'left', round: 1, revision: 1, action: 'strike', operationKey: id }) });
    expect(nativeBridge.guestRequest.mock.calls[2][0].headers.authorization).toBe('Bearer ' + 'x'.repeat(43));
    expect(validateGuestRequest(`/guest/v1/keepsakes/${id}/wizard-identity`, 'POST')).toContain('/wizard-identity');
    for (const [path, method] of [['/guest/v1/chronicle', 'POST'], [`/guest/v1/duels/${id}`, 'DELETE'], [`/guest/v1/duels/${id}/choices`, 'GET'], [`/guest/v1/duels/${id}?grant=secret`, 'GET']]) expect(() => validateGuestRequest(path, method)).toThrow();
    await expect(transport('/guest/v1/chronicle', { headers: { Authorization: 'Bearer ' + 'x'.repeat(43) } })).rejects.toThrow();
    await expect(transport(`/guest/v1/duels/${id}/choices`, { method: 'POST', headers: { Authorization: 'Bearer account-token' } })).rejects.toThrow();
    await expect(transport(`/guest/v1/duels/${id}/choices`, { method: 'POST', headers: { 'X-DV10-CSRF': 'owner-session' } })).rejects.toThrow();
    expect(nativeBridge.guestRequest).toHaveBeenCalledTimes(3);
  });
  it('rejects late native binary responses after route invalidation', async () => {
    let complete!: (value: object) => void;
    nativeBridge.guestRequest.mockReturnValue(new Promise(resolve => complete = resolve));
    const client = createArchiveClient(); const downloading = client.blob(`/guest/v1/keepsakes/${id}/assets/${id}`, ['image/png']);
    client.invalidate(); complete({ status: 200, headers: { 'Content-Type': 'image/png' }, bodyBase64: btoa('private bytes') });
    await expect(downloading).rejects.toMatchObject({ name: 'AbortError' });
  });
  it('validates the configured hosted origin and bounds native sharing by validated media and filename', async () => {
    expect(await guestOrigin()).toBe('https://archive.example.test');
    nativeBridge.guestCapabilities.mockResolvedValue({ available: true, origin: 'http://unsafe.test' }); await expect(guestOrigin()).rejects.toThrow();
    await shareNativeGuestFile(new File(['synthetic'], 'wand-art.png', { type: 'image/jpeg' }));
    expect(nativeBridge.shareGuestFile).toHaveBeenCalledWith({ filename: 'wand-art.jpg', mediaType: 'image/jpeg', bodyBase64: btoa('synthetic') });
    await expect(shareNativeGuestFile(new File(['html'], 'wand.png', { type: 'text/html' }))).rejects.toThrow();
    await expect(shareNativeGuestFile(new File(['synthetic'], '../wand.png', { type: 'image/png' }))).rejects.toThrow();
  });
  it('the real native keepsake SFC exchanges a claim and exports through the guest bridge and activity share', async () => {
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new Event('close')); };
    URL.createObjectURL = vi.fn(() => 'blob:native-fixture'); URL.revokeObjectURL = vi.fn();
    const browserFetch = vi.fn(() => { throw new Error('Native guest must not use browser fetch'); }); vi.stubGlobal('fetch', browserFetch);
    window.__dildovanderGuestClaim = 'c=' + 'a'.repeat(40);
    const view = { schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false, snapshot: { officialName: 'Native fixture wand', slot: 2 },
      editions: [{ id, story: { title: 'Native fixture story', text: 'Synthetic fixture story.' }, synthetic: true, publicationState: 'ready' }], portraits: [], jobs: [], assets: [] };
    nativeBridge.guestRequest.mockImplementation(async ({ path }) => {
      if (path.includes('/assets/')) return { status: 200, headers: { 'content-type': 'text/plain' }, bodyBase64: btoa('Synthetic exported story') };
      const result = path.endsWith('/bootstrap') ? { schemaVersion: 1, csrfToken: 'native-csrf' }
        : path.endsWith('/claim') ? { locator: id } : path.endsWith('/exports') ? { asset: { id } } : view;
      return { status: 200, headers: { 'content-type': 'application/json' }, bodyBase64: encode(result) };
    });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/keepsake/', component: KeepsakeView }, { path: '/archive', component: { template: '<p>Archive</p>' } }] });
    await router.push('/keepsake/'); await router.isReady();
    const wrapper = mount(KeepsakeView, { attachTo: document.body, global: { plugins: [router] } });
    try {
      await flushPromises(); expect(nativeBridge.guestRequest).not.toHaveBeenCalled();
      await wrapper.findAll('button').find(button => button.text() === 'Otevřít moji památku')!.trigger('click'); await flushPromises();
      expect(wrapper.text()).toContain('Native fixture story'); expect(router.currentRoute.value.query.k).toBe(id);
      await wrapper.findAll('button').find(button => button.text() === 'Příběh TXT')!.trigger('click'); await flushPromises();
      expect(wrapper.find('a[download]').exists()).toBe(false);
      await wrapper.findAll('button').find(button => button.text() === 'Sdílet soubor')!.trigger('click'); await flushPromises();
      expect(nativeBridge.shareGuestFile).toHaveBeenCalledWith({ filename: 'story.txt', mediaType: 'text/plain', bodyBase64: btoa('Synthetic exported story') });
      expect(browserFetch).not.toHaveBeenCalled();
    } finally { wrapper.unmount(); delete window.__dildovanderGuestClaim; }
  });
  it('native association explicitly crosses from the guest owner jar to the matching account jar without browser fetch', async () => {
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
    HTMLDialogElement.prototype.showModal = function () { this.open = true; };
    HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new Event('close')); };
    account.value = { id: 'native-account', displayName: 'Native account', roles: ['Viewer'] };
    const browserFetch = vi.fn(() => { throw new Error('Native association must not use browser fetch'); }); vi.stubGlobal('fetch', browserFetch);
    const view = { schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false, snapshot: { officialName: 'Native owner wand', slot: 1 }, editions: [], portraits: [], jobs: [], assets: [] };
    nativeBridge.guestRequest.mockImplementation(async ({ path }) => ({ status: 200, headers: { 'content-type': 'application/json' }, bodyBase64: encode(path.endsWith('/bootstrap')
      ? { schemaVersion: 1, csrfToken: 'native-guest-csrf' } : path.endsWith('/account-associations') ? { ticket: 'native-association-ticket' } : view) }));
    nativeBridge.request.mockResolvedValue({ status: 200, body: '{}' });
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/keepsake/', component: KeepsakeView }, { path: '/archive', component: { template: '<p>Archive</p>' } }] });
    await router.push(`/keepsake/?k=${id}`); await router.isReady();
    const wrapper = mount(KeepsakeView, { attachTo: document.body, global: { plugins: [router] } });
    try {
      await flushPromises(); await wrapper.findAll('button').find(button => button.text() === 'Připojit k mému účtu')!.trigger('click');
      expect(nativeBridge.request).not.toHaveBeenCalled();
      await wrapper.findAll('button').find(button => button.text() === 'Potvrdit připojení včetně soukromých portrétů')!.trigger('click'); await flushPromises();
      const proof = nativeBridge.guestRequest.mock.calls.find(([request]) => request.path.endsWith('/account-associations'))![0];
      expect(JSON.parse(new TextDecoder().decode(base64ToBytes(proof.bodyBase64)))).toMatchObject({ targetAccountId: 'native-account' });
      expect(nativeBridge.request).toHaveBeenCalledWith(expect.objectContaining({ path: '/app/v1/archive/associations', method: 'POST', body: JSON.stringify({ ticket: 'native-association-ticket' }) }));
      expect(browserFetch).not.toHaveBeenCalled();
    } finally { wrapper.unmount(); }
  });
});

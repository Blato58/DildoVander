import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import ArchiveView from './ArchiveView.vue';
import KeepsakeView from './KeepsakeView.vue';
import { account, offlineSession } from '../identity/session';
const entry = vi.hoisted(() => ({ value: null as string | null }));
vi.mock('../../application/guestClaim', () => ({ consumeGuestClaim: () => { const result = entry.value; entry.value = null; return result; } }));
const id = '11111111-1111-4111-8111-111111111111';
const secondId = '22222222-2222-4222-8222-222222222222';
const view = { schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false,
  snapshot: { officialName: '<script>catalog</script>', slot: 1 },
  editions: [{ id, story: { title: 'Fiction edition', text: 'One paragraph.\n\nAnother paragraph.' }, synthetic: true, publicationState: 'ready' }],
  portraits: [], jobs: [], assets: [] };
const ownerProjection = { ...view, readOnly: false, accountLinked: true, accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false, uploads: [], prophecies: [] };
const linkedProjection = { ...view, readOnly: true, accountLinked: true, accessKind: 'linkedViewer', canManage: false, canReadPrivate: false, canUnlink: true, uploads: [], prophecies: [] };
const partialReadiness = { state: 'partial', reason: 'reconciliation_required', canRetry: false, shouldPoll: true, hasStory: true, hasArtwork: false };
const readyReadiness = { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true };
let wrappers: VueWrapper[] = [];
async function render(component: typeof ArchiveView | typeof KeepsakeView, location: string) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/archive', component: ArchiveView }, { path: '/keepsake/', component: KeepsakeView }, { path: '/other', component: { template: '<p>other</p>' } }] });
  await router.push(location); await router.isReady();
  const wrapper = mount(component, { attachTo: document.body, global: { plugins: [router] } }); wrappers.push(wrapper); await flushPromises();
  return { wrapper, router };
}
beforeEach(() => {
  entry.value = null;
  account.value = null;
  offlineSession.value = false;
  vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_: string, action: () => Promise<unknown>) => action() } });
  URL.createObjectURL = vi.fn(() => 'blob:fixture'); URL.revokeObjectURL = vi.fn();
  HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new Event('close')); };
});
afterEach(() => { for (const wrapper of wrappers) wrapper.unmount(); wrappers = []; offlineSession.value = false; vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); document.body.innerHTML = ''; });
describe('real archive SFC routes', () => {
  it('public route only reads a credential-free gallery, renders escaped identity and never grants private tools', async () => {
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json({ schemaVersion: 1, evening: { id }, latest: null,
      items: [{ locator: id, editionId: id, officialName: '<script>catalog</script>', slot: 1, synthetic: true, story: { title: 'Edition', text: 'Story' }, assets: [] }] }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(ArchiveView, '/archive');
    expect(wrapper.text()).toContain('<script>catalog</script>'); expect(wrapper.find('script').exists()).toBe(false);
    expect(wrapper.text()).toContain('SYNTETICKÁ UKÁZKA'); expect(wrapper.find('input[type=file]').exists()).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1); expect(fetchMock.mock.calls[0][1]).toMatchObject({ credentials: 'omit', cache: 'no-store', redirect: 'error' });
  });
  it('a captured claim waits for explicit action and exchanges body token without a query secret', async () => {
    entry.value = '#c=' + 'a'.repeat(40);
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => Response.json(path.endsWith('bootstrap') ? { schemaVersion: 1, csrfToken: 'csrf' } : path.endsWith('/claim') ? { locator: id } : view));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper, router } = await render(KeepsakeView, '/keepsake/');
    expect(fetchMock).not.toHaveBeenCalled();
    await wrapper.findAll('button').find(button => button.text() === 'Otevřít moji památku')!.trigger('click'); await flushPromises();
    expect(router.currentRoute.value.query).toEqual({ k: id }); expect(wrapper.text()).toContain('Fiction edition');
    const exchange = fetchMock.mock.calls.find(([path]) => path.endsWith('/claim'))!;
    expect(exchange[0]).not.toContain('a'.repeat(40)); expect(JSON.parse(exchange[1]!.body as string)).toMatchObject({ token: 'a'.repeat(40) });
    expect(document.head.querySelector('meta[name=robots]')?.getAttribute('content')).toContain('noindex');
  });
  it('share route remains bound to its edition and has no owner, photo, history or deletion actions', async () => {
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json({ ...view, readOnly: false, sharedEditionId: id })); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}&view=share&editionId=${id}`);
    expect(fetchMock.mock.calls[0][0]).toBe(`/guest/v1/keepsakes/${id}?view=share&editionId=${id}`);
    expect(wrapper.find('input[type=file]').exists()).toBe(false); expect(wrapper.text()).not.toContain('Odvolat všechny přístupy');
    expect(wrapper.text()).not.toContain('Smazat celou památku'); expect(wrapper.text()).toContain('pouze čtení');
  });
  it('keeps an unexchanged in-memory claim across app switching after its fragment was removed', async () => {
    entry.value = '#r=' + 'a'.repeat(40);
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => Response.json(path.endsWith('bootstrap') ? { schemaVersion: 1, csrfToken: 'csrf' } : path.endsWith('/recover') ? { locator: id } : view));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, '/keepsake/');
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true); document.dispatchEvent(new Event('visibilitychange')); await flushPromises();
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false); document.dispatchEvent(new Event('visibilitychange')); await flushPromises();
    expect(fetchMock).not.toHaveBeenCalled(); expect(wrapper.text()).toContain('Obnovit můj přístup');
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit můj přístup')!.trigger('click'); await flushPromises();
    expect(fetchMock.mock.calls.some(([path]) => path.endsWith('/recover'))).toBe(true); expect(wrapper.text()).toContain('Fiction edition');
  });
  it('reuses shell noindex metadata without duplicating or removing its ownership', async () => {
    const robots = document.createElement('meta'); robots.name = 'robots'; robots.content = 'noindex, nofollow, noarchive'; document.head.append(robots);
    vi.stubGlobal('fetch', vi.fn());
    try {
      const { wrapper } = await render(KeepsakeView, '/keepsake/');
      expect(document.head.querySelectorAll('meta[name=robots]')).toHaveLength(1);
      wrapper.unmount(); wrappers = wrappers.filter(item => item !== wrapper);
      expect(document.head.querySelector('meta[name=robots]')).toBe(robots);
    } finally { robots.remove(); }
  });
  it('photo selection stays local and route departure during consent prevents upload and generation', async () => {
    let resolveConsent!: (value: Response) => void;
    const consent = new Promise<Response>(resolve => resolveConsent = resolve);
    const fetchMock = vi.fn(async (path: string) => {
      if (path.endsWith('bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'csrf' });
      if (path.endsWith('portrait-consents')) return consent;
      return Response.json(view);
    }); vi.stubGlobal('fetch', fetchMock);
    const { wrapper, router } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    const input = wrapper.find('input[type=file]'); const photo = new File(['synthetic fixture'], 'synthetic.png', { type: 'image/png' });
    Object.defineProperty(input.element, 'files', { configurable: true, value: [photo] }); await input.trigger('change');
    expect(fetchMock).toHaveBeenCalledTimes(1); expect(wrapper.find('img[alt="Místní náhled vybrané fotografie"]').exists()).toBe(true);
    const checkbox = wrapper.findAll('input[type=checkbox]').at(-1)!; await checkbox.setValue(true);
    await wrapper.findAll('button').find(button => button.text() === 'Odeslat fotografii a vytvořit portrét')!.trigger('click'); await flushPromises();
    expect(fetchMock.mock.calls.some(([path]) => path.endsWith('portrait-consents'))).toBe(true);
    await router.push(`/keepsake/?k=${secondId}`); await flushPromises(); resolveConsent(Response.json({ consentVersion: 1 })); await flushPromises();
    expect(fetchMock.mock.calls.some(([path]) => path.endsWith('portrait-uploads') || path.endsWith('/portraits'))).toBe(false);
    expect(wrapper.find('img[alt="Místní náhled vybrané fotografie"]').exists()).toBe(false); expect(URL.revokeObjectURL).toHaveBeenCalled();
  });
  it('only an explicit consented portrait action uploads and binds generation to that edition and consent', async () => {
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => {
      if (path.endsWith('bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'csrf' });
      if (path.endsWith('portrait-consents')) return Response.json({ consentVersion: 7 });
      if (path.endsWith('portrait-uploads')) return Response.json({ upload: { id: secondId } });
      if (path.endsWith('/portraits')) return Response.json({});
      return Response.json(view);
    }); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    const input = wrapper.find('input[type=file]'); const file = new File(['synthetic fixture'], 'fixture.png', { type: 'image/png' });
    Object.defineProperty(input.element, 'files', { configurable: true, value: [file] }); await input.trigger('change');
    const create = wrapper.findAll('button').find(button => button.text() === 'Odeslat fotografii a vytvořit portrét')!;
    expect(create.attributes('disabled')).toBeDefined();
    await wrapper.findAll('input[type=checkbox]').at(-1)!.setValue(true); expect(fetchMock).toHaveBeenCalledTimes(1);
    await create.trigger('click'); await flushPromises();
    const upload = fetchMock.mock.calls.find(([path]) => path.endsWith('portrait-uploads'))!;
    expect(upload[1]?.body).toBe(file); expect(upload[1]?.headers).toMatchObject({ 'X-DV10-Consent': '7', 'Content-Type': 'image/png', 'X-DV10-CSRF': 'csrf' });
    const generation = fetchMock.mock.calls.find(([path]) => path.endsWith('/portraits'))!;
    expect(JSON.parse(generation[1]!.body as string)).toMatchObject({ uploadId: secondId, editionId: id, consentVersion: 7, variant: 'portrait_only' });
    expect(wrapper.find('img[alt="Místní náhled vybrané fotografie"]').exists()).toBe(false);
  });
  it('manual deletion requires the literal confirmation and keeps provider and backup uncertainty separate', async () => {
    const fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
      if (path.endsWith('bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'csrf' });
      if (init?.method === 'DELETE') return Response.json({ receipt: { id: secondId, scope: 'all', localState: 'completed', providerState: 'unknown_not_provider_confirmed', backupState: 'pending_independent_backup_reconciliation' } });
      return Response.json(view);
    }); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Smazat celou památku')!.trigger('click');
    const confirm = wrapper.findAll('button').find(button => button.text() === 'Potvrdit smazání')!;
    expect(confirm.attributes('disabled')).toBeDefined(); expect(fetchMock).toHaveBeenCalledTimes(1);
    await wrapper.find('#archive-delete-confirm').setValue('SMAZAT'); await confirm.trigger('click'); await flushPromises();
    const deletion = fetchMock.mock.calls.find(([, init]) => init?.method === 'DELETE')!;
    expect(JSON.parse(deletion[1]!.body as string)).toMatchObject({ confirm: 'delete', expectedRevision: 1 });
    expect(wrapper.text()).toContain('odstranění poskytovatelem nepotvrzeno'); expect(wrapper.text()).toContain('čeká na nezávislé odstranění záloh');
    expect(wrapper.find('input[type=file]').exists()).toBe(false); expect(wrapper.text()).not.toContain('Fiction edition');
  });
  it('switching the public selected edition rejects a late export even when the transport ignores abort', async () => {
    let resolveFile!: (blob: Blob) => void;
    const file = new Promise<Blob>(resolve => resolveFile = resolve);
    const item = (locator: string, officialName: string) => ({ locator, editionId: locator, officialName, story: { title: 'Edition', text: 'Story' }, assets: [{ id: locator, kind: 'export_story', mediaType: 'text/plain' }] });
    const fetchMock = vi.fn(async (path: string) => path.endsWith('/evening')
      ? Response.json({ schemaVersion: 1, evening: { id }, latest: null, items: [item(id, 'First wand'), item(secondId, 'Second wand')] })
      : { ok: true, status: 200, blob: () => file });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(ArchiveView, '/archive');
    await wrapper.findAll('button').find(button => button.text() === 'Příběh TXT')!.trigger('click'); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Second wand')!.trigger('click');
    resolveFile(new Blob(['first private body'], { type: 'text/plain' })); await flushPromises();
    expect(wrapper.find('a[download]').exists()).toBe(false); expect(URL.createObjectURL).not.toHaveBeenCalled();
    expect(wrapper.find('#public-wand-name').text()).toBe('Second wand');
  });
  it('refresh keeps an explicitly selected older page and offers return to the latest index', async () => {
    const item = (locator: string, officialName: string) => ({ locator, editionId: locator, officialName, story: { title: 'Edition', text: 'Story' }, assets: [] });
    const fetchMock = vi.fn(async (path: string) => Response.json({ schemaVersion: 1, evening: { id }, latest: item(id, 'Latest wand'),
      items: path.includes('cursor=older') ? [item(secondId, 'Earlier wand')] : [item(id, 'Latest wand')], nextCursor: path.includes('cursor=older') ? null : 'older' }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(ArchiveView, '/archive');
    await wrapper.findAll('button').find(button => button.text() === 'Načíst další hůlky')!.trigger('click'); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Earlier wand')!.trigger('click');
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit přehled')!.trigger('click'); await flushPromises();
    expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('/guest/v1/evening?cursor=older'); expect(wrapper.find('#public-wand-name').text()).toBe('Earlier wand');
    await wrapper.findAll('button').find(button => button.text() === 'Zpět na nejnovější přehled')!.trigger('click'); await flushPromises();
    expect(wrapper.find('#public-wand-name').text()).toBe('Latest wand'); expect(fetchMock.mock.calls.at(-1)?.[0]).toBe('/guest/v1/evening');
  });
  it('linking requires an explicit owner confirmation including private portraits, then consumes the targeted ticket through the account API', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => {
      if (path.endsWith('/bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'guest-csrf' });
      if (path.endsWith('/account-associations')) return Response.json({ schemaVersion: 1, ticket: 'association-secret' });
      if (path === '/app/v1/archive/associations') return Response.json({ linked: true });
      return Response.json(view);
    }); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await wrapper.findAll('button').find(button => button.text() === 'Připojit k mému účtu')!.trigger('click'); await flushPromises();
    expect(fetchMock).toHaveBeenCalledTimes(1); expect(wrapper.text()).toContain('Účet Current account získá přístup');
    await wrapper.findAll('button').find(button => button.text() === 'Potvrdit připojení včetně soukromých portrétů')!.trigger('click'); await flushPromises();
    const proof = fetchMock.mock.calls.find(([path]) => path.endsWith('/account-associations'))!;
    expect(JSON.parse(proof[1]!.body as string)).toMatchObject({ targetAccountId: 'account-a' });
    const consume = fetchMock.mock.calls.find(([path]) => path === '/app/v1/archive/associations')!;
    expect(JSON.parse(consume[1]!.body as string)).toEqual({ ticket: 'association-secret' });
    expect(wrapper.text()).toContain('byly připojeny k tomuto účtu'); expect(wrapper.find('input[type=file]').exists()).toBe(true);
  });
  it('associated account reads private portrait results without guest mutations or raw photo upload controls', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Owner'] };
    const linked = { ...linkedProjection, portraits: [{ id: secondId, editionId: id, assetId: secondId, synthetic: true }], assets: [{ id: secondId, kind: 'portrait_image', mediaType: 'image/png' }] };
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => path.includes('/assets/') ? new Response(new Blob(['synthetic portrait'], { type: 'image/png' })) : Response.json(linked));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(fetchMock.mock.calls.every(([path]) => path.startsWith('/app/v1/archive/associations/'))).toBe(true);
    expect(wrapper.text()).toContain('Propojená památka · pouze čtení'); expect(wrapper.find('img[alt="Soukromý kouzelnický portrét"]').exists()).toBe(true);
    expect(wrapper.find('input[type=file]').exists()).toBe(false); expect(wrapper.text()).not.toContain('Smazat celou památku'); expect(wrapper.text()).not.toContain('Připojit k mému účtu');
    expect(wrapper.text()).toContain('Odpojit tuto památku od účtu');
  });
  it('account original owner gets private owner controls and share revocation targets only the owned route', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const owner = { ...ownerProjection, prophecies: [{ editionId: id, state: 'revealed', unlockAt: 'past', revelation: 'PRIVATE ACCOUNT OWNER PROPHECY' }] };
    let deleteAttempts = 0;
    const fetchMock = vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') return ++deleteAttempts === 1 ? Response.json({}, { status: 503 }) : new Response(null, { status: 204 });
      return Response.json(owner);
    });
    vi.stubGlobal('fetch', fetchMock);
    vi.stubGlobal('confirm', vi.fn(() => true));
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.text()).toContain('Soukromá památka · vlastník účtu');
    expect(wrapper.text()).toContain('PRIVATE ACCOUNT OWNER PROPHECY');
    expect(wrapper.find('input[type=file]').exists()).toBe(true);
    await wrapper.findAll('button').find(button => button.text() === 'Odvolat sdílené odkazy')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('Výsledek akce ještě nemáme potvrzený.');
    await wrapper.findAll('button').find(button => button.text() === 'Odvolat sdílené odkazy')!.trigger('click'); await flushPromises();
    const revocations = fetchMock.mock.calls.filter(([path, init]) => path === `/app/v1/archive/owned/${id}/share-links` && init?.method === 'DELETE');
    expect(revocations).toHaveLength(2);
    const headers = new Headers(revocations[0][1]?.headers);
    expect(headers.get('X-DV-CSRF')).toBe('');
    expect(headers.get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/i);
    expect(headers.get('Idempotency-Key')).toBe(new Headers(revocations[1][1]?.headers).get('Idempotency-Key'));
    expect(fetchMock.mock.calls.some(([path]) => String(path).startsWith('/guest/v1/'))).toBe(false);
    expect(wrapper.text()).toContain('Sdílené odkazy byly odvolány. Památka zůstává uložená.');
  });
  it('account owner recovery-link button sends a POST with its JSON body and idempotency key', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const fetchMock = vi.fn(async (path: string, init?: RequestInit) => path.endsWith('/recovery-links')
      ? Response.json({ url: `${location.origin}/keepsake/#r=${'r'.repeat(40)}` })
      : Response.json(ownerProjection));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click'); await flushPromises();
    const create = fetchMock.mock.calls.find(([path]) => path === `/app/v1/archive/owned/${id}/recovery-links`);
    expect(create?.[1]?.method).toBe('POST');
    expect(JSON.parse(create?.[1]?.body as string)).toEqual({ schemaVersion: 1 });
    expect(new Headers(create?.[1]?.headers).get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/i);
    expect(wrapper.find('#archive-issued-link').exists()).toBe(true);
  });
  it('account owner deletion requires the distinct confirmation and submits to the owned delete endpoint', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const receipt = { id: secondId, scope: 'all', localState: 'pending', providerState: 'unknown', backupState: 'pending' };
    const fetchMock = vi.fn(async (_path: string, init?: RequestInit) => init?.method === 'DELETE'
      ? Response.json({ schemaVersion: 1, receipt }, { status: 202 }) : Response.json(ownerProjection));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Smazat celou památku')!.trigger('click');
    const confirm = wrapper.findAll('button').find(button => button.text() === 'Potvrdit smazání')!;
    expect(confirm.attributes('disabled')).toBeDefined();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(false);
    await wrapper.find('#archive-delete-confirm').setValue('SMAZAT'); await confirm.trigger('click'); await flushPromises();
    const deletion = fetchMock.mock.calls.find(([path, init]) => path === `/app/v1/archive/owned/${id}` && init?.method === 'DELETE');
    expect(deletion).toBeDefined();
    expect(JSON.parse(deletion?.[1]?.body as string)).toMatchObject({ schemaVersion: 1, confirm: 'delete', expectedRevision: 1 });
    expect(new Headers(deletion?.[1]?.headers).get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/i);
    expect(fetchMock.mock.calls.some(([path]) => String(path).startsWith('/guest/v1/'))).toBe(false);
    expect(wrapper.text()).toContain('Požadavek na smazání');
  });
  it('linked viewer confirmation only removes the account association and preserves the content boundary', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const fetchMock = vi.fn(async (_path: string, init?: RequestInit) => init?.method === 'DELETE' ? new Response(null, { status: 204 }) : Response.json(linkedProjection));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Odpojit tuto památku od účtu')!.trigger('click');
    expect(wrapper.text()).toContain('Obsah, uložené soubory a návštěvnický přístup původního vlastníka zůstanou zachované.');
    const confirm = wrapper.findAll('button').find(button => button.text() === 'Potvrdit odpojení od účtu')!;
    expect(confirm.attributes('disabled')).toBeDefined();
    await wrapper.find('#archive-unlink-confirm').setValue('ODPOJIT'); await confirm.trigger('click'); await flushPromises();
    const unlink = fetchMock.mock.calls.find(([path, init]) => path === `/app/v1/archive/associations/${id}` && init?.method === 'DELETE');
    expect(unlink).toBeDefined();
    expect(new Headers(unlink?.[1]?.headers).get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/i);
    expect(fetchMock.mock.calls.some(([path, init]) => String(path).endsWith('/portrait') && init?.method === 'DELETE')).toBe(false);
    expect(wrapper.text()).toContain('Památka byla odpojena od tohoto účtu.');
  });
  it('a mismatched account capability response fails closed even when the signed-in role is Owner', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Owner'] };
    const fetchMock = vi.fn(async (_path: string) => Response.json({ ...linkedProjection, canManage: true }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.text()).not.toContain('Fiction edition');
    expect(wrapper.text()).not.toContain('Smazat celou památku');
    expect(wrapper.find('input[type=file]').exists()).toBe(false);
    expect(fetchMock.mock.calls.every(([path]) => String(path).startsWith('/app/v1/archive/associations/'))).toBe(true);
  });
  it('account switching during an owner mutation ignores the old response and clears the old private view', async () => {
    account.value = { id: 'account-a', displayName: 'First account', roles: ['Viewer'] };
    let resolveDelete!: (response: Response) => void;
    const pendingDelete = new Promise<Response>(resolve => resolveDelete = resolve);
    let reads = 0;
    const fetchMock = vi.fn(async (_path: string, init?: RequestInit) => {
      if (init?.method === 'DELETE') return pendingDelete;
      reads++;
      return reads === 1 ? Response.json(ownerProjection) : new Response('', { status: 404 });
    });
    vi.stubGlobal('fetch', fetchMock);
    vi.stubGlobal('confirm', vi.fn(() => true));
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.text()).toContain('Fiction edition');
    await wrapper.findAll('button').find(button => button.text() === 'Odvolat sdílené odkazy')!.trigger('click'); await flushPromises();
    expect(fetchMock.mock.calls.some(([, init]) => init?.method === 'DELETE')).toBe(true);
    account.value = { id: 'account-b', displayName: 'Second account', roles: ['Owner'] }; await flushPromises();
    expect(wrapper.text()).not.toContain('Fiction edition');
    resolveDelete(new Response(null, { status: 204 })); await flushPromises();
    expect(wrapper.text()).not.toContain('Sdílené odkazy byly odvolány.');
    expect(wrapper.text()).not.toContain('Testovací hůlka');
  });
  it('renders reconciliation readiness without replacing the partial story or exposing a retry action', async () => {
    const fetchMock = vi.fn(async () => Response.json({ ...view, readiness: partialReadiness }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    const notice = wrapper.get('[role="status"][aria-live="polite"][data-readiness-state="partial"]');
    expect(notice.text()).toBe('Příběh je připravený, výsledek obrazu ještě ověřujeme. Nové generování nespouštíme.');
    expect(wrapper.text()).toContain('One paragraph.');
    expect(wrapper.findAll('button').some(button => /Zkusit znovu|Opakovat generování|Znovu vytvořit/.test(button.text()))).toBe(false);
  });
  it('renders and polls safe readiness for linked viewers while stripping private jobs, uploads and prophecy', async () => {
    vi.useFakeTimers();
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const linked = { ...linkedProjection, readiness: partialReadiness, jobs: [], uploads: [], prophecies: [{ editionId: id, state: 'revealed', revelation: 'PRIVATE_LINKED_PROPHECY' }] };
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json(linked));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.get('[role="status"][aria-live="polite"][data-readiness-state="partial"]').text()).toContain('výsledek obrazu ještě ověřujeme');
    expect(wrapper.text()).toContain('One paragraph.');
    expect(wrapper.text()).not.toContain('PRIVATE_LINKED_PROPHECY');
    expect(wrapper.find('input[type=file]').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Smazat celou památku');
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    const archiveReads = fetchMock.mock.calls.filter(([path, init]) => String(path).startsWith('/app/v1/archive/') && (init?.method ?? 'GET') === 'GET');
    expect(archiveReads).toHaveLength(2);
    expect(fetchMock.mock.calls.every(([, init]) => (init?.method ?? 'GET') === 'GET')).toBe(true);
  });
  it.each([
    { label: 'visitor owner', accountMode: false, projection: view },
    { label: 'original account owner', accountMode: true, projection: ownerProjection },
  ])('continues bounded readiness polling for a locked prophecy on a ready $label archive', async ({ accountMode, projection }) => {
    vi.useFakeTimers();
    if (accountMode) account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const locked = { editionId: id, state: 'locked', unlockAt: '2099-01-01T00:00:00Z', revelation: 'PRIVATE_FUTURE_TEXT' };
    const revealed = { editionId: id, state: 'revealed', unlockAt: '2099-01-01T00:00:00Z', revelation: 'Server revealed this prophecy.' };
    let reads = 0;
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => {
      reads++;
      return Response.json({ ...projection, readiness: readyReadiness, prophecies: [reads === 1 ? locked : revealed] });
    });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?${accountMode ? 'account=1&' : ''}k=${id}`);
    expect(wrapper.text()).not.toContain('PRIVATE_FUTURE_TEXT');
    expect(wrapper.text()).toContain('Plné znění se odemkne');
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(reads).toBe(2);
    expect(wrapper.text()).toContain('Server revealed this prophecy.');
    await vi.advanceTimersByTimeAsync(15000); await flushPromises();
    expect(reads).toBe(2);
    expect(fetchMock.mock.calls.every(([, init]) => (init?.method ?? 'GET') === 'GET')).toBe(true);
  });
  it('does not poll a linked viewer for a hostile private locked-prophecy payload', async () => {
    vi.useFakeTimers();
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json({ ...linkedProjection, readiness: readyReadiness,
      jobs: [{ id: secondId, kind: 'base', state: 'queued' }], prophecies: [{ editionId: id, state: 'locked', unlockAt: 'past', revelation: 'PRIVATE_LINKED_SECRET' }] }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.text()).not.toContain('PRIVATE_LINKED_SECRET');
    await vi.advanceTimersByTimeAsync(15000); await flushPromises();
    expect(fetchMock.mock.calls.filter(([path]) => String(path) === `/app/v1/archive/associations/${id}`)).toHaveLength(1);
  });
  it('bounds readiness refreshes, offers a manual GET after the cap and cancels polling on unmount', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json({ ...view, readiness: partialReadiness }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    for (let index = 0; index < 12; index++) {
      await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    }
    const reads = () => fetchMock.mock.calls.filter(([path]) => String(path).endsWith(`/keepsakes/${id}`));
    expect(reads()).toHaveLength(13);
    const manualCheck = wrapper.findAll('button').find(button => button.text() === 'Zkontrolovat stav');
    expect(manualCheck?.exists()).toBe(true);
    expect(fetchMock.mock.calls.every(([, init]) => (init?.method ?? 'GET') === 'GET')).toBe(true);
    await manualCheck!.trigger('click'); await flushPromises();
    expect(reads()).toHaveLength(14);
    expect(wrapper.findAll('button').some(button => button.text() === 'Zkontrolovat stav')).toBe(false);
    wrapper.unmount();
    await vi.advanceTimersByTimeAsync(60000); await flushPromises();
    expect(reads()).toHaveLength(14);
  });
  it('does not poll terminal readiness or describe unavailable results as still preparing', async () => {
    vi.useFakeTimers();
    const terminal = { state: 'unavailable', reason: 'result_unavailable', canRetry: false, shouldPoll: false, hasStory: false, hasArtwork: false };
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json({ ...view, editions: [{ id, synthetic: true, publicationState: 'unavailable' }], jobs: [{ id: secondId, kind: 'base', state: 'queued' }], readiness: terminal }));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    expect(wrapper.find('[role="status"][data-readiness-state="unavailable"]').text()).toContain('Výsledek přípravy není dostupný');
    expect(wrapper.text()).toContain('Příběh není dostupný.');
    expect(wrapper.text()).toContain('Obraz není dostupný.');
    await vi.advanceTimersByTimeAsync(60000); await flushPromises();
    expect(fetchMock.mock.calls.filter(([path]) => String(path).endsWith(`/keepsakes/${id}`))).toHaveLength(1);
  });
  it('stops scheduled readiness reads when the browser goes offline', async () => {
    vi.useFakeTimers();
    const fetchMock = vi.fn(async (_path: string, _init?: RequestInit) => Response.json({ ...view, readiness: partialReadiness }));
    vi.stubGlobal('fetch', fetchMock);
    await render(KeepsakeView, `/keepsake/?k=${id}`);
    window.dispatchEvent(new Event('offline'));
    await vi.advanceTimersByTimeAsync(60000); await flushPromises();
    expect(fetchMock.mock.calls.filter(([path]) => String(path).endsWith(`/keepsakes/${id}`))).toHaveLength(1);
  });
  it('ignores an old readiness response that finishes after transient offline and reloads on online', async () => {
    vi.useFakeTimers();
    let resolveOld!: (response: Response) => void;
    const old = new Promise<Response>(resolve => resolveOld = resolve);
    let reads = 0;
    const fetchMock = vi.fn(async () => { reads++; return reads === 2 ? old : Response.json({ ...view, readiness: partialReadiness }); });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(reads).toBe(2);
    window.dispatchEvent(new Event('offline'));
    resolveOld(Response.json({ ...view, snapshot: { officialName: 'OFFLINE STALE RESULT', slot: 1 }, readiness: { ...readyReadiness, state: 'failed', shouldPoll: false } }));
    await flushPromises();
    expect(wrapper.text()).not.toContain('OFFLINE STALE RESULT');
    expect(wrapper.find('[data-readiness-state="partial"]').exists()).toBe(true);
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(reads).toBe(3);
    expect(wrapper.text()).not.toContain('OFFLINE STALE RESULT');
  });
  it('preserves owner POST idempotency across offlineSession cache mode and retries the same uncertain request', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    let rejectPost!: (error: Error) => void;
    const uncertainPost = new Promise<Response>((_resolve, reject) => rejectPost = reject);
    const posts: Array<{ body: string | null; idempotencyKey: string | null }> = [];
    const fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
      if (String(path).endsWith('/recovery-links') && init?.method === 'POST') {
        posts.push({ body: init.body as string, idempotencyKey: new Headers(init.headers).get('Idempotency-Key') });
        return posts.length === 1 ? uncertainPost : Response.json({ url: `${location.origin}/keepsake/#r=${'r'.repeat(40)}` });
      }
      return Response.json({ ...ownerProjection, readiness: partialReadiness });
    });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click');
    await flushPromises();
    expect(posts).toHaveLength(1);
    expect(posts[0].idempotencyKey).toMatch(/^[0-9a-f-]{36}$/i);
    window.dispatchEvent(new Event('offline'));
    offlineSession.value = true; await flushPromises();
    expect(wrapper.text()).not.toContain('Fiction edition');
    rejectPost(new TypeError('connection lost after server commit became uncertain'));
    await flushPromises();
    offlineSession.value = false; await flushPromises();
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(wrapper.text()).toContain('Fiction edition');
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click');
    await flushPromises();
    expect(posts).toHaveLength(2);
    expect(posts[1].body).toBe(posts[0].body);
    expect(posts[1].idempotencyKey).toBe(posts[0].idempotencyKey);
  });
  it('withholds a successful late owner response and replays it under the same key after reconnect', async () => {
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    let resolvePost!: (response: Response) => void;
    const pendingPost = new Promise<Response>(resolve => resolvePost = resolve);
    const posts: Array<{ body: string | null; idempotencyKey: string | null }> = [];
    const fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
      if (String(path).endsWith('/recovery-links') && init?.method === 'POST') {
        posts.push({ body: init.body as string, idempotencyKey: new Headers(init.headers).get('Idempotency-Key') });
        return posts.length === 1 ? pendingPost : Response.json({ url: `${location.origin}/keepsake/#r=${'r'.repeat(40)}` });
      }
      return Response.json({ ...ownerProjection, readiness: partialReadiness });
    });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click');
    await flushPromises();
    expect(posts).toHaveLength(1);
    const originalKey = posts[0].idempotencyKey;
    window.dispatchEvent(new Event('offline'));
    offlineSession.value = true; await flushPromises();
    resolvePost(Response.json({ url: `${location.origin}/keepsake/#r=${'r'.repeat(40)}` }));
    await flushPromises();
    expect(wrapper.find('#archive-issued-link').exists()).toBe(false);
    expect(wrapper.html()).not.toContain('keepsake/#r=');

    offlineSession.value = false; await flushPromises();
    window.dispatchEvent(new Event('online')); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click');
    await flushPromises();
    expect(posts).toHaveLength(2);
    expect(posts[1].body).toBe(posts[0].body);
    expect(posts[1].idempotencyKey).toBe(originalKey);
    expect(wrapper.find('#archive-issued-link').exists()).toBe(true);
  });
  it('resumes partial-result polling after a successful owner action', async () => {
    vi.useFakeTimers();
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    let reads = 0;
    const fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
      if (String(path).endsWith('/recovery-links') && init?.method === 'POST') return Response.json({ url: `${location.origin}/keepsake/#r=${'r'.repeat(40)}` });
      if (init?.method === 'GET') {
        reads++;
        return Response.json({ ...ownerProjection, readiness: reads === 1 ? partialReadiness : readyReadiness });
      }
      return Response.json({});
    });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(reads).toBe(1);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click');
    await flushPromises();
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(reads).toBe(2);
    expect(wrapper.find('[data-readiness-state="ready"]').exists()).toBe(true);
  });
  it('lets an owner mutation supersede a stalled readiness GET and fences its late result', async () => {
    vi.useFakeTimers();
    let resolvePoll!: (response: Response) => void;
    const pendingPoll = new Promise<Response>(resolve => resolvePoll = resolve);
    let reads = 0;
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const requests: Array<{ path: string; method: string; body?: string; key?: string }> = [];
    const fetchMock = vi.fn(async (path: string, init?: RequestInit) => {
      const method = init?.method ?? 'GET';
      requests.push({ path: String(path), method, body: typeof init?.body === 'string' ? init.body : undefined, key: new Headers(init?.headers).get('Idempotency-Key') ?? undefined });
      if (String(path).endsWith('/recovery-links')) return Response.json({ url: `${location.origin}/keepsake/#r=${'r'.repeat(40)}` });
      if (method === 'GET') { reads++; return reads === 2 ? pendingPoll : Response.json({ ...ownerProjection, readiness: partialReadiness }); }
      return Response.json(ownerProjection);
    });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(reads).toBe(2);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit soukromý návratový odkaz')!.trigger('click');
    await flushPromises();
    const create = requests.filter(request => request.path === `/app/v1/archive/owned/${id}/recovery-links`);
    expect(create).toHaveLength(1);
    expect(create[0]).toMatchObject({ method: 'POST', body: JSON.stringify({ schemaVersion: 1 }) });
    expect(create[0].key).toMatch(/^[0-9a-f-]{36}$/i);
    expect(wrapper.find('#archive-issued-link').exists()).toBe(true);
    resolvePoll(Response.json({ ...ownerProjection, snapshot: { officialName: 'STALE READ MUST NOT REPLACE THIS VIEW', slot: 1 }, readiness: { ...partialReadiness, state: 'failed', shouldPoll: false } }));
    await flushPromises();
    expect(wrapper.find('#archive-issued-link').exists()).toBe(true);
    expect(wrapper.text()).not.toContain('STALE READ MUST NOT REPLACE THIS VIEW');
    expect(wrapper.find('[data-readiness-state="partial"]').exists()).toBe(true);
    expect(requests.filter(request => request.path === `/app/v1/archive/owned/${id}/recovery-links`)).toHaveLength(1);
  });
  it('account switching immediately redacts private results and ignores an old noncooperative account response', async () => {
    account.value = { id: 'account-a', displayName: 'First account', roles: ['Viewer'] };
    let resolveOld!: (response: Response) => void;
    const old = new Promise<Response>(resolve => resolveOld = resolve); let reads = 0;
    const fetchMock = vi.fn(async () => { reads++; if (reads === 1) return Response.json({ ...linkedProjection, readiness: partialReadiness }); if (reads === 2) return old; return new Response('', { status: 404 }); });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.text()).toContain('Fiction edition');
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit stav památky')!.trigger('click'); await flushPromises();
    account.value = { id: 'account-b', displayName: 'Second account', roles: ['Viewer'] }; await flushPromises();
    expect(wrapper.text()).not.toContain('Fiction edition');
    expect(wrapper.find('[data-readiness-state="partial"]').exists()).toBe(false);
    resolveOld(Response.json({ ...linkedProjection, snapshot: { officialName: 'OLD ACCOUNT PRIVATE DATA', slot: 1 } })); await flushPromises();
    expect(wrapper.text()).not.toContain('OLD ACCOUNT PRIVATE DATA'); expect(wrapper.text()).not.toContain('Fiction edition');
  });
  it('account switching during owner proof issuance never consumes the previous account ticket', async () => {
    account.value = { id: 'account-a', displayName: 'First account', roles: ['Viewer'] };
    let resolveTicket!: (response: Response) => void;
    const ticket = new Promise<Response>(resolve => resolveTicket = resolve);
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => path.endsWith('/bootstrap') ? Response.json({ schemaVersion: 1, csrfToken: 'guest-csrf' })
      : path.endsWith('/account-associations') ? ticket : Response.json(view));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Připojit k mému účtu')!.trigger('click');
    await wrapper.findAll('button').find(button => button.text() === 'Potvrdit připojení včetně soukromých portrétů')!.trigger('click'); await flushPromises();
    account.value = { id: 'account-b', displayName: 'Second account', roles: ['Viewer'] }; await flushPromises();
    resolveTicket(Response.json({ schemaVersion: 1, ticket: 'FIRST-ACCOUNT-ONLY-TICKET' })); await flushPromises();
    expect(fetchMock.mock.calls.some(([path]) => path === '/app/v1/archive/associations')).toBe(false);
    expect(wrapper.text()).not.toContain('byly připojeny k tomuto účtu'); expect(wrapper.text()).not.toContain('FIRST-ACCOUNT-ONLY-TICKET');
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import ArchiveView from './ArchiveView.vue';
import KeepsakeView from './KeepsakeView.vue';
import { account } from '../identity/session';
const entry = vi.hoisted(() => ({ value: null as string | null }));
vi.mock('../../application/guestClaim', () => ({ consumeGuestClaim: () => { const result = entry.value; entry.value = null; return result; } }));
const id = '11111111-1111-4111-8111-111111111111';
const secondId = '22222222-2222-4222-8222-222222222222';
const view = { schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false,
  snapshot: { officialName: '<script>catalog</script>', slot: 1 },
  editions: [{ id, story: { title: 'Fiction edition', text: 'One paragraph.\n\nAnother paragraph.' }, synthetic: true, publicationState: 'ready' }],
  portraits: [], jobs: [], assets: [] };
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
  vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_: string, action: () => Promise<unknown>) => action() } });
  URL.createObjectURL = vi.fn(() => 'blob:fixture'); URL.revokeObjectURL = vi.fn();
  HTMLDialogElement.prototype.showModal = function () { this.open = true; };
  HTMLDialogElement.prototype.close = function () { this.open = false; this.dispatchEvent(new Event('close')); };
});
afterEach(() => { for (const wrapper of wrappers) wrapper.unmount(); wrappers = []; vi.restoreAllMocks(); vi.unstubAllGlobals(); document.body.innerHTML = ''; });
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
    account.value = { id: 'account-a', displayName: 'Current account', roles: ['Viewer'] };
    const linked = { ...view, readOnly: true, accountLinked: true, portraits: [{ id: secondId, editionId: id, assetId: secondId, synthetic: true }], assets: [{ id: secondId, kind: 'portrait_image', mediaType: 'image/png' }] };
    const fetchMock = vi.fn(async (path: string, _init?: RequestInit) => path.includes('/assets/') ? new Response(new Blob(['synthetic portrait'], { type: 'image/png' })) : Response.json(linked));
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(fetchMock.mock.calls.every(([path]) => path.startsWith('/app/v1/archive/associations/'))).toBe(true);
    expect(wrapper.text()).toContain('Soukromý archiv účtu · pouze čtení'); expect(wrapper.find('img[alt="Soukromý kouzelnický portrét"]').exists()).toBe(true);
    expect(wrapper.find('input[type=file]').exists()).toBe(false); expect(wrapper.text()).not.toContain('Odvolat všechny přístupy'); expect(wrapper.text()).not.toContain('Připojit k mému účtu');
  });
  it('account switching immediately redacts private results and ignores an old noncooperative account response', async () => {
    account.value = { id: 'account-a', displayName: 'First account', roles: ['Viewer'] };
    let resolveOld!: (response: Response) => void;
    const old = new Promise<Response>(resolve => resolveOld = resolve); let reads = 0;
    const fetchMock = vi.fn(async () => { reads++; if (reads === 1) return Response.json({ ...view, readOnly: true, accountLinked: true }); if (reads === 2) return old; return new Response('', { status: 404 }); });
    vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?account=1&k=${id}`);
    expect(wrapper.text()).toContain('Fiction edition');
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit stav památky')!.trigger('click'); await flushPromises();
    account.value = { id: 'account-b', displayName: 'Second account', roles: ['Viewer'] }; await flushPromises();
    expect(wrapper.text()).not.toContain('Fiction edition');
    resolveOld(Response.json({ ...view, readOnly: true, accountLinked: true, snapshot: { officialName: 'OLD ACCOUNT PRIVATE DATA', slot: 1 } })); await flushPromises();
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

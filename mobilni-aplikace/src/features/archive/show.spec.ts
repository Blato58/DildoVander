import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import DuelView from './DuelView.vue';
import ChronicleView from './ChronicleView.vue';
import KeepsakeView from './KeepsakeView.vue';
import { account, offlineSession, accountTransport, accountRequest } from '../identity/session';
import { DuelClient } from './showClient';
import { projectGalleryItem } from './client';
import { publicIdentity, ownerProphecy, projectChronicle, projectDuel } from './showContracts';
const captured = vi.hoisted(() => ({ grant: null as { token: string; side: 'left' | 'right' } | null }));
vi.mock('../../application/guestClaim', () => ({ consumeGuestClaim: () => null, consumeDuelGrant: () => { const value = captured.grant; captured.grant = null; return value; } }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue'); const account = ref<{ id: string; displayName: string; roles: string[] } | null>(null); const offlineSession = ref(false);
  return { account, offlineSession, accountRequest: vi.fn(), accountTransport: vi.fn(), hasRole: (role: string) => account.value?.roles.includes(role) ?? false };
});
const id = '11111111-1111-4111-8111-111111111111'; const other = '22222222-2222-4222-8222-222222222222';
const identity = { editionId: id, visibility: 'public_fiction', wizardAlias: '<script>mág</script>', houseOrGuild: 'Cech', dubiousSpecialization: 'Lest', signatureAbility: 'Schopnost', weakness: 'Slabina', ministryAssessment: 'Fikce', wandBond: 'Pouto', motto: 'Motto', microStory: 'Příběh', omen: 'Indicie' };
const duel = (revision = 1) => ({ id, revision, state: 'Running', mode: 'interactive', round: 1, deadline: '2026-10-19T19:15:00Z', narrationMode: 'web_only', narrationStatus: 'web_only_no_spoken_confirmation',
  left: { displayName: 'Levý mág', wandId: 'wand1', strength: 2, cunning: 3, defense: 4 }, right: { displayName: 'Pravý mág', wandId: 'wand1', strength: 3, cunning: 2, defense: 1 }, leftChoiceLocked: false, rightChoiceLocked: false, rounds: [] });
const chronicle = (revision = 1) => ({ schemaVersion: 1, eveningId: id, revision, provisional: true, createdAt: '2026-10-19T19:00:00Z', completedVisits: 0, confirmedPresentations: 0, completedDuels: 0, interruptedVisits: 1, unresolvedVisits: 2,
  wands: [{ slot: 1, officialName: 'Hůlka A', confirmedPresentations: 0 }, { slot: 2, officialName: 'Hůlka B', confirmedPresentations: 0 }], mostPopularSlots: [], narrative: 'Večer teprve začíná.' });
const keepsake = { schemaVersion: 1, locator: id, revision: 1, state: 'ready', readOnly: false, snapshot: { officialName: 'Katalogová hůlka', slot: 1 }, editions: [{ id, publicationState: 'published', story: { title: 'Edice', text: 'Příběh edice' }, synthetic: true }], jobs: [], portraits: [], assets: [] };
const deviceDetails = { devices: [{ deviceId: 'test-device', canOperate: true }], associations: [{ deviceId: 'test-device', alias: 'Kufr v salonu' }], grants: [], sessions: [], passkeys: [], passkeyCount: 1 };
const eveningList = { activeEveningId: id, evenings: [{ eveningId: id, name: 'Večer v salonu', createdAt: '2026-10-19T19:00:00Z' }] };
let wrappers: VueWrapper[] = [];
async function render(component: object, path: string) {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component }] });
  await router.push(path); await router.isReady();
  const wrapper = mount(component, { attachTo: document.body, global: { plugins: [router] } }); wrappers.push(wrapper); await flushPromises(); return { wrapper, router };
}
beforeEach(() => {
  captured.grant = null; account.value = null; offlineSession.value = false; vi.spyOn(document, 'hidden', 'get').mockReturnValue(false);
  vi.mocked(accountRequest).mockReset().mockResolvedValue(deviceDetails);
  vi.mocked(accountTransport).mockReset().mockImplementation(async path => Response.json(path === '/v1/evenings' ? eveningList : {}));
  Object.defineProperty(navigator, 'locks', { configurable: true, value: { request: async (_: string, work: () => Promise<unknown>) => work() } });
  URL.createObjectURL = vi.fn(() => 'blob:fixture'); URL.revokeObjectURL = vi.fn();
  HTMLDialogElement.prototype.showModal = function () { this.open = true; }; HTMLDialogElement.prototype.close = function () { this.open = false; };
});
afterEach(() => { for (const wrapper of wrappers) wrapper.unmount(); wrappers = []; vi.restoreAllMocks(); vi.unstubAllGlobals(); vi.useRealTimers(); document.body.innerHTML = ''; });
describe('Show Pack closed projections', () => {
  it('drops private fields from public fiction, chronicle and duel; locked prophecy retains no future body', () => {
    expect(publicIdentity({ ...identity, revelation: 'private', sourceRefs: ['private'] })).toEqual(identity);
    expect(publicIdentity({ ...identity, visibility: 'owner_only' })).toBeNull();
    expect(projectGalleryItem({ locator: id, editionId: other, wizardIdentity: identity })?.wizardIdentity).toBeNull();
    expect(ownerProphecy({ editionId: id, state: 'locked', unlockAt: 'later', revelation: 'private', afterword: 'private' })).toEqual({ editionId: id, state: 'locked', unlockAt: 'later' });
    expect(projectChronicle({ ...chronicle(), transcript: 'private', photograph: 'private', prophecy: 'private' })).not.toHaveProperty('transcript');
    const view = projectDuel({ ...duel(), token: 'private', leftChoice: 'trick', seed: 'future', result: { ending: 'future' } }, id);
    expect(view).not.toHaveProperty('token'); expect(view).not.toHaveProperty('seed'); expect(view.result).toBeNull();
  });
  it('keeps the same operation key after uncertain move delivery and fences nonabortable transport/body completion', async () => {
    const requests: RequestInit[] = [];
    const transport = vi.fn(async (_path: RequestInfo | URL, init?: RequestInit) => { requests.push(init!); return requests.length === 1 ? new Response('', { status: 503 }) : Response.json(duel(2)); });
    const client = new DuelClient(transport); const access = { token: 'x'.repeat(43), side: 'left' as const }; const view = projectDuel(duel(), id);
    await expect(client.choose(view, access, 'strike')).rejects.toThrow(); await client.choose(view, access, 'strike');
    expect(JSON.parse(requests[0].body as string).operationKey).toBe(JSON.parse(requests[1].body as string).operationKey);
    expect(requests[0]).toMatchObject({ credentials: 'omit', cache: 'no-store', redirect: 'error' });
    let complete!: (response: Response) => void; const late = new DuelClient(() => new Promise(resolve => complete = resolve));
    const reading = late.read(id); late.invalidate(); complete(Response.json(duel())); await expect(reading).rejects.toMatchObject({ name: 'AbortError' });
    let finish!: (value: unknown) => void; const body = new DuelClient(async () => ({ ok: true, json: () => new Promise(resolve => finish = resolve) } as unknown as Response));
    const decoding = body.read(id); await flushPromises(); body.invalidate(); finish(duel()); await expect(decoding).rejects.toMatchObject({ name: 'AbortError' });
  });
  it('submits guest moves securely on supported WebViews without crypto.randomUUID', async () => {
    const original = globalThis.crypto; const random = vi.fn((bytes: Uint8Array<ArrayBuffer>) => original.getRandomValues(bytes));
    vi.stubGlobal('crypto', { getRandomValues: random });
    const transport = vi.fn(async (_path: RequestInfo | URL, _init?: RequestInit) => Response.json(duel(2)));
    await new DuelClient(transport).choose(projectDuel(duel(), id), { token: 'x'.repeat(43), side: 'left' }, 'strike');
    expect(random).toHaveBeenCalledTimes(1);
    expect(JSON.parse(transport.mock.calls[0][1]!.body as string).operationKey).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
  });
});
describe('shared guest Show Pack screens', () => {
  it('a signed-in account cannot submit moves without a scoped side grant', async () => {
    account.value = { id: other, displayName: 'Správce', roles: ['Administrator'] };
    const fetchMock = vi.fn(async () => Response.json(duel())); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(DuelView, `/archive/duel?duel=${id}`);
    expect(wrapper.text()).toContain('Levý mág'); expect(wrapper.text()).toContain('Pouze webový průběh');
    expect(wrapper.findAll('button').some(button => button.text() === 'Úder')).toBe(false);
    expect(fetchMock).toHaveBeenCalledWith(`/guest/v1/duels/${id}`, expect.objectContaining({ credentials: 'omit' }));
  });
  it('sends only the side grant and exact revision once; server locks choices and older revisions cannot reopen them', async () => {
    captured.grant = { token: 'x'.repeat(43), side: 'left' };
    const fetchMock = vi.fn(async (_path: RequestInfo | URL, init?: RequestInit) => Response.json(init?.method === 'POST' ? { ...duel(2), leftChoiceLocked: true } : duel())); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(DuelView, `/archive/duel?duel=${id}`);
    const button = wrapper.findAll('button').find(button => button.text() === 'Úder')!; await button.trigger('click'); await button.trigger('click'); await flushPromises();
    const post = fetchMock.mock.calls.filter(([, init]) => init?.method === 'POST'); expect(post).toHaveLength(1);
    expect(post[0][1]).toMatchObject({ credentials: 'omit', headers: { Authorization: 'Bearer ' + 'x'.repeat(43) } });
    expect(JSON.parse(post[0][1]!.body as string)).toMatchObject({ side: 'left', round: 1, revision: 1, action: 'strike' });
    expect(wrapper.text()).toContain('Tah přijat a uzamčen');
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit souboj')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('Tah přijat a uzamčen'); expect(button.attributes('disabled')).toBeDefined();
  });
  it('redacts unavailable duel after grant denial, and discards late results after a route change', async () => {
    captured.grant = { token: 'x'.repeat(43), side: 'left' };
    let reads = 0;
    vi.stubGlobal('fetch', vi.fn(async (_path: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST' ? new Response('', { status: 403 }) : ++reads === 1 ? Response.json(duel()) : new Response('', { status: 404 })));
    const { wrapper } = await render(DuelView, `/archive/duel?duel=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Úder')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).not.toContain('Levý mág'); expect(wrapper.findAll('button').some(button => button.text() === 'Úder')).toBe(false);
    let complete!: (response: Response) => void;
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(resolve => complete = resolve)));
    const late = await render(DuelView, `/archive/duel?duel=${id}`); const initial = complete;
    await late.router.push(`/archive/duel?duel=${other}`); await flushPromises(); initial(Response.json(duel())); await flushPromises();
    expect(late.wrapper.text()).not.toContain('Levý mág');
  });
  it('a hidden page fences a nonabortable move while retaining its in-memory grant until page departure', async () => {
    captured.grant = { token: 'x'.repeat(43), side: 'left' };
    let finish!: (response: Response) => void;
    vi.stubGlobal('fetch', vi.fn(async (_path: RequestInfo | URL, init?: RequestInit) => init?.method === 'POST' ? new Promise<Response>(resolve => finish = resolve) : Response.json(duel())));
    const { wrapper } = await render(DuelView, `/archive/duel?duel=${id}`);
    await wrapper.findAll('button').find(button => button.text() === 'Úder')!.trigger('click');
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(true); document.dispatchEvent(new Event('visibilitychange'));
    finish(Response.json({ ...duel(2), leftChoiceLocked: true })); await flushPromises();
    expect(wrapper.text()).not.toContain('Levý mág');
    vi.spyOn(document, 'hidden', 'get').mockReturnValue(false); document.dispatchEvent(new Event('visibilitychange')); await flushPromises();
    expect(wrapper.findAll('button').some(button => button.text() === 'Úder')).toBe(true);
    window.dispatchEvent(new Event('pagehide')); window.dispatchEvent(new Event('pageshow')); await flushPromises();
    expect(wrapper.findAll('button').some(button => button.text() === 'Úder')).toBe(false);
  });
  it('shows zero counts and ties, applies late-outcome revisions, and erases a withdrawn public chronicle before refresh', async () => {
    let response: unknown = chronicle();
    const fetchMock = vi.fn(async (_path: RequestInfo | URL, _init?: RequestInit) => response ? Response.json(response) : new Response('', { status: 404 })); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(ChronicleView, '/archive/chronicle');
    expect(wrapper.text()).toContain('První potvrzená prezentace');
    response = { ...chronicle(2), completedVisits: 2, mostPopularSlots: [1, 2], confirmedPresentations: 2 };
    await wrapper.find('button').trigger('click'); await flushPromises(); expect(wrapper.text()).toContain('Na prvním místě je remíza');
    response = null; await wrapper.find('button').trigger('click'); await flushPromises(); expect(wrapper.find('table').exists()).toBe(false);
    expect(fetchMock.mock.calls.every(call => (call[1] as RequestInit).credentials === 'omit')).toBe(true);
  });
  it('account admin inspection is explicit, current-account-bound and immediately redacted on role loss', async () => {
    account.value = { id: other, displayName: 'Správce', roles: ['Administrator'] };
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(chronicle())));
    let complete!: (response: Response) => void; vi.mocked(accountTransport).mockImplementation(path => path === '/v1/evenings' ? Promise.resolve(Response.json(eveningList)) : new Promise(resolve => complete = resolve));
    const { wrapper } = await render(ChronicleView, '/archive/chronicle');
    expect(vi.mocked(accountTransport).mock.calls.some(([path]) => path.startsWith('/v1/admin/'))).toBe(false);
    expect(wrapper.find('#chronicle-device').text()).toContain('Kufr v salonu'); expect(wrapper.find('#chronicle-evening').text()).toContain('Večer v salonu');
    await wrapper.find('#chronicle-device').setValue('test-device'); await wrapper.find('#chronicle-evening').setValue(id); await wrapper.find('form').trigger('submit');
    expect(wrapper.find('table').exists()).toBe(false);
    account.value = { id: other, displayName: 'Správce', roles: ['Viewer'] };
    complete(Response.json({ snapshot: { ...chronicle(), narrative: 'Private' }, archivedEditions: 3 })); await flushPromises();
    expect(wrapper.text()).not.toContain('Private'); expect(wrapper.find('form').exists()).toBe(false);
  });
  it('uses authorized labeled selections, preserves opaque evenings and ignores an old suitcase list response', async () => {
    account.value = { id: other, displayName: 'Správce', roles: ['Administrator'] };
    vi.mocked(accountRequest).mockResolvedValue({ ...deviceDetails, devices: [{ deviceId: 'first', canOperate: true }, { deviceId: 'second', canOperate: false }], associations: [{ deviceId: 'first', alias: 'První kufr' }, { deviceId: 'second', alias: 'Druhý kufr' }] });
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(chronicle())));
    let first!: (response: Response) => void;
    vi.mocked(accountTransport).mockImplementation(async (path, init) => {
      if (path !== '/v1/evenings') return Response.json({ snapshot: { ...chronicle(), eveningId: 'legacy-evening_219' }, archivedEditions: 1 });
      return new Headers(init?.headers).get('X-DV-Device-Id') === 'first' ? new Promise<Response>(resolve => first = resolve)
        : Response.json({ activeEveningId: 'legacy-evening_219', evenings: [{ eveningId: 'legacy-evening_219', name: 'Druhý večer' }] });
    });
    const { wrapper } = await render(ChronicleView, '/archive/chronicle');
    expect(wrapper.findAll('input')).toHaveLength(0);
    await wrapper.find('#chronicle-device').setValue('first'); await flushPromises();
    await wrapper.find('#chronicle-device').setValue('second'); await flushPromises();
    first(Response.json({ activeEveningId: id, evenings: [{ eveningId: id, name: 'Opožděný první večer' }] })); await flushPromises();
    expect(wrapper.find('#chronicle-evening').text()).toContain('Druhý večer'); expect(wrapper.find('#chronicle-evening').text()).not.toContain('Opožděný');
    await wrapper.find('form').trigger('submit'); await flushPromises();
    const request = vi.mocked(accountTransport).mock.calls.find(([path]) => path.startsWith('/v1/admin/'))!;
    expect(new URL(request[0], 'https://example.test').searchParams.get('eveningId')).toBe('legacy-evening_219');
    expect(new Headers(request[1]!.headers).get('X-DV-Device-Id')).toBe('second');
  });
  it('rejects a private snapshot for a different evening even from a successful current-account response', async () => {
    account.value = { id: other, displayName: 'Správce', roles: ['Administrator'] };
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(chronicle())));
    vi.mocked(accountTransport).mockImplementation(async path => Response.json(path === '/v1/evenings' ? eveningList : { snapshot: { ...chronicle(), eveningId: other, narrative: 'Wrong private evening' }, archivedEditions: 1 }));
    const { wrapper } = await render(ChronicleView, '/archive/chronicle');
    await wrapper.find('form').trigger('submit'); await flushPromises();
    expect(wrapper.text()).not.toContain('Wrong private evening'); expect(wrapper.find('table').exists()).toBe(false);
    expect(wrapper.text()).toContain('Soukromý přehled není dostupný');
  });
  it('late authorized-device lists cannot restore a previous account and denied context refresh redacts private content', async () => {
    account.value = { id: other, displayName: 'Správce', roles: ['Administrator'] };
    vi.stubGlobal('fetch', vi.fn(async () => Response.json(chronicle())));
    let details!: (value: unknown) => void; vi.mocked(accountRequest).mockImplementationOnce(() => new Promise(resolve => details = resolve));
    const late = await render(ChronicleView, '/archive/chronicle');
    account.value = null; details(deviceDetails); await flushPromises(); expect(late.wrapper.find('form').exists()).toBe(false);
    account.value = { id: other, displayName: 'Správce', roles: ['Administrator'] }; await flushPromises();
    let denied = false;
    vi.mocked(accountTransport).mockImplementation(async path => path === '/v1/evenings' ? denied ? new Response('', { status: 403 }) : Response.json(eveningList)
      : Response.json({ snapshot: { ...chronicle(), narrative: 'Private authorized snapshot' }, archivedEditions: 1 }));
    const current = await render(ChronicleView, '/archive/chronicle');
    await current.wrapper.find('form').trigger('submit'); await flushPromises(); expect(current.wrapper.text()).toContain('Private authorized snapshot');
    denied = true; await current.wrapper.findAll('button').find(button => button.text() === 'Obnovit výběr')!.trigger('click'); await flushPromises();
    expect(current.wrapper.text()).not.toContain('Private authorized snapshot'); expect(current.wrapper.find('#chronicle-device').findAll('option')).toHaveLength(1);
    expect(current.wrapper.find('button[type=submit]').attributes('disabled')).toBeDefined();
  });
  it('legacy identity creation is explicit and locked prophecy never unlocks on a browser clock or leaks to shares', async () => {
    let hasIdentity = false;
    const fetchMock = vi.fn(async (path: RequestInfo | URL, _init?: RequestInit) => {
      if (String(path).endsWith('/bootstrap')) return Response.json({ schemaVersion: 1, csrfToken: 'fixture-csrf' });
      if (String(path).endsWith('/wizard-identity')) { hasIdentity = true; return Response.json({}); }
      return Response.json({ ...keepsake, wizardIdentities: hasIdentity ? [identity] : [], prophecies: [{ editionId: id, state: 'locked', unlockAt: '2000-01-01T00:00:00Z', revelation: 'Private future body' }] });
    }); vi.stubGlobal('fetch', fetchMock);
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}`);
    expect(fetchMock.mock.calls).toHaveLength(1); expect(wrapper.text()).not.toContain('Private future body');
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit kouzelnickou identitu této edice')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('<script>mág</script>'); expect(wrapper.find('script').exists()).toBe(false);
    expect(wrapper.findAll('button').find(button => button.text() === 'Kartička PNG')!.attributes('disabled')).toBeUndefined();
    const post = fetchMock.mock.calls.find(([path]) => String(path).endsWith('/wizard-identity'))!;
    expect(JSON.parse(post[1]!.body as string)).toEqual({ schemaVersion: 1, editionId: id }); expect((post[1]!.headers as Record<string, string>)['X-DV10-CSRF']).toBe('fixture-csrf');
    const share = await render(KeepsakeView, `/keepsake/?k=${id}&view=share&editionId=${id}`);
    expect(share.wrapper.text()).not.toContain('Soukromé proroctví'); expect(share.wrapper.text()).not.toContain('Private future body');
  });
  it('associated accounts receive public fiction with an honest owner-only prophecy boundary and no guest fallback', async () => {
    account.value = { id: other, displayName: 'Čtenář', roles: ['Viewer'] };
    const fetchMock = vi.fn(async () => { throw new Error('Account view must not fall back to owner cookies'); }); vi.stubGlobal('fetch', fetchMock);
    vi.mocked(accountTransport).mockResolvedValue(Response.json({ ...keepsake, readOnly: true, accountLinked: true, accessKind: 'linkedViewer', canManage: false, canReadPrivate: false, canUnlink: true, uploads: [], wizardIdentities: [identity], prophecies: [{ editionId: id, state: 'revealed', unlockAt: 'past', revelation: 'Private owner wording' }] }));
    const { wrapper } = await render(KeepsakeView, `/keepsake/?k=${id}&account=1`);
    expect(wrapper.text()).toContain('<script>mág</script>'); expect(wrapper.text()).toContain('Soukromé proroctví je dostupné jen vlastníkovi');
    expect(wrapper.text()).not.toContain('Private owner wording'); expect(wrapper.findAll('button').some(button => button.text().startsWith('Vytvořit kouzelnickou'))).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';

const api = vi.hoisted(() => ({ request: vi.fn(), disconnect: vi.fn(), discover: vi.fn(), connect: vi.fn(), start: vi.fn(), status: vi.fn(), end: vi.fn(), supported: true, setAccount: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref({ id: 'customer-1', displayName: 'Guest', roles: ['Customer'] });
  api.setAccount.mockImplementation((value: typeof account.value) => { account.value = value; });
  return { account, accountDeletionRequested: ref(false), accountRequest: api.request, authenticatePasskey: vi.fn(), signOut: vi.fn() };
});
vi.mock('./customerBle', () => ({
  customerBle: {
    supported: () => api.supported,
    disconnect: api.disconnect,
    discover: api.discover,
    connect: api.connect,
    start: api.start,
    status: api.status,
    end: api.end,
  },
}));
import CustomerView from './CustomerView.vue';

const profile = { profileId: 'profile-1', revision: 1, userInput: { name: 'Luna', world: 'Moon Saga' }, status: 'ready' };
const bleStatus = { v: 1, deviceId: 'device-1', bootId: 'boot-1', challenge: 'challenge-secret', capabilities: { customerStart: 1 }, state: 'idle' as const };
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(complete => { resolve = complete; }); return { promise, resolve }; }
const router = async (path = '/me/conversation') => {
  const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }, { path: '/me/conversation', component: CustomerView }, { path: '/me/account', component: { template: '<div />' } }] });
  await instance.push(path); await instance.isReady(); return instance;
};

afterEach(() => { vi.clearAllMocks(); api.disconnect.mockResolvedValue(undefined); api.supported = true; api.setAccount({ id: 'customer-1', displayName: 'Guest', roles: ['Customer'] }); vi.useRealTimers(); });

describe('customer conversation', () => {
  it('keeps OpenAI enrichment optional and submits only explicit consent', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } });
    await flushPromises();
    const consent = wrapper.get('#character-openai-consent');
    expect((consent.element as HTMLInputElement).checked).toBe(false);
    expect(wrapper.text()).toContain('OpenAI');
    expect(wrapper.text()).toContain('Bez zaškrtnutí profil uložíme');
    await wrapper.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/character', 'PUT', expect.objectContaining({ openAiConsent: false }), expect.any(AbortSignal));
    expect(wrapper.findAll('button').find(button => button.text() === 'Tohle není moje postava')!.attributes('disabled')).toBeDefined();
    await consent.setValue(true);
    await wrapper.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenLastCalledWith('/app/v1/customer/character', 'PUT', expect.objectContaining({ openAiConsent: true }), expect.any(AbortSignal));
    expect((consent.element as HTMLInputElement).checked).toBe(false);
    wrapper.unmount();
  });
  it.each(['expired', 'ended', 'cancelled', 'future_runtime_state'])('continues reconciling unrecognized session state %s', async state => {
    vi.useFakeTimers();
    let sessionReads = 0;
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) {
        sessionReads += 1;
        return { items: [{ id: 'uncertain-session', operationId: 'original-operation', deviceId: 'device-1', profileId: profile.profileId, profileRevision: 1, state, endRequested: false, createdAt: 1792540800 }] };
      }
      throw new Error(`Unexpected request ${path}`);
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    const initialReads = sessionReads;
    await vi.advanceTimersByTimeAsync(2500);
    await flushPromises();
    expect(sessionReads).toBeGreaterThan(initialReads);
    expect(wrapper.text()).toContain('Stav se ověřuje');
    expect(api.start).not.toHaveBeenCalled();
    expect(api.request.mock.calls.some(call => call[0].endsWith('/start-tickets'))).toBe(false);
    wrapper.unmount();
  });

  it('recovers a restored session after a failed initial poll and waits for recovering Stop acknowledgement', async () => {
    vi.useFakeTimers();
    api.supported = false;
    let sessionReads = 0;
    let state = 'recovering';
    let endRequested = false;
    api.request.mockImplementation(async (path: string, method?: string) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions') && method === 'GET') {
        sessionReads += 1;
        if (sessionReads === 1) throw new TypeError('Failed to fetch');
        return { items: [{ id: 'session-recovery', operationId: 'accepted-before-relaunch', deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, state, endRequested, createdAt: 1792540800, characterName: 'Restored' }] };
      }
      if (path.endsWith('/session-recovery/end') && method === 'POST') { endRequested = true; return { state: 'ending' }; }
      throw new Error(`Unexpected request ${path}`);
    });

    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    expect(wrapper.text()).toContain('Server není dostupný');
    expect(wrapper.findAll('button').some(button => button.text() === 'Obnovit stav rozhovoru')).toBe(true);

    await vi.advanceTimersByTimeAsync(1000);
    await flushPromises();
    expect(wrapper.text()).toContain('Stav: Obnovuje se');
    expect(wrapper.text()).not.toContain('Ověřujeme, zda kufr rozhovor přijal');
    expect(api.request.mock.calls.some(call => call[0].endsWith('/start-tickets'))).toBe(false);
    expect(api.start).not.toHaveBeenCalled();

    await wrapper.findAll('button').find(button => button.text() === 'Ukončit rozhovor')!.trigger('click');
    await flushPromises();
    expect(wrapper.findAll('button').find(button => button.text() === 'Konec požádán')?.attributes('disabled')).toBeDefined();
    expect(api.end).not.toHaveBeenCalled();
    expect(api.request.mock.calls.filter(call => call[0].endsWith('/session-recovery/end'))).toHaveLength(1);

    state = 'completed';
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit stav rozhovoru')!.trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('Stav: Dokončen');
    expect(wrapper.findAll('button').some(button => button.text() === 'Konec požádán')).toBe(false);
    wrapper.unmount();
  });

  it('redacts unknown native failures while showing actionable Bluetooth permission feedback', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    api.discover.mockRejectedValue(new Error('permission_denied'));
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();

    await wrapper.findAll('button').find(button => button.text() === 'Najít kufr poblíž')!.trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('Povolte aplikaci přístup k Bluetooth');
    expect(wrapper.text()).toContain('Diagnostika: permission_denied');

    api.discover.mockRejectedValueOnce(new Error('Authorization: Bearer sensitive-device-token'));
    await wrapper.findAll('button').find(button => button.text() === 'Najít kufr poblíž')!.trigger('click');
    await flushPromises();
    expect(wrapper.text()).not.toContain('sensitive-device-token');
    expect(wrapper.text()).toContain('Stav rozhovoru se nepodařilo načíst');
    wrapper.unmount();
  });

  it('cancels stale session work while hidden and refreshes when visible or online', async () => {
    vi.useFakeTimers();
    const originalVisibility = Object.getOwnPropertyDescriptor(document, 'visibilityState');
    const firstRead = deferred<{ items: Array<{ id: string; operationId: string; deviceId: string; profileId: string; profileRevision: number; state: string; endRequested: boolean; createdAt: number; characterName: string }> }>();
    let sessionReads = 0;
    api.request.mockImplementation((path: string) => {
      if (path.endsWith('/character')) return Promise.resolve({ profile });
      if (path.endsWith('/sessions')) {
        sessionReads += 1;
        return sessionReads === 1 ? firstRead.promise : Promise.resolve({ items: [] });
      }
      return Promise.resolve({ items: [] });
    });

    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    const firstRequest = api.request.mock.calls.find(call => call[0].endsWith('/sessions'))!;
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
    firstRead.resolve({ items: [{ id: 'late-session', operationId: 'late-op', deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, state: 'running', endRequested: false, createdAt: 1792540800, characterName: 'Late response' }] });
    await flushPromises();
    window.dispatchEvent(new Event('online'));
    await vi.advanceTimersByTimeAsync(5000);
    expect(firstRequest[3].aborted).toBe(true);
    expect(sessionReads).toBe(1);
    expect(wrapper.text()).not.toContain('Late response');

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect(sessionReads).toBe(2);
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(sessionReads).toBe(3);
    wrapper.unmount();
    if (originalVisibility) Object.defineProperty(document, 'visibilityState', originalVisibility);
    else Reflect.deleteProperty(document, 'visibilityState');
  });
  it('does not request Bluetooth until the visitor explicitly starts discovery', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    api.discover.mockResolvedValue({ devices: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    expect(api.discover).not.toHaveBeenCalled();
    await wrapper.get('button').trigger('click'); await flushPromises();
    expect(api.discover).toHaveBeenCalledOnce();
    wrapper.unmount();
  });

  it('creates a ticket only after a user click and keeps its operation id through the BLE start', async () => {
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return { items: [] };
      if (path.endsWith('/start-tickets')) return { ticket: 'ephemeral-ticket', operationId: 'operation-1', expiresAt: 'later' };
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    api.start.mockResolvedValue({ ...bleStatus, state: 'starting', operationId: 'operation-1', sessionId: 'session-1' });
    api.request.mockImplementation(async (path: string, _method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return { items: [] };
      if (path.endsWith('/start-tickets')) return { ticket: 'ephemeral-ticket', operationId: body?.operationId, expiresAt: 'later', sessionId: 'session-1' };
      if (path.endsWith('/sessions/session-1')) return { id: 'session-1', operationId: body?.operationId ?? 'operation-1', deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, state: 'starting', endRequested: false, createdAt: '2026-10-21T00:00:00Z' };
      throw new Error(`Unexpected request ${path}`);
    });
    api.status.mockResolvedValue({ ...bleStatus, state: 'starting', operationId: 'operation-1', sessionId: 'session-1' });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    const connectButton = wrapper.findAll('button').find(button => button.text() === 'Připojit')!;
    await connectButton.trigger('click'); await flushPromises();
    expect((wrapper.get('#conversation-openai-consent').element as HTMLInputElement).checked).toBe(false);
    expect(wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!.attributes('disabled')).toBeDefined();
    expect(api.request.mock.calls.some(call => call[0].endsWith('/start-tickets'))).toBe(false);
    await wrapper.get('#conversation-openai-consent').setValue(true);
    const startButton = wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!;
    await startButton.trigger('click'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/start-tickets', 'POST', expect.objectContaining({ protocolVersion: 1, deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, openAiConsent: true }), expect.any(AbortSignal));
    expect(api.start).toHaveBeenCalledWith('ephemeral-ticket', expect.any(String));
    expect(api.status).toHaveBeenCalled();
    const operationId = api.start.mock.calls[0][1];
    const ticketRequest = api.request.mock.calls.find(call => call[0].endsWith('/start-tickets'));
    expect(ticketRequest?.[2].operationId).toBe(operationId);
    expect(JSON.stringify(api.request.mock.calls)).not.toContain('ephemeral-ticket');
    wrapper.unmount();
  });

  it('keeps reconciling a queued start when the first backend list is empty, then offers account-owned end', async () => {
    vi.useFakeTimers();
    let sessionLists = 0;
    api.request.mockImplementation(async (path: string, method?: string) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) {
        sessionLists += 1;
        return { items: sessionLists >= 4 ? [{ id: 'session-live', operationId: api.start.mock.calls[0]?.[1], deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, state: 'running', endRequested: false, createdAt: 1792540800 }] : [] };
      }
      if (path.endsWith('/start-tickets')) return { ticket: 'ephemeral-ticket', operationId: 'op', expiresAt: 'later' };
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    api.status.mockResolvedValue({ ...bleStatus, challenge: 'fresh-challenge' });
    api.start.mockResolvedValue({ ...bleStatus, challenge: '', state: 'starting', operationId: 'op' });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    await wrapper.get('#conversation-openai-consent').setValue(true);
    await wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('Ověřujeme, zda kufr rozhovor přijal');
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(wrapper.text()).toContain('Probíhá');
    expect(wrapper.findAll('button').some(button => button.text() === 'Ukončit rozhovor')).toBe(true);
    expect(api.start).toHaveBeenCalledTimes(1);
    wrapper.unmount(); vi.useRealTimers();
  });

  it('uses a fresh BLE challenge for a new ticket after the previous session completes', async () => {
    const challenges = ['challenge-one', 'challenge-two', 'challenge-three', 'challenge-four'];
    let ticketCount = 0;
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return { items: [] };
      if (path.endsWith('/start-tickets')) { ticketCount += 1; return { ticket: `ticket-${ticketCount}`, operationId: `operation-${ticketCount}`, expiresAt: 'later', sessionId: `session-${ticketCount}` }; }
      if (/\/sessions\/session-\d+$/.test(path)) return { id: path.split('/').at(-1), operationId: `operation-${ticketCount}`, deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, state: 'completed', endRequested: false, createdAt: 1792540800 };
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    api.status.mockImplementation(async () => ({ ...bleStatus, challenge: challenges.shift() ?? 'challenge-next' }));
    api.start.mockImplementation(async (_ticket: string, operationId: string) => ({ ...bleStatus, challenge: '', state: 'starting', operationId, sessionId: `session-${ticketCount}` }));
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    await wrapper.get('#conversation-openai-consent').setValue(true);
    const start = () => wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!.trigger('click');
    await start(); await flushPromises(); await start(); await flushPromises();
    const tickets = api.request.mock.calls.filter(call => call[0].endsWith('/start-tickets'));
    expect(tickets).toHaveLength(2);
    expect(tickets.map(call => call[2].challenge)).toEqual(['challenge-one', 'challenge-three']);
    expect(api.status).toHaveBeenCalledTimes(4);
    wrapper.unmount();
  });

  it.each(['sessions', 'ble'] as const)('does not issue a ticket when consent is withdrawn during %s reconciliation', async stage => {
    const sessionReply = deferred<{ items: [] }>();
    const bleReply = deferred<typeof bleStatus>();
    let delaying = false;
    api.request.mockImplementation(async (path: string, _method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return delaying && stage === 'sessions' ? sessionReply.promise : { items: [] };
      if (path.endsWith('/start-tickets')) return { ticket: 'consented-ticket', operationId: body?.operationId, expiresAt: 'later' };
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    api.status.mockImplementation(async () => delaying && stage === 'ble' ? bleReply.promise : bleStatus);
    api.start.mockResolvedValue({ ...bleStatus, state: 'starting', operationId: 'accepted-operation' });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises(); await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    const consent = wrapper.get('#conversation-openai-consent');
    await consent.setValue(true);
    delaying = true;
    const startButton = () => wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!;
    await startButton().trigger('click'); await flushPromises();
    await consent.setValue(false);
    delaying = false;
    sessionReply.resolve({ items: [] }); bleReply.resolve(bleStatus);
    await flushPromises();
    expect(api.request.mock.calls.filter(call => call[0].endsWith('/start-tickets'))).toHaveLength(0);
    expect(api.start).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Souhlas s OpenAI byl odvolán před odesláním žádosti');
    expect(wrapper.text()).not.toContain('Ověřujeme, zda kufr rozhovor přijal');
    expect(startButton().attributes('disabled')).toBeDefined();
    await consent.setValue(true);
    await startButton().trigger('click'); await flushPromises();
    expect(api.request.mock.calls.filter(call => call[0].endsWith('/start-tickets'))).toHaveLength(1);
    expect(api.start).toHaveBeenCalledOnce();
    wrapper.unmount();
  });

  it('reuses the exact immutable ticket request and operation id for an uncertain retry', async () => {
    const ticketRequests: Record<string, unknown>[] = [];
    api.request.mockImplementation(async (path: string, _method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return { items: [] };
      if (path.endsWith('/start-tickets')) { ticketRequests.push(body!); return { ticket: `ticket-${ticketRequests.length}`, operationId: body?.operationId, expiresAt: 'later' }; }
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    const currentChallenges = ['ticket-challenge-original', 'different-challenge-after-error'];
    api.status.mockImplementation(async () => ({ ...bleStatus, challenge: currentChallenges.shift() ?? 'later-challenge' }));
    api.start.mockRejectedValueOnce(new Error('Potvrzení kufru zatím nedorazilo')).mockResolvedValue({ ...bleStatus, state: 'starting', challenge: '', operationId: 'accepted-later' });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises(); await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    await wrapper.get('#conversation-openai-consent').setValue(true);
    const startButton = () => wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!;
    await startButton().trigger('click'); await flushPromises();
    const original = structuredClone(ticketRequests[0]);
    await wrapper.get('#conversation-openai-consent').setValue(false);
    await startButton().trigger('click'); await flushPromises();
    expect(ticketRequests).toHaveLength(2);
    expect(ticketRequests[1]).toEqual(original);
    expect(ticketRequests[1].operationId).toBe(ticketRequests[0].operationId);
    expect(ticketRequests[0].challenge).toBe('ticket-challenge-original');
    expect(api.status).toHaveBeenCalledTimes(2);
    wrapper.unmount();
  });

  it('retires a pre-BLE issuer rejection and requires a fresh explicit ticket click', async () => {
    const ticketRequests: Record<string, unknown>[] = [];
    api.request.mockImplementation(async (path: string, _method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return { items: [] };
      if (path.endsWith('/start-tickets')) {
        ticketRequests.push(body!);
        if (ticketRequests.length === 1) throw Object.assign(new Error('Kufr neposkytl platnou výzvu.'), { status: 409, code: 'device_challenge_unavailable' });
        return { ticket: 'fresh-ticket', operationId: body?.operationId, expiresAt: 'later' };
      }
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    const challenges = ['issuer-challenge', 'after-rejection', 'fresh-click-challenge'];
    api.status.mockImplementation(async () => ({ ...bleStatus, challenge: challenges.shift() ?? 'later' }));
    api.start.mockResolvedValue({ ...bleStatus, state: 'starting', operationId: 'fresh-operation' });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises(); await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    await wrapper.get('#conversation-openai-consent').setValue(true);
    const startButton = () => wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!;
    await startButton().trigger('click'); await flushPromises();
    expect(api.start).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Kufr odmítl žádost ještě před pokusem o připojení');
    expect(wrapper.text()).not.toContain('Ověřujeme, zda kufr rozhovor přijal');
    await startButton().trigger('click'); await flushPromises();
    expect(ticketRequests).toHaveLength(2);
    expect(ticketRequests[1].operationId).not.toBe(ticketRequests[0].operationId);
    expect(ticketRequests[1].challenge).toBe('fresh-click-challenge');
    expect(api.start).toHaveBeenCalledOnce();
    wrapper.unmount();
  });

  it('creates a new operation only after the server confirms an expired unredeemed request', async () => {
    let listCount = 0;
    const ticketRequests: Record<string, unknown>[] = [];
    api.request.mockImplementation(async (path: string, _method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) {
        listCount += 1;
        return { items: [], ...(listCount === 4 ? { expiredOperations: [ticketRequests[0].operationId] } : {}) };
      }
      if (path.endsWith('/start-tickets')) { ticketRequests.push(body!); return { ticket: `ticket-${ticketRequests.length}`, operationId: body?.operationId, expiresAt: 'later' }; }
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus);
    const challenges = ['ticket-challenge-old', 'reconciled-after-error', 'ticket-challenge-new'];
    api.status.mockImplementation(async () => ({ ...bleStatus, challenge: challenges.shift() ?? 'unused' }));
    api.start.mockRejectedValueOnce(new Error('Přijetí žádosti se nepodařilo ověřit')).mockResolvedValue({ ...bleStatus, state: 'starting', challenge: '' });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises(); await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    await wrapper.get('#conversation-openai-consent').setValue(true);
    const startButton = () => wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!;
    await startButton().trigger('click'); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit stav rozhovoru')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('Předchozí žádost vypršela dříve, než ji kufr přijal');
    expect(ticketRequests).toHaveLength(1);
    await startButton().trigger('click'); await flushPromises();
    expect(ticketRequests).toHaveLength(2);
    expect(ticketRequests[1].operationId).not.toBe(ticketRequests[0].operationId);
    expect(ticketRequests[1].challenge).toBe('ticket-challenge-new');
    wrapper.unmount();
  });

  it('clears pending reconciliation on account switch and preserves unsaved edits while enrichment finishes', async () => {
    vi.useFakeTimers();
    let listCalls = 0;
    let profileReads = 0;
    api.request.mockImplementation(async (path: string, method?: string) => {
      if (path.endsWith('/character')) {
        profileReads += 1;
        return { profile: { ...profile, status: profileReads > 1 ? 'ready' : 'queued', enrichment: { summary: profileReads > 1 ? 'Hotovo' : 'Čeká' } } };
      }
      if (path.endsWith('/sessions')) { listCalls += 1; return { items: [] }; }
      if (path.endsWith('/start-tickets')) return { ticket: 'ticket', operationId: 'operation', expiresAt: 'later' };
      throw new Error(`Unexpected request ${path}`);
    });
    api.discover.mockResolvedValue({ devices: [{ id: 'device-1', name: 'Poustevník' }] });
    api.connect.mockResolvedValue(bleStatus); api.status.mockResolvedValue(bleStatus);
    api.start.mockResolvedValue({ ...bleStatus, state: 'starting', challenge: '', operationId: 'op' });
    const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }, { path: '/me/conversation', component: CustomerView }] });
    await instance.push('/me/character'); await instance.isReady();
    const wrapper = mount(CustomerView, { global: { plugins: [instance] } });
    await flushPromises();
    await wrapper.get('#character-name').setValue('Moje rozepsané jméno');
    await vi.advanceTimersByTimeAsync(3000); await flushPromises();
    expect((wrapper.get('#character-name').element as HTMLInputElement).value).toBe('Moje rozepsané jméno');
    expect(wrapper.text()).toContain('Připraveno');
    await instance.push('/me/conversation'); await flushPromises();
    await wrapper.get('button').trigger('click'); await flushPromises();
    await wrapper.get('select').setValue('device-1');
    await wrapper.findAll('button').find(button => button.text() === 'Připojit')!.trigger('click'); await flushPromises();
    await wrapper.get('#conversation-openai-consent').setValue(true);
    await wrapper.findAll('button').find(button => button.text() === 'Promluvit s kufrem')!.trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('Ověřujeme, zda kufr rozhovor přijal');
    const callsBeforeSwitch = listCalls;
    api.setAccount({ id: 'customer-2', displayName: 'Second guest', roles: ['Customer'] });
    await flushPromises(); await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    const callsAfterNewAccountLoad = listCalls;
    expect(callsAfterNewAccountLoad).toBeGreaterThan(callsBeforeSwitch);
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(listCalls).toBe(callsAfterNewAccountLoad);
    expect(wrapper.text()).not.toContain('Ověřujeme, zda kufr rozhovor přijal');
    wrapper.unmount(); vi.useRealTimers();
  });

  it('labels profile/session statuses and renders Unix-second timestamps as real dates', async () => {
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile: { ...profile, status: 'needs_clarification', enrichment: { needsClarification: true, ambiguities: ['Které období máte na mysli?'], facts: [{ text: 'Uvedená postava pochází z knihy.', sourceUrls: ['https://example.test/source'] }], conversationIdeas: ['Zeptat se na oblíbenou scénu.'], sources: [{ title: 'Katalog', url: 'https://example.test/catalog' }] } } };
      if (path.endsWith('/sessions')) return { items: [{ id: 'session-1', operationId: 'op', deviceId: 'device-1', profileId: 'profile-1', profileRevision: 1, state: 'completed', endRequested: false, createdAt: 1792540800 }] };
      throw new Error(`Unexpected request ${path}`);
    });
    const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }, { path: '/me/conversation', component: CustomerView }] });
    await instance.push('/me/character'); await instance.isReady();
    const wrapper = mount(CustomerView, { global: { plugins: [instance] } });
    await flushPromises();
    expect(wrapper.text()).toContain('Potřebuje upřesnění');
    expect(wrapper.text()).toContain('Server potřebuje upřesnit');
    expect(wrapper.text()).toContain('Zeptat se na oblíbenou scénu.');
    expect(wrapper.text()).toContain('https://example.test/source');
    expect(wrapper.text()).toContain('https://example.test/catalog');
    await instance.push('/me/conversation'); await flushPromises();
    expect(wrapper.text()).toContain('Dokončen');
    expect(wrapper.text()).not.toContain('needs_clarification');
    expect(wrapper.text()).not.toContain('ID profilu');
    expect(wrapper.text()).not.toContain('1970');
    wrapper.unmount();
  });

  it('reports unsupported Safari honestly without attempting BLE discovery', async () => {
    api.supported = false;
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    expect(wrapper.text()).toContain('neumí připojit kufr');
    expect(api.discover).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('creates a first profile at revision zero and reserves a new profile id for explicit new-character action', async () => {
    api.request.mockImplementation(async (path: string, method?: string, body?: Record<string, unknown>) => {
      if (path.endsWith('/character') && method === 'PUT') {
        return { profile: { profileId: body?.newCharacter ? 'profile-2' : 'profile-1', revision: body?.newCharacter ? 1 : 1, userInput: body?.userInput, status: 'queued' } };
      }
      if (path.endsWith('/character')) return { profile: null };
      if (path.endsWith('/sessions')) return { items: [] };
      throw new Error(`Unexpected request ${path}`);
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } });
    await flushPromises();
    await wrapper.get('#character-name').setValue('Luna');
    await wrapper.get('#character-world').setValue('Moon Saga');
    await wrapper.get('form').trigger('submit'); await flushPromises();
    const initial = api.request.mock.calls.find(call => call[0].endsWith('/character') && call[1] === 'PUT')![2];
    expect(initial).toMatchObject({ expectedRevision: 0, expectedProfileId: null, userInput: { name: 'Luna', world: 'Moon Saga' } });
    expect(initial).not.toHaveProperty('newCharacter');
    await wrapper.findAll('button').find(button => button.text() === 'Nová postava')!.trigger('click'); await flushPromises();
    expect(api.request.mock.calls.filter(call => call[1] === 'PUT')).toHaveLength(1);
    expect(wrapper.get('#new-character-name').element).toHaveProperty('value', '');
    await wrapper.get('#new-character-name').setValue('Mira');
    await wrapper.get('#new-character-world').setValue('Star Book');
    await wrapper.get('dialog form').trigger('submit'); await flushPromises();
    const writes = api.request.mock.calls.filter(call => call[0].endsWith('/character') && call[1] === 'PUT');
    expect(writes[1][2]).toMatchObject({ expectedRevision: 1, expectedProfileId: 'profile-1', newCharacter: true, openAiConsent: false, userInput: { name: 'Mira', world: 'Star Book' } });
    expect(wrapper.get('#character-name').element).toHaveProperty('value', 'Mira');
    expect(wrapper.get('#character-confirmation-title').text()).toBe('Mira');
    wrapper.unmount();
  });

  it('cancels a new identity without saving or losing existing unsaved corrections and consent', async () => {
    api.request.mockImplementation(async (path: string) => path.endsWith('/character') ? { profile } : { items: [] });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } });
    await flushPromises();
    await wrapper.get('#character-name').setValue('Moje neuložená oprava');
    await wrapper.get('#character-openai-consent').setValue(true);
    await wrapper.findAll('button').find(button => button.text() === 'Nová postava')!.trigger('click');
    expect((wrapper.get('#new-character-openai-consent').element as HTMLInputElement).checked).toBe(false);
    await wrapper.get('#new-character-name').setValue('Rozpracovaná nová');
    await wrapper.findAll('button').find(button => button.text() === 'Zrušit')!.trigger('click');
    await flushPromises();
    expect(wrapper.get('dialog').element).toHaveProperty('open', false);
    expect(wrapper.get('#character-name').element).toHaveProperty('value', 'Moje neuložená oprava');
    expect((wrapper.get('#character-openai-consent').element as HTMLInputElement).checked).toBe(true);
    expect(api.request.mock.calls.filter(call => call[1] === 'PUT')).toHaveLength(0);
    wrapper.unmount();
  });

  it('preserves both drafts after a rejected new identity and retries only on confirmation', async () => {
    let writes = 0;
    api.request.mockImplementation(async (path: string, method?: string, body?: Record<string, unknown>) => {
      if (method === 'PUT') {
        if (++writes === 1) throw new Error('Profil se mezitím změnil. Obnovte jej.');
        return { profile: { ...profile, profileId: 'profile-2', userInput: body?.userInput, status: 'saved' } };
      }
      return path.endsWith('/character') ? { profile } : { items: [] };
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router('/me/character')] } });
    await flushPromises();
    await wrapper.get('#character-name').setValue('Původní koncept');
    await wrapper.findAll('button').find(button => button.text() === 'Nová postava')!.trigger('click');
    await wrapper.get('#new-character-name').setValue('Mira');
    await wrapper.get('#new-character-world').setValue('Star Book');
    await wrapper.get('dialog form').trigger('submit'); await flushPromises();
    expect(wrapper.get('dialog').element).toHaveProperty('open', true);
    expect(wrapper.get('dialog [role="alert"]').text()).toContain('Profil se mezitím změnil');
    expect(wrapper.get('#character-name').element).toHaveProperty('value', 'Původní koncept');
    expect(wrapper.get('#new-character-name').element).toHaveProperty('value', 'Mira');
    expect(wrapper.get('#character-confirmation-title').text()).toBe('Luna');
    expect(writes).toBe(1);
    await wrapper.get('dialog form').trigger('submit'); await flushPromises();
    expect(writes).toBe(2);
    expect(wrapper.get('dialog').element).toHaveProperty('open', false);
    expect(wrapper.text()).toContain('Postava je uložená bez AI přípravy');
    wrapper.unmount();
  });

  it('ignores an older profile response after the signed-in account changes', async () => {
    const oldProfile = deferred<{ profile: typeof profile }>();
    api.request.mockImplementation((path: string) => {
      if (path.endsWith('/character')) return api.request.mock.calls.filter(call => call[0].endsWith('/character')).length === 1
        ? oldProfile.promise : Promise.resolve({ profile: { ...profile, profileId: 'new-profile', userInput: { name: 'Mira', world: 'Star Book' } } });
      return Promise.resolve({ items: [] });
    });
    const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }] });
    await instance.push('/me/character'); await instance.isReady();
    const wrapper = mount(CustomerView, { global: { plugins: [instance] } });
    api.setAccount({ id: 'customer-2', displayName: 'Second guest', roles: ['Customer'] });
    await flushPromises();
    oldProfile.resolve({ profile }); await flushPromises();
    expect((wrapper.get('#character-name').element as HTMLInputElement).value).toBe('Mira');
    wrapper.unmount();
  });

  it('resolves a selected historical conversation through its exact server session binding', async () => {
    const locators: Record<string, string> = {
      newest: 'c907bf23-cdf6-4e0a-b334-9558fb1ee6bc',
      older: '8d504e60-ad89-4d44-bfd7-8191ac4dc481',
    };
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile };
      if (path.endsWith('/sessions')) return { items: ['newest', 'older'].map((id, index) => ({
        id, operationId: `operation-${id}`, deviceId: 'device-1', profileId: profile.profileId,
        profileRevision: 1, state: 'completed', endRequested: false, createdAt: 1792540800 - index,
      })) };
      const id = path.split('/').at(-2)!;
      return {
        schemaVersion: 1, sessionId: id, resultLocator: locators[id],
        readiness: { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true },
        accessKind: 'originalOwner', canReadPrivate: true, canManage: true, canUnlink: false,
      };
    });
    const wrapper = mount(CustomerView, { global: { plugins: [await router()] } });
    await flushPromises();
    expect(wrapper.get('a[href^="/keepsake/"]').attributes('href')).toContain(locators.newest);
    await wrapper.get('#result-session').setValue('older');
    await flushPromises();
    expect(wrapper.get('a[href^="/keepsake/"]').attributes('href')).toContain(locators.older);
    const reads = api.request.mock.calls.filter(call => call[0].endsWith('/result'));
    expect(reads.map(call => call[0])).toEqual(['/app/v1/customer/sessions/newest/result', '/app/v1/customer/sessions/older/result']);
    expect(reads.every(call => call[1] === 'GET')).toBe(true);
    expect(api.start).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('clears drafts and personal-memory consent immediately when profile reload for a new account fails', async () => {
    let profileReads = 0;
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) {
        profileReads += 1;
        if (profileReads === 1) return { profile };
        throw new Error('Profil druhého účtu nelze načíst');
      }
      return { items: [] };
    });
    const instance = createRouter({ history: createMemoryHistory(), routes: [{ path: '/me/character', component: CustomerView }, { path: '/me/conversation', component: CustomerView }] });
    await instance.push('/me/character'); await instance.isReady();
    const wrapper = mount(CustomerView, { global: { plugins: [instance] } });
    await flushPromises();
    await wrapper.get('#character-name').setValue('Starý soukromý návrh');
    await instance.push('/me/conversation'); await flushPromises();
    await wrapper.get('#personal-memory-consent').setValue(true);
    await instance.push('/me/character'); await flushPromises();
    api.setAccount({ id: 'customer-2', displayName: 'Second guest', roles: ['Customer'] });
    await flushPromises();
    expect((wrapper.get('#character-name').element as HTMLInputElement).value).toBe('');
    expect((wrapper.get('#character-name').element as HTMLInputElement).value).toBe('');
    await instance.push('/me/conversation'); await flushPromises();
    expect((wrapper.get('#personal-memory-consent').element as HTMLInputElement).checked).toBe(false);
    expect(wrapper.text()).not.toContain('Starý soukromý návrh');
    wrapper.unmount();
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createMemoryHistory, createRouter } from 'vue-router';
import { account, offlineSession, signingOut } from '../identity/session';
import CustomerCompletionUpdates from './CustomerCompletionUpdates.vue';

const request = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return {
    accountRequest: request,
    account: ref(null), accountDeletionRequested: ref(false), offlineSession: ref(false), signingOut: ref(false),
  };
});

const locator = '11111111-1111-4111-8111-111111111111';
const eventId = locator;
const originalOnlineDescriptor = Object.getOwnPropertyDescriptor(window.navigator, 'onLine');
const update = (overrides: Record<string, unknown> = {}) => ({
  id: eventId, sequence: 1, kind: 'keepsake', state: 'ready', reason: null, resultLocator: locator,
  canRetry: false, hasStory: true, hasArtwork: true, occurredAt: '2026-10-22T09:10:11Z', readAt: null, ...overrides,
});
const page = (items = [update()], nextCursor: string | null = null) => ({ schemaVersion: 1, items, nextCursor });
const mountedWrappers: ReturnType<typeof mount>[] = [];

function setOnline(value: boolean): void {
  Object.defineProperty(window.navigator, 'onLine', { configurable: true, get: () => value });
}

function setup(locale: 'cs' | 'en' = 'cs') {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/keepsake/', component: { template: '<div>Result</div>' } },
      { path: '/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  });
  const wrapper = mount(CustomerCompletionUpdates, { props: { locale }, global: { plugins: [router] } });
  mountedWrappers.push(wrapper);
  return { wrapper, router };
}

beforeEach(() => {
  vi.useRealTimers();
  vi.resetAllMocks();
  setOnline(true);
  account.value = { id: 'account-a', displayName: 'Account A', roles: [] };
  offlineSession.value = false;
  signingOut.value = false;
});
afterEach(() => {
  for (const wrapper of mountedWrappers.splice(0).reverse()) wrapper.unmount();
  vi.restoreAllMocks();
  if (originalOnlineDescriptor) Object.defineProperty(window.navigator, 'onLine', originalOnlineDescriptor);
  else Reflect.deleteProperty(window.navigator, 'onLine');
  vi.useRealTimers();
});

describe('CustomerCompletionUpdates', () => {
  it('shows ready, partial, failed and empty states with explicit result navigation', async () => {
    const partialLocator = '22222222-2222-4222-8222-222222222222';
    const failedLocator = '33333333-3333-4333-8333-333333333333';
    request.mockResolvedValueOnce(page([
      update(), update({ id: partialLocator, resultLocator: partialLocator, sequence: 2, kind: 'portrait', state: 'partial', reason: 'reconciliation_required', hasArtwork: true }),
      update({ id: failedLocator, resultLocator: failedLocator, sequence: 3, state: 'failed', reason: 'generation_failed', hasStory: false }),
    ]));
    const { wrapper } = setup();
    await flushPromises();
    expect(wrapper.text()).toContain('Připraveno');
    expect(wrapper.text()).toContain('Částečně dostupné');
    expect(wrapper.text()).toContain('Výsledek se ověřuje.');
    expect(wrapper.text()).toContain('Nepodařilo se dokončit');
    expect(wrapper.text()).toContain('Příběh je dostupný');
    expect(wrapper.find('a[href*="/keepsake/?account=1&k="]').exists()).toBe(true);
    wrapper.unmount();

    request.mockResolvedValueOnce(page([]));
    const empty = setup().wrapper;
    await flushPromises();
    expect(empty.text()).toContain('Zatím tu nejsou žádné aktualizace výsledků.');
    empty.unmount();
  });

  it('marks an unread row only after its explicit button and preserves the server acknowledgement on refresh', async () => {
    request.mockResolvedValueOnce(page());
    const { wrapper } = setup();
    await flushPromises();
    expect(request.mock.calls.some(call => call[1] === 'PUT')).toBe(false);
    await wrapper.findAll('button').find(button => button.text() === 'Označit jako přečtené')!.trigger('click');
    await flushPromises();
    expect(request).toHaveBeenCalledWith(`/app/v1/customer/completion-updates/${eventId}/read`, 'PUT', { schemaVersion: 1 }, expect.any(AbortSignal));
    expect(wrapper.text()).toContain('Přečteno');

    request.mockResolvedValueOnce(page([update({ readAt: '2026-10-22T09:11:00Z' })]));
    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(wrapper.findAll('button').some(button => button.text() === 'Označit jako přečtené')).toBe(false);
    wrapper.unmount();
  });

  it('shows loading, request failure, browser offline and English copy without requesting notification permission', async () => {
    const permission = typeof Notification !== 'undefined' && typeof Notification.requestPermission === 'function'
      ? vi.spyOn(Notification, 'requestPermission') : null;
    expect(navigator.onLine).toBe(true);
    expect(offlineSession.value).toBe(false);
    let finish!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finish = resolve));
    const loading = setup().wrapper;
    await nextTick();
    expect(request).toHaveBeenCalledTimes(1);
    expect(loading.text()).toContain('Načítám vaše výsledky…');
    finish(page());
    await flushPromises();
    loading.unmount();
    if (permission) expect(permission).not.toHaveBeenCalled();

    request.mockRejectedValueOnce(new Error('offline'));
    const failed = setup().wrapper;
    await flushPromises();
    expect(failed.text()).toContain('Výsledky jsou dočasně nedostupné. Zkuste je obnovit.');
    failed.unmount();

    request.mockReset();
    setOnline(false);
    const offline = setup('en').wrapper;
    expect(offline.text()).toContain('Account results are unavailable in offline mode.');
    expect(request).not.toHaveBeenCalled();
    offline.unmount();
    permission?.mockRestore();
  });

  it('bounds polling and restarts a bounded cycle after manual refresh', async () => {
    vi.useFakeTimers();
    request.mockImplementation(() => Promise.resolve(page([update({ state: 'processing', reason: null })])));
    const wrapper = setup().wrapper;
    await flushPromises();
    await vi.advanceTimersByTimeAsync(12 * 5000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(13);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(13);
    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(14);
    wrapper.unmount();
    vi.useRealTimers();
  });

  it('keeps an explicit acknowledgement after the final bounded poll returns its earlier unread snapshot', async () => {
    vi.useFakeTimers();
    let finishWrite!: (value: unknown) => void;
    let finishFinalPoll!: (value: unknown) => void;
    request.mockImplementation((_path: string, method: string) => {
      if (method === 'PUT') return new Promise(resolve => finishWrite = resolve);
      const getCount = request.mock.calls.filter(call => call[1] === 'GET').length;
      if (getCount === 13) return new Promise(resolve => finishFinalPoll = resolve);
      return Promise.resolve(page([update({ state: 'processing' })]));
    });

    const wrapper = setup().wrapper;
    await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Označit jako přečtené')!.trigger('click');
    await flushPromises();

    await vi.advanceTimersByTimeAsync(12 * 5000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(13);
    finishWrite(undefined);
    await flushPromises();
    expect(wrapper.find('.completion-updates__item').classes()).not.toContain('completion-updates__item--unread');

    finishFinalPoll(page([update({ state: 'processing' })]));
    await flushPromises();
    expect(wrapper.find('.completion-updates__item').classes()).not.toContain('completion-updates__item--unread');
    expect(wrapper.text()).toContain('Přečteno');
    expect(wrapper.findAll('button').some(button => button.text() === 'Označit jako přečtené')).toBe(false);

    wrapper.unmount();
    vi.useRealTimers();
  });

  it('does not poll a terminal partial result after generation failure', async () => {
    vi.useFakeTimers();
    request.mockResolvedValueOnce(page([update({ state: 'partial', reason: 'generation_failed' })]));
    const wrapper = setup().wrapper;
    await flushPromises();
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(1);
    wrapper.unmount();
    vi.useRealTimers();
  });

  it('clears account A data and fences its late read and write after an account switch', async () => {
    let finishRead!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishRead = resolve));
    const wrapper = setup().wrapper;
    const getSignal = request.mock.calls[0][3] as AbortSignal;
    let finishAccountB!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishAccountB = resolve));
    account.value = { id: 'account-b', displayName: 'Account B', roles: [] };
    expect(getSignal.aborted).toBe(true);
    finishRead(page());
    await flushPromises();
    expect(wrapper.find('ol').exists()).toBe(false);
    finishAccountB(page());
    await flushPromises();
    let finishWrite!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishWrite = resolve));
    await wrapper.findAll('button').find(button => button.text() === 'Označit jako přečtené')!.trigger('click');
    await flushPromises();
    const pendingWrite = request.mock.calls.find(call => call[1] === 'PUT');
    account.value = { id: 'account-c', displayName: 'Account C', roles: [] };
    expect((pendingWrite?.[3] as AbortSignal).aborted).toBe(true);
    finishWrite(undefined);
    await flushPromises();
    expect(wrapper.text()).not.toContain('Přečteno');
    wrapper.unmount();
  });

  it('aborts and clears immediately on sign-out and does not dispatch another poll after disconnect', async () => {
    vi.useFakeTimers();
    let finish!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finish = resolve));
    const wrapper = setup().wrapper;
    const signal = request.mock.calls[0][3] as AbortSignal;
    signingOut.value = true;
    expect(signal.aborted).toBe(true);
    finish(page([update({ state: 'processing' })]));
    await flushPromises();
    expect(wrapper.find('ol').exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request).toHaveBeenCalledTimes(1);
    wrapper.unmount();
    vi.useRealTimers();
  });

  it('aborts late reads and acknowledgements on real browser offline and restores reads on reconnect', async () => {
    let finishInitialRead!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishInitialRead = resolve));
    const wrapper = setup().wrapper;
    const readSignal = request.mock.calls[0][3] as AbortSignal;
    setOnline(false);
    window.dispatchEvent(new Event('offline'));
    expect(readSignal.aborted).toBe(true);
    finishInitialRead(page());
    await flushPromises();
    expect(wrapper.find('ol').exists()).toBe(false);
    expect(request.mock.calls.filter(call => call[1] === 'GET')).toHaveLength(1);

    request.mockResolvedValueOnce(page());
    setOnline(true);
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    let finishAcknowledgement!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => finishAcknowledgement = resolve));
    await wrapper.findAll('button').find(button => button.text() === 'Označit jako přečtené')!.trigger('click');
    await flushPromises();
    const writeCall = request.mock.calls.find(call => call[1] === 'PUT');
    expect(writeCall).toBeDefined();
    setOnline(false);
    window.dispatchEvent(new Event('offline'));
    expect((writeCall?.[3] as AbortSignal).aborted).toBe(true);
    finishAcknowledgement(undefined);
    await flushPromises();
    expect(wrapper.find('ol').exists()).toBe(false);
    expect(request.mock.calls.filter(call => call[1] === 'PUT')).toHaveLength(1);
    wrapper.unmount();
  });

  it('aborts account requests and removes timers on unmount; refresh replaces cursor pages and deduplicates older rows', async () => {
    vi.useFakeTimers();
    const older = update({ id: locator.replace('11111111', '22222222'), sequence: 1, state: 'ready' });
    const other = update({
      id: locator.replace('11111111', '33333333'), resultLocator: locator.replace('11111111', '44444444'),
      sequence: 1, kind: 'portrait',
    });
    request.mockResolvedValueOnce(page([update({ sequence: 2 })], 'cursor-one'))
      .mockResolvedValueOnce(page([older, other], null))
      .mockResolvedValueOnce(page([update({ sequence: 3 })], null));
    const wrapper = setup().wrapper;
    await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Načíst starší aktualizace')!.trigger('click');
    await flushPromises();
    expect(wrapper.findAll('.completion-updates__item').length).toBe(2);
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit')!.trigger('click');
    await flushPromises();
    expect(wrapper.findAll('.completion-updates__item').length).toBe(1);
    const signal = request.mock.calls.at(-1)?.[3] as AbortSignal;
    wrapper.unmount();
    expect(signal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(request).toHaveBeenCalledTimes(3);
    vi.useRealTimers();
  });

  it('renders translated copy and does not auto-acknowledge when opening a result', async () => {
    request.mockResolvedValueOnce(page());
    const { wrapper, router } = setup('en');
    await flushPromises();
    expect(wrapper.text()).toContain('Your results');
    await wrapper.get('a').trigger('click');
    await flushPromises();
    expect(router.currentRoute.value.fullPath).toBe(`/keepsake/?account=1&k=${locator}`);
    expect(request.mock.calls.some(call => call[1] === 'PUT')).toBe(false);
    wrapper.unmount();
  });
});

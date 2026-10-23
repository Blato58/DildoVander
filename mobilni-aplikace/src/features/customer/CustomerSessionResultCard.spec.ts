import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { createMemoryHistory, createRouter } from 'vue-router';

const api = vi.hoisted(() => ({
  request: vi.fn(),
  setAccount: (_value: unknown) => {},
  setDeleting: (_value: boolean) => {},
}));

vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string; displayName: string; roles: string[] } | null>(null);
  const accountDeletionRequested = ref(false);
  api.setAccount = value => { account.value = value as typeof account.value; };
  api.setDeleting = value => { accountDeletionRequested.value = value; };
  return { account, accountDeletionRequested, accountRequest: api.request };
});

import CustomerSessionResultCard from './CustomerSessionResultCard.vue';

const sessionId = '6d8c7912-48c7-499c-a61a-138d0ca7932d';
const locator = 'e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
const customer = { id: 'customer-1', displayName: 'Host', roles: ['Customer'] };
const mounted: Array<{ unmount: () => void }> = [];
let originalOnLine: PropertyDescriptor | undefined;
let originalVisibility: PropertyDescriptor | undefined;

function readiness(
  state: string,
  overrides: Record<string, unknown> = {},
) {
  return {
    state,
    reason: null,
    canRetry: false,
    shouldPoll: false,
    hasStory: false,
    hasArtwork: false,
    ...overrides,
  };
}

function result(state: string, options: {
  sessionId?: string;
  locator?: string | null;
  readiness?: Record<string, unknown>;
  owner?: boolean;
} = {}) {
  const owner = options.owner ?? true;
  return {
    schemaVersion: 1,
    sessionId: options.sessionId ?? sessionId,
    resultLocator: options.locator === undefined ? locator : options.locator,
    readiness: readiness(state, options.readiness),
    accessKind: owner ? 'originalOwner' : null,
    canManage: owner,
    canReadPrivate: owner,
    canUnlink: false,
  };
}

async function render(id = sessionId) {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/:pathMatch(.*)*', component: { template: '<div />' } }],
  });
  await router.push('/');
  const wrapper = mount(CustomerSessionResultCard, {
    props: { sessionId: id },
    global: { plugins: [router] },
  });
  mounted.push(wrapper);
  return wrapper;
}

beforeEach(() => {
  originalOnLine = Object.getOwnPropertyDescriptor(navigator, 'onLine');
  originalVisibility = Object.getOwnPropertyDescriptor(document, 'visibilityState');
  Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
  api.setAccount(customer);
  api.setDeleting(false);
  api.request.mockReset();
  vi.useRealTimers();
});

afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount();
  vi.useRealTimers();
  if (originalOnLine) Object.defineProperty(navigator, 'onLine', originalOnLine);
  else Reflect.deleteProperty(navigator, 'onLine');
  if (originalVisibility) Object.defineProperty(document, 'visibilityState', originalVisibility);
  else Reflect.deleteProperty(document, 'visibilityState');
});

describe('customer session result card', () => {
  it('loads the nested endpoint response and links only a verified server locator', async () => {
    api.request.mockResolvedValue(result('ready'));
    const wrapper = await render();
    await flushPromises();

    expect(api.request).toHaveBeenCalledWith(
      `/app/v1/customer/sessions/${sessionId}/result`,
      'GET',
      undefined,
      expect.any(AbortSignal),
    );
    expect(wrapper.get(`a[href="/keepsake/?account=1&k=${locator}"]`).text()).toBe('Otevřít památku');
    expect(wrapper.text()).toContain('Památka je připravená.');
    wrapper.unmount();
  });

  it('does not invent navigation for an unbound pending result or expose retry/generation actions', async () => {
    api.request.mockResolvedValue(result('pending', {
      locator: null,
      owner: false,
      readiness: { reason: 'awaiting_result', shouldPoll: true },
    }));
    const wrapper = await render();
    await flushPromises();

    expect(wrapper.text()).toContain('Výsledek se připravuje.');
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.findAll('button').map(button => button.text())).toEqual(['Obnovit stav památky']);
    expect(api.request.mock.calls.filter(call => call[1] !== 'GET')).toHaveLength(0);
    wrapper.unmount();
  });

  it('preserves partial story, explains reconciliation and stops polling when the server makes it terminal', async () => {
    vi.useFakeTimers();
    api.request
      .mockResolvedValueOnce(result('partial', {
        readiness: {
          reason: 'reconciliation_required',
          canRetry: false,
          shouldPoll: true,
          hasStory: true,
          hasArtwork: false,
        },
      }))
      .mockResolvedValueOnce(result('partial', {
        readiness: {
          reason: 'generation_failed',
          canRetry: false,
          shouldPoll: false,
          hasStory: true,
          hasArtwork: false,
        },
      }));
    const wrapper = await render();
    await flushPromises();

    expect(wrapper.text()).toContain('Text památky je dostupný');
    expect(wrapper.text()).toContain('čeká na ověření');
    expect(wrapper.text()).toContain('automaticky znovu nevytváří');
    expect(wrapper.text()).not.toMatch(/hotov|dokončen/i);

    await vi.advanceTimersByTimeAsync(2500);
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(2);
    expect(wrapper.text()).toContain('Text památky je dostupný, ilustraci se nepodařilo připravit.');

    await vi.advanceTimersByTimeAsync(10_000);
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(2);
    wrapper.unmount();
  });

  it('qualifies a polled partial result through failure and only restores current claims after a fresh retry', async () => {
    vi.useFakeTimers();
    let failPoll!: (error: unknown) => void;
    api.request
      .mockResolvedValueOnce(result('partial', {
        readiness: {
          reason: 'reconciliation_required',
          shouldPoll: true,
          hasStory: true,
        },
      }))
      .mockImplementationOnce(() => new Promise((_, reject) => { failPoll = reject; }))
      .mockResolvedValueOnce(result('ready'));
    const wrapper = await render();
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);

    await vi.advanceTimersByTimeAsync(2500);
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Ověřuji aktuální stav. Poslední ověřený stav: Výsledek čeká na ověření. Aplikace ho automaticky znovu nevytváří.');
    expect(wrapper.text()).toContain('V posledním ověřeném výsledku:');
    expect(wrapper.text()).toContain('Text památky je dostupný.');

    failPoll(new TypeError('private-provider-poll-secret'));
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Stav se nepodařilo obnovit. Poslední ověřený stav: Výsledek čeká na ověření.');
    expect(wrapper.text()).toContain('Text památky je dostupný.');
    expect(wrapper.text()).not.toContain('private-provider-poll-secret');

    await vi.advanceTimersByTimeAsync(1000);
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);
    expect(wrapper.text()).toContain('Památka je připravená.');
    expect(wrapper.text()).not.toContain('V posledním ověřeném výsledku:');
    wrapper.unmount();
  });

  it('retries transient GET failure and allows manual refresh after a successful recovery', async () => {
    vi.useFakeTimers();
    api.request
      .mockRejectedValueOnce(new TypeError('private-provider-secret'))
      .mockResolvedValueOnce(result('pending', { locator: null, owner: false, readiness: { shouldPoll: true } }))
      .mockResolvedValueOnce(result('ready'));
    const wrapper = await render();
    await flushPromises();

    expect(wrapper.text()).toContain('Stav památky se nepodařilo ověřit. Obnovte stav.');
    expect(wrapper.text()).not.toContain('private-provider-secret');
    await vi.advanceTimersByTimeAsync(1000);
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(2);

    await wrapper.get('button').trigger('click');
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(3);
    expect(wrapper.find(`a[href="/keepsake/?account=1&k=${locator}"]`).exists()).toBe(true);
    wrapper.unmount();
  });

  it('moves a restored pending result to ready using status GETs only', async () => {
    vi.useFakeTimers();
    api.request
      .mockResolvedValueOnce(result('pending', {
        locator: null,
        owner: false,
        readiness: { reason: 'awaiting_result', shouldPoll: true },
      }))
      .mockResolvedValueOnce(result('ready'));
    const wrapper = await render();
    await flushPromises();
    expect(wrapper.text()).toContain('Výsledek se připravuje.');
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);

    await vi.advanceTimersByTimeAsync(2500);
    await flushPromises();
    expect(wrapper.text()).toContain('Památka je připravená.');
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);
    expect(api.request.mock.calls.every(call => call[1] === 'GET')).toBe(true);
    wrapper.unmount();
  });

  it('aborts while offline and restores a fresh status read when the app comes online', async () => {
    let finishOffline!: (value: unknown) => void;
    api.request
      .mockImplementationOnce(() => new Promise(resolve => { finishOffline = resolve; }))
      .mockResolvedValueOnce(result('ready'));
    const wrapper = await render();
    await flushPromises();
    const offlineSignal = api.request.mock.calls[0][3] as AbortSignal;

    window.dispatchEvent(new Event('offline'));
    await nextTick();
    expect(offlineSignal.aborted).toBe(true);
    finishOffline(result('ready'));
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);

    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(2);
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it('hides an old result route offline until an online status response verifies it again', async () => {
    let finishOnline!: (value: unknown) => void;
    api.request
      .mockResolvedValueOnce(result('ready'))
      .mockImplementationOnce(() => new Promise(resolve => { finishOnline = resolve; }));
    const wrapper = await render();
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);

    window.dispatchEvent(new Event('offline'));
    await nextTick();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Offline. Poslední ověřený stav: Památka je připravená.');

    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    finishOnline(result('ready'));
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it('labels a retained result as last verified during manual refresh and after a stale error', async () => {
    vi.useFakeTimers();
    let failRefresh!: (error: unknown) => void;
    api.request
      .mockResolvedValueOnce(result('ready'))
      .mockImplementationOnce(() => new Promise((_, reject) => { failRefresh = reject; }))
      .mockResolvedValueOnce(result('ready'));
    const wrapper = await render();
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);

    await wrapper.get('button').trigger('click');
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Ověřuji aktuální stav. Poslední ověřený stav: Památka je připravená.');
    failRefresh(new TypeError('private-provider-secret'));
    await flushPromises();

    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Stav se nepodařilo obnovit. Poslední ověřený stav: Památka je připravená.');
    expect(wrapper.text()).not.toContain('private-provider-secret');
    await vi.advanceTimersByTimeAsync(1000);
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it('hides the last verified route in the background until foreground refresh completes', async () => {
    const originalVisibility = Object.getOwnPropertyDescriptor(document, 'visibilityState');
    let finishForeground!: (value: unknown) => void;
    api.request
      .mockResolvedValueOnce(result('ready'))
      .mockImplementationOnce(() => new Promise(resolve => { finishForeground = resolve; }));
    const wrapper = await render();
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
    await nextTick();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    expect(wrapper.text()).toContain('Aplikace je na pozadí. Poslední ověřený stav: Památka je připravená.');

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);
    finishForeground(result('ready'));
    await flushPromises();
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(true);
    wrapper.unmount();
    if (originalVisibility) Object.defineProperty(document, 'visibilityState', originalVisibility);
    else Reflect.deleteProperty(document, 'visibilityState');
  });

  it('fences late responses on session/account/deletion changes and aborts on unmount', async () => {
    let finishOld!: (value: unknown) => void;
    api.request
      .mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve; }))
      .mockResolvedValue(result('ready', {
        sessionId: 'b8b228c8-b9d5-43d4-ac5a-8c0c85607d0e',
        locator: null,
        owner: false,
      }));
    const wrapper = await render();
    await flushPromises();
    const firstSignal = api.request.mock.calls[0][3] as AbortSignal;

    await wrapper.setProps({ sessionId: 'b8b228c8-b9d5-43d4-ac5a-8c0c85607d0e' });
    await flushPromises();
    finishOld(result('ready'));
    await flushPromises();
    expect(firstSignal.aborted).toBe(true);
    expect(wrapper.find('a[href*="keepsake"]').exists()).toBe(false);

    api.setAccount({ ...customer, id: 'customer-2' });
    await flushPromises();
    expect(wrapper.text()).toContain('Památka je připravená.');

    api.setDeleting(true);
    await flushPromises();
    expect(wrapper.find('.customer-session-result').exists()).toBe(false);
    const requestCountAtDeletion = api.request.mock.calls.length;

    let finishUnmounted!: (value: unknown) => void;
    api.request.mockImplementationOnce(() => new Promise(resolve => { finishUnmounted = resolve; }));
    api.setDeleting(false);
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(requestCountAtDeletion + 1);
    const latestSignal = api.request.mock.calls.at(-1)?.[3] as AbortSignal;
    wrapper.unmount();
    expect(latestSignal.aborted).toBe(true);
    finishUnmounted(result('ready'));
  });

  it('does not request for logged-out, staff, or deleting contexts', async () => {
    api.setAccount(null);
    const wrapper = await render();
    await flushPromises();
    expect(api.request).not.toHaveBeenCalled();

    api.setAccount({ id: 'operator', displayName: 'Operator', roles: ['Customer', 'Operator'] });
    await flushPromises();
    expect(api.request).not.toHaveBeenCalled();

    api.setAccount(customer);
    await flushPromises();
    const requestCountBeforeDeletion = api.request.mock.calls.length;
    expect(requestCountBeforeDeletion).toBe(1);
    api.setDeleting(true);
    await flushPromises();
    expect(api.request).toHaveBeenCalledTimes(requestCountBeforeDeletion);
    wrapper.unmount();
  });
});

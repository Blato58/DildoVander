import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick, ref } from 'vue';
import { createMemoryHistory, createRouter } from 'vue-router';
import type { Account } from '../features/identity/session';

const api = vi.hoisted(() => ({
  request: vi.fn(),
  setAccount: (_value: unknown) => {},
  setDeleting: (_value: boolean) => {},
}));

vi.mock('../features/identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<Account | null>(null);
  const accountDeletionRequested = ref(false);
  api.setAccount = value => { account.value = value as Account | null; };
  api.setDeleting = value => { accountDeletionRequested.value = value; };
  return { account, accountDeletionRequested, accountRequest: api.request };
});

vi.mock('../features/suitcase/deviceSession', async () => {
  const { ref } = await import('vue');
  return { deviceSession: { target: ref('unavailable'), message: ref('') } };
});
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => false } }));

import HomeView from './HomeView.vue';
import { account } from '../features/identity/session';
import { interfaceLanguagePreference } from './interfaceLanguage';

interface Deferred<T> { promise: Promise<T>; resolve(value: T): void }
function deferred<T>(): Deferred<T> {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>(complete => { resolve = complete; });
  return { promise, resolve };
}

async function mountHome() {
  const router = createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/', component: HomeView },
      { path: '/signin', component: { template: '<div />' } },
      { path: '/settings', component: { template: '<div />' } },
      { path: '/connections', component: { template: '<div />' } },
      { path: '/suitcase', component: { template: '<div />' } },
      { path: '/workshop', component: { template: '<div />' } },
      { path: '/archive', component: { template: '<div />' } },
      { path: '/me/:pane', component: { template: '<div />' } },
      { path: '/keepsake/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  });
  await router.push('/');
  await router.isReady();
  const wrapper = mount(HomeView, { global: { plugins: [router] } });
  await nextTick();
  return { router, wrapper };
}

const customer = { id: 'customer-1', displayName: 'Zákazník', roles: ['Customer'] };
const operator = { id: 'staff-1', displayName: 'Obsluha', roles: ['Operator'] };

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
  api.setAccount(null);
  api.setDeleting(false);
  api.request.mockReset();
  api.request.mockImplementation(async (path: string) => {
    if (path.endsWith('/character')) return { profile: null };
    if (path.endsWith('/sessions')) return { items: [] };
    throw new Error('Unexpected account read');
  });
  vi.stubGlobal('fetch', vi.fn().mockResolvedValue({
    ok: true,
    json: async () => ({ schemaVersion: 1, evening: null }),
  }));
});

afterEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe('Home customer journey', () => {
  it('preserves guest and staff home actions without customer endpoint reads', async () => {
    const guest = await mountHome();
    expect(guest.wrapper.text()).toContain('Přihlásit se');
    expect(guest.wrapper.findComponent({ name: 'CustomerJourneyCard' }).exists()).toBe(false);
    guest.wrapper.unmount();

    api.setAccount(operator);
    const staff = await mountHome();
    expect(staff.wrapper.text()).toContain('Připojit kufr');
    expect(staff.wrapper.text()).not.toContain('Váš další krok');
    expect(api.request).not.toHaveBeenCalled();
    staff.wrapper.unmount();
  });

  it('switches guest home copy without repeating network reads', async () => {
    const { wrapper } = await mountHome();
    await flushPromises();
    expect(wrapper.get('h1').text()).toBe('Kufr plný příběhů.');
    const requests = vi.mocked(fetch).mock.calls.length;

    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.get('h1').text()).toBe('A suitcase full of stories.');
    expect(wrapper.text()).toContain('Explore the suitcase model and wand stories.');
    expect(vi.mocked(fetch).mock.calls).toHaveLength(requests);
    expect(api.request).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('keeps mixed-role identities in the staff view', async () => {
    api.setAccount({ ...customer, roles: ['Customer', 'Operator'] });
    const { wrapper } = await mountHome();

    expect(wrapper.text()).toContain('Připojit kufr');
    expect(wrapper.text()).not.toContain('Váš další krok');
    expect(api.request).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it('loads an empty customer account and offers character creation', async () => {
    api.setAccount(customer);
    const { wrapper } = await mountHome();
    await flushPromises();

    expect(wrapper.text()).toContain('Váš další krok');
    expect(wrapper.get('a[href="/me/character"]').text()).toBe('Vytvořit postavu');
    expect(wrapper.get('a[href="/me/keepsakes"]').text()).toBe('Moje památky');
    expect(api.request.mock.calls.map(([path, method]) => [path, method])).toEqual([
      ['/app/v1/customer/character', 'GET'],
      ['/app/v1/customer/sessions', 'GET'],
    ]);
    expect(api.request.mock.calls.every(([, method]) => method === 'GET')).toBe(true);
    wrapper.unmount();
  });

  it('shows only status refresh guidance while customer reads are unresolved', async () => {
    api.setAccount(customer);
    const characterRead = deferred<{ profile: { status: string } | null }>();
    const sessionsRead = deferred<{ items: unknown[] }>();
    api.request.mockImplementation((path: string) => path.endsWith('/character')
      ? characterRead.promise
      : sessionsRead.promise);
    const { wrapper } = await mountHome();

    expect(wrapper.get('a[href="/me/conversation"]').text()).toBe('Obnovit stav rozhovoru');
    expect(wrapper.text()).not.toContain('Pokračovat k rozhovoru');
    characterRead.resolve({ profile: { status: 'saved' } });
    sessionsRead.resolve({ items: [] });
    await flushPromises();
    expect(wrapper.get('a[href="/me/conversation"]').text()).toBe('Pokračovat k rozhovoru');
    wrapper.unmount();
  });

  it('uses saved profiles without requiring AI preparation and prioritizes an unknown active session', async () => {
    api.setAccount(customer);
    api.request.mockImplementation(async (path: string) => path.endsWith('/character')
      ? { profile: { status: 'saved' } }
      : { items: [] });
    const saved = await mountHome();
    await flushPromises();
    expect(saved.wrapper.get('a[href="/me/conversation"]').text()).toBe('Pokračovat k rozhovoru');
    expect(saved.wrapper.text()).toContain('zvolíte souhlasy pro tento rozhovor');
    saved.wrapper.unmount();

    api.request.mockImplementation(async (path: string) => path.endsWith('/character')
      ? { profile: { status: 'ready' } }
      : { items: [{ state: 'future_runtime_state' }] });
    const active = await mountHome();
    await flushPromises();
    expect(active.wrapper.get('a[href="/me/conversation"]').text()).toBe('Pokračovat v rozhovoru');
    expect(active.wrapper.text()).toContain('sám nepotvrzuje aktuální spojení s kufrem');
    active.wrapper.unmount();
  });

  it('does not show readiness while loading and recovers a failed read automatically', async () => {
    vi.useFakeTimers();
    api.setAccount(customer);
    const characterRetry = deferred<{ profile: { status: string } }>();
    let characterReads = 0;
    api.request.mockImplementation((path: string) => {
      if (path.endsWith('/character')) {
        characterReads += 1;
        return characterReads === 1
          ? Promise.reject(new Error('private transport detail'))
          : characterRetry.promise;
      }
      return Promise.resolve({ items: [] });
    });
    const { wrapper } = await mountHome();
    await flushPromises();
    expect(wrapper.get('a[href="/me/conversation"]').text()).toBe('Obnovit stav rozhovoru');
    expect(wrapper.text()).not.toContain('private transport detail');

    await vi.advanceTimersByTimeAsync(1000);
    characterRetry.resolve({ profile: { status: 'saved' } });
    await flushPromises();
    expect(wrapper.get('a[href="/me/conversation"]').text()).toBe('Pokračovat k rozhovoru');
    wrapper.unmount();
  });

  it('ignores old-account completions, aborts the old read, and clears private data on logout', async () => {
    api.setAccount(customer);
    const accountReads = new Map<string, { profile: Deferred<{ profile: { status: string } }>; sessions: Deferred<{ items: unknown[] }> }>();
    const signals: AbortSignal[] = [];
    api.request.mockImplementation((path: string, _method: string, _body: unknown, signal: AbortSignal) => {
      const id = account.value?.id ?? '';
      signals.push(signal);
      if (!accountReads.has(id)) {
        accountReads.set(id, {
          profile: deferred(),
          sessions: deferred(),
        });
      }
      const reads = accountReads.get(id)!;
      return path.endsWith('/character') ? reads.profile.promise : reads.sessions.promise;
    });
    const { wrapper } = await mountHome();
    expect(signals).toHaveLength(2);

    api.setAccount({ id: 'customer-2', displayName: 'Nový účet', roles: ['Customer'] });
    await nextTick();
    expect(signals.slice(0, 2).every(signal => signal.aborted)).toBe(true);
    accountReads.get('customer-2')!.profile.resolve({ profile: { status: 'needs_clarification' } });
    accountReads.get('customer-2')!.sessions.resolve({ items: [] });
    await flushPromises();
    expect(wrapper.get('a[href="/me/character"]').text()).toBe('Upřesnit postavu');

    accountReads.get('customer-1')!.profile.resolve({ profile: { status: 'ready' } });
    accountReads.get('customer-1')!.sessions.resolve({ items: [] });
    await flushPromises();
    expect(wrapper.text()).not.toContain('Pokračovat k rozhovoru');
    api.setAccount(null);
    await nextTick();
    expect(wrapper.text()).not.toContain('Upřesnit postavu');
    expect(wrapper.text()).toContain('Přihlásit se');
    wrapper.unmount();
  });

  it('cancels private reads offline and refreshes them when the app is online again', async () => {
    api.setAccount(customer);
    const batches = Array.from({ length: 2 }, () => ({
      profile: deferred<{ profile: { status: string } | null }>(),
      sessions: deferred<{ items: unknown[] }>(),
    }));
    const signals: AbortSignal[] = [];
    let requests = 0;
    api.request.mockImplementation((path: string, _method: string, _body: unknown, signal: AbortSignal) => {
      const batch = batches[Math.floor(requests / 2)];
      requests += 1;
      signals.push(signal);
      return path.endsWith('/character') ? batch.profile.promise : batch.sessions.promise;
    });
    const { wrapper } = await mountHome();

    window.dispatchEvent(new Event('offline'));
    expect(signals.slice(0, 2).every(signal => signal.aborted)).toBe(true);
    window.dispatchEvent(new Event('online'));
    expect(signals).toHaveLength(4);
    batches[1].profile.resolve({ profile: { status: 'saved' } });
    batches[1].sessions.resolve({ items: [] });
    await flushPromises();
    expect(wrapper.get('a[href="/me/conversation"]').text()).toBe('Pokračovat k rozhovoru');
    wrapper.unmount();
  });

  it('opens only the newest completed session’s server-bound result and performs no writes', async () => {
    api.setAccount(customer);
    const locator = '8391bc53-f77b-46ee-a415-a8f82a3d2348';
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile: { status: 'saved' } };
      if (path.endsWith('/sessions')) return { items: [{ id: 'newest', state: 'completed' }, { id: 'older', state: 'completed' }] };
      if (path === '/app/v1/customer/sessions/newest/result') return {
        schemaVersion: 1, sessionId: 'newest', resultLocator: locator,
        readiness: { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true },
        accessKind: 'originalOwner', canReadPrivate: true, canManage: true, canUnlink: false,
      };
      throw new Error('Unexpected read');
    });
    const { wrapper } = await mountHome();
    await flushPromises();
    expect(wrapper.get('.customer-journey-card .aw-button--primary').attributes('href')).toBe(`/keepsake/?account=1&k=${locator}`);
    expect(api.request.mock.calls.every(call => call[1] === 'GET')).toBe(true);
    expect(api.request.mock.calls.some(call => call[0].includes('older/result'))).toBe(false);
    wrapper.unmount();
  });

  it('checks an unbound completed result instead of presenting it as a ready keepsake', async () => {
    vi.useFakeTimers();
    api.setAccount(customer);
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile: { status: 'ready' } };
      if (path.endsWith('/sessions')) return { items: [{ id: 'pending-result', state: 'completed' }] };
      return {
        schemaVersion: 1, sessionId: 'pending-result', resultLocator: null,
        readiness: { state: 'pending', reason: 'awaiting_result', canRetry: false, shouldPoll: true, hasStory: false, hasArtwork: false },
        accessKind: null, canReadPrivate: false, canManage: false, canUnlink: false,
      };
    });
    const { wrapper } = await mountHome();
    await flushPromises();
    expect(wrapper.get('.customer-journey-card .aw-button--primary').text()).toBe('Zkontrolovat památku');
    expect(wrapper.find('a[href^="/keepsake/"]').exists()).toBe(false);
    await vi.advanceTimersByTimeAsync(2500);
    expect(api.request.mock.calls.filter(call => call[0].endsWith('/result'))).toHaveLength(2);
    expect(api.request.mock.calls.every(call => call[1] === 'GET')).toBe(true);
    wrapper.unmount();
  });

  it('rejects a result for a different session without retaining a keepsake link', async () => {
    api.setAccount(customer);
    api.request.mockImplementation(async (path: string) => {
      if (path.endsWith('/character')) return { profile: { status: 'ready' } };
      if (path.endsWith('/sessions')) return { items: [{ id: 'expected-session', state: 'completed' }] };
      return {
        schemaVersion: 1, sessionId: 'different-session', resultLocator: '8391bc53-f77b-46ee-a415-a8f82a3d2348',
        readiness: { state: 'ready', reason: null, canRetry: false, shouldPoll: false, hasStory: true, hasArtwork: true },
        accessKind: 'originalOwner', canReadPrivate: true, canManage: true, canUnlink: false,
      };
    });
    const { wrapper } = await mountHome();
    await flushPromises();
    expect(wrapper.get('.customer-journey-card .aw-button--primary').text()).toBe('Obnovit stav rozhovoru');
    expect(wrapper.find('a[href^="/keepsake/"]').exists()).toBe(false);
    wrapper.unmount();
  });
});

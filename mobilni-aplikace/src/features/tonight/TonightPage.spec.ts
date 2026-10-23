import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { AccountDetails } from '../identity/session';
import { account, accountRequest, accountTransport } from '../identity/session';
import { deviceSession } from '../suitcase/deviceSession';
import { directorStop, clearDirectorStop } from '../director/directorStop';
import TonightPage from './TonightPage.vue';
vi.mock('./TonightWaiting.vue', () => ({ default: { name: 'TonightWaiting', props: ['deviceId', 'authorized'], template: '<div />' } }));

vi.mock('../identity/session', async () => {
  const vue = await import('vue');
  return {
    account: vue.ref(null),
    accountRequest: vi.fn(),
    accountTransport: vi.fn(),
  };
});

vi.mock('../suitcase/deviceSession', async () => {
  const vue = await import('vue');
  const snapshot = vue.shallowRef(null);
  return {
    deviceSession: {
      snapshot,
      savedDevice: vue.shallowRef({ paired: true, deviceId: 'case-local' }),
      lastObservedAt: vue.ref(1_800_000_000_000),
      native: false,
      requestOffline: vi.fn(async () => ({})),
      startVoice: vi.fn(() => true),
      stop: vi.fn(),
      stopVoice: vi.fn(),
      refreshSaved: vi.fn(async () => {}),
    },
  };
});

const nowMs = 1_800_000_000_000;
const localSnapshot = {
  source: 'native-device', controlMode: 'observer', phase: 'ready', readyMask: 63,
  activeCompartment: null, busy: false, status: 'ready', voice: { canStart: true },
  diagnostics: { deviceId: 'case-local', receivedAtMs: 12, lastObservedAtMs: nowMs, bootId: 'boot', devicePhase: 'ready', eventSequence: 2, deviceUptimeMs: 100, roundTripMs: 1, faultCount: 0 },
};

function accountDetails(deviceId: string, canOperate = true): AccountDetails {
  return {
    devices: [{ deviceId, canOperate }], sessions: [], grants: [], associations: [],
    passkeyCount: 0, passkeys: [],
  };
}

function showState(deviceId = 'case-local') {
  return {
    deviceId, eveningId: 'evening-1', revision: 1, phase: 'idle', sessionId: 'show-session-1',
    sessionGeneration: 'show-generation-1', mood: { dominant: 'amused', locked: false },
    tone: 'dry', tempo: 'standard', quiet: false, ambientEnabled: true, readyMask: 63,
    selectionLocked: false, updatedAt: new Date(nowMs).toISOString(),
  };
}

function memoryStatus() {
  return { storedBytes: 10, capacityBytes: 100, storageStatus: 'available', rejectedWrites: 0, storageIncomplete: false };
}

let wrappers: VueWrapper[] = [];
function mountPage() {
  return mount(TonightPage, {
    global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(nowMs);
  Object.defineProperty(document, 'hidden', { configurable: true, value: false });
  clearDirectorStop();
  account.value = { id: 'operator-a', displayName: 'Operator A', roles: ['Operator'] };
  (deviceSession.savedDevice as unknown as { value: { paired: boolean; deviceId: string } }).value = { paired: true, deviceId: 'case-local' };
  (deviceSession.snapshot as unknown as { value: typeof localSnapshot | null }).value = localSnapshot;
  (deviceSession.lastObservedAt as unknown as { value: number | null }).value = nowMs;
  vi.mocked(accountRequest).mockImplementation(async <T>(path: string) => {
    if (path === '/app/v1/account') return accountDetails('case-local') as T;
    if (path === '/app/v1/customer/admin/sessions') return { items: [] } as T;
    if (path.endsWith('/memory-status')) return memoryStatus() as T;
    throw new Error('unexpected account path');
  });
  vi.mocked(accountTransport).mockImplementation(async (path: string, init?: RequestInit) => {
    const target = path.match(/\/v1\/show\/devices\/([^/]+)/)?.[1] ?? 'case-local';
    return Response.json(init?.method === 'POST'
      ? { status: 'accepted', state: showState(decodeURIComponent(target)) }
      : showState(decodeURIComponent(target)));
  });
});

afterEach(() => {
  wrappers.forEach(wrapper => wrapper.unmount());
  wrappers = [];
  clearDirectorStop();
  vi.useRealTimers();
});

describe('TonightPage production composition', () => {
  it('uses account device grants, the real recovery polling cycle, and issues no command on open', async () => {
    const wrapper = mountPage();
    wrappers.push(wrapper);
    await flushPromises();

    expect(wrapper.text()).toContain('case-local');
    expect(wrapper.text()).toContain('Připraveno');
    expect(accountRequest).toHaveBeenCalledWith('/app/v1/account', 'GET', undefined, expect.any(AbortSignal));
    expect(accountTransport).toHaveBeenCalledWith('/v1/show/devices/case-local', expect.objectContaining({ method: 'GET' }));
    expect(vi.mocked(accountTransport).mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false);

    const readsBeforePoll = vi.mocked(accountRequest).mock.calls.filter(([path]) => path !== '/app/v1/account').length;
    await vi.advanceTimersByTimeAsync(5_000);
    await flushPromises();
    const readsAfterPoll = vi.mocked(accountRequest).mock.calls.filter(([path]) => path !== '/app/v1/account').length;
    expect(readsAfterPoll).toBe(readsBeforePoll + 2);
    expect(vi.mocked(accountTransport).mock.calls.some(([, init]) => init?.method === 'POST')).toBe(false);
  });

  it('keeps the authorized view unknown when the selected suitcase differs from native evidence', async () => {
    vi.mocked(accountRequest).mockImplementation(async <T>(path: string) => {
      if (path === '/app/v1/account') return accountDetails('case-remote') as T;
      if (path === '/app/v1/customer/admin/sessions') return { items: [] } as T;
      if (path.endsWith('/memory-status')) return memoryStatus() as T;
      throw new Error('unexpected account path');
    });
    const wrapper = mountPage();
    wrappers.push(wrapper);
    await flushPromises();

    expect(wrapper.text()).toContain('case-remote');
    expect(wrapper.text()).toContain('Stav zařízení neznámý');
    expect(wrapper.get('[data-action="next-visit"]').attributes('disabled')).toBeDefined();
    expect(wrapper.text()).not.toContain('Připraveno · nezaneprázdněný');
    expect(wrapper.text()).toContain('Stav sítě neznámý');
    expect(wrapper.text()).toContain('Fáze hlášeného zvuku neznámá');
  });

  it('does not read private status for a selected device without the exact operate grant', async () => {
    vi.mocked(accountRequest).mockImplementation(async <T>(path: string) => {
      if (path === '/app/v1/account') return accountDetails('case-local', false) as T;
      throw new Error('private endpoint must not be read');
    });
    const wrapper = mountPage();
    wrappers.push(wrapper);
    await flushPromises();

    expect(wrapper.text()).toContain('Oprávnění k obsluze není potvrzené');
    expect(vi.mocked(accountRequest).mock.calls.some(([path]) => path === '/app/v1/customer/admin/sessions' || path.endsWith('/memory-status'))).toBe(false);
    expect(directorStop.value).toBeNull();
  });

  it('keeps private reads hidden while account grants are still loading', async () => {
    let finishDetails!: (value: AccountDetails) => void;
    vi.mocked(accountRequest).mockImplementation(async <T>(path: string) => {
      if (path === '/app/v1/account') return await new Promise<T>(resolve => { finishDetails = value => resolve(value as T); });
      throw new Error('private endpoint must not be read before grants arrive');
    });
    const wrapper = mountPage();
    wrappers.push(wrapper);
    await flushPromises();

    expect(wrapper.text()).toContain('Oprávnění k obsluze se ověřuje');
    expect(vi.mocked(accountRequest).mock.calls.some(([path]) => path === '/app/v1/customer/admin/sessions' || path.endsWith('/memory-status'))).toBe(false);
    finishDetails(accountDetails('case-local'));
    await flushPromises();
    expect(vi.mocked(accountRequest).mock.calls.some(([path]) => path === '/app/v1/customer/admin/sessions')).toBe(true);
  });

  it('fences a late account-grant response after an account handoff', async () => {
    let finishOld!: (value: AccountDetails) => void;
    vi.mocked(accountRequest).mockImplementation(async <T>(path: string, _method?: string, _body?: unknown, signal?: AbortSignal) => {
      if (path === '/app/v1/account' && account.value?.id === 'operator-a') {
        return await new Promise<T>(resolve => { finishOld = value => resolve(value as T); });
      }
      if (path === '/app/v1/account') return accountDetails('case-new') as T;
      if (path === '/app/v1/customer/admin/sessions') return { items: [] } as T;
      if (path.endsWith('/memory-status')) return memoryStatus() as T;
      throw new Error('unexpected account path');
    });
    const wrapper = mountPage();
    wrappers.push(wrapper);
    await flushPromises();

    account.value = { id: 'operator-b', displayName: 'Operator B', roles: ['Operator'] };
    await flushPromises();
    finishOld(accountDetails('case-stale'));
    await flushPromises();

    expect(wrapper.text()).toContain('case-new');
    expect(wrapper.text()).not.toContain('case-stale');
    expect(wrapper.text()).toContain('Operator B');
    expect(wrapper.text()).not.toContain('operator-b');
  });

  it('retains the real registered backend Stop participant after route unmount', async () => {
    const wrapper = mountPage();
    wrappers.push(wrapper);
    await flushPromises();
    expect(directorStop.value?.canStop()).toBe(true);
    wrapper.unmount();
    wrappers = [];

    expect(directorStop.value?.canStop()).toBe(true);
    await directorStop.value!.stop();
    expect(vi.mocked(accountTransport).mock.calls.some(([path, init]) => path.endsWith('/commands') && init?.method === 'POST')).toBe(true);
  });
});

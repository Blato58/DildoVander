import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';

const api = vi.hoisted(() => ({ request: vi.fn(), setAccount: vi.fn() }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref({ id: 'operator-1', displayName: 'Operator', roles: ['Administrator'] });
  api.setAccount.mockImplementation((value: typeof account.value) => { account.value = value; });
  return { account, accountRequest: api.request };
});
import CustomerAdminView from './CustomerAdminView.vue';

const session = (state: string, endRequested = false) => ({
  id: 'session-1', operationId: 'operation-1', deviceId: 'device-1', state,
  endRequested, createdAt: 1792540800, characterName: 'Luna',
});
const originalConfirm = Object.getOwnPropertyDescriptor(window, 'confirm');

afterEach(() => {
  vi.clearAllMocks();
  vi.useRealTimers();
  api.setAccount({ id: 'operator-1', displayName: 'Operator', roles: ['Administrator'] });
  vi.restoreAllMocks();
  if (originalConfirm) Object.defineProperty(window, 'confirm', originalConfirm);
  else Reflect.deleteProperty(window, 'confirm');
});

describe('customer admin session recovery', () => {
  it('retries a failed initial poll and keeps recovering Stop pending until runtime acknowledgement', async () => {
    vi.useFakeTimers();
    Object.defineProperty(window, 'confirm', { configurable: true, value: () => true });
    let sessionReads = 0;
    let endRequested = false;
    let runtimeEnded = false;
    api.request.mockImplementation(async (path: string, method?: string) => {
      if (path.endsWith('/admin/sessions')) {
        sessionReads += 1;
        if (sessionReads === 1) throw new TypeError('Failed to fetch');
        return { items: runtimeEnded ? [] : [session('recovering', endRequested)] };
      }
      if (path === '/app/v1/account') return { devices: [], grants: [] };
      if (path.endsWith('/admin/characters')) return { items: [] };
      if (path.endsWith('/admin/devices/device-1')) return { deviceId: 'device-1', enabled: true };
      if (path.endsWith('/sessions/session-1/end') && method === 'POST') {
        endRequested = true;
        return { state: 'ending' };
      }
      throw new Error(`Unexpected request ${path}`);
    });

    const wrapper = mount(CustomerAdminView);
    await flushPromises();
    expect(wrapper.text()).toContain('Server není dostupný');
    expect(sessionReads).toBe(1);

    await vi.advanceTimersByTimeAsync(1000);
    await flushPromises();
    expect(sessionReads).toBe(2);
    expect(wrapper.text()).toContain('Obnovuje se');

    const stop = wrapper.findAll('button').find(button => button.text() === 'Ukončit rozhovor');
    expect(stop).toBeDefined();
    await stop!.trigger('click');
    await flushPromises();
    const pendingStop = wrapper.findAll('button').find(button => button.text() === 'Konec požádán');
    expect(pendingStop?.attributes('disabled')).toBeDefined();
    expect(api.request.mock.calls.filter(call => call[0].endsWith('/sessions/session-1/end'))).toHaveLength(1);

    runtimeEnded = true;
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit')!.trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('Není vidět žádná zákaznická relace.');
    expect(wrapper.findAll('button').some(button => button.text() === 'Konec požádán')).toBe(false);
    expect(api.request.mock.calls.filter(call => call[0].endsWith('/sessions/session-1/end'))).toHaveLength(1);
    wrapper.unmount();
  });

  it('aborts account-scoped polling and ignores a late result after an operator switch', async () => {
    let completeOldRequest!: (value: { items: ReturnType<typeof session>[] }) => void;
    let sessionReads = 0;
    api.request.mockImplementation((path: string) => {
      if (path.endsWith('/admin/sessions')) {
        sessionReads += 1;
        if (sessionReads === 1) return new Promise(resolve => { completeOldRequest = resolve; });
        return Promise.resolve({ items: [] });
      }
      if (path === '/app/v1/account') return Promise.resolve({ devices: [], grants: [] });
      if (path.endsWith('/admin/characters')) return Promise.resolve({ items: [] });
      return Promise.resolve({ deviceId: 'device-1', enabled: true });
    });

    const wrapper = mount(CustomerAdminView);
    await flushPromises();
    const oldRequest = api.request.mock.calls.find(call => call[0].endsWith('/admin/sessions'))!;
    api.setAccount({ id: 'operator-2', displayName: 'Second operator', roles: ['Administrator'] });
    await flushPromises();
    completeOldRequest({ items: [session('recovering')] });
    await flushPromises();

    expect(oldRequest[3].aborted).toBe(true);
    expect(sessionReads).toBe(2);
    expect(wrapper.text()).not.toContain('Luna');
    wrapper.unmount();
  });
});

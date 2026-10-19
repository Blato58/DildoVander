import { mount } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('vue-router', async () => {
  const { reactive } = await import('vue');
  return {
    useRoute: () => reactive({ path: '/' }),
    useRouter: () => ({ onError: () => () => {}, go: vi.fn() }),
  };
});
vi.mock('../src/features/identity/session', async () => {
  const { ref } = await import('vue');
  return {
    account: ref<{ id: string; displayName: string; roles: string[] } | null>(null),
    refreshIdentity: vi.fn().mockResolvedValue(undefined),
    hasRole: () => false,
  };
});
vi.mock('../src/features/suitcase/nativeDebug', async () => {
  const { ref } = await import('vue');
  const status = ref({ running: false, session: false, transcript: 'Previous visitor' });
  return { nativeDebug: {
    status,
    pending: ref(false),
    cancellationPending: ref(false),
    invalidatePrivateState: vi.fn(() => { status.value.transcript = ''; }),
    cancel: vi.fn(),
  } };
});
vi.mock('../src/features/suitcase/deviceSession', async () => {
  const { ref } = await import('vue');
  return { deviceSession: {
    snapshot: ref(null), target: ref('unavailable'), stopPending: ref(false), message: ref(''),
    clearUserGrants: vi.fn(), stop: vi.fn(),
  } };
});
vi.mock('../src/features/suitcase/servoBench', async () => {
  const { ref } = await import('vue');
  return { servoBench: { connected: ref(false), stop: vi.fn() } };
});

import App from '../src/application/App.vue';
import { account } from '../src/features/identity/session';
import { nativeDebug } from '../src/features/suitcase/nativeDebug';
import { deviceSession } from '../src/features/suitcase/deviceSession';

describe('application account boundary', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    account.value = { id: 'first', displayName: 'First', roles: [] };
  });

  it.each(['second', null])('redacts immediately on account change to %s while device cleanup is pending', async nextId => {
    let finishCleanup!: () => void;
    const cleanup = new Promise<void>(resolve => { finishCleanup = resolve; });
    vi.mocked(deviceSession.clearUserGrants).mockImplementation(() => cleanup);
    const wrapper = mount(App, {
      global: { plugins: [createPinia()], stubs: { RouterLink: true, RouterView: true } },
    });
    try {
      account.value = nextId ? { id: nextId, displayName: 'Second', roles: [] } : null;
      // No tick or settled cleanup is allowed before the privacy boundary.
      expect(nativeDebug.invalidatePrivateState).toHaveBeenCalledOnce();
      expect(deviceSession.clearUserGrants).toHaveBeenCalledOnce();
      expect(vi.mocked(nativeDebug.invalidatePrivateState).mock.invocationCallOrder[0])
        .toBeLessThan(vi.mocked(deviceSession.clearUserGrants).mock.invocationCallOrder[0]!);
      if (account.value) account.value = { ...account.value, displayName: 'Refreshed' };
      expect(nativeDebug.invalidatePrivateState).toHaveBeenCalledOnce();
    } finally {
      wrapper.unmount();
      finishCleanup();
      await cleanup;
    }
  });
});

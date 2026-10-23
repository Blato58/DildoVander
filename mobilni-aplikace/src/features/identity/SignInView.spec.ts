import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import SignInView from './SignInView.vue';
import { account } from './session';

const api = vi.hoisted(() => ({
  authenticate: vi.fn(),
  enrollment: null as { kind: 'invite' | 'recovery'; token: string } | null,
}));

vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return {
    account: ref(null),
    accountsEnabled: ref(true),
    initialized: ref(true),
    loading: ref(false),
    signingOut: ref(false),
    refreshIdentity: vi.fn(),
    authenticatePasskey: api.authenticate,
  };
});
vi.mock('../../application/guestClaim', () => ({
  consumeEnrollment: () => api.enrollment,
}));

function createSignInRouter() {
  return createRouter({
    history: createMemoryHistory(),
    routes: [
      { path: '/signin', component: { template: '<div />' } },
      { path: '/settings', component: { template: '<div />' } },
      { path: '/support', component: { template: '<div />' } },
      { path: '/suitcase', component: { template: '<div />' } },
      { path: '/me/character', component: { template: '<div />' } },
      { path: '/keepsake/:pathMatch(.*)*', component: { template: '<div />' } },
    ],
  });
}

async function mountSignIn(returnPath?: string) {
  const router = createSignInRouter();
  await router.push({ path: '/signin', query: returnPath ? { return: returnPath } : {} });
  const wrapper = mount(SignInView, { global: { plugins: [router] } });
  await flushPromises();
  return { router, wrapper };
}

beforeEach(() => {
  account.value = null;
  api.authenticate.mockReset();
  api.enrollment = null;
});

describe('sign-in destinations', () => {
  it('shows expandable lost-access guidance only on unauthenticated sign-in without making requests', async () => {
    const { router, wrapper } = await mountSignIn();
    const disclosure = wrapper.get('details');
    expect(disclosure.get('summary').text()).toBe('Ztratili jste přístup?');
    expect(disclosure.attributes('open')).toBeUndefined();
    await disclosure.get('summary').trigger('click');
    expect(disclosure.attributes('open')).toBeDefined();
    expect(wrapper.get('a[href="/support"]').text()).toBe('kontaktujte podporu');
    expect(api.authenticate).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Zobrazované jméno ani odkaz na památku vlastnictví účtu neprokazují.');
    expect(router.currentRoute.value.path).toBe('/signin');
    wrapper.unmount();
  });

  it('keeps recovery enrollment behavior and defaults a staff account to settings', async () => {
    api.enrollment = { kind: 'recovery', token: 'private-recovery-code' };
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'staff', displayName: 'Obsluha', roles: ['Operator'] };
    });
    const { router, wrapper } = await mountSignIn();

    expect(wrapper.get('h1').text()).toBe('Obnovit přístup');
    expect(wrapper.find('#account-name').exists()).toBe(false);
    expect(wrapper.text()).toContain('nahradí všechny původní klíče');
    expect(wrapper.text()).toContain('oprávnění zůstanou zachovány');
    expect(wrapper.find('details').exists()).toBe(false);
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('enroll', 'private-recovery-code', '');
    expect(router.currentRoute.value.path).toBe('/settings');
    wrapper.unmount();
  });

  it('keeps invitation enrollment separate from lost-access help', async () => {
    api.enrollment = { kind: 'invite', token: 'private-invitation-code' };
    const { wrapper } = await mountSignIn();

    expect(wrapper.get('h1').text()).toBe('Přijmout pozvánku');
    expect(wrapper.find('details').exists()).toBe(false);
    expect(wrapper.get('#account-invite').attributes('type')).toBe('password');
    expect(wrapper.text()).toContain('Kód získáte soukromě od vlastníka.');
    wrapper.unmount();
  });

  it('defaults an authenticated customer to the character page', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
    });
    const { router, wrapper } = await mountSignIn();

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('signin', '', '');
    expect(router.currentRoute.value.path).toBe('/me/character');
    wrapper.unmount();
  });

  it('preserves the authorized account keepsake return after customer sign-in', async () => {
    const keepsake = '/keepsake/?account=1&k=e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
    });
    const { router, wrapper } = await mountSignIn(keepsake);

    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(router.currentRoute.value.fullPath).toBe(keepsake);
    wrapper.unmount();
  });

  it('falls back to the customer default for an invalid return and keeps mixed roles on staff routing', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'customer', displayName: 'Zákazník', roles: ['Customer'] };
    });
    const invalidReturn = await mountSignIn('//outside.example');
    await invalidReturn.wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(invalidReturn.router.currentRoute.value.path).toBe('/me/character');
    invalidReturn.wrapper.unmount();

    account.value = null;
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'mixed', displayName: 'Obsluha', roles: ['Customer', 'Operator'] };
    });
    const mixedRole = await mountSignIn();
    await mixedRole.wrapper.get('form').trigger('submit');
    await flushPromises();
    expect(mixedRole.router.currentRoute.value.path).toBe('/settings');
    mixedRole.wrapper.unmount();
  });

  it('keeps customer account creation on the character page', async () => {
    api.authenticate.mockImplementation(async () => {
      account.value = { id: 'new-customer', displayName: 'Nový zákazník', roles: ['Customer'] };
    });
    const keepsake = '/keepsake/?account=1&k=e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
    const { router, wrapper } = await mountSignIn(keepsake);
    expect(wrapper.find('details').exists()).toBe(true);
    await wrapper.findAll('button').find(button => button.text() === 'Vytvořit zákaznický účet')!.trigger('click');
    await flushPromises();
    expect(wrapper.find('details').exists()).toBe(false);
    await wrapper.get('#customer-account-name').setValue('Nový zákazník');
    await wrapper.get('form').trigger('submit');
    await flushPromises();

    expect(api.authenticate).toHaveBeenCalledWith('customer-enroll', '', 'Nový zákazník');
    expect(router.currentRoute.value.path).toBe('/me/character');
    wrapper.unmount();
  });
});

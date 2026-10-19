import { describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import SignInView from './SignInView.vue';
const api = vi.hoisted(() => ({authenticate: vi.fn()}));
vi.mock('./session', async () => {
  const {ref} = await import('vue');
  return {account: ref(null), accountsEnabled: ref(true), initialized: ref(true), loading: ref(false), signingOut: ref(false), refreshIdentity: vi.fn(), authenticatePasskey: api.authenticate};
});
vi.mock('../../application/guestClaim', () => ({consumeEnrollment: () => ({kind: 'recovery', token: 'private-recovery-code'})}));
describe('explicit recovery journey', () => {
  it('preserves recovery kind, explains replacement, and never requests a new identity name', async () => {
    const router = createRouter({history: createMemoryHistory(), routes: [{path: '/signin', component: {template: '<div />'}}, {path: '/settings', component: {template: '<div />'}}]});
    await router.push('/signin');
    const wrapper = mount(SignInView, {global: {plugins: [router]}}); await flushPromises();
    expect(wrapper.get('h1').text()).toBe('Obnovit přístup'); expect(wrapper.find('#account-name').exists()).toBe(false);
    expect(wrapper.text()).toContain('nahradí všechny původní klíče'); expect(wrapper.text()).toContain('oprávnění zůstanou zachovány');
    await wrapper.get('form').trigger('submit'); await flushPromises();
    expect(api.authenticate).toHaveBeenCalledWith('enroll', 'private-recovery-code', '');
    expect(router.currentRoute.value.path).toBe('/settings'); wrapper.unmount();
  });
});

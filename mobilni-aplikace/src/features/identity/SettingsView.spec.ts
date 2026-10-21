import { beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import SettingsView from './SettingsView.vue';
const api = vi.hoisted(() => ({request: vi.fn(), signOut: vi.fn()}));
vi.mock('./session', async () => {
  const {ref} = await import('vue');
  return {account: ref({id: 'owner', displayName: 'Guest', roles: ['Owner']}), accountRequest: api.request, signOut: api.signOut, refreshIdentity: vi.fn(), authenticatePasskey: vi.fn(), hasRole: () => true};
});
vi.mock('../../application/preferences', () => ({usePreferences: () => ({theme: 'lord-voldemort', themes: [{id: 'lord-voldemort', label: 'Lord Voldemort'}, {id: 'olivander', label: 'Olivander'}]})}));
beforeEach(() => {
  let name: string | null = null;
  api.signOut.mockReset(); api.request.mockReset(); api.request.mockImplementation(async (_path, method, body) => {
    if (method === 'PUT') { name = body.name; return; }
    return {sessions: [{id: 'current', current: true, installationName: 'This installation', expiresAt: 1900000000, trusted: false}], grants: [], associations: [], passkeyCount: 1, passkeys: [{id: 'owned_key', name, createdAt: '2026-10-19T10:00:00Z', isBackedUp: true}]};
  });
});
describe('credential naming settings', () => {
  it('uses the fenced logout flow for the current installation', async () => {
    const wrapper = mount(SettingsView, {global: {stubs: {AccountArchiveList: true, EveningPanel: true, RouterLink: true}}});
    await flushPromises(); await wrapper.findAll('button').find(button => button.text() === 'Odhlásit toto zařízení')!.trigger('click'); await flushPromises();
    expect(api.signOut).toHaveBeenCalledOnce();
    expect(api.request.mock.calls.some(call => call[0] === '/app/v1/sessions/current')).toBe(false);
    wrapper.unmount();
  });
  it('submits an owned credential name and displays persisted metadata', async () => {
    const wrapper = mount(SettingsView, {global: {stubs: {AccountArchiveList: true, EveningPanel: true, RouterLink: true}}});
    await flushPromises(); await wrapper.get('#passkey-name-0').setValue('Pracovní notebook');
    await wrapper.get('.key-name-form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/passkeys/owned_key', 'PUT', {name: 'Pracovní notebook'});
    expect(wrapper.text()).toContain('Pracovní notebook'); expect(wrapper.text()).toContain('Název přístupového klíče byl uložen.');
    wrapper.unmount();
  });

  it('shows only account security controls on the customer account surface', async () => {
    const wrapper = mount(SettingsView, { props: { customerSurface: true }, global: { stubs: { AccountArchiveList: true, EveningPanel: true, RouterLink: { template: '<a><slot /></a>' } } } });
    await flushPromises();
    expect(wrapper.text()).toContain('Přístupové klíče');
    expect(wrapper.text()).toContain('Uložit název klíče');
    expect(wrapper.text()).toContain('Přihlášená zařízení');
    expect(wrapper.text()).toContain('Odhlásit toto zařízení');
    expect(wrapper.text()).not.toContain('Owner');
    expect(wrapper.text()).not.toContain('Přidělené prostředky');
    expect(wrapper.text()).not.toContain('Kufry propojené s účtem');
    expect(wrapper.text()).not.toContain('HTTP API v1');
    expect(wrapper.findAll('h1')).toHaveLength(0);
    wrapper.unmount();
  });
});

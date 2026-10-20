import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import OperationsPanel from './OperationsPanel.vue';
import { account } from './session';
const request = vi.hoisted(() => vi.fn());
vi.mock('./session', async () => ({ account: (await import('vue')).ref({ id: 'owner', roles: ['Owner'] }), accountRequest: request, hasRole: () => true }));
let wrapper: VueWrapper;
const response = () => ({ readiness: { accounts: true, keepsakes: false, keepsakeGeneration: false, nativeAssociationConfigured: true }, archive: null,
  modes: { revision: 'revision-1', activeConversations: 0, personalityId: 'ollivander-trunk', personalities: [{id:'ollivander-trunk',name:'DildoVander'}],
    characterEnabled: true, characterTone: 'Adult', characterFinaleProfile: 'Blocked', characterVersion: 'dv01-v1', engines: { schemaVersion: 1, primaryEngine: 'gpt-live',
      engines: ['gpt-live', 'realtime'].map(engine => ({ engine, model: engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1', ready: true, voices: ['cedar'], speed: { supported: false, min: 1, max: 1, default: 1 }, inputModes: ['audio'], cameraSources: ['phone'] })) } } });
beforeEach(() => { request.mockReset(); request.mockResolvedValue(response()); account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] }; });
afterEach(() => wrapper?.unmount());
describe('account operations canonical engine contract', () => {
  it('renders capabilities while mutations contain only supported personality and character fields', async () => {
    wrapper = mount(OperationsPanel, {global:{stubs:{RouterLink:{template:'<a><slot /></a>'}}}}); await flushPromises();
    expect(wrapper.text()).toContain('gpt-live-1'); expect(wrapper.text()).toContain('gpt-realtime-2.1');
    expect(wrapper.find('#operations-dialog').exists()).toBe(false); expect(wrapper.find('#operations-voice').exists()).toBe(false);
    await wrapper.get('#operations-finale').setValue('Limited'); await wrapper.get('form').trigger('submit'); await flushPromises();
    expect(request).toHaveBeenCalledWith('/app/v1/admin/operations/modes','PUT',{expectedRevision:'revision-1',personalityId:'ollivander-trunk',characterEnabled:true,characterTone:'Adult',characterFinaleProfile:'Limited'});
    const body = request.mock.calls.find(item => item[1] === 'PUT')![2]; expect(body).not.toHaveProperty('dialogMode'); expect(body).not.toHaveProperty('voiceMode');
  });
  it('cannot restore old account status or errors after account changes during a mutation', async () => {
    let reject!: (reason: Error) => void;
    request.mockImplementation((path, method) => method === 'PUT' ? new Promise((_, fail) => { reject=fail; }) : Promise.resolve(response()));
    wrapper = mount(OperationsPanel, {global:{stubs:{RouterLink:true}}}); await flushPromises(); await wrapper.get('form').trigger('submit');
    account.value = { id: 'other', displayName: 'Other', roles: ['Owner'] }; await flushPromises();
    reject(new Error('old private error')); await flushPromises(); expect(wrapper.text()).not.toContain('old private error');
    expect(wrapper.find('form').exists()).toBe(false); expect(request.mock.calls.filter(item => item[1] !== 'PUT')).toHaveLength(1);
  });
});

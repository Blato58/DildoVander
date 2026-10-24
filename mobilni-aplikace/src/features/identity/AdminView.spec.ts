import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import AdminView from './AdminView.vue';
const api = vi.hoisted(() => ({ request: vi.fn(), reauthenticate: vi.fn(), setAccount: (_value: {id:string;displayName:string;roles:string[]} | null) => {} }));
vi.mock('./session', async () => {
  const {ref} = await import('vue');
  const account = ref<{id:string;displayName:string;roles:string[]} | null>({id:'owner',displayName:'Owner',roles:['Owner']});
  api.setAccount = value => {account.value=value;};
  return {account,accountOrigin:ref('https://example.test'),accountRequest:api.request,authenticatePasskey:api.reauthenticate,
    refreshIdentity:vi.fn(),hasRole:(role:string)=>account.value?.roles.some(value=>value===role||value==='Owner')};
});
const render = () => mount(AdminView,{global:{stubs:{OperationsPanel:true,ModelReleasesPanel:true,RouterLink:true}}});
const failure = (code:string,status=400) => Object.assign(new Error('Server odmítl požadavek.'),{code,status});
const invitationToken = 'private-once-token-1234567890123456';
beforeEach(()=>{
  api.setAccount({id:'owner',displayName:'Owner',roles:['Owner']});api.request.mockReset();api.reauthenticate.mockReset().mockResolvedValue(undefined);
  api.request.mockImplementation(async(path:string)=>path==='/app/v1/admin/users'?{users:[],grants:[]}:{token:invitationToken,purpose:'invite'});
});
describe('account invitations',()=>{
  it('retains a confirmed one-use invitation without making an unrelated list refresh that could misreport failure',async()=>{
    const wrapper=render();await flushPromises();
    await wrapper.get('#invite-role').setValue('Tester');
    api.request.mockImplementation(async(path:string)=>{if(path==='/app/v1/admin/users')throw failure('server_error',500);return{token:invitationToken,purpose:'invite'};});
    await wrapper.get('.identity-form').trigger('submit');await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/invitations','POST',{role:'Tester',recoveryUserId:null});
    expect(api.request.mock.calls.filter(call=>call[0]==='/app/v1/admin/users')).toHaveLength(1);
    expect(wrapper.get('#invitation-link').element).toHaveProperty('value',`https://example.test/signin#invite=${invitationToken}`);
    expect(wrapper.find('[role=alert]').exists()).toBe(false);expect(wrapper.text()).toContain('Jednorázový odkaz byl vytvořen');wrapper.unmount();
  });
  it('shows the actual safe error code and HTTP status inside the app without leaking a server response',async()=>{
    const wrapper=render();await flushPromises();api.request.mockRejectedValue(failure('invalid_role'));
    await wrapper.get('.identity-form').trigger('submit');await flushPromises();
    expect(wrapper.get('[role=alert]').text()).toContain('HTTP 400 · invalid_role');expect(wrapper.find('#invitation-token').exists()).toBe(false);wrapper.unmount();
  });
  it('offers same-account verification without silently replaying the invitation',async()=>{
    const wrapper=render();await flushPromises();await wrapper.get('#invite-role').setValue('Tester');api.request.mockRejectedValue(failure('recent_passkey_required',403));
    await wrapper.get('.identity-form').trigger('submit');await flushPromises();
    await wrapper.get('[role=alert] button').trigger('click');await flushPromises();
    expect(api.reauthenticate).toHaveBeenCalledExactlyOnceWith('reauth');expect(wrapper.get('#invite-role').element).toHaveProperty('value','Tester');
    expect(api.request.mock.calls.filter(call=>call[0]==='/app/v1/admin/invitations')).toHaveLength(1);expect(wrapper.text()).toContain('Původní akci nyní odešlete znovu');wrapper.unmount();
  });
  it('discards a late invitation result when the account changes',async()=>{
    const wrapper=render();await flushPromises();let finish!:(value:unknown)=>void;
    api.request.mockImplementation(()=>new Promise(resolve=>{finish=resolve;}));await wrapper.get('.identity-form').trigger('submit');
    api.setAccount(null);finish({token:'previous-owner-secret',purpose:'invite'});await flushPromises();
    expect(wrapper.find('#invitation-token').exists()).toBe(false);expect(wrapper.html()).not.toContain('previous-owner-secret');wrapper.unmount();
  });
  it('does not claim an invitation was created when a successful HTTP response lacks a valid token',async()=>{
    const wrapper=render();await flushPromises();api.request.mockResolvedValue({purpose:'invite'});
    await wrapper.get('.identity-form').trigger('submit');await flushPromises();
    expect(wrapper.get('[role=alert]').text()).toContain('Vytvoření nebylo potvrzeno');expect(wrapper.find('#invitation-token').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Jednorázový odkaz byl vytvořen');wrapper.unmount();
  });
});

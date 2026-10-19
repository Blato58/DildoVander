import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import SimulationControls from './SimulationControls.vue';

const identity = vi.hoisted(() => ({ accountRequest: vi.fn(), refreshIdentity: vi.fn() }));
vi.mock('../identity/session', () => identity);
const props = { instanceId: 'sim-one', profile: 'deterministic', canControl: true, online: true, mode: 'simulation', replay: false };
beforeEach(() => { vi.resetAllMocks(); identity.refreshIdentity.mockResolvedValue(undefined); identity.accountRequest.mockResolvedValue({ accepted: true }); });

describe('Workshop simulation command boundary', () => {
  it('mounting and changing a source never submits commands', async () => {
    const wrapper = mount(SimulationControls, { props });
    await wrapper.setProps({ instanceId: 'sim-two' });
    expect(identity.accountRequest).not.toHaveBeenCalled();
    expect(identity.refreshIdentity).not.toHaveBeenCalled();
    wrapper.unmount();
  });
  it.each([{ mode: 'live' }, { replay: true }, { canControl: false }])('hides controls across the %o boundary', async changed => {
    const wrapper = mount(SimulationControls, { props: { ...props, ...changed } });
    expect(wrapper.find('button').exists()).toBe(false);
    expect(identity.accountRequest).not.toHaveBeenCalled(); wrapper.unmount();
  });
  it('refreshes account/CSRF before explicit command and preserves exact target', async () => {
    const wrapper = mount(SimulationControls, { props });
    await wrapper.findAll('button').find(button => button.text() === 'Servisní inspekce simulace')!.trigger('click');
    await flushPromises();
    expect(identity.refreshIdentity).toHaveBeenCalledOnce();
    expect(identity.accountRequest).toHaveBeenCalledWith('/v1/twin/instances/sim-one/commands', 'POST', { action: 'inspect' });
    expect(wrapper.text()).toContain('Povel přijal simulátor'); wrapper.unmount();
  });
  it('discards a pending intent when the target changes during account refresh', async () => {
    let finish!: () => void;
    identity.refreshIdentity.mockImplementation(() => new Promise<void>(resolve => { finish = resolve; }));
    const wrapper = mount(SimulationControls, { props });
    await wrapper.findAll('button').find(button => button.text() === 'Servisní inspekce simulace')!.trigger('click');
    await wrapper.setProps({ instanceId: 'sim-two' }); finish(); await flushPromises();
    expect(identity.accountRequest).not.toHaveBeenCalled(); wrapper.unmount();
  });
  it('keeps urgent Stop usable while ordinary requests are pending or stale', async () => {
    let finish!: (value: unknown) => void;
    identity.accountRequest.mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const wrapper = mount(SimulationControls, { props });
    await wrapper.findAll('button').find(button => button.text() === 'Servisní inspekce simulace')!.trigger('click'); await flushPromises();
    await wrapper.setProps({ online: false });
    const stop = wrapper.findAll('button').find(button => button.text() === 'Stop simulace')!;
    expect(stop.attributes('disabled')).toBeUndefined(); await stop.trigger('click'); await flushPromises();
    expect(identity.accountRequest).toHaveBeenCalledWith('/v1/twin/instances/sim-one/commands', 'POST', { action: 'stop' });
    finish({ accepted: true }); await flushPromises(); wrapper.unmount();
  });
});

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import MediaPanel from './MediaPanel.vue';

const media = vi.hoisted(() => ({ createMediaController: vi.fn() }));
const identity = vi.hoisted(() => ({ accountTransport: vi.fn(), refreshIdentity: vi.fn() }));
vi.mock('./media.mjs', () => ({ mediaController: media }));
vi.mock('../identity/session', () => identity);
const context = { active: true, replay: false, role: 'control', instanceId: 'sim-integration', mode: 'simulation', profile: 'integration', bootId: 'boot', status: 'online' };
let controller: any, transport: any;
beforeEach(() => {
  vi.resetAllMocks(); identity.refreshIdentity.mockResolvedValue(undefined);
  controller = { sync: vi.fn(), action: vi.fn(), dispose: vi.fn(), invalidate: vi.fn(), selection: vi.fn() };
  media.createMediaController.mockImplementation(options => {
    transport = options.transport;
    options.onChange({ visible: true, settings: { engine: 'fake', voice: 'marin', speed: '1', variant: 'main', consent: false }, disabled: {}, devices: { mic: [], speaker: [], camera: [] }, dialogue: ['<script>untrusted dialogue</script>'], notice: 'Média nejsou aktivovaná.' });
    return controller;
  });
});
describe('Vue media lifecycle', () => {
  it('mounts native Vue controls and escaped dialogue without activating media', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    expect(controller.sync).toHaveBeenCalledWith(context);
    expect(controller.action).not.toHaveBeenCalled(); expect(identity.accountTransport).not.toHaveBeenCalled();
    expect(wrapper.find('script').exists()).toBe(false); expect(wrapper.text()).toContain('<script>untrusted dialogue</script>');
    wrapper.unmount(); expect(controller.dispose).toHaveBeenCalledOnce();
  });
  it('invalidates delayed explicit start when the selected source changes', async () => {
    let finish!: () => void;
    identity.refreshIdentity.mockImplementation(() => new Promise<void>(resolve => { finish = resolve; }));
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Spustit rozhovor')!.trigger('click');
    await wrapper.setProps({ context: { ...context, instanceId: 'new-source' } }); finish(); await flushPromises();
    expect(controller.sync).toHaveBeenLastCalledWith(expect.objectContaining({ instanceId: 'new-source' }));
    expect(controller.action).not.toHaveBeenCalled(); wrapper.unmount();
  });
  it('passes scoped binary transport unchanged to account/native authority', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    identity.accountTransport.mockResolvedValue(new Response(null, { status: 204 }));
    const options = { method: 'POST', headers: { 'Session-Id': 'scoped-session' }, body: new Uint8Array([0,1]) };
    expect(await transport.request('sim-integration', '/media/input', options)).toBeNull();
    expect(identity.accountTransport).toHaveBeenCalledWith('/v1/twin/instances/sim-integration/media/input', options);
    wrapper.unmount();
  });
  it('preserves typed retired-input errors and cancels all controller ownership on unmount', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    identity.accountTransport.mockResolvedValue(new Response(JSON.stringify({ code: 'media-input-retired' }), { status: 409 }));
    await expect(transport.request('sim-integration', '/media/input', { method: 'POST' })).rejects.toMatchObject({ status: 409, code: 'media-input-retired' });
    wrapper.unmount(); expect(controller.dispose).toHaveBeenCalledOnce();
  });
});

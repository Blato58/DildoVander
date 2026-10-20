import { beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import MediaPanel from './MediaPanel.vue';

const media = vi.hoisted(() => ({ createMediaController: vi.fn() }));
const identity = vi.hoisted(() => ({ accountTransport: vi.fn(), refreshIdentity: vi.fn(), account: { value: { id: 'owner' } as { id: string } | null } }));
vi.mock('./media.mjs', () => ({ mediaController: media }));
vi.mock('../identity/session', async () => { const { ref } = await import('vue'); identity.account = ref({ id: 'owner' }); return identity; });
const context = { active: true, replay: false, role: 'control', instanceId: 'sim-integration', mode: 'simulation', profile: 'integration', bootId: 'boot', status: 'online' };
let controller: any, transport: any, publish: (value: any) => void;
beforeEach(() => {
  vi.resetAllMocks(); identity.account.value = { id: 'owner' }; identity.refreshIdentity.mockResolvedValue(undefined);
  controller = { sync: vi.fn(), action: vi.fn(), dispose: vi.fn(), invalidate: vi.fn(), selection: vi.fn() };
  media.createMediaController.mockImplementation(options => {
    transport = options.transport;
    publish = options.onChange;
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
    identity.account.value = { id: 'owner' }; await flushPromises(); expect(controller.dispose).not.toHaveBeenCalled();
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
  it('synchronously disposes private ownership on account loss and rejects late poll publication and upload completion', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    const oldPublish = publish, oldTransport = transport;
    let complete!: (value: Response) => void;
    identity.accountTransport.mockImplementationOnce(() => new Promise<Response>(resolve => { complete = resolve; }));
    const upload = oldTransport.request('sim-integration', '/media/input', { method: 'POST', body: new Uint8Array([0, 1]) });
    const stale = expect(upload).rejects.toMatchObject({ name: 'AbortError' });
    identity.account.value = null;
    expect(controller.dispose).toHaveBeenCalledOnce();
    oldPublish({ visible: true, dialogue: ['private late visitor'], settings: {}, disabled: {}, devices: {} });
    complete(new Response(null, { status: 204 })); await stale; await flushPromises();
    expect(wrapper.text()).not.toContain('untrusted dialogue'); expect(wrapper.text()).not.toContain('private late visitor');
    expect(wrapper.text()).toContain('Soukromá média byla vymazána');
    await expect(oldTransport.request('sim-integration', '/media/cancel', { method: 'POST' })).rejects.toMatchObject({ name: 'AbortError' });
    expect(identity.accountTransport).toHaveBeenCalledOnce(); wrapper.unmount();
  });
  it('does not adopt old active source props on account switch and requires a fresh explicit connection', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    identity.account.value = { id: 'other-owner' }; await flushPromises();
    expect(controller.dispose).toHaveBeenCalledOnce(); expect(media.createMediaController).toHaveBeenCalledOnce();
    await wrapper.setProps({ context: { ...context, status: 'stale' } });
    expect(media.createMediaController).toHaveBeenCalledOnce();
    await wrapper.setProps({ context: { ...context, active: false, bootId: '' } });
    await wrapper.setProps({ context }); await flushPromises();
    expect(media.createMediaController).toHaveBeenCalledTimes(2);
    expect(controller.action).not.toHaveBeenCalled(); wrapper.unmount();
  });
  it('never finishes an explicit start when its account changes during refresh', async () => {
    let complete!: () => void;
    identity.refreshIdentity.mockImplementationOnce(() => new Promise<void>(resolve => { complete = resolve; }));
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Spustit rozhovor')!.trigger('click');
    identity.account.value = { id: 'other-owner' }; complete(); await flushPromises();
    expect(controller.action).not.toHaveBeenCalled(); expect(controller.dispose).toHaveBeenCalledOnce(); wrapper.unmount();
  });
  it('permits one correlated cleanup on ordinary unmount but never borrows a different account for it', async () => {
    for (const switchAccount of [false, true]) {
      identity.account.value = { id: 'owner' }; identity.accountTransport.mockClear();
      identity.accountTransport.mockResolvedValue(new Response(null, { status: 204 }));
      const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
      controller.dispose.mockImplementation(() => { void transport.request('sim-integration', '/media/cancel', { method: 'POST', body: JSON.stringify({ sessionId: 'old-session', bootId: 'old-boot', epoch: '1' }) }).catch(() => {}); });
      if (switchAccount) identity.account.value = { id: 'other-owner' };
      wrapper.unmount(); await flushPromises();
      expect(identity.accountTransport).toHaveBeenCalledTimes(switchAccount ? 0 : 1);
      if (!switchAccount) expect(identity.accountTransport).toHaveBeenCalledWith('/v1/twin/instances/sim-integration/media/cancel', expect.objectContaining({ method: 'POST', body: JSON.stringify({ sessionId: 'old-session', bootId: 'old-boot', epoch: '1' }) }));
    }
  });
  it('passes scoped binary transport unchanged to account/native authority', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    identity.accountTransport.mockResolvedValue(new Response(null, { status: 204 }));
    const options = { method: 'POST', headers: { 'Session-Id': 'scoped-session' }, body: new Uint8Array([0,1]) };
    expect(await transport.request('sim-integration', '/media/input', options)).toBeNull();
    expect(identity.accountTransport).toHaveBeenCalledWith('/v1/twin/instances/sim-integration/media/input', options);
    wrapper.unmount();
  });
  it('reads canonical capabilities only through the exact instance account endpoint with the cancellation signal', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    const signal = new AbortController().signal;
    const engines = { schemaVersion: 1, primaryEngine: 'gpt-live', engines: [] };
    identity.accountTransport.mockResolvedValueOnce(new Response(JSON.stringify({ revision: '4', engine: 'fake' }))).mockResolvedValueOnce(new Response(JSON.stringify(engines)));
    expect(await transport.request('sim-integration', '/settings', { signal })).toEqual({ revision: '4', engine: 'fake', engines });
    expect(identity.accountTransport).toHaveBeenNthCalledWith(2, '/app/v1/workshop/sim-integration/engines', { signal });
    wrapper.unmount();
  });
  it('shows exact canonical model, voice and speed without replacing an unavailable selected engine', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    const settings = { engine: 'gpt-live', voice: 'marin', speed: '1', variant: 'main', consent: false };
    const capability = { engine: 'gpt-live', model: 'gpt-live-1', ready: false, unavailableReason: 'provider_credential_missing', voices: ['marin'], speed: { supported: false, min: 1, max: 1, default: 1 }, inputModes: ['audio'], cameraSources: ['phone', 'suitcase'] };
    publish({ visible: true, settings, disabled: { speed: true }, devices: { mic: [], speaker: [], camera: [] }, dialogue: [], configuration: { revision: '4', availability: { fake: true, gptLive: true }, engines: { schemaVersion: 1, primaryEngine: 'gpt-live', engines: [capability] } } });
    await flushPromises();
    expect(wrapper.get<HTMLSelectElement>('#media-engine').element.value).toBe('gpt-live');
    expect(wrapper.get('#media-engine option[value="gpt-live"]').attributes('disabled')).toBeDefined();
    expect(wrapper.findAll('#media-voice option').map(option => option.text())).toEqual(['marin']);
    expect(wrapper.findAll('#media-speed option').map(option => option.text())).toEqual(['1']);
    expect(wrapper.text()).toContain('gpt-live-1'); expect(wrapper.text()).toContain('provider_credential_missing');
    expect(controller.selection).not.toHaveBeenCalled(); expect(controller.action).not.toHaveBeenCalled();
    wrapper.unmount();
  });
  it('preserves typed retired-input errors and cancels all controller ownership on unmount', async () => {
    const wrapper = mount(MediaPanel, { props: { context } }); await flushPromises();
    identity.accountTransport.mockResolvedValue(new Response(JSON.stringify({ code: 'media-input-retired' }), { status: 409 }));
    await expect(transport.request('sim-integration', '/media/input', { method: 'POST' })).rejects.toMatchObject({ status: 409, code: 'media-input-retired' });
    wrapper.unmount(); expect(controller.dispose).toHaveBeenCalledOnce();
  });
});

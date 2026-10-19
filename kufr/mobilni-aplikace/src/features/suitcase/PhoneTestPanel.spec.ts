import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import PhoneTestPanel from './PhoneTestPanel.vue';
import { nativeDebug } from './nativeDebug';
import { deviceAssociation as cloud } from './deviceAssociation';
import type { EngineCapabilities } from '../debug/conversationCapabilities';
const bridge = vi.hoisted(() => ({ status: vi.fn(), stop: vi.fn(), startConversation: vi.fn(), captureCostumePhoto: vi.fn(),
  discardCostumePhoto: vi.fn(), attachCostumePhoto: vi.fn(), speakSystem: vi.fn() }));
vi.mock('@capacitor/core', () => ({ registerPlugin: () => bridge }));
vi.mock('./deviceSession', async () => ({ deviceSession: { native: true, savedDevice: (await import('vue')).ref({ deviceId: 'kufr-1' }) } }));
vi.mock('./deviceAssociation', async () => { const { ref } = await import('vue'); return { deviceAssociation: {
  account: ref({ id: 'admin-1', displayName: 'Admin', roles: ['Administrator'] }), canOperate: ref(true), administrator: ref(true) } }; });
function capabilities(): EngineCapabilities { return { schemaVersion: 1, primaryEngine: 'gpt-live', engines: ['gpt-live', 'realtime'].map(value => {
  const engine = value as 'gpt-live' | 'realtime'; return { engine, model: engine === 'gpt-live' ? 'gpt-live-1' : 'gpt-realtime-2.1', ready: true,
    voices: engine === 'gpt-live' ? ['cedar'] : ['marin', 'coral'], speed: { supported: engine === 'realtime', min: engine === 'realtime' ? .8 : 1, max: engine === 'realtime' ? 1.2 : 1, default: 1 }, inputModes: ['audio'], cameraSources: ['phone'] }; }) }; }
let wrapper: VueWrapper;
function button(label: string) { const value = wrapper.findAll('button').find(item => item.text() === label); if (!value) throw new Error(label); return value; }
beforeEach(() => {
  vi.clearAllMocks();
  const preferences = new Map<string, string>();
  Object.defineProperty(window, 'localStorage', { configurable: true, value: {
    getItem: (key: string) => preferences.get(key) ?? null,
    setItem: (key: string, value: string) => preferences.set(key, value),
  } });
  cloud.account.value = { id: 'admin-1', displayName: 'Admin', roles: ['Administrator'] };
  (cloud.administrator as { value: boolean }).value = true;
  nativeDebug.status.value = { configured: true, running: false, phase: 'idle', mode: 'openai', accessMode: 'account', deviceId: 'kufr-1', engines: capabilities() };
  nativeDebug.pending.value = false; nativeDebug.cancellationPending.value = false;
  bridge.status.mockImplementation(async () => nativeDebug.status.value);
  bridge.stop.mockResolvedValue(undefined); bridge.startConversation.mockResolvedValue(undefined);
  bridge.discardCostumePhoto.mockResolvedValue(undefined);
});
afterEach(() => { wrapper?.unmount(); nativeDebug.dispose(); vi.restoreAllMocks(); });
describe('phone Vue engine and consent journeys', () => {
  it('offers only live/realtime with metadata voices and no retired text or PTT actions', async () => {
    wrapper = mount(PhoneTestPanel); await flushPromises();
    expect(wrapper.get('[data-engine]').findAll('option').map(item => item.attributes('value'))).toEqual(['', 'gpt-live', 'realtime']);
    expect(wrapper.get('[data-voice]').findAll('option').map(item => item.text())).toEqual(['cedar']);
    expect(wrapper.get('[data-speed]').attributes('disabled')).toBeDefined();
    expect(wrapper.find('textarea').exists()).toBe(false); expect(bridge.startConversation).not.toHaveBeenCalled();
    await wrapper.get('[data-engine]').setValue('realtime'); await wrapper.get('[data-speed]').setValue('1.15');
    nativeDebug.status.value = { ...nativeDebug.status.value, engines: capabilities() }; await flushPromises();
    expect((wrapper.get('[data-speed]').element as HTMLInputElement).value).toBe('1.15');
    await button('Spustit AI rozhovor').trigger('click'); await flushPromises();
    expect(bridge.startConversation).toHaveBeenCalledWith({ expectedMode: 'openai', readyMask: 63, costumeDescription: undefined, engine: 'realtime', voice: 'marin', speed: 1.15 });
  });
  it('never silently changes the selected unavailable engine or starts without capabilities', async () => {
    nativeDebug.status.value.engines!.engines[0]!.ready = false;
    wrapper = mount(PhoneTestPanel); await flushPromises();
    expect((wrapper.get('[data-engine]').element as HTMLSelectElement).value).toBe('gpt-live');
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeDefined();
    nativeDebug.status.value = { ...nativeDebug.status.value, engines: undefined }; await flushPromises();
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeDefined(); expect(bridge.startConversation).not.toHaveBeenCalled();
  });
  it('blocks an unrelated retired saved preference until explicit engine choice', async () => {
    window.localStorage.setItem('kufr.conversation.engine.v1', 'chained'); wrapper = mount(PhoneTestPanel); await flushPromises();
    expect((wrapper.get('[data-engine]').element as HTMLSelectElement).value).toBe('');
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeDefined();
    expect(wrapper.text()).toContain('Uložený režim už není podporován');
  });
  it('requires explicit identity and consent confirmation and consumes it once for a new guest', async () => {
    wrapper = mount(PhoneTestPanel); await flushPromises(); await wrapper.get('[data-guest-id]').setValue('Guest-042');
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeDefined();
    await wrapper.get('[data-guest-verified]').setValue(true); await wrapper.get('[data-guest-long-term]').setValue(true);
    await wrapper.get('[data-guest-cloud]').setValue(true);
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeDefined();
    await wrapper.get('[data-guest-confirm]').setValue(true); await button('Spustit AI rozhovor').trigger('click'); await flushPromises();
    expect(bridge.startConversation).toHaveBeenCalledWith(expect.objectContaining({ confirmGuestMemory: true,
      guestMemory: { guestId: 'Guest-042', identityVerified: true, longTermAllowed: true, cloudExportAllowed: true } }));
    expect((wrapper.get('[data-guest-id]').element as HTMLInputElement).value).toBe('');
    expect(window.localStorage.getItem('Guest-042')).toBeNull();
  });
  it('lets an Operator start anonymously without exposing administrator identity binding', async () => {
    (cloud.administrator as { value: boolean }).value = false;
    wrapper = mount(PhoneTestPanel); await flushPromises(); expect(wrapper.find('[data-guest-id]').exists()).toBe(false);
    await button('Spustit AI rozhovor').trigger('click'); await flushPromises();
    expect(bridge.startConversation.mock.calls[0]![0]).not.toHaveProperty('guestMemory');
    expect(bridge.startConversation.mock.calls[0]![0]).not.toHaveProperty('confirmGuestMemory');
  });
  it('requires a new identity verification when a confirmed guest ID changes', async () => {
    wrapper = mount(PhoneTestPanel); await flushPromises(); await wrapper.get('[data-guest-id]').setValue('First');
    await wrapper.get('[data-guest-verified]').setValue(true); await wrapper.get('[data-guest-confirm]').setValue(true);
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeUndefined();
    await wrapper.get('[data-guest-id]').setValue('Second');
    expect((wrapper.get('[data-guest-verified]').element as HTMLInputElement).checked).toBe(false);
    expect((wrapper.get('[data-guest-confirm]').element as HTMLInputElement).checked).toBe(false);
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeDefined();
  });
  it('Stop supersedes a pending start and prevents a late photo from attaching to the stopped guest', async () => {
    let finish!: () => void; bridge.startConversation.mockReturnValue(new Promise<void>(resolve => { finish = resolve; }));
    bridge.captureCostumePhoto.mockResolvedValue({ draftId: 'approved-once' });
    wrapper = mount(PhoneTestPanel); await flushPromises();
    const consent = wrapper.findAll('input[type="checkbox"]').find(item => item.element.parentElement?.textContent?.includes('odesláním jedné'))!;
    await consent.setValue(true); await button('Pořídit schválenou fotku').trigger('click'); await flushPromises();
    await button('Spustit AI rozhovor').trigger('click'); await flushPromises();
    await button('Ukončit testovací rozhovor').trigger('click'); await flushPromises(); expect(bridge.stop).toHaveBeenCalledOnce();
    finish(); await flushPromises(); expect(bridge.attachCostumePhoto).not.toHaveBeenCalled();
  });
  it('attaches or removes a separately approved photo during healthy live audio without stopping it', async () => {
    nativeDebug.status.value = { ...nativeDebug.status.value, session: true, running: true, effectiveEngine: 'gpt-live', phase: 'listening' };
    bridge.captureCostumePhoto.mockResolvedValue({ draftId: 'live-photo' }); bridge.attachCostumePhoto.mockResolvedValue({ status: 'attached', imageId: 'image-1', revision: 1 });
    wrapper = mount(PhoneTestPanel); await flushPromises();
    const consent = wrapper.findAll('input[type="checkbox"]').find(item => item.element.parentElement?.textContent?.includes('odesláním jedné'))!;
    await consent.setValue(true); await button('Pořídit schválenou fotku').trigger('click'); await flushPromises();
    expect(bridge.attachCostumePhoto).not.toHaveBeenCalled();
    await button('Odeslat schválenou fotku').trigger('click'); await flushPromises();
    expect(bridge.attachCostumePhoto).toHaveBeenCalledWith({ draftId: 'live-photo' });
    await button('Zahodit fotku').trigger('click'); await flushPromises();
    expect(bridge.discardCostumePhoto).toHaveBeenCalledOnce(); expect(bridge.stop).not.toHaveBeenCalled();
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, type VueWrapper } from '@vue/test-utils';
import { nextTick } from 'vue';
import SuitcaseView from './SuitcaseView.vue';
import ConnectionsView from './ConnectionsView.vue';
import { deviceSession as device } from './deviceSession';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
vi.mock('@capacitor/core', () => ({ Capacitor: { getPlatform: () => 'web', isNativePlatform: () => false }, registerPlugin: () => ({}) }));
vi.mock('../device_connection/NativeDeviceConnection', () => ({ nativeBridge: {}, NativeDeviceConnection: class {} }));
let wrapper: VueWrapper;
const mountOptions = { attachTo: document.body, global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } };
function button(label: string) { const result = wrapper.findAll('button').find(item => item.text() === label); if (!result) throw new Error(`Missing button ${label}`); return result; }
function state(overrides: Partial<DeviceSnapshot> = {}): DeviceSnapshot {
  return { source: 'native-device', phase: 'ready', controlMode: 'observer', readyMask: 63, activeCompartment: null,
    busy: false, revision: 5, status: 'Connected', ...overrides };
}
beforeEach(() => { device.dispose(); device.snapshot.value = null; device.target.value = 'unavailable'; });
afterEach(() => { wrapper?.unmount(); device.dispose(); vi.restoreAllMocks(); });
describe('Vue suitcase journeys', () => {
  it('opens the browser unavailable and only creates a visibly labelled demo on explicit tap', async () => {
    wrapper = mount(SuitcaseView, mountOptions);
    expect(wrapper.text()).toContain('Kufr není připojený'); expect(device.snapshot.value).toBeNull();
    await button('Otevřít místní ukázku').trigger('click');
    expect(wrapper.text()).toContain('MÍSTNÍ UKÁZKA'); expect(device.snapshot.value?.source).toBe('local-fake');
    expect(device.snapshot.value?.controlMode).toBe('observer');
  });
  it('requires explicit manual acquisition and confirmation before a selection, invalidated by revision', async () => {
    device.useDemo(); wrapper = mount(SuitcaseView, mountOptions);
    const select = vi.spyOn(device, 'select'); await button('Ruční ovládání').trigger('click');
    expect(button('Převzít ruční řízení').attributes('disabled')).toBeUndefined();
    await button('Převzít ruční řízení').trigger('click');
    await wrapper.findAll('button').find(item => item.text() === 'Vybrat přihrádku')!.trigger('click');
    expect(select).not.toHaveBeenCalled(); expect(wrapper.text()).toContain('Potvrzení může spustit fyzický pohyb');
    device.snapshot.value = { ...device.snapshot.value!, revision: 10 }; await nextTick();
    expect(wrapper.text()).not.toContain('Potvrzení může spustit fyzický pohyb'); expect(select).not.toHaveBeenCalled();
  });
  it('preserves a text draft across heartbeats and clears it on lease revocation', async () => {
    device.snapshot.value = state({ source: 'e2-simulator', controlMode: 'manual', conversation: { canSubmit: true, canCancel: false, pending: false, reply: null, simulated: false, status: '' } });
    wrapper = mount(SuitcaseView, mountOptions); await button('Text').trigger('click');
    await wrapper.get('textarea').setValue('Rozepsaný host'); const element = wrapper.get('textarea').element;
    element.focus(); device.snapshot.value = { ...device.snapshot.value! }; await nextTick();
    expect(wrapper.get('textarea').element).toBe(element); expect(element.value).toBe('Rozepsaný host'); expect(document.activeElement).toBe(element);
    device.snapshot.value = { ...device.snapshot.value!, controlMode: 'observer' }; await nextTick(); expect(element.value).toBe('');
  });
  it('does not offer or send retired text turns for a real device', () => {
    device.snapshot.value = state({ controlMode: 'manual', conversation: { canSubmit: true, canCancel: true, pending: false, reply: null, simulated: false, status: '' } });
    const submit = vi.spyOn(device, 'submitText');
    wrapper = mount(SuitcaseView, mountOptions);
    expect(wrapper.findAll('button').some(item => item.text() === 'Text')).toBe(false);
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(submit).not.toHaveBeenCalled();
  });
  it('requires device voice availability and consumes one photo consent only for an explicit start', async () => {
    device.snapshot.value = state({ voice: { available: true, cameraAvailable: true, mode: 'realtime', voice: 'cedar',
      phase: 'idle', error: null, pending: false, canConfigure: true, canStart: true, canStop: false, status: 'Ready' } });
    const start = vi.spyOn(device, 'startVoice').mockImplementation(() => true);
    wrapper = mount(SuitcaseView, mountOptions); await wrapper.get('input[type="checkbox"]').setValue(true);
    await button('Spustit hlas na kufru').trigger('click'); expect(start).toHaveBeenCalledWith(true);
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false);
    device.snapshot.value = { ...device.snapshot.value!, controlMode: 'manual' }; await nextTick();
    expect(button('Spustit hlas na kufru').attributes('disabled')).toBeDefined();
  });
  it('connections clearly separates native-only pairing from explicit browser targets', () => {
    wrapper = mount(ConnectionsView, mountOptions);
    expect(wrapper.text()).toContain('aplikaci na iPhonu'); expect(wrapper.find('input').exists()).toBe(false);
    expect(button('Otevřít místní ukázku').exists()).toBe(true); expect(device.snapshot.value).toBeNull();
  });
});

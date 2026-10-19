import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import { nextTick } from 'vue';
import AwButton from '../src/components/AwButton.vue';
import AwField from '../src/components/AwField.vue';
import SuitcaseView from '../src/features/suitcase/SuitcaseView.vue';
import App from '../src/application/App.vue';
import { safeReturnPath } from '../src/application/navigation';
import { deviceSession } from '../src/features/suitcase/deviceSession';
import { nativeDebug } from '../src/features/suitcase/nativeDebug';
import { refreshIdentity } from '../src/features/identity/session';
import type { DeviceSnapshot } from '../src/features/device_connection/FakeDeviceConnection';

vi.mock('../src/features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), refreshIdentity: vi.fn(async () => {}), hasRole: () => false };
});

function snapshot(revision = 1): DeviceSnapshot {
  return { source: 'e2-simulator', controlMode: 'manual', phase: 'ready', readyMask: 63,
    activeCompartment: null, busy: false, revision, status: 'Označený syntetický stav',
    conversation: { canSubmit: true, canCancel: false, status: '', pending: false } as DeviceSnapshot['conversation'] };
}
const wrappers: Array<ReturnType<typeof mount>> = [];
beforeEach(() => { deviceSession.snapshot.value = null; deviceSession.target.value = 'unavailable'; });
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.innerHTML = ''; vi.restoreAllMocks(); });
function renderSuitcase() {
  const wrapper = mount(SuitcaseView, { attachTo: document.body, global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  wrappers.push(wrapper); return wrapper;
}

describe('application boundaries', () => {
  it('rejects unsafe login return locations', () => {
    for (const value of ['//evil.test', 'https://evil.test', '/\\evil.test', '/signin', '/guest/v1', '/settings\nadmin']) expect(safeReturnPath(value)).toBe('/');
    expect(safeReturnPath('/workshop?part=tray')).toBe('/workshop?part=tray');
    expect(safeReturnPath('/settings/admin')).toBe('/settings/admin');
  });
  it('uses native form and accessible field semantics', async () => {
    const button = mount(AwButton, { props: { busy: true, variant: 'primary' }, slots: { default: 'Uložit' } });
    wrappers.push(button);
    expect(button.attributes('type')).toBe('button'); expect(button.attributes('disabled')).toBeDefined(); expect(button.attributes('aria-busy')).toBe('true');
    const field = mount(AwField, { props: { id: 'name', label: 'Jméno', hint: 'Vaše jméno', error: 'Povinné pole' },
      slots: { default: '<input id="name" aria-describedby="name-hint name-error" aria-invalid="true">' } });
    wrappers.push(field); expect(field.get('label').attributes('for')).toBe('name'); expect(field.get('#name-error').attributes('role')).toBe('alert');
  });
  it('never connects, acquires a lease or sends a command on a route mount', () => {
    const acquire = vi.spyOn(deviceSession, 'acquire'); const select = vi.spyOn(deviceSession, 'select'); const connect = vi.spyOn(deviceSession, 'connectSaved');
    const wrapper = renderSuitcase(); expect(wrapper.text()).toContain('Kufr není připojený');
    expect(acquire).not.toHaveBeenCalled(); expect(select).not.toHaveBeenCalled(); expect(connect).not.toHaveBeenCalled();
  });
  it('preserves typed text, focus and element identity across heartbeat snapshots', async () => {
    deviceSession.snapshot.value = snapshot(); const wrapper = renderSuitcase();
    await wrapper.findAll('button').find(button => button.text() === 'Text')!.trigger('click');
    const input = wrapper.get('textarea'); await input.setValue('Rozepsaná odpověď'); (input.element as HTMLTextAreaElement).focus();
    const original = input.element; deviceSession.snapshot.value = { ...snapshot(), status: 'Další heartbeat' }; await nextTick();
    expect(wrapper.get('textarea').element).toBe(original); expect((original as HTMLTextAreaElement).value).toBe('Rozepsaná odpověď'); expect(document.activeElement).toBe(original);
  });
  it('invalidates a pending physical confirmation after revision changes', async () => {
    deviceSession.snapshot.value = snapshot(); const select = vi.spyOn(deviceSession, 'select'); const wrapper = renderSuitcase();
    await wrapper.findAll('button').find(button => button.text() === 'Ruční ovládání')!.trigger('click');
    await wrapper.findAll('button').find(button => button.text() === 'Vybrat přihrádku')!.trigger('click');
    expect(wrapper.text()).toContain('Spustit výběr'); deviceSession.snapshot.value = snapshot(2); await nextTick();
    expect(wrapper.text()).not.toContain('Spustit výběr'); expect(select).not.toHaveBeenCalled();
  });
  it('keeps the shell and urgent Stop available during navigation and pending work', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<h1>Domov</h1>' } },
      { path: '/archive', component: { template: '<h1>Archiv</h1>' } },
    ] });
    await router.push('/'); await router.isReady();
    deviceSession.snapshot.value = snapshot(); deviceSession.pending.value = true;
    const stop = vi.spyOn(deviceSession, 'stop').mockImplementation(() => {});
    nativeDebug.pending.value = false;
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    const header = wrapper.get('header').element;
    const button = wrapper.findAll('button').find(button => button.text() === 'Stop')!; expect(button.attributes('disabled')).toBeUndefined();
    await button.trigger('click'); expect(stop).toHaveBeenCalledTimes(1);
    await router.push('/archive'); await flushPromises(); expect(wrapper.get('header').element).toBe(header); expect(wrapper.get('main').text()).toContain('Archiv');
    expect(wrapper.findAll('nav').some(nav => nav.attributes('aria-label') === 'Sekce aplikace')).toBe(true);
    const sessionRefresh = vi.mocked(refreshIdentity); sessionRefresh.mockClear();
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(sessionRefresh).toHaveBeenCalledTimes(1);
    wrapper.unmount(); wrappers.splice(wrappers.indexOf(wrapper), 1);
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(sessionRefresh).toHaveBeenCalledTimes(1);
    deviceSession.pending.value = false;
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createPinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import { nextTick, ref, type Ref } from 'vue';
import { Capacitor } from '@capacitor/core';
import AwButton from '../src/components/AwButton.vue';
import AwField from '../src/components/AwField.vue';
import SuitcaseView from '../src/features/suitcase/SuitcaseView.vue';
import App from '../src/application/App.vue';
import { safeReturnPath } from '../src/application/navigation';
import { deviceSession } from '../src/features/suitcase/deviceSession';
import { nativeDebug } from '../src/features/suitcase/nativeDebug';
import { account, accountDeletionRequested, offlineSession, offlineSessionExpiresAt, refreshIdentity } from '../src/features/identity/session';
import { installAccessGuard } from '../src/application/accessGuard';
import type { DeviceSnapshot } from '../src/features/device_connection/FakeDeviceConnection';

vi.mock('../src/features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), accountDeletionRequested: ref(false), offlineSession: ref(false), offlineSessionExpiresAt: ref(0), refreshIdentity: vi.fn(async () => {}), hasRole: () => false };
});

function snapshot(revision = 1): DeviceSnapshot {
  return { source: 'e2-simulator', controlMode: 'manual', phase: 'ready', readyMask: 63,
    activeCompartment: null, busy: false, revision, status: 'Označený syntetický stav',
    conversation: { canSubmit: true, canCancel: false, status: '', pending: false } as DeviceSnapshot['conversation'] };
}
const wrappers: Array<ReturnType<typeof mount>> = [];
const mockDeletionRequested = accountDeletionRequested as Ref<boolean>;
beforeEach(() => { account.value = null; mockDeletionRequested.value = false; offlineSession.value = false; offlineSessionExpiresAt.value = 0; deviceSession.snapshot.value = null; deviceSession.target.value = 'unavailable'; });
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.innerHTML = ''; vi.restoreAllMocks(); });
function renderSuitcase() {
  const wrapper = mount(SuitcaseView, { attachTo: document.body, global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  wrappers.push(wrapper); return wrapper;
}

describe('application boundaries', () => {
  it.each([
    { deletion: true, destination: '/account-deletion' },
    { deletion: false, destination: '/signin' },
  ])('starts local privacy cleanup before routing account loss to $destination', async ({ deletion, destination }) => {
    const events: string[] = [];
    const publicView = { setup() { events.push('public-view'); }, template: '<p>Anonymous view</p>' };
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<p>Landing</p>' } },
      { path: '/signin', component: publicView },
      { path: '/account-deletion', component: publicView },
      { path: '/suitcase', component: { template: '<input aria-label="Private draft" />' }, meta: { requiresAccount: true, operation: true } },
    ] });
    account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] };
    await router.push('/suitcase');
    let finishCleanup!: () => void;
    const cleanup = new Promise<void>(resolve => { finishCleanup = resolve; });
    vi.spyOn(nativeDebug, 'invalidatePrivateState').mockImplementation(() => { events.push('redact'); });
    vi.spyOn(deviceSession, 'clearUserGrants').mockImplementation(() => { events.push('cleanup-start'); return cleanup; });
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    try {
      mockDeletionRequested.value = deletion;
      account.value = null;
      expect(events).toEqual(['redact', 'cleanup-start']);
      // A subsequent offline-state change must not replace the deletion receipt
      // with the ordinary sign-in redirect while navigation is settling.
      offlineSession.value = true;
      await flushPromises();
      expect(router.currentRoute.value.path).toBe(destination);
      expect(wrapper.find('main input').exists()).toBe(false);
      expect(events).toEqual(['redact', 'cleanup-start', 'public-view']);
      expect(router.currentRoute.value.query.return).toBe(deletion ? undefined : '/suitcase');
    } finally { finishCleanup(); await cleanup; }
  });

  it('removes cloud views on offline transition without retiring the paired local session', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<p>Landing</p>' } },
      { path: '/suitcase', component: { template: '<p>Local controls</p>' }, meta: { requiresAccount: true, operation: true, offlineLocal: true } },
      { path: '/suitcase/director', component: { template: '<input aria-label="Cloud draft" />' }, meta: { requiresAccount: true, operation: true } },
    ] });
    installAccessGuard(router);
    account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] };
    await router.push('/suitcase/director');
    const cleanup = vi.spyOn(deviceSession, 'clearUserGrants');
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } }); wrappers.push(wrapper);
    expect(wrapper.find('main input').exists()).toBe(true);
    offlineSessionExpiresAt.value = Date.now() + 60000;
    offlineSession.value = true;
    await flushPromises();
    expect(wrapper.find('main input').exists()).toBe(false);
    expect(router.currentRoute.value.query.access).toBe('online');
    expect(cleanup).not.toHaveBeenCalled();
    vi.mocked(refreshIdentity).mockClear();
    await router.push('/suitcase'); await flushPromises();
    expect(wrapper.text()).toContain('Local controls');
    expect(refreshIdentity).not.toHaveBeenCalled();
  });
  it('unmounts cached operations on logout and discards their drafts before another account enters', async () => {
    let mounts = 0;
    const operation = { setup() { ++mounts; return { draft: ref('') }; }, template: '<input aria-label="Private draft" v-model="draft" />' };
    const router = createRouter({ history: createMemoryHistory(), routes: [
      { path: '/', component: { template: '<p>Public landing</p>' } },
      { path: '/signin', component: { template: '<p>Sign in</p>' } },
      { path: '/suitcase', name: 'private-operation', component: operation, meta: { keepAlive: true, requiresAccount: true, operation: true } },
    ] });
    installAccessGuard(router);
    account.value = { id: 'first', displayName: 'First', roles: ['Operator'] };
    await router.push('/suitcase');
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } });
    wrappers.push(wrapper);
    await wrapper.get('main input').setValue('Private first-account draft');
    account.value = null;
    await flushPromises();
    expect(wrapper.find('main input').exists()).toBe(false);
    expect(router.currentRoute.value.path).toBe('/signin');
    account.value = { id: 'second', displayName: 'Second', roles: ['Operator'] };
    await router.push('/suitcase'); await flushPromises();
    expect((wrapper.get('main input').element as HTMLInputElement).value).toBe('');
    expect(mounts).toBe(2);
    account.value = { ...account.value, roles: ['Viewer'] };
    await flushPromises();
    expect(wrapper.find('main input').exists()).toBe(false);
    expect(router.currentRoute.value.query.access).toBe('operator');
  });
  it.each([false, true])('keeps browser-only install navigation out of native context (native=%s)', async native => {
    vi.spyOn(Capacitor, 'isNativePlatform').mockReturnValue(native);
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<p>Current operation</p>' } }] });
    await router.push('/'); await router.isReady();
    const wrapper = mount(App, { global: { plugins: [router, createPinia()] } });
    wrappers.push(wrapper);
    expect(wrapper.find('.application-install-link').exists()).toBe(!native);
    expect(wrapper.get('main').text()).toBe('Current operation');
  });
  it('switches both registered themes without replacing the route, draft or focus', async () => {
    let mounts = 0;
    const editor = {
      setup() { mounts += 1; return { draft: ref('') }; },
      template: '<label>Rozepsaná poznámka<input v-model="draft" /></label>',
    };
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: editor }] });
    await router.push('/'); await router.isReady();
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [router, createPinia()] } });
    wrappers.push(wrapper);
    const input = wrapper.get('main input');
    await input.setValue('Poznámka zůstává');
    (input.element as HTMLInputElement).focus();
    const selector = wrapper.get('select[aria-label="Vzhled aplikace"]');
    expect(selector.findAll('option').map(option => option.text())).toEqual(['Lord Voldemort', 'Olivander']);
    for (const theme of ['olivander', 'lord-voldemort']) {
      await selector.setValue(theme);
      expect(document.documentElement.dataset.theme).toBe(theme);
      expect(wrapper.get('main input').element).toBe(input.element);
      expect((input.element as HTMLInputElement).value).toBe('Poznámka zůstává');
      expect(document.activeElement).toBe(input.element);
    }
    expect(mounts).toBe(1);
  });
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

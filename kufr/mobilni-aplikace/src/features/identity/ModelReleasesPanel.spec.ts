import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
import ModelReleasesPanel from './ModelReleasesPanel.vue';
import { account } from './session';
const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  const current = ref({ id: 'owner', displayName: 'Owner', roles: ['Owner'] });
  return { account: current, accountRequest: api.request, hasRole: (role: string) => current.value?.roles.includes('Owner') || current.value?.roles.includes(role) };
});
const first = 'a'.repeat(64), second = 'b'.repeat(64);
const release = (hash: string, canSelect = true) => ({ bundleHash: hash, modelId: 'synthetic-test', revision: 'r1', sourceRevision: 'independent-model-source', cadVerification: canSelect ? 'verified' : 'not-verified', canSelect, bytes: 123 });
let active: { bundleHash: string; modelId: string } | null;
const catalog = () => ({ enabled: true, active, releases: [release(first), release(second, false)], invalidReleases: 0, truncated: false, integrity: 'checked_on_selection' });
const wrappers: ReturnType<typeof mount>[] = [];
beforeEach(() => {
  active = null; api.request.mockReset();
  account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] };
  api.request.mockImplementation(async (_path: string, method?: string, body?: { bundleHash: string }) => {
    if (method === 'POST') { active = { bundleHash: body!.bundleHash, modelId: 'synthetic-test' }; return { active }; }
    return structuredClone(catalog());
  });
});
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.innerHTML = ''; });
async function render() {
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/settings/admin', component: { template: '<div />' } }, { path: '/signin', component: { template: '<div />' } }] });
  await router.push('/settings/admin');
  const wrapper = mount(ModelReleasesPanel, { attachTo: document.body, global: { plugins: [router] } });
  wrappers.push(wrapper); await flushPromises(); return wrapper;
}
describe('published model administration', () => {
  it('reads catalog without activating anything and separates model provenance', async () => {
    const wrapper = await render();
    expect(api.request).toHaveBeenCalledExactlyOnceWith('/app/v1/admin/model-releases');
    expect(wrapper.get(`option[value="${second}"]`).attributes('disabled')).toBeDefined();
    await wrapper.get('select').setValue(first);
    expect(wrapper.text()).toContain('independent-model-source');
    expect(wrapper.get('button[type="submit"]').attributes('disabled')).toBeDefined();
  });
  it('requires explicit confirmation and includes the currently observed reference', async () => {
    const wrapper = await render(); await wrapper.get('select').setValue(first);
    await wrapper.get('form').trigger('submit');
    expect(api.request.mock.calls.filter(call => call[1] === 'POST')).toHaveLength(0);
    await wrapper.get('input[type="checkbox"]').setValue(true); await wrapper.get('form').trigger('submit'); await flushPromises();
    expect(api.request).toHaveBeenCalledWith('/app/v1/admin/model-releases/active', 'POST', { bundleHash: first, expectedActiveHash: null, confirm: true });
    expect(wrapper.text()).toContain('Aktivní model byl změněn');
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false);
  });
  it('offers identity-bound reauthentication after an error without silently retrying', async () => {
    const wrapper = await render();
    api.request.mockRejectedValueOnce(new Error('Je nutné nové ověření přístupovým klíčem.'));
    await wrapper.get('select').setValue(first); await wrapper.get('input[type="checkbox"]').setValue(true); await wrapper.get('form').trigger('submit'); await flushPromises();
    const target = new URL(wrapper.get('a').attributes('href')!, 'https://example.invalid');
    expect(target.pathname).toBe('/signin'); expect(target.searchParams.get('reauth')).toBe('1'); expect(target.searchParams.get('return')).toBe('/settings/admin');
    expect(api.request.mock.calls.filter(call => call[1] === 'POST')).toHaveLength(1);
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false);
  });
  it('invalidates pending confirmation when the same account loses its role', async () => {
    const wrapper = await render(); await wrapper.get('select').setValue(first); await wrapper.get('input[type="checkbox"]').setValue(true);
    account.value = { id: 'owner', displayName: 'Owner', roles: ['Viewer'] }; await flushPromises();
    expect(wrapper.find('form').exists()).toBe(false);
    expect(api.request.mock.calls.filter(call => call[1] === 'POST')).toHaveLength(0);
    account.value = { id: 'owner', displayName: 'Owner', roles: ['Owner'] }; await flushPromises();
    expect((wrapper.get('input[type="checkbox"]').element as HTMLInputElement).checked).toBe(false);
  });
});

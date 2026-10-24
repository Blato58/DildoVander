import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount, flushPromises, type VueWrapper } from '@vue/test-utils';
import { createRouter, createMemoryHistory } from 'vue-router';
const api = vi.hoisted(() => ({ request: vi.fn(), setAccount: (_value: unknown) => {} }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue'); const account = ref<unknown>({ id: 'owner' });
  api.setAccount = value => { account.value = value; };
  return { account, accountRequest: api.request, offlineSession: ref(false), accountDeletionRequested: ref(false) };
});
import CustomerDuelHistory from './CustomerDuelHistory.vue';
const id = '11111111-1111-4111-8111-111111111111';
const duel = { id, revision: 1, state: 'Completed', mode: 'interactive', round: 3, left: { displayName: 'Luna', wandId: 'wand-left' }, right: { displayName: 'Sol', wandId: 'wand-right' }, rounds: [], result: { leftPoints: 2, rightPoints: 1, ending: 'Konec.' }, leftChoice: 'private' };
let wrapper: VueWrapper | undefined;
async function render() { const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/:pathMatch(.*)*', component: CustomerDuelHistory }] }); await router.push('/me/keepsakes'); wrapper = mount(CustomerDuelHistory, { global: { plugins: [router] } }); await flushPromises(); return wrapper; }
beforeEach(() => { api.setAccount({ id: 'owner' }); api.request.mockReset(); vi.spyOn(document, 'hidden', 'get').mockReturnValue(false); });
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks(); });
describe('account battle history', () => {
  it('reads owned history and links to read-only account detail without leaking hidden fields', async () => {
    api.request.mockResolvedValue({ items: [duel] }); const view = await render();
    expect(view.text()).toContain('Luna'); expect(view.text()).not.toContain('private');
    expect(view.get('a').attributes('href')).toBe(`/archive/duel?account=1&duel=${id}`);
    expect(api.request).toHaveBeenCalledWith('/app/v1/customer/duels', 'GET', undefined, expect.any(AbortSignal));
  });
  it('clears the prior account before loading, and fences a late nonabortable result', async () => {
    let complete!: (value: unknown) => void; api.request.mockReturnValue(new Promise(resolve => { complete = resolve; }));
    const view = await render(); const prior = complete; api.setAccount(null); prior({ items: [duel] }); await flushPromises();
    expect(view.text()).not.toContain('Luna'); expect(view.find('a').exists()).toBe(false);
  });
  it('rejects malformed history without retaining a partial prior list', async () => {
    api.request.mockResolvedValue({ items: [duel] }); const view = await render();
    api.request.mockResolvedValue({ items: [duel, { id: 'malformed' }] }); await view.find('button').trigger('click'); await flushPromises();
    expect(view.text()).not.toContain('Luna'); expect(view.text()).toContain('Historii soubojů se nepodařilo ověřit');
  });
});

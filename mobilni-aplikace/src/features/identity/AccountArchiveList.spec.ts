import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { account, offlineSession } from './session';
import AccountArchiveList from './AccountArchiveList.vue';

const api = vi.hoisted(() => ({ request: vi.fn() }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'account-a' }), offlineSession: ref(false), accountRequest: api.request };
});

const owner = { locator: 'owner-id', linkedAt: 1, officialName: 'Moje památka', accessKind: 'originalOwner', canManage: true, canUnlink: false };
const linked = { locator: 'linked-id', linkedAt: 2, officialName: 'Propojená památka', accessKind: 'linkedViewer', canManage: false, canUnlink: true };
let wrappers: ReturnType<typeof mount>[] = [];
const render = async () => {
  const wrapper = mount(AccountArchiveList, { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } });
  wrappers.push(wrapper); await flushPromises(); return wrapper;
};

beforeEach(() => {
  account.value = { id: 'account-a' } as never;
  offlineSession.value = false;
  api.request.mockReset().mockResolvedValue({ items: [owner, linked] });
});
afterEach(() => { wrappers.forEach(wrapper => wrapper.unmount()); wrappers = []; vi.restoreAllMocks(); vi.unstubAllGlobals(); });

describe('account archive list capabilities', () => {
  it('labels original-owner and linked-viewer rows accurately and only offers unlink for a linked viewer', async () => {
    const wrapper = await render();
    const rows = wrapper.findAll('li');
    expect(rows[0].text()).toContain('Vaše památka · tento účet ji může spravovat');
    expect(rows[0].find('button').exists()).toBe(false);
    expect(rows[1].text()).toContain('Propojená památka · pouze čtení');
    expect(rows[1].find('button').text()).toBe('Odpojit od účtu');
  });

  it('fails closed for unknown or contradictory capability projections', async () => {
    api.request.mockResolvedValue({ items: [
      { ...owner, locator: 'unknown-id', accessKind: 'unexpected', canManage: true, canUnlink: true },
      { ...linked, locator: 'contradictory-id', canManage: true },
    ] });
    const wrapper = await render();
    for (const row of wrapper.findAll('li')) {
      expect(row.text()).toContain('Přístup nelze ověřit; odpojení není dostupné.');
      expect(row.find('button').exists()).toBe(false);
    }
  });

  it('unlinks only a live linked-viewer item and ignores the action if the account changes during confirmation', async () => {
    const confirm = vi.fn(() => true);
    vi.stubGlobal('confirm', confirm);
    const wrapper = await render();
    await wrapper.findAll('li')[0].findAll('button').at(0)?.trigger('click');
    expect(confirm).not.toHaveBeenCalled();
    await wrapper.findAll('li')[1].find('button').trigger('click');
    expect(api.request.mock.calls.some(([path, method]) => path === '/app/v1/archive/associations/linked-id' && method === 'DELETE')).toBe(true);

    api.request.mockReset().mockResolvedValue({ items: [linked] });
    const switched = await render();
    confirm.mockImplementationOnce(() => { account.value = { id: 'account-b' } as never; return true; });
    await switched.find('button').trigger('click');
    expect(confirm).toHaveBeenCalledTimes(2);
    expect(api.request.mock.calls.some(([, method]) => method === 'DELETE')).toBe(false);
  });
});

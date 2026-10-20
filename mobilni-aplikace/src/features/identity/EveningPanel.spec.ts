import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import EveningPanel from './EveningPanel.vue';
import { account } from './session';
const api = vi.hoisted(() => ({ transport: vi.fn(), refresh: vi.fn() }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'operator', displayName: 'Operator', roles: ['Operator'] }), accountTransport: api.transport, refreshIdentity: api.refresh };
});
const first = '11111111-1111-4111-8111-111111111111';
const second = '22222222-2222-4222-8222-222222222222';
const list = { activeEveningId: first, evenings: [
  { eveningId: first, name: 'První večer', createdAt: '2026-10-19T10:00:00Z', exchangeCount: 2, droppedCount: 0 },
  { eveningId: second, name: 'Druhý večer', createdAt: '2026-10-19T12:00:00Z', exchangeCount: 0, droppedCount: 0 },
] };
const details = (canOperate = true) => ({ devices: [{deviceId: 'granted-device', canOperate}], grants: [], associations: [], sessions: [], passkeyCount: 1, passkeys: [] });
const wrappers: ReturnType<typeof mount>[] = [];
beforeEach(() => { api.transport.mockReset(); api.refresh.mockReset(); account.value = { id: 'operator', displayName: 'Operator', roles: ['Operator'] }; api.transport.mockImplementation(async () => Response.json(list)); Object.defineProperty(window, 'confirm', { configurable: true, value: vi.fn(() => true) }); });
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks(); });
async function render(canOperate = true) { const wrapper = mount(EveningPanel, { props: {details: details(canOperate)} }); wrappers.push(wrapper); await flushPromises(); return wrapper; }
describe('shared account evening management', () => {
  it('loads exact device without a mutation and keeps read grants read-only', async () => {
    const wrapper = await render(false);
    expect(api.transport).toHaveBeenCalledTimes(1);
    expect(api.transport.mock.calls[0][0]).toBe('/v1/evenings');
    expect(api.transport.mock.calls[0][1].headers['X-DV-Device-Id']).toBe('granted-device');
    expect(wrapper.text()).toContain('První večer'); expect(wrapper.find('form').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Aktivovat večer');
  });
  it('requires confirmation and preserves idempotency and expected revision across uncertain retry', async () => {
    const wrapper = await render(); await wrapper.get('input').setValue('Nový večer');
    vi.mocked(window.confirm).mockReturnValueOnce(false); await wrapper.get('form').trigger('submit');
    expect(api.transport).toHaveBeenCalledTimes(1);
    api.transport.mockRejectedValueOnce(new Error('Spojení přerušeno'));
    await wrapper.get('form').trigger('submit'); await flushPromises();
    const mutation = api.transport.mock.calls[1][1];
    expect(JSON.parse(mutation.body)).toEqual({ name: 'Nový večer', expectedEveningId: first });
    expect(mutation.headers['Idempotency-Key']).toMatch(/^[0-9a-f-]{36}$/i);
    const retry = wrapper.findAll('button').find(button => button.text() === 'Zopakovat stejný požadavek')!;
    await retry.trigger('click'); await flushPromises();
    expect(api.transport.mock.calls[2][1]).toEqual(mutation);
  });
  it('discards an old account response immediately after account change', async () => {
    let complete!: (value: Response) => void;
    api.transport.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    const wrapper = mount(EveningPanel, { props: {details: details()} }); wrappers.push(wrapper);
    await flushPromises(); account.value = { id: 'other', displayName: 'Other', roles: ['Viewer'] };
    await flushPromises(); complete(Response.json(list)); await flushPromises();
    expect(wrapper.text()).not.toContain('První večer'); expect(wrapper.find('form').exists()).toBe(false);
  });
  it('clears private rows and reports a revoked device grant', async () => {
    const wrapper = await render();
    api.transport.mockResolvedValueOnce(Response.json({code: 'access_token_required'}, { status: 401 }));
    await wrapper.findAll('button').find(button => button.text() === 'Obnovit večery')!.trigger('click'); await flushPromises();
    expect(api.refresh).toHaveBeenCalledOnce(); expect(wrapper.text()).not.toContain('První večer');
    expect(wrapper.text()).toContain('už nemáte oprávnění');
  });
});

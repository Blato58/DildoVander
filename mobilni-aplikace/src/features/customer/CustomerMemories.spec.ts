import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';

const api = vi.hoisted(() => ({ read: vi.fn(), mutate: vi.fn(), setAccount: (_id: string | null) => {} }));
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref<{ id: string } | null>({ id: 'alice' });
  api.setAccount = id => { account.value = id ? { id } : null; };
  return { account, accountDeletionRequested: ref(false) };
});
vi.mock('./customerMemory', async importOriginal => ({
  ...await importOriginal<typeof import('./customerMemory')>(),
  readCustomerMemories: api.read, mutateCustomerMemory: api.mutate,
}));
import CustomerMemories from './CustomerMemories.vue';
import { CustomerMemoryError } from './customerMemory';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

const fact = {
  id: '10000000-0000-4000-8000-000000000001', revision: 1, text: 'Modrá je moje oblíbená.',
  kind: 'UserReportedFact', canCorrect: true, canForget: true,
};
const confirmed = { ...fact, id: '10000000-0000-4000-8000-000000000002', kind: 'ConfirmedSelection', text: 'Potvrzená volba', canCorrect: false };
const list = (items = [fact, confirmed]) => ({ revision: 'a'.repeat(64), complete: true, items });
let wrapper: VueWrapper | undefined;
const online = Object.getOwnPropertyDescriptor(navigator, 'onLine');
const visibility = Object.getOwnPropertyDescriptor(document, 'visibilityState');

function button(text: string) {
  const found = wrapper!.findAll('button').find(value => value.text() === text);
  if (!found) throw new Error(`Missing button ${text}`);
  return found;
}

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
  api.read.mockReset().mockResolvedValue(list());
  api.mutate.mockReset().mockResolvedValue(undefined);
  api.setAccount('alice');
  Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
});
afterEach(() => {
  wrapper?.unmount(); wrapper = undefined;
  if (online) Object.defineProperty(navigator, 'onLine', online); else Reflect.deleteProperty(navigator, 'onLine');
  if (visibility) Object.defineProperty(document, 'visibilityState', visibility); else Reflect.deleteProperty(document, 'visibilityState');
});

describe('personal memory controls', () => {
  it('reads without writing and distinguishes empty readable projection from retained data', async () => {
    api.read.mockResolvedValue(list([]));
    wrapper = mount(CustomerMemories); await flushPromises();
    expect(wrapper.text()).toContain('Další uložené údaje mohou být mimo tento seznam');
    expect(api.mutate).not.toHaveBeenCalled();
  });

  it('requires explicit confirmation and only exposes correction for user-reported facts', async () => {
    wrapper = mount(CustomerMemories); await flushPromises();
    expect(wrapper.findAll('button').filter(value => value.text() === 'Opravit')).toHaveLength(1);
    await wrapper.findAll('button').find(value => value.text() === 'Zapomenout')!.trigger('click');
    expect(api.mutate).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('všechny její předchozí verze');
    await button('Ano, zapomenout tuto paměť').trigger('click'); await flushPromises();
    expect(api.mutate).toHaveBeenCalledOnce();
    expect(api.mutate.mock.calls[0]?.[1]).toEqual({ confirmed: true });
    expect(api.read).toHaveBeenCalledTimes(2);
  });

  it('does not replay an uncertain correction and requires a fresh read before another action', async () => {
    api.mutate.mockRejectedValue(new CustomerMemoryError('memory_mutation_outcome_unknown', 409));
    wrapper = mount(CustomerMemories); await flushPromises();
    await button('Opravit').trigger('click');
    await wrapper.get('textarea').setValue('Zelená.');
    await button('Uložit opravu').trigger('click'); await flushPromises();
    expect(api.mutate).toHaveBeenCalledOnce();
    expect(wrapper.text()).toContain('obnovte seznam a zkontrolujte výsledek');
    expect(button('Opravit').attributes('disabled')).toBeDefined();
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(api.mutate).toHaveBeenCalledOnce();
    await button('Obnovit paměti').trigger('click'); await flushPromises();
    expect(button('Opravit').attributes('disabled')).toBeUndefined();
  });

  it('discards a late read on account switch and clears another account draft', async () => {
    let resolve!: (value: unknown) => void;
    api.read.mockImplementationOnce(() => new Promise(value => { resolve = value; }));
    wrapper = mount(CustomerMemories); await flushPromises();
    api.setAccount('bob'); await flushPromises();
    expect(api.read).toHaveBeenCalledTimes(2);
    resolve(list([{ ...fact, text: 'Alice private late content' }])); await flushPromises();
    expect(wrapper.text()).not.toContain('Alice private late content');
    await button('Opravit').trigger('click');
    api.setAccount(null); await flushPromises();
    expect(wrapper.find('textarea').exists()).toBe(false);
    expect(wrapper.text()).not.toContain('Modrá je moje oblíbená.');
  });

  it('reacts to English without losing forget consequences or the owner data warning', async () => {
    api.read.mockResolvedValue(list([fact]));
    wrapper = mount(CustomerMemories); await flushPromises();
    interfaceLanguagePreference.setLanguage('en');
    await flushPromises();
    expect(wrapper.text()).toContain('My memories');
    expect(wrapper.text()).toContain('Personal facts that are currently available to this account. Memories are not automatically deleted.');
    await button('Forget').trigger('click');
    expect(wrapper.text()).toContain('Forget this memory and all its previous versions? This change cannot be undone.');
  });

  it('blocks offline actions and refreshes on return without retrying a write', async () => {
    wrapper = mount(CustomerMemories); await flushPromises();
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    window.dispatchEvent(new Event('offline')); await flushPromises();
    expect(button('Opravit').attributes('disabled')).toBeDefined();
    expect(wrapper.text()).toContain('Jste offline');
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
    window.dispatchEvent(new Event('online')); await flushPromises();
    expect(api.read).toHaveBeenCalledTimes(2);
    expect(api.mutate).not.toHaveBeenCalled();
  });

  it('removes prior private content after access is revoked', async () => {
    wrapper = mount(CustomerMemories); await flushPromises();
    api.read.mockRejectedValueOnce(new CustomerMemoryError('memory_unavailable', 404));
    await button('Obnovit paměti').trigger('click'); await flushPromises();
    expect(wrapper.text()).not.toContain('Modrá je moje oblíbená.');
    expect(wrapper.text()).toContain('Přístup k pamětem už není dostupný');
    expect(api.mutate).not.toHaveBeenCalled();
  });

  it('bounds rendered records while keeping older readable facts accessible', async () => {
    api.read.mockResolvedValue(list(Array.from({ length: 26 }, (_, index) => ({ ...fact, id: String(index), text: `Fact ${index}` }))));
    wrapper = mount(CustomerMemories); await flushPromises();
    expect(wrapper.findAll('li')).toHaveLength(25);
    expect(wrapper.text()).not.toContain('Fact 25');
    await button('Další paměti').trigger('click');
    expect(wrapper.findAll('li')).toHaveLength(1);
    expect(wrapper.text()).toContain('Fact 25');
    expect(wrapper.text()).toContain('26 čitelných faktů');
    expect(api.mutate).not.toHaveBeenCalled();
  });
});

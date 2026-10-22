import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import AccountDeletion from './AccountDeletion.vue';
import { account } from './session';

const api = vi.hoisted(() => ({ authenticate: vi.fn(), deleteAccount: vi.fn(), status: vi.fn() }));
vi.mock('./session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), authenticatePasskey: api.authenticate, deleteAccount: api.deleteAccount, getAccountDeletionStatus: api.status };
});
const current = { id: 'current', displayName: 'Můj účet', roles: ['Customer'] };
beforeEach(() => {
  account.value = { ...current };
  api.authenticate.mockReset().mockResolvedValue(undefined);
  api.deleteAccount.mockReset();
  api.status.mockReset().mockResolvedValue('deletion_requested');
});
afterEach(() => vi.useRealTimers());

describe('account deletion confirmation', () => {
  it('explains irreversible own-data deletion and never mutates before explicit submission', async () => {
    const view = mount(AccountDeletion);
    expect(view.text()).toContain('Smazání nelze vrátit zpět');
    expect(view.text()).toContain('data ostatních hostů zůstanou zachována');
    expect(api.deleteAccount).not.toHaveBeenCalled();
    await view.get('button').trigger('click');
    await flushPromises();
    expect(api.authenticate).toHaveBeenCalledExactlyOnceWith('reauth');
    expect(api.deleteAccount).not.toHaveBeenCalled();
    await view.findAll('button').find(button => button.text() === 'Zrušit')!.trigger('click');
    expect(view.text()).not.toContain('Trvale smazat můj účet');
    expect(api.deleteAccount).not.toHaveBeenCalled();
    view.unmount();
  });

  it('retains account after a rejection and requires fresh verification for a retry', async () => {
    api.deleteAccount.mockRejectedValue(new Error('Alespoň jeden aktivní vlastník musí zůstat zachován.'));
    const view = mount(AccountDeletion);
    await view.get('button').trigger('click'); await flushPromises();
    await view.get('button').trigger('click'); await flushPromises();
    expect(api.deleteAccount).toHaveBeenCalledExactlyOnceWith('current');
    expect(account.value?.id).toBe('current');
    expect(view.get('[role="alert"]').text()).toContain('aktivní vlastník');
    expect(view.text()).toContain('Ověřit účet pro smazání');
    view.unmount();
  });

  it('announces accepted asynchronous cleanup after local signout', async () => {
    api.deleteAccount.mockImplementation(async () => { account.value = null; });
    const view = mount(AccountDeletion);
    await view.get('button').trigger('click'); await flushPromises();
    await view.get('button').trigger('click'); await flushPromises();
    expect(view.get('[role="status"]').text()).toContain('Mazání soukromých dat pokračuje na serveru');
    expect(view.text()).not.toContain('Ověřit účet pro smazání');
    view.unmount();
  });

  it('discards reauthentication and deletion controls when the account changes', async () => {
    let complete!: () => void;
    api.authenticate.mockReturnValue(new Promise<void>(resolve => { complete = resolve; }));
    const view = mount(AccountDeletion);
    await view.get('button').trigger('click');
    expect(view.get('button').attributes('disabled')).toBeDefined();
    account.value = { ...current, id: 'other' };
    complete(); await flushPromises();
    expect(view.text()).not.toContain('Trvale smazat můj účet');
    expect(api.deleteAccount).not.toHaveBeenCalled();
    view.unmount();
  });

  it('does not display an old deletion receipt in a new account', async () => {
    let complete!: () => void;
    api.deleteAccount.mockReturnValue(new Promise<void>(resolve => { complete = resolve; }));
    const view = mount(AccountDeletion);
    await view.get('button').trigger('click'); await flushPromises();
    await view.get('button').trigger('click');
    account.value = { ...current, id: 'other' };
    complete(); await flushPromises();
    expect(view.text()).not.toContain('Žádost o smazání byla přijata');
    view.unmount();
  });

  it('restores anonymous receipt status and polls until cleanup completes', async () => {
    vi.useFakeTimers();
    account.value = null;
    api.status.mockResolvedValueOnce('deletion_requested').mockResolvedValueOnce('deleted');
    const view = mount(AccountDeletion);
    await flushPromises();
    expect(view.text()).toContain('Mazání soukromých dat pokračuje');
    await vi.advanceTimersByTimeAsync(5000); await flushPromises();
    expect(view.text()).toContain('bylo dokončeno');
    await vi.advanceTimersByTimeAsync(6000);
    expect(api.status).toHaveBeenCalledTimes(2);
    view.unmount();
  });

  it('silently ignores a missing receipt and supports retry after a status error', async () => {
    account.value = null;
    api.status.mockResolvedValueOnce(null);
    const absent = mount(AccountDeletion); await flushPromises();
    expect(absent.find('section').exists()).toBe(false);
    absent.unmount();
    api.status.mockRejectedValueOnce(new Error('offline')).mockResolvedValueOnce('deleted');
    const view = mount(AccountDeletion); await flushPromises();
    expect(view.get('[role="alert"]').text()).toContain('nepodařilo ověřit');
    await view.get('button').trigger('click'); await flushPromises();
    expect(view.text()).toContain('bylo dokončeno');
    view.unmount();
  });

  it('cancels an anonymous status request on unmount and account switching', async () => {
    account.value = null;
    api.status.mockImplementation(() => new Promise(() => {}));
    const view = mount(AccountDeletion); await flushPromises();
    const signal = api.status.mock.calls.at(-1)![0] as AbortSignal;
    account.value = { ...current };
    expect(signal.aborted).toBe(true);
    view.unmount();
    account.value = null;
    const removed = mount(AccountDeletion); await flushPromises();
    const removedSignal = api.status.mock.calls.at(-1)![0] as AbortSignal;
    removed.unmount();
    expect(removedSignal.aborted).toBe(true);
  });

  it('shows receipt status without exposing deletion controls in receipt-only mode', async () => {
    account.value = null;
    api.status.mockResolvedValueOnce('deleted');
    const view = mount(AccountDeletion, { props: { receiptOnly: true } });
    await flushPromises();
    expect(api.status).toHaveBeenCalledOnce();
    expect(view.text()).toContain('bylo dokončeno');
    expect(view.text()).not.toContain('Ověřit účet pro smazání');
    view.unmount();
  });

  it('hides receipt-only status for a signed-in account, including late responses and reload', async () => {
    account.value = null;
    let complete!: (status: string) => void;
    api.status.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    const view = mount(AccountDeletion, { props: { receiptOnly: true } });
    await flushPromises();
    const signal = api.status.mock.calls.at(-1)![0] as AbortSignal;
    account.value = { ...current, id: 'other' };
    complete('deleted');
    await flushPromises();
    expect(signal.aborted).toBe(true);
    expect(view.text()).toContain('Jste přihlášeni k aktivnímu účtu');
    expect(view.text()).not.toContain('bylo dokončeno');
    expect(view.text()).not.toContain('Ověřit účet pro smazání');
    view.unmount();
    api.status.mockClear();
    const reloaded = mount(AccountDeletion, { props: { receiptOnly: true } });
    await flushPromises();
    expect(api.status).not.toHaveBeenCalled();
    expect(reloaded.text()).not.toContain('bylo dokončeno');
    reloaded.unmount();
  });

  it('bounds automatic checks and pauses them while hidden', async () => {
    vi.useFakeTimers();
    account.value = null;
    const view = mount(AccountDeletion); await flushPromises();
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(6000);
    expect(api.status).toHaveBeenCalledTimes(1);
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange')); await flushPromises();
    await vi.advanceTimersByTimeAsync(310000);
    expect(api.status).toHaveBeenCalledTimes(60);
    await vi.advanceTimersByTimeAsync(20000);
    expect(api.status).toHaveBeenCalledTimes(60);
    view.unmount();
  });
});

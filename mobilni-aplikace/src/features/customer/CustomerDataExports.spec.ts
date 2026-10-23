import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import CustomerDataExports from './CustomerDataExports.vue';
import { customerDataExportPath, type CustomerDataExportStorage } from './customerDataExport';
import { interfaceLanguagePreference } from '../../application/interfaceLanguage';

const state = vi.hoisted(() => ({
  account: null as { id: string; displayName: string; roles: string[] } | null,
  deleting: false,
  offline: false,
  share: vi.fn(),
  setAccount: (_value: { id: string; displayName: string; roles: string[] } | null) => {},
  setDeleting: (_value: boolean) => {},
  setOffline: (_value: boolean) => {},
}));

vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  const account = ref(state.account);
  const accountDeletionRequested = ref(state.deleting);
  const offlineSession = ref(state.offline);
  state.setAccount = value => { account.value = value; };
  state.setDeleting = value => { accountDeletionRequested.value = value; };
  state.setOffline = value => { offlineSession.value = value; };
  return { account, accountDeletionRequested, offlineSession, accountTransport: vi.fn(), shareNativeAccountDataFile: state.share };
});

const customer = { id: 'customer-1', displayName: 'Host', roles: ['Customer'] };
const jobId = 'e513a7ce-1f2a-4320-9ca2-463fb179b0f4';
const jobPath = customerDataExportPath(jobId);
const mounted: Array<{ unmount: () => void }> = [];
let originalOnLine: PropertyDescriptor | undefined;
let originalVisibility: PropertyDescriptor | undefined;
let originalConfirm: PropertyDescriptor | undefined;

function storageWithJob(id = jobId): CustomerDataExportStorage {
  const values = new Map([[`dildovander.customer-data-export.v1.${customer.id}`, JSON.stringify({ schemaVersion: 1, jobId: id, pendingIdempotencyKey: null })]]);
  return { getItem: key => values.get(key) ?? null, setItem: (key, value) => { values.set(key, value); } };
}

function job(stateName = 'ready') {
  return {
    schemaVersion: 1,
    id: jobId,
    state: stateName,
    reason: null,
    requestedAt: '2026-10-23T09:00:00Z',
    readyAt: stateName === 'ready' ? '2026-10-23T09:01:00Z' : null,
    downloadExpiresAt: stateName === 'ready' ? '2026-10-23T10:01:00Z' : null,
    downloadPath: stateName === 'ready' ? `${jobPath}/download` : null,
  };
}

async function render(options: {
  native?: boolean;
  transport?: (path: string, init?: RequestInit) => Promise<Response>;
  storage?: CustomerDataExportStorage;
  deliver?: (file: Blob, name: string) => void | Promise<void>;
} = {}) {
  const wrapper = mount(CustomerDataExports, {
    props: options,
  });
  mounted.push(wrapper);
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  interfaceLanguagePreference.setScope(null);
  interfaceLanguagePreference.setLanguage('cs');
  originalOnLine = Object.getOwnPropertyDescriptor(navigator, 'onLine');
  originalVisibility = Object.getOwnPropertyDescriptor(document, 'hidden');
  originalConfirm = Object.getOwnPropertyDescriptor(window, 'confirm');
  Object.defineProperty(navigator, 'onLine', { configurable: true, value: true });
  Object.defineProperty(document, 'hidden', { configurable: true, value: false });
  state.setAccount?.(customer);
  state.setDeleting?.(false);
  state.setOffline?.(false);
  state.share.mockReset();
  vi.useRealTimers();
});

afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount();
  vi.useRealTimers();
  if (originalOnLine) Object.defineProperty(navigator, 'onLine', originalOnLine);
  else Reflect.deleteProperty(navigator, 'onLine');
  if (originalVisibility) Object.defineProperty(document, 'hidden', originalVisibility);
  else Reflect.deleteProperty(document, 'hidden');
  if (originalConfirm) Object.defineProperty(window, 'confirm', originalConfirm);
  else Reflect.deleteProperty(window, 'confirm');
});

describe('customer data exports UI', () => {
  it('reacts to English while preserving the export scope and technical-job-only deletion meaning', async () => {
    const transport = vi.fn().mockResolvedValue(new Response(JSON.stringify(job()), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const wrapper = await render({ transport, storage: storageWithJob() });
    interfaceLanguagePreference.setLanguage('en');
    await nextTick();
    expect(wrapper.text()).toContain('Download account data');
    expect(wrapper.text()).toContain('It includes the account identifier');
    expect(wrapper.text()).toContain('other people’s data');
    expect(wrapper.findAll('button').some(button => button.text() === 'Delete export job only')).toBe(true);
  });
  it('offers explicit native sharing without downloading on page load', async () => {
    const transport = vi.fn().mockResolvedValue(new Response(JSON.stringify(job()), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const deliver = vi.fn();
    const wrapper = await render({ native: true, transport, storage: storageWithJob(), deliver });

    expect(wrapper.text()).toContain('systémové nabídce sdílení');
    expect(wrapper.text()).toContain('nelze odvolat');
    expect(wrapper.text()).toContain('Export je připraven.');
    expect(wrapper.findAll('button').some(button => button.text() === 'Stáhnout JSON')).toBe(false);
    expect(transport.mock.calls.map(([path, init]) => [path, init?.method ?? 'GET'])).toEqual([[jobPath, 'GET']]);
    expect(deliver).not.toHaveBeenCalled();
    expect(state.share).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it.each([true, false])('reports the native platform completion value %s without promising delivery', async completed => {
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(job()), { headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ schemaVersion: 1, account: { id: customer.id } }), { headers: { 'Content-Type': 'application/json' } }));
    state.share.mockImplementation(async (_file, _name, context) => { context.check(); return { completed }; });
    const wrapper = await render({ native: true, transport, storage: storageWithJob() });
    await wrapper.findAll('button').find(button => button.text() === 'Sdílet JSON')!.trigger('click');
    await flushPromises();
    expect(state.share).toHaveBeenCalledTimes(1);
    expect(state.share.mock.calls[0][1]).toBe('customer-data-export.json');
    expect(wrapper.text()).toContain(completed ? 'To nepotvrzuje uložení ani doručení' : 'Sdílení bylo zrušeno');
    expect(transport).toHaveBeenCalledTimes(2);
  });

  it('aborts pending native sharing when the account changes and clears its result', async () => {
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(job()), { headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ schemaVersion: 1, account: { id: customer.id } }), { headers: { 'Content-Type': 'application/json' } }));
    let finish!: (result: { completed: boolean }) => void;
    state.share.mockImplementation(() => new Promise(resolve => { finish = resolve; }));
    const wrapper = await render({ native: true, transport, storage: storageWithJob() });
    await wrapper.findAll('button').find(button => button.text() === 'Sdílet JSON')!.trigger('click');
    await flushPromises();
    const context = state.share.mock.calls[0][2];
    state.setAccount(null);
    expect(context.signal.aborted).toBe(true);
    expect(() => context.check()).toThrow();
    finish({ completed: true });
    await flushPromises();
    expect(wrapper.text()).not.toContain('Systém oznámil dokončení');
    expect(state.share).toHaveBeenCalledTimes(1);
  });

  it('restores a saved job with GET only and keeps the explicit confirmed replacement separate', async () => {
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(job()), { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify(job('requested')), { status: 202, headers: { Location: jobPath, 'Content-Type': 'application/json' } }));
    Object.defineProperty(window, 'confirm', { configurable: true, value: vi.fn().mockReturnValue(true) });
    const wrapper = await render({ native: true, transport, storage: storageWithJob() });
    expect(transport).toHaveBeenCalledTimes(1);

    await wrapper.findAll('button').find(button => button.text().includes('Připravit nový export'))?.trigger('click');
    await flushPromises();

    expect(transport.mock.calls.map(([path, init]) => [path, init?.method])).toEqual([
      [jobPath, 'GET'],
      ['/app/v1/customer/data/exports', 'POST'],
    ]);
    expect(transport.mock.calls[1][1]?.body).toBe('{"schemaVersion":1}');
    expect(new Headers(transport.mock.calls[1][1]?.headers).get('Idempotency-Key')).toMatch(/^[0-9a-f-]{36}$/i);
    expect(wrapper.text()).toContain('Export byl vyžádán.');
    wrapper.unmount();
  });

  it.each(['session offline', 'browser offline', 'hidden'])('does not delete an existing job while %s', async mode => {
    const transport = vi.fn().mockResolvedValue(new Response(JSON.stringify(job()), { headers: { 'Content-Type': 'application/json' } }));
    const confirm = vi.fn().mockReturnValue(true);
    Object.defineProperty(window, 'confirm', { configurable: true, value: confirm });
    const wrapper = await render({ transport, storage: storageWithJob() });
    if (mode === 'session offline') state.setOffline(true);
    else if (mode === 'browser offline') {
      Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
      window.dispatchEvent(new Event('offline'));
    } else {
      Object.defineProperty(document, 'hidden', { configurable: true, value: true });
      document.dispatchEvent(new Event('visibilitychange'));
    }
    await flushPromises();
    const remove = wrapper.findAll('button').find(button => button.text().includes('Smazat pouze úlohu'))!;
    expect((remove.element as HTMLButtonElement).disabled).toBe(true);
    await remove.trigger('click');
    await flushPromises();
    expect(confirm).not.toHaveBeenCalled();
    expect(transport).toHaveBeenCalledTimes(1);
    expect(transport.mock.calls[0][1]?.method).toBe('GET');
  });

  it('checks wall-clock expiry before native dispatch even when the expiry timer has not run', async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date('2026-10-23T10:00:59Z'));
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(job()), { headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify({ schemaVersion: 1, account: { id: customer.id } }), { headers: { 'Content-Type': 'application/json' } }));
    const dispatch = vi.fn();
    state.share.mockImplementation(async (_file, _name, context) => {
      // Model synchronous file preparation crossing expiry before timers can run.
      vi.setSystemTime(new Date('2026-10-23T10:01:01Z'));
      context.check();
      dispatch();
      return { completed: true };
    });
    const wrapper = await render({ native: true, transport, storage: storageWithJob() });
    await wrapper.findAll('button').find(button => button.text() === 'Sdílet JSON')!.trigger('click');
    await flushPromises();
    expect(state.share).toHaveBeenCalledTimes(1);
    expect(dispatch).not.toHaveBeenCalled();
    expect(wrapper.text()).not.toContain('Systém oznámil dokončení');
  });

  it('retries a restored job after an initial transient status failure, then polls active state to ready', async () => {
    vi.useFakeTimers();
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response('{"title":"unavailable"}', { status: 503 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(job('requested')), { status: 200, headers: { 'Content-Type': 'application/json' } }))
      .mockResolvedValueOnce(new Response(JSON.stringify(job()), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    const wrapper = await render({ native: true, transport, storage: storageWithJob() });
    expect(wrapper.text()).toContain('Stav uloženého exportu se nepodařilo ověřit.');

    await vi.advanceTimersByTimeAsync(5000);
    await flushPromises();
    expect(wrapper.text()).toContain('Export byl vyžádán.');
    await vi.advanceTimersByTimeAsync(5000);
    await flushPromises();
    expect(wrapper.text()).toContain('Export je připraven.');
    expect(wrapper.text()).not.toContain('Služba je dočasně zaneprázdněná.');
    expect(transport).toHaveBeenCalledTimes(3);
    expect(transport.mock.calls.every(([, init]) => init?.method === 'GET')).toBe(true);
    wrapper.unmount();
  });

  it('replaces a definitively rejected pending key only after a new explicit action', async () => {
    const transport = vi.fn()
      .mockResolvedValueOnce(new Response('{"title":"conflict","detail":"private"}', { status: 409 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(job('requested')), { status: 202, headers: { Location: jobPath, 'Content-Type': 'application/json' } }));
    Object.defineProperty(window, 'confirm', { configurable: true, value: vi.fn().mockReturnValue(true) });
    const wrapper = await render({ native: true, transport, storage: storageWithJob('no-job') });

    const action = () => wrapper.findAll('button').find(button => button.text().includes('Připravit export') || button.text().includes('nový export'));
    await action()?.trigger('click');
    await flushPromises();
    expect(wrapper.text()).toContain('Server tento klíč odmítl.');
    expect(transport).toHaveBeenCalledTimes(1);

    await action()?.trigger('click');
    await flushPromises();
    expect(transport).toHaveBeenCalledTimes(2);
    expect(new Headers(transport.mock.calls[0][1]?.headers).get('Idempotency-Key'))
      .not.toBe(new Headers(transport.mock.calls[1][1]?.headers).get('Idempotency-Key'));
    expect(transport.mock.calls[1][1]?.body).toBe('{"schemaVersion":1}');
    expect(wrapper.text()).toContain('Export byl vyžádán.');
    expect(wrapper.text()).not.toContain('private');
    wrapper.unmount();
  });

  it('requires explicit confirmation before a new key could replace another account job', async () => {
    const transport = vi.fn();
    Object.defineProperty(window, 'confirm', { configurable: true, value: vi.fn().mockReturnValue(false) });
    const wrapper = await render({ native: true, transport });
    await wrapper.findAll('button').find(button => button.text() === 'Připravit export')?.trigger('click');
    await flushPromises();

    expect(transport).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('Zatím nemáte připravený export.');
    wrapper.unmount();
  });

  it('does not issue export requests when signed out, deleting, or offline', async () => {
    const transport = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }));
    state.setAccount?.(null);
    const signedOut = await render({ transport });
    expect(transport).not.toHaveBeenCalled();
    signedOut.unmount();

    state.setAccount?.(customer);
    state.setDeleting?.(true);
    const deleting = await render({ transport });
    expect(transport).not.toHaveBeenCalled();
    deleting.unmount();

    state.setDeleting?.(false);
    Object.defineProperty(navigator, 'onLine', { configurable: true, value: false });
    const offline = await render({ transport });
    expect(offline.text()).toContain('Pro přípravu a ověření exportu je potřeba připojení k internetu.');
    expect(transport).not.toHaveBeenCalled();
    expect(offline.findAll('button').every(button => (button.element as HTMLButtonElement).disabled)).toBe(true);
    offline.unmount();
  });

  it('aborts and ignores an old account response', async () => {
    let finish!: (value: Response) => void;
    let signal!: AbortSignal;
    const transport = vi.fn((_path: string, init?: RequestInit) => {
      signal = init?.signal as AbortSignal;
      return new Promise<Response>(resolve => { finish = resolve; });
    });
    const wrapper = await render({ transport, storage: storageWithJob() });

    state.setAccount?.({ ...customer, id: 'customer-2' });
    await flushPromises();
    finish(new Response(JSON.stringify(job()), { status: 200, headers: { 'Content-Type': 'application/json' } }));
    await flushPromises();

    expect(signal.aborted).toBe(true);
    expect(wrapper.text()).toContain('Zatím nemáte připravený export.');
    wrapper.unmount();
  });
});

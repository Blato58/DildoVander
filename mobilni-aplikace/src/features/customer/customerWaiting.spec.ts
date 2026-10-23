import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { account, accountDeletionRequested, offlineSession } from '../identity/session';
import CustomerWaiting from './CustomerWaiting.vue';

const sessionMocks = vi.hoisted(() => ({
  account: { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', roles: ['Customer'] },
  deletion: false,
  offline: false,
  transport: vi.fn(),
  setDeletion: (_value: boolean) => undefined,
}));

vi.mock('../identity/session', async () => {
  const vue = await import('vue');
  const deletion = vue.ref(sessionMocks.deletion);
  sessionMocks.setDeletion = value => { deletion.value = value; };
  return {
    account: vue.ref(sessionMocks.account),
    accountDeletionRequested: deletion,
    offlineSession: vue.ref(sessionMocks.offline),
    accountTransport: sessionMocks.transport,
  };
});

const deviceId = 'case-7';
const eventId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const eveningId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const profileId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
const entryId = 'eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee';
const claimId = 'ffffffff-ffff-4fff-8fff-ffffffffffff';
const bootId = '22222222-2222-4222-8222-222222222222';

function snapshot(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 1,
    availability: { state: 'available', reason: 'runtime_ready', fresh: true },
    event: { id: eventId, deviceId, eveningId, state: 'open', revision: 3 },
    entry: null,
    position: null,
    count: 0,
    ...overrides,
  };
}

function waitingSnapshot(overrides: Record<string, unknown> = {}) {
  return snapshot({
    entry: { id: entryId, profileId, sequence: 1, revision: 4, state: 'waiting', claim: null },
    position: 1,
    count: 1,
    ...overrides,
  });
}

function response(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function mountWaiting(props: Partial<{ deviceId: string | null; profileId: string | null; proximity: { bootId: string; challenge: string } | null }> = {}) {
  return mount(CustomerWaiting, {
    props: {
      deviceId,
      profileId,
      proximity: { bootId, challenge: 'fresh-proximity-challenge-012345' },
      ...props,
    },
    global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } },
  });
}

function buttonWithText(wrapper: ReturnType<typeof mount>, text: string) {
  const button = wrapper.findAll('button').find(candidate => candidate.text().includes(text));
  if (!button) throw new Error(`Missing button: ${text}`);
  return button;
}

describe('CustomerWaiting', () => {
  it('restores a durable claim after remount and hands it off only on an explicit fresh click', async () => {
    const claimed = waitingSnapshot({ entry: { id: entryId, profileId, sequence: 1, revision: 5,
      state: 'claimed', claim: { id: claimId, expiresAt: Math.floor(Date.now() / 1000) + 90 } } });
    sessionMocks.transport.mockImplementation(async () => response(claimed));
    const first = mountWaiting();
    await flushPromises();
    expect(first.emitted('claim')).toBeUndefined();
    first.unmount();
    const restored = mountWaiting();
    await flushPromises();
    expect(restored.emitted('claim')).toBeUndefined();
    await buttonWithText(restored, 'Použít rezervované místo').trigger('click');
    expect(restored.emitted('claim')?.[0]?.[0]).toMatchObject({ operationId: claimId, profileId, eventId, entryId });
    expect(sessionMocks.transport.mock.calls.every(call => !call[1]?.method || call[1].method === 'GET')).toBe(true);
    restored.unmount();
  });
  beforeEach(() => {
    account.value = { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'Test', roles: ['Customer'] };
    sessionMocks.setDeletion(false);
    offlineSession.value = false;
    sessionMocks.transport.mockReset();
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    vi.stubGlobal('localStorage', {
      getItem: () => null,
      setItem: () => undefined,
      removeItem: () => undefined,
    });
    vi.stubGlobal('navigator', { onLine: true });
  });

  it('loads status without joining or starting a conversation automatically', async () => {
    sessionMocks.transport.mockResolvedValue(response(snapshot()));
    const wrapper = mountWaiting();
    await flushPromises();

    expect(wrapper.text()).toContain('Kufr je připraven.');
    expect(sessionMocks.transport).toHaveBeenCalledTimes(1);
    expect(sessionMocks.transport.mock.calls[0][0]).toBe(`/app/v1/customer/waiting/${deviceId}`);
    expect(sessionMocks.transport.mock.calls[0][1]?.method).toBe('GET');
    expect(sessionMocks.transport.mock.calls.some(([path]) => String(path).includes('/start-tickets'))).toBe(false);
    expect(wrapper.text()).toContain('Zařadit se do fronty');
    wrapper.unmount();
  });

  it('joins only after an explicit action with a current proximity challenge', async () => {
    sessionMocks.transport
      .mockResolvedValueOnce(response(snapshot()))
      .mockResolvedValueOnce(response(waitingSnapshot()))
      .mockResolvedValueOnce(response(waitingSnapshot()));
    const wrapper = mountWaiting();
    await flushPromises();
    await buttonWithText(wrapper, 'Zařadit se do fronty').trigger('click');
    await flushPromises();

    expect(sessionMocks.transport.mock.calls.map(([path, init]) => [path, init?.method])).toEqual([
      [`/app/v1/customer/waiting/${deviceId}`, 'GET'],
      [`/app/v1/customer/waiting/${deviceId}/join`, 'POST'],
      [`/app/v1/customer/waiting/${deviceId}`, 'GET'],
    ]);
    const body = JSON.parse(String(sessionMocks.transport.mock.calls[1][1]?.body));
    expect(body).toMatchObject({ eventId, profileId, bootId, challenge: 'fresh-proximity-challenge-012345' });
    expect(wrapper.text()).toContain('1. místě');
    expect(sessionMocks.transport.mock.calls.some(([path]) => String(path).includes('/start-tickets'))).toBe(false);
    wrapper.unmount();
  });

  it('requires a fresh available head-of-queue claim and emits only after a confirming GET', async () => {
    const claimed = waitingSnapshot({
      entry: { id: entryId, profileId, sequence: 1, revision: 5, state: 'claimed', claim: { id: claimId, expiresAt: Math.floor(Date.now() / 1000) + 60 } },
    });
    let releaseConfirm!: (value: Response) => void;
    sessionMocks.transport
      .mockResolvedValueOnce(response(waitingSnapshot()))
      .mockResolvedValueOnce(response(claimed))
      .mockImplementationOnce(() => new Promise(resolve => { releaseConfirm = resolve; }));
    const wrapper = mountWaiting();
    await flushPromises();
    await buttonWithText(wrapper, 'Rezervovat své místo').trigger('click');
    await vi.waitFor(() => expect(sessionMocks.transport).toHaveBeenCalledTimes(3));
    await flushPromises();
    expect(wrapper.emitted('claim')).toBeUndefined();
    releaseConfirm(response(claimed));
    await flushPromises();

    const claims = wrapper.emitted('claim');
    expect(claims).toHaveLength(1);
    expect(claims?.[0]?.[0]).toMatchObject({ operationId: claimId, eventId, entryId, profileId });
    expect(sessionMocks.transport.mock.calls.map(([path, init]) => [path, init?.method])).toEqual([
      [`/app/v1/customer/waiting/${deviceId}`, 'GET'],
      [`/app/v1/customer/waiting/${deviceId}/claim`, 'POST'],
      [`/app/v1/customer/waiting/${deviceId}`, 'GET'],
    ]);
    expect(wrapper.text()).toContain('Spuštění rozhovoru potvrďte zvlášť');
    wrapper.unmount();
  });

  it('does not emit a claim when admission is paused before the confirming GET', async () => {
    const claimed = waitingSnapshot({
      entry: { id: entryId, profileId, sequence: 1, revision: 5, state: 'claimed', claim: { id: claimId, expiresAt: Math.floor(Date.now() / 1000) + 60 } },
    });
    sessionMocks.transport
      .mockResolvedValueOnce(response(waitingSnapshot()))
      .mockResolvedValueOnce(response(claimed))
      .mockResolvedValueOnce(response({ ...claimed, availability: { state: 'paused', reason: 'admission_paused', fresh: true } }));
    const wrapper = mountWaiting();
    await flushPromises();
    await buttonWithText(wrapper, 'Rezervovat své místo').trigger('click');
    await flushPromises();
    expect(sessionMocks.transport).toHaveBeenCalledTimes(3);
    expect(wrapper.emitted('claim')).toBeUndefined();
    wrapper.unmount();
  });

  it.each(['open', 'paused'] as const)('allows leave while %s without proximity but never for an accepted entry', async eventState => {
    sessionMocks.transport
      .mockResolvedValueOnce(response(waitingSnapshot({
        event: { id: eventId, deviceId, eveningId, state: eventState, revision: 3 },
        availability: eventState === 'paused'
          ? { state: 'paused', reason: 'admission_paused', fresh: true }
          : { state: 'available', reason: 'runtime_ready', fresh: true },
      })))
      .mockResolvedValueOnce(response(snapshot()))
      .mockResolvedValueOnce(response(snapshot()));
    const wrapper = mountWaiting({ proximity: null });
    await flushPromises();
    const leave = buttonWithText(wrapper, 'Opustit čekací seznam');
    expect((leave.element as HTMLButtonElement).disabled).toBe(false);
    await leave.trigger('click');
    await flushPromises();
    expect(sessionMocks.transport.mock.calls[1][0]).toBe(`/app/v1/customer/waiting/${deviceId}/leave`);
    expect(JSON.parse(String(sessionMocks.transport.mock.calls[1][1]?.body))).not.toHaveProperty('challenge');
    wrapper.unmount();

    sessionMocks.transport.mockReset().mockResolvedValue(response(waitingSnapshot({
      entry: { id: entryId, profileId, sequence: 1, revision: 5, state: 'accepted', claim: null },
      position: null,
    })));
    const accepted = mountWaiting({ proximity: null });
    await flushPromises();
    expect(accepted.text()).toContain('Místo bylo přijato.');
    expect(accepted.text()).toContain('Otevřít rozhovor');
    expect(accepted.text()).not.toContain('Opustit čekací seznam');
    accepted.unmount();
  });

  it('hides customer requests for staff/deleted/offline scopes and aborts on unmount', async () => {
    const staff = { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', roles: ['Customer', 'Operator'] };
    account.value = { ...staff, displayName: 'Test' };
    const denied = mountWaiting();
    await flushPromises();
    expect(sessionMocks.transport).not.toHaveBeenCalled();
    denied.unmount();

    account.value = { id: 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa', displayName: 'Test', roles: ['Customer'] };
    sessionMocks.setDeletion(true);
    expect(accountDeletionRequested.value).toBe(true);
    sessionMocks.transport.mockClear();
    const deleting = mountWaiting();
    await flushPromises();
    expect(deleting.find('.customer-waiting').exists()).toBe(false);
    expect(sessionMocks.transport).not.toHaveBeenCalled();
    deleting.unmount();

    sessionMocks.setDeletion(false);
    let signal: AbortSignal | undefined;
    sessionMocks.transport.mockImplementation((_path, init) => {
      signal = init?.signal as AbortSignal;
      return new Promise(() => undefined);
    });
    const mounted = mountWaiting();
    mounted.unmount();
    expect(signal?.aborted).toBe(true);
  });

  it('clears old account data during account switch and does not render stale completions', async () => {
    let releaseOld!: (value: Response) => void;
    sessionMocks.transport
      .mockImplementationOnce(() => new Promise(resolve => { releaseOld = resolve; }))
      .mockResolvedValueOnce(response(snapshot({ availability: { state: 'busy', reason: 'lease_occupied', fresh: true } })));
    const wrapper = mountWaiting();
    await flushPromises();
    account.value = { id: '99999999-9999-4999-8999-999999999999', displayName: 'Other', roles: ['Customer'] };
    await flushPromises();
    releaseOld(response(snapshot()));
    await flushPromises();
    expect(wrapper.text()).toContain('Kufr právě obsluhuje jiný rozhovor.');
    expect(wrapper.text()).not.toContain('Předchozí účet');
    wrapper.unmount();
  });

  it('waits offline and refreshes when the connection returns; backgrounding aborts an active read', async () => {
    vi.stubGlobal('navigator', { onLine: false });
    sessionMocks.transport.mockImplementation((_path, init) => {
      return new Promise((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
      });
    });
    const wrapper = mountWaiting();
    await flushPromises();
    expect(sessionMocks.transport).not.toHaveBeenCalled();

    vi.stubGlobal('navigator', { onLine: true });
    window.dispatchEvent(new Event('online'));
    await flushPromises();
    expect(sessionMocks.transport).toHaveBeenCalledTimes(1);
    const signal = sessionMocks.transport.mock.calls[0][1]?.signal as AbortSignal;

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
    expect(signal.aborted).toBe(true);
    wrapper.unmount();
  });

  it('aborts an in-flight explicit command when the signed-in account changes', async () => {
    let commandSignal: AbortSignal | undefined;
    sessionMocks.transport.mockImplementation((path, init) => {
      if (init?.method === 'GET') return Promise.resolve(response(waitingSnapshot()));
      commandSignal = init?.signal as AbortSignal;
      return new Promise((_resolve, reject) => {
        init?.signal?.addEventListener('abort', () => reject(new DOMException('Aborted', 'AbortError')), { once: true });
      });
    });
    const wrapper = mountWaiting({ proximity: null });
    await flushPromises();
    await buttonWithText(wrapper, 'Opustit čekací seznam').trigger('click');
    await flushPromises();
    expect(commandSignal?.aborted).toBe(false);

    account.value = { id: '99999999-9999-4999-8999-999999999999', displayName: 'Other', roles: ['Customer'] };
    expect(commandSignal?.aborted).toBe(true);
    wrapper.unmount();
  });
});

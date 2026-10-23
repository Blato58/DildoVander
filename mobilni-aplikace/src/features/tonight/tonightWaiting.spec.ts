import { flushPromises, mount } from '@vue/test-utils';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { account, accountDeletionRequested, offlineSession, signingOut } from '../identity/session';
import TonightWaiting from './TonightWaiting.vue';

const sessionMocks = vi.hoisted(() => ({
  account: { id: '11111111-1111-4111-8111-111111111111', displayName: 'Staff', roles: ['Operator'] },
  deletion: false,
  offline: false,
  signingOut: false,
  transport: vi.fn(),
  setDeletion: (_value: boolean) => undefined,
  setOffline: (_value: boolean) => undefined,
  setSigningOut: (_value: boolean) => undefined,
}));

vi.mock('../identity/session', async () => {
  const vue = await import('vue');
  const deletion = vue.ref(sessionMocks.deletion);
  const offline = vue.ref(sessionMocks.offline);
  const signingOutState = vue.ref(sessionMocks.signingOut);
  sessionMocks.setDeletion = value => { deletion.value = value; };
  sessionMocks.setOffline = value => { offline.value = value; };
  sessionMocks.setSigningOut = value => { signingOutState.value = value; };
  return {
    account: vue.ref(sessionMocks.account),
    accountDeletionRequested: deletion,
    offlineSession: offline,
    signingOut: signingOutState,
    accountTransport: sessionMocks.transport,
  };
});

const deviceId = 'case-operator';
const eventId = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa';
const eveningId = 'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb';
const entryId = 'cccccccc-cccc-4ccc-8ccc-cccccccccccc';
const originalOnline = Object.getOwnPropertyDescriptor(window.navigator, 'onLine');
const originalLocalStorage = Object.getOwnPropertyDescriptor(window, 'localStorage');

function snapshot(overrides: Record<string, unknown> = {}) {
  return {
    schemaVersion: 1,
    availability: { state: 'available', reason: 'runtime_ready', fresh: true },
    event: { id: eventId, deviceId, eveningId, state: 'open', revision: 3 },
    head: { entryId, revision: 2, state: 'waiting' },
    count: 2,
    ...overrides,
  };
}

function response(value: unknown, status = 200): Response {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } });
}

function mountWaiting(props: Partial<{ deviceId: string | null; authorized: boolean }> = {}) {
  return mount(TonightWaiting, {
    props: { deviceId, authorized: true, ...props },
  });
}

function button(wrapper: ReturnType<typeof mount>, label: string) {
  const found = wrapper.findAll('button').find(candidate => candidate.text().includes(label));
  if (!found) throw new Error(`Missing button: ${label}`);
  return found;
}

describe('TonightWaiting', () => {
  beforeEach(() => {
    account.value = { ...sessionMocks.account, roles: ['Operator'] };
    sessionMocks.setDeletion(false);
    sessionMocks.setOffline(false);
    sessionMocks.setSigningOut(false);
    sessionMocks.transport.mockReset();
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    Object.defineProperty(window.navigator, 'onLine', { configurable: true, value: true });
    const values = new Map<string, string>();
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => { values.set(key, value); },
        removeItem: (key: string) => { values.delete(key); },
      },
    });
  });

  it('loads only for an authorized signed-in operator and never mutates on mount', async () => {
    sessionMocks.transport.mockImplementation(async () => response(snapshot()));
    const denied = mountWaiting({ authorized: false });
    await flushPromises();
    expect(sessionMocks.transport).not.toHaveBeenCalled();
    denied.unmount();

    const wrapper = mountWaiting();
    await flushPromises();
    expect(sessionMocks.transport).toHaveBeenCalledTimes(1);
    expect(sessionMocks.transport.mock.calls[0][0]).toBe(`/app/v1/admin/devices/${deviceId}/waiting`);
    expect(sessionMocks.transport.mock.calls[0][1]?.method).toBe('GET');
    expect(sessionMocks.transport.mock.calls.some(call => call[1]?.method === 'POST')).toBe(false);
    expect(wrapper.text()).toContain('Otevřená');
    expect(wrapper.text()).toContain('2');
    expect(wrapper.text()).not.toContain(entryId);
    wrapper.unmount();
  });

  it('offers explicit open and resume commands with distinct server contracts', async () => {
    const replacementId = 'dddddddd-dddd-4ddd-8ddd-dddddddddddd';
    sessionMocks.transport
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'closed' } })))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, id: replacementId, revision: 1 } })))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, id: replacementId, revision: 1 } })));
    const opening = mountWaiting();
    await flushPromises();
    await button(opening, 'Otevřít seznam pro aktuální večer').trigger('click');
    await flushPromises();
    const openPost = sessionMocks.transport.mock.calls.find(call => call[1]?.method === 'POST');
    expect(openPost?.[0]).toBe(`/app/v1/admin/devices/${deviceId}/waiting/open`);
    expect(JSON.parse(String(openPost?.[1]?.body))).toMatchObject({
      schemaVersion: 1, previousEventId: eventId, expectedRevision: 3,
    });
    opening.unmount();

    sessionMocks.transport.mockReset();
    sessionMocks.transport
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'paused' } })))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'open', revision: 4 } })))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'open', revision: 4 } })));
    const resuming = mountWaiting();
    await flushPromises();
    await button(resuming, 'Obnovit přijímání').trigger('click');
    await flushPromises();
    const resumePost = sessionMocks.transport.mock.calls.find(call => call[1]?.method === 'POST');
    expect(resumePost?.[0]).toBe(`/app/v1/admin/devices/${deviceId}/waiting/state`);
    expect(JSON.parse(String(resumePost?.[1]?.body))).toMatchObject({
      schemaVersion: 1, eventId, expectedRevision: 3, state: 'open',
    });
    resuming.unmount();
  });

  it('does not read while account deletion is pending', async () => {
    sessionMocks.setDeletion(true);
    const wrapper = mountWaiting();
    await flushPromises();
    expect(sessionMocks.transport).not.toHaveBeenCalled();
    expect(wrapper.text()).toContain('vyžadují přihlášení');
    wrapper.unmount();
  });

  it('uses the verified event revision for explicit pause and then refreshes the projection', async () => {
    sessionMocks.transport
      .mockResolvedValueOnce(response(snapshot()))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'paused', revision: 4 } })))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'paused', revision: 4 } })));
    const wrapper = mountWaiting();
    await flushPromises();
    expect(sessionMocks.transport).toHaveBeenCalledTimes(1);
    await button(wrapper, 'Pozastavit přijímání').trigger('click');
    await flushPromises();

    const mutation = sessionMocks.transport.mock.calls.find(call => call[1]?.method === 'POST');
    expect(mutation?.[0]).toBe(`/app/v1/admin/devices/${deviceId}/waiting/state`);
    expect(JSON.parse(String(mutation?.[1]?.body))).toMatchObject({
      schemaVersion: 1, eventId, expectedRevision: 3, state: 'paused',
    });
    expect(sessionMocks.transport).toHaveBeenCalledTimes(3);
    expect(wrapper.text()).toContain('Pozastavená');
    expect(wrapper.findAll('button').some(item => item.text() === 'Obnovit přijímání')).toBe(true);
    wrapper.unmount();
  });

  it('suppresses mutations while offline, then requires a fresh read after reconnect', async () => {
    sessionMocks.transport.mockImplementation(async () => response(snapshot()));
    const wrapper = mountWaiting();
    await flushPromises();
    expect(wrapper.findAll('button').some(item => item.text() === 'Pozastavit přijímání')).toBe(true);

    Object.defineProperty(window.navigator, 'onLine', { configurable: true, value: false });
    window.dispatchEvent(new Event('offline'));
    await flushPromises();
    expect(wrapper.findAll('button').some(item => item.text() === 'Pozastavit přijímání')).toBe(false);
    expect(wrapper.text()).toContain('poslední známý stav');
    expect(sessionMocks.transport.mock.calls.filter(call => call[1]?.method === 'POST')).toHaveLength(0);

    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect(wrapper.findAll('button').some(item => item.text() === 'Pozastavit přijímání')).toBe(false);

    Object.defineProperty(window.navigator, 'onLine', { configurable: true, value: true });
    window.dispatchEvent(new Event('online'));
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
    document.dispatchEvent(new Event('visibilitychange'));
    await flushPromises();
    expect(sessionMocks.transport.mock.calls.filter(call => call[1]?.method === 'GET').length).toBeGreaterThan(1);
    expect(wrapper.findAll('button').some(item => item.text() === 'Pozastavit přijímání')).toBe(true);
    wrapper.unmount();
  });

  it('invalidates late account responses and clears the previous account projection', async () => {
    let resolveFirst!: (value: Response) => void;
    sessionMocks.transport.mockImplementationOnce(() => new Promise(resolve => { resolveFirst = resolve; }))
      .mockImplementation(async () => response(snapshot({ availability: { state: 'offline', reason: 'runtime_offline', fresh: true } })));
    const wrapper = mountWaiting();
    await flushPromises();
    expect(sessionMocks.transport).toHaveBeenCalledTimes(1);

    account.value = { id: '22222222-2222-4222-8222-222222222222', displayName: 'Other', roles: ['Operator'] };
    await flushPromises();
    expect(sessionMocks.transport.mock.calls.length).toBeGreaterThan(1);
    resolveFirst(response(snapshot()));
    await flushPromises();
    expect(wrapper.text()).toContain('Kufr hlásí, že je offline.');
    expect(wrapper.text()).not.toContain('Kufr hlásí připravenost.');
    wrapper.unmount();
  });

  it('does not retry an uncertain mutation automatically and offers the identical request after a fresh GET', async () => {
    const values = new Map<string, string>();
    Object.defineProperty(window, 'localStorage', {
      configurable: true,
      value: {
        getItem: (key: string) => values.get(key) ?? null,
        setItem: (key: string, value: string) => { values.set(key, value); },
        removeItem: (key: string) => { values.delete(key); },
      },
    });
    sessionMocks.transport
      .mockResolvedValueOnce(response(snapshot()))
      .mockRejectedValueOnce(new Error('private transport failure'))
      .mockResolvedValueOnce(response(snapshot()))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'paused', revision: 4 } })))
      .mockResolvedValueOnce(response(snapshot({ event: { ...snapshot().event, state: 'paused', revision: 4 } })));
    const wrapper = mountWaiting();
    await flushPromises();
    await button(wrapper, 'Pozastavit přijímání').trigger('click');
    await flushPromises();
    expect(sessionMocks.transport.mock.calls.filter(call => call[1]?.method === 'POST')).toHaveLength(1);
    expect(wrapper.text()).not.toContain('private transport failure');
    expect(wrapper.findAll('button').some(item => item.text() === 'Opakovat stejný požadavek')).toBe(true);

    await button(wrapper, 'Opakovat stejný požadavek').trigger('click');
    await flushPromises();
    const posts = sessionMocks.transport.mock.calls.filter(call => call[1]?.method === 'POST');
    expect(posts).toHaveLength(2);
    expect(JSON.parse(String(posts[1][1]?.body))).toEqual(JSON.parse(String(posts[0][1]?.body)));
    expect(wrapper.text()).toContain('Pozastavená');
    wrapper.unmount();
  });
});

afterEach(() => {
  if (originalOnline) Object.defineProperty(window.navigator, 'onLine', originalOnline);
  if (originalLocalStorage) Object.defineProperty(window, 'localStorage', originalLocalStorage);
  vi.unstubAllGlobals();
});

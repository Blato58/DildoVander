import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { defineComponent } from 'vue';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { createMemoryHistory, createRouter } from 'vue-router';
const transport = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref({ id: 'tester', roles: ['Tester'] }), accountOrigin: ref(''), accountTransport: transport };
});
vi.mock('../persona-lab/voice', () => ({ LabVoice: class { dispose() {} setOutputEnabled() {} } }));
import { useTestingSessions } from './useTestingSessions';
import { TestingError } from './client';
import type { TestSessionDetail } from './contracts';

let wrapper: VueWrapper | undefined;
let test: ReturnType<typeof useTestingSessions>;
let revision: number;
const detail = () => ({ session: { id: 'test', status: 'open', revision, inputMode: 'text', engine: 'gpt-live-1' }, conversation: { id: 'lab', turns: [] } }) as unknown as TestSessionDetail;
async function render() {
  const host = defineComponent({ setup() { test = useTestingSessions(); return {}; }, template: '<div />' });
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/testing-sessions', component: host }] });
  await router.push('/testing-sessions?session=test'); await router.isReady();
  wrapper = mount(host, { global: { plugins: [router] } }); await flushPromises();
}
beforeEach(() => {
  revision = 1;
  transport.mockReset();
  transport.mockImplementation((path: string) => Promise.resolve(Response.json(path.endsWith('/test') ? detail() : [])));
});
afterEach(() => { wrapper?.unmount(); });

describe('testing status recovery', () => {
  it('keeps successful reads during a partial failure, correlates the error and clears it on recovery without replaying commands', async () => {
    await render(); revision = 2;
    const healthy = transport.getMockImplementation()!;
    transport.mockImplementation((path: string) => path === '/app/v1/testing-sessions'
      ? Promise.resolve(Response.json({ code: 'testing_request_failed', traceId: '01234567-89ab-4cde-8f01-23456789abcd' }, { status: 503 })) : healthy(path));
    await test.refresh();
    expect(test.selected.value?.session.revision).toBe(2);
    expect(test.stale.value).toBe(true);
    expect(test.errorRequestId.value).toBe('01234567-89ab-4cde-8f01-23456789abcd');
    transport.mockImplementation(healthy);
    await test.refresh();
    expect(test.stale.value).toBe(false); expect(test.error.value).toBe(''); expect(test.errorRequestId.value).toBe('');
    expect(transport.mock.calls.every(([, init]) => init.method === 'GET')).toBe(true);
  });
  it('preserves an unconfirmed action after a successful read', async () => {
    await render();
    await test.run(async () => { throw new TestingError('testing_action_failed', 502, '01234567-89ab-4cde-8f01-23456789abcd'); });
    await test.refresh();
    expect(test.error.value).toBe('testing_action_failed');
    expect(test.errorRequestId.value).toBe('01234567-89ab-4cde-8f01-23456789abcd');
  });
  it('fences an older polling snapshot when a mutation starts', async () => {
    await render();
    const old = detail();
    let release!: (response: Response) => void;
    const healthy = transport.getMockImplementation()!;
    transport.mockImplementation((path: string) => path.endsWith('/test') ? new Promise<Response>(resolve => { release = resolve; }) : healthy(path));
    const pending = test.refresh();
    await test.run(async () => { revision = 3; test.selected.value = detail(); });
    release(Response.json(old)); await pending;
    expect(test.selected.value?.session.revision).toBe(3);
  });
  it('defers visibility reads during a pending mutation and reconciles from a fresh command read', async () => {
    await render();
    let finishCommand!: () => void;
    const command = test.run(async () => {
      await new Promise<void>(resolve => { finishCommand = resolve; });
      revision = 4; test.selected.value = detail();
      await test.refreshAfterCommand();
    });
    const before = transport.mock.calls.length;
    await test.refresh();
    expect(transport.mock.calls).toHaveLength(before);
    finishCommand(); await command;
    expect(transport.mock.calls.length).toBeGreaterThan(before);
    expect(test.selected.value?.session.revision).toBe(4);
  });
  it('clears the previous server trace when a local consent error replaces it', async () => {
    await render();
    await test.run(async () => { throw new TestingError('testing_request_failed', 503, '01234567-89ab-4cde-8f01-23456789abcd'); });
    expect(test.errorRequestId.value).not.toBe('');
    await test.saveCharacter({ name: 'Name', world: 'World' });
    expect(test.error.value).toContain('potvrďte souhlas');
    expect(test.errorRequestId.value).toBe('');
  });
  it('acknowledges a saved character when a subsequent status read fails', async () => {
    await render(); test.consent.value = true;
    const saved = { id: 'saved', profile: { revision: 1, status: 'queued' } };
    transport.mockImplementation((path: string, init: RequestInit) => {
      if (path.endsWith('/characters')) return Promise.resolve(Response.json(init.method === 'POST' ? saved : [saved]));
      if (path.endsWith('/character')) return Promise.resolve(Response.json(detail()));
      return Promise.resolve(Response.json({ code: 'testing_request_failed' }, { status: 503 }));
    });
    expect(await test.saveCharacter({ name: 'Name', world: 'World' })).toBe(true);
    expect(test.characters.value[0].id).toBe('saved');
    expect(test.error.value).toBe('testing_request_failed');
    expect(test.notice.value).toContain('Postava uložena');
    expect(transport.mock.calls.filter(([, init]) => init.method === 'POST')).toHaveLength(2);
  });
});

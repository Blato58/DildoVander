import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import PersonaLabView from './PersonaLabView.vue';
import { sanitizeSystemDiagnostics, systemDiagnosticsBundle } from './systemDiagnostics';

const transport = vi.hoisted(() => vi.fn());
const identity = await vi.hoisted(async () => { const { ref } = await import('vue'); return { account: ref<{ id: string; roles: string[] } | null>({ id: 'admin', roles: ['Administrator'] }) }; });
vi.mock('../identity/session', async () => { const { ref } = await import('vue'); return {
  account: identity.account, accountOrigin: ref('https://example.test'), accountTransport: transport,
  hasRole: (role: string) => identity.account.value?.roles.includes(role) ?? false,
}; });
vi.mock('../suitcase/deviceSession', async () => { const { ref } = await import('vue'); return { deviceSession: {
  native: false, snapshot: ref(null), savedDevice: ref({ paired: false }), labConnectionEpoch: () => 1,
} }; });
vi.mock('../suitcase/deviceAssociation', async () => { const { ref } = await import('vue'); return { deviceAssociation: { canOperate: ref(false) } }; });
vi.mock('./voice', () => ({ LabVoice: class { dispose() {} } }));

function fixture() {
  return { storage: { sessions: 3, revisions: 2, sessionsTruncated: true, recentSessions: [{
    sessionId: 'system-session', engine: 'gpt-live-1', personaRevisionId: 'revision', platform: 'ios', status: 'ended', testMode: 'hardware',
    createdAt: '2026-10-20T12:00:00Z', endedAt: '2026-10-20T12:01:00Z', eventsTruncated: true,
    events: [{ id: 'event', type: 'provider_tokens', code: 'observed', createdAt: '2026-10-20T12:00:00Z', inputTokens: 0, outputTokens: null,
      text: 'PRIVATE_TRANSCRIPT', comment: 'PRIVATE_COMMENT', testContext: 'PRIVATE_TOKEN', testContextId: 'PRIVATE_DIGEST' }],
    ownerId: 'PRIVATE_OWNER', config: { instructions: 'PRIVATE_INSTRUCTIONS' }, memory: 'PRIVATE_MEMORY', evaluation: 'PRIVATE_EVALUATION',
  }] }, activeConnections: 1, liveAvailable: true, realtimeAvailable: false, simulationOnly: false, hardwareContext: true,
    futureContent: 'PRIVATE_FUTURE' };
}
const wrappers: ReturnType<typeof mount>[] = [];
beforeEach(() => {
  identity.account.value = { id: 'admin', roles: ['Administrator'] };
  transport.mockReset().mockImplementation(async (path: string) => {
    const value = path.endsWith('/diagnostics') ? fixture()
      : path.endsWith('/catalog') ? { capabilities: { live: true, realtime: true, voice: true, text: true, simulationOnly: false, defaultMode: 'simulation', hardwareContext: true },
        activeRevisionId: 'revision', versions: [{ id: 'revision', name: 'Baseline', engine: 'gpt-live-1', voice: 'cedar', instructions: 'Synthetic', createdAt: '', revision: 1 }], scenarios: [], memoryFixtures: [] }
        : path.endsWith('/analytics') ? { sessions: 0, evaluations: 0, metrics: [], comparisons: [] } : [];
    return new Response(JSON.stringify(value));
  });
});
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.restoreAllMocks(); });
async function render() { const wrapper = mount(PersonaLabView); wrappers.push(wrapper); await flushPromises(); return wrapper; }

describe('bounded system diagnostic projection', () => {
  it('exports an explicit whitelist without future content, account identities or capabilities', () => {
    const sanitized = sanitizeSystemDiagnostics(fixture());
    const bundle = systemDiagnosticsBundle(sanitized);
    expect(JSON.stringify(bundle)).not.toContain('PRIVATE_');
    expect(sanitized.storage.recentSessions[0]!.events[0]).toMatchObject({ inputTokens: 0, outputTokens: null });
    expect(bundle).toMatchObject({ schemaVersion: 1, limits: { sessions: 20, eventsPerSession: 20 }, activeConnections: 1 });
    // Re-project at export too, even if future code accidentally extends state.
    expect(JSON.stringify(systemDiagnosticsBundle(Object.assign(sanitized, { transcript: 'PRIVATE_ADDED_LATER' })))).not.toContain('PRIVATE_');
  });
  it('bounds sessions/events, preserves last-event chronology and marks local truncation', () => {
    const source = fixture(); source.storage.sessionsTruncated = false;
    const session = source.storage.recentSessions[0]!; session.eventsTruncated = false;
    source.storage.recentSessions = Array.from({ length: 25 }, (_, index) => ({ ...session, sessionId: `s${index}`,
      events: Array.from({ length: 24 }, (_, event) => ({ ...session.events[0]!, id: `e${event}` })) }));
    const result = sanitizeSystemDiagnostics(source);
    expect(result.storage.recentSessions).toHaveLength(20); expect(result.storage.sessionsTruncated).toBe(true);
    expect(result.storage.recentSessions[0]!.events.map(event => event.id)).toEqual(Array.from({ length: 20 }, (_, index) => `e${index + 4}`));
    expect(result.storage.recentSessions[0]!.eventsTruncated).toBe(true);
    expect(sanitizeSystemDiagnostics({ activeConnections: -1 }).activeConnections).toBeNull();
  });
});
describe('administrator diagnostic journey', () => {
  it('accepts the actual mixed-capability catalog and loads diagnostics only after an admin button click', async () => {
    const wrapper = await render();
    expect(wrapper.find('#lab-revision option[value="revision"]').exists()).toBe(true);
    expect(transport.mock.calls.some(([path]) => path.endsWith('/diagnostics'))).toBe(false);
    await wrapper.get('.lab-system-diagnostics > summary').trigger('click');
    expect(transport.mock.calls.some(([path]) => path.endsWith('/diagnostics'))).toBe(false);
    await wrapper.findAll('button').find(button => button.text() === 'Načíst systémovou diagnostiku')!.trigger('click'); await flushPromises();
    expect(transport.mock.calls.filter(([path]) => path.endsWith('/diagnostics'))).toHaveLength(1);
    expect(wrapper.text()).toContain('system-session'); expect(wrapper.text()).toContain('Seznam sezení je zkrácený');
    expect(wrapper.text()).not.toContain('PRIVATE_');
    identity.account.value = null; await flushPromises(); expect(wrapper.text()).not.toContain('system-session');
    identity.account.value = { id: 'next-admin', roles: ['Administrator'] }; await flushPromises();
    expect(wrapper.text()).not.toContain('system-session');
    expect(transport.mock.calls.filter(([path]) => path.endsWith('/diagnostics'))).toHaveLength(1);
  });
  it('keeps administrator diagnostics controls unavailable to testers', async () => {
    identity.account.value = { id: 'tester', roles: ['Tester'] };
    const wrapper = await render(); expect(wrapper.find('.lab-system-diagnostics').exists()).toBe(false);
    expect(transport.mock.calls.some(([path]) => path.endsWith('/diagnostics'))).toBe(false);
  });
  it('ignores a late diagnostic result after the account changes', async () => {
    const fallback = transport.getMockImplementation()!; let finish!: (response: Response) => void;
    transport.mockImplementation((path: string, init: RequestInit) => path.endsWith('/diagnostics')
      ? new Promise<Response>(resolve => { finish = resolve; }) : fallback(path, init));
    const wrapper = await render();
    await wrapper.findAll('button').find(button => button.text() === 'Načíst systémovou diagnostiku')!.trigger('click');
    identity.account.value = { id: 'next-admin', roles: ['Administrator'] }; await flushPromises();
    finish(new Response(JSON.stringify(fixture()))); await flushPromises();
    expect(wrapper.text()).not.toContain('system-session');
  });
});

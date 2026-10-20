import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import { chooseComparison, compareSessions, comparisonObservations } from './comparison';
import type { LabSession } from './contracts';
import PersonaLabView from './PersonaLabView.vue';

const transport = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => { const { ref } = await import('vue'); return {
  account: ref({ id: 'owner', roles: ['Tester'] }), accountOrigin: ref('https://example.test'), accountTransport: transport, hasRole: () => false,
}; });
vi.mock('../suitcase/deviceSession', async () => { const { ref } = await import('vue'); return { deviceSession: {
  native: false, snapshot: ref(null), savedDevice: ref({ paired: false }), labConnectionEpoch: () => 1,
} }; });
vi.mock('../suitcase/deviceAssociation', async () => { const { ref } = await import('vue'); return { deviceAssociation: { canOperate: ref(false) } }; });
vi.mock('./voice', () => ({ LabVoice: class { dispose() {} } }));

function sample(id: string, overall = 4): LabSession {
  return {
    id, ownerId: 'owner', label: id, createdAt: '2026-10-20T10:00:00Z', endedAt: '2026-10-20T10:00:10Z', status: 'ended',
    engine: 'gpt-live-1', platform: 'web', scenarioId: 'intro', fixtureId: 'empty', recordContent: false,
    config: { id: 'revision', parentId: null, name: 'Test', instructions: 'Synthetic', engine: 'gpt-live-1', voice: 'cedar', status: 'baseline', createdAt: '', publishedAt: null, revision: 1 },
    memorySnapshot: { id: 'empty', name: 'Empty', description: '', current: '', evening: '', returning: '' },
    scenarioSnapshot: { id: 'intro', name: 'Intro', description: '', openingText: 'Synthetic input', scriptSha256: 'a'.repeat(64), script: [{ index: 1, input: 'Synthetic input', checks: [] }] },
    turns: [{ id: `${id}-answer`, role: 'assistant', text: '', createdAt: '', latencyMs: 100 }], events: [],
    evaluation: { scores: { overall, humor: 4, personaConsistency: 4, naturalness: 4, responsiveness: 4, memoryAccuracy: 4, technicalReliability: 4 }, comment: '', turnComments: [] },
  };
}
describe('descriptive A/B evidence', () => {
  it('prevents self-comparison from either selection direction', () => {
    expect(chooseComparison({ left: 'a', right: 'b' }, 'left', 'b')).toEqual({ left: 'b', right: '' });
    expect(chooseComparison({ left: 'a', right: 'b' }, 'right', 'a')).toEqual({ left: '', right: 'a' });
  });
  it('describes human preference only for matched completed scripted observations', () => {
    const a = sample('a'); const b = sample('b', 3); b.engine = 'gpt-realtime-2.1';
    const result = compareSessions(a, b, new Set(['a', 'b']));
    expect(result.reasons).toEqual([]); expect(result.recommendation).toContain('předběžně A'); expect(result.modelChanged).toBe(true);
    expect(compareSessions(a, sample('b'), new Set(['a', 'b'])).recommendation).toContain('shodné');
  });
  it('refuses a recommendation for changed script/memory/mode/platform or missing human evidence', () => {
    const a = sample('a'); const b = sample('b');
    b.scenarioSnapshot!.scriptSha256 = 'b'.repeat(64); b.memorySnapshot!.evening = 'Different'; b.platform = 'ios';
    b.hardware = { deviceId: 'case', bootId: 'boot', expectedRevision: 1, conversationId: null, status: 'ended', expiresAt: '' }; b.evaluation = null;
    const result = compareSessions(a, b, new Set(['a', 'b']));
    expect(result.reasons).toHaveLength(5); expect(result.recommendation).toContain('Nedostatek');
    expect(compareSessions(a, sample('b'), new Set()).reasons.join(' ')).toContain('ruční vstupy');
  });
  it('does not treat partial script completion or a failed session as comparable', () => {
    const b = sample('b'); b.turns = []; b.status = 'failed';
    expect(compareSessions(sample('a'), b, new Set(['a', 'b'])).reasons).toHaveLength(2);
  });
  it('keeps zero, unknown token counts and observed failures distinct', () => {
    const a = sample('a'); a.events = [
      { id: 'tokens', type: 'provider_tokens', code: 'observed', createdAt: '', inputTokens: 0, outputTokens: null },
      { id: 'failure', type: 'provider_failure', code: 'timeout', createdAt: '' },
      { id: 'cancel', type: 'provider_failure', code: 'user_cancelled', createdAt: '' },
    ];
    a.turns.push({ ...a.turns[0]!, id: 'second', latencyMs: 300 });
    expect(comparisonObservations(a)).toMatchObject({ meanMs: 200, p95Ms: 300, latencyCount: 2, failures: 1, tokens: { input: 0, output: null } });
  });
  it('distinguishes missing cost from an observed zero and deduplicates only valid price increments', () => {
    const a = sample('a');
    expect(comparisonObservations(a).cost).toEqual({ estimatedUsd: null, samples: 0 });
    const cost = { id: 'zero', type: 'provider_cost', code: 'micro_usd', createdAt: '', costMicroUsd: 0 };
    a.events = [cost];
    expect(comparisonObservations(a).cost).toEqual({ estimatedUsd: 0, samples: 1 });
    a.events = [cost, { ...cost }, { ...cost, id: 'one', costMicroUsd: 1 }, { ...cost, id: 'one', costMicroUsd: 1 },
      { ...cost, id: 'negative', costMicroUsd: -1 }, { ...cost, id: 'missing', costMicroUsd: null },
      { ...cost, id: 'fraction', costMicroUsd: .5 }, { ...cost, id: 'infinite', costMicroUsd: Infinity },
      { ...cost, id: 'wrong-type', type: 'provider_turn', costMicroUsd: 1000 },
      { ...cost, id: 'wrong-code', code: 'estimated', costMicroUsd: 1000 }];
    expect(comparisonObservations(a).cost).toEqual({ estimatedUsd: .000001, samples: 2 });
  });
});

const wrappers: ReturnType<typeof mount>[] = [];
beforeEach(() => {
  transport.mockReset().mockImplementation(async (path: string) => {
    const session = sample('suite-session');
    const value = path.endsWith('/catalog') ? { capabilities: { live: true, realtime: true, simulationOnly: true }, versions: [session.config], scenarios: [session.scenarioSnapshot], memoryFixtures: [session.memorySnapshot] }
      : path.endsWith('/analytics') ? { sessions: 0, evaluations: 0, comparisons: [], metrics: [] }
        : path.endsWith('/sessions') ? []
          : path.endsWith('/suites') ? [{ id: 'suite', ownerId: 'owner', engine: 'gpt-live-1', status: 'completed', createdAt: '', results: [{ scenarioId: 'intro', sessionId: 'suite-session', status: 'completed', assertions: [] }] }]
            : session;
    return new Response(JSON.stringify(value));
  });
});
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); });
describe('owned suite result navigation', () => {
  async function openResult() {
    const wrapper = mount(PersonaLabView); wrappers.push(wrapper); await flushPromises();
    await wrapper.findAll('button').find(button => button.text() === 'Otevřít sezení a hodnotit')!.trigger('click'); await flushPromises();
    return wrapper;
  }
  it('fetches the actual owned session even when absent from the stale list and opens its exact evaluation', async () => {
    const wrapper = await openResult();
    expect(transport).toHaveBeenCalledWith('/app/v1/persona-lab/sessions/suite-session', expect.objectContaining({ method: 'GET' }));
    expect(wrapper.get('.lab-evaluation').isVisible()).toBe(true);
    expect(wrapper.find('#lab-comment-suite-session-answer').exists()).toBe(true);
  });
  it.each([404, 403])('reports unavailable or unauthorized results (%s) without showing an unrelated evaluation', async status => {
    const baseline = transport.getMockImplementation()!;
    transport.mockImplementation((path: string, init: RequestInit) => path.endsWith('/sessions/suite-session')
      ? Promise.resolve(new Response(JSON.stringify({ code: status === 404 ? 'lab_session_not_found' : 'forbidden' }), { status })) : baseline(path, init));
    const wrapper = await openResult();
    expect(wrapper.find('.lab-evaluation').exists()).toBe(false);
    expect(wrapper.text()).toContain(status === 404 ? 'už není dostupné' : 'oprávnění');
  });
});

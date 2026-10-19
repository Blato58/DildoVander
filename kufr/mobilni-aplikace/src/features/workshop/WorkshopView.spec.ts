import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import WorkshopView from './WorkshopView.vue';

const identity = vi.hoisted(() => ({ accountRequest: vi.fn(), account: { value: { id: 'owner' } as { id: string } | null } }));
vi.mock('../identity/session', async () => { const { ref } = await import('vue'); identity.account = ref({ id: 'owner' }); return identity; });
vi.mock('./engine.mjs', async importOriginal => ({
  ...await importOriginal<typeof import('./engine.mjs')>(),
  createViewerEngine: () => ({ update: vi.fn(), dispose: vi.fn() }),
}));
const bundle = {
  manifest: { modelId: 'synthetic-view-test', revision: 'test', bundleHash: 'a'.repeat(64), provenance: { kind: 'synthetic-fixture', cadVerification: 'not-verified', sourceRevision: 'test' } },
  model: { meshes: [], routes: [], assemblyGuide: { steps: [] }, mechanism: { travel_degrees: 180 }, twinManifest: null },
};
const instances = [
  { instanceId: 'device-other', mode: 'live' },
  { instanceId: 'device-exact', mode: 'simulation', canControl: true },
  { instanceId: 'device-exact', mode: 'live', canControl: false },
];
const wrappers: ReturnType<typeof mount>[] = [];
async function open(query: string) {
  history.replaceState({}, '', `/workshop${query}`);
  const wrapper = mount(WorkshopView, { props: { bundle }, global: { stubs: { SimulationControls: true, MediaPanel: true, EffectPreview: true, ModelLabels: true, ModelDownloads: true } } });
  wrappers.push(wrapper); await flushPromises(); return wrapper;
}
async function list(wrapper: ReturnType<typeof mount>) {
  await wrapper.findAll('button').find(button => button.text() === 'Načíst dostupné zdroje')!.trigger('click');
  await flushPromises();
}
beforeEach(() => { vi.clearAllMocks(); identity.account.value = { id: 'owner' }; identity.accountRequest.mockResolvedValue({ instances }); vi.stubGlobal('fetch', vi.fn()); });
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); vi.useRealTimers(); vi.unstubAllGlobals(); history.replaceState({}, '', '/'); });

describe('Workshop contextual live source', () => {
  it('waits for explicit authorized listing, then selects only the exact live instance without connecting', async () => {
    const wrapper = await open('?mode=live&source=device-exact');
    expect(wrapper.get<HTMLSelectElement>('#observer-mode').element.value).toBe('live');
    expect(wrapper.text()).toContain('Požadovaný živý kufr: device-exact');
    expect(identity.accountRequest).not.toHaveBeenCalled(); expect(fetch).not.toHaveBeenCalled();
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    await list(wrapper);
    expect(identity.accountRequest).toHaveBeenCalledExactlyOnceWith('/v1/twin/instances', 'GET', undefined, expect.any(AbortSignal));
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('device-exact');
    expect(wrapper.findAll('#observer-source option')).toHaveLength(3);
    expect(wrapper.text()).toContain('Pozorování spustíte tlačítkem Pozorovat');
    expect(wrapper.find('simulation-controls-stub').exists()).toBe(false);
  });
  it('reports an unavailable requested source without choosing another returned instance', async () => {
    const wrapper = await open('?mode=live&source=missing-device'); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    expect(wrapper.text()).toContain('není v dostupném autorizovaném seznamu');
    expect(identity.accountRequest).toHaveBeenCalledExactlyOnceWith('/v1/twin/instances', 'GET', undefined, expect.any(AbortSignal));
  });
  it('does not turn simulation intent into live selection', async () => {
    const wrapper = await open('?mode=simulation&source=device-exact'); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-mode').element.value).toBe('simulation');
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    expect(wrapper.text()).not.toContain('Požadovaný živý kufr');
    await wrapper.get('#observer-mode').setValue('live'); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
  });
  it('accepts backend IDs with periods and any allowed first character', async () => {
    identity.accountRequest.mockResolvedValue({ instances: [{ instanceId: '.case.s3_-', mode: 'live' }] });
    const wrapper = await open('?mode=live&source=.case.s3_-'); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('.case.s3_-');
    expect(identity.accountRequest).toHaveBeenCalledExactlyOnceWith('/v1/twin/instances', 'GET', undefined, expect.any(AbortSignal));
  });
  it.each(['%3Cscript%3E', 'a'.repeat(97), 'case:s3', 'device-exact&source=device-other'])('ignores unsafe or ambiguous source query %s', async source => {
    const wrapper = await open(`?mode=live&source=${source}`); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    expect(wrapper.text()).not.toContain('Požadovaný živý kufr');
  });
});

function privateEnvelope(sequence = '1') {
  return { status: 'online', ageMs: 0, receivedAtUtc: new Date().toISOString(), snapshot: { schemaVersion: 1, sequence, timeMs: sequence, revision: '1',
    source: { instanceId: 'device-exact', deviceId: 'private-owner-device', bootId: 'private-boot', mode: 'live', cadManifestRevision: null, hardwareProfile: 'B-effects' },
    reported: { physicalVerification: 'unavailable', servo: [] }, model: null, trace: { events: [] } } };
}
async function connectPrivate(wrapper: ReturnType<typeof mount>) {
  identity.accountRequest.mockResolvedValueOnce({ instances }).mockResolvedValueOnce(privateEnvelope());
  await list(wrapper);
  await wrapper.findAll('button').find(button => button.text() === 'Pozorovat')!.trigger('click'); await flushPromises();
  expect(wrapper.text()).toContain('private-owner-device'); expect(wrapper.text()).toContain('1 snapshotů');
}
describe('Workshop account observation ownership', () => {
  it('clears private snapshots, recording and sources synchronously on account switch and fences an uncooperative delayed poll', async () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    const wrapper = await open('?mode=live&source=device-exact'); await connectPrivate(wrapper);
    let complete!: (value: unknown) => void;
    identity.accountRequest.mockImplementationOnce(() => new Promise(resolve => { complete = resolve; }));
    await vi.advanceTimersByTimeAsync(750);
    const signal = identity.accountRequest.mock.calls.at(-1)![3] as AbortSignal;
    identity.account.value = { id: 'other-owner' };
    expect(signal.aborted).toBe(true); complete(privateEnvelope('2')); await flushPromises();
    expect(wrapper.text()).not.toContain('private-owner-device'); expect(wrapper.text()).toContain('0 snapshotů');
    expect(wrapper.findAll('#observer-source option')).toHaveLength(1);
    expect(wrapper.findAll('button').find(button => button.text() === 'Uložit záznam')!.attributes('disabled')).toBeDefined();
    const requests = identity.accountRequest.mock.calls.length; await vi.advanceTimersByTimeAsync(3000);
    expect(identity.accountRequest).toHaveBeenCalledTimes(requests);
    expect(wrapper.text()).toContain('synthetic-view-test');
  });
  it.each([401, 403])('clears scoped data and stops polling after HTTP %s without relying on a global account change', async status => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'setInterval', 'clearInterval'] });
    const wrapper = await open('?mode=live&source=device-exact'); await connectPrivate(wrapper);
    identity.accountRequest.mockRejectedValueOnce(Object.assign(new Error('scope revoked'), { status }));
    await vi.advanceTimersByTimeAsync(750); await flushPromises();
    expect(identity.account.value?.id).toBe('owner'); expect(wrapper.text()).not.toContain('private-owner-device');
    expect(wrapper.text()).toContain('0 snapshotů'); expect(wrapper.findAll('#observer-source option')).toHaveLength(1);
    const requests = identity.accountRequest.mock.calls.length; await vi.advanceTimersByTimeAsync(3000);
    expect(identity.accountRequest).toHaveBeenCalledTimes(requests);
  });
  it('keeps explicitly imported local replay separate from account-owned recordings on logout', async () => {
    const wrapper = await open('?mode=live');
    const recording = { schemaVersion: 1, profile: 'observer', cadManifestRevision: null, snapshots: [privateEnvelope()] };
    const input = wrapper.get<HTMLInputElement>('#recording-import');
    Object.defineProperty(input.element, 'files', { configurable: true, value: [{ size: 100, text: async () => JSON.stringify(recording) }] });
    await input.trigger('change'); await flushPromises();
    expect(wrapper.text()).toContain('REPLAY'); identity.account.value = null; await flushPromises();
    expect(wrapper.text()).toContain('REPLAY'); expect(wrapper.text()).toContain('1 snapshotů');
    expect(identity.accountRequest).not.toHaveBeenCalled();
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import WorkshopView from './WorkshopView.vue';

const identity = vi.hoisted(() => ({ accountRequest: vi.fn() }));
vi.mock('../identity/session', () => identity);
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
beforeEach(() => { vi.clearAllMocks(); identity.accountRequest.mockResolvedValue({ instances }); vi.stubGlobal('fetch', vi.fn()); });
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); vi.unstubAllGlobals(); history.replaceState({}, '', '/'); });

describe('Workshop contextual live source', () => {
  it('waits for explicit authorized listing, then selects only the exact live instance without connecting', async () => {
    const wrapper = await open('?mode=live&source=device-exact');
    expect(wrapper.get<HTMLSelectElement>('#observer-mode').element.value).toBe('live');
    expect(wrapper.text()).toContain('Požadovaný živý kufr: device-exact');
    expect(identity.accountRequest).not.toHaveBeenCalled(); expect(fetch).not.toHaveBeenCalled();
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    await list(wrapper);
    expect(identity.accountRequest).toHaveBeenCalledExactlyOnceWith('/v1/twin/instances');
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('device-exact');
    expect(wrapper.findAll('#observer-source option')).toHaveLength(3);
    expect(wrapper.text()).toContain('Pozorování spustíte tlačítkem Pozorovat');
    expect(wrapper.find('simulation-controls-stub').exists()).toBe(false);
  });
  it('reports an unavailable requested source without choosing another returned instance', async () => {
    const wrapper = await open('?mode=live&source=missing-device'); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    expect(wrapper.text()).toContain('není v dostupném autorizovaném seznamu');
    expect(identity.accountRequest).toHaveBeenCalledExactlyOnceWith('/v1/twin/instances');
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
    expect(identity.accountRequest).toHaveBeenCalledExactlyOnceWith('/v1/twin/instances');
  });
  it.each(['%3Cscript%3E', 'a'.repeat(97), 'case:s3', 'device-exact&source=device-other'])('ignores unsafe or ambiguous source query %s', async source => {
    const wrapper = await open(`?mode=live&source=${source}`); await list(wrapper);
    expect(wrapper.get<HTMLSelectElement>('#observer-source').element.value).toBe('');
    expect(wrapper.text()).not.toContain('Požadovaný živý kufr');
  });
});

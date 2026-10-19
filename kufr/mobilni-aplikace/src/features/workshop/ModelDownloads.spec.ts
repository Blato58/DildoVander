import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import ModelDownloads from './ModelDownloads.vue';
const bridge = vi.hoisted(() => ({ native: false, publicOrigin: vi.fn(), publicResource: vi.fn(), cancelPublicResource: vi.fn() }));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => bridge.native }, registerPlugin: () => bridge }));

const manifest = { bundleHash: 'a'.repeat(64), modelId: 'synthetic-test-model', revision: 'F2', provenance: { kind: 'cad-export' }, assets: [{ kind: 'model-json', path: 'model.json', sha256: 'b'.repeat(64), bytes: 100 }] };
beforeEach(() => { vi.clearAllMocks(); bridge.native = false; });
afterEach(() => { vi.unstubAllGlobals(); });
describe('model downloads', () => {
  it('never requests network or offers manufacturing on fixture/offline surfaces', async () => {
    bridge.native = true;
    const fetch = vi.fn(); vi.stubGlobal('fetch', fetch);
    for (const props of [{ bundle: { manifest }, offline: true }, { bundle: { manifest: { ...manifest, provenance: { kind: 'synthetic-fixture' } } }, offline: false }]) {
      const wrapper = mount(ModelDownloads, { props }); await flushPromises();
      expect(wrapper.find('a').exists()).toBe(false); wrapper.unmount();
    }
    expect(fetch).not.toHaveBeenCalled();
    expect(bridge.publicOrigin).not.toHaveBeenCalled(); expect(bridge.publicResource).not.toHaveBeenCalled();
  });
  it('shows a bound actual archive link and separates the original unbound archive', async () => {
    const base = { kind: 'manufacturing', modelId: manifest.modelId, releaseId: 'c'.repeat(64), sha256: 'c'.repeat(64), path: `releases/${'c'.repeat(64)}/F2.zip`, bytes: 1024, version: 'F2', cadVerification: 'verified', physicalAcceptance: false };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ schemaVersion: 1, artifacts: [{ ...base, label: '<script>archive</script>', modelAssetSha256: manifest.assets[0].sha256 }, { ...base, label: 'Původní archiv', modelAssetSha256: null }] }))));
    const wrapper = mount(ModelDownloads, { props: { bundle: { manifest }, offline: false } }); await flushPromises();
    expect(wrapper.text()).toContain('Historické výrobní archivy'); expect(wrapper.text()).toContain('nemají vazbu na hash');
    expect(wrapper.find('script').exists()).toBe(false); expect(wrapper.findAll('a')).toHaveLength(2);
    for (const link of wrapper.findAll('a')) expect(link.attributes('href')).toContain('/downloads/releases/');
    wrapper.unmount();
  });
  it('uses the canonical public origin for native model links, distribution index and actual download links', async () => {
    bridge.native = true; const origin = 'https://public-models.example';
    vi.stubGlobal('fetch', vi.fn()); bridge.publicOrigin.mockResolvedValue({ origin });
    const item = { kind: 'manufacturing', modelId: manifest.modelId, releaseId: 'c'.repeat(64), sha256: 'c'.repeat(64), path: `releases/${'c'.repeat(64)}/F2.zip`, bytes: 1024, label: 'Archiv', version: 'F2', cadVerification: 'verified', physicalAcceptance: false, modelAssetSha256: manifest.assets[0].sha256 };
    bridge.publicResource.mockResolvedValue({ status: 200, headers: { 'content-type': 'application/json' }, bodyBase64: btoa(JSON.stringify({ schemaVersion: 1, artifacts: [item] })) });
    const resourceManifestUrl = `${origin}/models/bundles/${manifest.bundleHash}/model-bundle.json`;
    const wrapper = mount(ModelDownloads, { props: { bundle: { manifest, resourceManifestUrl }, offline: false } }); await flushPromises();
    expect(bridge.publicResource).toHaveBeenCalledWith({ path: '/downloads/index.json', maxBytes: 262144, requestId: expect.any(String) });
    expect(wrapper.findAll('a').map(link => link.attributes('href'))).toEqual([resourceManifestUrl, `${origin}/models/bundles/${manifest.bundleHash}/model.json`, `${origin}/downloads/${item.path}`]);
    expect(fetch).not.toHaveBeenCalled(); wrapper.unmount();
  });
});

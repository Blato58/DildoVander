import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises } from '@vue/test-utils';
import fixtureManifest from './fixture/model-bundle.json';
import modelJson from './fixture/model.json?raw';
import { canonicalManifest, loadPublishedBundle, sha256 } from './model-bundle.mjs';

const bridge = vi.hoisted(() => ({ native: true, publicOrigin: vi.fn(), publicResource: vi.fn(), cancelPublicResource: vi.fn(), accountTransport: vi.fn(), request: vi.fn() }));
vi.mock('@capacitor/core', () => ({ Capacitor: { isNativePlatform: () => bridge.native }, registerPlugin: () => bridge }));
import { nativePublicReader, publicResourceClient, publicResourcePath, validatePublicOrigin } from './public-resources';

const origin = 'https://models.example';
const encode = (value: string) => btoa(Array.from(new TextEncoder().encode(value), byte => String.fromCharCode(byte)).join(''));
const response = (value: string) => ({ status: 200, headers: { 'content-type': 'application/json' }, bodyBase64: encode(value) });
beforeEach(() => { vi.clearAllMocks(); bridge.native = true; bridge.publicOrigin.mockResolvedValue({ origin }); bridge.cancelPublicResource.mockResolvedValue(undefined); });
afterEach(() => { vi.unstubAllGlobals(); });

describe('native public Workshop resources', () => {
  it('loads with legacy cancellation and securely generates request IDs when randomUUID is unavailable', async () => {
    const nativeCrypto = crypto;
    const random = vi.fn((bytes: Uint8Array<ArrayBuffer>) => nativeCrypto.getRandomValues(bytes));
    vi.stubGlobal('crypto', { getRandomValues: random });
    const controller = new AbortController();
    Object.defineProperties(controller.signal, { throwIfAborted: { value: undefined }, reason: { value: undefined } });
    bridge.publicResource.mockResolvedValue(response('{}'));
    const client = await publicResourceClient(controller.signal);
    await client.read(origin + '/models/active.json', 100, controller.signal);
    await client.read(origin + '/models/active.json', 100, controller.signal);
    const ids = bridge.publicResource.mock.calls.map(([request]) => request.requestId);
    expect(random).toHaveBeenCalledTimes(2);
    expect(ids[0]).toMatch(/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/);
    expect(ids[1]).not.toBe(ids[0]);
    controller.abort();
    await expect(client.read(origin + '/models/active.json', 100, controller.signal)).rejects.toMatchObject({ name: 'AbortError' });
    expect(bridge.publicResource).toHaveBeenCalledTimes(2);
  });

  it('loads active manifest and every hashed asset from the configured HTTPS origin without account calls or browser fetch', async () => {
    vi.stubGlobal('fetch', vi.fn());
    const manifest = structuredClone(fixtureManifest); manifest.provenance.kind = 'cad-export';
    manifest.bundleHash = await sha256(new TextEncoder().encode(canonicalManifest(manifest)));
    const root = `/models/bundles/${manifest.bundleHash}/`;
    const bodies: Record<string, string> = { '/models/active.json': JSON.stringify({ schemaVersion: 1, modelId: manifest.modelId, bundleHash: manifest.bundleHash, manifestPath: `bundles/${manifest.bundleHash}/model-bundle.json` }), [root + 'model-bundle.json']: JSON.stringify(manifest), [root + 'model.json']: modelJson };
    bridge.publicResource.mockImplementation(async ({ path }) => response(bodies[path]));
    const client = await publicResourceClient(new AbortController().signal);
    const bundle = await loadPublishedBundle(new URL('/models/', client.origin).href, { readBytes: client.read });
    expect(bundle.resourceManifestUrl).toBe(origin + root + 'model-bundle.json');
    expect(bridge.publicResource.mock.calls.map(([request]) => request.path)).toEqual(['/models/active.json', root + 'model-bundle.json', root + 'model.json']);
    for (const [request] of bridge.publicResource.mock.calls) {
      expect(Object.keys(request).sort()).toEqual(['maxBytes', 'path', 'requestId']);
      expect(request.requestId).toMatch(/^[a-f0-9-]{36}$/);
    }
    expect(fetch).not.toHaveBeenCalled(); expect(bridge.request).not.toHaveBeenCalled(); expect(bridge.accountTransport).not.toHaveBeenCalled();
    bridge.publicResource.mockImplementation(async ({ path }) => response(path.endsWith('/model.json') ? modelJson.replace('"modelId"', '"modelIb"') : bodies[path]));
    await expect(loadPublishedBundle(new URL('/models/', client.origin).href, { readBytes: client.read })).rejects.toThrow(/integrity failure/);
  });
  it.each([origin + '/models/../app/v1/session', 'https://elsewhere.example/models/active.json', origin + '/models/active.json?token=x', origin + '/models/%61ctive.json', origin + '/downloads/releases/' + 'a'.repeat(64) + '/file.zip'])('rejects origin or path escape before invoking the bridge: %s', async url => {
    const read = nativePublicReader(bridge, origin);
    await expect(read(url, 100, new AbortController().signal)).rejects.toThrow();
    expect(bridge.publicResource).not.toHaveBeenCalled();
  });
  it('loads a declared nested non-json-extension asset served as inert octet-stream while enforcing its JSON hash', async () => {
    const manifest = structuredClone(fixtureManifest); manifest.provenance.kind = 'cad-export';
    manifest.assets[0].path = 'nested/model.data';
    manifest.bundleHash = await sha256(new TextEncoder().encode(canonicalManifest(manifest)));
    const root = `/models/bundles/${manifest.bundleHash}/`;
    const metadata: Record<string, string> = {
      '/models/active.json': JSON.stringify({ schemaVersion: 1, modelId: manifest.modelId, bundleHash: manifest.bundleHash, manifestPath: `bundles/${manifest.bundleHash}/model-bundle.json` }),
      [root + 'model-bundle.json']: JSON.stringify(manifest),
    };
    bridge.publicResource.mockImplementation(async ({ path }) => path === root + 'nested/model.data'
      ? { ...response(modelJson), headers: { 'content-type': 'application/octet-stream' } }
      : response(metadata[path]));
    const client = await publicResourceClient(new AbortController().signal);
    const bundle = await loadPublishedBundle(new URL('/models/', client.origin).href, { readBytes: client.read });
    expect(bundle.model.modelId).toBe(JSON.parse(modelJson).modelId);
    expect(bundle.manifest.assets[0].sha256).toBe(await sha256(new TextEncoder().encode(modelJson)));
    expect(bridge.publicResource.mock.calls.map(([request]) => request.path)).toEqual(['/models/active.json', root + 'model-bundle.json', root + 'nested/model.data']);
    bridge.publicResource.mockImplementation(async ({ path }) => path === root + 'nested/model.data'
      ? { ...response(modelJson.replace('"modelId"', '"modelIb"')), headers: { 'content-type': 'application/octet-stream' } }
      : response(metadata[path]));
    await expect(loadPublishedBundle(new URL('/models/', client.origin).href, { readBytes: client.read })).rejects.toThrow(/integrity failure/);
  });
  it('rejects over-limit native bytes and invalid origin configuration', async () => {
    bridge.publicResource.mockResolvedValue(response('12345'));
    await expect(nativePublicReader(bridge, origin)(origin + '/models/active.json', 3, new AbortController().signal)).rejects.toThrow(/velikost/);
    expect(() => publicResourcePath('/models/active.json', origin, 131073)).toThrow();
    expect(() => validatePublicOrigin('http://models.example')).toThrow();
  });
  it('cancels obsolete native transfer and queues the next source until native cancellation settles', async () => {
    let finishCancel!: () => void, finishOld!: (value: ReturnType<typeof response>) => void;
    bridge.publicResource.mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve; })).mockResolvedValue(response('{}'));
    bridge.cancelPublicResource.mockImplementationOnce(() => new Promise<void>(resolve => { finishCancel = resolve; }));
    const read = nativePublicReader(bridge, origin), controller = new AbortController();
    const old = read(origin + '/models/active.json', 100, controller.signal);
    const rejected = expect(old).rejects.toMatchObject({ name: 'AbortError' });
    await flushPromises(); controller.abort();
    const next = read(origin + '/downloads/index.json', 100, new AbortController().signal);
    await flushPromises(); expect(bridge.publicResource).toHaveBeenCalledOnce();
    expect(bridge.cancelPublicResource).toHaveBeenCalledWith({ requestId: bridge.publicResource.mock.calls[0][0].requestId });
    finishCancel(); await rejected; expect(new TextDecoder().decode(await next)).toBe('{}');
    finishOld(response('stale')); await flushPromises();
    expect(bridge.publicResource).toHaveBeenCalledTimes(2);
  });
  it('keeps web reads same-origin with omitted credentials and no native account bridge', async () => {
    bridge.native = false; vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}')));
    const client = await publicResourceClient(new AbortController().signal);
    await client.read(new URL('/downloads/index.json', client.origin), 262144, new AbortController().signal);
    expect(fetch).toHaveBeenCalledWith(expect.any(URL), expect.objectContaining({ credentials: 'omit', redirect: 'error' }));
    expect(bridge.publicOrigin).not.toHaveBeenCalled(); expect(bridge.publicResource).not.toHaveBeenCalled();
  });
});

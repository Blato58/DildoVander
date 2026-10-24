import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import GeneratedWand from './GeneratedWand.vue';
import { projectWandArtwork, useWandArtworkRetry } from './wandArtwork';
import { projectDuel } from './showContracts';
import { account, accountTransport } from '../identity/session';
vi.mock('../identity/session', async () => ({ account: (await import('vue')).ref({ id: 'owner' }), accountTransport: vi.fn() }));
const locator = '11111111-1111-4111-8111-111111111111';
const asset = '22222222-2222-4222-8222-222222222222';
const wand = { wandId: 'individual', physicalSlot: 2, name: 'Ozvěna trnů', material: 'Tis', core: 'Nit svitu', temperament: 'Netrpělivá', lore: 'Pamatuje každou urážku.', designDescription: 'Větvený úchop se stříbrnými trny.', affinity: 'Světlo', weakness: 'Ticho', combatArchetype: 'offensive', combatRulesVersion: 'wand-archetypes-v1', spells: ['strike', 'trick', 'shield'].map((action, index) => ({ action, name: `Osobní kouzlo ${index}`, description: `Kouzlo provede magii ${index}.` })) };
afterEach(() => { vi.clearAllMocks(); account.value = { id: 'owner' } as typeof account.value; });
describe('canonical wand artwork', () => {
  it('keeps fiction available through image failure and emits only an explicit retry', async () => {
    const wrapper = mount(GeneratedWand, { props: { wand, showName: true, imageState: 'failed', canRetry: true } });
    expect(wrapper.text()).toContain(wand.name); expect(wrapper.text()).toContain(wand.lore); expect(wrapper.text()).toContain('Útočná');
    expect(wrapper.findAll('[data-spell]')).toHaveLength(3); expect(wrapper.text()).not.toContain('Přihrádka 2'); expect(wrapper.emitted('retry')).toBeUndefined();
    await wrapper.find('button').trigger('click'); expect(wrapper.emitted('retry')).toHaveLength(1);
    await wrapper.setProps({ imageUrl: 'blob:canonical', imageState: 'ready', canRetry: false });
    expect(wrapper.find('img').attributes('src')).toBe('blob:canonical'); expect(wrapper.find('img').attributes('alt')).toContain(wand.name);
    await wrapper.find('img').trigger('error'); expect(wrapper.text()).toContain('Ilustrace není dostupná'); expect(wrapper.text()).toContain(wand.name); expect(wrapper.find('button').exists()).toBe(false); wrapper.unmount();
  });
  it('projects only a versioned identifier and prevents replacing published art', () => {
    const published = projectWandArtwork({ locator, assetId: asset, status: 'ready', canRetry: true, version: 1, prompt: 'private', accountId: 'secret' });
    expect(published).toEqual({ locator, assetId: asset, status: 'ready', canRetry: false, version: 1, synthetic: false });
    expect(projectWandArtwork({ locator, status: 'ready', version: 1 })).toBeNull();
    expect(projectWandArtwork({ locator, status: 'failed', version: 2 })).toBeNull();
    expect(projectWandArtwork({ locator: 'https://foreign.example', status: 'failed', version: 1 })).toBeNull();
  });
  it('retains one operation key for uncertain retries and fences account changes', async () => {
    const retry = useWandArtworkRetry(); const art = projectWandArtwork({ locator, assetId: null, status: 'failed', canRetry: true, version: 1 }); const refresh = vi.fn(async () => {});
    vi.mocked(accountTransport).mockRejectedValueOnce(new Error('network'));
    await retry.retry(art, refresh); expect(retry.failed.value).toBe(true); expect(refresh).not.toHaveBeenCalled();
    const first = vi.mocked(accountTransport).mock.calls[0]; const key = new Headers(first[1]?.headers).get('Idempotency-Key');
    expect(key).toMatch(/^[0-9a-f-]{36}$/); expect(first[0]).toBe(`/app/v1/customer/keepsakes/${locator}/wand-artwork/retry`); expect(first[1]?.body).toBe('{}');
    let finish!: (value: Response) => void; vi.mocked(accountTransport).mockImplementationOnce(() => new Promise(resolve => { finish = resolve; }));
    const pending = retry.retry(art, refresh); await flushPromises(); account.value = { id: 'other' } as typeof account.value; retry.invalidate(); finish(Response.json({})); await pending;
    expect(new Headers(vi.mocked(accountTransport).mock.calls[1][1]?.headers).get('Idempotency-Key')).toBe(key); expect(refresh).not.toHaveBeenCalled();
    expect(retry.busy.value).toBe(false);
  });
  it('accepts only approved public artwork paths on duel snapshots', () => {
    const raw = { id: locator, revision: 1, state: 'Running', mode: 'interactive', round: 1, rounds: [], left: { displayName: 'A', wandName: wand.name, spells: wand.spells, artworkUrl: `/guest/v1/evenings/${locator}/keepsakes/${locator}/editions/${locator}/assets/${asset}`, combatArchetype: 'offensive', strength: 4, cunning: 2, defense: 3 }, right: { displayName: 'B', artworkUrl: `/app/v1/archive/owned/${locator}/assets/${asset}` } };
    expect(projectDuel(raw, locator).left.artworkUrl).toBe(raw.left.artworkUrl); expect(projectDuel(raw, locator).right.artworkUrl).toBeUndefined();
    expect(projectDuel({ ...raw, left: { ...raw.left, artworkUrl: `https://other.example${raw.left.artworkUrl}` } }, locator).left.artworkUrl).toBeUndefined();
  });
});

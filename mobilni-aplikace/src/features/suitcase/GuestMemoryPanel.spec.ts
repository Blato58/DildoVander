import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import GuestMemoryPanel from './GuestMemoryPanel.vue';
import { nativeDebug } from './nativeDebug';
import { deviceAssociation as cloud } from './deviceAssociation';
const bridge = vi.hoisted(() => ({ status: vi.fn(), stop: vi.fn(), inspectMemory: vi.fn(), deleteConversationMemory: vi.fn() }));
vi.mock('@capacitor/core', () => ({ registerPlugin: () => bridge }));
vi.mock('./deviceSession', () => ({ deviceSession: { native: true } }));
vi.mock('./deviceAssociation', async () => { const { ref } = await import('vue'); return { deviceAssociation: { account: ref({ id: 'admin', roles: ['Administrator'] }), administrator: ref(true) } }; });
const inspection = { identity: null, epoch: 7, deletionEpoch: 2, records: [{ memoryId: 'record-1', text: 'Private approved fact', sourceReference: 'event-1', revision: 3, isActive: true }], totalRecords: 1, hasMore: false,
  memoryStatus: { provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready', pending: 0, failed: 0, deleting: 0, storedBytes: 838861, capacityBytes: 1048576, rejectedWrites: 0 } };
let wrapper: VueWrapper;
function button(text: string) { return wrapper.findAll('button').find(item => item.text() === text)!; }
beforeEach(() => { vi.clearAllMocks(); nativeDebug.pending.value = false;
  cloud.account.value = { id: 'admin', displayName: 'Admin', roles: ['Administrator'] };
  nativeDebug.status.value = { configured: true, running: false, session: false, phase: 'idle', mode: 'openai', accessMode: 'account', deviceId: 'kufr-1' };
  bridge.status.mockImplementation(async () => nativeDebug.status.value); bridge.inspectMemory.mockResolvedValue(inspection);
  bridge.deleteConversationMemory.mockResolvedValue({ localDeletion: 'complete', remoteDeletion: 'pending' }); });
afterEach(() => { wrapper?.unmount(); nativeDebug.dispose(); });
describe('scoped memory administration', () => {
  it('reads only an explicitly chosen conversation and requires fresh explicit deletion confirmation', async () => {
    wrapper = mount(GuestMemoryPanel); expect(bridge.inspectMemory).not.toHaveBeenCalled();
    await wrapper.get('[data-memory-conversation]').setValue('conversation-1'); await button('Načíst paměť rozhovoru').trigger('click'); await flushPromises();
    expect(bridge.inspectMemory).toHaveBeenCalledWith({ conversationId: 'conversation-1' }); expect(wrapper.text()).toContain('Private approved fact');
    expect(button('Odstranit paměť tohoto rozhovoru').attributes('disabled')).toBeDefined();
    await wrapper.get('[data-memory-confirm]').setValue(true); await button('Odstranit paměť tohoto rozhovoru').trigger('click'); await flushPromises();
    expect(bridge.deleteConversationMemory).toHaveBeenCalledWith({ conversationId: 'conversation-1', expectedEpoch: 7, confirm: true });
    expect(wrapper.text()).toContain('Vzdálený úklid čeká'); expect(wrapper.text()).not.toContain('Private approved fact');
  });
  it('discards a late previous-account memory response', async () => {
    let resolve!: (value: unknown) => void; bridge.inspectMemory.mockReturnValue(new Promise(value => { resolve = value; }));
    wrapper = mount(GuestMemoryPanel); await wrapper.get('[data-memory-conversation]').setValue('conversation-1');
    await button('Načíst paměť rozhovoru').trigger('click');
    cloud.account.value = { id: 'another-admin', displayName: 'Other', roles: ['Administrator'] }; await flushPromises();
    resolve(inspection); await flushPromises(); expect(wrapper.text()).not.toContain('Private approved fact'); expect(wrapper.find('[data-memory-confirm]').exists()).toBe(false);
  });
  it('does not permit memory deletion while a conversation is active', async () => {
    nativeDebug.status.value = { ...nativeDebug.status.value, session: true };
    wrapper = mount(GuestMemoryPanel); await wrapper.get('[data-memory-conversation]').setValue('conversation-1');
    await button('Načíst paměť rozhovoru').trigger('click'); await flushPromises();
    expect(wrapper.get('[data-memory-confirm]').attributes('disabled')).toBeDefined();
    expect(button('Odstranit paměť tohoto rozhovoru').attributes('disabled')).toBeDefined(); expect(bridge.deleteConversationMemory).not.toHaveBeenCalled();
  });
  it('shows revoked verification and consent flags truthfully', async () => {
    bridge.inspectMemory.mockResolvedValue({ ...inspection, identity: { guestId: 'Guest-042', identityVerified: false, longTermAllowed: false, cloudExportAllowed: false, version: 4 } });
    wrapper = mount(GuestMemoryPanel); await wrapper.get('[data-memory-conversation]').setValue('conversation-1');
    await button('Načíst paměť rozhovoru').trigger('click'); await flushPromises();
    expect(wrapper.text()).toContain('Neověřená nebo odvolaná vazba'); expect(wrapper.text()).toContain('Dlouhodobá osobní paměť: nepovolena');
    expect(wrapper.text()).toContain('cloudový index: nepovolen'); expect(wrapper.text()).toContain('verze 4');
  });
  it('shows global archive capacity, exact byte counts and historical rejected writes', async () => {
    bridge.inspectMemory.mockResolvedValue({ ...inspection, memoryStatus: { ...inspection.memoryStatus,
      storageStatus: 'warning', storageIncomplete: true, rejectedWrites: 2,
      lastStorageErrorCode: 'archive_capacity', lastStorageErrorAt: '2026-10-23T10:15:30.000Z' } });
    wrapper = mount(GuestMemoryPanel); await wrapper.get('[data-memory-conversation]').setValue('conversation-1');
    await button('Načíst paměť rozhovoru').trigger('click'); await flushPromises();
    const storage = wrapper.get('[aria-label="Kapacita sdíleného archivu"]');
    const storageText = storage.text().replaceAll('\u00a0', ' ');
    expect(storage.text()).toContain('Varování: využití dosáhlo alespoň 80 %');
    expect(storageText).toContain('0,8 MiB (838 861 B) z 1 MiB (1 048 576 B)');
    expect(storage.text()).toContain('Odmítnuté zápisy: 2');
    expect(storage.text()).toContain('Některé zápisy v minulosti nebyly uloženy');
    expect(storage.text()).toContain('Tento přehled nepotvrzuje uložení všech zápisů');
    expect(storage.text()).toContain('Kapacita společného archivu byla dosažena');
    expect(storage.text()).toContain('2026-10-23T10:15:30.000Z');
    expect(storage.text()).toContain('nikoli pouze k tomuto rozhovoru');
  });
  it('uses the authoritative full status and never renders an unknown backend error code', async () => {
    const untrustedCode = '<img src=x onerror=alert(1)>private backend detail';
    bridge.inspectMemory.mockResolvedValue({ ...inspection, memoryStatus: { ...inspection.memoryStatus,
      storedBytes: 10, capacityBytes: 1000, storageStatus: 'full', storageIncomplete: false,
      lastStorageErrorCode: untrustedCode, lastStorageErrorAt: 'not a timestamp' } });
    wrapper = mount(GuestMemoryPanel); await wrapper.get('[data-memory-conversation]').setValue('conversation-1');
    await button('Načíst paměť rozhovoru').trigger('click'); await flushPromises();
    const storage = wrapper.get('[aria-label="Kapacita sdíleného archivu"]');
    expect(storage.text()).toContain('Stav úložiště: Plná');
    expect(storage.text()).toContain('Poslední odmítnutí mělo jinou příčinu');
    expect(storage.text()).not.toContain(untrustedCode);
    expect(storage.text()).not.toContain('not a timestamp');
  });
  it('keeps capacity counts readable when optional status fields are absent in an older fixture', async () => {
    wrapper = mount(GuestMemoryPanel); await wrapper.get('[data-memory-conversation]').setValue('conversation-1');
    await button('Načíst paměť rozhovoru').trigger('click'); await flushPromises();
    const storage = wrapper.get('[aria-label="Kapacita sdíleného archivu"]');
    const storageText = storage.text().replaceAll('\u00a0', ' ');
    expect(storage.text()).toContain('Stav úložiště: Stav kapacity není dostupný');
    expect(storageText).toContain('0,8 MiB (838 861 B) z 1 MiB (1 048 576 B)');
    expect(storage.text()).not.toContain('Některé zápisy v minulosti nebyly uloženy');
  });
});

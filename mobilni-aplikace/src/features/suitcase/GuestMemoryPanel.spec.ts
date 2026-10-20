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
  memoryStatus: { provider: 'cloud', configured: true, degraded: false, configurationStatus: 'ready', pending: 0, failed: 0, deleting: 0 } };
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
});

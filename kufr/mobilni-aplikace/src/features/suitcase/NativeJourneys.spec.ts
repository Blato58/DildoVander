import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import ConnectionsView from './ConnectionsView.vue';
import DiagnosticsView from './DiagnosticsView.vue';
import { deviceSession } from './deviceSession';
import { nativeDebug } from './nativeDebug';
import { account } from '../identity/session';

const harness = vi.hoisted(() => ({
  paired: false,
  savedDevice: vi.fn(),
  provisioning: { discover: vi.fn(), connect: vi.fn(), status: vi.fn(), pairLocal: vi.fn(), configure: vi.fn(),
    command: vi.fn(), disconnect: vi.fn(), openSettings: vi.fn() },
  debug: { status: vi.fn(), stop: vi.fn(), forgetProfile: vi.fn(), configure: vi.fn(), useAccount: vi.fn(), changeServerModes: vi.fn(), startConversation: vi.fn() },
  identity: { request: vi.fn() },
}));
vi.mock('@capacitor/core', () => ({ Capacitor: { getPlatform: () => 'ios', isNativePlatform: () => true },
  registerPlugin: (name: string) => name === 'KufrDebug' ? harness.debug : name === 'KufrIdentity' ? harness.identity : harness.provisioning }));
vi.mock('../device_connection/NativeDeviceConnection', () => ({
  nativeBridge: { savedDevice: harness.savedDevice }, NativeDeviceConnection: class {},
}));
let wrapper: VueWrapper;
const options = { global: { stubs: { RouterLink: { template: '<a><slot /></a>' } } } };
function button(label: string) { const found = wrapper.findAll('button').find(item => item.text() === label); if (!found) throw new Error(`Missing ${label}`); return found; }
const provisioningStatus = {
  v: 1, ok: true, deviceId: 'kufr-1', firmwareVersion: 'test',
  network: { phase: 'connected', ipv4: '192.168.0.62', activeProfile: 'hotspot', lastSuccessfulProfile: 'hotspot', result: 'ok',
    hotspot: { configured: true, ssid: 'Test hotspot' }, fallback: { configured: false, ssid: '' } },
  backendConnected: false, ota: { state: 'idle' },
};
beforeEach(async () => {
  vi.useFakeTimers(); vi.clearAllMocks(); harness.paired = false;
  account.value = null;
  harness.identity.request.mockResolvedValue({ status: 200, body: JSON.stringify({ grants: [
    { id: 'g-1', resourceType: 'device', resourceId: 'kufr-1', permission: 'operate' }], associations: [], sessions: [] }) });
  harness.savedDevice.mockImplementation(async () => harness.paired ? { paired: true, deviceId: 'kufr-1', host: '192.168.0.62' } : { paired: false });
  harness.provisioning.discover.mockResolvedValue({ devices: [{ name: 'KUFR_TEST' }] });
  harness.provisioning.connect.mockResolvedValue(provisioningStatus);
  harness.provisioning.status.mockResolvedValue(provisioningStatus);
  harness.provisioning.configure.mockResolvedValue(provisioningStatus);
  harness.provisioning.command.mockResolvedValue(provisioningStatus);
  harness.provisioning.disconnect.mockResolvedValue(undefined);
  harness.provisioning.pairLocal.mockImplementation(async () => { harness.paired = true; return { deviceId: 'kufr-1', host: '192.168.0.62' }; });
  harness.debug.status.mockResolvedValue({ configured: true, running: false, phase: 'idle', mode: 'fake',
    server: { revision: 'rev-7', dialogMode: 'fake', voiceMode: 'fake', dialogAvailable: true, voiceAvailable: true,
      activeConversations: 0, lifetime: 'until_restart', personalityId: 'ollivander-trunk', characterEnabled: false,
      characterTone: 'Adult', characterFinaleProfile: 'Blocked', personalities: [] } });
  await deviceSession.disconnectBle(); deviceSession.snapshot.value = null;
});
afterEach(async () => { wrapper?.unmount(); await deviceSession.disconnectBle(); deviceSession.dispose(); nativeDebug.dispose(); vi.useRealTimers(); });
async function connectBle(): Promise<void> {
  await flushPromises(); await button('Vyhledat Bluetooth kufry').trigger('click'); await flushPromises();
  await button('Ověřit KUFR_TEST').trigger('click'); await flushPromises();
}
describe('native Vue connection and service journeys', () => {
  it('mount only reads saved pairing; explicit BLE verification and pair action enroll the local device', async () => {
    wrapper = mount(ConnectionsView, options); await flushPromises();
    expect(harness.savedDevice).toHaveBeenCalledOnce(); expect(harness.provisioning.discover).not.toHaveBeenCalled();
    expect(button('Spárovat místní ovládání').attributes('disabled')).toBeDefined();
    await connectBle(); expect(harness.provisioning.connect).toHaveBeenCalledWith({ name: 'KUFR_TEST' });
    await button('Spárovat místní ovládání').trigger('click'); await flushPromises();
    expect(harness.provisioning.pairLocal).toHaveBeenCalledOnce();
    expect(wrapper.text()).toContain('Uložený kufr'); expect(button('Připojit uložený kufr').exists()).toBe(true);
    expect(deviceSession.snapshot.value).toBeNull();
  });
  it('network configuration uses native credentials and deletion requires a second explicit confirmation', async () => {
    wrapper = mount(ConnectionsView, options); await connectBle();
    await button('Nastavit síť').trigger('click'); await flushPromises();
    expect(harness.provisioning.configure).toHaveBeenCalledWith({ profile: 'hotspot' });
    await button('Smazat vybranou síť…').trigger('click'); expect(harness.provisioning.command).not.toHaveBeenCalled();
    await button('Potvrdit').trigger('click'); await flushPromises();
    expect(harness.provisioning.command).toHaveBeenCalledWith({ command: 'delete', profile: 'hotspot', automaticInstall: undefined });
  });
  it('diagnostics stays passive on mount and preserves DV01 revision and restricted finale contract', async () => {
    wrapper = mount(DiagnosticsView, options); await flushPromises();
    expect(wrapper.get('#phone-heading').text()).toBe('Test hlasu na telefonu');
    expect(wrapper.get('#servo-bench-title').text()).toBe('Stolní test a ruční profil serva');
    expect(harness.debug.startConversation).not.toHaveBeenCalled(); expect(harness.debug.changeServerModes).not.toHaveBeenCalled();
    const finale = wrapper.findAll('select').find(item => item.find('option[value="Blocked"]').exists())!;
    expect(finale.findAll('option').map(item => item.attributes('value'))).toEqual(['Blocked', 'Limited']);
    await finale.setValue('Limited'); await button('Použít DV01 pro nové rozhovory').trigger('click'); await flushPromises();
    expect(harness.debug.changeServerModes).toHaveBeenCalledWith(expect.objectContaining({ expectedRevision: 'rev-7',
      dialogMode: 'fake', voiceMode: 'fake', characterFinaleProfile: 'Limited', personalityId: 'ollivander-trunk' }));
    expect(deviceSession.snapshot.value).toBeNull();
  });
  it('normal native diagnostics uses the signed-in exact-device account without a service credential or admin modes', async () => {
    harness.paired = true; account.value = { id: 'operator-1', displayName: 'Operator', roles: ['Operator'] };
    harness.debug.status.mockResolvedValue({ configured: false, running: false, phase: 'idle', mode: 'unknown', accessMode: 'none', server: null });
    harness.debug.useAccount.mockImplementation(async ({ deviceId }) => {
      harness.debug.status.mockResolvedValue({ configured: true, running: false, phase: 'idle', mode: 'openai',
        accessMode: 'account', deviceId, dialogMode: 'responses', server: null });
    });
    wrapper = mount(DiagnosticsView, options); await flushPromises();
    expect(harness.debug.useAccount).not.toHaveBeenCalled();
    expect(button('Použít přihlášený účet pro tento kufr').attributes('disabled')).toBeUndefined();
    await button('Použít přihlášený účet pro tento kufr').trigger('click'); await flushPromises();
    expect(harness.debug.useAccount).toHaveBeenCalledWith({ deviceId: 'kufr-1' });
    expect(harness.debug.configure).not.toHaveBeenCalled(); expect(wrapper.find('input[type="password"]').exists()).toBe(false);
    expect(button('Spustit AI rozhovor').attributes('disabled')).toBeUndefined();
    expect(wrapper.text()).not.toContain('DV01 · dospělá roast postava');
  });
});

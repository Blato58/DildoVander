import { registerPlugin } from '@capacitor/core';
import { ProvisioningClient, errorMessage, type NetworkProfile, type ProvisioningBridge, type ProvisioningStatus } from './ProvisioningClient';

const bridge = registerPlugin<ProvisioningBridge>('KufrProvisioning');

export class ProvisioningPanel {
  readonly element = document.createElement('section');
  private readonly client: ProvisioningClient;
  private connected = false;
  private busy = false;
  constructor(private readonly nativeMode: boolean, private readonly onPaired: () => Promise<void> = async () => {}) {
    this.element.id = 'provisioning-panel';
    this.element.className = 'debug-panel';
    this.element.hidden = true;
    this.element.setAttribute('role', 'tabpanel');
    this.element.setAttribute('aria-labelledby', 'tab-provisioning');
    this.element.innerHTML = `
      <h1>Síť kufru</h1>
      <p>Bluetooth nastaví Wi-Fi přímo v kufru. Telefon nemusí být připojen k vlastnímu hotspotu.
         Pozorování a ruční ovládání dál používají místní síť.</p>
      <section class="debug-card">
        <h2>iPhone Osobní hotspot · přednostní síť</h2>
        <ol><li>V Nastavení → Osobní hotspot zapněte Povolit připojení ostatním.</li>
          <li>Zapněte Maximalizovat kompatibilitu (2,4 GHz). ESP32 nepoužívá 5GHz síť.</li>
          <li>Připravte název iPhonu (SSID) a heslo z této obrazovky. Zadejte je do zabezpečeného dialogu.</li></ol>
        <p>Aplikace hotspot sama nezapíná a jeho heslo nečte. Při prvním připojení nechte obrazovku
          Osobního hotspotu otevřenou; poté se vraťte k ověření stavu kufru.</p>
        <button type="button" data-ble="settings">Nastavení aplikace / oprávnění Bluetooth</button>
      </section>
      <section class="debug-card">
        <h2>Najít a ověřit kufr</h2>
        <p>BLE je dostupné jen v omezeném servisním nebo obnovovacím okně. Ověření vyžaduje
          jedinečný PoP z vnitřního servisního štítku; nejde o token místního WSS.</p>
        <div class="actions"><button type="button" data-ble="discover">Vyhledat BLE kufry</button>
          <button type="button" data-ble="disconnect" disabled>Odpojit BLE</button></div>
        <p>Po ověření BLE spárujte místní ovládání jedním tlačítkem. Adresa, otisk a token se
          převezmou automaticky; přístup zůstane v Keychain tohoto iPhonu i při aktualizaci aplikace.</p>
        <button type="button" data-ble="pair-local" disabled>Spárovat ovládání přes Wi-Fi</button>
        <ul class="ble-devices" aria-label="Nalezené BLE kufry"></ul>
        <p class="status" role="status" aria-live="polite">${nativeMode ? 'Vyhledejte zapnutý kufr.' : 'BLE provisioning je dostupný v nativní iPhone aplikaci. Web nemá přístup k Bluetooth.'}</p>
        <dl class="debug-metrics ble-metrics" hidden>
          <div><dt>Zařízení / firmware</dt><dd data-metric="device"></dd></div>
          <div><dt>Wi-Fi / výsledek</dt><dd data-metric="network"></dd></div>
          <div><dt>Aktivní / poslední síť</dt><dd data-metric="active"></dd></div>
          <div><dt>Hotspot</dt><dd data-metric="hotspot"></dd></div>
          <div><dt>Záložní Wi-Fi</dt><dd data-metric="fallback"></dd></div>
          <div><dt>Backend</dt><dd data-metric="backend"></dd></div>
          <div><dt>Aktualizace firmware</dt><dd data-metric="ota"></dd></div>
        </dl>
      </section>
      <section class="debug-card">
        <h2>Sítě a aktualizace</h2>
        <p>Hotspot má přednost před záložní Wi-Fi. Nové údaje nahradí staré až po úspěšném připojení;
          při chybě kufr zachová předchozí nastavení. Hesla se v telefonu neukládají.</p>
        <label>Upravovaná síť<select data-profile><option value="hotspot">iPhone Osobní hotspot</option>
          <option value="fallback">Záložní Wi-Fi</option></select></label>
        <div class="actions ble-network-actions">
          <button type="button" data-ble="configure" disabled>Nastavit / nahradit údaje</button>
          <button type="button" data-ble="test" disabled>Otestovat připojení sítě</button>
          <button type="button" data-ble="delete" disabled>Smazat vybranou síť…</button>
          <button type="button" data-ble="status" disabled>Obnovit stav</button>
          <button type="button" data-ble="ota-check" disabled>Zkontrolovat firmware</button>
          <button type="button" data-ble="ota-install" disabled>Nainstalovat ověřenou aktualizaci…</button>
          <button type="button" data-ble="ota-enable" disabled>Povolit automatickou instalaci…</button>
          <button type="button" data-ble="ota-disable" disabled>Vypnout automatickou instalaci…</button>
        </div>
        <p>Instalaci kufr povolí pouze v bezpečném klidu. Po restartu znovu vyhledejte BLE kufr;
          Wi-Fi a jeho samostatný provoz pokračují i po odpojení telefonu.</p>
      </section>`;
    this.client = new ProvisioningClient(bridge, (status, message, busy) => this.update(status, message, busy));
    this.element.addEventListener('click', (event) => {
      const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-ble]');
      if (!button || button.disabled) return;
      void this.action(button.dataset.ble!, button.dataset.name);
    });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) void this.client.disconnect();
    });
    this.update(null, '', false);
  }

  show(visible: boolean): void {
    this.element.hidden = !visible;
    this.client.show(visible);
  }

  async refreshLocalAddress(): Promise<void> {
    await this.client.refreshLocalAddress();
  }

  private profile(): NetworkProfile {
    return this.element.querySelector<HTMLSelectElement>('[data-profile]')!.value as NetworkProfile;
  }

  private async action(action: string, name?: string): Promise<void> {
    if (!this.nativeMode || this.busy) return;
    // Preserve the tap's target while a background BLE refresh releases its slot.
    const profile = this.profile();
    if (action === 'discover') {
      await this.client.disconnect();
      this.update(null, 'Hledám BLE kufry…', true);
      const list = this.element.querySelector<HTMLElement>('.ble-devices')!;
      list.replaceChildren();
      try {
        const result = await bridge.discover();
        for (const device of result.devices) {
          const item = document.createElement('li');
          const button = document.createElement('button');
          button.type = 'button';
          button.textContent = `Připojit a ověřit ${device.name}`;
          button.dataset.ble = 'connect';
          button.dataset.name = device.name;
          item.append(button);
          list.append(item);
        }
        this.update(null, result.devices.length ? 'Vyberte svůj kufr a zadejte jeho PoP.' : 'Žádný BLE kufr. Ověřte Bluetooth, vzdálenost a servisní okno kufru.', false);
      } catch (error) { this.update(null, errorMessage(error), false); }
      return;
    }
    if (action === 'connect' && name) { await this.client.connect(name); return; }
    if (action === 'disconnect') { await this.client.disconnect(); return; }
    if (action === 'settings') {
      await bridge.openSettings().catch((error) => this.update(null, errorMessage(error), false));
      return;
    }
    if (action === 'pair-local') { await this.client.pairLocal(this.onPaired); return; }
    if (action === 'configure') { await this.client.perform(() => bridge.configure({ profile })); return; }
    if (action === 'status') { await this.client.perform(() => bridge.status()); return; }
    if (action === 'ota-enable' || action === 'ota-disable') {
      await this.client.perform(() => bridge.command({ command: 'ota-policy', automaticInstall: action === 'ota-enable' }));
      return;
    }
    if (['test', 'delete', 'ota-check', 'ota-install'].includes(action)) {
      await this.client.perform(() => bridge.command({ command: action as 'test' | 'delete' | 'ota-check' | 'ota-install', profile }));
    }
  }

  private update(status: ProvisioningStatus | null, message: string, busy: boolean): void {
    this.busy = busy;
    if (status) this.connected = true;
    else if (!busy && message) this.connected = false;
    const messageElement = this.element.querySelector<HTMLElement>('[role="status"]')!;
    if (message && messageElement.textContent !== message) messageElement.textContent = message;
    const metrics = this.element.querySelector<HTMLElement>('.ble-metrics')!;
    if (metrics.hidden === this.connected) metrics.hidden = !this.connected;
    if (status) {
      const values: Record<string, string> = {
        device: `${status.deviceId} · ${status.firmwareVersion}`,
        network: `${status.network.phase} · ${status.network.result}`,
        active: `${status.network.activeProfile || 'žádná'} / ${status.network.lastSuccessfulProfile || 'žádná'}`,
        hotspot: status.network.hotspot.configured ? status.network.hotspot.ssid : 'nenastavený',
        fallback: status.network.fallback.configured ? status.network.fallback.ssid : 'nenastavená',
        backend: status.backendConnected ? 'připojený' : 'nepřipojený / neověřený',
        ota: `${status.ota.state}${status.ota.version ? ` · ${status.ota.version}` : ''}${status.ota.availableVersion ? ` · dostupná ${status.ota.availableVersion}` : ''}${status.ota.error ? ` · ${status.ota.error}` : ''}${status.ota.automaticInstall === undefined ? '' : status.ota.automaticInstall ? ' · automatická instalace zapnutá' : ' · automatická instalace vypnutá'}`,
      };
      for (const [key, value] of Object.entries(values)) {
        const metric = this.element.querySelector<HTMLElement>(`[data-metric="${key}"]`)!;
        if (metric.textContent !== value) metric.textContent = value;
      }
    }
    for (const button of this.element.querySelectorAll<HTMLButtonElement>('[data-ble]')) {
      const needsConnection = !['discover', 'connect', 'settings'].includes(button.dataset.ble!);
      const disabled = !this.nativeMode || busy || (needsConnection && !this.connected);
      if (button.disabled !== disabled) button.disabled = disabled;
    }
    const profile = this.element.querySelector<HTMLSelectElement>('[data-profile]')!;
    const disabled = !this.nativeMode || busy;
    if (profile.disabled !== disabled) profile.disabled = disabled;
  }
}

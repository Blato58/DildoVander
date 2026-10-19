import { button, emptyState, field } from '../../../../design-system/ui.js';
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
    this.element.tabIndex = -1;
    this.element.innerHTML = `
      <h1>Připojení kufru</h1>
      <p class="page-intro">Uložený kufr připojte přes místní Wi-Fi. Pro první připojení připravte síť, ověřte kufr přes Bluetooth a spárujte ovládání.</p>
      <nav class="section-nav" aria-label="Kroky připojení"><a href="#network-prepare">Připravit síť</a>
        <a href="#network-pair">Ověřit kufr</a><a href="#network-manage">Spravovat síť</a></nav>
      <section class="debug-card">
        <h2 id="network-prepare">1. Připravit síť</h2>
        <ol><li>V Nastavení → Osobní hotspot zapněte Povolit připojení ostatním.</li>
          <li>Zapněte Maximalizovat kompatibilitu (2,4 GHz). ESP32 nepoužívá 5GHz síť.</li>
          <li>Připravte název iPhonu (SSID) a heslo z této obrazovky. Zadejte je do zabezpečeného dialogu.</li></ol>
        <p>Aplikace hotspot sama nezapíná a jeho heslo nečte. Při prvním připojení nechte obrazovku
          Osobního hotspotu otevřenou; poté se vraťte k ověření stavu kufru.</p>
        ${button({ label: "Nastavení aplikace / oprávnění Bluetooth", attributes: {"data-ble": "settings"} })}
      </section>
      <section class="debug-card">
        <h2 id="network-pair">2. Ověřit a spárovat kufr</h2>
        <p>BLE je dostupné jen v omezeném servisním nebo obnovovacím okně. Ověření vyžaduje
          jedinečný PoP z vnitřního servisního štítku; nejde o token místního WSS.</p>
        <div class="actions">${button({ label: "Vyhledat BLE kufry", attributes: {"data-ble": "discover"} })}
          ${button({ label: "Odpojit BLE", disabled: true, attributes: {"data-ble": "disconnect"} })}</div>
        <p>Po ověření BLE spárujte místní ovládání jedním tlačítkem. Adresa, otisk a token se
          převezmou automaticky; přístup zůstane v Keychain tohoto iPhonu i při aktualizaci aplikace.</p>
        ${button({ label: "Spárovat ovládání přes Wi-Fi", disabled: true, attributes: {"data-ble": "pair-local"} })}
        <ul class="ble-devices" aria-label="Nalezené BLE kufry"></ul>
        <p class="status" data-provisioning-status role="status" aria-live="polite">${nativeMode ? 'Vyhledejte zapnutý kufr.' : 'BLE provisioning je dostupný v nativní iPhone aplikaci. Web nemá přístup k Bluetooth.'}</p>
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
        <h2 id="network-manage">3. Spravovat sítě a aktualizace</h2>
        <p>Hotspot má přednost před záložní Wi-Fi. Nové údaje nahradí staré až po úspěšném připojení;
          při chybě kufr zachová předchozí nastavení. Hesla se v telefonu neukládají.</p>
        ${field({ id: 'network-profile', label: "Upravovaná síť", control: `<select class="aw-input" data-profile><option value="hotspot">iPhone Osobní hotspot</option>
          <option value="fallback">Záložní Wi-Fi</option></select>` })}
        <div class="actions ble-network-actions">
          ${button({ label: "Nastavit / nahradit údaje", disabled: true, attributes: {"data-ble": "configure"} })}
          ${button({ label: "Otestovat připojení sítě", disabled: true, attributes: {"data-ble": "test"} })}
          ${button({ label: "Smazat vybranou síť…", disabled: true, attributes: {"data-ble": "delete"} })}
          ${button({ label: "Obnovit stav", disabled: true, attributes: {"data-ble": "status"} })}
          ${button({ label: "Zkontrolovat firmware", disabled: true, attributes: {"data-ble": "ota-check"} })}
          ${button({ label: "Nainstalovat ověřenou aktualizaci…", disabled: true, attributes: {"data-ble": "ota-install"} })}
          ${button({ label: "Povolit automatickou instalaci…", disabled: true, attributes: {"data-ble": "ota-enable"} })}
          ${button({ label: "Vypnout automatickou instalaci…", disabled: true, attributes: {"data-ble": "ota-disable"} })}
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

  async refreshLocalAddress(): Promise<boolean> {
    return this.client.refreshLocalAddress();
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
        if (result.devices.length === 0) {
          const item = document.createElement('li');
          item.innerHTML = emptyState({ title: 'Žádný BLE kufr',
            message: 'Ověřte Bluetooth, vzdálenost a servisní okno kufru. Pak hledání zopakujte.' });
          list.append(item);
        }
        for (const device of result.devices) {
          const item = document.createElement('li');
          const button = document.createElement('button');
          button.type = 'button';
          button.className = 'aw-button aw-button--secondary';
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
    const messageElement = this.element.querySelector<HTMLElement>('[data-provisioning-status]')!;
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

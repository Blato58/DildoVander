export type NetworkProfile = 'hotspot' | 'fallback';
export type ProvisioningDevice = { name: string };
export type ProvisioningStatus = {
  v: 1;
  ok: boolean;
  error?: string;
  deviceId: string;
  firmwareVersion: string;
  network: {
    phase: string;
    activeProfile: string;
    lastSuccessfulProfile: string;
    result: string;
    hotspot: { configured: boolean; ssid: string };
    fallback: { configured: boolean; ssid: string };
  };
  backendConnected: boolean;
  ota: { state: string; version?: string; availableVersion?: string; error?: string; automaticInstall?: boolean };
};

export interface ProvisioningBridge {
  discover(): Promise<{ devices: ProvisioningDevice[] }>;
  connect(options: { name: string }): Promise<ProvisioningStatus>;
  status(): Promise<ProvisioningStatus>;
  configure(options: { profile: NetworkProfile }): Promise<ProvisioningStatus>;
  command(options: { command: 'test' | 'delete' | 'ota-check' | 'ota-install' | 'ota-policy'; profile?: NetworkProfile; automaticInstall?: boolean }): Promise<ProvisioningStatus>;
  disconnect(): Promise<void>;
  openSettings(): Promise<void>;
}

// Credentials belong exclusively to the native dialog and encrypted BLE session.
// A timed-out mutation is never retried here: refresh status or reconnect first.
export class ProvisioningClient {
  private generation = 0;
  private timer: ReturnType<typeof setTimeout> | undefined;
  private connected = false;
  private pending = false;
  private visible = false;
  private poll: Promise<void> | undefined;
  private queued = false;
  constructor(
    private readonly bridge: ProvisioningBridge,
    private readonly update: (status: ProvisioningStatus | null, message: string, busy: boolean) => void,
  ) {}

  show(visible: boolean): void {
    this.visible = visible;
    this.cancelPoll();
    if (visible && this.connected && !this.pending) this.schedulePoll();
  }

  async connect(name: string): Promise<void> {
    const requestedGeneration = this.generation;
    if (this.poll && !await this.waitForPoll(requestedGeneration)) return;
    if (requestedGeneration !== this.generation) return;
    if (this.pending) return;
    const generation = ++this.generation;
    this.cancelPoll();
    this.pending = true;
    this.connected = false;
    this.update(null, 'Ověřuji kufr přes Bluetooth…', true);
    try {
      const status = await this.bridge.connect({ name });
      if (generation !== this.generation) return;
      this.connected = true;
      this.update(status, 'Zabezpečené BLE spojení. Hesla se v telefonu neukládají.', false);
    } catch (error) {
      if (generation === this.generation) this.update(null, errorMessage(error), false);
    } finally {
      if (generation === this.generation) {
        this.pending = false;
        this.schedulePoll();
      }
    }
  }

  async perform(action: () => Promise<ProvisioningStatus>): Promise<void> {
    const requestedGeneration = this.generation;
    // A tap during a background refresh waits for the BLE slot instead of
    // being dropped or starting a concurrent native request.
    if (this.poll && !await this.waitForPoll(requestedGeneration)) return;
    if (requestedGeneration !== this.generation) return;
    if (!this.connected || this.pending) return;
    const generation = this.generation;
    this.pending = true;
    this.cancelPoll();
    this.update(null, 'Čekám na potvrzení kufru…', true);
    try {
      const status = await action();
      if (generation === this.generation) {
        this.update(status, status.ok ? 'Požadavek přijat; stav se průběžně obnovuje.' : `Kufr odmítl požadavek: ${status.error ?? 'unknown'}`, false);
      }
    } catch (error) {
      if (generation === this.generation) this.update(null, errorMessage(error), false);
    } finally {
      if (generation === this.generation) {
        this.pending = false;
        this.schedulePoll();
      }
    }
  }

  async disconnect(): Promise<void> {
    ++this.generation;
    this.cancelPoll();
    this.pending = false;
    this.poll = undefined;
    this.queued = false;
    this.connected = false;
    this.update(null, 'BLE odpojeno. Samostatný kufr pokračuje bez telefonu.', false);
    await this.bridge.disconnect().catch(() => undefined);
  }

  private cancelPoll(): void {
    if (this.timer !== undefined) clearTimeout(this.timer);
    this.timer = undefined;
  }

  private async waitForPoll(generation: number): Promise<boolean> {
    if (this.queued) return false;
    this.queued = true;
    // Reserve one user action and disable further taps while its BLE slot waits.
    // Background polling alone still leaves the screen unchanged.
    this.update(null, 'Čekám na dokončení obnovy stavu kufru…', true);
    await this.poll;
    if (generation !== this.generation) return false;
    this.queued = false;
    return true;
  }

  private schedulePoll(): void {
    if (!this.connected || !this.visible || this.pending) return;
    const generation = this.generation;
    this.timer = setTimeout(() => {
      this.timer = undefined;
      this.poll = this.refresh(generation);
    }, 1000);
  }

  private async refresh(generation: number): Promise<void> {
    this.pending = true;
    try {
      const status = await this.bridge.status();
      if (generation === this.generation) this.update(status, '', this.queued);
    } catch (error) {
      if (generation === this.generation) {
        this.connected = false;
        this.update(null, errorMessage(error), false);
      }
    } finally {
      if (generation === this.generation) {
        this.pending = false;
        this.poll = undefined;
        this.schedulePoll();
      }
    }
  }
}

export function errorMessage(error: unknown): string {
  const code = typeof error === 'object' && error !== null && 'code' in error ? String(error.code) : '';
  const messages: Record<string, string> = {
    cancelled: 'Akce byla zrušena.',
    bluetooth_unavailable: 'Bluetooth není dostupné. Zapněte Bluetooth a povolte přístup této aplikaci v Nastavení.',
    provisioning_unavailable: 'Kufr není v servisním BLE okně. Ověřte jeho napájení a servisní režim.',
    authentication_failed: 'Ověření selhalo. Použijte PoP z vnitřního servisního štítku kufru.',
    credential_write_failed: 'PoP se nepodařilo bezpečně uložit. Odemkněte telefon a znovu kufr ověřte.',
    invalid_credentials: 'SSID musí mít 1–32 bajtů; heslo WPA 8–63 bajtů.',
    disconnected: 'BLE bylo odpojeno. Vyhledejte kufr a znovu jej ověřte; uložená Wi-Fi zůstává v kufru.',
    timeout: 'Kufr neodpověděl včas. Výsledek změny může být nejistý; znovu se připojte a ověřte stav.',
    invalid_response: 'Kufr vrátil neplatnou nebo nekompatibilní diagnostiku.',
    busy: 'Jiná BLE akce právě probíhá.',
  };
  return messages[code] ?? 'Požadavek selhal. Obnovte stav nebo znovu vyhledejte kufr.';
}

import type { ControlSocket } from './SimulatorDeviceConnection';

export interface NativeTransportEvent {
  connectionId: string;
  state: 'open' | 'closed' | 'frame';
  frame?: string;
  failureCode?: string;
}

const failureCodes = new Set(['invalid_identity', 'certificate_mismatch', 'authorization_rejected',
  'server_busy', 'handshake_rejected', 'tls_failed', 'host_unreachable', 'network_unavailable',
  'connection_timeout', 'protocol_error', 'connection_lost']);

export interface NativeTransportBridge {
  connect(options: { connectionId: string; host?: string; deviceId?: string }): Promise<void>;
  send(options: { connectionId: string; frame: string }): Promise<void>;
  close(options: { connectionId: string }): Promise<void>;
  addListener(
    name: 'transport',
    callback: (event: NativeTransportEvent) => void,
  ): Promise<{ remove(): Promise<void> }>;
}

// No browser socket and no credential cross this boundary. The UUID prevents
// late native events or close calls from affecting a replacement connection.
export class NativeControlSocket implements ControlSocket {
  readyState = 0;
  private readonly connectionId = crypto.randomUUID();
  private readonly listeners = new Map<string, Array<(event: { data?: unknown; failureCode?: string }) => void>>();
  private listener: { remove(): Promise<void> } | null = null;

  private readonly route?: { host: string; deviceId: string };

  constructor(private readonly bridge: NativeTransportBridge, route?: { host: string; deviceId: string }) {
    // Copy before listener registration yields: later BLE/route changes must
    // not redirect this connection attempt to a different saved hostname.
    this.route = route ? { host: route.host, deviceId: route.deviceId } : undefined;
    void this.startAsync();
  }

  private async startAsync(): Promise<void> {
    try {
      const listener = await this.bridge.addListener('transport', (event) => {
        if (event.connectionId !== this.connectionId || this.readyState === 3) { return; }
        if (event.state === 'open' && this.readyState === 0) {
          this.readyState = 1;
          this.emit('open');
        } else if (event.state === 'closed') {
          this.close(event.failureCode && failureCodes.has(event.failureCode) ? event.failureCode : undefined);
        } else if (event.state === 'frame' && this.readyState === 1) {
          this.emit('message', { data: event.frame });
        } else {
          this.close();
        }
      });
      if (this.readyState === 3) {
        await listener.remove();
        return;
      }
      this.listener = listener;
      await this.bridge.connect({ connectionId: this.connectionId, ...this.route });
      if (this.readyState === 3) {
        await this.bridge.close({ connectionId: this.connectionId });
      }
    } catch {
      this.close();
    }
  }

  addEventListener(type: string, callback: (event: { data?: unknown; failureCode?: string }) => void): void {
    const callbacks = this.listeners.get(type) ?? [];
    callbacks.push(callback);
    this.listeners.set(type, callbacks);
  }

  send(frame: string): void {
    if (this.readyState !== 1) { throw new Error('Socket is closed'); }
    void this.bridge.send({ connectionId: this.connectionId, frame }).catch(() => this.close());
  }

  close(failureCode?: string): void {
    if (this.readyState === 3) { return; }
    this.readyState = 3;
    void this.bridge.close({ connectionId: this.connectionId }).catch(() => {});
    if (this.listener !== null) {
      void this.listener.remove().catch(() => {});
      this.listener = null;
    }
    this.emit('close', failureCode ? { failureCode } : {});
    this.listeners.clear();
  }

  private emit(type: string, event: { data?: unknown; failureCode?: string } = {}): void {
    for (const callback of this.listeners.get(type) ?? []) { callback(event); }
  }
}

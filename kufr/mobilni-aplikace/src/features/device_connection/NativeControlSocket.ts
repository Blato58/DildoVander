import type { ControlSocket } from './SimulatorDeviceConnection';

export interface NativeTransportEvent {
  connectionId: string;
  state: 'open' | 'closed' | 'frame';
  frame?: string;
}

export interface NativeTransportBridge {
  connect(options: { connectionId: string }): Promise<void>;
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
  private readonly listeners = new Map<string, Array<(event: { data?: unknown }) => void>>();
  private listener: { remove(): Promise<void> } | null = null;

  constructor(private readonly bridge: NativeTransportBridge) {
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
          this.close();
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
      await this.bridge.connect({ connectionId: this.connectionId });
      if (this.readyState === 3) {
        await this.bridge.close({ connectionId: this.connectionId });
      }
    } catch {
      this.close();
    }
  }

  addEventListener(type: string, callback: (event: { data?: unknown }) => void): void {
    const callbacks = this.listeners.get(type) ?? [];
    callbacks.push(callback);
    this.listeners.set(type, callbacks);
  }

  send(frame: string): void {
    if (this.readyState !== 1) { throw new Error('Socket is closed'); }
    void this.bridge.send({ connectionId: this.connectionId, frame }).catch(() => this.close());
  }

  close(): void {
    if (this.readyState === 3) { return; }
    this.readyState = 3;
    void this.bridge.close({ connectionId: this.connectionId }).catch(() => {});
    if (this.listener !== null) {
      void this.listener.remove().catch(() => {});
      this.listener = null;
    }
    this.emit('close');
    this.listeners.clear();
  }

  private emit(type: string, event: { data?: unknown } = {}): void {
    for (const callback of this.listeners.get(type) ?? []) { callback(event); }
  }
}

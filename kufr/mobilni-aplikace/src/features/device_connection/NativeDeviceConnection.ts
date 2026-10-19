import { registerPlugin } from '@capacitor/core';
import { validateControlMessage } from '../../../../contracts/control-validator.mjs';
import type { DeviceSnapshot } from './FakeDeviceConnection';
import { NativeControlSocket, type NativeTransportBridge } from './NativeControlSocket';
import { ProtocolDeviceConnection } from './SimulatorDeviceConnection';

export interface SavedNativeDevice {
  paired: boolean;
  deviceId?: string;
  host?: string;
}

interface KufrNativeBridge extends NativeTransportBridge {
  savedDevice(): Promise<SavedNativeDevice>;
  discover(): Promise<{ failed: boolean; bleAddressAvailable?: boolean; devices: { deviceId: string; host: string; capabilities: string[] }[] }>;
  setAddress(options: { host: string }): Promise<void>;
  pair(options: { deviceId: string; host: string; spkiSha256: string }): Promise<{ deviceId: string; host: string }>;
  forget(): Promise<void>;
}

export const nativeBridge = registerPlugin<KufrNativeBridge>('KufrDeviceTransport');
const encoder = new TextEncoder();

export class NativeDeviceConnection extends ProtocolDeviceConnection {
  private readonly route: { deviceId: string; host?: string };

  constructor(onChange: (snapshot: DeviceSnapshot) => void, deviceId: string) {
    const route: { deviceId: string; host?: string } = { deviceId };
    super(onChange, {
      source: 'native-device',
      deviceId,
      autoConnect: false,
      createSocket: () => new NativeControlSocket(nativeBridge,
        route.host ? { host: route.host, deviceId: route.deviceId } : undefined),
      parseFrame: (frame) => validateControlMessage(encoder.encode(frame)) as Record<string, unknown>,
    });
    this.route = route;
  }

  override connect(host?: string): void {
    this.route.host = host;
    super.connect();
  }
}

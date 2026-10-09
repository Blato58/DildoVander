import { registerPlugin } from '@capacitor/core';
import schema from '../../../../contracts/control-message-v1.schema.json';
import { createControlValidator } from '../../../../contracts/control-validator-core.mjs';
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
const validate = createControlValidator(schema);
const encoder = new TextEncoder();

export class NativeDeviceConnection extends ProtocolDeviceConnection {
  constructor(onChange: (snapshot: DeviceSnapshot) => void, deviceId: string) {
    super(onChange, {
      source: 'native-device',
      deviceId,
      autoConnect: false,
      createSocket: () => new NativeControlSocket(nativeBridge),
      parseFrame: (frame) => validate(encoder.encode(frame)) as Record<string, unknown>,
    });
  }
}

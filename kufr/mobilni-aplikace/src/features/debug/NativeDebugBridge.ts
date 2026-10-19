import { registerPlugin } from '@capacitor/core';

// Register this native plugin once; each owning feature declares its own narrow API.
const bridge = registerPlugin('KufrDebug');
export function nativeDebugBridge<T>(): T { return bridge as T; }

export interface NativeShowRequest {
  deviceId: string;
  suffix: string;
  method: 'GET' | 'POST';
  body?: string;
  confirm?: boolean;
}
export interface NativeShowResponse { status: number; body: unknown }
export interface NativeShowBridge {
  useAccount(options: { deviceId: string }): Promise<void>;
  showRequest(options: NativeShowRequest): Promise<NativeShowResponse>;
}

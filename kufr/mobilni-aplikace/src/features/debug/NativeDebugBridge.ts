import { registerPlugin } from '@capacitor/core';

// Register this native plugin once; each owning feature declares its own narrow API.
const bridge = registerPlugin('KufrDebug');
export function nativeDebugBridge<T>(): T { return bridge as T; }

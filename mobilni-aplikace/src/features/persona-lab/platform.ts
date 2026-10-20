import { Capacitor } from '@capacitor/core';
import type { LabClientPlatform } from './contracts';

// Diagnostic origin only; this claim never establishes authorization or native acceptance.
export function labClientPlatform(): LabClientPlatform | undefined {
  const platform = Capacitor.getPlatform();
  if (platform === 'ios' && Capacitor.isNativePlatform()) return 'ios';
  if (platform === 'web' && !Capacitor.isNativePlatform()) return 'web';
  return undefined;
}

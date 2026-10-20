import { describe, expect, it, vi } from 'vitest';
import { labClientPlatform } from './platform';
const capabilities = vi.hoisted(() => ({ platform: 'web', native: false }));
vi.mock('@capacitor/core', () => ({ Capacitor: { getPlatform: () => capabilities.platform, isNativePlatform: () => capabilities.native } }));
describe('diagnostic platform claim', () => {
  it('uses Capacitor capabilities rather than a user-agent guess', () => {
    capabilities.platform = 'web'; capabilities.native = false; expect(labClientPlatform()).toBe('web');
    capabilities.platform = 'ios'; capabilities.native = true; expect(labClientPlatform()).toBe('ios');
    capabilities.platform = 'ios'; capabilities.native = false; expect(labClientPlatform()).toBeUndefined();
    capabilities.platform = 'android'; capabilities.native = true; expect(labClientPlatform()).toBeUndefined();
  });
});

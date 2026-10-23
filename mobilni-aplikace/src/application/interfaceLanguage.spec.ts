import { describe, expect, it, vi } from 'vitest';
import {
  createInterfaceLanguagePreference,
  type InterfaceLanguageStorage,
} from './interfaceLanguage';

class MemoryStorage implements InterfaceLanguageStorage {
  private readonly values = new Map<string, string>();
  writes = 0;
  getItem(key: string) { return this.values.get(key) ?? null; }
  setItem(key: string, value: string) { this.writes += 1; this.values.set(key, value); }
}

describe('interface language preference', () => {
  it('keeps signed-out device and account choices isolated across switches and restart', () => {
    const storage = new MemoryStorage();
    const preference = createInterfaceLanguagePreference(storage);
    expect(preference.language.value).toBe('cs');
    expect(preference.scope.value).toEqual({ kind: 'device' });
    expect(preference.persistenceStatus.value).toBe('unverified');

    preference.setLanguage('en');
    expect(preference.persistenceStatus.value).toBe('saved');
    const writesBeforeScopeSwitches = storage.writes;
    preference.setScope('account-a');
    expect(preference.language.value).toBe('cs');
    preference.setLanguage('en');
    preference.setScope('account-b');
    expect(preference.language.value).toBe('cs');
    expect(storage.writes).toBe(writesBeforeScopeSwitches + 1);
    preference.setLanguage('cs');
    preference.setScope('account-a');
    expect(preference.language.value).toBe('en');
    preference.setScope(null);
    expect(preference.language.value).toBe('en');

    const restarted = createInterfaceLanguagePreference(storage);
    expect(restarted.language.value).toBe('en');
    restarted.setScope('account-a');
    expect(restarted.language.value).toBe('en');
    restarted.setScope('account-b');
    expect(restarted.language.value).toBe('cs');
  });

  it('falls back to Czech for malformed or unsupported stored values without copying them', () => {
    const storage = new MemoryStorage();
    const preference = createInterfaceLanguagePreference(storage);
    preference.setLanguage('en');
    preference.setScope('unsupported-value');
    storage.setItem('dildovander.interface-language.v1.account.unsupported-value', 'fr');

    preference.setScope('unsupported-value');
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('unverified');
    expect(preference.setLanguage('de')).toBe(false);
    expect(preference.language.value).toBe('cs');
  });

  it('remains usable when browser storage is denied or throws on reads and writes', () => {
    const deniedStorage: InterfaceLanguageStorage = {
      getItem() { throw new Error('private browsing denied'); },
      setItem() { throw new Error('private browsing denied'); },
    };
    const preference = createInterfaceLanguagePreference(deniedStorage);
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('unavailable');
    expect(preference.setLanguage('en')).toBe(true);
    expect(preference.language.value).toBe('en');
    expect(preference.persistenceStatus.value).toBe('unavailable');
    preference.setScope('account-a');
    expect(preference.language.value).toBe('cs');
    expect(preference.persistenceStatus.value).toBe('unavailable');
    preference.setLanguage('en');
    preference.setScope('account-b');
    expect(preference.language.value).toBe('cs');
    preference.setLanguage('en');
    preference.setScope('account-a');
    expect(preference.language.value).toBe('en');
    preference.setScope(null);
    expect(preference.language.value).toBe('en');
    preference.setLanguage('en');
    preference.setScope('account-a');
    expect(preference.language.value).toBe('en');
  });

  it('uses the Czech runtime default when accessing localStorage itself throws', () => {
    const deniedWindow = Object.defineProperty({}, 'localStorage', {
      configurable: true,
      get() { throw new Error('private browsing denied'); },
    });
    vi.stubGlobal('window', deniedWindow);
    try {
      const preference = createInterfaceLanguagePreference();
      expect(preference.language.value).toBe('cs');
      expect(preference.persistenceStatus.value).toBe('unavailable');
      expect(preference.setLanguage('en')).toBe(true);
      expect(preference.language.value).toBe('en');
      expect(preference.persistenceStatus.value).toBe('unavailable');
    } finally {
      vi.unstubAllGlobals();
    }
  });
});

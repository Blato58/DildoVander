import { computed, ref, type ComputedRef } from 'vue';

export const interfaceLanguages = [
  { value: 'cs', label: 'Čeština' },
  { value: 'en', label: 'English' },
] as const;

export type InterfaceLanguage = typeof interfaceLanguages[number]['value'];

export type InterfaceLanguageScope =
  | { readonly kind: 'device' }
  | { readonly kind: 'account'; readonly accountId: string };

export type InterfaceLanguagePersistenceStatus = 'saved' | 'unverified' | 'unavailable';

export interface InterfaceLanguageStorage {
  getItem(key: string): string | null;
  setItem(key: string, value: string): void;
}

export interface InterfaceLanguagePreference {
  readonly language: ComputedRef<InterfaceLanguage>;
  readonly scope: ComputedRef<InterfaceLanguageScope>;
  readonly persistenceStatus: ComputedRef<InterfaceLanguagePersistenceStatus>;
  /** Load a scope without copying or persisting the previous scope's choice. */
  setScope(accountId: string | null | undefined): void;
  /** Update the active scope; unsupported values are ignored and return false. */
  setLanguage(value: unknown): boolean;
}

const storagePrefix = 'dildovander.interface-language.v1';
const defaultLanguage: InterfaceLanguage = 'cs';

function isInterfaceLanguage(value: unknown): value is InterfaceLanguage {
  return value === 'cs' || value === 'en';
}

function getBrowserStorage(): InterfaceLanguageStorage | undefined {
  try {
    return typeof window === 'undefined' ? undefined : window.localStorage;
  } catch {
    // Browser privacy settings may deny access even when localStorage exists.
    return undefined;
  }
}

function keyForScope(scope: InterfaceLanguageScope): string {
  return scope.kind === 'device'
    ? `${storagePrefix}.device`
    : `${storagePrefix}.account.${encodeURIComponent(scope.accountId)}`;
}

function readScope(storage: InterfaceLanguageStorage | undefined, scope: InterfaceLanguageScope): {
  language: InterfaceLanguage;
  persistenceStatus: InterfaceLanguagePersistenceStatus;
} {
  if (!storage) return { language: defaultLanguage, persistenceStatus: 'unavailable' };
  try {
    const stored = storage.getItem(keyForScope(scope));
    return isInterfaceLanguage(stored)
      ? { language: stored, persistenceStatus: 'saved' }
      : { language: defaultLanguage, persistenceStatus: 'unverified' };
  } catch {
    return { language: defaultLanguage, persistenceStatus: 'unavailable' };
  }
}

/**
 * Local-only interface preference. Account choices are isolated by account ID;
 * signed-out visitors use a distinct device choice. Changing scope only reads.
 */
export function createInterfaceLanguagePreference(
  storage: InterfaceLanguageStorage | undefined = getBrowserStorage(),
): InterfaceLanguagePreference {
  const activeScope = ref<InterfaceLanguageScope>({ kind: 'device' });
  const activeKey = ref(keyForScope(activeScope.value));
  const initial = readScope(storage, activeScope.value);
  const activeLanguage = ref<InterfaceLanguage>(initial.language);
  const activePersistenceStatus = ref<InterfaceLanguagePersistenceStatus>(initial.persistenceStatus);
  const languageByScope = new Map<string, InterfaceLanguage>([[activeKey.value, initial.language]]);
  const persistenceByScope = new Map<string, InterfaceLanguagePersistenceStatus>([[activeKey.value, initial.persistenceStatus]]);

  function setScope(accountId: string | null | undefined): void {
    const scope: InterfaceLanguageScope = accountId?.trim()
      ? { kind: 'account', accountId: accountId.trim() }
      : { kind: 'device' };
    activeScope.value = scope;
    activeKey.value = keyForScope(scope);
    if (languageByScope.has(activeKey.value)) {
      activeLanguage.value = languageByScope.get(activeKey.value)!;
      activePersistenceStatus.value = persistenceByScope.get(activeKey.value) ?? 'unverified';
      return;
    }
    const loaded = readScope(storage, scope);
    activeLanguage.value = loaded.language;
    activePersistenceStatus.value = loaded.persistenceStatus;
    languageByScope.set(activeKey.value, loaded.language);
    persistenceByScope.set(activeKey.value, loaded.persistenceStatus);
  }

  function setLanguage(value: unknown): boolean {
    if (!isInterfaceLanguage(value)) return false;
    activeLanguage.value = value;
    languageByScope.set(activeKey.value, value);
    try {
      if (!storage) throw new Error('Storage unavailable');
      storage.setItem(activeKey.value, value);
      activePersistenceStatus.value = 'saved';
    } catch {
      // Keep the choice for this runtime even when persistence is unavailable.
      activePersistenceStatus.value = 'unavailable';
    }
    persistenceByScope.set(activeKey.value, activePersistenceStatus.value);
    return true;
  }

  return {
    language: computed(() => activeLanguage.value),
    scope: computed(() => activeScope.value),
    persistenceStatus: computed(() => activePersistenceStatus.value),
    setScope,
    setLanguage,
  };
}

/** Shared app instance for the header and any future localized views/catalogs. */
export const interfaceLanguagePreference = createInterfaceLanguagePreference();

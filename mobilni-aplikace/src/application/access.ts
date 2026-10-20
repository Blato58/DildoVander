import type { Account } from '../features/identity/session';

declare module 'vue-router' {
  interface RouteMeta {
    requiresAccount?: boolean;
    operation?: boolean;
    administrator?: boolean;
    offlineLocal?: boolean;
  }
}

export interface AccessMeta { requiresAccount?: unknown; operation?: unknown; administrator?: unknown; offlineLocal?: unknown }
export type AccessDenial = 'signin' | 'operator' | 'administrator' | 'online';

export function canOperateSuitcase(account: Account | null): boolean {
  return !!account && account.roles.some(role => ['Owner', 'Administrator', 'Operator'].includes(role));
}

export function accessDenial(meta: AccessMeta, account: Account | null, offline = false): AccessDenial | null {
  if (!meta.requiresAccount) return null;
  if (!account) return 'signin';
  if (offline && !meta.offlineLocal) return 'online';
  if (meta.administrator && !account.roles.some(role => ['Owner', 'Administrator'].includes(role))) return 'administrator';
  if (meta.operation && !canOperateSuitcase(account)) return 'operator';
  return null;
}

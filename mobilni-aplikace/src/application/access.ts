import type { Account } from '../features/identity/session';

declare module 'vue-router' {
  interface RouteMeta {
    requiresAccount?: boolean;
    operation?: boolean;
    administrator?: boolean;
    offlineLocal?: boolean;
    customer?: boolean;
    staff?: boolean;
    tester?: boolean;
    lab?: boolean;
  }
}

export interface AccessMeta { requiresAccount?: unknown; operation?: unknown; administrator?: unknown; offlineLocal?: unknown; customer?: unknown; staff?: unknown; tester?: unknown; lab?: unknown }
export type AccessDenial = 'signin' | 'operator' | 'administrator' | 'online' | 'customer' | 'staff' | 'tester';

export function isCustomerAccount(account: Account | null): boolean {
  return !!account?.roles.includes('Customer')
    && !account.roles.some(role => ['Owner', 'Administrator', 'Operator', 'Viewer'].includes(role));
}

export function canEnterStaffArea(account: Account | null): boolean {
  return !!account && account.roles.some(role => ['Owner', 'Administrator', 'Operator', 'Viewer'].includes(role));
}

export function canOperateSuitcase(account: Account | null): boolean {
  return !!account && account.roles.some(role => ['Owner', 'Administrator', 'Operator'].includes(role));
}

export function accessDenial(meta: AccessMeta, account: Account | null, offline = false): AccessDenial | null {
  if (!meta.requiresAccount) return null;
  if (!account) return 'signin';
  if (meta.tester && !account.roles.includes('Tester')) return 'tester';
  if (meta.lab && !canEnterStaffArea(account) && !account.roles.includes('Tester')) return 'staff';
  if (meta.staff && !canEnterStaffArea(account)) return 'staff';
  if (offline && !meta.offlineLocal) return 'online';
  if (meta.administrator && !account.roles.some(role => ['Owner', 'Administrator'].includes(role))) return 'administrator';
  if (meta.operation && !canOperateSuitcase(account)) return 'operator';
  return null;
}

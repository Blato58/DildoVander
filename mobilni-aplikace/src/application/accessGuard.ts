import type { Router, RouteLocationNormalized } from 'vue-router';
import { account, offlineSession, offlineSessionExpiresAt, refreshIdentity } from '../features/identity/session';
import { accessDenial } from './access';
import { safeReturnPath } from './navigation';

export function accessRedirect(route: Pick<RouteLocationNormalized, 'meta' | 'fullPath'>) {
  const denied = accessDenial(route.meta, account.value, offlineSession.value);
  if (denied === 'signin') return { path: '/signin', query: { return: safeReturnPath(route.fullPath) }, replace: true };
  if (denied === 'staff') return { path: '/me/character', replace: true };
  if (denied) return { path: '/', query: { access: denied }, replace: true };
  return null;
}

export function installAccessGuard(router: Router): void {
  router.beforeEach(async to => {
    if (!to.meta.requiresAccount) return;
    if (to.meta.offlineLocal && offlineSession.value && offlineSessionExpiresAt.value > Date.now()) return accessRedirect(to) ?? undefined;
    // A route transition checks the server session, not a remembered UI role.
    try { await refreshIdentity(); } catch { return { path: '/signin', query: { return: safeReturnPath(to.fullPath) }, replace: true }; }
    return accessRedirect(to) ?? undefined;
  });
}

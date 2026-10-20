import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import { account, offlineSession, offlineSessionExpiresAt, refreshIdentity } from '../src/features/identity/session';
import { installAccessGuard } from '../src/application/accessGuard';
import { accessDenial } from '../src/application/access';

vi.mock('../src/features/identity/session', async () => {
  const { ref } = await import('vue');
  return { account: ref(null), offlineSession: ref(false), offlineSessionExpiresAt: ref(0), refreshIdentity: vi.fn(async () => {}) };
});
const operator = { id: 'operator', displayName: 'Operator', roles: ['Operator'] };
function guardedRouter() {
  const router = createRouter({ history: createMemoryHistory(), routes: [
    { path: '/', component: {} }, { path: '/signin', component: {} },
    { path: '/workshop', component: {} }, { path: '/archive', component: {} },
    { path: '/suitcase', component: {}, meta: { requiresAccount: true, operation: true, offlineLocal: true } },
    { path: '/connections', component: {}, meta: { requiresAccount: true, operation: true, offlineLocal: true } },
    { path: '/suitcase/director', component: {}, meta: { requiresAccount: true, operation: true } },
    { path: '/settings/admin', component: {}, meta: { requiresAccount: true, administrator: true } },
  ] });
  installAccessGuard(router);
  return router;
}
beforeEach(() => { account.value = null; offlineSession.value = false; offlineSessionExpiresAt.value = 0; vi.mocked(refreshIdentity).mockReset().mockResolvedValue(); });
describe('operational route authentication', () => {
  it('re-enters local controls offline without WAN but rejects cloud views', async () => {
    account.value = operator; offlineSession.value = true; offlineSessionExpiresAt.value = Date.now() + 60000;
    const router = guardedRouter();
    for (const path of ['/connections', '/suitcase']) { await router.push(path); expect(router.currentRoute.value.path).toBe(path); }
    expect(refreshIdentity).not.toHaveBeenCalled();
    await router.push('/suitcase/director');
    expect(router.currentRoute.value.query.access).toBe('online');
    expect(refreshIdentity).toHaveBeenCalledOnce();
  });
  it.each(['/suitcase', '/connections', '/settings/admin'])('requires a verified session for direct URL %s', async path => {
    const router = guardedRouter(); await router.push(path + '?view=details');
    expect(router.currentRoute.value.path).toBe('/signin');
    expect(router.currentRoute.value.query.return).toBe(path + '?view=details');
  });
  it('keeps public landing/models/archive independent of account availability', async () => {
    const router = guardedRouter();
    for (const path of ['/', '/workshop', '/archive']) { await router.push(path); expect(router.currentRoute.value.path).toBe(path); }
    expect(refreshIdentity).not.toHaveBeenCalled();
  });
  it('accepts an operator but rejects a viewer and an operator opening administration', async () => {
    const router = guardedRouter(); account.value = operator;
    await router.push('/suitcase'); expect(router.currentRoute.value.path).toBe('/suitcase');
    await router.push('/settings/admin'); expect(router.currentRoute.value.query.access).toBe('administrator');
    account.value = { ...operator, roles: ['Viewer'] };
    await router.push('/connections'); expect(router.currentRoute.value.query.access).toBe('operator');
  });
  it('fails closed when a remembered account loses its server session', async () => {
    account.value = operator;
    vi.mocked(refreshIdentity).mockImplementation(async () => { account.value = null; });
    const router = guardedRouter(); await router.push('/suitcase');
    expect(router.currentRoute.value.path).toBe('/signin');
  });
  it('fails closed on session verification errors even with a remembered account', async () => {
    account.value = operator; vi.mocked(refreshIdentity).mockRejectedValue(new Error('offline'));
    const router = guardedRouter(); await router.push('/suitcase');
    expect(router.currentRoute.value.path).toBe('/signin');
  });
  it('rejects active route rendering immediately after logout or role removal', () => {
    const meta = { requiresAccount: true, operation: true };
    expect(accessDenial(meta, operator)).toBeNull();
    expect(accessDenial(meta, null)).toBe('signin');
    expect(accessDenial(meta, { ...operator, roles: ['Viewer'] })).toBe('operator');
  });
});

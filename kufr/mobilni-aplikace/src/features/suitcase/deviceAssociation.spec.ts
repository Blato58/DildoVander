import { beforeEach, describe, expect, it, vi } from 'vitest';
import { nextTick } from 'vue';
import { flushPromises } from '@vue/test-utils';
const request = vi.hoisted(() => vi.fn());
vi.mock('../identity/session', async () => ({ account: (await import('vue')).ref(null), accountRequest: request }));
vi.mock('./deviceSession', async () => ({ deviceSession: { native: true, savedDevice: (await import('vue')).shallowRef({ paired: false }) } }));
let cloud: (typeof import('./deviceAssociation'))['deviceAssociation'];
let identity: typeof import('../identity/session');
let device: (typeof import('./deviceSession'))['deviceSession'];
const readGrant = { id: 'g-1', userId: 'u-1', resourceType: 'device', resourceId: 'kufr-1', permission: 'read' };
let details: { grants: typeof readGrant[]; associations: Array<{ id: string; deviceId: string; alias: string; installationId: string; createdAt: number }>; sessions: Array<{ id: string; current: boolean }> };
beforeEach(async () => {
  vi.resetModules(); request.mockReset();
  details = { grants: [], associations: [], sessions: [{ id: 'install-1', current: true }] };
  request.mockImplementation(async path => path === '/app/v1/account' ? structuredClone(details) : undefined);
  identity = await import('../identity/session'); device = (await import('./deviceSession')).deviceSession;
  cloud = (await import('./deviceAssociation')).deviceAssociation;
});
async function signIn(grants = [readGrant]): Promise<void> {
  details.grants = grants; device.savedDevice.value = { paired: true, deviceId: 'kufr-1', host: 'local' };
  identity.account.value = { id: 'u-1', displayName: 'Operator', roles: ['Operator'] };
  await nextTick(); await flushPromises(); await cloud.refresh();
}
describe('account association composes grants with independent physical pairing', () => {
  it('local pairing and a grant for another exact ID cannot create an association or operate grant', async () => {
    await signIn([{ ...readGrant, resourceId: 'other-kufr', permission: 'manage' }]); request.mockClear();
    await cloud.attach('Main trunk'); expect(cloud.canRead.value).toBe(false); expect(cloud.canOperate.value).toBe(false);
    expect(request).not.toHaveBeenCalled();
  });
  it('a read grant allows only an alias association; it does not imply operate permission', async () => {
    await signIn(); expect(cloud.canRead.value).toBe(true); expect(cloud.canOperate.value).toBe(false);
    await cloud.attach('Hlavní kufr');
    expect(request).toHaveBeenCalledWith('/app/v1/devices/kufr-1/association', 'POST', { alias: 'Hlavní kufr' });
    expect(cloud.canOperate.value).toBe(false); expect(device.savedDevice.value.paired).toBe(true);
  });
  it('a late cloud response cannot restore previous-account grants after logout', async () => {
    await signIn(); let finish!: (value: unknown) => void;
    request.mockReturnValueOnce(new Promise(resolve => { finish = resolve; }));
    const reading = cloud.refresh(); identity.account.value = null; await nextTick(); finish(details); await reading;
    expect(cloud.details.value).toBeNull(); expect(cloud.canRead.value).toBe(false);
  });
  it('offline association removal remains unconfirmed and retains the separate local identity', async () => {
    details.associations = [{ id: 'a-1', deviceId: 'kufr-1', alias: 'Main', installationId: 'install-1', createdAt: 1 }];
    await signIn(); request.mockRejectedValueOnce(new Error('Offline')); await cloud.remove();
    expect(cloud.association.value?.id).toBe('a-1'); expect(cloud.message.value).toContain('považujte za nezměněnou');
    expect(device.savedDevice.value.paired).toBe(true);
  });
});

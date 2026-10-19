import { computed, ref, shallowRef, watch } from 'vue';
import { account, accountRequest, type ResourceGrant } from '../identity/session';
import { deviceSession } from './deviceSession';

interface Association { id: string; deviceId: string; alias: string; installationId: string; createdAt: number }
interface DeviceAccountDetails { grants: ResourceGrant[]; associations: Association[]; sessions: Array<{ id: string; current: boolean }> }
const details = shallowRef<DeviceAccountDetails | null>(null);
const pending = ref(false);
const message = ref('Přihlaste se pro správu vazby na účet. Místní párování funguje nezávisle.');
let generation = 0;
const deviceId = computed(() => deviceSession.savedDevice.value.paired ? deviceSession.savedDevice.value.deviceId : undefined);
const administrator = computed(() => account.value?.roles.some(role => role === 'Owner' || role === 'Administrator') === true);
const permissions = computed(() => details.value?.grants.filter(grant => grant.resourceType === 'device' && grant.resourceId === deviceId.value).map(grant => grant.permission) ?? []);
const canRead = computed(() => !!details.value && (administrator.value || permissions.value.some(permission => ['read', 'operate', 'manage'].includes(permission))));
const canOperate = computed(() => !!details.value && (administrator.value || (account.value?.roles.includes('Operator') === true && permissions.value.some(permission => ['operate', 'manage'].includes(permission)))));
const association = computed(() => {
  const installation = details.value?.sessions.find(session => session.current)?.id;
  return details.value?.associations?.find(item => item.deviceId === deviceId.value && item.installationId === installation) ?? null;
});

async function refresh(): Promise<void> {
  const requested = ++generation;
  const userId = account.value?.id;
  details.value = null;
  if (!userId) { message.value = 'Přihlaste se pro správu vazby na účet. Místní párování funguje nezávisle.'; return; }
  pending.value = true;
  try {
    const value = await accountRequest<DeviceAccountDetails>('/app/v1/account');
    if (requested !== generation || account.value?.id !== userId) return;
    details.value = value; message.value = 'Cloudová oprávnění a vazba instalace byly ověřeny serverem.';
  } catch (error) {
    if (requested === generation) message.value = error instanceof Error ? error.message : 'Cloud nelze ověřit. Poslední místní párování neopravňuje ke cloudovým akcím.';
  } finally { if (requested === generation) pending.value = false; }
}
async function change(method: 'POST' | 'DELETE', alias?: string): Promise<void> {
  if (pending.value || !account.value || !deviceId.value || !deviceSession.native || (method === 'POST' && !canRead.value)) return;
  const requested = generation;
  const userId = account.value.id;
  const target = deviceId.value;
  pending.value = true;
  try {
    await accountRequest(`/app/v1/devices/${encodeURIComponent(target)}/association`, method, method === 'POST' ? { alias: alias?.trim() ?? '' } : undefined);
    if (requested !== generation || userId !== account.value?.id || target !== deviceId.value) return;
    pending.value = false;
    await refresh();
    if (account.value?.id === userId && target === deviceId.value && details.value) message.value = method === 'POST'
      ? 'Kufr přiřazen k této instalaci. Vazba nevytváří další oprávnění ani nové párování.'
      : 'Cloudová vazba odebrána. Místní párování v Keychain a oprávnění přidělená správcem zůstávají samostatná.';
  } catch (error) {
    if (requested === generation) message.value = (error instanceof Error ? error.message : 'Server změnu nepotvrdil.') + ' Dokud server změnu nepotvrdí, cloudovou vazbu považujte za nezměněnou.';
  } finally { if (requested === generation) pending.value = false; }
}
watch([() => account.value?.id, deviceId], () => { pending.value = false; void refresh(); });

export const deviceAssociation = {
  account, pending, message, details, permissions, canRead, canOperate, administrator, association, refresh,
  attach: (alias: string) => change('POST', alias), remove: () => change('DELETE'),
};

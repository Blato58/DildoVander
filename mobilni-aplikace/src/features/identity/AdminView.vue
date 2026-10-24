<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import OperationsPanel from './OperationsPanel.vue';
import ModelReleasesPanel from './ModelReleasesPanel.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountOrigin, accountRequest, authenticatePasskey, refreshIdentity, hasRole, type Account, type ResourceGrant } from './session';
interface ManagedAccount extends Account { disabled: boolean }
const users = ref<ManagedAccount[]>([]); const grants = ref<ResourceGrant[]>([]);
const error = ref(''); const busy = ref(false); const inviteRole = ref('Viewer'); const recoveryId = ref(''); const token = ref('');
const message = ref(''); const failureCode = ref(''); const failureStatus = ref<number | null>(null);
let epoch = 0;
function showError(cause: unknown): void {
  error.value = cause instanceof Error ? cause.message : 'Akci účtu se nepodařilo dokončit.';
  const detail = cause as { code?: unknown; status?: unknown } | null;
  failureCode.value = typeof detail?.code === 'string' && /^[a-z][a-z0-9_]{0,79}$/.test(detail.code) ? detail.code : '';
  failureStatus.value = typeof detail?.status === 'number' && detail.status >= 400 && detail.status <= 599 ? detail.status : null;
}
const selectedUser = ref(''); const resourceType = ref('device'); const resourceId = ref(''); const permission = ref('read');
const roles = ['Owner', 'Administrator', 'Operator', 'Viewer', 'Customer', 'Tester'];
const invitationKind = ref('invite');
const invitationLink = computed(() => token.value && accountOrigin.value ? `${accountOrigin.value}/signin#${invitationKind.value}=${encodeURIComponent(token.value)}` : '');
async function load() {
  const current = epoch;
  await refreshIdentity();
  if (current !== epoch || !hasRole('Administrator')) return;
  const data = await accountRequest<{ users: ManagedAccount[]; grants: ResourceGrant[] }>('/app/v1/admin/users');
  if (current !== epoch) return;
  users.value = data.users; grants.value = data.grants;
}
onMounted(async () => { const current = epoch; try { await load(); } catch (e) { if (current === epoch) showError(e); } });
watch(() => `${account.value?.id}:${account.value?.roles.join(',')}`, () => {
  epoch++; token.value = ''; users.value = []; grants.value = []; error.value = ''; message.value = '';
  inviteRole.value = 'Viewer'; recoveryId.value = ''; selectedUser.value = ''; resourceId.value = '';
  failureCode.value = ''; failureStatus.value = null; busy.value = false;
}, { flush: 'sync' });
onBeforeUnmount(() => { epoch++; token.value = ''; });
async function action(work: () => Promise<unknown>, reload = true) {
  if (busy.value) return;
  const current = epoch;
  busy.value = true; error.value = ''; message.value = ''; failureCode.value = ''; failureStatus.value = null;
  try {
    await work();
    if (current !== epoch) return;
    if (reload) {
      try { await load(); }
      catch { if (current === epoch) message.value = 'Změna byla potvrzena serverem, ale seznam se nepodařilo obnovit. Obnovte stránku; změnu neopakujte.'; }
    }
  } catch (e) { if (current === epoch) showError(e); }
  finally { if (current === epoch) busy.value = false; }
}
async function invite() {
  if (busy.value) return;
  const current = epoch;
  token.value = '';
  await action(async () => {
    const result = await accountRequest<{ token: string; purpose: string }>('/app/v1/admin/invitations', 'POST', { role: inviteRole.value, recoveryUserId: recoveryId.value || null });
    if (current !== epoch) return;
    if (typeof result?.token !== 'string' || !/^[A-Za-z0-9_-]{20,256}$/.test(result.token)
      || !['invite', 'recovery'].includes(result.purpose)) throw new Error('Server nevrátil platnou pozvánku. Vytvoření nebylo potvrzeno.');
    token.value = result.token;
    invitationKind.value = result.purpose === 'recovery' ? 'recovery' : 'invite';
    message.value = 'Jednorázový odkaz byl vytvořen. Předejte ho soukromě účastníkovi.';
  }, false);
}
async function reauthenticate() {
  const current = epoch;
  await action(async () => { await authenticatePasskey('reauth'); if (current === epoch) message.value = 'Totožnost byla ověřena. Původní akci nyní odešlete znovu.'; }, false);
}
</script>
<template>
  <section class="page-stack" aria-labelledby="admin-title">
    <header><p class="eyebrow">Správa aplikace</p><h1 id="admin-title">Uživatelé a oprávnění</h1><p>Vlastník řídí účty a obnovu. Správci přidělují přístup ke konkrétním prostředkům.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}
      <p v-if="failureStatus || failureCode">{{ failureStatus ? `HTTP ${failureStatus}` : '' }}{{ failureStatus && failureCode ? ' · ' : '' }}{{ failureCode }}</p>
      <AwButton v-if="failureCode === 'recent_passkey_required'" :busy="busy" @click="reauthenticate">Ověřit totožnost přístupovým klíčem</AwButton>
    </AwNotice>
    <AwNotice v-if="message" tone="info" role="status">{{ message }}</AwNotice>
    <AwNotice v-if="!hasRole('Administrator')" tone="warning">Tato stránka vyžaduje oprávnění správce. <RouterLink to="/settings">Zpět na účet</RouterLink></AwNotice>
    <template v-else>
      <p><RouterLink :to="{path:'/signin',query:{return:'/settings/admin'}}">Znovu ověřit účet přístupovým klíčem</RouterLink></p>
      <OperationsPanel />
      <ModelReleasesPanel />
      <section v-if="hasRole('Owner')" class="aw-card"><h2>Pozvánka nebo obnova</h2><form class="identity-form" @submit.prevent="invite"><label for="invite-role">Role nového účtu</label><select id="invite-role" v-model="inviteRole" class="aw-select"><option v-for="role in roles" :key="role">{{ role }}</option></select><label for="recovery-account">Obnova existujícího účtu</label><select id="recovery-account" v-model="recoveryId" class="aw-select"><option value="">Vytvořit nový účet</option><option v-for="user in users.filter(u => !u.disabled)" :key="user.id" :value="user.id">{{ user.displayName }}</option></select><p v-if="recoveryId">Obnova po ověření nového klíče odebere původní klíče a ukončí všechny relace daného účtu.</p><AwButton type="submit" :busy="busy">{{ recoveryId ? 'Vytvořit kód obnovy' : 'Vytvořit pozvánku' }}</AwButton></form><div v-if="token" class="invite-output"><p>Kód platí jednu hodinu a lze ho použít jednou. Předávejte ho soukromě. Znovu ho nelze zobrazit.</p><label v-if="invitationLink" for="invitation-link">Soukromý odkaz pro přijetí</label><input v-if="invitationLink" id="invitation-link" :value="invitationLink" readonly class="aw-input" autocomplete="off" /><label for="invitation-token">Jednorázový kód</label><input id="invitation-token" :value="token" readonly class="aw-input" autocomplete="off" /><AwButton @click="token = ''">Skrýt kód</AwButton></div></section>
      <section class="aw-card"><h2>Účty</h2><p v-if="!users.length">Žádné účty k zobrazení.</p><ul class="identity-list"><li v-for="user in users" :key="user.id"><h3>{{ user.displayName }} <small v-if="user.id === account?.id">· váš účet</small></h3><form v-if="hasRole('Owner')" class="user-controls" @submit.prevent="action(() => accountRequest(`/app/v1/admin/users/${encodeURIComponent(user.id)}`, 'PUT', { role: user.roles[0], disabled: user.disabled }))"><label :for="`role-${user.id}`">Role</label><select :id="`role-${user.id}`" v-model="user.roles[0]" class="aw-select"><option v-for="role in roles" :key="role">{{ role }}</option></select><label><input v-model="user.disabled" type="checkbox" /> Účet je zablokován</label><AwButton type="submit" :disabled="busy">Uložit a odhlásit relace</AwButton></form><p v-else>{{ user.roles.join(', ') }} · {{ user.disabled ? 'zablokován' : 'aktivní' }}</p></li></ul></section>
      <section class="aw-card"><h2>Přidělit prostředek</h2><form class="identity-form" @submit.prevent="action(() => accountRequest('/app/v1/admin/grants', 'POST', { userId: selectedUser, resourceType, resourceId, permission }))"><label for="grant-user">Uživatel</label><select id="grant-user" v-model="selectedUser" required class="aw-select"><option value="" disabled>Vyberte účet</option><option v-for="user in users.filter(u => !u.disabled)" :key="user.id" :value="user.id">{{ user.displayName }}</option></select><label for="grant-type">Typ prostředku</label><select id="grant-type" v-model="resourceType" class="aw-select"><option value="device">Kufr</option><option value="workshop">Dílna</option></select><label for="grant-id">Identifikátor prostředku</label><input id="grant-id" v-model="resourceId" required maxlength="128" class="aw-input" /><label for="grant-permission">Oprávnění</label><select id="grant-permission" v-model="permission" class="aw-select"><option value="read">Prohlížet</option><option value="operate">Ovládat</option><option value="manage">Spravovat</option></select><AwButton type="submit" :busy="busy">Přidělit</AwButton></form><ul class="identity-list"><li v-for="grant in grants" :key="grant.id"><p>{{ users.find(u => u.id === grant.userId)?.displayName ?? grant.userId }} · {{ grant.resourceType }} · {{ grant.resourceId }} · {{ grant.permission }}</p><AwButton variant="danger" :disabled="busy" @click="action(() => accountRequest(`/app/v1/admin/grants/${encodeURIComponent(grant.id)}`, 'DELETE'))">Odebrat oprávnění</AwButton></li></ul></section>
    </template>
  </section>
</template>
<style scoped>
.identity-form { display: grid; gap: .75rem; max-width: 36rem; }.identity-list { list-style: none; padding: 0; }.identity-list li { border-top: 1px solid var(--aw-border-subtle); padding-block: 1rem; }.user-controls { display: flex; align-items: center; flex-wrap: wrap; gap: .75rem; }.invite-output { margin-top: 1.5rem; display: grid; gap: .75rem; }small { font-size: .8rem; }
</style>

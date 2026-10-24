<script setup lang="ts">
import { computed, onMounted, ref } from 'vue';
import OperationsPanel from './OperationsPanel.vue';
import ModelReleasesPanel from './ModelReleasesPanel.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountOrigin, accountRequest, refreshIdentity, hasRole, type Account, type ResourceGrant } from './session';
interface ManagedAccount extends Account { disabled: boolean }
const users = ref<ManagedAccount[]>([]); const grants = ref<ResourceGrant[]>([]);
const error = ref(''); const busy = ref(false); const inviteRole = ref('Viewer'); const recoveryId = ref(''); const token = ref('');
const selectedUser = ref(''); const resourceType = ref('device'); const resourceId = ref(''); const permission = ref('read');
const roles = ['Owner', 'Administrator', 'Operator', 'Viewer', 'Customer', 'Tester'];
const invitationKind = ref('invite');
const invitationLink = computed(() => token.value && accountOrigin.value ? `${accountOrigin.value}/signin#${invitationKind.value}=${encodeURIComponent(token.value)}` : '');
async function load() {
  await refreshIdentity();
  if (!hasRole('Administrator')) return;
  const data = await accountRequest<{ users: ManagedAccount[]; grants: ResourceGrant[] }>('/app/v1/admin/users');
  users.value = data.users; grants.value = data.grants;
}
onMounted(async () => { try { await load(); } catch (e) { error.value = (e as Error).message; } });
async function action(work: () => Promise<unknown>) {
  busy.value = true; error.value = '';
  try { await work(); await load(); } catch (e) { error.value = (e as Error).message; }
  finally { busy.value = false; }
}
async function invite() {
  token.value = '';
  await action(async () => {
    const result = await accountRequest<{ token: string; purpose: string }>('/app/v1/admin/invitations', 'POST', { role: inviteRole.value, recoveryUserId: recoveryId.value || null });
    token.value = result.token;
    invitationKind.value = result.purpose === 'recovery' ? 'recovery' : 'invite';
  });
}
</script>
<template>
  <section class="page-stack" aria-labelledby="admin-title">
    <header><p class="eyebrow">Správa aplikace</p><h1 id="admin-title">Uživatelé a oprávnění</h1><p>Vlastník řídí účty a obnovu. Správci přidělují přístup ke konkrétním prostředkům.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
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

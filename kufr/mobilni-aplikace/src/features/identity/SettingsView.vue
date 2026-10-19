<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import AccountArchiveList from './AccountArchiveList.vue';
import EveningPanel from './EveningPanel.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { usePreferences } from '../../application/preferences';
import { account, accountRequest, refreshIdentity, authenticatePasskey, signOut, hasRole, type AccountDetails, type AccountSession } from './session';
const preferences = usePreferences();
const details = ref<AccountDetails | null>(null);
const keyNames = ref<Record<string, string>>({});
const error = ref(''); const message = ref(''); const busy = ref(false);
let detailsGeneration = 0;
watch(() => account.value?.id, () => { ++detailsGeneration; details.value = null; keyNames.value = {}; error.value = ''; message.value = ''; }, { flush: 'sync' });
async function load() {
  await refreshIdentity();
  const generation = ++detailsGeneration;
  const result = account.value ? await accountRequest<AccountDetails>('/app/v1/account') : null;
  if (generation === detailsGeneration) {
    details.value = result;
    keyNames.value = Object.fromEntries((result?.passkeys ?? []).map(key => [key.id, key.name ?? '']));
  }
}
onMounted(async () => { try { await load(); } catch (e) { error.value = (e as Error).message; } });
async function action(work: () => Promise<unknown>, success: string) {
  busy.value = true; error.value = ''; message.value = '';
  try { await work(); await load(); message.value = success; } catch (e) { error.value = (e as Error).message; }
  finally { busy.value = false; }
}
function trust(session: AccountSession) {
  void action(() => accountRequest(`/app/v1/installations/${encodeURIComponent(session.id)}`, 'PUT', {
    name: session.installationName, trusted: !session.trusted,
  }), 'Důvěra instalace byla změněna.');
}
</script>
<template>
  <section class="page-stack" aria-labelledby="settings-title">
    <header><p class="eyebrow">Váš prostor</p><h1 id="settings-title">Nastavení</h1><p>Vzhled aplikace a přístup k vašemu účtu.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice><AwNotice v-if="message" tone="success" role="status">{{ message }}</AwNotice>
    <section class="aw-card"><h2>Vzhled</h2><label for="account-theme">Barevné téma</label><select id="account-theme" v-model="preferences.theme" class="aw-select"><option value="dark">Tmavé</option><option value="light">Světlé</option><option value="system">Podle zařízení</option></select></section>
    <section v-if="!account" class="aw-card"><h2>Přihlášení</h2><p>Přihlaste se pro soukromý archiv, přidělené prostředky a správu přístupů.</p><RouterLink to="/signin" class="aw-button aw-button--primary">Přihlásit se</RouterLink></section>
    <template v-else>
      <AccountArchiveList />
      <EveningPanel :details="details" />
      <section v-if="details?.associations.length" class="aw-card">
        <h2>Kufry propojené s účtem</h2>
        <p>Propojení uchovává název a instalaci. Odebrání neodstraní místní párování ani přidělená oprávnění.</p>
        <ul class="identity-list">
          <li v-for="association in details.associations" :key="association.id">
            <strong>{{association.alias}}</strong><p>{{association.deviceId}}</p>
            <AwButton :disabled="busy" @click="action(() => accountRequest(`/app/v1/devices/${encodeURIComponent(association.deviceId)}/association`, 'DELETE'), 'Propojení kufru s účtem bylo odebráno.')">Odebrat propojení</AwButton>
          </li>
        </ul>
      </section>
      <section class="aw-card"><h2>{{ account.displayName }}</h2><p>{{ account.roles.join(', ') }}</p><p v-if="details">Počet přístupových klíčů: {{ details.passkeyCount }}. Další klíč usnadní obnovu při ztrátě zařízení.</p><AwButton :busy="busy" @click="action(() => authenticatePasskey('add'), 'Přístupový klíč byl přidán.')">Přidat přístupový klíč</AwButton><p v-if="hasRole('Administrator')"><RouterLink to="/settings/admin">Správa uživatelů a oprávnění</RouterLink></p></section>
      <section v-if="details" class="aw-card">
        <h2>Přístupové klíče</h2>
        <p>Poslední klíč nelze odebrat. Odebrání klíče odhlásí ostatní relace účtu.</p>
        <p>Pojmenování a správa klíčů vyžadují nedávné ověření. <RouterLink to="/signin?return=/settings">Znovu ověřit účet</RouterLink></p>
        <ul class="identity-list">
          <li v-for="(key, index) in details.passkeys" :key="key.id">
            <strong>{{ key.name || `Přístupový klíč ${index + 1}` }}</strong>
            <p>Přidán {{ new Date(key.createdAt).toLocaleDateString('cs') }} · {{ key.isBackedUp ? 'zálohovaný poskytovatelem' : 'záloha nepotvrzena' }}</p>
            <form class="key-name-form" @submit.prevent="action(() => accountRequest(`/app/v1/passkeys/${encodeURIComponent(key.id)}`, 'PUT', {name: keyNames[key.id]}), 'Název přístupového klíče byl uložen.')">
              <label :for="`passkey-name-${index}`">Název přístupového klíče {{ index + 1 }}</label>
              <input :id="`passkey-name-${index}`" v-model="keyNames[key.id]" class="aw-input" maxlength="80" required :disabled="busy" />
              <AwButton type="submit" :disabled="busy || !keyNames[key.id]?.trim() || keyNames[key.id]?.trim() === key.name">Uložit název klíče</AwButton>
            </form>
            <AwButton variant="danger" :disabled="busy || details.passkeys.length === 1" @click="action(() => accountRequest(`/app/v1/passkeys/${encodeURIComponent(key.id)}`, 'DELETE'), 'Přístupový klíč byl odebrán.')">Odebrat klíč</AwButton>
          </li>
        </ul>
      </section>
      <section v-if="details" class="aw-card"><h2>Přihlášená zařízení</h2><p>Odebrání ukončí přístup dané relace. Označení důvěry nenahrazuje párování s kufrem ani neprodlužuje platnost přihlášení.</p><ul class="identity-list"><li v-for="session in details.sessions" :key="session.id"><div><strong>{{ session.installationName }}</strong> <span v-if="session.current">· toto zařízení</span><p>Platí do {{ new Date(session.expiresAt * 1000).toLocaleString('cs') }} · {{ session.trusted ? 'důvěryhodná instalace' : 'běžná relace' }}</p></div><div class="button-row"><AwButton :disabled="busy" @click="trust(session)">{{ session.trusted ? 'Zrušit důvěru' : 'Označit jako důvěryhodné' }}</AwButton><AwButton variant="danger" :disabled="busy" @click="action(() => session.current ? signOut() : accountRequest(`/app/v1/sessions/${encodeURIComponent(session.id)}`, 'DELETE'), 'Přístup zařízení byl odebrán.')">{{ session.current ? 'Odhlásit toto zařízení' : 'Odhlásit zařízení' }}</AwButton></div></li></ul></section>
      <section v-if="details" class="aw-card"><h2>Přidělené prostředky</h2><p v-if="!details.grants.length">Nemáte přidělené jednotlivé prostředky. Role vlastníka a správce mohou mít širší oprávnění.</p><ul v-else><li v-for="grant in details.grants" :key="grant.id">{{ grant.resourceType }} · {{ grant.resourceId }} · {{ grant.permission }}</li></ul><p>Přístupové údaje zařízení a návštěvníků zůstávají oddělené od účtu.</p></section>
    </template>
  </section>
</template>
<style scoped>
.identity-list { list-style: none; padding: 0; }.identity-list li { padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }.button-row { display: flex; flex-wrap: wrap; gap: .75rem; }.key-name-form { display: grid; gap: .5rem; margin-block: 1rem; }.key-name-form button { justify-self: start; }select { display: block; margin-top: .5rem; max-width: 100%; }
</style>

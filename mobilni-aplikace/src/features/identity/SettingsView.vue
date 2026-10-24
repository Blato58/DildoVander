<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue';
import AccountArchiveList from './AccountArchiveList.vue';
import AccountDeletion from './AccountDeletion.vue';
import RequestDiagnostics from './RequestDiagnostics.vue';
import EveningPanel from './EveningPanel.vue';
import CustomerDataPanel from '../customer/CustomerDataPanel.vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { usePreferences } from '../../application/preferences';
import { applicationBuild } from '../../application/buildIdentity';
import { isCustomerAccount } from '../../application/access';
import { account, accountDeletionRequested, accountRequest, refreshIdentity, authenticatePasskey, signOut, hasRole, type AccountDetails, type AccountSession } from './session';
const props = withDefaults(defineProps<{ customerSurface?: boolean }>(), { customerSurface: false });
const preferences = usePreferences();
const showCustomerData = computed(() => props.customerSurface && isCustomerAccount(account.value) && !accountDeletionRequested.value);
const details = ref<AccountDetails | null>(null);
const keyNames = ref<Record<string, string>>({});
const error = ref<CustomerCopyKey | ''>(''); const message = ref<CustomerCopyKey | ''>(''); const busy = ref(false);
const dateLocale = computed(() => customerCopy.language.value === 'en' ? 'en-GB' : 'cs-CZ');
const themeLabels: Record<string, CustomerCopyKey> = {
  'lord-voldemort': 'settings.theme-voldemort',
  olivander: 'settings.theme-olivander',
};
function themeLabel(theme: { id: string; label: string }): string {
  const key = themeLabels[theme.id];
  return key ? customerCopy.text(key) : theme.label;
}
function errorKey(value: unknown): CustomerCopyKey {
  return value instanceof DOMException && value.name === 'NotAllowedError'
    ? 'settings.error-cancelled'
    : 'settings.error-generic';
}
let detailsGeneration = 0;
watch(() => account.value?.id, () => { ++detailsGeneration; details.value = null; keyNames.value = {}; error.value = ''; message.value = ''; busy.value = false; }, { flush: 'sync' });
async function load() {
  const beforeRefresh = detailsGeneration;
  await refreshIdentity();
  if (beforeRefresh !== detailsGeneration) return;
  const generation = ++detailsGeneration;
  const accountId = account.value?.id;
  const result = account.value ? await accountRequest<AccountDetails>('/app/v1/account') : null;
  if (generation === detailsGeneration && accountId === account.value?.id) {
    details.value = result;
    keyNames.value = Object.fromEntries((result?.passkeys ?? []).map(key => [key.id, key.name ?? '']));
  }
}
onMounted(async () => { try { await load(); } catch (e) { error.value = errorKey(e); } });
async function action(work: () => Promise<unknown>, success: CustomerCopyKey) {
  const generation = detailsGeneration; const accountId = account.value?.id;
  busy.value = true; error.value = ''; message.value = '';
  try { await work(); if (generation !== detailsGeneration || accountId !== account.value?.id) return; await load(); if (accountId === account.value?.id) message.value = success; }
  catch (e) { if (generation === detailsGeneration && accountId === account.value?.id) error.value = errorKey(e); }
  finally { if (accountId === account.value?.id) busy.value = false; }
}
function trust(session: AccountSession) {
  void action(() => accountRequest(`/app/v1/installations/${encodeURIComponent(session.id)}`, 'PUT', {
    name: session.installationName, trusted: !session.trusted,
  }), 'settings.trust-changed');
}
</script>
<template>
  <section class="page-stack" :aria-label="customerSurface ? customerCopy.text('settings.customer-aria') : undefined" :aria-labelledby="customerSurface ? undefined : 'settings-title'">
    <header v-if="!customerSurface"><p class="eyebrow">{{ customerCopy.text('settings.eyebrow') }}</p><h1 id="settings-title">{{ customerCopy.text('settings.title') }}</h1><p>{{ customerCopy.text('settings.intro') }}</p></header>
    <section v-if="!customerSurface" class="aw-card" aria-labelledby="application-version-heading">
      <h2 id="application-version-heading">{{ customerCopy.text('settings.version-heading') }}</h2>
      <p>{{ customerCopy.text('settings.version-running') }}</p>
      <p>{{ customerCopy.text('settings.build-source') }} <code class="application-revision">{{ applicationBuild.revision || customerCopy.text('settings.build-unknown') }}</code></p>
      <p v-if="applicationBuild.dirty === true">{{ customerCopy.text('settings.build-dirty') }}</p>
      <p v-else-if="applicationBuild.dirty === null">{{ customerCopy.text('settings.build-clean-unknown') }}</p>
      <p>{{ customerCopy.text('settings.api-version') }}</p>
    </section>
    <AwNotice v-if="error" tone="danger" role="alert">{{ customerCopy.text(error) }}</AwNotice><AwNotice v-if="message" tone="success" role="status">{{ customerCopy.text(message) }}</AwNotice>
    <section v-if="!customerSurface" class="aw-card"><h2>{{ customerCopy.text('settings.appearance') }}</h2><label for="account-theme">{{ customerCopy.text('settings.theme') }}</label><select id="account-theme" v-model="preferences.theme" class="aw-select"><option v-for="theme in preferences.themes" :key="theme.id" :value="theme.id">{{ themeLabel(theme) }}</option></select></section>
    <section v-if="!account" class="aw-card"><h2>{{ customerCopy.text('settings.signin-heading') }}</h2><p>{{ customerCopy.text('settings.signin-intro') }}</p><RouterLink to="/signin" class="aw-button aw-button--primary">{{ customerCopy.text('settings.signin-link') }}</RouterLink></section>
    <template v-else>
      <AccountArchiveList v-if="!customerSurface" />
      <EveningPanel v-if="!customerSurface" :details="details" />
      <section v-if="!customerSurface && details?.associations.length" class="aw-card">
        <h2>{{ customerCopy.text('settings.linked-devices') }}</h2>
        <p>{{ customerCopy.text('settings.linked-devices-help') }}</p>
        <ul class="identity-list">
          <li v-for="association in details.associations" :key="association.id">
            <strong>{{association.alias}}</strong><p>{{association.deviceId}}</p>
            <AwButton :disabled="busy" @click="action(() => accountRequest(`/app/v1/devices/${encodeURIComponent(association.deviceId)}/association`, 'DELETE'), 'settings.device-unlinked')">{{ customerCopy.text('settings.unlink-device') }}</AwButton>
          </li>
        </ul>
      </section>
      <section class="aw-card"><h2>{{ account.displayName }}</h2><p v-if="!customerSurface">{{ account.roles.join(', ') }}</p><p v-if="details">{{ customerCopy.text('settings.passkey-count', { count: details.passkeyCount }) }}</p><AwButton :busy="busy" @click="action(() => authenticatePasskey('add'), 'settings.passkey-added')">{{ customerCopy.text('settings.add-passkey') }}</AwButton><p v-if="!customerSurface && hasRole('Administrator')"><RouterLink to="/settings/admin">{{ customerCopy.text('settings.admin-link') }}</RouterLink></p></section>
      <section v-if="details" class="aw-card">
        <h2>{{ customerCopy.text('settings.passkeys') }}</h2>
        <p>{{ customerCopy.text('settings.passkey-name-help') }}</p>
        <p>{{ customerCopy.text('settings.passkey-removal-warning') }}</p>
        <p>{{ customerCopy.text('settings.passkey-reauth') }} <RouterLink :to="`/signin?return=${customerSurface ? '/me/account' : '/settings'}`">{{ customerCopy.text('settings.reauthenticate-account') }}</RouterLink></p>
        <ul class="identity-list">
          <li v-for="(key, index) in details.passkeys" :key="key.id">
            <strong>{{ key.name || customerCopy.text('settings.passkey-default-name', { number: index + 1 }) }}</strong>
            <p>{{ customerCopy.text('settings.passkey-added-at', { date: new Date(key.createdAt).toLocaleDateString(dateLocale) }) }} · {{ customerCopy.text(key.isBackedUp ? 'settings.passkey-backed-up' : 'settings.passkey-backup-unknown') }}</p>
            <form class="key-name-form" @submit.prevent="action(() => accountRequest(`/app/v1/passkeys/${encodeURIComponent(key.id)}`, 'PUT', {name: keyNames[key.id]}), 'settings.passkey-name-saved')">
              <label :for="`passkey-name-${index}`">{{ customerCopy.text('settings.passkey-name-label', { number: index + 1 }) }}</label>
              <input :id="`passkey-name-${index}`" v-model="keyNames[key.id]" class="aw-input" maxlength="80" required :disabled="busy" />
              <AwButton type="submit" :disabled="busy || !keyNames[key.id]?.trim() || keyNames[key.id]?.trim() === key.name">{{ customerCopy.text('settings.save-passkey-name') }}</AwButton>
            </form>
            <AwButton variant="danger" :disabled="busy || details.passkeys.length === 1" @click="action(() => accountRequest(`/app/v1/passkeys/${encodeURIComponent(key.id)}`, 'DELETE'), 'settings.passkey-removed')">{{ customerCopy.text('settings.remove-passkey') }}</AwButton>
          </li>
        </ul>
      </section>
      <section v-if="details" class="aw-card">
        <h2>{{ customerCopy.text('settings.sessions') }}</h2>
        <p>{{ customerCopy.text('settings.session-removal-help') }}</p>
        <ul class="identity-list">
          <li v-for="session in details.sessions" :key="session.id">
            <div>
              <strong>{{ session.installationName }}</strong>
              <span v-if="session.current"> · {{ customerCopy.text('settings.this-device') }}</span>
              <p>{{ customerCopy.text('settings.session-expires', { date: new Date(session.expiresAt * 1000).toLocaleString(dateLocale) }) }} · {{ customerCopy.text(session.trusted ? 'settings.trusted-session' : 'settings.regular-session') }}</p>
            </div>
            <div class="button-row">
              <AwButton :disabled="busy" @click="trust(session)">{{ customerCopy.text(session.trusted ? 'settings.untrust-installation' : 'settings.trust-installation') }}</AwButton>
              <AwButton variant="danger" :disabled="busy" @click="action(() => session.current ? signOut() : accountRequest(`/app/v1/sessions/${encodeURIComponent(session.id)}`, 'DELETE'), 'settings.device-removed')">{{ customerCopy.text(session.current ? 'settings.sign-out-current' : 'settings.sign-out-device') }}</AwButton>
            </div>
          </li>
        </ul>
      </section>
      <section v-if="!customerSurface && details" class="aw-card">
        <h2>{{ customerCopy.text('settings.grants') }}</h2>
        <p v-if="!details.grants.length">{{ customerCopy.text('settings.no-grants') }}</p>
        <ul v-else><li v-for="grant in details.grants" :key="grant.id">{{ grant.resourceType }} · {{ grant.resourceId }} · {{ grant.permission }}</li></ul>
        <p>{{ customerCopy.text('settings.guest-separation') }}</p>
      </section>
      <CustomerDataPanel v-if="showCustomerData" :account-id="account.id" :eligible="showCustomerData" />
    </template>
    <AccountDeletion />
    <RequestDiagnostics />
  </section>
</template>
<style scoped>
.application-revision { overflow-wrap: anywhere; }
.identity-list { list-style: none; padding: 0; }.identity-list li { padding-block: 1rem; border-top: 1px solid var(--aw-border-subtle); }.button-row { display: flex; flex-wrap: wrap; gap: .75rem; }.key-name-form { display: grid; gap: .5rem; margin-block: 1rem; }.key-name-form button { justify-self: start; }select { display: block; margin-top: .5rem; max-width: 100%; }
</style>

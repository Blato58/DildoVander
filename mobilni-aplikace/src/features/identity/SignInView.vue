<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { consumeEnrollment } from '../../application/guestClaim';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwField from '../../components/AwField.vue';
import LostAccessHelp from './LostAccessHelp.vue';
import { defaultSignedInDestination } from '../customer/customerJourney';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { account, accountsEnabled, initialized, loading, signingOut, refreshIdentity, authenticatePasskey } from './session';
const router = useRouter();
const route = useRoute();
const enrollment = ref(false);
const customerEnrollment = ref(false);
const enrollmentKind = ref<'invite' | 'recovery'>('invite');
const token = ref('');
const displayName = ref('');
const customerDisplayName = ref('');
const busy = ref(false);
const error = ref<CustomerCopyKey | ''>('');
function errorKey(value: unknown): CustomerCopyKey {
  if (value instanceof DOMException && value.name === 'NotAllowedError') return 'signin.error-cancelled';
  if (value instanceof Error && value.message === 'Přístupové klíče vyžadují HTTPS a podporovaný prohlížeč.') return 'signin.error-secure-context';
  if (value instanceof Error && value.message === 'Ověření přístupovým klíčem už probíhá.') return 'signin.error-pending';
  if (value instanceof Error && 'code' in value) {
    switch (value.code) {
      case 'accounts_not_configured': return 'signin.accounts-disabled';
      case 'invitation_expired_or_used': return 'signin.error-invitation';
      case 'ceremony_expired_or_used': return 'signin.error-ceremony';
      case 'csrf_required': return 'signin.error-page-expired';
      case 'account_origin_required': return 'signin.error-origin';
      case 'passkey_not_verified': return 'signin.error-passkey';
      case 'recent_passkey_required': return 'signin.error-recent-passkey';
      case 'same_account_passkey_required': return 'signin.error-same-account';
    }
  }
  return 'signin.error-generic';
}
onMounted(async () => {
  const invitation = consumeEnrollment();
  if (invitation) { enrollment.value = true; enrollmentKind.value = invitation.kind; token.value = invitation.token; }
  try { await refreshIdentity(); } catch (e) { error.value = errorKey(e); }
});
async function submit() {
  error.value = ''; busy.value = true;
  try {
    await authenticatePasskey(account.value ? 'reauth' : customerEnrollment.value ? 'customer-enroll' : enrollment.value ? 'enroll' : 'signin', token.value, customerEnrollment.value ? customerDisplayName.value : displayName.value);
    token.value = '';
    await router.replace(customerEnrollment.value
      ? '/me/character'
      : defaultSignedInDestination(account.value, route.query.return));
  } catch (e) { error.value = errorKey(e); }
  finally { busy.value = false; }
}
</script>
<template>
  <section class="page-stack identity-page" aria-labelledby="sign-in-title">
    <header><p class="eyebrow">{{ customerCopy.text('signin.account-eyebrow') }}</p><h1 id="sign-in-title">{{ enrollment ? enrollmentKind === 'recovery' ? customerCopy.text('signin.recovery-title') : customerCopy.text('signin.invite-title') : customerCopy.text('signin.title') }}</h1><p>{{ customerCopy.text('signin.account-purpose') }}</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ customerCopy.text(error) }}</AwNotice>
    <p v-if="loading" role="status">{{ customerCopy.text('signin.loading') }}</p>
    <p v-if="signingOut" role="status">{{ customerCopy.text('signin.signing-out') }}</p>
    <AwNotice v-if="initialized && !accountsEnabled" tone="info">{{ customerCopy.text('signin.accounts-disabled') }}</AwNotice>
    <div v-if="account" class="aw-card"><p>{{ customerCopy.text('signin.signed-in-as', { name: account.displayName }) }}</p><AwButton variant="primary" :busy="busy" @click="submit">{{ customerCopy.text('signin.reauthenticate') }}</AwButton><p><RouterLink to="/settings">{{ customerCopy.text('signin.open-settings') }}</RouterLink></p></div>
    <form v-else class="aw-card identity-form" @submit.prevent="submit">
      <template v-if="customerEnrollment">
        <AwField id="customer-account-name" :label="customerCopy.text('signin.customer-name')"><input id="customer-account-name" v-model="customerDisplayName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
        <p class="identity-hint">{{ customerCopy.text('signin.customer-purpose') }}</p>
      </template>
      <template v-else-if="enrollment">
        <AwField id="enrollment-kind" :label="customerCopy.text('signin.code-kind')"><select id="enrollment-kind" v-model="enrollmentKind" class="aw-select" :disabled="busy"><option value="invite">{{ customerCopy.text('signin.invite-option') }}</option><option value="recovery">{{ customerCopy.text('signin.recovery-option') }}</option></select></AwField>
        <AwNotice v-if="enrollmentKind === 'recovery'" tone="warning">{{ customerCopy.text('signin.recovery-enrollment-warning') }} {{ customerCopy.text('signin.recovery-code-target') }}</AwNotice>
        <AwField v-else id="account-name" :label="customerCopy.text('signin.invite-name')"><input id="account-name" v-model="displayName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
        <AwField id="account-invite" :label="customerCopy.text('signin.token-label')" :hint="customerCopy.text('signin.token-hint')"><input id="account-invite" v-model="token" class="aw-input" type="password" autocomplete="off" maxlength="256" required :disabled="busy" /></AwField>
      </template>
      <AwButton type="submit" variant="primary" :busy="busy" :disabled="!accountsEnabled || signingOut">{{ customerCopy.text(customerEnrollment ? 'signin.customer-create' : enrollment ? enrollmentKind === 'recovery' ? 'signin.recovery-title' : 'signin.invite-create-key' : 'signin.submit-signin') }}</AwButton>
      <AwButton v-if="customerEnrollment" :disabled="busy" @click="customerEnrollment = false; error = ''">{{ customerCopy.text('signin.back-to-signin') }}</AwButton>
      <template v-else>
        <AwButton :disabled="busy" @click="enrollment = !enrollment; error = ''; token = ''">{{ customerCopy.text(enrollment ? 'signin.have-passkey' : 'signin.have-invite') }}</AwButton>
        <AwButton v-if="!enrollment" :disabled="busy" @click="customerEnrollment = true; error = ''">{{ customerCopy.text('signin.create-customer') }}</AwButton>
      </template>
      <p v-if="enrollment" class="identity-hint">{{ customerCopy.text('signin.enrollment-help') }}</p>
      <p v-else-if="!customerEnrollment" class="identity-hint">{{ customerCopy.text('signin.customer-enrollment-help') }}</p>
    </form>
    <LostAccessHelp v-if="!account && !enrollment && !customerEnrollment" />
    <RouterLink to="/archive">{{ customerCopy.text('signin.public-archive') }}</RouterLink>
  </section>
</template>
<style scoped>
.identity-page { max-width: 44rem; }.identity-form { display: grid; gap: 1.25rem; }.identity-hint { color: var(--aw-muted); font-size: .9rem; }
</style>

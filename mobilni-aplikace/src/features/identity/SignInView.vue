<script setup lang="ts">
import { onMounted, ref } from 'vue';
import { useRouter, useRoute } from 'vue-router';
import { safeReturnPath } from '../../application/navigation';
import { consumeEnrollment } from '../../application/guestClaim';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwField from '../../components/AwField.vue';
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
const error = ref('');
onMounted(async () => {
  const invitation = consumeEnrollment();
  if (invitation) { enrollment.value = true; enrollmentKind.value = invitation.kind; token.value = invitation.token; }
  try { await refreshIdentity(); } catch (e) { error.value = (e as Error).message; }
});
async function submit() {
  error.value = ''; busy.value = true;
  try {
    await authenticatePasskey(account.value ? 'reauth' : customerEnrollment.value ? 'customer-enroll' : enrollment.value ? 'enroll' : 'signin', token.value, customerEnrollment.value ? customerDisplayName.value : displayName.value);
    token.value = ''; await router.replace(customerEnrollment.value ? '/me/character' : route.query.return ? safeReturnPath(route.query.return) : '/settings');
  } catch (e) { error.value = e instanceof DOMException && e.name === 'NotAllowedError' ? 'Ověření bylo zrušeno nebo vypršelo. Můžete ho spustit znovu.' : (e as Error).message; }
  finally { busy.value = false; }
}
</script>
<template>
  <section class="page-stack identity-page" aria-labelledby="sign-in-title">
    <header><p class="eyebrow">Váš účet</p><h1 id="sign-in-title">{{ enrollment ? enrollmentKind === 'recovery' ? 'Obnovit přístup' : 'Přijmout pozvánku' : 'Přihlásit se' }}</h1><p>Jeden účet pro vaše kufry, dílnu a soukromý archiv. Přístup ověříte klíčem uloženým ve svém zařízení.</p></header>
    <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
    <p v-if="loading" role="status">Ověřuji dostupnost přihlášení…</p>
    <p v-if="signingOut" role="status">Dokončuji odhlášení. Nové přihlášení bude dostupné po dokončení předchozího požadavku.</p>
    <AwNotice v-if="initialized && !accountsEnabled" tone="info">Přihlášení je dostupné až po konfiguraci serveru správcem. Veřejné příběhy a model si můžete prohlédnout bez účtu.</AwNotice>
    <div v-if="account" class="aw-card"><p>Jste přihlášeni jako {{ account.displayName }}. Pro citlivé změny znovu ověřte tentýž účet.</p><AwButton variant="primary" :busy="busy" @click="submit">Znovu ověřit přístupovým klíčem</AwButton><p><RouterLink to="/settings">Otevřít nastavení účtu</RouterLink></p></div>
    <form v-else class="aw-card identity-form" @submit.prevent="submit">
      <template v-if="customerEnrollment">
        <AwField id="customer-account-name" label="Jak vám máme říkat?"><input id="customer-account-name" v-model="customerDisplayName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
        <p class="identity-hint">Účet vám uloží vaši postavu, rozhovory a památky. Přístupový klíč zůstane v tomto zařízení.</p>
      </template>
      <template v-else-if="enrollment">
        <AwField id="enrollment-kind" label="Typ kódu"><select id="enrollment-kind" v-model="enrollmentKind" class="aw-select" :disabled="busy"><option value="invite">Pozvánka k vytvoření účtu</option><option value="recovery">Obnova přístupu k existujícímu účtu</option></select></AwField>
        <AwNotice v-if="enrollmentKind === 'recovery'" tone="warning">Po úspěšném ověření nový klíč nahradí všechny původní klíče a odhlásí ostatní zařízení. Stávající účet, jeho jméno a přidělená oprávnění zůstanou zachovány. Obnovovaný účet určuje soukromý kód.</AwNotice>
        <AwField v-else id="account-name" label="Zobrazované jméno"><input id="account-name" v-model="displayName" class="aw-input" maxlength="80" autocomplete="nickname" required :disabled="busy" /></AwField>
        <AwField id="account-invite" label="Pozvánka nebo kód obnovy" hint="Kód získáte soukromě od vlastníka. Vložte ho jen na důvěryhodné adrese serveru."><input id="account-invite" v-model="token" class="aw-input" type="password" autocomplete="off" maxlength="256" required :disabled="busy" /></AwField>
      </template>
      <AwButton type="submit" variant="primary" :busy="busy" :disabled="!accountsEnabled || signingOut">{{ customerEnrollment ? 'Vytvořit účet a přístupový klíč' : enrollment ? enrollmentKind === 'recovery' ? 'Obnovit přístup' : 'Vytvořit přístupový klíč' : 'Přihlásit přístupovým klíčem' }}</AwButton>
      <AwButton v-if="customerEnrollment" :disabled="busy" @click="customerEnrollment = false; error = ''">Zpět k přihlášení</AwButton>
      <template v-else>
        <AwButton :disabled="busy" @click="enrollment = !enrollment; error = ''; token = ''">{{ enrollment ? 'Už mám přístupový klíč' : 'Mám pozvánku nebo kód obnovy' }}</AwButton>
        <AwButton v-if="!enrollment" :disabled="busy" @click="customerEnrollment = true; error = ''">Vytvořit zákaznický účet</AwButton>
      </template>
      <p v-if="enrollment" class="identity-hint">Při ztrátě všech klíčů požádejte vlastníka o jednorázový kód obnovy. Pozvánku nebo obnovovací kód vložte jen na důvěryhodné adrese serveru.</p>
      <p v-else-if="!customerEnrollment" class="identity-hint">Nový zákaznický účet vytvoříte bez pozvánky. Přihlášení obsluhy používá soukromou pozvánku.</p>
    </form>
    <RouterLink to="/archive">Prohlédnout veřejný archiv</RouterLink>
  </section>
</template>
<style scoped>
.identity-page { max-width: 44rem; }.identity-form { display: grid; gap: 1.25rem; }.identity-hint { color: var(--aw-muted); font-size: .9rem; }
</style>

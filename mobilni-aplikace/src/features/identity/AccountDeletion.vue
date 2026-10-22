<script setup lang="ts">
import { onMounted, onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, authenticatePasskey, deleteAccount, getAccountDeletionStatus } from './session';

const props = withDefaults(defineProps<{ receiptOnly?: boolean }>(), { receiptOnly: false });
const busy = ref(false);
const verifiedAccountId = ref('');
const error = ref('');
const accepted = ref(false);
const completed = ref(false);
const checking = ref(false);
const statusError = ref('');
const receiptMissing = ref(false);
let statusRequest: AbortController | undefined;
let statusTimer: ReturnType<typeof setTimeout> | undefined;
let checksRemaining = 60;
let submitting = false;
let disposed = false;
let generation = 0;
function cancelStatus() {
  clearTimeout(statusTimer);
  statusRequest?.abort();
  statusRequest = undefined;
  checking.value = false;
}
async function checkStatus() {
  if (disposed || account.value || checking.value || document.visibilityState === 'hidden') return;
  clearTimeout(statusTimer);
  const controller = new AbortController();
  statusRequest = controller;
  checking.value = true;
  statusError.value = '';
  try {
    const status = await getAccountDeletionStatus(controller.signal);
    if (disposed || controller.signal.aborted || account.value) return;
    receiptMissing.value = !status;
    if (status) {
      accepted.value = true;
      completed.value = status === 'deleted';
    } else if (accepted.value) {
      statusError.value = 'Potvrzení smazání už není dostupné. Pro ověření se obraťte na podporu.';
    }
    if (status === 'deletion_requested' && --checksRemaining > 0) statusTimer = setTimeout(() => void checkStatus(), 5000);
  } catch (failure) {
    if (!controller.signal.aborted && !disposed) statusError.value = 'Stav smazání se nepodařilo ověřit. Zkuste kontrolu znovu.';
  } finally {
    if (statusRequest === controller) { statusRequest = undefined; checking.value = false; }
  }
}
function visibilityChanged() {
  if (document.visibilityState === 'hidden') cancelStatus();
  else if (accepted.value && !completed.value && checksRemaining > 0) void checkStatus();
}
onMounted(() => { document.addEventListener('visibilitychange', visibilityChanged); if (!account.value) void checkStatus(); });
onBeforeUnmount(() => { disposed = true; cancelStatus(); document.removeEventListener('visibilitychange', visibilityChanged); });
watch(() => account.value?.id, next => {
  ++generation;
  cancelStatus();
  verifiedAccountId.value = '';
  error.value = '';
  accepted.value = false;
  completed.value = false;
  statusError.value = '';
  receiptMissing.value = false;
  checksRemaining = 60;
  busy.value = false;
  if (!next && !submitting) void checkStatus();
}, { flush: 'sync' });

async function verify() {
  const accountId = account.value?.id;
  if (!accountId || busy.value) return;
  const attempt = generation;
  busy.value = true;
  error.value = '';
  verifiedAccountId.value = '';
  try {
    await authenticatePasskey('reauth');
    if (attempt === generation && account.value?.id === accountId) verifiedAccountId.value = accountId;
  } catch (failure) {
    if (attempt === generation) error.value = (failure as Error).message;
  } finally {
    if (attempt === generation) busy.value = false;
  }
}

async function submit() {
  const accountId = verifiedAccountId.value;
  if (!accountId || account.value?.id !== accountId || busy.value) return;
  const attempt = generation;
  busy.value = true;
  error.value = '';
  submitting = true;
  try {
    await deleteAccount(accountId);
    // Successful deletion clears account synchronously. Never show its receipt
    // to a different account that signed in while this request was pending.
    if (!disposed && !account.value) { accepted.value = true; void checkStatus(); }
  } catch (failure) {
    if (attempt === generation) {
      error.value = (failure as Error).message;
      if (!failure || typeof failure !== 'object' || !('status' in failure)) {
        error.value += ' Pokud se přerušilo spojení, výsledek může být neznámý. Ověřte stav účtu před dalším pokusem.';
      }
    }
  } finally {
    submitting = false;
    if (attempt === generation) {
      busy.value = false;
      verifiedAccountId.value = '';
    }
  }
}
</script>

<template>
  <section v-if="account || accepted || statusError || receiptOnly" class="aw-card account-deletion" aria-labelledby="account-deletion-title" :aria-busy="busy || checking">
    <h2 id="account-deletion-title">Smazání účtu</h2>
    <p v-if="receiptOnly && account">Jste přihlášeni k aktivnímu účtu. Potvrzení dřívějšího smazání se během přihlášení nezobrazuje.</p>
    <template v-else-if="accepted || !account || receiptOnly">
      <p v-if="receiptOnly && checking && !accepted" role="status">Ověřuji stav smazání účtu…</p>
      <p v-if="receiptOnly && receiptMissing && !accepted">Na tomto zařízení není dostupné potvrzení smazání. Pokud jste o smazání požádali jinde, ověřte stav na původním zařízení nebo se obraťte na podporu.</p>
      <AwNotice v-if="accepted" :tone="completed ? 'success' : 'info'" role="status">
        <template v-if="completed">Smazání účtu a jeho soukromých dat v aplikaci bylo dokončeno.</template>
        <template v-else>Žádost o smazání byla přijata. Účet je zablokovaný a přihlášení na všech zařízeních ukončena. Mazání soukromých dat pokračuje na serveru; nemusí být dokončeno okamžitě.</template>
      </AwNotice>
      <p v-if="accepted">Toto potvrzení se týká dat aplikace. Případné zálohy poskytovatelů mají vlastní dobu uchování. Mazání běžně probíhá automaticky; při delším čekání se obraťte na podporu.</p>
      <AwNotice v-if="statusError" tone="danger" role="alert">{{ statusError }}</AwNotice>
      <AwButton v-if="!completed" :busy="checking" :disabled="checking" @click="checksRemaining = 60; checkStatus()">Zkontrolovat stav smazání</AwButton>
    </template>
    <template v-else>
      <p>Trvale smažete svůj účet, soukromé fotografie, portréty, příběhy a postavu. Vaše sdílené odkazy přestanou fungovat. Přístup ztratíte na všech zařízeních. Smazání nelze vrátit zpět.</p>
      <p>Účty a data ostatních hostů zůstanou zachována. Nejprve ověřte svůj účet přístupovým klíčem. Poté smazání výslovně potvrdíte.</p>
      <AwNotice v-if="error" tone="danger" role="alert">{{ error }}</AwNotice>
      <AwButton v-if="!verifiedAccountId" :busy="busy" :disabled="busy" @click="verify">Ověřit účet pro smazání</AwButton>
      <template v-else>
        <p role="status">Účet {{ account?.displayName }} byl ověřen. Další krok vyžádá konečné potvrzení nevratného smazání.</p>
        <div class="deletion-actions">
          <AwButton variant="danger" :busy="busy" :disabled="busy" @click="submit">Trvale smazat můj účet</AwButton>
          <AwButton :disabled="busy" @click="verifiedAccountId = ''">Zrušit</AwButton>
        </div>
      </template>
    </template>
  </section>
</template>

<style scoped>
.deletion-actions { display: flex; flex-wrap: wrap; gap: .75rem; }
</style>

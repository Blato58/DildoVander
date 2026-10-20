<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { deviceAssociation as cloud } from './deviceAssociation';
import { deviceSession as device } from './deviceSession';
const alias = ref('');
const confirmRemove = ref(false);
watch(cloud.association, value => { alias.value = value?.alias ?? ''; confirmRemove.value = false; });
onMounted(() => { void cloud.refresh(); });
</script>

<template>
  <section class="aw-card association-panel" aria-labelledby="device-account-title">
    <h2 id="device-account-title">Kufr a váš účet</h2>
    <p>Serverová oprávnění přiděluje vlastník nebo správce pro přesné ID kufru. Místní přístup vyžaduje samostatné nativní párování s PoP a ověřeným certifikátem. Hledání v síti, alias ani tato vazba žádné oprávnění nevytváří.</p>
    <dl>
      <div><dt>Místní párování</dt><dd>{{ device.savedDevice.value.paired ? `Uloženo v Keychain · ${device.savedDevice.value.deviceId}` : 'Nepotvrzené · nejprve ověřte kufr přes Bluetooth' }}</dd></div>
      <div><dt>Cloudové oprávnění</dt><dd>{{ !cloud.account.value ? 'Nepřihlášený účet' : !cloud.details.value ? 'Neověřené · cloud není dostupný nebo stav nebyl načten' : cloud.canOperate.value ? 'Grant/role dovoluje ovládání; připojení se ověřuje samostatně' : cloud.canRead.value ? 'Pouze čtení' : 'Pro tento kufr není přidělené oprávnění' }}</dd></div>
      <div><dt>Vazba této instalace</dt><dd>{{ cloud.association.value ? cloud.association.value.alias || cloud.association.value.deviceId : 'Nenastavená' }}</dd></div>
    </dl>
    <p role="status">{{ cloud.message.value }}</p>
    <RouterLink v-if="!cloud.account.value" to="/signin">Přihlásit se k účtu</RouterLink>
    <template v-else>
      <AwButton :busy="cloud.pending.value" @click="cloud.refresh">Obnovit cloudová oprávnění</AwButton>
      <p v-if="cloud.details.value && !cloud.canRead.value">Požádejte vlastníka nebo správce o přístup k přesnému ID svého kufru. Párování samo přístup k backendu nerozšiřuje.</p>
      <form v-if="device.native && device.savedDevice.value.paired && cloud.canRead.value" @submit.prevent="cloud.attach(alias)">
        <label class="aw-field">Název kufru pro tuto instalaci<input v-model="alias" class="aw-input" maxlength="80" required :disabled="cloud.pending.value" placeholder="Například Hlavní kufr"></label>
        <div class="association-actions"><AwButton type="submit" variant="primary" :disabled="cloud.pending.value || !alias.trim()">{{ cloud.association.value ? 'Uložit název a vazbu' : 'Přiřadit spárovaný kufr k instalaci' }}</AwButton></div>
      </form>
    </template>
    <AwButton v-if="cloud.association.value" :disabled="cloud.pending.value" @click="confirmRemove = true">Odebrat cloudovou vazbu…</AwButton>
    <div v-if="confirmRemove" class="aw-notice"><p>Odebrat vazbu této instalace na serveru? Tento krok nemaže data, samostatné granty správce ani místní Keychain párování.</p><div class="association-actions"><AwButton @click="confirmRemove = false">Zrušit</AwButton><AwButton :disabled="cloud.pending.value" @click="cloud.remove(); confirmRemove = false">Potvrdit odebrání vazby</AwButton></div></div>
    <p class="aw-help">Cloudová změna a revokace vyžaduje potvrzení serveru. Offline instalace tím okamžitě neztrácí samostatný místní přístup. Pro jeho odebrání použijte zvlášť Odebrat párování z iPhonu; pro serverová oprávnění správu účtu.</p>
  </section>
</template>

<style scoped>
.association-panel{display:grid;gap:.75rem}.association-panel dl{display:grid;gap:1rem}.association-panel dd{margin:0;overflow-wrap:anywhere}.association-actions{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.aw-field{display:grid;gap:.5rem;margin-block:.75rem}
</style>

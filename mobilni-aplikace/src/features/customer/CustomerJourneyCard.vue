<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink } from 'vue-router';
import AwNotice from '../../components/AwNotice.vue';
import { getCustomerJourneyAction, type CustomerJourneyInput, type CustomerJourneyMeaning } from './customerJourney';

const props = defineProps<{ journey: CustomerJourneyInput }>();
const nextAction = computed(() => getCustomerJourneyAction(props.journey));
const guidance: Record<CustomerJourneyMeaning, string> = {
  'authentication-required': 'Přihlaste se pro svou postavu, rozhovory a soukromé památky.',
  'staff-destination': 'Oprávnění k obsluze a jednotlivým kufrům najdete ve svém účtu.',
  'verification-required': 'Stav účtu nebo rozhovoru není ověřený. Než začnete znovu, obnovte ho na obrazovce rozhovoru.',
  'active-session': 'Vraťte se k existujícímu rozhovoru. Jeho uložený stav sám nepotvrzuje aktuální spojení s kufrem.',
  'character-missing': 'Začněte jménem a světem své postavy. Profil můžete uložit i bez přípravy pomocí AI.',
  'profile-review': 'Zkontrolujte uloženou postavu nebo ji upřesněte. Oprava zachová její identitu a historii.',
  'preparation-pending': 'Příprava postavy ještě není dokončená. Na její obrazovce uvidíte aktuální stav.',
  'latest-result': 'Otevřete svou poslední dostupnou památku. Další rozhovor najdete v části Rozhovor.',
  'conversation-ready': 'Postava je uložená. Na obrazovce rozhovoru ověříte blízký kufr a zvolíte souhlasy pro tento rozhovor.',
};
</script>

<template>
  <section class="customer-journey-card page-stack" aria-labelledby="customer-next-action-title">
    <p class="eyebrow">Váš další krok</p>
    <h2 id="customer-next-action-title">Postava, rozhovor a vaše památky</h2>
    <AwNotice :tone="nextAction.meaning === 'verification-required' ? 'warning' : 'info'">
      {{ guidance[nextAction.meaning] }}
    </AwNotice>
    <RouterLink class="aw-button aw-button--primary" :to="nextAction.route">{{ nextAction.label }}</RouterLink>
    <RouterLink v-if="journey.account" :to="nextAction.meaning === 'staff-destination' ? '/settings' : '/me/account'">Účet a pomoc s přístupem</RouterLink>
  </section>
</template>

<style scoped>
.customer-journey-card { align-items: start; }
.customer-journey-card h2, .customer-journey-card p { margin: 0; }
.customer-journey-card .aw-button { max-width: 100%; white-space: normal; text-align: center; }
</style>

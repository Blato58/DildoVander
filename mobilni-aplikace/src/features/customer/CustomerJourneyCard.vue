<script setup lang="ts">
import { computed } from 'vue';
import { RouterLink } from 'vue-router';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import {
  getCustomerJourneyAction,
  type CustomerJourneyInput,
  type CustomerJourneyKey,
  type CustomerJourneyMeaning,
} from './customerJourney';

const props = defineProps<{ journey: CustomerJourneyInput }>();
const nextAction = computed(() => getCustomerJourneyAction(props.journey));
const actionCopy: Record<CustomerJourneyKey, CustomerCopyKey> = {
  'sign-in': 'journey.sign-in',
  'staff-home': 'journey.staff-home',
  'refresh-session': 'journey.refresh-session',
  'resume-session': 'journey.resume-session',
  'create-character': 'journey.create-character',
  'review-character': 'journey.review-character',
  'review-preparation': 'journey.review-preparation',
  'open-latest-result': 'journey.open-latest-result',
  'check-result': 'journey.check-result',
  'continue-conversation': 'journey.continue-conversation',
};
const meaningCopy: Record<CustomerJourneyMeaning, CustomerCopyKey> = {
  'authentication-required': 'journey.auth-guidance',
  'staff-destination': 'journey.staff-guidance',
  'verification-required': 'journey.verification-guidance',
  'active-session': 'journey.active-guidance',
  'character-missing': 'journey.missing-character-guidance',
  'profile-review': 'journey.review-guidance',
  'preparation-pending': 'journey.preparation-guidance',
  'latest-result': 'journey.latest-result-guidance',
  'result-preparing': 'journey.result-preparing-guidance',
  'conversation-ready': 'journey.conversation-ready-guidance',
};
const title = computed(() => customerCopy.text('journey.title'));
const eyebrow = computed(() => customerCopy.text('journey.eyebrow'));
const actionLabel = computed(() => customerCopy.text(actionCopy[nextAction.value.key]));
const actionGuidance = computed(() => customerCopy.text(meaningCopy[nextAction.value.meaning]));
const accountHelp = computed(() => customerCopy.text('journey.account-help'));
</script>

<template>
  <section class="customer-journey-card page-stack" aria-labelledby="customer-next-action-title">
    <p class="eyebrow">{{ eyebrow }}</p>
    <h2 id="customer-next-action-title">{{ title }}</h2>
    <AwNotice :tone="nextAction.meaning === 'verification-required' ? 'warning' : 'info'">
      {{ actionGuidance }}
    </AwNotice>
    <RouterLink class="aw-button aw-button--primary" :to="nextAction.route">{{ actionLabel }}</RouterLink>
    <RouterLink v-if="journey.account" :to="nextAction.meaning === 'staff-destination' ? '/settings' : '/me/account'">{{ accountHelp }}</RouterLink>
  </section>
</template>

<style scoped>
.customer-journey-card { align-items: start; }
.customer-journey-card h2, .customer-journey-card p { margin: 0; }
.customer-journey-card .aw-button { max-width: 100%; white-space: normal; text-align: center; }
</style>

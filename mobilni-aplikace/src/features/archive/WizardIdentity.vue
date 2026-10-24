<script setup lang="ts">
import { computed } from 'vue';
import { publicIdentity } from './showContracts';
import GeneratedWand from './GeneratedWand.vue';
import { customerCopy } from '../../application/customerCopy';
const props = defineProps<{ identity?: unknown }>();
const card = computed(() => publicIdentity(props.identity));
const traits = computed(() => card.value ? [
  [customerCopy.text('keepsake.identity-institution'), card.value.houseOrGuild], [customerCopy.text('keepsake.identity-ability'), card.value.signatureAbility],
  [customerCopy.text('keepsake.identity-weakness'), card.value.weakness], [customerCopy.text('keepsake.identity-specialization'), card.value.dubiousSpecialization],
  [customerCopy.text('keepsake.identity-assessment'), card.value.ministryAssessment], [customerCopy.text('keepsake.identity-bond'), card.value.wandBond],
] : []);
</script>
<template>
  <section v-if="card" class="aw-card archive-identity" :aria-label="customerCopy.text('keepsake.identity-title')">
    <h2>{{ card.wizardAlias }}</h2><p class="archive-caption">{{ customerCopy.text('keepsake.identity-caption') }}</p>
    <GeneratedWand v-if="card.generatedWand" :wand="card.generatedWand" show-name />
    <dl><template v-for="[label, value] in traits" :key="label"><dt>{{ label }}</dt><dd>{{ value }}</dd></template></dl>
    <blockquote>{{ card.motto }}</blockquote><p>{{ card.microStory }}</p><p>{{ customerCopy.text('keepsake.identity-omen') }}: {{ card.omen }}</p>
  </section>
  <p v-else class="archive-muted">{{ customerCopy.text('keepsake.identity-empty') }}</p>
</template>

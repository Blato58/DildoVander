<script setup lang="ts">
import { computed } from 'vue';
import { publicIdentity } from './showContracts';
const props = defineProps<{ identity?: unknown }>();
const card = computed(() => publicIdentity(props.identity));
const traits = computed(() => card.value ? [
  ['Instituce', card.value.houseOrGuild], ['Schopnost', card.value.signatureAbility],
  ['Slabina', card.value.weakness], ['Pochybná specializace', card.value.dubiousSpecialization],
  ['Ministerské hodnocení', card.value.ministryAssessment], ['Pouto s hůlkou', card.value.wandBond],
] : []);
</script>
<template>
  <section v-if="card" class="aw-card archive-identity" aria-label="Kouzelnická identita">
    <h2>{{ card.wizardAlias }}</h2><p class="archive-caption">Kouzelnická fikce · nejde o osobní posudek</p>
    <dl><template v-for="[label, value] in traits" :key="label"><dt>{{ label }}</dt><dd>{{ value }}</dd></template></dl>
    <blockquote>{{ card.motto }}</blockquote><p>{{ card.microStory }}</p><p>Veřejná indicie: {{ card.omen }}</p>
  </section>
  <p v-else class="archive-muted">Magický profil zatím nevytvořen.</p>
</template>

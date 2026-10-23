<script setup lang="ts">
import { computed } from 'vue';
import { ownerProphecy } from './showContracts';
import { customerCopy } from '../../application/customerCopy';
const props = defineProps<{ prophecy?: unknown }>();
const view = computed(() => ownerProphecy(props.prophecy));
const when = computed(() => {
  const date = new Date(view.value?.unlockAt ?? '');
  return Number.isNaN(date.valueOf()) ? customerCopy.text('keepsake.prophecy-fallback-time') : new Intl.DateTimeFormat(customerCopy.language.value === 'en' ? 'en-GB' : 'cs-CZ', { timeZone: 'Europe/Prague', dateStyle: 'medium', timeStyle: 'short' }).format(date);
});
</script>
<template>
  <section v-if="view" class="aw-card" :aria-label="customerCopy.text('keepsake.prophecy-title')">
    <h2>{{ customerCopy.text('keepsake.prophecy-title') }}</h2><p class="archive-caption">{{ customerCopy.text('keepsake.prophecy-caption') }}</p>
    <template v-if="view.state === 'revealed'"><p>{{ view.revelation }}</p><p v-if="view.afterword">{{ view.afterword }}</p></template>
    <p v-else>{{ customerCopy.text('keepsake.prophecy-pending', { when }) }}</p>
  </section>
</template>

<script setup lang="ts">
import { computed } from 'vue';
import { ownerProphecy } from './showContracts';
const props = defineProps<{ prophecy?: unknown }>();
const view = computed(() => ownerProphecy(props.prophecy));
const when = computed(() => {
  const date = new Date(view.value?.unlockAt ?? '');
  return Number.isNaN(date.valueOf()) ? 'za 15 minut od vzniku identity' : new Intl.DateTimeFormat('cs-CZ', { timeZone: 'Europe/Prague', dateStyle: 'medium', timeStyle: 'short' }).format(date);
});
</script>
<template>
  <section v-if="view" class="aw-card" aria-label="Soukromé proroctví">
    <h2>Soukromé proroctví</h2><p class="archive-caption">Komediální fikce · soukromá část památky</p>
    <template v-if="view.state === 'revealed'"><p>{{ view.revelation }}</p><p v-if="view.afterword">{{ view.afterword }}</p></template>
    <p v-else>Plné znění se odemkne {{ when }} (Praha), při ukončení tohoto večera nebo povelem obsluhy. Čas ověřuje server. Obnovení stavu nic negeneruje.</p>
  </section>
</template>

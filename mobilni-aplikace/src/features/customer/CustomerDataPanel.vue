<script setup lang="ts">
import { computed, ref, useId, watch } from 'vue';
import CustomerDataExports from './CustomerDataExports.vue';
import CustomerMemories from './CustomerMemories.vue';

const props = defineProps<{
  accountId: string;
  eligible: boolean;
}>();

const expanded = ref(false);
const instanceId = `customer-data-panel-${useId()}`;
const headingId = `${instanceId}-title`;
const contentId = `${instanceId}-content`;
const disclosureLabel = computed(() => expanded.value ? 'Skrýt moje data' : 'Zobrazit moje data');

watch(() => [props.accountId, props.eligible] as const, () => {
  expanded.value = false;
}, { flush: 'sync' });
</script>

<template>
  <section class="aw-card customer-data-panel" :aria-labelledby="headingId">
    <header class="customer-data-panel__header">
      <div>
        <h2 :id="headingId">Moje data</h2>
        <p>Přehled aktuálně dostupných osobních pamětí a možnost exportu údajů účtu.</p>
      </div>
      <button
        class="aw-button aw-button--secondary customer-data-panel__toggle"
        type="button"
        :aria-expanded="expanded"
        :aria-controls="contentId"
        :disabled="!eligible"
        @click="expanded = !expanded"
      >
        {{ disclosureLabel }}
      </button>
    </header>

    <div :id="contentId" v-show="expanded && eligible" class="customer-data-panel__content">
      <CustomerMemories v-if="expanded && eligible" />
      <CustomerDataExports v-if="expanded && eligible" />
    </div>
  </section>
</template>

<style scoped>
.customer-data-panel { min-width: 0; }
.customer-data-panel__header { display: flex; align-items: start; justify-content: space-between; gap: var(--aw-space-4, 1rem); }
.customer-data-panel__header h2, .customer-data-panel__header p { margin: 0; }
.customer-data-panel__header > div { min-width: 0; }
.customer-data-panel__header p { overflow-wrap: anywhere; }
.customer-data-panel__toggle { flex: 0 0 auto; max-width: 100%; white-space: normal; }
.customer-data-panel__content { display: grid; gap: var(--aw-space-6, 2rem); margin-block-start: var(--aw-space-5, 1.5rem); min-width: 0; }
@media (max-width: 30rem) {
  .customer-data-panel__header { flex-direction: column; }
}
</style>

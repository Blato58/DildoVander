<script setup lang="ts">
import { computed } from 'vue';
import { engineLabels, parseEngineCapabilities } from './conversationCapabilities';
const props = defineProps<{ capabilities?: unknown }>();
const inventory = computed(() => parseEngineCapabilities(props.capabilities));
</script>
<template>
  <section aria-label="Dostupnost rozhovorových enginů">
    <p v-if="!inventory" class="aw-notice">Obnovte stav serveru. Je vyžadován kontrakt rozhovorových enginů v1.</p>
    <template v-else><p>Primární engine: {{ engineLabels[inventory.primaryEngine] }}. Konfigurace není důkaz úspěšného připojení k modelu.</p>
      <dl class="engine-capabilities"><template v-for="item in inventory.engines" :key="item.engine"><dt>{{ engineLabels[item.engine] }}</dt><dd>{{ item.ready ? 'Konfigurace připravena' : 'Nedostupný · ověřte konfiguraci a přístup k modelu' }}<br>Hlasy: {{ item.voices.join(', ') || 'žádné' }} · tempo {{ item.speed.supported ? `${item.speed.min}–${item.speed.max}×` : `${item.speed.default}× pevné` }}<br>Vstup: {{ item.inputModes.join(', ') || 'žádný' }} · kamery: {{ item.cameraSources.join(', ') || 'žádné' }}</dd></template></dl>
    </template>
  </section>
</template>
<style scoped>.engine-capabilities{display:grid;grid-template-columns:minmax(8rem,1fr) 2fr;gap:.75rem}.engine-capabilities dt{font-weight:600}.engine-capabilities dd{margin:0;overflow-wrap:anywhere}@media(max-width:480px){.engine-capabilities{display:block}.engine-capabilities dd{margin-block:.5rem 1rem}}</style>

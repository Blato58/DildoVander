<script setup lang="ts">
import ArchiveDialog from './ArchiveDialog.vue';
defineProps<{ url: string; filename: string; canShare: boolean; native: boolean }>();
const emit = defineEmits<{ close: []; share: [] }>();
</script>
<template>
  <ArchiveDialog :open="!!url" title="Soubor je připravený" @close="emit('close')">
    <p v-if="native">Použij systémové sdílení → Uložit do Souborů; u obrázku případně Uložit obrázek. Uložení do Fotek potvrzuje až systém.</p>
    <p v-else>Na iPhonu použij Sdílet → Uložit do Souborů; u obrázku případně Uložit obrázek. Uložení do Fotek potvrzuje až systém.</p>
    <a v-if="url && !native" class="aw-button" :href="url" :download="filename">Stáhnout soubor</a>
    <button v-if="canShare" class="aw-button aw-button--secondary" @click="emit('share')">Sdílet soubor</button>
    <p v-if="native && !canShare">Systémové sdílení není dostupné. Památku můžeš otevřít v prohlížeči se svým soukromým odkazem.</p>
  </ArchiveDialog>
</template>

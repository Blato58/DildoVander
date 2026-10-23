<script setup lang="ts">
import ArchiveDialog from './ArchiveDialog.vue';
import { customerCopy } from '../../application/customerCopy';
defineProps<{ url: string; filename: string; canShare: boolean; native: boolean }>();
const emit = defineEmits<{ close: []; share: [] }>();
</script>
<template>
  <ArchiveDialog :open="!!url" :title="customerCopy.text('keepsake.download-ready')" @close="emit('close')">
    <p v-if="native">{{ customerCopy.text('keepsake.download-native') }}</p>
    <p v-else>{{ customerCopy.text('keepsake.download-web') }}</p>
    <a v-if="url && !native" class="aw-button" :href="url" :download="filename">{{ customerCopy.text('keepsake.download') }}</a>
    <button v-if="canShare" class="aw-button aw-button--secondary" @click="emit('share')">{{ customerCopy.text('keepsake.share-file') }}</button>
    <p v-if="native && !canShare">{{ customerCopy.text('keepsake.share-unavailable') }}</p>
  </ArchiveDialog>
</template>

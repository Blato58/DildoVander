<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { bindDialog } from '../../../../design-system/ui.js';
import { customerCopy } from '../../application/customerCopy';
const props = defineProps<{ open: boolean; title: string }>();
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement>();
let binding: ReturnType<typeof bindDialog> | undefined;
watch(() => props.open, async open => {
  await nextTick();
  if (!dialog.value) return;
  binding ??= bindDialog(dialog.value, { onClose: () => emit('close') });
  if (open) binding.open(); else binding.close();
}, { immediate: true });
onBeforeUnmount(() => binding?.destroy());
</script>
<template>
  <dialog ref="dialog" class="aw-dialog archive-dialog" :aria-label="title">
    <h2>{{ title }}</h2><slot />
    <button class="aw-button aw-button--ghost" @click="emit('close')">{{ customerCopy.text('keepsake.dialog-close') }}</button>
  </dialog>
</template>
<style scoped>.archive-dialog { max-width: min(56rem, 95vw); } .archive-dialog :deep(img) { max-width: 100%; max-height: 70vh; object-fit: contain; }</style>

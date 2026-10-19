<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
const props = defineProps<{ open: boolean; title: string }>();
const emit = defineEmits<{ close: [] }>();
const dialog = ref<HTMLDialogElement>();
let previousFocus: HTMLElement | null = null;
watch(() => dialog.value && props.open, open => {
  if (open && !dialog.value?.open) {
    previousFocus = document.activeElement as HTMLElement | null;
    dialog.value?.showModal();
  } else if (!open && dialog.value?.open) {
    dialog.value.close(); previousFocus?.focus();
  }
}, { flush: 'post', immediate: true });
onBeforeUnmount(() => { dialog.value?.close(); previousFocus?.focus(); });
</script>

<template>
  <dialog ref="dialog" class="aw-dialog" :aria-label="title" @cancel.prevent="emit('close')" @close="emit('close')">
    <h2>{{ title }}</h2><slot />
    <button class="aw-button aw-button--secondary" @click="emit('close')">Zavřít</button>
  </dialog>
</template>

<script setup lang="ts">
withDefaults(defineProps<{
  variant?: 'primary' | 'secondary' | 'danger';
  type?: 'button' | 'submit' | 'reset';
  disabled?: boolean;
  busy?: boolean;
}>(), { variant: 'secondary', type: 'button', disabled: false, busy: false });
</script>

<template>
  <button :type="type" :class="['aw-button', `aw-button--${variant}`]"
    :disabled="disabled || busy" :aria-busy="busy || undefined"><slot /></button>
</template>

<script setup lang="ts">
withDefaults(defineProps<{
  tone?: 'info' | 'warning' | 'danger' | 'success';
  title?: string;
  live?: boolean;
}>(), { tone: 'info', live: true });
</script>

<template>
  <div :class="['aw-notice', `aw-notice--${tone}`]"
    :role="live ? (tone === 'danger' ? 'alert' : 'status') : undefined">
    <strong v-if="title">{{ title }}</strong><div><slot /></div>
  </div>
</template>

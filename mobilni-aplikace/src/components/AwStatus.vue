<script setup lang="ts">
withDefaults(defineProps<{ tone?: 'info' | 'warning' | 'danger' | 'success' }>(), { tone: 'info' });
</script>

<template><span :class="['aw-badge', `aw-badge--${tone}`]"><slot /></span></template>

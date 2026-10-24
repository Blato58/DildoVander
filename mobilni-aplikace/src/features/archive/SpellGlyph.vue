<script setup lang="ts">
defineProps<{ action: string }>();
</script>
<template>
  <svg class="archive-spell-glyph" viewBox="0 0 48 48" aria-hidden="true" focusable="false">
    <path v-if="action === 'shield'" d="M24 5 39 11v11c0 10-15 20-15 20S9 32 9 22V11ZM17 23l5 5 10-11" />
    <path v-else-if="action === 'trick'" d="M5 24s7-12 19-12 19 12 19 12-7 12-19 12S5 24 5 24Zm14 0a5 5 0 1 0 10 0 5 5 0 1 0-10 0M7 7l5 5m24 24 5 5M7 41l5-5M36 12l5-5" />
    <path v-else d="m29 4-18 23h12l-4 17 18-24H25ZM6 11l5 3m26 21 5 3M4 36l6-3M38 11l5-3" />
  </svg>
</template>

<script setup lang="ts">
defineProps<{ id: string; label: string; hint?: string; error?: string }>();
</script>

<template>
  <div class="aw-field">
    <label :for="id">{{ label }}</label>
    <slot :id="id" :describedby="[hint && `${id}-hint`, error && `${id}-error`].filter(Boolean).join(' ') || undefined"
      :invalid="error ? true : undefined" />
    <p v-if="hint" :id="`${id}-hint`" class="aw-field-description">{{ hint }}</p>
    <p v-if="error" :id="`${id}-error`" class="aw-field-error" role="alert">{{ error }}</p>
  </div>
</template>

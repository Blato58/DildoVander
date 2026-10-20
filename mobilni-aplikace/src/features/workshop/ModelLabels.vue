<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { graphics } from './graphics.mjs';
const props = defineProps<{ names: { id: string; label: string; point: number[] }[]; width: number; height: number }>();
const emit = defineEmits<{ select: [id: string] }>();
const root = ref<HTMLElement>(), sizes = ref<Record<string, { w: number; h: number }>>({});
const placements = computed<any[]>(() => graphics.placeLabels(props.names.map(name => ({ ...name, ...(sizes.value[name.id] || { w: 100, h: 22 }) })), props.width, props.height));
watch(() => props.names, async () => {
  await nextTick(); if (!root.value) return;
  const next = { ...sizes.value }; let changed = false;
  for (const node of root.value.querySelectorAll<HTMLElement>('[data-label-id]')) {
    const id = node.dataset.labelId!, w = node.offsetWidth, h = node.offsetHeight;
    if (next[id]?.w !== w || next[id]?.h !== h) { next[id] = { w, h }; changed = true; }
  }
  if (changed) sizes.value = next;
}, { immediate: true });
</script>

<template>
  <div ref="root" class="model-labels" role="group" aria-label="Názvy dílů v modelu">
    <svg :viewBox="`0 0 ${width} ${height}`" aria-hidden="true"><line v-for="item in placements" :key="item.id" :x1="item.point[0]" :y1="item.point[1]" :x2="item.x + item.w / 2" :y2="item.y + item.h / 2" stroke="var(--aw-muted)" stroke-opacity=".35" /></svg>
    <button v-for="item in placements" :key="item.id" :data-label-id="item.id" :style="{left: `${item.x}px`, top: `${item.y}px`}" @click="emit('select', item.id)">{{ item.label }}</button>
  </div>
</template>

<style scoped>
.model-labels, .model-labels svg { position: absolute; inset: 0; width: 100%; height: 100%; pointer-events: none; }
.model-labels button { position: absolute; max-width: 14rem; font-family: var(--aw-font-mono); font-size: 10px; padding: 2px 4px; border: 1px solid var(--aw-border-subtle); border-radius: .25rem; background: var(--aw-surface); color: var(--aw-ivory); cursor: pointer; pointer-events: auto; }
</style>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute } from 'vue-router';
import InterfaceLanguageSelect from './InterfaceLanguageSelect.vue';
import { interfaceLanguagePreference } from './interfaceLanguage';
import { customerCopy } from './customerCopy';
import { usePreferences } from './preferences';

const props = defineProps<{ accountId?: string | null }>();
const preferences = usePreferences();
const route = useRoute();
const disclosure = ref<HTMLDetailsElement>();

function close(restoreFocus = false): void {
  if (!disclosure.value?.open) return;
  disclosure.value.open = false;
  if (restoreFocus) disclosure.value.querySelector('summary')?.focus();
}
function closeOutside(event: PointerEvent): void {
  if (event.target instanceof Node && !disclosure.value?.contains(event.target)) close();
}
watch(() => route.fullPath, () => close());
watch(() => props.accountId, () => close());
onMounted(() => document.addEventListener('pointerdown', closeOutside));
onBeforeUnmount(() => document.removeEventListener('pointerdown', closeOutside));
</script>

<template>
  <details ref="disclosure" class="shell-preferences" @keydown.esc.stop.prevent="close(true)">
    <summary>{{ customerCopy.text('shell.preferences') }}</summary>
    <div class="shell-preferences-panel">
      <InterfaceLanguageSelect :preference="interfaceLanguagePreference" :account-id="accountId" />
      <label class="theme-control">
        <span>{{ customerCopy.text('shell.theme') }}</span>
        <select v-model="preferences.theme" :aria-label="customerCopy.text('shell.theme')">
          <option v-for="theme in preferences.themes" :key="theme.id" :value="theme.id">{{ theme.id === 'lord-voldemort' ? customerCopy.text('settings.theme-voldemort') : theme.id === 'olivander' ? customerCopy.text('settings.theme-olivander') : theme.label }}</option>
        </select>
      </label>
    </div>
  </details>
</template>

<style scoped>
.shell-preferences { min-width: 0; }
summary { min-height: var(--aw-touch); display: flex; align-items: center; gap: var(--aw-space-2); padding: var(--aw-space-2) var(--aw-space-3); border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-control); color: var(--aw-ivory); cursor: pointer; font-size: .85rem; list-style: none; }
summary::-webkit-details-marker { display: none; }
summary::after { content: ''; width: .4rem; height: .4rem; border-right: 1px solid currentColor; border-bottom: 1px solid currentColor; transform: rotate(45deg); margin-bottom: .2rem; }
details[open] summary { background: var(--aw-accent-soft); }
details[open] summary::after { transform: rotate(225deg); margin-bottom: -.2rem; }
.shell-preferences-panel { position: absolute; z-index: 1; top: 100%; inset-inline-end: max(.75rem, env(safe-area-inset-right)); width: min(24rem, calc(100% - 1.5rem)); max-height: max(6rem, calc(100dvh - var(--application-navigation-height, 5rem) - 9rem)); overflow-y: auto; overscroll-behavior: contain; display: grid; gap: var(--aw-space-4); padding: var(--aw-space-4); background: var(--aw-surface); border: 1px solid var(--aw-border); border-radius: var(--aw-radius-card); }
.shell-preferences-panel :deep(.interface-language-select) { width: 100%; }
.theme-control { display: grid; gap: var(--aw-space-2); font-size: var(--aw-text-small); }
.theme-control select { width: 100%; max-width: 100%; font-size: 1rem; }
</style>

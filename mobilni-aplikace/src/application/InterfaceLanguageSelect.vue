<script setup lang="ts">
import { computed, useId, watch } from 'vue';
import {
  interfaceLanguages,
  type InterfaceLanguagePreference,
} from './interfaceLanguage';

const props = defineProps<{
  preference: InterfaceLanguagePreference;
  accountId?: string | null;
}>();
const selectId = `interface-language-${useId()}`;
const hintId = `${selectId}-scope`;

watch(
  () => [props.preference, props.accountId] as const,
  ([preference, accountId]) => preference.setScope(accountId),
  { immediate: true, flush: 'sync' },
);

const selectedLanguage = computed({
  get: () => props.preference.language.value,
  set: (value: string) => { props.preference.setLanguage(value); },
});
const label = computed(() => props.preference.language.value === 'en' ? 'Interface language' : 'Jazyk rozhraní');
const scopeHint = computed(() => {
  const english = props.preference.language.value === 'en';
  const scope = props.preference.scope.value;
  const persistence = props.preference.persistenceStatus.value;
  if (persistence === 'unavailable') {
    return english
      ? 'This choice lasts only for this app session; browser storage is unavailable.'
      : 'Volba platí jen v tomto spuštění; prohlížeč ji nemůže uložit.';
  }
  if (persistence === 'unverified') {
    return english
      ? scope.kind === 'account'
        ? 'For this account on this device; persistent storage is not yet verified.'
        : 'For this device; persistent storage is not yet verified.'
      : scope.kind === 'account'
        ? 'Pro tento účet v tomto zařízení; trvalé uložení zatím není ověřené.'
        : 'Pro toto zařízení; trvalé uložení zatím není ověřené.';
  }
  return english
    ? scope.kind === 'account'
      ? 'Saved only for this account on this device.'
      : 'Saved only on this device.'
    : scope.kind === 'account'
      ? 'Uloženo pouze pro tento účet v tomto zařízení.'
      : 'Uloženo pouze na tomto zařízení.';
});
</script>

<template>
  <div class="interface-language-select">
    <label :for="selectId">{{ label }}</label>
    <select :id="selectId" v-model="selectedLanguage" :aria-describedby="hintId">
      <option v-for="language in interfaceLanguages" :key="language.value" :value="language.value">
        {{ language.label }}
      </option>
    </select>
    <small :id="hintId">{{ scopeHint }}</small>
  </div>
</template>

<style scoped>
.interface-language-select { display: grid; gap: var(--aw-space-2); min-width: 0; width: clamp(9rem, 20vw, 22rem); max-width: 100%; overflow-wrap: anywhere; }
.interface-language-select label { font-size: var(--aw-text-small); }
.interface-language-select select { width: 100%; min-width: 0; min-height: var(--aw-touch); padding: var(--aw-space-2) var(--aw-space-3); border: 1px solid var(--aw-border); border-radius: var(--aw-radius-control); background: var(--aw-void); color: var(--aw-ivory); font-size: 1rem; }
.interface-language-select small { color: var(--aw-muted); font-size: var(--aw-text-small); }
</style>

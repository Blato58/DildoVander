import { defineStore } from 'pinia';
import { computed, onScopeDispose, ref } from 'vue';
import { getTheme, setTheme, subscribe, themes, type ThemeId } from '../../../design-system/theme.mjs';

// The shared runtime owns persistence and document state for every UI surface.
// Pinia bridges its notifications into Vue without remounting application views.
export const usePreferences = defineStore('preferences', () => {
  const currentTheme = ref<ThemeId>(getTheme());
  const unsubscribe = subscribe((value: ThemeId) => { currentTheme.value = value; });
  onScopeDispose(unsubscribe);
  const theme = computed({
    get: () => currentTheme.value,
    set: (value: ThemeId) => { setTheme(value); },
  });
  return { theme, themes };
});

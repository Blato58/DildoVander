import { defineStore } from 'pinia';
import { ref, watch } from 'vue';

type Theme = 'dark' | 'light' | 'system';
const isTheme = (value: unknown): value is Theme => ['dark', 'light', 'system'].includes(String(value));

export const usePreferences = defineStore('preferences', () => {
  let initial: Theme = 'dark';
  try {
    const saved = localStorage.getItem('dildovander.theme') ?? localStorage.getItem('aw-theme');
    if (isTheme(saved)) initial = saved;
  } catch { /* Private browsing may deny storage; memory preference still works. */ }
  const theme = ref<Theme>(initial);
  watch(theme, value => {
    document.documentElement.dataset.theme = value;
    try { localStorage.setItem('dildovander.theme', value); } catch { /* Optional persistence. */ }
  }, { immediate: true });
  return { theme };
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import { createPinia, disposePinia } from 'pinia';
import { getTheme, setTheme } from '../../design-system/theme.mjs';
import { usePreferences } from '../src/application/preferences';
import { viewerThemeColors } from '../src/features/workshop/engine.mjs';

const stores: ReturnType<typeof createPinia>[] = [];
function preferences() {
  const pinia = createPinia();
  stores.push(pinia);
  return usePreferences(pinia);
}
afterEach(() => {
  stores.splice(0).forEach(disposePinia);
  setTheme('lord-voldemort');
  localStorage.clear();
  document.documentElement.style.removeProperty('--aw-viewer-background');
  document.documentElement.style.removeProperty('--aw-viewer-selection');
  vi.restoreAllMocks();
});

describe('shared application theme preference', () => {
  it('uses the registry and synchronizes independent Vue consumers immediately', () => {
    const first = preferences();
    const second = preferences();
    expect(first.themes.map(theme => theme.label)).toEqual(['Lord Voldemort', 'Olivander']);
    first.theme = 'olivander';
    expect(second.theme).toBe('olivander');
    expect(getTheme()).toBe('olivander');
    expect(document.documentElement.dataset.theme).toBe('olivander');
    expect(localStorage.getItem('dildovander.theme')).toBe('olivander');
    second.theme = 'lord-voldemort';
    expect(first.theme).toBe('lord-voldemort');
  });

  it('reflects another tab and falls back safely when its stored value is invalid', () => {
    const store = preferences();
    window.dispatchEvent(new StorageEvent('storage', { key: 'dildovander.theme', newValue: 'olivander' }));
    expect(store.theme).toBe('olivander');
    window.dispatchEvent(new StorageEvent('storage', { key: 'dildovander.theme', newValue: 'invalid' }));
    expect(store.theme).toBe('lord-voldemort');
  });

  it('continues synchronizing in memory if storage is unavailable', () => {
    const store = preferences();
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('Storage denied'); });
    store.theme = 'olivander';
    expect(store.theme).toBe('olivander');
    expect(document.documentElement.dataset.theme).toBe('olivander');
  });
});

describe('Workshop theme presentation', () => {
  it('reads current semantic colors without altering material state', () => {
    const canvas = document.createElement('canvas');
    for (const [background, selection] of [['#0f0f11', '#606acc'], ['#1b2430', '#b8c0ff']]) {
      document.documentElement.style.setProperty('--aw-viewer-background', background!);
      document.documentElement.style.setProperty('--aw-viewer-selection', selection!);
      const colors = viewerThemeColors(canvas);
      expect(colors.background).toEqual([1, 3, 5].map(index => parseInt(background!.slice(index, index + 2), 16) / 255));
      expect(colors.selection).toEqual([1, 3, 5].map(index => parseInt(selection!.slice(index, index + 2), 16) / 255));
    }
  });
});

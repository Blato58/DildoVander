/* Source for the generated synchronous bootstrap. The registry comes from tokens.json. */
(function () {
  'use strict';
  if (globalThis.DildoVanderTheme) return;
  /* THEME_REGISTRY */
  const storageKey = 'dildovander.theme';
  const listeners = new Set();
  const root = globalThis.document?.documentElement;
  const legacy = new Set(['dark', 'light', 'system']);
  const normalizeTheme = value => themes.some(theme => theme.id === value)
    ? value : legacy.has(value) ? 'olivander' : defaultTheme;
  let current = defaultTheme;
  try {
    current = normalizeTheme(globalThis.localStorage?.getItem(storageKey)
      ?? globalThis.localStorage?.getItem('aw-theme'));
  } catch { /* Optional storage: the in-memory preference remains usable. */ }

  function applyTheme() {
    if (!root) return;
    root.dataset.theme = current;
    root.style.colorScheme = 'dark';
    const background = themes.find(theme => theme.id === current).background;
    let meta = document.querySelector('meta[name="theme-color"]');
    if (!meta) {
      meta = document.createElement('meta');
      meta.name = 'theme-color';
      document.head?.append(meta);
    }
    meta.content = background;
  }

  function update(value, persist) {
    const next = normalizeTheme(value);
    const changed = next !== current;
    current = next;
    applyTheme();
    if (persist) {
      try { globalThis.localStorage?.setItem(storageKey, current); } catch { /* Optional. */ }
    }
    if (changed) listeners.forEach(listener => listener(current));
    return current;
  }

  globalThis.addEventListener?.('storage', event => {
    if (event.key === storageKey || event.key === null) update(event.newValue, false);
  });
  themes.forEach(Object.freeze);
  globalThis.DildoVanderTheme = Object.freeze({
    themes: Object.freeze(themes), defaultTheme, normalizeTheme,
    getTheme: () => current,
    setTheme: value => update(value, true),
    subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); },
  });
  applyTheme();
})();

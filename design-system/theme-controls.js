/* Standalone adapters share the same registry and preference as the Vue shell. */
(function () {
  const document = globalThis.document;
  if (!document?.querySelectorAll) return;
  function mount() {
    const runtime = globalThis.DildoVanderTheme;
    if (!runtime) return;
    const controls = [...document.querySelectorAll('[data-theme-select]')];
    for (const control of controls) {
      control.replaceChildren(...runtime.themes.map(theme => {
        const option = document.createElement('option');
        option.value = theme.id;
        option.textContent = theme.label;
        return option;
      }));
      control.value = runtime.getTheme();
      control.addEventListener('change', () => runtime.setTheme(control.value));
    }
    runtime.subscribe(theme => controls.forEach(control => { control.value = theme; }));
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount, { once: true });
  else mount();
})();

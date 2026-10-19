// A visit-only preference: the guest portal never persists browser data.
const systemTheme = window.matchMedia('(prefers-color-scheme: light)');
const controls = document.querySelectorAll('[data-theme-select]');
let preference = 'system';
function applyTheme() {
  document.documentElement.dataset.theme = preference === 'system'
    ? (systemTheme.matches ? 'light' : 'dark') : preference;
}
for (const control of controls) {
  control.addEventListener('change', () => {
    preference = control.value === 'light' || control.value === 'dark' ? control.value : 'system';
    for (const peer of controls) peer.value = preference;
    applyTheme();
  });
}
systemTheme.addEventListener('change', applyTheme);
applyTheme();

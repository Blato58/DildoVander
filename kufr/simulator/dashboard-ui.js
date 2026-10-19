import { button, field, statusBadge, notice, emptyState } from '../design-system/ui.js';

export function mountDashboardUi(document) {
  document.querySelector('#connection-controls').innerHTML = [
    field({ id: 'token', label: 'Testovací bearer token', control: '<input class="aw-input" type="password" autocomplete="off" spellcheck="false" aria-describedby="token-help connection-error">' }),
    button({ id: 'connect', label: 'Připojit pozorování', variant: 'primary' }),
    button({ id: 'disconnect', label: 'Odpojit', disabled: true }),
  ].join('');
  document.querySelector('#timeline-tools').innerHTML = [
    field({ id: 'event-filter', label: 'Filtrovat události', control: '<input class="aw-input" type="search" placeholder="Název nebo číslo přihrádky" autocomplete="off">' }),
    button({ id: 'clear-events', label: 'Vymazat zobrazení', disabled: true }),
  ].join('');
  document.querySelector('#theme-control').innerHTML = button({
    id: 'theme-toggle', label: 'Světlý režim', attributes: { 'aria-pressed': 'false' },
  });
}

export { statusBadge, notice, emptyState };

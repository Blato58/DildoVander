import { button, statusBadge, notice, emptyState, wandCard, field, bindTabs, bindDialog } from '../ui.js';

document.querySelector('#buttons').innerHTML = [
  button({ label: 'Hlavní akce', variant: 'primary' }),
  button({ label: 'Vedlejší akce' }),
  button({ label: 'Stop — ukázka', variant: 'danger', disabled: true }),
  button({ label: 'Načítání…', loading: true }),
].join('');
document.querySelector('#badges').innerHTML = [
  statusBadge({ label: 'Statická ukázka', tone: 'info' }),
  statusBadge({ label: 'Připraveno', tone: 'success' }),
  statusBadge({ label: 'Poloha není změřená', tone: 'warning' }),
].join('');
document.querySelector('#notice').innerHTML = notice({ title: 'Bez připojení', message: 'Náhled slouží k ověření vzhledu a ovládání. Neprokazuje fyzický stav kufru.', tone: 'info' });
document.querySelector('#wands').innerHTML = [
  wandCard({ title: 'První hůlka', subtitle: 'Ukázková přihrádka', number: 1, selected: true }),
  wandCard({ title: 'Druhá hůlka', subtitle: 'Výběr pouze v náhledu', number: 2 }),
  wandCard({ title: 'Třetí hůlka', subtitle: 'Nedostupná v ukázce', number: 3, disabled: true }),
].join('');
document.querySelector('#wands').addEventListener('click', (event) => {
  const card = event.target.closest('.aw-wand-card');
  if (!card || card.disabled) return;
  document.querySelectorAll('.aw-wand-card').forEach((item) => item.setAttribute('aria-pressed', String(item === card)));
});
document.querySelector('#panel-history').innerHTML = emptyState({ title: 'Zatím žádná historie', message: 'Ukázka neukládá rozhovory ani paměť návštěvníků.' });
document.querySelector('#field').innerHTML = field({ id: 'example-name', label: 'Název ukázky', control: '<input class="aw-input" type="text" value="">', description: 'Pouze místní náhled; údaje se neodesílají.', error: 'Vyplňte název pro ukázku chybového stavu.' });
document.querySelector('#dialog-action').innerHTML = button({ label: 'Otevřít ukázkový dialog', id: 'open-dialog' });
bindTabs(document.querySelector('#tabs'));
const dialog = bindDialog(document.querySelector('#example-dialog'), {
  onClose(value) { document.querySelector('#dialog-result').textContent = value === 'confirmed' ? 'Ukázka potvrzena. Focus se vrátil na tlačítko.' : 'Ukázka zrušena. Focus se vrátil na tlačítko.'; },
});
document.querySelector('#open-dialog').addEventListener('click', (event) => dialog.open(event.currentTarget));
document.querySelector('#theme').addEventListener('change', (event) => { document.documentElement.dataset.theme = event.target.value; });

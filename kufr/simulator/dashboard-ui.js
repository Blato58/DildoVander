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

export function modelGuidance({ preview, connected, current, snapshot, journalPending }) {
  if (preview) {
    return { title: 'Veřejný náhled simulace', message: 'Pro pozorování modelu otevřete místní simulátor. Tento náhled nepřijímá tokeny.', tone: 'info' };
  }
  if (!connected || !current) {
    return {
      title: connected ? 'Čeká na snapshot modelu' : 'Pozorování není připojeno',
      message: snapshot ? 'Zobrazené hodnoty jsou historické. Nové připojení musí nejprve přijmout snapshot; dostupnost ani průběh nyní nejsou potvrzeny.' : 'Připojte místní pozorování testovacím tokenem. Do přijetí snapshotu stav přihrádek není známý.',
      tone: 'neutral',
    };
  }
  if (snapshot.phase === 'fault') {
    return { title: 'Model hlásí poruchu', message: 'Prohlédněte události a chybu v ovladači simulátoru. Tento pozorovatel poruchu neruší ani neposílá Stop.', tone: 'danger' };
  }
  if (journalPending || snapshot.phase === 'recoveryRequired' || snapshot.phase === 'stopped') {
    return { title: 'Model vyžaduje zotavení', message: 'Zkontrolujte přerušenou sekvenci v ovladači simulátoru a proveďte jeho postup zotavení. Neuzavřený journal nemažte kvůli opětovnému výběru.', tone: 'warning' };
  }
  if (snapshot.phase === 'ready') {
    return { title: 'Model čeká na výběr', message: 'Výběr spusťte v ovladači simulátoru. Zde uvidíte požadavky a změny modelu; fyzický pohyb se neověřuje.', tone: 'info' };
  }
  if (snapshot.phase === 'bootSafe') {
    return { title: 'Model se připravuje', message: 'Vyčkejte na další snapshot. Start modelu sám nepotvrzuje připravenost k výběru.', tone: 'info' };
  }
  return { title: 'Probíhá simulovaná sekvence', message: 'Sledujte fázi a události níže. Případný Stop patří do ovladače simulátoru; událost pohonu je požadavek modelu.', tone: 'info' };
}

export { statusBadge, notice, emptyState };

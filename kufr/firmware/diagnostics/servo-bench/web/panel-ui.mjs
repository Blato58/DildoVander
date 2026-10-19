import { button, field, statusBadge, notice, emptyState } from '../../../../design-system/ui.js';

export function mountBenchUi(document) {
  const mount = (id, markup) => { document.getElementById(id).innerHTML = markup; };
  mount('log-empty', emptyState({ title: 'Zatím bez sériových zpráv', message: 'Po připojení USB se zde zobrazí posledních 200 řádků.' }));
  mount('connection-actions', button({ id: 'connect', label: 'Připojit USB', variant: 'primary' }) +
    button({ id: 'disconnect', label: 'Odpojit', disabled: true }));
  mount('pulse-actions', button({ id: 'arm', label: 'Arm · připravit jeden pulz', disabled: true }) +
    button({ id: 'pulse', label: 'Pulz 500 ms', variant: 'primary', disabled: true }));
  mount('calibration-actions', button({ id: 'cal-arm', label: 'Arm · připravit pokus', disabled: true }) +
    button({ id: 'cal-move', label: 'Spustit kalibrační pokus', variant: 'primary', disabled: true }));
  mount('auto-actions', button({ id: 'auto-min', label: 'Hledat dolní mez', variant: 'primary', disabled: true }) +
    button({ id: 'auto-max', label: 'Hledat horní mez', variant: 'primary', disabled: true }));
  mount('stop-actions', button({ id: 'stop', label: 'Stop', variant: 'danger', disabled: true }) +
    button({ id: 'noise', label: 'Drhne / vrčí', variant: 'danger', disabled: true }));
  mount('calibration-fields', field({ id: 'cal-width', label: 'Cílová šířka pulzu (µs)',
    control: '<input class="aw-input" type="number" min="1000" max="2000" step="1" value="1500" aria-describedby="cal-help">' }) +
    field({ id: 'cal-duration', label: 'Doba pokusu (ms)',
      control: '<input class="aw-input" type="number" min="500" max="3000" step="100" value="1500" aria-describedby="cal-help">' }));
}

export { statusBadge, notice, emptyState };

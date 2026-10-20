import { button, notice, statusBadge } from '../../design-system/ui.js';

// UI composition only. Geometry, camera, selection and simulation remain in viewer.js.
export function mountViewerUi(document) {
  const get = id => document.getElementById(id);
  const modes = [
    ['open', 'Provoz', 'operation'], ['service', 'Servis', 'service'],
    ['closed', 'Přeprava', 'transport'], ['assembly-mode', 'Montáž', 'assembly'],
  ];
  get('mode-actions').innerHTML = modes.map(([id, label, mode]) => button({
    id, label, variant: 'ghost',
    attributes: { 'aria-pressed': String(mode === 'operation'), 'data-viewer-mode': mode },
  })).join('');
  get('model-status').innerHTML = statusBadge({ label: 'Návrhový model', tone: 'neutral' });
  get('sequence-actions').innerHTML = [
    button({ id: 'play-main', label: 'Hlavní výběr', variant: 'primary' }),
    button({ id: 'play-short', label: 'Krátké opakování' }),
    button({ id: 'effect-stop', label: 'Stop', variant: 'danger' }),
  ].join('');
}

export function renderViewerError(container, message) {
  container.innerHTML = notice({ title: '3D model není dostupný', message, tone: 'danger' });
  container.hidden = false;
}

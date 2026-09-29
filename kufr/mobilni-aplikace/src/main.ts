import './style.css';
import {
  FakeDeviceConnection,
  type DeviceSnapshot,
} from './features/device_connection/FakeDeviceConnection';

type PendingAction = {
  kind: 'select' | 'reload';
  compartment: number;
};

const app = document.querySelector<HTMLElement>('#app');
if (app === null) {
  throw new Error('Chybí kořenový prvek aplikace.');
}

let pendingAction: PendingAction | null = null;
let currentSnapshot: DeviceSnapshot;

function render(snapshot: DeviceSnapshot): void {
  currentSnapshot = snapshot;
  const canControl = snapshot.controlMode === 'manual' && snapshot.phase === 'ready';
  const modeText = snapshot.controlMode === 'manual' ? 'Ruční režim' : 'Pozorování';
  const phaseText = snapshot.phase === 'running'
    ? `Probíhá výběr ${snapshot.activeCompartment}`
    : snapshot.phase === 'stopped' ? 'Zastaveno' : 'Připraveno';
  const cards = Array.from({ length: 6 }, (_, index) => {
    const compartment = index + 1;
    const ready = (snapshot.readyMask & (1 << index)) !== 0;
    const action = ready ? 'select' : 'reload';
    const label = ready ? 'Vybrat' : 'Potvrdit založení';
    return `
      <article class="compartment ${ready ? '' : 'empty'}">
        <div class="compartment-number">${compartment}</div>
        <div class="compartment-info">
          <h3>Přihrádka ${compartment}</h3>
          <p>${ready ? 'Připravena v simulaci' : 'Nedostupná v simulaci'}</p>
        </div>
        <button type="button" data-action="${action}" data-compartment="${compartment}"
          ${canControl ? '' : 'disabled'}>${label}</button>
      </article>`;
  }).join('');
  const pending = pendingAction === null ? '' : `
    <section class="confirmation" aria-labelledby="confirm-title">
      <h2 id="confirm-title">Potvrdit ${pendingAction.kind === 'select' ? 'výběr' : 'založení'}?</h2>
      <p>${pendingAction.kind === 'select'
    ? `Spustit simulovaný výběr přihrádky ${pendingAction.compartment}.`
    : `Potvrzuji kontrolu a založení přihrádky ${pendingAction.compartment} pouze v simulaci.`}</p>
      <div class="actions">
        <button type="button" data-action="cancel">Zrušit</button>
        <button type="button" class="primary" data-action="confirm">Potvrdit</button>
      </div>
    </section>`;

  app!.innerHTML = `
    <header>
      <p class="eyebrow">AI KUFR · VÝVOJOVÝ OVLADAČ</p>
      <h1>Ruční ovládání</h1>
      <div class="simulation">SIMULACE · Bez připojení ke kufru</div>
    </header>
    <main>
      <section class="overview" aria-label="Stav simulace">
        <div><span>Režim</span><strong>${modeText}</strong></div>
        <div><span>Stav</span><strong>${phaseText}</strong></div>
        <div><span>Připraveno</span><strong>${snapshot.readyMask.toString(2).replace(/0/g, '').length} / 6</strong></div>
      </section>
      <p class="status" role="status">${snapshot.status}</p>
      <div class="toolbar">
        ${snapshot.controlMode === 'observer'
    ? `<button type="button" class="primary" data-action="acquire"
             ${snapshot.phase === 'stopped' ? 'disabled' : ''}>Převzít řízení simulace</button>`
    : '<button type="button" data-action="release">Uvolnit řízení</button>'}
        <button type="button" class="stop" data-action="stop"
          ${snapshot.phase === 'stopped' ? 'disabled' : ''}>Stop simulace</button>
      </div>
      ${snapshot.phase === 'stopped' ? `
        <section class="recovery">
          <strong>Výsledek je nejistý</strong>
          <p>Po skutečném Stop by byla nutná místní kontrola kufru. Zde lze obnovit pouze fake stav.</p>
          <button type="button" data-action="inspect">Simulovat místní kontrolu</button>
        </section>` : ''}
      <section aria-labelledby="compartments-title">
        <div class="section-heading">
          <h2 id="compartments-title">Přihrádky</h2>
          <span>1–6</span>
        </div>
        <div class="compartments">${cards}</div>
      </section>
      ${pending}
      <p class="footnote">Tato stránka neposílá povely do zařízení. Stavy a časování jsou jen místní fake.</p>
    </main>`;
}

const connection = new FakeDeviceConnection(render);

app.addEventListener('click', (event) => {
  const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-action]');
  if (button === null || button.disabled) {
    return;
  }
  const compartment = Number(button.dataset.compartment);
  switch (button.dataset.action) {
    case 'acquire':
      connection.acquire();
      break;
    case 'release':
      pendingAction = null;
      connection.release();
      break;
    case 'stop':
      pendingAction = null;
      connection.stop();
      break;
    case 'inspect':
      connection.inspectLocally();
      break;
    case 'select':
    case 'reload':
      if (!Number.isInteger(compartment) || compartment < 1 || compartment > 6) {
        return;
      }
      pendingAction = { kind: button.dataset.action, compartment };
      render(currentSnapshot);
      app.querySelector<HTMLButtonElement>('[data-action="cancel"]')?.focus();
      break;
    case 'cancel':
      pendingAction = null;
      render(currentSnapshot);
      break;
    case 'confirm': {
      const action = pendingAction;
      pendingAction = null;
      if (action === null) {
        return;
      }
      if (action.kind === 'select') {
        connection.select(action.compartment);
      } else {
        connection.confirmReload(action.compartment);
      }
      break;
    }
  }
});

window.setInterval(() => {
  if (!document.hidden) {
    connection.heartbeat();
  }
}, 2000);

document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    pendingAction = null;
    connection.release();
  }
});

window.addEventListener('pagehide', () => connection.release());

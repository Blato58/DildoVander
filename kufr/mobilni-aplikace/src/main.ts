import './style.css';
import {
  FakeDeviceConnection,
  type DeviceSnapshot,
} from './features/device_connection/FakeDeviceConnection';
import { SimulatorDeviceConnection } from './features/device_connection/SimulatorDeviceConnection';

type PendingAction = {
  kind: 'select' | 'reload';
  compartment: number;
  revision: number | undefined;
};

const app = document.querySelector<HTMLElement>('#app');
if (app === null) {
  throw new Error('Chybí kořenový prvek aplikace.');
}

let pendingAction: PendingAction | null = null;
let currentSnapshot: DeviceSnapshot;
const modeResponse = await fetch('/dev-mode').catch(() => null);
const simulatorMode = modeResponse?.ok &&
  modeResponse.headers.get('content-type')?.includes('application/json') &&
  (await modeResponse.json() as { simulator?: boolean }).simulator === true;

function render(snapshot: DeviceSnapshot): void {
  if (snapshot.phase !== 'ready' || snapshot.controlMode !== 'manual' ||
      snapshot.busy || (pendingAction !== null &&
      pendingAction.revision !== snapshot.revision)) {
    pendingAction = null;
  }
  currentSnapshot = snapshot;
  const canControl = snapshot.controlMode === 'manual' && snapshot.phase === 'ready' &&
    !snapshot.busy;
  const modeText = snapshot.controlMode === 'manual' ? 'Ruční režim' : 'Pozorování';
  const phaseText = snapshot.phase === 'running'
    ? snapshot.activeCompartment === null
      ? 'Probíhá výběr'
      : `Probíhá výběr ${snapshot.activeCompartment}`
    : snapshot.phase === 'stopped' ? 'Zastaveno'
      : snapshot.phase === 'disconnected' ? 'Odpojeno' : 'Připraveno';
  const cards = Array.from({ length: 6 }, (_, index) => {
    const compartment = index + 1;
    const ready = (snapshot.readyMask & (1 << index)) !== 0;
    const disconnected = snapshot.phase === 'disconnected';
    const action = ready ? 'select' : 'reload';
    const label = disconnected ? 'Nedostupné' : ready ? 'Vybrat' : 'Potvrdit založení';
    return `
      <article class="compartment ${ready && !disconnected ? '' : 'empty'}">
        <div class="compartment-number">${compartment}</div>
        <div class="compartment-info">
          <h3>Přihrádka ${compartment}</h3>
          <p>${disconnected ? 'Stav není aktuální' : ready
    ? 'Připravena v simulaci' : 'Nedostupná v simulaci'}</p>
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
      <div class="simulation">SIMULACE · ${snapshot.source === 'local-fake'
    ? 'Místní fake bez připojení'
    : 'E2 hostitelský simulátor, bez fyzického kufru'}</div>
    </header>
    <main>
      <section class="overview" aria-label="Stav simulace">
        <div><span>Režim</span><strong>${modeText}</strong></div>
        <div><span>Stav</span><strong>${phaseText}</strong></div>
        <div><span>Připraveno</span><strong>${snapshot.phase === 'disconnected'
    ? '? / 6' : snapshot.readyMask.toString(2).replace(/0/g, '').length + ' / 6'}</strong></div>
      </section>
      <p class="status" role="status">${snapshot.status}</p>
      <div class="toolbar">
        ${snapshot.controlMode === 'observer'
    ? `<button type="button" class="primary" data-action="acquire"
             ${snapshot.phase !== 'ready' || snapshot.busy ? 'disabled' : ''}>Převzít řízení simulace</button>`
    : '<button type="button" data-action="release">Uvolnit řízení</button>'}
        <button type="button" class="stop" data-action="stop"
          ${snapshot.phase === 'stopped' || snapshot.phase === 'disconnected' ? 'disabled' : ''}>Stop simulace</button>
      </div>
      ${snapshot.phase === 'stopped' ? `
        <section class="recovery">
          <strong>Výsledek je nejistý</strong>
          <p>${snapshot.source === 'local-fake'
    ? 'Po skutečném Stop by byla nutná místní kontrola kufru. Zde lze obnovit pouze fake stav.'
    : 'Simulátor vyžaduje servisní kontrolu mimo mobilní UI. Výsledek otevření není fyzicky ověřen.'}</p>
          ${snapshot.source === 'local-fake'
    ? '<button type="button" data-action="inspect">Simulovat místní kontrolu</button>' : ''}
        </section>` : ''}
      <section aria-labelledby="compartments-title">
        <div class="section-heading">
          <h2 id="compartments-title">Přihrádky</h2>
          <span>1–6</span>
        </div>
        <div class="compartments">${cards}</div>
      </section>
      ${pending}
      <p class="footnote">${snapshot.source === 'local-fake'
    ? 'Tato stránka neposílá povely do zařízení. Stavy a časování jsou jen místní fake.'
    : 'Povely jdou pouze do místního E2 C++ simulátoru. Žádný fyzický kufr není připojen.'}</p>
    </main>`;
}

const connection = simulatorMode
  ? new SimulatorDeviceConnection(render)
  : new FakeDeviceConnection(render);

app.addEventListener('click', (event) => {
  const target = event.target;
  const element = target instanceof Element
    ? target
    : target instanceof Node ? target.parentElement : null;
  const button = element?.closest<HTMLButtonElement>('button[data-action]') ?? null;
  if (button === null || button.disabled) {
    return;
  }
  const compartment = Number(button.dataset.compartment);
  switch (button.dataset.action) {
    case 'acquire':
      if (!connection.acquire()) {
        render({ ...currentSnapshot, status: 'Převzetí nyní není dostupné; obnovte spojení nebo stav.' });
      }
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
      if (connection instanceof FakeDeviceConnection) {
        connection.inspectLocally();
      }
      break;
    case 'select':
    case 'reload':
      if (!Number.isInteger(compartment) || compartment < 1 || compartment > 6) {
        return;
      }
      pendingAction = {
        kind: button.dataset.action,
        compartment,
        revision: currentSnapshot.revision,
      };
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
    if (connection instanceof SimulatorDeviceConnection) {
      connection.suspend();
    }
  } else if (connection instanceof SimulatorDeviceConnection) {
    connection.connect();
  }
});

window.addEventListener('pagehide', () => {
  connection.release();
  if (connection instanceof SimulatorDeviceConnection) {
    connection.suspend();
  }
});

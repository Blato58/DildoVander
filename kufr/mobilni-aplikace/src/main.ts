import './style.css';
import { Capacitor } from '@capacitor/core';
import { NativeDeviceConnection, nativeBridge, type SavedNativeDevice } from './features/device_connection/NativeDeviceConnection';
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
const nativeMode = Capacitor.getPlatform() === 'ios';
let connection: FakeDeviceConnection | SimulatorDeviceConnection | NativeDeviceConnection | null = null;
const modeResponse = nativeMode ? null : await fetch('/dev-mode').catch(() => null);
const simulatorMode = modeResponse?.ok &&
  modeResponse.headers.get('content-type')?.includes('application/json') &&
  (await modeResponse.json() as { simulator?: boolean }).simulator === true;

function escapeText(value: string): string {
  return value.replace(/[&<>"']/g, (character) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[character]!));
}

function render(snapshot: DeviceSnapshot): void {
  const physical = snapshot.source === 'native-device';
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
    ? physical ? 'Připravena podle kufru' : 'Připravena v simulaci'
    : physical ? 'Nepřipravená podle kufru' : 'Nedostupná v simulaci'}</p>
        </div>
        <button type="button" data-action="${action}" data-compartment="${compartment}"
          ${canControl ? '' : 'disabled'}>${label}</button>
      </article>`;
  }).join('');
  const pending = pendingAction === null ? '' : `
    <section class="confirmation" aria-labelledby="confirm-title">
      <h2 id="confirm-title">Potvrdit ${pendingAction.kind === 'select' ? 'výběr' : 'založení'}?</h2>
      <p>${pendingAction.kind === 'select'
    ? `Spustit ${physical ? 'ruční' : 'simulovaný'} výběr přihrádky ${pendingAction.compartment}.`
    : `Potvrzuji kontrolu a založení přihrádky ${pendingAction.compartment}${physical ? ' v kufru' : ' pouze v simulaci'}.`}</p>
      <div class="actions">
        <button type="button" data-action="cancel">Zrušit</button>
        <button type="button" class="primary" data-action="confirm">Potvrdit</button>
      </div>
    </section>`;

  app!.innerHTML = `
    <header>
      <p class="eyebrow">AI KUFR · ${physical ? 'MÍSTNÍ OVLADAČ' : 'VÝVOJOVÝ OVLADAČ'}</p>
      <h1>Ruční ovládání</h1>
      <div class="simulation">${physical ? 'MÍSTNÍ KUFR · Nativní připojení' : `SIMULACE · ${snapshot.source === 'local-fake'
    ? 'Místní fake bez připojení'
    : 'E2 hostitelský simulátor, bez fyzického kufru'}`}</div>
    </header>
    <main>
      <section class="overview" aria-label="Stav kufru">
        <div><span>Režim</span><strong>${modeText}</strong></div>
        <div><span>Stav</span><strong>${phaseText}</strong></div>
        <div><span>Připraveno</span><strong>${snapshot.phase === 'disconnected'
    ? '? / 6' : snapshot.readyMask.toString(2).replace(/0/g, '').length + ' / 6'}</strong></div>
      </section>
      <p class="status" role="status">${escapeText(snapshot.status)}</p>
      <div class="toolbar">
        ${snapshot.controlMode === 'observer'
    ? `<button type="button" class="primary" data-action="acquire"
             ${snapshot.phase !== 'ready' || snapshot.busy ? 'disabled' : ''}>${physical ? 'Převzít ruční řízení' : 'Převzít řízení simulace'}</button>`
    : '<button type="button" data-action="release">Uvolnit řízení</button>'}
        <button type="button" class="stop" data-action="stop"
          ${snapshot.phase === 'stopped' || snapshot.phase === 'disconnected' ? 'disabled' : ''}>${physical ? 'Stop kufru' : 'Stop simulace'}</button>
      </div>
      ${snapshot.phase === 'stopped' ? `
        <section class="recovery">
          <strong>Výsledek je nejistý</strong>
          <p>${snapshot.source === 'local-fake'
    ? 'Po skutečném Stop by byla nutná místní kontrola kufru. Zde lze obnovit pouze fake stav.'
    : physical ? 'Je nutná místní kontrola kufru. Telefon neověřuje polohu přihrádky a neobchází servisní zotavení.'
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
    : physical ? 'Povely jdou přímo do spárovaného kufru v LAN. Po ACK čekejte na výsledek a zkontrolujte přihrádku.'
    : 'Povely jdou pouze do místního E2 C++ simulátoru. Žádný fyzický kufr není připojen.'}</p>
    </main>`;
}

if (nativeMode) {
  await initializeNativeAsync();
} else {
  connection = simulatorMode
    ? new SimulatorDeviceConnection(render)
    : new FakeDeviceConnection(render);
}

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
      if (!connection?.acquire()) {
        render({ ...currentSnapshot, status: 'Převzetí nyní není dostupné; obnovte spojení nebo stav.' });
      }
      break;
    case 'release':
      pendingAction = null;
      connection?.release();
      break;
    case 'stop':
      pendingAction = null;
      connection?.stop();
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
        connection?.select(action.compartment);
      } else {
        connection?.confirmReload(action.compartment);
      }
      break;
    }
  }
});

window.setInterval(() => {
  if (!document.hidden) {
    connection?.heartbeat();
  }
}, 2000);

document.addEventListener('visibilitychange', () => {
  if (document.hidden) {
    pendingAction = null;
    connection?.release();
    if ((connection instanceof SimulatorDeviceConnection || connection instanceof NativeDeviceConnection)) {
      connection.suspend();
    }
  } else if (connection instanceof SimulatorDeviceConnection) {
    connection.connect();
  }
});

window.addEventListener('pagehide', () => {
  connection?.release();
  if ((connection instanceof SimulatorDeviceConnection || connection instanceof NativeDeviceConnection)) {
    connection.suspend();
  }
});

async function initializeNativeAsync(): Promise<void> {
  const settings = document.createElement('section');
  settings.className = 'connection-settings';
  settings.setAttribute('aria-label', 'Párování a místní připojení');
  settings.innerHTML = `
    <h2>Připojení ke kufru</h2>
    <p>Údaje opište z QR uvnitř kufru. Tajemství zadáte až v nativním dialogu.</p>
    <form>
      <label>Adresa v místní síti<input name="host" autocomplete="off" autocapitalize="none" required maxlength="253" placeholder="192.168.1.50"></label>
      <label>Device ID<input name="deviceId" autocomplete="off" autocapitalize="none" required maxlength="64"></label>
      <label>Otisk SPKI (SHA-256, malé hex znaky)<input name="spkiSha256" autocomplete="off" autocapitalize="none" required pattern="[0-9a-f]{64}" minlength="64" maxlength="64"></label>
      <button type="submit">Spárovat kufr</button>
    </form>
    <div class="actions">
      <button type="button" data-native="connect" disabled>Připojit uložený kufr</button>
      <button type="button" data-native="disconnect" disabled>Odpojit</button>
      <button type="button" data-native="forget" disabled>Zapomenout párování</button>
    </div>
    <p role="status"></p>`;
  app!.before(settings);
  const form = settings.querySelector<HTMLFormElement>('form')!;
  const status = settings.querySelector<HTMLElement>('[role="status"]')!;
  const connectButton = settings.querySelector<HTMLButtonElement>('[data-native="connect"]')!;
  const disconnectButton = settings.querySelector<HTMLButtonElement>('[data-native="disconnect"]')!;
  const forgetButton = settings.querySelector<HTMLButtonElement>('[data-native="forget"]')!;
  const unavailable = (message: string) => render({
    source: 'native-device', controlMode: 'observer', phase: 'disconnected',
    readyMask: 0, activeCompartment: null, busy: false, status: message,
  });
  unavailable('Kufr je odpojený. Nejprve zkontrolujte nebo vytvořte párování.');
  let paired = false;
  let busy = false;
  const update = () => {
    form.hidden = paired;
    connectButton.disabled = busy || !paired;
    disconnectButton.disabled = busy || !paired;
    forgetButton.disabled = busy || !paired;
    form.querySelector<HTMLButtonElement>('button')!.disabled = busy;
  };
  const applySavedDevice = (saved: SavedNativeDevice) => {
    paired = saved.paired && typeof saved.deviceId === 'string' && typeof saved.host === 'string';
    connection?.release();
    if (connection instanceof NativeDeviceConnection) { connection.suspend(); }
    connection = paired ? new NativeDeviceConnection(render, saved.deviceId!) : null;
    status.textContent = paired ? `Uložený kufr ${saved.deviceId} · ${saved.host}. Připojení převezme jen pozorování.`
      : 'Žádný kufr není spárovaný.';
    unavailable('Kufr je odpojený. Připojte jej výslovně tlačítkem.');
    update();
  };
  try { applySavedDevice(await nativeBridge.savedDevice()); }
  catch { status.textContent = 'Párování nelze přečíst. Odemkněte telefon; falešné ovládání se nezapne.'; }
  const performAsync = async (action: () => Promise<void>) => {
    if (busy) { return; }
    busy = true;
    update();
    try { await action(); }
    catch (error) { status.textContent = error instanceof Error ? error.message : 'Připojení selhalo.'; }
    finally { busy = false; update(); }
  };
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (!form.reportValidity()) { return; }
    const fields = new FormData(form);
    void performAsync(async () => {
      await nativeBridge.pair({
        host: String(fields.get('host')), deviceId: String(fields.get('deviceId')),
        spkiSha256: String(fields.get('spkiSha256')),
      });
      form.reset();
      applySavedDevice(await nativeBridge.savedDevice());
    });
  });
  connectButton.addEventListener('click', () => {
    if (!document.hidden && connection instanceof NativeDeviceConnection) { connection.connect(); }
  });
  disconnectButton.addEventListener('click', () => {
    connection?.release();
    if (connection instanceof NativeDeviceConnection) { connection.suspend(); }
  });
  forgetButton.addEventListener('click', () => {
    void performAsync(async () => {
      await nativeBridge.forget();
      applySavedDevice(await nativeBridge.savedDevice());
    });
  });
}

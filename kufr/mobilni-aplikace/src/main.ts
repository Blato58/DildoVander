import './style.css';
import { VoicePanel } from './features/voice_control/VoicePanel';
import { Capacitor } from '@capacitor/core';
import { NativeDeviceConnection, nativeBridge, type SavedNativeDevice } from './features/device_connection/NativeDeviceConnection';
import {
  FakeDeviceConnection,
  type DeviceSnapshot,
} from './features/device_connection/FakeDeviceConnection';
import { SimulatorDeviceConnection } from './features/device_connection/SimulatorDeviceConnection';
import { DebugPanel } from './features/debug/DebugPanel';
import { ProvisioningPanel } from './features/provisioning/ProvisioningPanel';

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

// Keep the draft outside the snapshot-rendered controls so heartbeat never
// destroys focus or partially typed text. Content stays only in page memory.
const conversationPanel = document.createElement('section');
conversationPanel.className = 'conversation-panel';
conversationPanel.setAttribute('aria-label', 'Textový rozhovor přes kufr');
conversationPanel.innerHTML = `
  <h2>Textový rozhovor</h2>
  <p class="conversation-explanation"></p>
  <form>
    <label for="visitor-text">Vaše odpověď</label>
    <textarea id="visitor-text" name="text" rows="3" maxlength="2000" autocomplete="off" required></textarea>
    <div class="actions">
      <button type="submit" class="primary" disabled>Odeslat text kufru</button>
      <button type="button" data-conversation="cancel" disabled>Zrušit tah</button>
    </div>
  </form>
  <p role="status"></p>
  <div class="conversation-reply" hidden><strong></strong><p></p></div>`;
app.after(conversationPanel);
const controlPanel = document.createElement('div');
controlPanel.id = 'control-panel';
controlPanel.setAttribute('role', 'tabpanel');
controlPanel.setAttribute('aria-labelledby', 'tab-control');
app.before(controlPanel);
controlPanel.append(app, conversationPanel);
const voicePanel = new VoicePanel(() => connection instanceof NativeDeviceConnection ? connection : null);
conversationPanel.before(voicePanel.element);
const debugPanel = new DebugPanel(nativeMode, () => {
  pendingAction = null;
  connection?.stop();
});
controlPanel.after(debugPanel.element);
const provisioningPanel = new ProvisioningPanel(nativeMode);
debugPanel.element.after(provisioningPanel.element);
const tabs = document.createElement('nav');
tabs.className = 'app-tabs';
tabs.setAttribute('aria-label', 'Sekce aplikace');
tabs.innerHTML = `<div role="tablist" aria-label="Sekce aplikace">
  <button type="button" role="tab" id="tab-control" aria-controls="control-panel" aria-selected="true">Ovládání</button>
  <button type="button" role="tab" id="tab-debug" aria-controls="debug-panel" aria-selected="false" tabindex="-1">Debug</button>
  <button type="button" role="tab" id="tab-provisioning" aria-controls="provisioning-panel" aria-selected="false" tabindex="-1">Síť kufru</button></div>`;
controlPanel.before(tabs);
function selectTab(id: string): void {
  controlPanel.hidden = id !== 'tab-control';
  debugPanel.show(id === 'tab-debug');
  provisioningPanel.show(id === 'tab-provisioning');
  for (const button of Array.from(tabs.querySelectorAll<HTMLButtonElement>('[role="tab"]'))) {
    const selected = button.id === id;
    button.setAttribute('aria-selected', String(selected));
    button.tabIndex = selected ? 0 : -1;
  }
}
tabs.addEventListener('click', (event) => {
  const button = (event.target as HTMLElement).closest('[role="tab"]');
  if (button) { selectTab(button.id); }
});
tabs.addEventListener('keydown', (event) => {
  if (['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
    event.preventDefault();
    const buttons = Array.from(tabs.querySelectorAll<HTMLButtonElement>('[role="tab"]'));
    const current = buttons.findIndex((button) => button.getAttribute('aria-selected') === 'true');
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
      : (current + (event.key === 'ArrowRight' ? 1 : -1) + buttons.length) % buttons.length;
    selectTab(buttons[index].id);
    tabs.querySelector<HTMLButtonElement>('[aria-selected="true"]')!.focus();
  }
});
const conversationForm = conversationPanel.querySelector<HTMLFormElement>('form')!;
const visitorText = conversationPanel.querySelector<HTMLTextAreaElement>('textarea')!;
const sendTextButton = conversationPanel.querySelector<HTMLButtonElement>('[type="submit"]')!;
const cancelTurnButton = conversationPanel.querySelector<HTMLButtonElement>('[data-conversation="cancel"]')!;

function renderConversation(snapshot: DeviceSnapshot): void {
  conversationPanel.hidden = snapshot.source === 'local-fake';
  conversationPanel.querySelector<HTMLElement>('.conversation-explanation')!.textContent =
    snapshot.source === 'native-device'
      ? 'Kufr předá text své cloudové AI službě. Textové výměny uchovává paměť večera na backendu, dokud ji obsluha výslovně nesmaže nebo se nevytěsní nejstarší obsah. Telefon se připojuje jen ke kufru.'
      : 'Simulátor vrací pevnou repliku. Text neopouští místní vývojové prostředí; žádná skutečná AI není připojena.';
  const conversation = snapshot.conversation;
  visitorText.disabled = conversation?.canSubmit !== true;
  sendTextButton.disabled = conversation?.canSubmit !== true;
  cancelTurnButton.disabled = conversation?.canCancel !== true;
  if (snapshot.phase === 'disconnected' || snapshot.phase === 'stopped' || snapshot.controlMode !== 'manual') {
    visitorText.value = '';
  }
  conversationPanel.querySelector<HTMLElement>('[role="status"]')!.textContent = conversation?.status ?? '';
  const reply = conversationPanel.querySelector<HTMLElement>('.conversation-reply')!;
  reply.hidden = conversation?.reply == null;
  reply.querySelector<HTMLElement>('strong')!.textContent = conversation?.simulated ? 'SIMULOVANÁ ODPOVĚĎ' : 'Odpověď kufru';
  reply.querySelector<HTMLElement>('p')!.textContent = conversation?.reply ?? '';
}

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
  debugPanel.update(snapshot);
  renderConversation(snapshot);
  voicePanel.update(snapshot);
  const canControl = snapshot.controlMode === 'manual' && snapshot.phase === 'ready' &&
    !snapshot.busy && !snapshot.conversation?.pending;
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

conversationForm.addEventListener('submit', (event) => {
  event.preventDefault();
  if (sendTextButton.disabled || !(connection instanceof SimulatorDeviceConnection || connection instanceof NativeDeviceConnection)) {
    return;
  }
  if (connection.submitText(visitorText.value)) {
    visitorText.value = '';
  }
});
cancelTurnButton.addEventListener('click', () => {
  if (connection instanceof SimulatorDeviceConnection || connection instanceof NativeDeviceConnection) {
    visitorText.value = '';
    connection.cancelConversation();
  }
});

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
    <button type="button" data-native="discover">Hledat kufr v síti (5 s)</button>
    <p>Telefon a kufr musí být ve stejné místní síti. Nalezené údaje jsou jen vodítko;
      identitu ověřuje otisk z QR. Bez nálezu zadejte IP nebo nastavte DHCP rezervaci v routeru.</p>
    <div data-discovery-results></div>
    <form>
      <label>Adresa v místní síti<input name="host" autocomplete="off" autocapitalize="none" required maxlength="253" placeholder="192.168.1.50"></label>
      <label>Device ID<input name="deviceId" autocomplete="off" autocapitalize="none" required pattern="[a-z]([a-z0-9]|-){0,63}" maxlength="64"></label>
      <label>Otisk SPKI (SHA-256, malé hex znaky)<input name="spkiSha256" autocomplete="off" autocapitalize="none" required pattern="[0-9a-f]{64}" minlength="64" maxlength="64"></label>
      <button type="submit">Spárovat kufr</button>
    </form>
    <form data-address hidden>
      <label>Nová IP nebo DNS adresa uloženého kufru<input name="address" autocomplete="off" autocapitalize="none" required maxlength="253"></label>
      <button type="submit">Použít adresu</button>
    </form>
    <div class="actions">
      <button type="button" data-native="connect" disabled>Připojit uložený kufr</button>
      <button type="button" data-native="disconnect" disabled>Odpojit</button>
      <button type="button" data-native="forget" disabled>Zapomenout párování</button>
    </div>
    <p role="status"></p>`;
  app!.before(settings);
  const form = settings.querySelector<HTMLFormElement>('form')!;
  const addressForm = settings.querySelector<HTMLFormElement>('[data-address]')!;
  const addressInput = addressForm.querySelector<HTMLInputElement>('input')!;
  const discoverButton = settings.querySelector<HTMLButtonElement>('[data-native="discover"]')!;
  const discoveryResults = settings.querySelector<HTMLElement>('[data-discovery-results]')!;
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
  let savedDeviceId: string | undefined;
  let busy = false;
  const update = () => {
    form.hidden = paired;
    addressForm.hidden = !paired;
    discoverButton.disabled = busy;
    addressForm.querySelector<HTMLButtonElement>('button')!.disabled = busy;
    discoveryResults.querySelectorAll<HTMLButtonElement>('button').forEach((button) => { button.disabled = busy; });
    connectButton.disabled = busy || !paired;
    disconnectButton.disabled = busy || !paired;
    forgetButton.disabled = busy || !paired;
    form.querySelector<HTMLButtonElement>('button')!.disabled = busy;
  };
  const applySavedDevice = (saved: SavedNativeDevice) => {
    paired = saved.paired && typeof saved.deviceId === 'string' && typeof saved.host === 'string';
    savedDeviceId = paired ? saved.deviceId : undefined;
    addressInput.value = paired ? saved.host! : '';
    discoveryResults.replaceChildren();
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
  const changeAddressAsync = async (host: string) => {
    connection?.release();
    if (connection instanceof NativeDeviceConnection) { connection.suspend(); }
    await nativeBridge.setAddress({ host });
    applySavedDevice(await nativeBridge.savedDevice());
  };
  addressForm.addEventListener('submit', (event) => {
    event.preventDefault();
    if (addressForm.reportValidity()) {
      void performAsync(() => changeAddressAsync(addressInput.value));
    }
  });
  discoverButton.addEventListener('click', () => {
    void performAsync(async () => {
      discoveryResults.replaceChildren();
      status.textContent = 'Hledám kufr v místní síti, nejvýše 5 sekund…';
      const result = await nativeBridge.discover();
      const devices = result.devices.filter((device) => !paired || device.deviceId === savedDeviceId);
      status.textContent = result.failed
        ? 'Místní hledání není dostupné. Povolte aplikaci místní síť v Nastavení, ověřte Wi-Fi nebo zadejte IP.'
        : devices.length === 0
          ? 'Za 5 sekund nebyl nalezen odpovídající kufr. Zadejte IP nebo nastavte DHCP rezervaci v routeru.'
          : 'Nalezené adresy nejsou ověřená identita. Vyberte adresu; připojení potom spustíte výslovně.';
      for (const device of devices) {
        const button = document.createElement('button');
        button.type = 'button';
        button.textContent = `${device.deviceId} · ${device.host}`;
        button.addEventListener('click', () => {
          if (paired) {
            void performAsync(() => changeAddressAsync(device.host));
          } else {
            form.querySelector<HTMLInputElement>('[name="host"]')!.value = device.host;
            form.querySelector<HTMLInputElement>('[name="deviceId"]')!.value = device.deviceId;
            status.textContent = 'Adresa je vyplněná. Device ID a otisk ověřte podle QR uvnitř kufru.';
          }
        });
        discoveryResults.append(button);
      }
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

import { bindTabs, button, emptyState, escapeHtml, field, notice, statusBadge, wandCard } from '../../design-system/ui.js';
import { primitives } from '../../design-system/tokens.mjs';
import './style.css';
import { VoicePanel } from './features/voice_control/VoicePanel';
import { DancePanel } from './features/dance/DancePanel';
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
let stopPending = false;
let stopSubmitting = false;
let stopStatus = '';
const nativeMode = Capacitor.getPlatform() === 'ios';
let connection: FakeDeviceConnection | SimulatorDeviceConnection | NativeDeviceConnection | null = null;
// Static Companion explicitly selects fake mode; local Vite still discovers E2.
const publishedFakeMode = document.querySelector<HTMLMetaElement>('meta[name="kufr-browser-mode"]')?.content === 'local-fake';
const modeResponse = nativeMode || publishedFakeMode ? null : await fetch('/dev-mode').catch(() => null);
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
    ${field({ id: 'visitor-text', label: 'Vaše odpověď', control: '<textarea class="aw-input" name="text" rows="3" maxlength="2000" autocomplete="off" required></textarea>' })}
    <div class="actions">
      ${button({ label: "Odeslat text kufru", variant: 'primary', type: 'submit', disabled: true })}
      ${button({ label: "Zrušit tah", disabled: true, attributes: {"data-conversation": "cancel"} })}
    </div>
  </form>
  <p role="status"></p>
  <div class="conversation-reply" hidden><strong></strong><p></p></div>`;
app.after(conversationPanel);
const controlPanel = document.createElement('div');
controlPanel.id = 'control-panel';
controlPanel.setAttribute('role', 'tabpanel');
controlPanel.setAttribute('aria-labelledby', 'tab-control');
controlPanel.tabIndex = -1;
app.before(controlPanel);
controlPanel.append(app, conversationPanel);
const voicePanel = new VoicePanel(() => connection instanceof NativeDeviceConnection ? connection : null);
conversationPanel.before(voicePanel.element);
const dancePanel = new DancePanel(() => connection instanceof NativeDeviceConnection ? connection : null);
voicePanel.element.after(dancePanel.element);
const performanceTools = document.createElement('div');
performanceTools.className = 'performance-tools';
controlPanel.append(performanceTools);
performanceTools.append(voicePanel.element, dancePanel.element, conversationPanel);
const debugPanel = new DebugPanel(nativeMode, stopDevice);
// Debug refreshes its button independently. Native fieldset disabling keeps the
// same in-flight guard effective across those refreshes without changing telemetry.
const debugStopGuard = document.createElement('fieldset');
debugStopGuard.className = 'device-stop-guard';
const debugStop = debugPanel.element.querySelector<HTMLButtonElement>('[data-debug="esp-stop"]')!;
debugStop.before(debugStopGuard);
debugStopGuard.append(debugStop);
controlPanel.after(debugPanel.element);
let refreshNativePairing: (() => Promise<void>) | undefined;
const provisioningPanel = new ProvisioningPanel(nativeMode, async () => { await refreshNativePairing?.(); });
debugPanel.element.after(provisioningPanel.element);
const tabs = document.createElement('nav');
tabs.className = 'app-tabs';
tabs.setAttribute('aria-label', 'Sekce aplikace');
tabs.innerHTML = `<a class="skip-link" href="#control-panel">Přejít k obsahu</a><div class="workshop-bar">
  <div class="workshop-brand"><svg class="workshop-mark" viewBox="0 0 32 32" fill="none" aria-hidden="true"><rect x="5" y="9" width="22" height="18" rx="4"/><path d="M11 9V5h10v4M12 17h8M16 13v8"/></svg>
    <strong>DildoVander<span>Ovladač kufru</span></strong></div>
  <div class="shell-actions"><button type="button" class="aw-button aw-button--secondary theme-toggle" data-theme-toggle aria-label="Přepnout na světlý vzhled"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.5 1.5m11 11L19 19M5 19l1.5-1.5m11-11L19 5"/></svg></button>
  ${button({ label: "Stop kufru", variant: 'danger', disabled: true, attributes: {"class": "shell-stop", 'data-shell-stop': true} })}</div>
  </div><div role="tablist" aria-label="Sekce aplikace">
  <button type="button" class="aw-button aw-button--secondary" role="tab" id="tab-control" aria-controls="control-panel" aria-selected="true"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><rect x="3" y="3" width="7" height="7" rx="2"/><rect x="14" y="3" width="7" height="7" rx="2"/><rect x="3" y="14" width="7" height="7" rx="2"/><rect x="14" y="14" width="7" height="7" rx="2"/></svg>Ovládání</button>
  <button type="button" class="aw-button aw-button--secondary" role="tab" id="tab-provisioning" aria-controls="provisioning-panel" aria-selected="false" tabindex="-1"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M3 9a14 14 0 0 1 18 0M6 13a9 9 0 0 1 12 0M9 17a4 4 0 0 1 6 0"/><circle cx="12" cy="21" r=".5"/></svg>Síť kufru</button>
  <button type="button" class="aw-button aw-button--secondary" role="tab" id="tab-debug" aria-controls="debug-panel" aria-selected="false" tabindex="-1"><svg viewBox="0 0 24 24" fill="none" aria-hidden="true"><path d="M3 12h4l3-7 4 14 3-7h4"/></svg>Diagnostika</button></div>
  <p class="shell-status" data-shell-status role="status" aria-live="polite" aria-atomic="true"></p>`;
controlPanel.before(tabs);
const shellStop = tabs.querySelector<HTMLButtonElement>('[data-shell-stop]')!;
const shellStatus = tabs.querySelector<HTMLElement>('[data-shell-status]')!;
shellStop.addEventListener('click', stopDevice);
const themeToggle = tabs.querySelector<HTMLButtonElement>('[data-theme-toggle]')!;
function applyTheme(theme: 'light' | 'dark'): void {
  document.documentElement.dataset.theme = theme;
  themeToggle.setAttribute('aria-label', theme === 'dark' ? 'Přepnout na světlý vzhled' : 'Přepnout na tmavý vzhled');
  themeToggle.setAttribute('aria-pressed', String(theme === 'light'));
  document.querySelector<HTMLMetaElement>('meta[name="theme-color"]')?.setAttribute('content', theme === 'dark' ? primitives.midnight : primitives.mist);
}
let savedTheme = document.documentElement.dataset.theme;
try { savedTheme = localStorage.getItem('aw-theme') ?? savedTheme; } catch { /* Storage may be unavailable in a restricted browser. */ }
applyTheme(savedTheme === 'light' ? 'light' : 'dark');
themeToggle.addEventListener('click', () => {
  const theme = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
  applyTheme(theme);
  try { localStorage.setItem('aw-theme', theme); } catch { /* The current session still retains its selected theme. */ }
});

function updateStopControls(snapshot: DeviceSnapshot): void {
  if (stopPending && !stopSubmitting &&
      (snapshot.phase === 'stopped' || snapshot.phase === 'disconnected')) {
    stopPending = false;
    stopStatus = '';
  }
  // Ordinary/equal-sequence snapshots can clear adapter busy while Stop is
  // still unresolved. Retain its submission status until a terminal state;
  // an unrelated heartbeat must not announce success.
  const unavailable = stopPending || snapshot.phase === 'stopped' || snapshot.phase === 'disconnected';
  shellStop.disabled = unavailable;
  shellStop.setAttribute('aria-busy', String(stopPending));
  shellStop.textContent = stopPending ? 'Stop · čekám…'
    : snapshot.source === 'native-device' ? 'Stop kufru' : 'Stop simulace';
  debugStopGuard.disabled = unavailable;
  const controlStop = app!.querySelector<HTMLButtonElement>('[data-action="stop"]');
  if (controlStop) { controlStop.disabled = unavailable; }
  // Avoid repeating identical announcements on heartbeat or tab changes.
  const status = stopPending ? stopStatus : snapshot.status;
  if (shellStatus.textContent !== status) { shellStatus.textContent = status; }
}

function stopDevice(): void {
  if (stopPending || !connection || !currentSnapshot ||
      currentSnapshot.phase === 'stopped' || currentSnapshot.phase === 'disconnected') {
    return;
  }
  pendingAction = null;
  stopPending = true;
  stopStatus = 'Čekám na potvrzený výsledek Stop; fyzické zastavení není ověřené.';
  stopSubmitting = true;
  updateStopControls(currentSnapshot);
  try {
    // Adapters publish pending/result snapshots synchronously or later; returning
    // from this void command is never an acknowledgment of physical shutdown.
    connection.stop();
  } finally {
    if (currentSnapshot.busy) { stopStatus = currentSnapshot.status; }
    stopSubmitting = false;
    updateStopControls(currentSnapshot);
  }
}
function selectTab(id: string): void {
  tabs.querySelector<HTMLButtonElement>(`#${id}`)?.click();
}

bindTabs(tabs, { onSelect: (tab) => {
  const id = tab.id;
  controlPanel.hidden = id !== 'tab-control';
  debugPanel.show(id === 'tab-debug');
  provisioningPanel.show(id === 'tab-provisioning');
  tabs.querySelector<HTMLAnchorElement>('.skip-link')!.href = `#${tab.getAttribute('aria-controls')}`;
  if (currentSnapshot) { updateStopControls(currentSnapshot); }
  window.scrollTo({ top: 0, behavior: 'auto' });
} });
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
  dancePanel.update(snapshot);
  const canControl = snapshot.controlMode === 'manual' && snapshot.phase === 'ready' &&
    !snapshot.busy && !snapshot.conversation?.pending;
  const modeText = snapshot.controlMode === 'manual' ? 'Ruční režim' : 'Pozorování';
  const phaseText = snapshot.dance && ['starting', 'playing', 'stopping'].includes(snapshot.dance.phase)
    ? 'Dance for me'
    : snapshot.phase === 'running'
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
    const subtitle = disconnected ? 'Stav není aktuální' : ready
      ? physical ? 'Připravena podle kufru' : 'Připravena v simulaci'
      : physical ? 'Nepřipravená podle kufru' : 'Nedostupná v simulaci';
    return `<article class="compartment ${ready && !disconnected ? '' : 'empty'}">
      ${wandCard({ title: `Přihrádka ${compartment}`, number: compartment, subtitle,
        interactive: false, selected: snapshot.activeCompartment === compartment, disabled: disconnected || !ready })}
      ${button({ label, disabled: !canControl,
        attributes: { 'data-action': action, 'data-compartment': compartment } })}
    </article>`;
  }).join('');
  const pending = pendingAction === null ? '' : `
    <section class="confirmation" aria-labelledby="confirm-title">
      <h2 id="confirm-title">Potvrdit ${pendingAction.kind === 'select' ? 'výběr' : 'založení'}?</h2>
      <p>${pendingAction.kind === 'select'
    ? `Spustit ${physical ? 'ruční' : 'simulovaný'} výběr přihrádky ${pendingAction.compartment}.`
    : `Potvrzuji kontrolu a založení přihrádky ${pendingAction.compartment}${physical ? ' v kufru' : ' pouze v simulaci'}.`}</p>
      <div class="actions">
        ${button({ label: "Zrušit", attributes: {"data-action": "cancel"} })}
        ${button({ label: "Potvrdit", variant: 'primary', attributes: {"data-action": "confirm"} })}
      </div>
    </section>`;

  app!.innerHTML = `
    <header>
      <h1>Ruční ovládání</h1>
      <div class="simulation">${physical ? 'MÍSTNÍ KUFR · Nativní připojení' : `SIMULACE · ${snapshot.source === 'local-fake'
    ? 'Místní fake bez připojení'
    : 'E2 hostitelský simulátor, bez fyzického kufru'}`}</div>
    </header>
    <main>
      <section class="overview" aria-label="Stav kufru">
        <div><span>Režim</span>${statusBadge({ label: modeText, tone: snapshot.controlMode === 'manual' ? 'info' : 'neutral' })}</div>
        <div><span>Stav</span>${statusBadge({ label: phaseText, tone: snapshot.phase === 'stopped' ? 'danger' : snapshot.phase === 'disconnected' ? 'warning' : snapshot.phase === 'running' ? 'info' : 'success' })}</div>
        <div><span>Připraveno</span><strong>${snapshot.phase === 'disconnected'
    ? '? / 6' : snapshot.readyMask.toString(2).replace(/0/g, '').length + ' / 6'}</strong></div>
      </section>
      <p class="status" role="status">${escapeHtml(snapshot.status)}</p>
      <div class="toolbar">
        ${snapshot.controlMode === 'observer'
    ? button({ label: physical ? 'Převzít ruční řízení' : 'Převzít řízení simulace', variant: 'primary',
        disabled: snapshot.phase !== 'ready' || snapshot.busy, attributes: { 'data-action': 'acquire' } })
    : button({ label: 'Uvolnit řízení', attributes: { 'data-action': 'release' } })}
        ${button({ label: physical ? 'Stop kufru' : 'Stop simulace', variant: 'danger',
          disabled: snapshot.phase === 'stopped' || snapshot.phase === 'disconnected', attributes: { 'data-action': 'stop' } })}
      </div>
      <p class="control-guidance">${snapshot.phase === 'disconnected'
    ? physical ? 'Připojte spárovaný kufr. Pro první připojení otevřete Síť kufru.'
      : 'Spojení se simulátorem není aktuální. Ovládání zůstává vypnuté.'
    : snapshot.phase === 'stopped'
      ? 'Před dalším řízením je nutná místní kontrola a zotavení.'
    : snapshot.phase === 'running'
      ? 'Probíhá akce. Vyčkejte na výsledek; v případě potřeby použijte Stop.'
    : snapshot.controlMode === 'observer'
      ? 'Sledujete stav. Pro výběr přihrádky převezměte ruční řízení.'
      : 'Vyberte přihrádku a potvrďte povel. Po dokončení uvolněte řízení.'}</p>
      ${snapshot.phase === 'stopped' ? `
        <section class="recovery">
          ${notice({ title: 'Výsledek je nejistý', message: snapshot.source === 'local-fake'
    ? 'Po skutečném Stop by byla nutná místní kontrola kufru. Zde lze obnovit pouze fake stav.'
    : physical ? 'Je nutná místní kontrola kufru. Telefon neověřuje polohu přihrádky a neobchází servisní zotavení.'
    : 'Simulátor vyžaduje servisní kontrolu mimo mobilní UI. Výsledek otevření není fyzicky ověřen.', tone: 'danger' })}
          ${snapshot.source === 'local-fake'
    ? `${button({ label: "Simulovat místní kontrolu", attributes: {"data-action": "inspect"} })}` : ''}
        </section>` : ''}
      ${pending}
      <section aria-labelledby="compartments-title">
        <div class="section-heading">
          <h2 id="compartments-title">Přihrádky</h2>
          <span>Šest samostatných přihrádek</span>
        </div>
        <div class="compartments">${cards}</div>
      </section>
      <p class="footnote">${snapshot.source === 'local-fake'
    ? 'Tato stránka neposílá povely do zařízení. Stavy a časování jsou jen místní fake.'
    : physical ? 'Povely jdou přímo do spárovaného kufru v LAN. Po ACK čekejte na výsledek a zkontrolujte přihrádku.'
    : 'Povely jdou pouze do místního E2 C++ simulátoru. Žádný fyzický kufr není připojen.'}</p>
    </main>`;
  updateStopControls(snapshot);
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
      stopDevice();
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

let controlConnectGeneration = 0;
document.addEventListener('visibilitychange', () => {
  ++controlConnectGeneration;
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
  ++controlConnectGeneration;
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
    <p>Uložený kufr připojte ve stejné místní síti. První párování vás provede přes Bluetooth.</p>
    ${button({ label: "První připojení · nastavit síť kufru", attributes: {"class": "connection-setup", "data-open-network": true} })}
    <details class="manual-pairing"><summary>Ruční párování a hledání v LAN</summary>
    ${button({ label: "Hledat kufr v síti (5 s)", attributes: {"data-native": "discover"} })}
    <p>Telefon a kufr musí být ve stejné místní síti. Nalezené údaje jsou jen vodítko;
      identitu ověřuje uložený otisk. Připojení přes zabezpečené BLE v Síť kufru doplní aktuální IP automaticky.</p>
    <div data-discovery-results></div>
    <form>
      ${field({ id: 'pair-host', label: "Adresa v místní síti", control: `<input class="aw-input" name="host" autocomplete="off" autocapitalize="none" required maxlength="253" placeholder="192.168.1.50">` })}
      ${field({ id: 'pair-device-id', label: "Device ID", control: `<input class="aw-input" name="deviceId" autocomplete="off" autocapitalize="none" required pattern="[a-z]([a-z0-9]|-){0,63}" maxlength="64">` })}
      ${field({ id: 'pair-spki-sha256', label: "Otisk SPKI (SHA-256, malé hex znaky)", control: `<input class="aw-input" name="spkiSha256" autocomplete="off" autocapitalize="none" required pattern="[0-9a-f]{64}" minlength="64" maxlength="64">` })}
      ${button({ label: "Spárovat kufr", type: 'submit' })}
    </form>
    <form data-address hidden>
      ${field({ id: 'saved-device-address', label: "Nová IP nebo DNS adresa uloženého kufru", control: `<input class="aw-input" name="address" autocomplete="off" autocapitalize="none" required maxlength="253">` })}
      ${button({ label: "Použít adresu", type: 'submit' })}
    </form>
    </details>
    <div class="actions">
      ${button({ label: "Připojit uložený kufr", variant: 'primary', disabled: true, attributes: {"data-native": "connect"} })}
      ${button({ label: "Odpojit", disabled: true, attributes: {"data-native": "disconnect"} })}
      ${button({ label: "Zapomenout párování", disabled: true, attributes: {"data-native": "forget"} })}
    </div>
    <p role="status"></p>`;
  app!.before(settings);
  settings.querySelector('[data-open-network]')!.addEventListener('click', () => {
    selectTab('tab-provisioning');
    tabs.querySelector<HTMLButtonElement>('#tab-provisioning')!.focus();
  });
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
    ++controlConnectGeneration;
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
  refreshNativePairing = async () => { applySavedDevice(await nativeBridge.savedDevice()); };
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
    ++controlConnectGeneration;
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
      status.textContent = result.bleAddressAvailable && devices.length > 0
        ? (result.failed
          ? 'Místní hledání není dostupné. Adresa spárovaného kufru je dostupná přes zabezpečené Bluetooth; můžete se připojit.'
          : 'Adresa spárovaného kufru je dostupná přes zabezpečené Bluetooth; můžete se připojit.')
        : result.failed
        ? 'Místní hledání není dostupné. Povolte aplikaci místní síť v Nastavení, ověřte Wi-Fi nebo zadejte IP.'
        : devices.length === 0
          ? 'Za 5 sekund nebyl nalezen odpovídající kufr. Telefon i kufr musí být ve stejné síti; kufr musí mít také servisně nastavenou místní TLS identitu a párování. Samotné BLE nastavení Wi-Fi nestačí. U připraveného kufru lze zadat IP.'
          : 'Nalezené adresy nejsou ověřená identita. Vyberte adresu; připojení potom spustíte výslovně.';
      if (devices.length === 0) {
        discoveryResults.innerHTML = emptyState({ title: 'Žádný odpovídající kufr',
          message: 'Ověřte místní síť a oprávnění aplikace. Adresu lze zadat ručně.' });
      }
      for (const device of devices) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'aw-button aw-button--secondary';
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
    if (document.hidden) return;
    const generation = controlConnectGeneration;
    const current = () => !document.hidden && generation === controlConnectGeneration;
    void performAsync(async () => {
      const refreshed = await provisioningPanel.refreshLocalAddress();
      if (!current()) return;
      if (!refreshed) {
        status.textContent = 'BLE ještě pracuje nebo adresu neobnovilo. Dokončete připojení v Síť kufru a zkuste znovu.';
        return;
      }
      const saved = await nativeBridge.savedDevice();
      if (!current()) return;
      if (!saved.paired || saved.deviceId !== savedDeviceId || typeof saved.host !== 'string') {
        applySavedDevice(saved);
        return;
      }
      // Preserve unresolved-command state across a reconnect to the same device.
      addressInput.value = saved.host;
      status.textContent = `Uložený kufr ${saved.deviceId} · ${saved.host}.`;
      if (connection instanceof NativeDeviceConnection) { connection.connect(saved.host); }
    });
  });
  disconnectButton.addEventListener('click', () => {
    ++controlConnectGeneration;
    connection?.release();
    if (connection instanceof NativeDeviceConnection) { connection.suspend(); }
  });
  forgetButton.addEventListener('click', () => {
    ++controlConnectGeneration;
    void performAsync(async () => {
      await nativeBridge.forget();
      applySavedDevice(await nativeBridge.savedDevice());
    });
  });
}

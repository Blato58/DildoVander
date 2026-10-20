import { mountDashboardUi, modelGuidance, statusBadge, notice, emptyState } from './dashboard-ui.js';

mountDashboardUi(document);

const tokenInput = document.querySelector('#token');
const connectButton = document.querySelector('#connect');
const disconnectButton = document.querySelector('#disconnect');
const statusLabel = document.querySelector('#connection-status');
const errorLabel = document.querySelector('#connection-error');
const slots = document.querySelector('#slots');
const timeline = document.querySelector('#timeline');
const eventFilter = document.querySelector('#event-filter');
const eventCount = document.querySelector('#event-count');
const clearEventsButton = document.querySelector('#clear-events');
const emptyTimeline = document.querySelector('#timeline-empty');
const freshnessLabel = document.querySelector('#model-freshness');
const guidanceLabel = document.querySelector('#model-guidance');
const themeButton = document.querySelector('#theme-toggle');
const eventAnnouncement = document.querySelector('#event-announcement');
const staticPreview = document.body.hasAttribute('data-static-preview');

if (staticPreview) {
  tokenInput.disabled = true;
  connectButton.disabled = true;
  disconnectButton.disabled = true;
  statusLabel.textContent = 'Veřejný náhled · místní simulátor není připojen';
  document.querySelector('#token-help').textContent = 'Veřejný náhled nepřijímá přístupové údaje.';
  document.querySelector('#static-preview-help').hidden = false;
}

const phaseLabels = {
  bootSafe: 'Bezpečný start',
  recoveryRequired: 'Nutná kontrola',
  ready: 'Připraveno',
  searching: 'Hledání',
  highlighting: 'Zvýraznění',
  vibrating: 'Vibrace',
  releasing: 'Uvolňování',
  stopped: 'Zastaveno',
  fault: 'Porucha',
};
const modeLabels = {
  standalone: 'Samostatný',
  manual: 'Ruční',
  recovery: 'Zotavení',
};
const eventLabels = {
  actuators_off: 'Požadavek: vypnout pohony',
  search: 'Hledání přihrádky',
  highlight: 'Zvýraznění přihrádky',
  vibration_on: 'Požadavek: zapnout vibraci',
  release_latch: 'Požadavek: uvolnit západku',
  stopped: 'Sekvence zastavena',
  completed: 'Sekvence programu dokončena',
};

let connection;
let latestSnapshot;
let receivedAt;
let eventHistory = [];

function renderGuidance() {
  guidanceLabel.innerHTML = notice({
    ...modelGuidance({
      preview: staticPreview,
      connected: document.body.dataset.connection === 'connected',
      current: document.body.dataset.modelCurrent === 'true',
      snapshot: latestSnapshot?.snapshot,
      journalPending: latestSnapshot?.journalPending,
    }),
    live: false,
  });
}

function renderSlots(state) {
  slots.replaceChildren();
  for (let compartment = 1; compartment <= 6; compartment += 1) {
    const available = state && (state.readyMask & (1 << (compartment - 1))) !== 0;
    const item = document.createElement('li');
    item.className = !state ? 'unknown' : available ? '' : 'unavailable';
    const number = document.createElement('span');
    number.className = 'slot-number';
    number.textContent = String(compartment).padStart(2, '0');
    const title = document.createElement('span');
    title.className = 'slot-label';
    title.textContent = `Přihrádka ${compartment}`;
    const stateText = document.createElement('span');
    stateText.className = 'slot-state';
    stateText.innerHTML = statusBadge({
      label: !state ? 'Neznámá' : document.body.dataset.modelCurrent !== 'true'
        ? available ? 'Dříve dostupná' : 'Dříve nedostupná'
        : available ? 'Dostupná v modelu' : 'Nedostupná v modelu',
      tone: state && available && document.body.dataset.modelCurrent === 'true' ? 'success' : 'neutral',
    });
    item.append(number, title, stateText);
    slots.append(item);
  }
}

function renderSnapshot(message) {
  const state = message.snapshot;
  latestSnapshot = message;
  receivedAt = new Date().toLocaleTimeString('cs-CZ');
  document.body.dataset.modelCurrent = 'true';
  document.querySelector('#mode').textContent = modeLabels[state.controlMode] ?? state.controlMode;
  document.querySelector('#phase').textContent = phaseLabels[state.phase] ?? state.phase;
  document.querySelector('#journal').textContent = message.journalPending ? 'Neuzavřený' : 'Uzavřený';
  document.querySelector('#revision').textContent = String(state.revision);
  freshnessLabel.textContent = `Snapshot přijat ${receivedAt} · čas prohlížeče`;
  renderSlots(state);
  renderGuidance();
}

function describeEvent(event) {
  const compartment = event.compartment > 0 ? ` ${event.compartment}` : '';
  return `${eventLabels[event.event] ?? event.event}${compartment}`;
}

function renderTimeline() {
  const query = eventFilter.value.trim().toLocaleLowerCase('cs');
  const visibleEvents = eventHistory.filter((event) =>
    describeEvent(event).toLocaleLowerCase('cs').includes(query));
  timeline.replaceChildren();
  for (const event of visibleEvents) {
    const item = document.createElement('li');
    const time = document.createElement('time');
    time.textContent = `${(event.timeMs / 1000).toFixed(2)} s`;
    const description = document.createElement('span');
    description.textContent = describeEvent(event);
    item.append(time, description);
    timeline.append(item);
  }
  eventCount.textContent = query
    ? `${visibleEvents.length} z ${eventHistory.length} událostí`
    : `${eventHistory.length} událostí · nejvýše 100`;
  clearEventsButton.disabled = eventHistory.length === 0;
  emptyTimeline.hidden = visibleEvents.length > 0;
  const message = query && eventHistory.length > 0
    ? 'Filtru neodpovídá žádná událost. Zkuste jiný název nebo číslo přihrádky.'
    : 'Po připojení se zde zobrazí události. Nejnovější budou nahoře.';
  emptyTimeline.innerHTML = emptyState({ title: query ? 'Žádná shoda' : 'Zatím bez událostí', message });
}

function renderEvent(event) {
  eventHistory.unshift(event);
  eventHistory = eventHistory.slice(0, 100);
  renderTimeline();
  const announcement = document.createElement('span');
  announcement.textContent = `${(event.timeMs / 1000).toFixed(2)} s: ${describeEvent(event)}`;
  eventAnnouncement.replaceChildren(announcement);
}

function setConnectionState(state) {
  document.body.dataset.connection = state;
  document.body.dataset.modelCurrent = 'false';
  const disconnected = state === 'disconnected';
  connectButton.disabled = !disconnected;
  disconnectButton.disabled = disconnected;
  tokenInput.disabled = !disconnected;
  connectButton.textContent = state === 'connecting' ? 'Připojuji…' : 'Připojit pozorování';
  statusLabel.innerHTML = statusBadge({
    label: state === 'connected' ? 'Připojeno · pouze pozorování'
      : state === 'connecting' ? 'Připojuji k simulátoru…' : 'Nepřipojeno',
    tone: state === 'connected' ? 'success' : state === 'connecting' ? 'info' : 'neutral',
  });
  if (state !== 'connected') {
    renderSlots(latestSnapshot?.snapshot);
    freshnessLabel.textContent = latestSnapshot
      ? `Poslední snapshot ${receivedAt} · není aktuální` : 'Stav není známý';
    if (latestSnapshot) {
      document.querySelector('#phase').textContent = `Poslední: ${phaseLabels[latestSnapshot.snapshot.phase] ?? latestSnapshot.snapshot.phase}`;
    }
  } else {
    renderSlots(latestSnapshot?.snapshot);
    freshnessLabel.textContent = latestSnapshot
      ? 'Čeká na nový snapshot · předchozí není aktuální' : 'Čeká na snapshot modelu';
  }
  renderGuidance();
}

async function connect() {
  if (staticPreview || connection) {
    return;
  }
  let token = tokenInput.value.trim();
  tokenInput.value = '';
  errorLabel.textContent = '';
  if (!token) {
    renderError('Zadejte testovací token.');
    tokenInput.focus();
    return;
  }

  const controller = new AbortController();
  connection = controller;
  // Each observation has its own model-relative timeline; do not mix restarts.
  eventHistory = [];
  eventFilter.value = '';
  renderTimeline();
  setConnectionState('connecting');

  try {
    const response = await fetch('/v1/observe', {
      headers: { Authorization: `Bearer ${token}` },
      cache: 'no-store',
      signal: controller.signal,
    });
    token = '';
    if (!response.ok || !response.body) {
      throw new Error(response.status === 401 ? 'Neplatný testovací token.' : `Spojení odmítnuto (${response.status}).`);
    }
    setConnectionState('connected');
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let pending = '';
    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        renderError('Pozorování bylo ukončeno. Pro aktuální stav se připojte znovu.');
        break;
      }
      pending += decoder.decode(value, { stream: true });
      let end;
      while ((end = pending.indexOf('\n')) !== -1) {
        const line = pending.slice(0, end);
        pending = pending.slice(end + 1);
        if (!line) {
          continue;
        }
        const message = JSON.parse(line);
        if (message.kind === 'snapshot') {
          renderSnapshot(message);
        } else if (message.kind === 'timeline') {
          renderEvent(message.event);
        }
      }
      if (pending.length > 65536) {
        throw new Error('Časová osa překročila limit zprávy.');
      }
    }
  } catch (error) {
    if (error.name !== 'AbortError') {
      renderError(`${error.message} Zkontrolujte místní simulátor a připojte se znovu.`);
    }
  } finally {
    if (connection === controller) {
      connection = null;
      setConnectionState('disconnected');
    }
  }
}

function renderError(message) {
  errorLabel.innerHTML = notice({ title: 'Pozorování není připojeno', message, tone: 'danger' });
}

connectButton.addEventListener('click', connect);
disconnectButton.addEventListener('click', () => connection?.abort());
eventFilter.addEventListener('input', renderTimeline);
clearEventsButton.addEventListener('click', () => {
  eventHistory = [];
  renderTimeline();
  emptyTimeline.innerHTML = emptyState({ title: 'Zobrazení vymazáno', message: 'Nové události se budou přidávat dál; model se nezměnil.' });
  eventFilter.focus();
});
const themePreference = globalThis.DildoVanderTheme;
function renderThemeControl() {
  const current = themePreference.themes.find(theme => theme.id === themePreference.getTheme());
  const next = themePreference.themes.find(theme => theme.id !== current.id);
  themeButton.textContent = `Vzhled: ${current.label}`;
  themeButton.setAttribute('aria-label', `Vzhled: ${current.label}. Změnit na ${next.label}`);
}
themeButton.addEventListener('click', () => {
  const next = themePreference.themes.find(theme => theme.id !== themePreference.getTheme());
  themePreference.setTheme(next.id);
});
themePreference.subscribe(renderThemeControl);
renderThemeControl();
tokenInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    connect();
  }
});
window.addEventListener('beforeunload', () => connection?.abort());
renderSlots();
renderGuidance();
renderTimeline();

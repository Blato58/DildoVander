const tokenInput = document.querySelector('#token');
const connectButton = document.querySelector('#connect');
const disconnectButton = document.querySelector('#disconnect');
const statusLabel = document.querySelector('#connection-status');
const errorLabel = document.querySelector('#connection-error');
const slots = document.querySelector('#slots');
const timeline = document.querySelector('#timeline');
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

function renderSnapshot(message) {
  const state = message.snapshot;
  document.querySelector('#mode').textContent = modeLabels[state.controlMode] ?? state.controlMode;
  document.querySelector('#phase').textContent = phaseLabels[state.phase] ?? state.phase;
  document.querySelector('#journal').textContent = message.journalPending ? 'Neuzavřený' : 'Uzavřený';
  document.querySelector('#revision').textContent = String(state.revision);

  slots.replaceChildren();
  for (let compartment = 1; compartment <= 6; compartment += 1) {
    const available = (state.readyMask & (1 << (compartment - 1))) !== 0;
    const item = document.createElement('li');
    item.className = available ? '' : 'unavailable';
    const title = document.createElement('strong');
    title.textContent = `Přihrádka ${compartment}`;
    const stateText = document.createElement('span');
    stateText.textContent = available ? 'Dostupná' : 'Nedostupná';
    item.append(title, stateText);
    slots.append(item);
  }
}

function renderEvent(event) {
  const item = document.createElement('li');
  const time = document.createElement('time');
  time.textContent = `${(event.timeMs / 1000).toFixed(2)} s`;
  const description = document.createElement('span');
  const compartment = event.compartment > 0 ? ` ${event.compartment}` : '';
  description.textContent = `${eventLabels[event.event] ?? event.event}${compartment}`;
  item.append(time, description);
  timeline.prepend(item);
  while (timeline.children.length > 100) {
    timeline.lastElementChild.remove();
  }
}

function setConnected(connected) {
  connectButton.disabled = connected;
  disconnectButton.disabled = !connected;
  statusLabel.textContent = connected ? 'Připojeno k simulátoru · pouze pozorování' : 'Nepřipojeno';
}

async function connect() {
  if (staticPreview || connection) {
    return;
  }
  let token = tokenInput.value.trim();
  tokenInput.value = '';
  errorLabel.textContent = '';
  if (!token) {
    errorLabel.textContent = 'Zadejte testovací token.';
    tokenInput.focus();
    return;
  }

  const controller = new AbortController();
  connection = controller;
  connectButton.disabled = true;
  statusLabel.textContent = 'Připojuji…';

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
    setConnected(true);
    const reader = response.body.getReader();
    const decoder = new TextDecoder();
    let pending = '';
    while (true) {
      const { value, done } = await reader.read();
      if (done) {
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
      errorLabel.textContent = error.message;
    }
  } finally {
    if (connection === controller) {
      connection = null;
      setConnected(false);
    }
  }
}

connectButton.addEventListener('click', connect);
disconnectButton.addEventListener('click', () => connection?.abort());
tokenInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter') {
    connect();
  }
});
window.addEventListener('beforeunload', () => connection?.abort());

import { button, notice } from '/design-system/ui.js';

const $ = id => document.getElementById(id);
const labels = { strike: 'Úder', trick: 'Lest', shield: 'Štít' };
const states = { AwaitingParticipants: 'Čekáme na přístupy obou stran.', Ready: 'Souboj připraven. Spouští ho obsluha.', Running: 'Souboj právě probíhá.', Completed: 'Souboj dokončen.', Cancelled: 'Souboj zrušen. Vítěz nebyl vyhlášen.', Interrupted: 'Souboj přerušen. Vítěz nebyl vyhlášen.' };
const query = new URLSearchParams(location.search);
const fragment = new URLSearchParams(location.hash.slice(1));
const id = query.get('duel');
// Grants stay in memory and never enter requests, referrers or persistent storage
// except the explicit Authorization header on the scoped move endpoint.
let token = fragment.get('grant');
let side = fragment.get('side');
history.replaceState(null, '', location.pathname + location.search);
let current;
let pending = false;
let controller;
let timer;
let epoch = 0;
let revision = -1;
let statusKey;
let grantInvalid = false;

function status(message, tone = 'info', detail = '') {
  if (statusKey === `${tone}:${message}:${detail}`) return;
  statusKey = `${tone}:${message}:${detail}`;
  $('duel-status').innerHTML = notice({ message, detail, tone, live: false });
}
function text(id, value) { $(id).textContent = String(value ?? ''); }
function hasGrant() { return !grantInvalid && typeof token === 'string' && /^[A-Za-z0-9_-]{43}$/.test(token) && ['left', 'right'].includes(side); }
function active() { return current?.state === 'Running'; }
function choiceOpen() { return active() && current.deadline != null
  && ['choice', 'web_only_no_spoken_confirmation'].includes(current.narrationStatus); }
function narrationCopy(view) {
  if (view.narrationMode === 'web_only') return 'Pouze webový průběh. Mluvené přehrání v kufru není potvrzené.';
  if (view.narrationMode !== 'physical_device') return '';
  return {
    intro_dispatch: 'Čekáme na potvrzené přehrání úvodu ze zařízení.',
    intro_wait: 'Čekáme na potvrzené přehrání úvodu ze zařízení.',
    round_dispatch: 'Čekáme na potvrzené přehrání komentáře kola ze zařízení.',
    round_wait: 'Čekáme na potvrzené přehrání komentáře kola ze zařízení.',
    final_dispatch: 'Čekáme na potvrzené přehrání závěru ze zařízení.',
    final_wait: 'Čekáme na potvrzené přehrání závěru ze zařízení.',
    choice: 'Zařízení potvrdilo předchozí přehrání. Nyní je prostor pro tahy.',
    playback_confirmed: 'Zařízení potvrdilo přehrání úvodu, komentářů kol i závěru.',
    interrupted: 'Souboj byl přerušen před potvrzením celého přehrání.',
    cancelled: 'Další přehrávání souboje bylo odvoláno.'
  }[view.narrationStatus] ?? 'Potvrzení přehrání ze zařízení zatím není dostupné.';
}
function locked() { return side === 'left' ? current?.leftChoiceLocked : current?.rightChoiceLocked; }
function element(tag, value) { const node = document.createElement(tag); node.textContent = value; return node; }
function render(view) {
  if (!view || view.id !== id || !view.left || !view.right || !Number.isInteger(view.revision)
      || !Array.isArray(view.rounds) || view.rounds.length > 3 || !states[view.state]) throw new Error('invalid_response');
  if (view.revision < revision) return;
  current = view;
  revision = view.revision;
  $('duel-participants').hidden = false;
  status(grantInvalid ? 'Soubojový přístup není platný. Můžeš sledovat průběh; nový přístup poskytne obsluha.'
    : `${view.mode === 'exhibition' ? 'Fiktivní exhibice · ' : ''}${states[view.state]}`,
  grantInvalid ? 'warning' : view.state === 'Completed' ? 'success' : 'info', narrationCopy(view));
  for (const participant of ['left', 'right']) {
    const identity = view[participant];
    text(`${participant}-name`, identity.displayName);
    text(`${participant}-wand`, `Digitální hůlka: ${identity.wandId}`);
    text(`${participant}-stats`, `Síla ${identity.strength} · Lest ${identity.cunning} · Obrana ${identity.defense}`);
    const points = view.rounds.reduce((sum, round) => sum + (round.leftScore === round.rightScore ? .5
      : (participant === 'left' ? round.leftScore > round.rightScore : round.rightScore > round.leftScore) ? 1 : 0), 0);
    text(`${participant}-score`, `${points} b.`);
  }
  $('duel-controls').hidden = !hasGrant() || view.mode !== 'interactive';
  text('choice-heading', `Tvůj tah · ${side === 'left' ? view.left.displayName : view.right.displayName}`);
  text('choice-status', locked() ? 'Tah přijat a uzamčen. Protivníkův tah zůstává skrytý.'
    : choiceOpen() ? `Kolo ${view.round} ze 3. Vyber jednu akci.`
      : active() ? narrationCopy(view) : states[view.state]);
  $('duel-actions').innerHTML = Object.entries(labels).map(([action, label]) => button({
    label, variant: 'primary', disabled: !choiceOpen() || locked() || pending,
    attributes: { 'data-action': action }
  })).join('');
  $('duel-audience').hidden = hasGrant();
  $('duel-rounds').replaceChildren(...view.rounds.map(round => {
    const script = view.commentary?.rounds?.find(item => item.round === round.number);
    const row = document.createElement('li');
    row.append(element('h3', `Kolo ${round.number} · ${round.leftScore} : ${round.rightScore}`),
      element('p', `${labels[round.leftAction] ?? 'Tah'}${round.leftAutomatic ? ' (automaticky)' : ''} × ${labels[round.rightAction] ?? 'Tah'}${round.rightAutomatic ? ' (automaticky)' : ''}`),
      element('p', script?.quip ?? round.quip), element('p', `Vyprávěná nehoda bez vlivu na body: ${script?.mishap ?? round.mishap}`));
    return row;
  }));
  $('duel-result').hidden = !view.result;
  $('duel-commentary-origin').hidden = view.commentary?.provider !== 'openai';
  if (view.result) text('result-copy', `${view.result.exhibition ? 'Fiktivní exhibice, nezapočítává se jako výhra hosta. ' : ''}${view.result.ending} ${view.result.leftPoints} : ${view.result.rightPoints}`);
}

async function refresh() {
  clearTimeout(timer);
  if (!id || !/^[0-9a-f-]{36}$/i.test(id)) { status('Odkaz na souboj není platný.', 'danger'); return; }
  if (document.hidden || pending) return;
  const requestEpoch = ++epoch;
  controller?.abort();
  controller = new AbortController();
  try {
    const response = await fetch(`/guest/v1/duels/${id}`, { credentials: 'omit', cache: 'no-store', redirect: 'error', signal: controller.signal });
    if (!response.ok) throw new Error(response.status === 404 ? 'unavailable' : 'network');
    const view = await response.json();
    if (requestEpoch !== epoch || document.hidden) return;
    render(view);
  } catch (error) {
    if (error.name !== 'AbortError' && requestEpoch === epoch) {
      if (error.message === 'unavailable') {
        current = null;
        $('duel-participants').hidden = $('duel-controls').hidden = $('duel-result').hidden = $('duel-commentary-origin').hidden = true;
        $('duel-rounds').replaceChildren();
        status('Souboj není veřejně dostupný. Obsluha může ověřit zveřejnění obou identit.', 'warning');
      } else status('Spojení s přehledem se přerušilo. Obnovujeme stav řízený serverem.', 'warning');
    }
  } finally {
    if (requestEpoch === epoch && !document.hidden) timer = setTimeout(refresh,
      current && ['Completed', 'Cancelled', 'Interrupted'].includes(current.state) ? 10000 : 1500);
  }
}

$('duel-actions').addEventListener('click', async event => {
  const action = event.target.closest('button[data-action]')?.dataset.action;
  if (!labels[action] || !hasGrant() || !choiceOpen() || locked() || pending) return;
  pending = true;
  clearTimeout(timer);
  controller?.abort();
  ++epoch;
  const moveEpoch = epoch;
  const body = { side, round: current.round, revision: current.revision, action, operationKey: crypto.randomUUID() };
  render(current);
  try {
    const response = await fetch(`/guest/v1/duels/${id}/choices`, { method: 'POST', credentials: 'omit', cache: 'no-store', redirect: 'error',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` }, body: JSON.stringify(body) });
    if (!response.ok) throw new Error(response.status === 403 ? 'grant' : response.status === 409 ? 'closed' : 'network');
    const view = await response.json();
    if (moveEpoch === epoch && !document.hidden) render(view);
  } catch (error) {
    if (error.message === 'grant') grantInvalid = true;
    status(error.message === 'grant' ? 'Soubojový přístup není platný. Obrať se na obsluhu.'
      : error.message === 'closed' ? 'Stav kola se změnil. Obnovujeme platné možnosti.'
      : 'Potvrzení tahu nedorazilo. Obnovujeme stav přijatý serverem.', 'warning');
  } finally { pending = false; if (current) render(current); await refresh(); }
});
document.addEventListener('visibilitychange', () => {
  ++epoch;
  controller?.abort();
  clearTimeout(timer);
  if (!document.hidden) refresh();
});
window.addEventListener('pagehide', () => { ++epoch; controller?.abort(); clearTimeout(timer); });
window.addEventListener('hashchange', () => {
  const access = new URLSearchParams(location.hash.slice(1));
  token = access.get('grant');
  side = access.get('side');
  grantInvalid = false;
  history.replaceState(null, '', location.pathname + location.search);
  if (current) render(current);
  refresh();
});
status('Načítáme souboj…');
refresh();

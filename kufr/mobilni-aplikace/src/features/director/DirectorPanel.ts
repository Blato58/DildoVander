import { button, emptyState, escapeHtml, field, notice, statusBadge } from '../../../../design-system/ui.js';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import { DirectorClient, DirectorRequestError, type ShowState } from './DirectorClient';
import { DuelGrantMemory, duelPageUrl, type DuelSide } from './DuelLinks';

export interface DirectorLocal {
  stop(): void;
  cancelSpeech(): void;
  nextVisit(): boolean;
  offline(action: string, compartment?: number, commandId?: string,
    options?: { enabled?: boolean; shortVariant?: boolean }): Promise<unknown>;
}
interface Identity { identityId: string; displayName: string; wandId: string }
interface Prophecy { outcomeId: string; omen: string; unlockAt: string; state: string }
interface Duel {
  id: string; deviceId: string; eveningId: string; revision: number; state: string; round: number;
  mode: 'autoplay' | 'interactive' | 'exhibition';
  narrationMode?: 'unbound' | 'web_only' | 'physical_device';
  narrationStatus?: string; narrationUnavailableReason?: string | null; deadline?: string | null;
  left: { displayName: string }; right: { displayName: string };
  result?: { winner: string; ending: string } | null;
}

export function duelNarrationLabel(duel: Pick<Duel, 'narrationMode' | 'narrationStatus'>): string {
  if (duel.narrationMode === 'web_only') return 'Webový souboj. Hlas kufru se nespouští.';
  if (duel.narrationMode === 'physical_device') {
    return duel.narrationStatus === 'playback_confirmed'
      ? 'Zařízení potvrdilo dokončení přehrání. Slyšitelnost nebyla změřena.'
      : 'Vyprávění je určeno kufru. Dokončení přehrání zatím není potvrzené.';
  }
  return 'Způsob vyprávění zatím není potvrzený. Tento pohled nedokládá zvuk z kufru.';
}

interface RemoteStopOperation {
  id: string;
  pending: boolean;
  status: 'sending' | 'accepted' | 'unknown' | 'rejected';
  expectedDeviceRevision?: number;
  acceptedAt?: string;
}

export function remoteStopStatus(state: ShowState | undefined, operation: RemoteStopOperation | undefined,
  snapshotReceivedAt: number, now = Date.now()): string {
  if (!operation) return '';
  if (operation.pending) return 'Stop odesílám přes backend. Výsledek zařízení zatím není potvrzený.';
  if (operation.status === 'unknown' || operation.status === 'sending') return 'Výsledek Stop přes backend je neznámý. Povel se automaticky neopakuje; ověřte kufr nebo použijte místní či fyzické zastavení.';
  if (operation.status === 'rejected') return 'Backend Stop nepřijal. Ověřte přístup; místní a fyzické zastavení mají vlastní cestu.';
  const report = state?.deviceSettings;
  const observedAt = report ? Date.parse(report.observedAt) : NaN;
  const acceptedAt = operation.acceptedAt ? Date.parse(operation.acceptedAt) : NaN;
  const fresh = now - snapshotReceivedAt < 12000 && now - observedAt < 12000 && observedAt <= now + 5000;
  const confirmed = operation.expectedDeviceRevision !== undefined && operation.expectedDeviceRevision > 0
    && state?.deviceStopRevision === operation.expectedDeviceRevision
    && report?.stopRevision === operation.expectedDeviceRevision && observedAt >= acceptedAt && fresh;
  return confirmed ? 'Zařízení potvrdilo uplatnění Stop. Fyzické zastavení nebylo změřeno.'
    : 'Backend přijal Stop. Čekám na aktuální potvrzení stejné revize zařízením; fyzické zastavení není ověřené.';
}

const control = (id: string, label: string, values: Array<[string, string]>) => field({ id, label,
  control: `<select class="aw-input" id="${id}">${values.map(([value, text]) => `<option value="${value}">${escapeHtml(text)}</option>`).join('')}</select>` });
const action = (label: string, kind: string, value?: string) => button({ label,
  attributes: { 'data-director-command': kind, 'data-value': value, 'data-online': true }, disabled: true });
const commandLabels: Record<string, string> = {
  'next-visit': 'Další návštěva', 'quick-finale': 'Krátké finále', 'guest-leave': 'Odchod hosta',
  'scene-twist': 'Scénový zásah', 'cancel-cue': 'Zrušení zásahu', mood: 'Nálada návštěvy',
  'mood-evening': 'Nálada večera', 'mood-auto': 'Automatická nálada', tone: 'Tón', tempo: 'Tempo',
  quiet: 'Ticho', ambient: 'Ambient', 'ambient-trigger': 'Jednorázový ambient',
  override: 'Preferovaná hůlka', 'cancel-speech': 'Ukončení řeči',
};
const stateLabels: Record<string, string> = {
  amused: 'pobavený', bored: 'znuděný', irritated: 'podrážděný', confident: 'sebejistý',
  suspicious: 'podezíravý', panicking: 'panikařící', automatic: 'automaticky', session: 'tato návštěva',
  evening: 'celý večer', reserved: 'zdrženlivý', dry: 'suchý', adult: 'dospělý', chaos: 'chaos',
  standard: 'standardní', express: 'expresní', idle: 'Připraveno', conversation: 'Rozhovor',
  greeting: 'Přivítání', finale: 'Finále', interrupted: 'Přerušeno', stopped: 'Režie zastavena',
  listening: 'Poslouchám', speaking: 'Mluvím', selection: 'Výběr hůlky', failed: 'Chyba',
  ready: 'Připraveno', running: 'Probíhá', awaitingparticipants: 'Čeká na soupeře',
  completed: 'Dokončeno', cancelled: 'Zrušeno', expired: 'Vypršelo',
};
const stateLabel = (value: string) => stateLabels[value] ?? 'Neznámý stav';
const sceneLabels: Record<string, string> = {
  coronation: 'Korunovace', 'ministerial-complaint': 'Ministerská stížnost', 'wand-rebellion': 'Hůlka protestuje',
  'professional-funeral': 'Teatrální zhroucení', 'luxury-fraud': 'Luxusní podvod',
  'bad-taste-prophecy': 'Proroctví špatného vkusu', 'shelf-dispute': 'Spor přihrádek',
  'approved-silence': 'Schválené ticho', 'room-roast': 'Roast místnosti',
};

export class DirectorPanel {
  readonly element = document.createElement('section');
  private readonly client: DirectorClient;
  private state?: ShowState;
  private snapshot?: DeviceSnapshot;
  private visible = false;
  private authorized = false;
  private pending = false;
  private refreshedAt = 0;
  private generation = 0;
  private readSequence = 0;
  private controlEpoch = 0;
  private remoteStop?: RemoteStopOperation;
  private timer?: number;
  private duel?: Duel;
  private readonly duelGrants = new DuelGrantMemory();
  private identities: Identity[] = [];
  private prophecies: Prophecy[] = [];
  private operation?: { id: string; label: string; status: string };

  constructor(native: boolean, private readonly local: DirectorLocal) {
    this.client = new DirectorClient(native);
    this.element.id = 'director-panel';
    this.element.className = 'director-panel';
    this.element.hidden = true;
    this.element.tabIndex = -1;
    this.element.setAttribute('aria-labelledby', 'director-heading');
    this.element.innerHTML = `<header class="director-heading"><div><p class="eyebrow">Živá režie</p><h1 id="director-heading">Director Mode</h1><p>Jeden kufr. Jeden aktuální stav. Každý zásah má vlastní výsledek.</p></div>${button({ label: 'Zpět do Provozu', attributes: { 'data-director-back': true } })}</header>
      <div class="director-connection" data-director-status></div>
      <p class="director-feedback" data-director-feedback role="status" aria-live="polite"></p>
      <form data-director-access class="aw-section"><h2>Operátorský přístup</h2><p>Skrytý vstup neuděluje oprávnění. Server ověřuje administrátora při každém požadavku; místní kufr používá vlastní párování.</p>
      ${field({ id: 'director-device', label: 'Identita kufru', control: '<input class="aw-input" id="director-device" required maxlength="128" autocomplete="off">' })}
      ${native ? '<p>Serverový přístup nastavíte v nativním dialogu. Credential zůstává v Keychain.</p>' : `${field({ id: 'director-address', label: 'HTTPS adresa backendu', control: '<input class="aw-input" id="director-address" type="url" required autocomplete="url">' })}${field({ id: 'director-credential', label: 'Administrátorský credential', control: '<input class="aw-input" id="director-credential" type="password" required autocomplete="off">', description: 'Pouze v paměti této otevřené stránky. Nikdy provider klíč.' })}`}
      <div class="actions">${button({ label: native ? 'Nastavit a ověřit přístup' : 'Ověřit přístup', variant: 'primary', type: 'submit' })}${button({ label: 'Odhlásit režii', attributes: { 'data-forget': true } })}</div></form>
      <div data-director-content hidden>
      <section class="director-visit aw-section" aria-labelledby="director-visit-heading"><div class="aw-section-heading"><h2 id="director-visit-heading">Aktuální návštěva</h2>${button({ label: 'Obnovit stav', attributes: { 'data-refresh': true } })}</div><div data-visit></div><div data-lifecycle></div>
      <div class="actions">${action('Další návštěva', 'next-visit')}${action('Zkrátit finále', 'quick-finale')}${action('Host odchází', 'guest-leave')}</div></section>
      <section class="aw-section" aria-labelledby="director-cues"><h2 id="director-cues">Krátký zásah</h2><p>Platí jen pro současný scope, nejvýše 30 sekund. Přijetí není odehrání ani fyzický pohyb.</p><div class="director-cues">${action('Zvrat', 'scene-twist', 'shelf-dispute')}${action('Hůlka protestuje', 'scene-twist', 'wand-rebellion')}${action('Teatrální zhroucení', 'scene-twist', 'professional-funeral')}${action('Roast místnosti', 'scene-twist', 'room-roast')}</div>${action('Zrušit čekající zásah', 'cancel-cue')}</section>
      <details class="aw-section"><summary>Nálada, tón a tempo</summary><p>Volby jsou oddělené; změna obsahu neuděluje fyzické oprávnění.</p><div class="director-grid">${control('director-mood', 'Nálada', [['amused','Pobavený'],['bored','Znuděný'],['irritated','Podrážděný'],['confident','Sebejistý'],['suspicious','Podezíravý'],['panicking','Panikařící']])}${control('director-scope', 'Rozsah nálady', [['session','Současná návštěva'],['evening','Celý večer']])}</div><div class="actions">${button({ label: 'Použít náladu', attributes: { 'data-mood': true, 'data-online': true }, disabled: true })}${action('Vrátit automatickou náladu', 'mood-auto')}</div><div class="director-grid">${control('director-tone', 'Tón', [['reserved','Zdrženlivý'],['dry','Suchý'],['adult','Dospělý'],['chaos','Chaos']])}${control('director-tempo', 'Tempo', [['standard','Standardní'],['express','Expresní']])}</div><div class="actions">${button({ label: 'Použít tón', attributes: { 'data-setting': 'tone', 'data-online': true }, disabled: true })}${button({ label: 'Použít tempo', attributes: { 'data-setting': 'tempo', 'data-online': true }, disabled: true })}</div></details>
      <details class="aw-section"><summary>Ambient a ticho</summary><div class="actions">${action('Zapnout ambient', 'ambient', 'on')}${action('Vypnout ambient', 'ambient', 'off')}${action('Jednorázový ambient', 'ambient-trigger')}${action('Obnovit zvuk', 'quiet', 'off')}</div></details>
      <details class="aw-section"><summary>Preferovaná hůlka</summary><p>Pouze před fyzickým přijetím návrhu. Obsluha nemění dostupnost ani kalibraci; zařízení ověří nový návrh znovu.</p>${control('director-wand', 'Preferovaná přihrádka', [1,2,3,4,5,6].map(n => [String(n), `Hůlka ${n}`]))}<p data-wand-availability></p>${button({ label: 'Ověřit a změnit návrh', attributes: { 'data-override': true, 'data-online': true }, disabled: true })}</details>
      <details class="aw-section"><summary>Souboj dvou hostů</summary><p>Vyberte dva existující identity snapshoty tohoto večera. Hosté musí souhlasit.</p>${button({ label: 'Načíst identity a souboje', attributes: { 'data-identities': true, 'data-online': true }, disabled: true })}<div class="director-grid">${control('director-left', 'Levý soupeř', [])}${control('director-right', 'Pravý soupeř', [])}</div><label class="director-consent"><input type="checkbox" data-duel-consent> Oba hosté souhlasí se soubojem</label><div class="actions">${button({ label: 'Připravit souboj', attributes: { 'data-duel-create': true, 'data-online': true }, disabled: true })}${button({ label: 'Spustit připravený souboj', attributes: { 'data-duel-start': true, 'data-online': true }, disabled: true })}${button({ label: 'Zrušit souboj', attributes: { 'data-duel-cancel': true, 'data-online': true }, disabled: true })}</div><div data-duel-state>${emptyState({ title: 'Souboj není načtený', message: 'Načtěte aktuální stav serveru.' })}</div></details>
      <details class="aw-section"><summary>Proroctví a kronika</summary>${button({ label: 'Načíst proroctví večera', attributes: { 'data-prophecies-load': true, 'data-online': true }, disabled: true })}<div data-prophecies></div><div data-end-evening><h3>Uzavřít večer</h3><p>Uzavře příjem dalších návštěv a připraví kroniku. Archiv zůstane zachován.</p>${button({ label: 'Připravit uzavření večera', attributes: { 'data-end-prepare': true, 'data-online': true }, disabled: true })}<div data-end-confirm hidden><p>Opravdu uzavřít aktuálně zobrazený večer? Běžící operace se dokončí podle svého stavu.</p>${button({ label: 'Potvrdit uzavření večera', variant: 'danger', attributes: { 'data-end-confirm-button': true } })}${button({ label: 'Ponechat večer otevřený', attributes: { 'data-end-cancel': true } })}</div></div></details>
      </div>
      <details class="aw-section"><summary>Offline Show · místní řízení</summary><p>Bez AI a WAN. Preflight ověřuje místní banku a katalog. Arm vyžaduje platný ruční lease a jeho přijetím předává řízení kufru. Odpojení pozorovatele autonomní show nezastaví.</p><div class="actions">${button({ label: 'Místní preflight', attributes: { 'data-offline': 'offline_preflight' } })}${button({ label: 'Arm · předat místnímu režimu', attributes: { 'data-offline': 'offline_arm' } })}</div>${control('director-offline-wand', 'Místní výběr', [1,2,3,4,5,6].map(n => [String(n), `Hůlka ${n}`]))}<div class="actions">${button({ label: 'Spustit místní návštěvu', attributes: { 'data-offline': 'offline_start' } })}${button({ label: 'Krátká místní návštěva', attributes: { 'data-offline': 'offline_start_short' } })}${button({ label: 'Načíst místní výsledky', attributes: { 'data-offline': 'offline_outbox' } })}${button({ label: 'Synchronizovat výsledky', attributes: { 'data-offline-sync': true } })}</div><p data-offline-state role="status">Připravenost není ověřená.</p></details>
      <div class="director-persistent" aria-label="Trvalé řízení">${button({ label: 'Stop kufru', variant: 'danger', attributes: { 'data-director-stop': true } })}${button({ label: 'Ukončit řeč', attributes: { 'data-speech-cancel': true } })}${button({ label: 'Quiet · místní ticho', attributes: { 'data-offline': 'quiet' } })}</div>`;
    this.query('.director-heading').append(this.query('[data-forget]'));
    this.query('[data-director-stop]').insertAdjacentHTML('afterend', button({ label: 'Stop přes backend', variant: 'danger',
      disabled: true, attributes: { 'data-remote-stop': true, 'aria-describedby': 'director-remote-stop-status' } }));
    this.query('.director-persistent').insertAdjacentHTML('beforeend', '<p id="director-remote-stop-status" data-remote-stop-status role="status" aria-live="polite" hidden></p>');
    this.query('.director-consent').insertAdjacentHTML('beforebegin', control('director-duel-mode', 'Způsob souboje', [
      ['autoplay', 'Automatický souboj'], ['interactive', 'Hosté volí na svých telefonech'],
    ]));
    this.query('[data-duel-state]').insertAdjacentHTML('afterend', `<section data-duel-links hidden>
      <h3>Odkazy souboje</h3><p>Veřejný výsledek je jen ke čtení. Herní odkaz opravňuje pouze jednoho soupeře; předejte jej právě jemu. Po opuštění režie zde odkazy nezůstávají uložené.</p>
      <a class="aw-button aw-button--secondary" data-duel-public target="_blank" rel="noopener noreferrer" hidden>Otevřít veřejný výsledek</a>
      <p data-duel-origin-status></p><div class="director-grid">${(['left', 'right'] as const).map(side => `<div>
        <h4>${side === 'left' ? 'Levý' : 'Pravý'} soupeř</h4><p data-duel-grant-status="${side}"></p>
        <div class="actions">${button({ label: `Vydat odkaz · ${side === 'left' ? 'levý' : 'pravý'}`, attributes: { 'data-duel-grant-op': 'issue', 'data-side': side, 'data-online': true }, disabled: true })}
        ${button({ label: `Odvolat odkaz · ${side === 'left' ? 'levý' : 'pravý'}`, attributes: { 'data-duel-grant-op': 'revoke', 'data-side': side, 'data-online': true }, disabled: true })}</div>
        <a class="aw-button aw-button--secondary" data-duel-play="${side}" target="_blank" rel="noopener noreferrer" hidden>Otevřít hru · ${side === 'left' ? 'levý' : 'pravý'}</a>
      </div>`).join('')}</div></section>`);
    this.query('[data-offline-state]').insertAdjacentHTML('beforebegin', `<div class="actions">
      ${button({ label: 'Místní Quiet zapnout', attributes: { 'data-offline': 'quiet', 'data-enabled': 'true' } })}
      ${button({ label: 'Místní Quiet vypnout', attributes: { 'data-offline': 'quiet', 'data-enabled': 'false' } })}
      ${button({ label: 'Místní ambient zapnout', attributes: { 'data-offline': 'offline_ambient', 'data-enabled': 'true' } })}
      ${button({ label: 'Místní ambient vypnout', attributes: { 'data-offline': 'offline_ambient', 'data-enabled': 'false' } })}</div>`);
    this.bind(native);
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) { ++this.generation; this.refreshedAt = 0; this.duelGrants.clear(); this.abandonRemoteStop(); this.update(); }
      else if (this.visible && this.authorized) { void this.refresh(); }
    });
  }

  show(visible: boolean): void {
    this.visible = visible;
    this.element.hidden = !visible;
    if (this.timer) window.clearInterval(this.timer);
    if (visible) {
      this.update();
      if (this.authorized) void this.refresh();
      this.timer = window.setInterval(() => { this.update(); if ((!this.pending || this.remoteStop) && !this.remoteStop?.pending && this.authorized && !document.hidden) void this.refresh(); }, 5000);
    } else { ++this.generation; this.refreshedAt = 0; this.duelGrants.clear(); this.abandonRemoteStop(); this.renderDuelLinks(); }
  }

  updateDevice(snapshot: DeviceSnapshot): void { this.snapshot = snapshot; if (this.visible) this.update(); }
  private query<T extends HTMLElement>(selector: string): T { return this.element.querySelector<T>(selector)!; }
  private value(id: string): string { return this.query<HTMLInputElement | HTMLSelectElement>(`#director-${id}`).value; }
  private feedback(message: string): void { this.query('[data-director-feedback]').textContent = message; }
  private async request<T>(suffix = '', body?: unknown): Promise<T> {
    const generation = this.generation;
    const controlEpoch = this.controlEpoch;
    const deviceId = this.client.deviceId;
    const result = await this.client.request<T>(suffix, body);
    if (generation !== this.generation || controlEpoch !== this.controlEpoch || deviceId !== this.client.deviceId || document.hidden) {
      throw new Error('Odpověď patří předchozímu pohledu. Obnovte stav; povel se neopakuje.');
    }
    return result;
  }
  private online(): boolean { return this.authorized && !this.pending && !this.remoteStop?.pending && Date.now() - this.refreshedAt < 12000 && !!this.state && !document.hidden; }

  private canRemoteStop(): boolean {
    return this.authorized && !!this.state && this.state.deviceId === this.client.deviceId
      && !this.remoteStop?.pending && !document.hidden;
  }

  private abandonRemoteStop(): void {
    if (this.remoteStop?.pending) { this.remoteStop.pending = false; this.remoteStop.status = 'unknown'; }
  }

  private update(): void {
    const snapshot = this.snapshot;
    const deviceLive = snapshot?.source === 'native-device' && snapshot.phase !== 'disconnected';
    const fresh = this.authorized && Date.now() - this.refreshedAt < 12000;
    this.query('[data-director-status]').innerHTML = `${statusBadge({ label: deviceLive ? 'Zařízení · místně připojeno' : 'Zařízení · místní cesta nedostupná', tone: deviceLive ? 'success' : 'warning' })}${statusBadge({ label: fresh ? 'Backend · ověřen' : 'Backend · stav neověřen', tone: fresh ? 'success' : 'warning' })}${statusBadge({ label: snapshot?.voice?.available ? `AI · ${snapshot.voice.mode}` : 'AI · dostupnost neověřena' })}<p>${fresh && deviceLive ? 'Online' : deviceLive || fresh ? 'Degraded' : 'Offline'} · Autorita: ${snapshot?.controlMode === 'manual' ? 'ruční lease telefonu' : deviceLive ? 'autonomní kufr / pozorovatel' : 'neověřena'}. ${this.refreshedAt ? `Serverový snapshot před ${Math.floor((Date.now() - this.refreshedAt) / 1000)} s.` : 'Před akcí ověřte stav.'}</p>`;
    this.query('[data-director-content]').hidden = !this.authorized;
    this.query('[data-director-access]').hidden = this.authorized;
    this.query('[data-director-content]').setAttribute('aria-busy', String(this.pending));
    this.query('[data-director-access]').setAttribute('aria-busy', String(this.pending));
    this.query<HTMLButtonElement>('[data-director-access] [type="submit"]').disabled = this.pending;
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-online]')) item.disabled = !this.online();
    const activeSession = !!this.state?.sessionId && !!this.state.sessionGeneration;
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-director-command]')) {
      if (['quick-finale', 'guest-leave', 'scene-twist', 'cancel-speech'].includes(item.dataset.directorCommand!)) item.disabled ||= !activeSession;
    }
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-setting]')) item.disabled ||= !activeSession;
    const preferredSlot = Number(this.value('wand'));
    const preferredAvailable = ((this.state?.readyMask ?? 0) & (1 << (preferredSlot - 1))) !== 0;
    this.query<HTMLButtonElement>('[data-override]').disabled ||= !activeSession
      || this.state?.selectionLocked !== false || !preferredAvailable;
    this.query<HTMLButtonElement>('[data-mood]').disabled ||= this.value('scope') === 'session' && !activeSession;
    this.query<HTMLButtonElement>('[data-director-command="next-visit"]').disabled = !this.online()
      || !deviceLive || snapshot?.voice?.canStart !== true || snapshot.readyMask === 0;
    this.query<HTMLButtonElement>('[data-director-stop]').disabled = !deviceLive || snapshot?.phase === 'stopped';
    const remoteStopButton = this.query<HTMLButtonElement>('[data-remote-stop]');
    remoteStopButton.disabled = !this.canRemoteStop();
    remoteStopButton.setAttribute('aria-busy', String(this.remoteStop?.pending === true));
    remoteStopButton.textContent = this.remoteStop?.pending ? 'Stop přes backend · čekám…' : 'Stop přes backend';
    const remoteStatus = this.query('[data-remote-stop-status]');
    remoteStatus.hidden = !this.remoteStop;
    const stopMessage = remoteStopStatus(this.state, this.remoteStop, this.refreshedAt);
    if (remoteStatus.textContent !== stopMessage) remoteStatus.textContent = stopMessage;
    this.query<HTMLButtonElement>('[data-speech-cancel]').disabled = !deviceLive || !snapshot?.voice?.canStop;
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-offline]')) item.disabled = !deviceLive || this.pending;
    this.query<HTMLButtonElement>('[data-offline-sync]').disabled = !deviceLive || !this.online();
    const state = this.state;
    if (state) {
      const started = state.sessionStartedAt ? Date.parse(state.sessionStartedAt) : NaN;
      const duration = Number.isFinite(started) ? `${Math.max(0, Math.floor((Date.now() - started) / 60000))} min` : '—';
      this.query('[data-visit]').innerHTML = `<div class="director-grid"><div><p class="eyebrow">${state.sessionId ? 'Aktivní anonymní návštěva' : 'Bez aktivní návštěvy'} · ${duration}</p><h3>${escapeHtml(stateLabel(state.phase))}</h3><p>${escapeHtml(state.sceneId ? (sceneLabels[state.sceneId] ?? 'Scéna') : 'Žádná scéna neběží')}</p></div><div><p>Nálada · ${escapeHtml(stateLabel(state.mood.dominant))} (${escapeHtml(stateLabel(state.moodScope ?? 'automatic'))})</p><p>Tón · ${escapeHtml(stateLabel(state.tone))} · Tempo · ${escapeHtml(stateLabel(state.tempo))}</p><p>${state.quiet ? 'Quiet je aktivní' : 'Zvuk povolen'} · Ambient ${state.ambientEnabled ? 'zapnutý' : 'vypnutý'}</p></div></div><p>Hůlka: ${snapshot?.activeCompartment ?? 'žádný fyzicky přijatý výběr'} · Blokace: ${escapeHtml(state.occupiedBy ?? 'server žádnou nehlásí')}</p><p class="director-meta">Večer ${escapeHtml(state.eveningId)} · Revize ${state.revision}</p>`;
      this.query('[data-wand-availability]').textContent = snapshot ? `Místní dostupnost: ${[1,2,3,4,5,6].filter(n => (snapshot.readyMask & (1 << (n - 1))) !== 0).join(', ') || 'žádná'}. Zdroj: ${snapshot.source}.` : 'Dostupnost zařízení není ověřená.';
    }
    this.query('[data-lifecycle]').innerHTML = this.operation ? notice({ title: this.operation.label, message: this.operation.status, detail: `ID ${this.operation.id}`, live: false }) : notice({ message: state?.pendingCue ? 'Krátký zásah čeká na vhodnou fázi.' : 'Žádný odeslaný zásah v tomto panelu.', live: false });
    this.query<HTMLButtonElement>('[data-duel-start]').disabled = !this.online() || !this.duel || !['pending','created','ready'].includes(this.duel.state.toLowerCase());
    this.query<HTMLButtonElement>('[data-duel-cancel]').disabled = !this.online() || !this.duel || ['completed','cancelled','expired','interrupted'].includes(this.duel.state.toLowerCase());
    const leftIdentity = this.value('left'), rightIdentity = this.value('right');
    const currentPair = leftIdentity !== rightIdentity && this.identities.some(identity => identity.identityId === leftIdentity)
      && this.identities.some(identity => identity.identityId === rightIdentity);
    this.query<HTMLButtonElement>('[data-duel-create]').disabled ||= !currentPair;
    this.renderDuelLinks();
  }

  private async refresh(): Promise<void> {
    const generation = this.generation;
    const readSequence = ++this.readSequence;
    try {
      const state = await this.request<ShowState>();
      if (generation !== this.generation || readSequence !== this.readSequence || !this.visible || document.hidden) return;
      if (state.deviceId !== this.client.deviceId || !Number.isInteger(state.revision) || !state.mood) throw new Error('Neplatný stav serveru.');
      if (this.state?.eveningId !== state.eveningId) {
        this.identities = []; this.prophecies = []; this.duel = undefined; this.duelGrants.clear();
        this.query('#director-left').replaceChildren(); this.query('#director-right').replaceChildren();
        this.query('[data-end-confirm]').hidden = true; this.renderDuel();
      }
      this.state = state; this.authorized = true; this.refreshedAt = Date.now();
      if (this.duel && !['completed','cancelled','expired','interrupted'].includes(this.duel.state.toLowerCase())) {
        const duel = await this.request<Duel>(`/duels/${encodeURIComponent(this.duel.id)}`);
        if (readSequence !== this.readSequence) return;
        this.setDuel(duel);
      }
      this.update();
    } catch (error) { if (generation !== this.generation || readSequence !== this.readSequence) return; this.refreshedAt = 0; if (error instanceof DirectorRequestError && [401,403].includes(error.status)) this.authorized = false; this.feedback(this.error(error)); this.update(); }
  }

  private error(error: unknown): string { return error instanceof DirectorRequestError ? error.message : error instanceof Error ? error.message : 'Spojení selhalo. Výsledek není ověřený.'; }
  private async perform(label: string, work: () => Promise<unknown>): Promise<void> {
    if (this.pending) return;
    ++this.readSequence;
    const generation = this.generation;
    const controlEpoch = this.controlEpoch;
    this.pending = true; this.feedback(`${label} · čekám na potvrzený výsledek…`); this.update();
    try { await work(); if (generation === this.generation && controlEpoch === this.controlEpoch) this.feedback(`${label}: serverový výsledek přijat.`); }
    catch (error) { if (generation === this.generation && controlEpoch === this.controlEpoch) { this.refreshedAt = 0; this.feedback(this.error(error)); if (this.operation) this.operation.status = error instanceof DirectorRequestError ? 'Odmítnuto. Obnovte stav; povel nebyl opakován.' : 'Výsledek neznámý. Povel se automaticky neopakuje.'; } }
    finally { this.pending = false; if (generation === this.generation) this.update(); }
  }

  private async stopThroughBackend(): Promise<void> {
    if (!this.canRemoteStop() || !this.state) return;
    const generation = this.generation;
    const deviceId = this.client.deviceId;
    const state = this.state;
    const operation: RemoteStopOperation = { id: crypto.randomUUID(), pending: true, status: 'sending' };
    this.remoteStop = operation;
    ++this.controlEpoch;
    ++this.readSequence;
    if (this.operation) this.operation.status = 'Zneplatněno povelem Stop; pozdní odpověď se nepoužije.';
    this.update();
    const current = () => generation === this.generation && deviceId === this.client.deviceId && this.remoteStop === operation;
    try {
      // The authenticated whole-device Stop is the backend's priority command.
      // It intentionally bypasses the ordinary pending guard and stale reads.
      const result = await this.client.request<{ status: string; state: ShowState }>('/commands', {
        commandId: operation.id, expectedStateRevision: state.revision, kind: 'stop', ttlSeconds: 30,
      });
      if (!current()) return;
      if (!['accepted', 'replayed'].includes(result.status) || result.state.deviceId !== deviceId
        || !Number.isInteger(result.state.revision)) throw new Error('Neplatné potvrzení Stop.');
      operation.status = 'accepted';
      operation.expectedDeviceRevision = result.state.deviceStopRevision;
      operation.acceptedAt = result.state.updatedAt;
      this.state = result.state;
      this.refreshedAt = Date.now();
      ++this.readSequence;
    } catch (error) {
      if (!current()) return;
      operation.status = error instanceof DirectorRequestError ? 'rejected' : 'unknown';
      this.refreshedAt = 0;
      ++this.readSequence;
      if (error instanceof DirectorRequestError && [401, 403].includes(error.status)) this.authorized = false;
    } finally {
      operation.pending = false;
      if (operation.status === 'sending') operation.status = 'unknown';
      if (current()) this.update();
    }
  }

  private async command(kind: string, value?: string): Promise<void> {
    if (!this.online() || !this.state) return;
    const state = this.state;
    const id = crypto.randomUUID();
    this.operation = { id, label: commandLabels[kind] ?? 'Zásah obsluhy', status: 'Odesílám; výsledek není potvrzený.' };
    await this.perform(this.operation.label, async () => {
      const result = await this.request<{ status: string; code: string; state: ShowState }>('/commands', {
        commandId: id, expectedStateRevision: state.revision, kind, value, ttlSeconds: 30,
        sessionId: state.sessionId, sessionGeneration: state.sessionGeneration,
      });
      this.operation!.status = `${result.status === 'replayed' ? 'Dříve přijatý povel' : 'Povel přijat'}. Přijetí není potvrzení odehrání ani fyzického dokončení.`;
      this.state = result.state; this.refreshedAt = Date.now();
      if (kind === 'next-visit') {
        if (!this.local.nextVisit()) throw new Error('Scope je připravený, ale kufr novou návštěvu nepřijal. Obnovte místní připravenost; start se neopakuje.');
        this.operation!.status = 'Start nové návštěvy předán místnímu vlastníku. Čekám na potvrzený stav hlasu kufru.';
      }
      if (kind === 'quiet' || kind === 'ambient') {
        this.operation!.status = 'Backend přijal nastavení. Místní kufr zatím změnu nepotvrdil.';
        if (this.snapshot?.source === 'native-device' && this.snapshot.phase !== 'disconnected') {
          const localState = await this.local.offline(kind === 'quiet' ? 'quiet' : 'offline_ambient', undefined, undefined, { enabled: value === 'on' });
          this.query('[data-offline-state]').textContent = this.offlineSummary(localState);
          this.operation!.status = 'Nastavení backendu i místního režimu potvrzeno. Fyzické ticho nebylo změřeno.';
        } else {
          this.operation!.status = 'Backend nastaven. Místní cesta není dostupná; změna místního audia není potvrzená.';
        }
      }
    });
  }

  private bind(native: boolean): void {
    this.query<HTMLFormElement>('[data-director-access]').addEventListener('submit', event => {
      event.preventDefault();
      if (this.pending) return;
      ++this.generation;
      void this.perform('Ověření přístupu', async () => {
        this.authorized = false; this.state = undefined; this.refreshedAt = 0;
        this.remoteStop = undefined;
        this.duel = undefined; this.duelGrants.clear(); this.renderDuel();
        await this.client.configure(native ? '' : this.value('address'), native ? '' : this.value('credential'), this.value('device'));
        if (!native) this.query<HTMLInputElement>('#director-credential').value = '';
        await this.refresh();
      });
    });
    this.query('[data-forget]').addEventListener('click', () => { ++this.generation; this.client.forget(); this.remoteStop = undefined; this.authorized = false; this.state = undefined; this.duel = undefined; this.duelGrants.clear(); this.identities = []; this.prophecies = []; this.refreshedAt = 0; this.query('[data-prophecies]').replaceChildren(); this.feedback('Režie odhlášena. Nativní profil lze spravovat v Diagnostice.'); this.renderDuel(); this.update(); });
    this.query('[data-refresh]').addEventListener('click', () => { if (!this.pending) void this.refresh(); });
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-director-command]')) item.addEventListener('click', () => void this.command(item.dataset.directorCommand!, item.dataset.value));
    this.query('[data-mood]').addEventListener('click', () => void this.command(this.value('scope') === 'evening' ? 'mood-evening' : 'mood', this.value('mood')));
    this.query('#director-scope').addEventListener('change', () => this.update());
    this.query('#director-wand').addEventListener('change', () => this.update());
    for (const side of ['left', 'right']) this.query(`#director-${side}`).addEventListener('change', () => this.update());
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-setting]')) item.addEventListener('click', () => void this.command(item.dataset.setting!, this.value(item.dataset.setting!)));
    this.query('[data-override]').addEventListener('click', () => void this.command('override', this.value('wand')));
    this.query('[data-director-stop]').addEventListener('click', () => { this.local.stop(); this.feedback('Stop směrován místní cestou. Čekejte na stav zařízení; fyzické zastavení není změřené.'); });
    this.query('[data-remote-stop]').addEventListener('click', () => void this.stopThroughBackend());
    this.query('[data-speech-cancel]').addEventListener('click', () => { this.local.cancelSpeech(); if (this.online()) void this.command('cancel-speech'); });
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-offline]')) item.addEventListener('click', () => void this.perform('Místní režim', async () => {
      const shortVariant = item.dataset.offline === 'offline_start_short';
      const kind = shortVariant ? 'offline_start' : item.dataset.offline!;
      const result = await this.local.offline(kind, Number(this.value('offline-wand')), undefined,
        { shortVariant, ...(kind === 'quiet' || kind === 'offline_ambient' ? { enabled: item.dataset.enabled !== 'false' } : {}) });
      this.query('[data-offline-state]').textContent = typeof result === 'string' ? result : this.offlineSummary(result);
    }));
    this.query('[data-offline-sync]').addEventListener('click', () => void this.perform('Synchronizace', async () => {
      if (!this.state || this.snapshot?.source !== 'native-device') throw new Error('Vyžaduje autentizované místní spojení a backend.');
      const outbox = await this.local.offline('offline_outbox') as { records: Array<{ operationId: string; deviceId: string }> };
      if (!Array.isArray(outbox.records)) throw new Error('Kufr neposkytl platný outbox.');
      let count = 0;
      for (const outcome of outbox.records) {
        if (outcome.deviceId !== this.client.deviceId) throw new Error('Výsledek patří jinému kufru.');
        const result = await this.request<{ operationId: string }>('/offline-outcomes', { schemaVersion: 1, outcome });
        if (result.operationId !== outcome.operationId) throw new Error('Server nepotvrdil tento výsledek.');
        await this.local.offline('offline_sync_ack', undefined, outcome.operationId);
        ++count;
      }
      this.query('[data-offline-state]').textContent = `Server potvrdil ${count} výsledků. Místní záznamy byly potvrzeny až po přijetí serverem.`;
    }));
    this.query('[data-identities]').addEventListener('click', () => void this.perform('Identity', async () => {
      if (!this.state) return;
      const query = `?eveningId=${encodeURIComponent(this.state.eveningId)}`;
      const [identities, duels] = await Promise.all([this.request<Identity[]>(`/identities${query}`), this.request<Duel[]>(`/duels${query}`)]);
      this.identities = identities;
      for (const side of ['left','right']) this.query<HTMLSelectElement>(`#director-${side}`).innerHTML = identities.map(identity => `<option value="${escapeHtml(identity.identityId)}">${escapeHtml(identity.displayName)} · ${escapeHtml(identity.wandId)}</option>`).join('');
      this.setDuel(duels[0]);
    }));
    this.query('[data-duel-create]').addEventListener('click', () => void this.perform('Příprava souboje', async () => {
      if (!this.state || !this.query<HTMLInputElement>('[data-duel-consent]').checked) throw new Error('Nejdříve potvrďte souhlas obou hostů.');
      const left = this.value('left'), right = this.value('right');
      if (!left || left === right || !this.identities.some(identity => identity.identityId === left)
        || !this.identities.some(identity => identity.identityId === right)) throw new Error('Vyberte dvě různé identity tohoto večera.');
      const mode = this.value('duel-mode');
      if (!['autoplay', 'interactive'].includes(mode)) throw new Error('Vyberte podporovaný způsob souboje.');
      const duel = await this.request<Duel>('/duels', { eveningId: this.state.eveningId, leftIdentityId: left, rightIdentityId: right, mode, consentConfirmed: true, operationKey: crypto.randomUUID() });
      this.setDuel(duel);
    }));
    for (const operation of ['start','cancel']) this.query(`[data-duel-${operation}]`).addEventListener('click', () => void this.perform('Souboj', async () => {
      if (!this.duel) return;
      const duel = await this.request<Duel>(`/duels/${encodeURIComponent(this.duel.id)}/${operation}`, { revision: this.duel.revision, operationKey: crypto.randomUUID() });
      this.setDuel(duel);
    }));
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-duel-grant-op]')) {
      item.addEventListener('click', () => {
        if (!this.online() || !this.duel || this.duel.mode !== 'interactive') return;
        const duel = this.duel;
        const side = item.dataset.side as DuelSide;
        const operation = item.dataset.duelGrantOp;
        if (!['left', 'right'].includes(side) || !['issue', 'revoke'].includes(operation ?? '')) return;
        // An uncertain issue/revoke may already invalidate the previous token.
        this.duelGrants.remove(side); this.renderDuelLinks();
        void this.perform(operation === 'issue' ? 'Vydání odkazu soupeře' : 'Odvolání odkazu soupeře', async () => {
          const body = { side, revision: duel.revision, operationKey: crypto.randomUUID() };
          if (operation === 'issue') {
            const result = await this.request<{ token: string; side: DuelSide; expiresAt: string; duel: Duel }>(
              `/duels/${encodeURIComponent(duel.id)}/grants`, body);
            if (result.side !== side || result.duel.id !== duel.id) throw new Error('Odkaz nepatří vybranému soupeři a souboji.');
            this.setDuel(result.duel);
            this.duelGrants.remember(side, { token: result.token, expiresAt: result.expiresAt });
          } else {
            const result = await this.request<Duel>(`/duels/${encodeURIComponent(duel.id)}/grants/revoke`, body);
            if (result.id !== duel.id) throw new Error('Server nepotvrdil odvolání pro vybraný souboj.');
            this.setDuel(result); this.duelGrants.remove(side);
          }
          this.renderDuelLinks();
        });
      });
    }
    this.query('[data-prophecies-load]').addEventListener('click', () => void this.perform('Proroctví', async () => { if (!this.state) return; this.prophecies = await this.request<Prophecy[]>(`/prophecies?eveningId=${encodeURIComponent(this.state.eveningId)}`); this.renderProphecies(); }));
    this.query('[data-end-prepare]').addEventListener('click', () => { this.query('[data-end-confirm]').hidden = false; this.query<HTMLButtonElement>('[data-end-confirm-button]').focus(); });
    this.query('[data-end-cancel]').addEventListener('click', () => { this.query('[data-end-confirm]').hidden = true; this.query<HTMLButtonElement>('[data-end-prepare]').focus(); });
    this.query('[data-end-confirm-button]').addEventListener('click', () => void this.perform('Uzavření večera', async () => { if (!this.state) return; await this.request('/end-evening', { eveningId: this.state.eveningId, expectedStateRevision: this.state.revision }); this.query('[data-end-confirm]').hidden = true; await this.refresh(); }));
  }

  private offlineSummary(result: unknown): string {
    const response = result as { offline?: unknown };
    const value = (response.offline ?? result) as { state?: string; armed?: boolean; quiet?: boolean; ambientEnabled?: boolean; missing?: string[]; outboxCount?: number };
    return `Místní stav: ${value.state ?? 'neověřený'} · Arm: ${value.armed ? 'ano' : 'ne'} · Quiet: ${value.quiet ? 'ano' : 'ne'} · Ambient: ${value.ambientEnabled ? 'zapnutý' : 'vypnutý'} · Výsledků: ${value.outboxCount ?? 'neznámý počet'} · Chybí: ${value.missing?.join(', ') || 'nic nehlášeno'}. Dokončení je časované, nikoli změřené.`;
  }
  private renderDuel(): void {
    const duel = this.duel;
    this.query('[data-duel-state]').innerHTML = duel
      ? `${notice({ title: `${duel.left.displayName} × ${duel.right.displayName}`, message: `${stateLabel(duel.state.toLowerCase())} · Kolo ${duel.round}`, detail: duel.result ? `${duel.result.winner === 'draw' ? 'Remíza' : duel.result.winner === 'left' ? 'Vítězí levý soupeř' : 'Vítězí pravý soupeř'} · ${duel.result.ending}` : 'Souboj je fiktivní, nemění dostupnost hůlek.', live: false })}
        <p data-duel-narration>${escapeHtml(duelNarrationLabel(duel))}</p>
        ${duel.narrationUnavailableReason ? `<details><summary>Technický stav vyprávění</summary><p class="director-meta">${escapeHtml(duel.narrationUnavailableReason)}</p></details>` : ''}`
      : emptyState({ title: this.identities.length ? 'Žádný souboj' : 'Žádné identity v tomto večeru', message: 'Nový souboj vyžaduje dva dokončené identity snapshoty.' });
    this.renderDuelLinks();
  }
  private setDuel(duel: Duel | undefined): void {
    if (duel && (!this.state || duel.deviceId !== this.client.deviceId || duel.eveningId !== this.state.eveningId)) {
      throw new Error('Souboj patří jinému zařízení nebo večeru.');
    }
    if (!duel || duel.id !== this.duel?.id || ['completed', 'cancelled', 'expired', 'interrupted'].includes(duel.state.toLowerCase())) this.duelGrants.clear();
    this.duel = duel;
    if (duel && this.state) this.duelGrants.bind(this.state.eveningId, duel.id);
    this.renderDuel();
  }
  private renderDuelLinks(): void {
    const root = this.query('[data-duel-links]');
    const duel = this.duel;
    root.hidden = !duel;
    if (!duel) {
      for (const link of this.element.querySelectorAll<HTMLAnchorElement>('[data-duel-play], [data-duel-public]')) {
        link.removeAttribute('href'); link.hidden = true;
      }
      return;
    }
    const publicUrl = duelPageUrl(this.state?.publicOrigin ?? undefined, duel.id);
    const publicLink = this.query<HTMLAnchorElement>('[data-duel-public]');
    publicLink.hidden = !publicUrl;
    if (publicUrl) publicLink.href = publicUrl;
    else publicLink.removeAttribute('href');
    this.query('[data-duel-origin-status]').textContent = publicUrl
      ? 'Odkazy používají veřejnou adresu nastavenou na serveru.'
      : 'Veřejná adresa není bezpečně nastavená. Herní odkazy zatím nelze vydat.';
    const active = !['completed', 'cancelled', 'expired', 'interrupted'].includes(duel.state.toLowerCase());
    for (const side of ['left', 'right'] as const) {
      const grant = this.duelGrants.get(side);
      const playUrl = grant && publicUrl ? duelPageUrl(this.state?.publicOrigin ?? undefined, duel.id, { side, token: grant.token }) : null;
      const playLink = this.query<HTMLAnchorElement>(`[data-duel-play="${side}"]`);
      playLink.hidden = !playUrl;
      if (playUrl) playLink.href = playUrl;
      else playLink.removeAttribute('href');
      this.query(`[data-duel-grant-status="${side}"]`).textContent = grant
        ? `Vydaný odkaz expiruje nejpozději ${new Date(grant.expiresAt).toLocaleString('cs-CZ')}. Platnost ověřuje server; nové vydání odvolá předchozí odkaz této strany.`
        : duel.mode === 'interactive' ? 'Platný herní odkaz není v paměti tohoto pohledu.' : 'Automatický souboj herní odkazy nepotřebuje.';
      this.query<HTMLButtonElement>(`[data-duel-grant-op="issue"][data-side="${side}"]`).disabled = !this.online()
        || !publicUrl || duel.mode !== 'interactive' || !['awaitingparticipants', 'ready', 'running'].includes(duel.state.toLowerCase());
      this.query<HTMLButtonElement>(`[data-duel-grant-op="revoke"][data-side="${side}"]`).disabled = !this.online()
        || duel.mode !== 'interactive' || !active;
    }
  }
  private renderProphecies(): void {
    this.query('[data-prophecies]').innerHTML = this.prophecies.length ? this.prophecies.map((value, index) => `<article><h3>${escapeHtml(value.omen)}</h3><p>${['unlocked', 'revealed'].includes(value.state) ? 'Odhaleno návštěvníkovi. Soukromé znění zůstává v jeho památce.' : `Uzamčeno do ${escapeHtml(value.unlockAt)}`}</p>${['unlocked', 'revealed'].includes(value.state) ? '' : button({ label: 'Odhalit toto proroctví', attributes: { 'data-reveal': index, 'data-online': true }, disabled: !this.online() })}</article>`).join('') : emptyState({ title: 'Žádná proroctví', message: 'Tento večer zatím nemá výsledek k odhalení.' });
    for (const item of this.element.querySelectorAll<HTMLButtonElement>('[data-reveal]')) item.addEventListener('click', () => void this.perform('Odhalení proroctví', async () => {
      if (!this.state) return;
      const outcome = this.prophecies[Number(item.dataset.reveal)];
      await this.request(`/prophecies/${encodeURIComponent(outcome.outcomeId)}/reveal`, { eveningId: this.state.eveningId });
      this.prophecies = await this.request<Prophecy[]>(`/prophecies?eveningId=${encodeURIComponent(this.state.eveningId)}`); this.renderProphecies();
    }));
  }
}

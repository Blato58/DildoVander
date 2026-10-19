import { button, field } from '../../../../design-system/ui.js';
import { registerPlugin } from '@capacitor/core';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import { DebugJournal, type DebugCategory, type DebugCode } from './DebugJournal';
import { ServerLogBuffer, type ServerLogPage } from './ServerLogs';

type ConversationEngine = 'gpt-live' | 'realtime';
interface EngineCapability {
  engine: ConversationEngine;
  model: string;
  ready: boolean;
  unavailableReason?: string | null;
  voices: string[];
  speed: { supported: boolean; min: number; max: number; default: number };
  inputModes: string[];
  cameraSources: string[];
}
interface EngineCapabilities {
  schemaVersion: number;
  primaryEngine: ConversationEngine;
  engines: EngineCapability[];
}
interface GuestMemoryBinding {
  guestId: string;
  identityVerified: boolean;
  longTermAllowed: boolean;
  cloudExportAllowed: boolean;
}
interface MemoryInspection {
  identity: (GuestMemoryBinding & { version: number; conversationId: string; deviceId: string }) | null;
  epoch: number;
  deletionEpoch: number;
  records: Array<{ memoryId: string; text: string; sourceReference: string; revision: number; isActive: boolean }>;
  totalRecords: number;
  hasMore: boolean;
  memoryStatus: { provider: string; configured: boolean; degraded: boolean; configurationStatus: string;
    pending: number; failed: number; deleting: number; storedBytes: number; capacityBytes: number; rejectedWrites: number };
}
const enginePreferenceKey = 'kufr.conversation.engine.v1';

export function validGuestMemoryId(value: string): boolean {
  return value.length >= 1 && value.length <= 80 && !/[^A-Za-z0-9._-]/.test(value);
}

/** Only the retired PTT variant has an equivalent retained engine. Other old
 * values require a deliberate operator choice rather than a silent fallback. */
export function migrateConversationEngine(value: string | null): { engine?: ConversationEngine; message?: string } {
  if (value === null) return { engine: 'gpt-live' };
  if (value === 'gpt-live' || value === 'realtime') return { engine: value };
  if (value === 'realtime-ptt') return {
    engine: 'realtime', message: 'Původní Realtime PTT bylo převedeno na GPT Realtime 2.1. Ověřte dostupný způsob vstupu.',
  };
  return { message: 'Uložený rozhovorový režim už není podporován. Vyberte GPT Live nebo GPT Realtime 2.1.' };
}

function validCapability(value: EngineCapability): boolean {
  return value != null && typeof value.ready === 'boolean'
    && Array.isArray(value.voices) && value.voices.every(voice => typeof voice === 'string')
    && Array.isArray(value.cameraSources) && value.cameraSources.every(source => typeof source === 'string')
    && Array.isArray(value.inputModes) && value.inputModes.every(mode => typeof mode === 'string')
    && value.speed != null && typeof value.speed.supported === 'boolean'
    && Number.isFinite(value.speed.min) && Number.isFinite(value.speed.max) && Number.isFinite(value.speed.default)
    && value.speed.min <= value.speed.default && value.speed.default <= value.speed.max;
}

interface VoiceStatus {
  configured: boolean;
  running: boolean;
  session?: boolean;
  recording?: boolean;
  handsFree?: boolean;
  cameraActive?: boolean;
  imagePending?: boolean;
  inputActive?: boolean;
  outputActive?: boolean;
  toolActive?: boolean;
  effectiveEngine?: string;
  effectiveModel?: string;
  sessionGeneration?: number;
  conversationId?: string;
  lastConversationId?: string;
  errorCode?: string | null;
  costumeStatus?: string | null;
  audioOutput?: { route: 'speaker' | 'receiver' | 'external' | 'none'; volume: number;
    receivedSeconds: number; renderedSeconds: number; engineRunning: boolean } | null;
  transcript?: Array<{ role: 'kufr' | 'visitor'; text: string }>;
  selected?: number | null;
  timings?: { dialogMs?: number; speechMs?: number; downloadMs?: number; firstAudioMs?: number; totalMs: number };
  phase: 'idle' | 'checking' | 'opening' | 'awaiting' | 'turn' | 'transcription'
    | 'recording' | 'listening' | 'thinking' | 'waiting_listening'
    | 'speech' | 'download' | 'playing' | 'completed' | 'cancelled' | 'failed';
  mode: 'unknown' | 'fake' | 'openai';
  server?: ServerModes | null;
  evenings?: EveningList | null;
}
interface EveningList {
  activeEveningId: string;
  evenings: Array<{ eveningId: string; name: string; createdAt: string; exchangeCount: number; droppedCount: number }>;
}
interface ServerModes {
  revision: string;
  engines?: EngineCapabilities;
  activeConversations: number;
  lifetime: 'until_restart';
  personalityId?: string;
  personalityLifetime?: 'persistent';
  characterEnabled?: boolean;
  characterTone?: 'Reserved' | 'Dry' | 'Adult' | 'Chaos';
  characterFinaleProfile?: 'Limited' | 'Blocked';
  personalities?: Array<{ id: string; name: string; description: string; promptSha256: string }>;
}
interface DebugBridge {
  readServerLogs(options: { after?: number; level: string }): Promise<ServerLogPage>;
  status(): Promise<VoiceStatus>;
  configure(): Promise<void>;
  forgetProfile(): Promise<void>;
  inspectEvenings(): Promise<void>;
  createEvening(options: { name: string; expectedEveningId: string }): Promise<void>;
  activateEvening(options: { eveningId: string; expectedEveningId: string }): Promise<void>;
  inspectBackend(): Promise<void>;
  inspectServer(): Promise<void>;
  changeServerModes(options: { expectedRevision: string; personalityId?: string;
    characterEnabled?: boolean; characterTone?: string; characterFinaleProfile?: string; resetCharacterVariety?: boolean }): Promise<void>;
  captureCostumePhoto(): Promise<{ draftId: string | null }>;
  attachCostumePhoto(options: { draftId: string }): Promise<{ status: string; imageId: string; revision: number }>;
  discardCostumePhoto(): Promise<void>;
  inspectMemory(options: { conversationId: string }): Promise<MemoryInspection>;
  deleteConversationMemory(options: { conversationId: string; expectedEpoch: number }): Promise<{
    localDeletion: 'complete'; remoteDeletion: 'complete' | 'pending';
  }>;
  startConversation(options: {
    expectedMode: 'openai'; readyMask: number; photoDraftId?: string; costumeDescription?: string;
    voice: string; speed: number; engine: ConversationEngine;
    guestMemory?: GuestMemoryBinding;
  }): Promise<void>;
  speakSystem(): Promise<void>;
  stop(): Promise<void>;
  shareLog(options: { json: string }): Promise<void>;
}
const bridge = registerPlugin<DebugBridge>('KufrDebug');
const phases: Record<VoiceStatus['phase'], string> = {
  idle: 'Připraveno', checking: 'Ověřuji backend', opening: 'Čekám na úvodní repliku',
  awaiting: 'Čekám na vaši odpověď', turn: 'Připravuji odpověď',
  transcription: 'Přepisuji nahrávku', recording: 'Nahrávám odpověď',
  listening: 'Poslouchám. Můžete mluvit', thinking: 'Připravuji odpověď',
  waiting_listening: 'Zapínám další poslech',
  speech: 'Generuji hlas', download: 'Stahuji audio', playing: 'Přehrávám na telefonu', completed: 'Dokončeno',
  cancelled: 'Zastaveno', failed: 'Akce selhala; ověřte přístup, server a OpenAI účet',
};

export class DebugPanel {
  readonly element = document.createElement('section');
  readonly journal = new DebugJournal();
  private snapshot?: DeviceSnapshot;
  private paused = false;
  private visible = false;
  private category: DebugCategory | 'all' = 'all';
  private pending = false;
  private cancellationPending = false;
  private photoDraftId?: string;
  private photoSequence = 0;
  private photoTimer?: number;
  private photoMessage = 'Bez fotky. Rozhovor ji nepotřebuje.';
  private serverRevision?: string;
  private eveningRevision?: string;
  private statusSequence = 0;
  private actionGeneration = 0;
  private photoPending = false;
  private photoAttaching = false;
  private photoDiscarding = false;
  private photoUsed = false;
  private photoCancellationFailed = false;
  private engineMigrationMessage?: string;
  private statusPending = false;
  private memoryPending = false;
  private memorySequence = 0;
  private memoryInspection?: MemoryInspection;
  private memoryInspectedConversation?: string;
  private lastMemoryConversation?: string;
  private memoryMessage = 'Načtěte paměť konkrétního rozhovoru. Čtení nevolá AI.';
  private readonly serverLogs = new ServerLogBuffer();
  private logsPending = false;
  private logsSequence = 0;
  private logsMessage = 'Nastavte přístup správce a načtěte logy serveru.';
  private renderedLogs?: ServerLogBuffer['entries'];
  private voice: VoiceStatus = { configured: false, running: false, phase: 'idle', mode: 'unknown' };

  constructor(private readonly native: boolean, private readonly stopDevice: () => void) {
    this.element.className = 'debug-panel';
    this.element.id = 'debug-panel';
    this.element.hidden = true;
    this.element.setAttribute('role', 'tabpanel');
    this.element.setAttribute('aria-labelledby', 'tab-debug');
    this.element.tabIndex = -1;
    this.element.innerHTML = `
      <div class="debug-heading"><div><h1>Diagnostika</h1></div>
        ${button({ label: "Stop kufru", variant: 'danger', disabled: true, attributes: {"data-debug": "esp-stop"} })}</div>
      <p>Záložka sleduje existující spojení. Pro pozorování není nutné převzít řízení.</p>
      <nav class="section-nav" aria-label="Přejít v diagnostice">
        <a href="#esp-heading">Kufr</a><a href="#server-heading">Server</a>
        <a href="#server-log-heading">Log serveru</a><a href="#evening-heading">Večery</a>
        <a href="#guest-memory-heading">Paměť hosta</a>
        <a href="#voice-heading">Hlas</a><a href="#debug-conversation-heading">Zkušební rozhovor</a>
        <a href="#log-heading">Log aplikace</a>
      </nav>
      <p class="aw-notice aw-notice--danger" data-voice-error role="alert" hidden></p>
      <section class="debug-card" aria-labelledby="esp-heading"><h2 id="esp-heading">Běh ESP</h2>
        <p data-source></p><dl class="debug-metrics"></dl>
        <p data-freshness></p>
        <p class="footnote">Údaje pocházejí z přijatých snapshotů. GPIO, napájení ani fyzický pohyb z nich nelze ověřit.</p></section>
      <section class="debug-card" aria-labelledby="server-heading"><h2 id="server-heading">Server</h2>
        <p>Personalita a pravidla postavy platí pro celý backend a ukládají se i přes restart.
          Změny lze použít mezi rozhovory.
          AI testy čerpají OpenAI účet až při spuštění.</p>
        <div class="actions">
          ${button({ label: "Nastavit serverový / testovací přístup", attributes: {"data-debug": "configure"} })}
          ${button({ label: "Zapomenout uložený přístup", attributes: {"data-debug": "forget-profile"} })}
          ${button({ label: "Obnovit stav serveru", attributes: {"data-debug": "server-inspect"} })}
          ${field({ id: 'server-personality', label: "Personalita", control: `<select class="aw-input" aria-label="Personalita kufru" data-server="personality">
            <option value="">Nejprve obnovte stav serveru</option></select>` })}
          ${button({ label: "Použít nastavení serveru", variant: 'primary', attributes: {"data-debug": "server-apply"} })}
        </div>
        <fieldset class="aw-field"><legend>DV01 · dospělá roast postava</legend>
          <label><input type="checkbox" data-character="enabled"> Zapnout pro další rozhovory</label>
          ${field({ id: 'character-tone', label: "Hostitelský tón", control: `<select class="aw-input" data-character="tone" aria-label="Hostitelský tón DV01">
            <option value="Reserved">Reserved</option><option value="Dry">Dry · bez nadávek</option>
            <option value="Adult" selected>Adult · ostrý roast</option><option value="Chaos">Chaos · absurdní roast</option></select>` })}
          ${field({ id: 'character-finale', label: "Finále", control: `<select class="aw-input" data-character="finale" aria-label="Publikační profil finále DV01">
            <option value="Blocked">Blokované · bez fyzického reveal</option>
            <option value="Limited">Omezené · bez vysloveného názvu hůlky</option></select>` })}
          <p class="footnote">Pojmenované finále a roast fotografie čekají na samostatné ověření. Změna platí pro nové session;
            ukončení živého rozhovoru a nouzový Stop používejte přes stávající ovládání.
            DV01 vyžaduje personalitu ollivander-trunk. DV01 přepis se nepřenáší do paměti dalšího hosta.</p>
          <div class="actions">${button({ label: "Použít DV01 pro nové rozhovory", attributes: {"data-debug": "character-apply"} })}
            ${button({ label: "Resetovat anonymní pestrost", attributes: {"data-debug": "character-reset"} })}</div>
        </fieldset>
        <p data-personality-description></p>
        <p data-server-status role="status"></p>
        <p class="footnote">Správa vyžaduje samostatný credential správce a testovací ID debug-server-admin.
          Běžný přístup kufru nestačí. Po restartu nebo změně nasazení obnovte stav enginů.</p></section>
      <section class="debug-card" aria-labelledby="server-log-heading"><h2 id="server-log-heading">Log serveru</h2>
        <p>Technické události serveru za posledních 24 hodin, nejvýše 5000 záznamů.
          Zobrazení obsahuje posledních 200 událostí vybraného filtru.</p>
        <div class="actions">${field({ id: 'server-log-level', label: "Úroveň", control: `<select class="aw-input" data-server-log-level aria-label="Úroveň serverového logu">
          <option value="info">Všechny</option><option value="warning">Varování a chyby</option><option value="error">Chyby</option></select>` })}
          ${button({ label: "Načíst nové logy", attributes: {"data-debug": "logs-refresh"} })}
          ${button({ label: "Sdílet serverový log JSON", attributes: {"data-debug": "logs-export"} })}</div>
        <label><input type="checkbox" data-server-log-live> Automaticky obnovovat každé 2 sekundy</label>
        <p data-server-log-status role="status"></p>
        <ol class="debug-log" data-server-log-list aria-label="Události serveru"></ol>
        <p class="footnote">ID požadavku nebo operace lze sdílet s Codexem pro dohledání chyby.
          Logy neobsahují přístupové údaje, text rozhovoru ani audio.</p></section>
      <section class="debug-card" aria-labelledby="evening-heading"><h2 id="evening-heading">Večery a paměť rozhovorů</h2>
        <p>Nový večer začne s prázdnou pamětí. Přepnutím obnovíte paměť vybraného večera;
          předchozí večery zůstanou uložené. Změny jsou dostupné mezi rozhovory.</p>
        <div class="actions">${button({ label: "Obnovit seznam večerů", attributes: {"data-debug": "evenings-inspect"} })}</div>
        <p data-evening-status role="status"></p>
        ${field({ id: 'saved-evening', label: "Uložený večer", control: `<select class="aw-input" data-evening-selection aria-label="Uložený večer"></select>` })}
        <div class="actions">${button({ label: "Přepnout aktivní večer", attributes: {"data-debug": "evening-activate"} })}</div>
        ${field({ id: 'new-evening-name', label: "Jméno nového večera", control: `<input class="aw-input" type="text" data-evening-name aria-label="Jméno nového večera"
          maxlength="160" placeholder="Například páteční oslava" autocomplete="off">` })}
        <div class="actions">${button({ label: "Založit a aktivovat nový večer", variant: 'primary', attributes: {"data-debug": "evening-create"} })}</div>
        <p class="footnote">Seznam patří nastavené testovací identitě. Obsah rozhovorů se zde nestahuje
          a založení ani přepnutí samo nevolá AI.</p>
      </section>
      <section class="debug-card" aria-labelledby="guest-memory-heading"><h2 id="guest-memory-heading">Paměť hosta</h2>
        <p>Přístup správce zobrazí jen záznamy použitelné pro zvolený rozhovor a jeho ověřenou identitu.
          Čtení ani odstranění nevolá AI. Smazání paměti nemění mechanický stav kufru.</p>
        ${field({ id: 'memory-conversation', label: 'ID rozhovoru pro kontrolu paměti', control: `<input class="aw-input" data-memory-conversation type="text" maxlength="64" autocomplete="off" spellcheck="false">` })}
        <div class="actions">${button({ label: 'Načíst paměť rozhovoru', attributes: {'data-debug': 'memory-inspect'} })}</div>
        <p data-memory-status role="status"></p><p data-memory-identity></p><p data-memory-provider></p>
        <ol class="debug-transcript" data-memory-records aria-label="Použitelná paměť rozhovoru"></ol>
        <label><input type="checkbox" data-memory-delete-confirm> Potvrzuji odstranění paměti tohoto rozhovoru.</label>
        <p class="footnote">Před odstraněním ukončete rozhovor. Odstranění osobní paměti jiných rozhovorů ani archivu památek není součástí této akce.</p>
        <div class="actions">${button({ label: 'Odstranit paměť tohoto rozhovoru', variant: 'danger', attributes: {'data-debug': 'memory-delete'} })}</div>
      </section>
      <section class="debug-card" aria-labelledby="voice-heading"><h2 id="voice-heading">Hlas</h2>
        <p>Samostatný test reproduktoru nevolá AI a neovládá kufr. Rozhovor spusťte níže
          pod servisní identitou backendu. Místní párovací token sem nepatří.</p>
        <div class="actions">
          ${button({ label: "Test reproduktoru telefonu", attributes: {"data-debug": "system"} })}
          ${button({ label: "Ověřit backend", attributes: {"data-debug": "inspect"} })}
          ${button({ label: "Zastavit hlas", attributes: {"data-debug": "voice-stop"} })}
        </div>
        <p data-voice-status role="status"></p>
        <p class="footnote">Systémový hlas telefonu slouží pouze k ověření reproduktoru.
          Přístup se ukládá do Keychain iPhonu a zachová při aktualizaci. Do webu ani logu se credential nepředává.</p></section>
      <section class="debug-card debug-conversation" aria-labelledby="debug-conversation-heading">
        <h2 id="debug-conversation-heading">Zkušební rozhovor a výběr hůlky</h2>
        <p>Rozhovor běží na telefonu pod servisní identitou. Dostupnost přihrádek je pouze simulovaná;
          návrh hůlky nespouští pohon kufru. AI režim čerpá OpenAI účet.</p>
        <p data-conversation-personality>Personalitu zvolte a použijte v nastavení serveru výše.</p>
        <p data-conversation-identity></p>
        <fieldset data-guest-memory><legend>Identita a souhlas pro nového hosta</legend>
          <p>Bez stálého ID začne anonymní host. Pro návrat stejného hosta zadejte stejné ID a ověřte ho osobně;
            fotografie ani odhad modelu identitu neověřují. Použijte vlastní evidenční kód, nikoli odhadnuté jméno.
            ID rozlišuje velká a malá písmena; smí mít 1–80 znaků A–Z, a–z, 0–9, tečku, podtržítko a pomlčku.
            Volba se použije před první replikou a neukládá se do telefonu.</p>
          ${field({ id: 'guest-memory-id', label: 'Stálé ID vracejícího se hosta (volitelné)', control: `<input class="aw-input" data-guest-id type="text" maxlength="80" autocomplete="off" placeholder="Například host-042">` })}
          <label><input type="checkbox" data-guest-verified> Obsluha ověřila identitu tohoto hosta.</label>
          <label><input type="checkbox" data-guest-long-term> Host souhlasí s dlouhodobou osobní pamětí.</label>
          <label><input type="checkbox" data-guest-cloud> Host souhlasí také s exportem osobní paměti do cloudového indexu.</label>
          <p data-guest-memory-status class="footnote" role="status"></p>
        </fieldset>
        <fieldset data-conversation-availability><legend>Simulované dostupné přihrádky</legend>
          <div class="debug-ready-grid">
            ${Array.from({ length: 6 }, (_, index) => `<label><input type="checkbox" data-ready="${index + 1}" checked> ${index + 1}</label>`).join('')}
          </div>
        </fieldset>
        <div class="debug-conversation-options">
          ${field({ id: 'conversation-engine', label: "Konverzační engine", control: `<select class="aw-input" data-conversation-engine aria-label="Konverzační engine">
            <option value="gpt-live" selected>GPT Live · gpt-live-1</option>
            <option value="realtime">GPT Realtime 2.1 · gpt-realtime-2.1</option></select>` })}
          ${field({ id: 'conversation-voice', label: "Hlas", control: `<select class="aw-input" data-conversation-voice aria-label="Hlas zkušebního rozhovoru">
            <option value="cedar" selected>Cedar</option><option value="marin">Marin</option>
            <option value="coral">Coral</option></select>` })}
          ${field({ id: 'conversation-speed', label: "Tempo", control: `<select class="aw-input" data-conversation-speed aria-label="Tempo zkušebního rozhovoru">
            <option value="0.9">0,9×</option><option value="1">1×</option>
            <option value="1.1" selected>1,1×</option><option value="1.2">1,2×</option></select>` })}
        </div>
        <p data-engine-status role="status"></p>
        <fieldset data-costume-controls><legend>Volitelná nápověda kostýmu</legend>
          <p>Fotku pořizujte jen se souhlasem hosta. Potvrzením „Použít fotku“ souhlasíte s jejím předáním
            serveru a OpenAI pro tento rozhovor. Fotka se neukládá do galerie ani paměti večera.</p>
          <div class="actions">${button({ label: "Se souhlasem vyfotit kostým", attributes: {"data-debug": "costume-photo"} })}
            ${button({ label: "Odeslat schválenou fotku", attributes: {"data-debug": "costume-attach"} })}
            ${button({ label: "Zrušit fotku", attributes: {"data-debug": "costume-discard"} })}</div>
          <p data-costume-status role="status"></p>
          ${field({ id: 'costume-description', label: "Ruční popis nebo oprava odhadu", control: `<input class="aw-input" type="text" data-costume-description maxlength="240"
            autocomplete="off" placeholder="Například čaroděj s modrým pláštěm">` })}
          <p class="footnote">Popis má přednost před fotkou. Vzhled je nejistá nápověda; neověřuje identitu.
            Po chybě fotky rozhovor pokračuje. Běžný přepis se dál ukládá do paměti večera.</p>
        </fieldset>
        <div class="actions">
          ${button({ label: "Zahájit rozhovor s novým hostem", variant: 'primary', attributes: {"data-debug": "conversation-ai"} })}
          ${button({ label: "Ukončit rozhovor", variant: 'danger', attributes: {"data-debug": "conversation-stop"} })}
        </div>
        <p data-conversation-status role="status"></p>
        <p data-conversation-error role="alert" hidden></p>
        <p data-conversation-audio class="footnote" hidden></p>
        <ol class="debug-transcript" data-conversation-transcript aria-label="Přepis zkušebního rozhovoru"></ol>
        <p class="footnote">GPT Live je hlavní rozhovor; GPT Realtime 2.1 zůstává záměrnou alternativou.
          Povolte mikrofon a mluvte. Po ukončení nový start vytvoří nový rozhovor a nového hosta.
          Náklady a dostupnost řídí OpenAI účet.
          Zastavit jej můžete kdykoli. Hlas se průběžně posílá serveru a přepis může zůstat v paměti vybraného večera.</p>
        <p data-conversation-selection></p>
        <p data-conversation-timings class="footnote"></p>
      </section>
      <section class="debug-card" aria-labelledby="log-heading"><h2 id="log-heading">Log aplikace</h2>
        <p>Posledních 200 změn stavu a hlasových akcí v paměti. Nejde o sériový log ESP.
          Rozhovory, audio ani přístupová tajemství se nezapisují.</p>
        <div class="actions">${field({ id: 'application-log-filter', label: "Filtr", control: `<select class="aw-input" aria-label="Filtr logu">
          <option value="all">Vše</option><option value="device">ESP / simulátor</option>
          <option value="voice">Hlas</option><option value="app">Aplikace</option></select>` })}
          ${button({ label: "Pozastavit zobrazení", attributes: {"data-debug": "pause", "aria-pressed": "false"} })}
          ${button({ label: "Vymazat log", attributes: {"data-debug": "clear"} })}
          ${button({ label: "Exportovat log JSON", attributes: {"data-debug": "export"} })}</div>
        <p data-log-count></p><ol class="debug-log" aria-label="Diagnostické události"></ol>
      </section>`;
    // Keep test-session status and cancellation outside collapsible setup sections.
    // This is a backend test session; it never inherits the device Stop action.
    const sessionBar = document.createElement('section');
    sessionBar.className = 'debug-session-bar';
    sessionBar.setAttribute('aria-label', 'Zkušební rozhovor na telefonu');
    const sessionLabel = document.createElement('strong');
    sessionLabel.textContent = 'Zkušební rozhovor na telefonu';
    sessionBar.append(sessionLabel,
      this.element.querySelector('[data-conversation-status]')!,
      this.element.querySelector('[data-debug="conversation-stop"]')!);
    this.element.querySelector('.section-nav')!.after(sessionBar);
    for (const section of this.element.querySelectorAll<HTMLElement>(':scope > .debug-card')) {
      const heading = section.querySelector<HTMLHeadingElement>('h2')!;
      const group = document.createElement('details');
      group.className = 'diagnostic-group';
      group.open = heading.id === 'debug-conversation-heading';
      const summary = document.createElement('summary');
      section.before(group);
      summary.append(heading);
      group.append(summary, section);
    }
    this.element.querySelector('.section-nav')!.addEventListener('click', event => {
      const link = (event.target as HTMLElement).closest<HTMLAnchorElement>('a[href^="#"]');
      const heading = link && this.element.querySelector<HTMLElement>(link.getAttribute('href')!);
      const group = heading?.closest('details');
      if (group) group.open = true;
    });
    this.element.addEventListener('change', event => {
      if ((event.target as HTMLElement).matches('[data-server="personality"]')) this.renderPersonality();
    });
    this.element.addEventListener('click', (event) => {
      const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-debug]');
      if (button && !button.disabled) { void this.actionAsync(button.dataset.debug!); }
    });
    this.element.querySelector('[aria-label="Filtr logu"]')!.addEventListener('change', (event) => {
      this.category = (event.target as HTMLSelectElement).value as DebugCategory | 'all';
      this.renderLog();
    });
    const engineSelector = this.element.querySelector<HTMLSelectElement>('[data-conversation-engine]')!;
    let storedEngine: string | null = null;
    try { storedEngine = localStorage.getItem(enginePreferenceKey); } catch { /* Storage can be disabled. */ }
    const migration = migrateConversationEngine(storedEngine);
    engineSelector.value = migration.engine ?? '';
    this.engineMigrationMessage = migration.message;
    engineSelector.addEventListener('change', () => {
      this.engineMigrationMessage = undefined;
      try { localStorage.setItem(enginePreferenceKey, engineSelector.value); } catch { /* Session selection still applies. */ }
      this.clearCostumeDraft();
      if (this.native) void bridge.discardCostumePhoto().catch(() => undefined);
      this.renderConversationControls();
    });
    this.element.querySelector('[data-evening-name]')!.addEventListener('input', () => this.renderEveningControls());
    this.element.querySelector('[data-evening-selection]')!.addEventListener('change', () => this.renderEveningControls());
    this.element.querySelector('[data-memory-conversation]')!.addEventListener('input', () => {
      this.clearMemoryInspection();
      this.renderMemory();
    });
    this.element.querySelector('[data-memory-delete-confirm]')!.addEventListener('change', () => this.renderMemory());
    this.element.querySelector('[data-guest-memory]')!.addEventListener('change', event => {
      const control = event.target as HTMLInputElement;
      if (control.matches('[data-guest-verified]') && !control.checked) {
        this.element.querySelector<HTMLInputElement>('[data-guest-long-term]')!.checked = false;
        this.element.querySelector<HTMLInputElement>('[data-guest-cloud]')!.checked = false;
      }
      if (control.matches('[data-guest-long-term]') && !control.checked) {
        this.element.querySelector<HTMLInputElement>('[data-guest-cloud]')!.checked = false;
      }
      this.renderConversationControls();
    });
    this.element.querySelector('[data-guest-id]')!.addEventListener('input', () => {
      for (const selector of ['[data-guest-verified]', '[data-guest-long-term]', '[data-guest-cloud]']) {
        this.element.querySelector<HTMLInputElement>(selector)!.checked = false;
      }
      this.renderConversationControls();
    });
    this.element.querySelector('[data-server-log-level]')!.addEventListener('change', () => {
      this.clearServerLogs();
      void this.refreshServerLogs();
    });
    window.setInterval(() => {
      if (this.visible && !document.hidden && this.element.querySelector<HTMLInputElement>('[data-server-log-live]')!.checked) {
        void this.refreshServerLogs();
      }
    }, 2000);
    window.setInterval(() => {
      if (this.visible && !document.hidden) {
        this.renderDevice();
        if (this.native) { void this.refreshVoiceAsync(); }
      }
    }, 500);
    document.addEventListener('visibilitychange', () => {
      if (document.hidden && (this.photoPending || this.voice.cameraActive)) return;
      if (document.hidden) {
        this.clearServerLogs();
        this.clearCostumeDraft();
        this.clearGuestDraft();
        this.clearMemoryInspection();
        this.actionGeneration += 1;
        this.pending = false;
        this.cancellationPending = false;
        this.statusSequence += 1;
        this.journal.record('background');
        this.voice = { ...this.voice, transcript: [], selected: null, timings: undefined,
          session: false, recording: false, evenings: null, phase: 'cancelled' };
        this.renderVoice();
        if (this.native) { void bridge.stop().catch(() => undefined); }
      } else if (this.native) {
        void this.refreshVoiceAsync();
      }
    });
    this.renderVoice();
  }

  show(visible: boolean): void {
    this.visible = visible;
    this.element.hidden = !visible;
    if (visible) {
      this.renderDevice();
      this.renderLog();
      if (this.native) { void this.refreshVoiceAsync(); }
    }
  }

  update(snapshot: DeviceSnapshot): void {
    this.snapshot = snapshot;
    this.journal.observe(snapshot);
    if (this.visible) {
      this.renderDevice();
      if (!this.paused) { this.renderLog(); }
    }
  }

  private renderDevice(): void {
    const snapshot = this.snapshot;
    const metrics = this.element.querySelector('.debug-metrics')!;
    metrics.replaceChildren();
    const diagnostics = snapshot?.diagnostics;
    const values = {
      'Fáze zařízení': diagnostics?.devicePhase ?? snapshot?.phase ?? 'disconnected', 'Řízení': snapshot?.controlMode ?? 'observer',
      'Dostupné přihrádky': snapshot ? Array.from({ length: 6 }, (_, i) => (snapshot.readyMask & (1 << i)) ? i + 1 : null).filter(Boolean).join(', ') || 'žádné' : '—',
      'Revize': snapshot?.revision ?? '—', 'Událost': diagnostics?.eventSequence ?? '—',
      'Uptime vzorku': diagnostics ? `${(diagnostics.deviceUptimeMs / 1000).toFixed(1)} s` : '—',
      'Heartbeat RTT': diagnostics?.roundTripMs == null ? '—' : `${diagnostics.roundTripMs} ms`,
      'Poruchy': diagnostics?.faultCount ?? '—', 'Boot ID': diagnostics?.bootId ?? '—',
    };
    for (const [label, value] of Object.entries(values)) {
      const term = document.createElement('dt'); term.textContent = label;
      const detail = document.createElement('dd'); detail.textContent = String(value);
      const pair = document.createElement('div'); pair.append(term, detail); metrics.append(pair);
    }
    this.element.querySelector('[data-source]')!.textContent = snapshot?.source === 'native-device'
      ? 'Nativní spojení s kufrem' : snapshot?.source === 'e2-simulator' ? 'HOSTITELSKÝ SIMULÁTOR · bez ESP' : 'WEBOVÁ SIMULACE · bez připojeného ESP';
    this.element.querySelector('[data-freshness]')!.textContent = !diagnostics || snapshot?.phase === 'disconnected'
      ? 'Živá telemetrie ESP není dostupná.'
      : `Poslední zpráva před ${Math.max(0, (performance.now() - diagnostics.receivedAtMs) / 1000).toFixed(1)} s.`;
    this.button('esp-stop').disabled = !snapshot || snapshot.phase === 'disconnected';
    this.button('esp-stop').textContent = snapshot?.source === 'native-device' ? 'Stop kufru' : 'Stop simulace';
  }

  private renderLog(): void {
    const entries = this.journal.read(this.category);
    const list = this.element.querySelector('[aria-label="Diagnostické události"]')!;
    list.replaceChildren();
    for (const entry of entries.reverse()) {
      const row = document.createElement('li');
      row.textContent = `${entry.time.slice(11, 23)} · ${entry.category} · ${entry.code} ${Object.entries(entry.values).map(([key, value]) => `${key}=${value}`).join(' ')}`;
      list.append(row);
    }
    this.element.querySelector('[data-log-count]')!.textContent = `${entries.length} z ${this.journal.read().length} událostí${this.paused ? ' · zobrazení pozastaveno, sběr pokračuje' : ''}`;
  }

  private renderVoice(): void {
    this.renderServerLogs();
    for (const name of ['system', 'configure', 'forget-profile', 'inspect', 'server-inspect', 'server-apply']) {
      this.button(name).disabled = !this.native || this.pending || this.memoryPending || this.voice.running || this.voice.session === true;
    }
    this.button('inspect').disabled ||= !this.voice.configured;
    this.button('forget-profile').disabled ||= !this.voice.configured;
    this.renderServer();
    this.renderEvenings();
    this.renderMemory();
    this.button('voice-stop').disabled = !this.native || this.cancellationPending || (!this.pending && !this.voice.running);
    this.element.querySelector('[data-voice-status]')!.textContent = !this.native
      ? 'Hlasové testy jsou dostupné v nativní iOS aplikaci.'
      : `${phases[this.voice.phase] ?? phases.failed}${!this.voice.configured ? ' · přístup není nastaven' : ''}`;
    this.renderConversation();
  }

  private renderServer(): void {
    const server = this.voice.server;
    const busy = !this.native || !this.voice.configured || this.pending
      || this.voice.running || this.voice.session === true;
    const personality = this.element.querySelector<HTMLSelectElement>('[data-server="personality"]')!;
    if (server?.revision !== this.serverRevision) {
      personality.replaceChildren();
      for (const item of server?.personalities ?? []) {
        const option = document.createElement('option');
        option.value = item.id; option.textContent = item.name;
        personality.append(option);
      }
      if (!personality.options.length) {
        const option = document.createElement('option'); option.value = '';
        option.textContent = server ? 'Server vyžaduje aktualizaci pro personality' : 'Nejprve obnovte stav serveru';
        personality.append(option);
      }
      personality.value = server?.personalityId ?? '';
      this.element.querySelector<HTMLInputElement>('[data-character="enabled"]')!.checked = server?.characterEnabled ?? false;
      this.element.querySelector<HTMLSelectElement>('[data-character="tone"]')!.value = server?.characterTone ?? 'Adult';
      this.element.querySelector<HTMLSelectElement>('[data-character="finale"]')!.value = server?.characterFinaleProfile ?? 'Blocked';
      this.serverRevision = server?.revision;
    }
    personality.disabled = busy || !server?.personalities?.length || server.activeConversations > 0;
    this.renderPersonality();
    this.button('server-inspect').disabled ||= !this.voice.configured;
    this.button('server-apply').disabled ||= !server || server.activeConversations > 0;
    for (const control of this.element.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[data-character]')) {
      control.disabled = busy || !server || typeof server.characterEnabled !== 'boolean';
    }
    this.button('character-apply').disabled = busy || !server || typeof server.characterEnabled !== 'boolean';
    this.button('character-reset').disabled = busy || !server || typeof server.characterEnabled !== 'boolean';
    this.element.querySelector('[data-server-status]')!.textContent = server
      ? `Aktivní rozhovory: ${server.activeConversations}. Dostupnost jednotlivých enginů je uvedena u volby rozhovoru.`
      : this.native ? 'Nastavte přístup správce a obnovte stav serveru.' : 'Správa serveru je dostupná v nativní iOS aplikaci.';
  }

  private renderPersonality(): void {
    const server = this.voice.server;
    const selected = this.element.querySelector<HTMLSelectElement>('[data-server="personality"]')!.value;
    this.element.querySelector('[data-personality-description]')!.textContent =
      server?.personalities?.find(item => item.id === selected)?.description ?? '';
    const active = server?.personalities?.find(item => item.id === server.personalityId);
    this.element.querySelector('[data-conversation-personality]')!.textContent = active
      ? `Test používá aktivní personalitu: ${active.name}. Změnu nejprve použijte v nastavení serveru. Dostupnost enginu a tón ověří skutečný rozhovor.`
      : 'Personalitu zvolte a použijte v nastavení serveru výše.';
  }

  private clearServerLogs(): void {
    this.logsSequence += 1;
    this.serverLogs.clear();
    this.logsMessage = 'Načtěte logy serveru pro aktuální přístup a filtr.';
    this.renderServerLogs();
  }

  private renderServerLogs(): void {
    this.button('logs-refresh').disabled = !this.native || !this.voice.configured || this.logsPending;
    this.button('logs-export').disabled = !this.native || this.serverLogs.entries.length === 0;
    this.element.querySelector<HTMLInputElement>('[data-server-log-live]')!.disabled = !this.native || !this.voice.configured;
    this.element.querySelector('[data-server-log-status]')!.textContent = !this.native
      ? 'Serverové logy jsou dostupné v nativní iOS aplikaci.'
      : `${this.logsMessage}${this.logsPending ? ' · načítám' : ''}${this.serverLogs.gap ? ' · část historie již byla odstraněna' : ''}${this.serverLogs.hasMore ? ' · čekají další události, obnovte log' : ''}${this.serverLogs.droppedWrites ? ` · nezapsané události: ${this.serverLogs.droppedWrites}` : ''}`;
    if (this.renderedLogs === this.serverLogs.entries) { return; }
    this.renderedLogs = this.serverLogs.entries;
    const list = this.element.querySelector('[data-server-log-list]')!;
    list.replaceChildren();
    for (const entry of [...this.serverLogs.entries].reverse()) {
      const row = document.createElement('li');
      row.textContent = `${entry.timestamp.slice(11, 23)} UTC · ${entry.level} · ${entry.eventCode}`
        + (entry.method ? ` · ${entry.method} ${entry.route ?? ''}` : '')
        + (entry.status ? ` · HTTP ${entry.status}` : '')
        + (entry.durationMs != null ? ` · ${entry.durationMs} ms` : '')
        + (entry.code ? ` · ${entry.code}` : '')
        + (entry.traceId ? ` · request=${entry.traceId}` : '')
        + (entry.operationId ? ` · operation=${entry.operationId}` : '')
        + (entry.conversationId ? ` · conversation=${entry.conversationId}` : '');
      list.append(row);
    }
  }

  private async refreshServerLogs(): Promise<void> {
    if (!this.native || !this.voice.configured || this.logsPending || document.hidden) { return; }
    const sequence = this.logsSequence;
    const level = this.element.querySelector<HTMLSelectElement>('[data-server-log-level]')!.value;
    this.logsPending = true; this.renderServerLogs();
    try {
      let page = await bridge.readServerLogs({ after: this.serverLogs.cursor, level });
      if (sequence !== this.logsSequence || document.hidden) { return; }
      if (!this.serverLogs.accept(page)) {
        page = await bridge.readServerLogs({ level });
        if (sequence !== this.logsSequence || document.hidden) { return; }
        this.serverLogs.accept(page);
      }
      this.logsMessage = `${this.serverLogs.entries.length} událostí · obnoveno ${new Date().toLocaleTimeString('cs-CZ')}`;
    } catch {
      if (sequence === this.logsSequence) {
        this.logsMessage = 'Logy se nepodařilo načíst. Ověřte credential správce a verzi serveru. Zobrazené události jsou poslední načtené.';
        this.element.querySelector<HTMLInputElement>('[data-server-log-live]')!.checked = false;
      }
    } finally { this.logsPending = false; this.renderServerLogs(); }
  }

  private renderConversation(): void {
    const transcript = this.element.querySelector<HTMLOListElement>('[data-conversation-transcript]')!;
    transcript.replaceChildren();
    for (const turn of this.voice.transcript ?? []) {
      const row = document.createElement('li');
      const speaker = document.createElement('strong');
      speaker.textContent = turn.role === 'kufr' ? 'Kufr' : 'Vy';
      const content = document.createElement('p');
      content.textContent = turn.text;
      row.append(speaker, content);
      transcript.append(row);
    }

    const selected = this.voice.selected;
    const conversationIdentity = this.voice.conversationId ?? this.voice.lastConversationId;
    this.element.querySelector('[data-conversation-identity]')!.textContent = conversationIdentity
      ? `ID ${this.voice.session ? 'aktuálního' : 'posledního'} rozhovoru: ${conversationIdentity}. Paměť lze zkontrolovat v sekci Paměť hosta.` : 'Nový start vytvoří vlastní ID rozhovoru a nového hosta.';
    const errors: Record<string, string> = {
      cost_limit: 'OpenAI účet omezil dostupnost AI. Ověřte jeho kredit a limity.',
      turn_limit: 'Rozhovor dosáhl maximálního počtu odpovědí. Spusťte nový rozhovor.',
      dialogue_busy: 'Server právě zpracovává jiný rozhovor. Zkuste to za chvíli.',
      realtime_unavailable: 'Realtime hlas není na serveru dostupný. Obnovte stav serveru.',
      native_status_unavailable: 'Stav telefonu se nepodařilo načíst. Rozhovor může stále běžet; použijte Ukončit rozhovor a obnovte stav.',
      microphone_unavailable: 'Povolte mikrofon aplikaci v Nastavení iPhonu.',
      realtime_live_audio_overflow: 'Odesílání mikrofonu se zpozdilo a zaplnilo zvukovou frontu. Zkuste rozhovor znovu; příčinou může být aplikace nebo odezva serveru i při stabilním připojení.',
      realtime_live_audio_start_failed: 'iPhone nemohl spustit mikrofon a reproduktor. Ukončete rozhovor a zkuste jej znovu.',
      realtime_live_audio_interrupted: 'iOS přerušil zvuk nebo změnil mikrofon či reproduktor. Spusťte rozhovor znovu.',
      realtime_live_audio_route_changed: 'Změnil se mikrofon nebo reproduktor. Spusťte rozhovor znovu.',
      realtime_live_audio_session_changed: 'iOS změnil režim zvuku během rozhovoru. Spusťte rozhovor znovu.',
      realtime_live_audio_format_changed: 'iOS změnil formát mikrofonu. Spusťte rozhovor znovu.',
      realtime_live_audio_engine_stopped: 'iOS zastavil zvukový vstup nebo výstup. Spusťte rozhovor znovu.',
      realtime_live_audio_output_stalled: 'Přehrávač iOS nepředává zvuk reproduktoru. Rozhovor byl zastaven; spusťte jej znovu.',
      realtime_live_audio_start_unstable: 'Zvukový vstup se nepodařilo stabilně spustit. Spusťte rozhovor znovu.',
      realtime_live_audio_route_unknown: 'iOS ohlásil nerozpoznanou změnu zvuku. Spusťte rozhovor znovu.',
      realtime_live_audio_services_reset: 'iOS restartoval zvukovou službu. Spusťte rozhovor znovu.',
      realtime_live_audio_conversion_failed: 'Nepodařilo se převést zvuk mikrofonu. Spusťte rozhovor znovu.',
      realtime_live_audio_failed: 'Nepodařilo se odeslat zvuk mikrofonu serveru. Ověřte připojení a spusťte rozhovor znovu.',
      realtime_live_protocol_failed: 'Server poslal neplatnou nebo neúplnou Realtime odpověď. Spusťte rozhovor znovu.',
      realtime_live_http_failed: 'Server odmítl hlasové spojení. Ověřte stav serveru a přístup.',
      realtime_live_connection_failed: 'Hlasové spojení se serverem se přerušilo. Ověřte připojení a spusťte rozhovor znovu.',
      realtime_live_connection_timeout: 'Server včas neodpověděl. Ověřte připojení a spusťte rozhovor znovu.',
      realtime_live_tls_failed: 'iPhone nemohl ověřit zabezpečené spojení se serverem. Ověřte datum telefonu a HTTPS adresu.',
      realtime_live_response_too_large: 'Hlasová odpověď překročila povolenou velikost. Spusťte rozhovor znovu.',
      realtime_cleanup_failed: 'Server nepotvrdil ukončení rozhovoru. Zkuste znovu Ukončit rozhovor.',
    };
    const error = this.element.querySelector<HTMLParagraphElement>('[data-conversation-error]')!;
    error.hidden = !this.voice.errorCode;
    const errorCode = this.voice.errorCode;
    const technicalCode = errorCode && /^[a-z][a-z0-9_]{0,63}$/.test(errorCode) ? ` (${errorCode})` : '';
    error.textContent = errorCode
      ? (errors[errorCode] ?? 'Hlasové spojení se přerušilo. Ukončete rozhovor a spusťte jej znovu.') + technicalCode : '';
    this.element.querySelector('[data-conversation-selection]')!.textContent = selected == null
      ? ''
      : `Simulovaný návrh: přihrádka ${selected}. Žádný pohon se nespustil.`;
    const output = this.voice.audioOutput;
    const audioStatus = this.element.querySelector<HTMLParagraphElement>('[data-conversation-audio]')!;
    audioStatus.hidden = !output;
    const routes = { speaker: 'reproduktor', receiver: 'telefonní sluchátko', external: 'externí výstup', none: 'žádný výstup' };
    audioStatus.textContent = output
      ? `Zvuk: ${routes[output.route] ?? 'neznámý výstup'} · hlasitost ${Math.round(output.volume * 100)} %. `
        + `Přijato ${output.receivedSeconds.toFixed(1)} s, předáno výstupu ${output.renderedSeconds.toFixed(1)} s.`
        + (output.volume < 0.05 ? ' Zvyšte hlasitost bočním tlačítkem během rozhovoru.' : '')
      : '';
    const timings = this.voice.timings;
    this.element.querySelector('[data-conversation-timings]')!.textContent = timings && Number.isFinite(timings.totalMs)
      ? Number.isFinite(timings.firstAudioMs)
        ? `Realtime tah: první audio ${Math.round(timings.firstAudioMs!)} ms · celkem ${Math.round(timings.totalMs)} ms.`
        : [timings.dialogMs, timings.speechMs, timings.downloadMs].every(Number.isFinite)
          ? `Poslední tah: dialog ${Math.round(timings.dialogMs!)} ms · hlas ${Math.round(timings.speechMs!)} ms · stažení ${Math.round(timings.downloadMs!)} ms · celkem ${Math.round(timings.totalMs)} ms.`
          : ''
      : '';
    this.element.querySelector('[data-conversation-status]')!.textContent = !this.native
      ? 'Zkušební rozhovor je dostupný v nativní iOS aplikaci.'
      : this.cancellationPending ? 'Ukončuji rozhovor. S dalším hostem vyčkejte na potvrzení.'
      : this.voice.session
        ? `${phases[this.voice.phase] ?? phases.failed} · rozhovor je aktivní.`
        : selected != null
          ? 'Výběr skončil. Návrh je pouze simulovaný; ukončením smažete přepis.'
          : this.voice.phase === 'completed' && (this.voice.transcript?.length ?? 0) > 0
            ? 'Rozhovor skončil bez návrhu hůlky.'
          : 'Připravte simulovanou dostupnost a spusťte rozhovor.';
    const activity = [
      this.voice.inputActive === true ? 'Mikrofon přijímá zvuk' : null,
      this.voice.outputActive === true ? 'Hlas přehrává odpověď' : null,
      this.voice.toolActive === true ? 'Probíhá práce nástroje' : null,
    ].filter(Boolean).join(' · ');
    const status = this.element.querySelector('[data-conversation-status]')!;
    if (this.voice.session && this.voice.effectiveModel) {
      status.textContent += ` · ${this.voice.effectiveModel}`;
      if (activity) status.textContent += ` · ${activity}`;
    }
    this.element.querySelector('[data-costume-status]')!.textContent =
      this.photoPending || this.photoAttaching || this.photoDraftId || this.photoUsed || this.photoCancellationFailed
        ? this.photoMessage : this.voice.costumeStatus ?? this.photoMessage;
    this.renderConversationControls();
  }

  private renderEvenings(): void {
    const list = this.voice.evenings;
    const selection = this.element.querySelector<HTMLSelectElement>('[data-evening-selection]')!;
    const previous = selection.value;
    const activeChanged = this.eveningRevision !== list?.activeEveningId;
    selection.replaceChildren();
    for (const evening of list?.evenings ?? []) {
      const option = document.createElement('option');
      option.value = evening.eveningId;
      option.textContent = `${evening.name} · ${new Date(evening.createdAt).toLocaleDateString('cs-CZ')} · ${evening.exchangeCount} výměn${evening.eveningId === list?.activeEveningId ? ' · aktivní' : ''}`;
      selection.append(option);
    }
    selection.value = activeChanged ? list?.activeEveningId ?? '' : previous;
    if (!selection.value && list) { selection.value = list.activeEveningId; }
    this.eveningRevision = list?.activeEveningId;
    const active = list?.evenings.find(evening => evening.eveningId === list.activeEveningId);
    this.element.querySelector('[data-evening-status]')!.textContent = !this.native
      ? 'Správa večerů je dostupná v nativní iOS aplikaci.'
      : active ? `Aktivní večer: ${active.name} · ${active.exchangeCount} uložených výměn.`
        : 'Obnovte seznam večerů pro nastavenou identitu.';
    this.renderEveningControls();
  }

  private renderEveningControls(): void {
    const busy = !this.native || !this.voice.configured || this.pending || this.voice.running || this.voice.session === true;
    const list = this.voice.evenings;
    const name = this.element.querySelector<HTMLInputElement>('[data-evening-name]')!;
    const selection = this.element.querySelector<HTMLSelectElement>('[data-evening-selection]')!;
    name.disabled = busy;
    selection.disabled = busy || !list;
    this.button('evenings-inspect').disabled = busy;
    this.button('evening-create').disabled = busy || !list || list.evenings.length >= 100
      || !name.value.trim() || [...name.value.trim()].length > 80;
    this.button('evening-activate').disabled = busy || !list || !selection.value || selection.value === list.activeEveningId;
  }

  private clearCostumeDraft(clearDescription = true): void {
    this.photoSequence += 1;
    if (this.photoTimer !== undefined) window.clearTimeout(this.photoTimer);
    this.photoTimer = undefined;
    this.photoDraftId = undefined;
    this.photoPending = false;
    this.photoAttaching = false;
    this.photoDiscarding = false;
    this.photoUsed = false;
    this.photoCancellationFailed = false;
    this.photoMessage = 'Bez fotky. Rozhovor ji nepotřebuje.';
    this.voice.costumeStatus = null;
    if (clearDescription) this.element.querySelector<HTMLInputElement>('[data-costume-description]')!.value = '';
  }

  private selectedCapability(): EngineCapability | undefined {
    const capabilities = this.voice.server?.engines;
    if (capabilities?.schemaVersion !== 1 || !Array.isArray(capabilities.engines)) return undefined;
    const selected = this.element.querySelector<HTMLSelectElement>('[data-conversation-engine]')!.value;
    return capabilities.engines.find(item => validCapability(item) && item.engine === selected
      && (item.engine === 'gpt-live' ? item.model === 'gpt-live-1' : item.model === 'gpt-realtime-2.1'));
  }

  private guestMemoryDraft(): GuestMemoryBinding | undefined {
    const guestId = this.element.querySelector<HTMLInputElement>('[data-guest-id]')!.value;
    if (!guestId) return undefined;
    return { guestId,
      identityVerified: this.element.querySelector<HTMLInputElement>('[data-guest-verified]')!.checked,
      longTermAllowed: this.element.querySelector<HTMLInputElement>('[data-guest-long-term]')!.checked,
      cloudExportAllowed: this.element.querySelector<HTMLInputElement>('[data-guest-cloud]')!.checked };
  }

  private clearGuestDraft(): void {
    this.element.querySelector<HTMLInputElement>('[data-guest-id]')!.value = '';
    for (const selector of ['[data-guest-verified]', '[data-guest-long-term]', '[data-guest-cloud]']) {
      this.element.querySelector<HTMLInputElement>(selector)!.checked = false;
    }
  }

  private clearMemoryInspection(): void {
    this.memorySequence += 1;
    this.memoryPending = false;
    this.memoryInspection = undefined;
    this.memoryInspectedConversation = undefined;
    this.memoryMessage = 'Načtěte paměť konkrétního rozhovoru. Čtení nevolá AI.';
    this.element.querySelector<HTMLInputElement>('[data-memory-delete-confirm]')!.checked = false;
    this.element.querySelector('[data-memory-records]')!.replaceChildren();
  }

  private renderMemory(): void {
    const current = this.voice.conversationId ?? this.voice.lastConversationId;
    const target = this.element.querySelector<HTMLInputElement>('[data-memory-conversation]')!;
    if (current && current !== this.lastMemoryConversation) {
      this.lastMemoryConversation = current;
      target.value = current;
      this.clearMemoryInspection();
    }
    const conversationId = target.value.trim();
    const busy = !this.native || !this.voice.configured || this.memoryPending || this.pending;
    target.disabled = busy;
    this.button('memory-inspect').disabled = busy || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(conversationId);
    const confirmed = this.element.querySelector<HTMLInputElement>('[data-memory-delete-confirm]')!;
    confirmed.disabled = busy || this.voice.session === true || !this.memoryInspection;
    this.button('memory-delete').disabled = busy || this.voice.session === true || !confirmed.checked
      || !this.memoryInspection || this.memoryInspectedConversation !== conversationId;
    this.element.querySelector('[data-memory-status]')!.textContent = !this.native
      ? 'Kontrola paměti vyžaduje nativní aplikaci a přístup správce.' : this.memoryMessage;
    const identity = this.memoryInspection?.identity;
    this.element.querySelector('[data-memory-identity]')!.textContent = this.memoryInspection
      ? identity ? `Host: ${identity.guestId} · ověření obsluhou: ${identity.identityVerified ? 'ano' : 'ne'} · dlouhodobá paměť: ${identity.longTermAllowed ? 'povolena' : 'zakázána'} · cloudový export: ${identity.cloudExportAllowed ? 'povolen' : 'zakázán'} · verze ${identity.version}.`
        : 'Rozhovor nemá vazbu na stálou osobní identitu.' : '';
    const provider = this.memoryInspection?.memoryStatus;
    const providerStates: Record<string, string> = {
      disabled: 'paměť je na serveru vypnutá', key_missing: 'chybí provider credential',
      store_ids_missing: 'cloudový index není nastaven', not_verified: 'konfigurace nebyla vzdáleně ověřena',
      verified: 'server ověřil dostupnost', configured: 'místní konfigurace je připravena',
    };
    this.element.querySelector('[data-memory-provider]')!.textContent = provider
      ? `Cloudový index: ${providerStates[provider.configurationStatus] ?? 'stav není ověřen'}${provider.degraded ? ' · omezený provoz' : ''}. Čekající zápisy ${provider.pending} · neúspěšné ${provider.failed} · čekající odstranění ${provider.deleting}. Místní záznamy jsou zobrazeny výše.` : '';
  }

  private async memoryActionAsync(action: 'memory-inspect' | 'memory-delete'): Promise<void> {
    if (!this.native || !this.voice.configured || this.memoryPending || this.pending) return;
    const conversationId = this.element.querySelector<HTMLInputElement>('[data-memory-conversation]')!.value.trim();
    if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(conversationId)) return;
    const inspection = this.memoryInspection;
    if (action === 'memory-delete' && (this.voice.session || !inspection || this.memoryInspectedConversation !== conversationId
      || !this.element.querySelector<HTMLInputElement>('[data-memory-delete-confirm]')!.checked)) return;
    const sequence = ++this.memorySequence;
    const generation = this.actionGeneration;
    this.memoryPending = true;
    this.memoryMessage = action === 'memory-inspect' ? 'Načítám oprávněnou paměť…' : 'Odstraňuji paměť vybraného rozhovoru…';
    this.renderMemory();
    this.renderConversationControls();
    try {
      if (action === 'memory-delete') {
        const result = await bridge.deleteConversationMemory({ conversationId, expectedEpoch: inspection!.epoch });
        if (sequence !== this.memorySequence || generation !== this.actionGeneration) return;
        if (result.localDeletion !== 'complete' || !['complete', 'pending'].includes(result.remoteDeletion)) throw new Error('deletion_unconfirmed');
        this.memoryInspection = undefined;
        this.memoryInspectedConversation = undefined;
        this.element.querySelector('[data-memory-records]')!.replaceChildren();
        this.memoryMessage = result.remoteDeletion === 'pending'
          ? 'Místní paměť rozhovoru byla odstraněna. Cloudové odstranění ještě čeká na potvrzení; obnovte kontrolu.'
          : 'Místní i cloudové odstranění paměti rozhovoru bylo potvrzeno.';
      } else {
        const result = await bridge.inspectMemory({ conversationId });
        if (sequence !== this.memorySequence || generation !== this.actionGeneration) return;
        if (!Number.isSafeInteger(result.epoch) || result.epoch < 0 || !Array.isArray(result.records)
          || result.records.length > 50 || !result.memoryStatus) throw new Error('memory_contract_invalid');
        this.memoryInspection = result;
        this.memoryInspectedConversation = conversationId;
        this.memoryMessage = `${result.records.length} z ${result.totalRecords} oprávněných záznamů${result.hasMore ? ' · výpis je omezen na 50' : ''}. Revize paměti ${result.epoch}.`;
        const records = this.element.querySelector('[data-memory-records]')!;
        records.replaceChildren();
        for (const record of result.records) {
          if (typeof record.text !== 'string') continue;
          const row = document.createElement('li');
          const text = document.createElement('p'); text.textContent = record.text.slice(0, 2000);
          const source = document.createElement('small'); source.textContent = `Zdroj: ${String(record.sourceReference ?? '').slice(0, 200)} · revize ${record.revision}`;
          row.append(text, source); records.append(row);
        }
      }
    } catch {
      if (sequence !== this.memorySequence || generation !== this.actionGeneration) return;
      this.memoryMessage = action === 'memory-delete'
        ? 'Odstranění nebylo potvrzeno. Obnovte paměť a znovu potvrďte odstranění; revize se mohla změnit.'
        : 'Paměť se nepodařilo načíst. Ověřte ID rozhovoru, přístup správce a verzi serveru.';
      this.memoryInspection = undefined;
      this.memoryInspectedConversation = undefined;
      this.element.querySelector('[data-memory-records]')!.replaceChildren();
    } finally {
      if (sequence === this.memorySequence && generation === this.actionGeneration) {
        this.memoryPending = false;
        this.element.querySelector<HTMLInputElement>('[data-memory-delete-confirm]')!.checked = false;
        this.renderMemory();
        this.renderConversationControls();
      }
    }
  }

  private async attachCostumePhotoAsync(draftId: string, sequence: number): Promise<void> {
    const generation = this.actionGeneration;
    const conversationId = this.voice.conversationId;
    const sessionGeneration = this.voice.sessionGeneration;
    this.photoAttaching = true;
    this.photoMessage = 'Odesílám schválenou fotku k tomuto rozhovoru. Hlas pokračuje.';
    this.renderConversation();
    try {
      const result = await bridge.attachCostumePhoto({ draftId });
      if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
      if (conversationId !== this.voice.conversationId || sessionGeneration !== this.voice.sessionGeneration) return;
      if (result.status !== 'analysed' || typeof result.imageId !== 'string' || !result.imageId
        || !Number.isSafeInteger(result.revision) || result.revision < 1) throw new Error('image_not_analysed');
      this.clearCostumeDraft(false);
      this.photoUsed = true;
      this.photoMessage = 'Fotka analyzována pro tento rozhovor. Vzhled je nejistá nápověda, nikoli identita.';
    } catch {
      if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
      this.photoMessage = 'Fotka nebyla zpracována. Rozhovor pokračuje; zkuste Odeslat schválenou fotku nebo ji zrušte.';
    } finally {
      if (generation === this.actionGeneration && (sequence === this.photoSequence || !this.photoDraftId)) {
        this.photoAttaching = false;
        this.renderConversation();
      }
    }
  }

  private renderConversationControls(): void {
    const session = this.voice.session === true;
    const busy = !this.native || this.pending || this.cancellationPending || this.memoryPending;
    const capability = this.selectedCapability();
    const cameraBusy = this.photoPending || this.photoAttaching || this.voice.cameraActive === true || this.voice.imagePending === true;
    this.button('costume-photo').disabled = busy || cameraBusy || !this.voice.configured
      || session && !capability?.cameraSources.includes('phone');
    this.button('costume-discard').disabled = !this.native || this.photoDiscarding
      || (!this.photoDraftId && !cameraBusy && !this.photoUsed && !this.photoCancellationFailed);
    this.button('costume-attach').disabled = busy || cameraBusy || !session || !this.photoDraftId;
    this.element.querySelector<HTMLInputElement>('[data-costume-description]')!.disabled = busy || session;
    this.button('conversation-ai').disabled = busy || session || this.voice.running || cameraBusy
      || !this.voice.configured || !capability?.ready;
    this.button('conversation-stop').disabled = !this.native || this.cancellationPending
      || (!this.pending && !session && !this.voice.running && !cameraBusy
        && !this.photoDraftId && (this.voice.transcript?.length ?? 0) === 0 && this.voice.selected == null);
    this.element.querySelectorAll<HTMLInputElement>('[data-ready]').forEach(input => {
      input.disabled = busy || session;
    });
    const engine = this.element.querySelector<HTMLSelectElement>('[data-conversation-engine]')!;
    engine.disabled = busy || session || cameraBusy;
    const speed = this.element.querySelector<HTMLSelectElement>('[data-conversation-speed]')!;
    speed.disabled = busy || session || !capability?.speed.supported;
    if (capability && !capability.speed.supported) speed.value = String(capability.speed.default);
    for (const option of Array.from(speed.options)) {
      option.disabled = !capability || Number(option.value) < capability.speed.min || Number(option.value) > capability.speed.max;
    }
    const voice = this.element.querySelector<HTMLSelectElement>('[data-conversation-voice]')!;
    voice.disabled = busy || session || !capability;
    for (const option of Array.from(voice.options)) option.disabled = !capability?.voices.includes(option.value);
    // No silent engine/voice substitution: a stale explicit setting prevents start.
    const voiceSupported = capability?.voices.includes(voice.value) === true;
    this.button('conversation-ai').disabled ||= !voiceSupported;
    const guest = this.guestMemoryDraft();
    const guestValid = !guest || guest.identityVerified && validGuestMemoryId(guest.guestId)
      && (!guest.cloudExportAllowed || guest.longTermAllowed);
    this.button('conversation-ai').disabled ||= !guestValid;
    for (const input of this.element.querySelectorAll<HTMLInputElement>('[data-guest-memory] input')) input.disabled = busy || session;
    this.element.querySelector<HTMLInputElement>('[data-guest-long-term]')!.disabled ||= !guest?.identityVerified;
    this.element.querySelector<HTMLInputElement>('[data-guest-cloud]')!.disabled ||= !guest?.identityVerified || !guest.longTermAllowed;
    this.element.querySelector('[data-guest-memory-status]')!.textContent = !guest ? 'Nový anonymní host. Osobní paměť jiného hosta se nepoužije.'
      : !validGuestMemoryId(guest.guestId) ? 'ID musí mít 1–80 znaků A–Z, a–z, 0–9, tečku, podtržítko nebo pomlčku. Mezery ani jiné znaky nejsou povoleny; velikost písmen se zachovává.'
      : !guestValid ? 'Před startem ověřte identitu hosta. Cloudový export vyžaduje také souhlas s dlouhodobou pamětí.'
      : `Při startu se ověří vazba na ${guest.guestId}; při chybě se rozhovor nespustí. Po startu se tato volba vymaže pro dalšího hosta.`;
    const status = this.element.querySelector<HTMLElement>('[data-engine-status]')!;
    const readiness = !this.native ? 'Rozhovor vyžaduje nativní iOS aplikaci s uloženým přístupem.'
      : !this.voice.configured ? 'Nastavte servisní přístup k serveru.'
      : !this.voice.server?.engines ? 'Obnovte stav serveru. Tato verze vyžaduje kontrakt enginů v1.'
      : !capability ? 'Server nebo uložená volba vyžadují aktualizaci. Vyberte podporovaný engine a obnovte stav.'
      : !capability.ready ? capability.unavailableReason === 'provider_credential_missing'
        ? 'Na serveru chybí přístup OpenAI. Správce musí nastavit provider credential a obnovit stav.'
        : 'Zvolený engine není dostupný. Ověřte konfiguraci serveru a přístup OpenAI účtu k tomuto modelu; potom obnovte stav.'
      : !voiceSupported ? 'Vybraný hlas engine nepodporuje. Zvolte dostupný hlas.'
      : `Konfigurace připravena · ${capability.model}${capability.speed.supported ? '' : ' · pevné tempo 1×'}. Dostupnost modelu ověří připojení.`;
    status.textContent = [this.engineMigrationMessage, readiness].filter(Boolean).join(' ');

  }

  private button(name: string): HTMLButtonElement {
    return this.element.querySelector<HTMLButtonElement>(`[data-debug="${name}"]`)!;
  }

  private async refreshVoiceAsync(): Promise<void> {
    if (this.statusPending) return;
    this.statusPending = true;
    const generation = this.actionGeneration;
    const sequence = ++this.statusSequence;
    const previousPhase = this.voice.phase;
    let status: VoiceStatus;
    try { status = await bridge.status(); }
    catch { status = { ...this.voice, phase: 'failed', errorCode: 'native_status_unavailable' }; }
    finally { this.statusPending = false; }
    if (sequence !== this.statusSequence || generation !== this.actionGeneration || document.hidden) { return; }
    this.voice = status;
    if (this.voice.phase !== previousPhase && this.voice.phase === 'completed') {
      this.journal.record('voice_finished');
      if (!this.paused) { this.renderLog(); }
    }
    this.renderVoice();
  }

  private async actionAsync(action: string): Promise<void> {
    if (action === 'logs-refresh') { await this.refreshServerLogs(); return; }
    if (action === 'logs-export') {
      await bridge.shareLog({ json: JSON.stringify({ source: 'server', storeId: this.serverLogs.storeId,
        entries: this.serverLogs.entries }, null, 2) }).catch(() => {
        this.logsMessage = 'Sdílení logu se nepodařilo.'; this.renderServerLogs();
      });
      return;
    }
    if (action === 'configure' || action === 'forget-profile') {
      this.clearServerLogs();
      this.clearMemoryInspection();
      this.clearGuestDraft();
      this.lastMemoryConversation = undefined;
      this.element.querySelector<HTMLInputElement>('[data-memory-conversation]')!.value = '';
    }
    if (action === 'esp-stop') { this.stopDevice(); return; }
    if (action === 'pause') {
      this.paused = !this.paused;
      this.button('pause').textContent = this.paused ? 'Pokračovat v zobrazení' : 'Pozastavit zobrazení';
      this.button('pause').setAttribute('aria-pressed', String(this.paused));
      this.renderLog(); return;
    }
    if (action === 'clear') { this.journal.clear(); this.renderLog(); return; }
    if (action === 'export') {
      if (this.native) {
        await bridge.shareLog({ json: this.journal.export() }).catch(() => {
          this.element.querySelector('[data-log-count]')!.textContent = 'Export se nepodařil. Zkuste jej znovu v popředí.';
        });
        return;
      }
      const url = URL.createObjectURL(new Blob([this.journal.export()], { type: 'application/json' }));
      const link = document.createElement('a'); link.href = url; link.download = 'kufr-debug.json';
      link.click(); window.setTimeout(() => URL.revokeObjectURL(url), 1000); return;
    }
    if (!this.native) { return; }
    if (action === 'memory-inspect' || action === 'memory-delete') {
      await this.memoryActionAsync(action);
      return;
    }
    if (action === 'voice-stop' || action === 'conversation-stop') {
      if (this.cancellationPending) return;
      const generation = ++this.actionGeneration;
      this.clearCostumeDraft();
      this.clearGuestDraft();
      this.clearMemoryInspection();
      this.statusSequence += 1;
      this.pending = true;
      this.cancellationPending = true;
      this.renderVoice();
      await bridge.stop().catch(() => undefined);
      if (generation !== this.actionGeneration) return;
      this.cancellationPending = false;
      this.pending = false;
      this.journal.record('voice_stopped'); await this.refreshVoiceAsync();
      if (!this.paused) { this.renderLog(); }
      return;
    }
    if (action === 'costume-discard') {
      if (this.photoDiscarding) return;
      this.clearCostumeDraft(false);
      const sequence = this.photoSequence;
      const generation = this.actionGeneration;
      this.photoPending = true;
      this.photoDiscarding = true;
      this.photoMessage = 'Ruším fotku; hlasový rozhovor pokračuje.';
      this.renderConversation();
      try {
        await bridge.discardCostumePhoto();
        if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
        this.voice.costumeStatus = null;
        this.photoMessage = 'Fotka zrušena. Hlasový rozhovor může pokračovat.';
      } catch {
        if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
        this.photoCancellationFailed = true;
        this.photoMessage = 'Server nepotvrdil zrušení fotky. Zkuste Zrušit fotku znovu; rozhovor pokračuje.';
      } finally {
        if (sequence === this.photoSequence && generation === this.actionGeneration) {
          this.photoPending = false;
          this.photoDiscarding = false;
          this.renderConversation();
        }
      }
      return;
    }
    if (action === 'costume-attach') {
      if (!this.voice.session || !this.photoDraftId || this.photoPending || this.photoAttaching) return;
      await this.attachCostumePhotoAsync(this.photoDraftId, this.photoSequence);
      return;
    }
    if (action === 'costume-photo') {
      if (this.pending || this.photoPending || this.photoAttaching || this.cancellationPending) return;
      const replaceImage = this.photoUsed || this.photoCancellationFailed;
      this.clearCostumeDraft(false);
      const sequence = ++this.photoSequence;
      const generation = this.actionGeneration;
      const capturedForSession = this.voice.session === true;
      const conversationId = this.voice.conversationId;
      const sessionGeneration = this.voice.sessionGeneration;
      this.photoPending = true;
      this.photoMessage = 'Otevírám fotoaparát. Před odesláním potvrďte náhled.';
      this.renderConversation();
      try {
        if (replaceImage) {
          try { await bridge.discardCostumePhoto(); }
          catch {
            if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
            this.photoCancellationFailed = true;
            this.photoMessage = 'Předchozí fotku server neodebral. Zkuste Zrušit fotku znovu; hlas pokračuje.';
            return;
          }
          if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
          this.voice.costumeStatus = null;
        }
        const result = await bridge.captureCostumePhoto();
        if (sequence !== this.photoSequence || generation !== this.actionGeneration) return;
        if (capturedForSession && (!this.voice.session || conversationId !== this.voice.conversationId
          || sessionGeneration !== this.voice.sessionGeneration)) {
          await bridge.discardCostumePhoto().catch(() => undefined);
          this.photoMessage = 'Původní rozhovor skončil. Pro dalšího hosta pořiďte novou fotku.';
          return;
        }
        this.photoDraftId = result.draftId ?? undefined;
        this.photoMessage = result.draftId ? 'Schválená fotka připravena na 2 minuty pro tento rozhovor.'
          : 'Fotka zrušena. Hlasový rozhovor může pokračovat.';
        if (this.photoTimer !== undefined) window.clearTimeout(this.photoTimer);
        if (result.draftId) this.photoTimer = window.setTimeout(() => {
          if (sequence !== this.photoSequence) return;
          this.clearCostumeDraft(false);
          void bridge.discardCostumePhoto().catch(() => undefined);
          this.photoMessage = 'Fotka vypršela. Pořiďte novou nebo pokračujte bez ní.';
          this.renderConversation();
        }, 120000);
      } catch {
        if (sequence === this.photoSequence) this.photoMessage = 'Fotoaparát není dostupný nebo povolený. Rozhovor pokračuje; povolte kameru v Nastavení nebo použijte ruční popis.';
      } finally {
        if (sequence === this.photoSequence) {
          this.photoPending = false;
          this.renderConversation();
          if (this.photoDraftId && this.voice.session && generation === this.actionGeneration) {
            await this.attachCostumePhotoAsync(this.photoDraftId, sequence);
          }
        }
      }
      return;
    }
    if (this.pending || this.memoryPending) { return; }
    const errorText = this.element.querySelector<HTMLElement>('[data-voice-error]')!;
    errorText.hidden = true;
    const generation = ++this.actionGeneration;
    this.statusSequence += 1;
    this.pending = true; this.renderVoice();
    let code: DebugCode | null = 'voice_started';
    try {
      if (action === 'configure') { await bridge.configure(); code = 'profile_changed'; }
      else if (action === 'forget-profile') {
        await bridge.forgetProfile(); code = 'profile_forgotten';
        if (generation !== this.actionGeneration) return;
      }
      else if (action === 'evenings-inspect') { await bridge.inspectEvenings(); code = 'evenings_checked'; }
      else if (action === 'evening-create' || action === 'evening-activate') {
        const list = this.voice.evenings;
        if (!list) { return; }
        if (action === 'evening-create') {
          const name = this.element.querySelector<HTMLInputElement>('[data-evening-name]')!;
          if (!name.value.trim() || [...name.value].length > 80) { return; }
          await bridge.createEvening({ name: name.value.trim(), expectedEveningId: list.activeEveningId });
          if (generation !== this.actionGeneration) return;
          name.value = '';
        } else {
          const eveningId = this.element.querySelector<HTMLSelectElement>('[data-evening-selection]')!.value;
          await bridge.activateEvening({ eveningId, expectedEveningId: list.activeEveningId });
        }
        if (generation !== this.actionGeneration) return;
        code = 'evening_changed';
      }
      else if (action === 'server-inspect') { await bridge.inspectServer(); code = 'backend_checked'; }
      else if (action === 'character-apply' || action === 'character-reset') {
        const server = this.voice.server;
        if (!server) { return; }
        await bridge.changeServerModes({ expectedRevision: server.revision, personalityId: server.personalityId,
          ...(action === 'character-apply' ? {
            characterEnabled: this.element.querySelector<HTMLInputElement>('[data-character="enabled"]')!.checked,
            characterTone: this.element.querySelector<HTMLSelectElement>('[data-character="tone"]')!.value,
            characterFinaleProfile: this.element.querySelector<HTMLSelectElement>('[data-character="finale"]')!.value,
          } : {}),
          resetCharacterVariety: action === 'character-reset' });
        code = 'server_modes_changed';
      }
      else if (action === 'server-apply') {
        const server = this.voice.server;
        if (!server) { return; }
        const personalityId = this.element.querySelector<HTMLSelectElement>('[data-server="personality"]')!.value || undefined;
        await bridge.changeServerModes({ expectedRevision: server.revision, personalityId });
        code = 'server_modes_changed';
      }
      else if (action === 'inspect') { await bridge.inspectBackend(); code = 'backend_checked'; }
      else if (action === 'system') { await bridge.speakSystem(); }
      else if (action === 'conversation-ai') {
        if (this.voice.session || this.voice.running || this.photoPending || this.photoAttaching || this.memoryPending) return;
        const guestMemory = this.guestMemoryDraft();
        if (guestMemory && (!guestMemory.identityVerified || !validGuestMemoryId(guestMemory.guestId)
          || guestMemory.cloudExportAllowed && !guestMemory.longTermAllowed)) return;
        const readyMask = Array.from(this.element.querySelectorAll<HTMLInputElement>('[data-ready]'))
          .reduce((mask, input) => mask | (input.checked ? 1 << (Number(input.dataset.ready) - 1) : 0), 0);
        const voice = this.element.querySelector<HTMLSelectElement>('[data-conversation-voice]')!.value;
        const speed = Number(this.element.querySelector<HTMLSelectElement>('[data-conversation-speed]')!.value);
        const engine = this.element.querySelector<HTMLSelectElement>('[data-conversation-engine]')!.value as ConversationEngine;
        if (!this.selectedCapability()?.ready || !['gpt-live', 'realtime'].includes(engine)) return;
        const photoDraftId = this.photoDraftId;
        const photoSequence = this.photoSequence;
        const costumeDescription = this.element.querySelector<HTMLInputElement>('[data-costume-description]')!.value.trim() || undefined;
        this.photoUsed = false;
        this.element.querySelector<HTMLInputElement>('[data-costume-description]')!.value = '';
        await bridge.startConversation({
          costumeDescription,
          expectedMode: 'openai',
          readyMask, voice, speed, engine,
          ...(guestMemory ? { guestMemory } : {}),
        });
        if (generation !== this.actionGeneration) return;
        this.clearGuestDraft();
        if (photoDraftId && photoSequence === this.photoSequence) {
          await this.refreshVoiceAsync();
          if (generation === this.actionGeneration && this.voice.session) {
            await this.attachCostumePhotoAsync(photoDraftId, photoSequence);
          }
        }
      }
      if (code !== null && generation === this.actionGeneration) {
        const state = code === 'voice_started' ? await bridge.status().catch(() => null) : null;
        if (generation === this.actionGeneration && state?.phase !== 'cancelled') this.journal.record(code);
      }
    } catch {
      // Never render provider/transport error text or credential-bearing URLs.
      if (generation !== this.actionGeneration) return;
      const state = await bridge.status().catch(() => null);
      if (generation !== this.actionGeneration || document.hidden) return;
      if (state?.phase === 'cancelled') {
        this.journal.record('voice_stopped');
      } else {
        this.journal.record('voice_failed');
        errorText.textContent = action.startsWith('evening')
          ? 'Večer nebyl ověřen nebo změněn. Obnovte seznam a ověřte přístup. Probíhající rozhovor nebo souběžná změna přepnutí zablokují.'
          : action.startsWith('server-')
          ? 'Změna nebo ověření serveru nebyly potvrzeny. Obnovte stav serveru. Zkontrolujte credential správce; probíhající rozhovor nebo změna z jiného telefonu přepnutí zablokují.'
          : 'Akce nebyla dokončena. Ověřte HTTPS adresu, testovací debug- identitu, credential a režim serveru. Backend musí podporovat /v1/diagnostics. Po neúspěšném úklidu nebo ztracené odpovědi může být nutné počkat na expiraci testovacího sezení.';
        errorText.hidden = false;
      }
    } finally {
      // A response from a cancelled guest must not unlock or refresh a newer action.
      if (generation === this.actionGeneration) {
        this.pending = false;
        await this.refreshVoiceAsync();
        if (!this.paused) { this.renderLog(); }
      }
    }
  }
}

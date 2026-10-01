import { registerPlugin } from '@capacitor/core';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import { DebugJournal, type DebugCategory, type DebugCode } from './DebugJournal';
import { ServerLogBuffer, type ServerLogPage } from './ServerLogs';

interface VoiceStatus {
  configured: boolean;
  running: boolean;
  session?: boolean;
  recording?: boolean;
  handsFree?: boolean;
  errorCode?: string | null;
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
  dialogMode: 'fake' | 'responses';
  voiceMode: 'fake' | 'openai';
  dialogAvailable: boolean;
  voiceAvailable: boolean;
  activeConversations: number;
  lifetime: 'until_restart';
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
  changeServerModes(options: { expectedRevision: string; dialogMode: string; voiceMode: string }): Promise<void>;
  runOpeningSpeech(options: { expectedMode: 'fake' | 'openai' }): Promise<void>;
  startConversation(options: {
    expectedMode: 'fake' | 'openai'; readyMask: number;
    voice: 'cedar' | 'marin' | 'onyx' | 'coral'; speed: number; engine: 'realtime' | 'realtime-ptt' | 'chained';
  }): Promise<void>;
  answerConversation(options: { text: string }): Promise<void>;
  startRecording(): Promise<void>;
  finishRecording(): Promise<void>;
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
  cancelled: 'Zastaveno', failed: 'Akce selhala; ověřte přístup, server a jeho AI limity',
};

export class DebugPanel {
  readonly element = document.createElement('section');
  readonly journal = new DebugJournal();
  private snapshot?: DeviceSnapshot;
  private paused = false;
  private visible = false;
  private category: DebugCategory | 'all' = 'all';
  private pending = false;
  private serverRevision?: string;
  private eveningRevision?: string;
  private statusSequence = 0;
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
    this.element.innerHTML = `
      <div class="debug-heading"><div><span class="eyebrow">SERVIS A TESTOVÁNÍ</span><h1>Debug</h1></div>
        <button type="button" class="stop" data-debug="esp-stop" disabled>Stop kufru</button></div>
      <p>Záložka sleduje existující spojení. Pro pozorování není nutné převzít řízení.</p>
      <p data-voice-error role="alert" hidden></p>
      <section class="debug-card" aria-labelledby="esp-heading"><h2 id="esp-heading">Běh ESP</h2>
        <p data-source></p><dl class="debug-metrics"></dl>
        <p data-freshness></p>
        <p class="footnote">Údaje pocházejí z přijatých snapshotů. GPIO, napájení ani fyzický pohyb z nich nelze ověřit.</p></section>
      <section class="debug-card" aria-labelledby="server-heading"><h2 id="server-heading">Server</h2>
        <p>Změna platí pro celý backend do restartu. Režim lze změnit mezi rozhovory.
          AI režim může čerpat serverový limit až při spuštění testu nebo rozhovoru.</p>
        <div class="actions">
          <button type="button" data-debug="configure">Nastavit serverový / testovací přístup</button>
          <button type="button" data-debug="forget-profile">Zapomenout uložený přístup</button>
          <button type="button" data-debug="server-inspect">Obnovit stav serveru</button>
          <label>Dialog <select aria-label="Režim dialogu" data-server="dialog">
            <option value="fake">Fake</option><option value="responses">AI · Sol</option></select></label>
          <label>Hlas <select aria-label="Režim hlasu" data-server="voice">
            <option value="fake">Fake · tiché WAV</option><option value="openai">AI · STT / TTS</option></select></label>
          <button type="button" class="primary" data-debug="server-apply">Použít režimy serveru</button>
        </div>
        <p data-server-status role="status"></p>
        <p class="footnote">Správa vyžaduje samostatný credential správce a testovací ID debug-server-admin.
          Běžný přístup kufru nestačí. Po restartu platí režimy nasazení; obnovte stav.</p></section>
      <section class="debug-card" aria-labelledby="server-log-heading"><h2 id="server-log-heading">Log serveru</h2>
        <p>Technické události serveru za posledních 24 hodin, nejvýše 5000 záznamů.
          Zobrazení obsahuje posledních 200 událostí vybraného filtru.</p>
        <div class="actions"><label>Úroveň <select data-server-log-level aria-label="Úroveň serverového logu">
          <option value="info">Všechny</option><option value="warning">Varování a chyby</option><option value="error">Chyby</option></select></label>
          <button type="button" data-debug="logs-refresh">Načíst nové logy</button>
          <button type="button" data-debug="logs-export">Sdílet serverový log JSON</button></div>
        <label><input type="checkbox" data-server-log-live> Automaticky obnovovat každé 2 sekundy</label>
        <p data-server-log-status role="status"></p>
        <ol class="debug-log" data-server-log-list aria-label="Události serveru"></ol>
        <p class="footnote">ID požadavku nebo operace lze sdílet s Codexem pro dohledání chyby.
          Logy neobsahují přístupové údaje, text rozhovoru ani audio.</p></section>
      <section class="debug-card" aria-labelledby="evening-heading"><h2 id="evening-heading">Večery a paměť rozhovorů</h2>
        <p>Nový večer začne s prázdnou pamětí. Přepnutím obnovíte paměť vybraného večera;
          předchozí večery zůstanou uložené. Změny jsou dostupné mezi rozhovory.</p>
        <div class="actions"><button type="button" data-debug="evenings-inspect">Obnovit seznam večerů</button></div>
        <p data-evening-status role="status"></p>
        <label>Uložený večer <select data-evening-selection aria-label="Uložený večer"></select></label>
        <div class="actions"><button type="button" data-debug="evening-activate">Přepnout aktivní večer</button></div>
        <label>Jméno nového večera <input type="text" data-evening-name aria-label="Jméno nového večera"
          maxlength="160" placeholder="Například páteční oslava" autocomplete="off"></label>
        <div class="actions"><button type="button" class="primary" data-debug="evening-create">Založit a aktivovat nový večer</button></div>
        <p class="footnote">Seznam patří nastavené testovací identitě. Obsah rozhovorů se zde nestahuje
          a založení ani přepnutí samo nevolá AI.</p>
      </section>
      <section class="debug-card" aria-labelledby="voice-heading"><h2 id="voice-heading">Hlas</h2>
        <p>AI test přehraje úvodní repliku na telefonu. Neovládá pohony ani reproduktor kufru.
          Vyžaduje samostatné testovací device ID a credential backendu; místní párovací token sem nepatří.
          Replika se může uložit do paměti testovacího večera a test může čerpat serverový AI limit.</p>
        <div class="actions">
          <button type="button" data-debug="system">Test reproduktoru telefonu</button>
          <button type="button" data-debug="inspect">Ověřit backend</button>
          <button type="button" class="primary" data-debug="ai">Test AI hlasu</button>
          <button type="button" data-debug="fake">Test fake audio toku</button>
          <button type="button" data-debug="voice-stop">Zastavit hlas</button>
        </div>
        <p data-voice-status role="status"></p>
        <p class="footnote">Systémový hlas telefonu není AI TTS. Fake backend vrací tiché testovací WAV.
          Přístup se ukládá do Keychain iPhonu a zachová při aktualizaci. Do webu ani logu se credential nepředává.</p></section>
      <section class="debug-card debug-conversation" aria-labelledby="debug-conversation-heading">
        <h2 id="debug-conversation-heading">Zkušební rozhovor a výběr hůlky</h2>
        <p>Rozhovor běží na telefonu pod servisní identitou. Dostupnost přihrádek je pouze simulovaná;
          návrh hůlky nespouští pohon kufru. AI režim může čerpat serverový limit.</p>
        <fieldset data-conversation-availability><legend>Simulované dostupné přihrádky</legend>
          <div class="debug-ready-grid">
            ${Array.from({ length: 6 }, (_, index) => `<label><input type="checkbox" data-ready="${index + 1}" checked> ${index + 1}</label>`).join('')}
          </div>
        </fieldset>
        <div class="debug-conversation-options">
          <label>Tok <select data-conversation-engine aria-label="Tok AI rozhovoru">
            <option value="realtime" selected>Realtime · bez tlačítek</option>
            <option value="realtime-ptt">Realtime · ruční nahrávání</option>
            <option value="chained">Řetězený · kompatibilní</option></select></label>
          <label>Hlas <select data-conversation-voice aria-label="Hlas zkušebního rozhovoru">
            <option value="cedar" selected>Cedar</option><option value="marin">Marin</option>
            <option value="onyx">Onyx</option><option value="coral">Coral</option></select></label>
          <label>Tempo <select data-conversation-speed aria-label="Tempo zkušebního rozhovoru">
            <option value="0.9">0,9×</option><option value="1">1×</option>
            <option value="1.1" selected>1,1×</option><option value="1.2">1,2×</option></select></label>
        </div>
        <div class="actions">
          <button type="button" class="primary" data-debug="conversation-ai">Začít AI rozhovor</button>
          <button type="button" data-debug="conversation-fake">Začít fake rozhovor</button>
          <button type="button" class="stop" data-debug="conversation-stop">Ukončit a smazat rozhovor</button>
        </div>
        <p data-conversation-status role="status"></p>
        <p data-conversation-error role="alert" hidden></p>
        <ol class="debug-transcript" data-conversation-transcript aria-label="Přepis zkušebního rozhovoru"></ol>
        <div data-answer-controls><label class="debug-answer-label" for="debug-answer">Vaše odpověď</label>
        <textarea id="debug-answer" data-conversation-answer maxlength="1000" rows="3"
          placeholder="Napište vlastní odpověď"></textarea>
        <div class="actions">
          <button type="button" class="primary" data-debug="conversation-send">Odeslat odpověď</button>
          <button type="button" data-debug="conversation-record">Začít nahrávat odpověď</button>
        </div>
        </div>
        <p class="footnote">V Realtime stačí spustit rozhovor a povolit mikrofon. Mluvte, když aplikace poslouchá;
          konec odpovědi pozná automaticky a po replice kufru sama pokračuje v poslechu.
          Zastavit jej můžete kdykoli. Hlas se průběžně posílá serveru a přepis může zůstat v paměti vybraného večera.</p>
        <p data-conversation-selection></p>
        <p data-conversation-timings class="footnote"></p>
      </section>
      <section class="debug-card" aria-labelledby="log-heading"><h2 id="log-heading">Log aplikace</h2>
        <p>Posledních 200 změn stavu a hlasových akcí v paměti. Nejde o sériový log ESP.
          Rozhovory, audio ani přístupová tajemství se nezapisují.</p>
        <div class="actions"><label>Filtr <select aria-label="Filtr logu">
          <option value="all">Vše</option><option value="device">ESP / simulátor</option>
          <option value="voice">Hlas</option><option value="app">Aplikace</option></select></label>
          <button type="button" data-debug="pause" aria-pressed="false">Pozastavit zobrazení</button>
          <button type="button" data-debug="clear">Vymazat log</button>
          <button type="button" data-debug="export">Exportovat log JSON</button></div>
        <p data-log-count></p><ol class="debug-log" aria-label="Diagnostické události"></ol>
      </section>`;
    this.element.addEventListener('click', (event) => {
      const button = (event.target as HTMLElement).closest<HTMLButtonElement>('button[data-debug]');
      if (button && !button.disabled) { void this.actionAsync(button.dataset.debug!); }
    });
    this.element.querySelector('[aria-label="Filtr logu"]')!.addEventListener('change', (event) => {
      this.category = (event.target as HTMLSelectElement).value as DebugCategory | 'all';
      this.renderLog();
    });
    this.element.querySelector('[data-conversation-answer]')!.addEventListener('input', () => {
      this.renderConversationControls();
    });
    this.element.querySelector('[data-conversation-engine]')!.addEventListener('change', () => this.renderConversationControls());
    this.element.querySelector('[data-evening-name]')!.addEventListener('input', () => this.renderEveningControls());
    this.element.querySelector('[data-evening-selection]')!.addEventListener('change', () => this.renderEveningControls());
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
      if (document.hidden) {
        this.clearServerLogs();
        this.statusSequence += 1;
        this.journal.record('background');
        this.element.querySelector<HTMLTextAreaElement>('[data-conversation-answer]')!.value = '';
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
    for (const name of ['system', 'configure', 'forget-profile', 'inspect', 'ai', 'fake', 'server-inspect', 'server-apply']) {
      this.button(name).disabled = !this.native || this.pending || this.voice.running || this.voice.session === true;
    }
    this.button('inspect').disabled ||= !this.voice.configured;
    this.button('forget-profile').disabled ||= !this.voice.configured;
    this.button('ai').disabled ||= !this.voice.configured || this.voice.mode !== 'openai';
    this.button('fake').disabled ||= !this.voice.configured || this.voice.mode !== 'fake';
    this.button('fake').disabled ||= this.voice.server?.dialogMode === 'responses';
    this.renderServer();
    this.renderEvenings();
    this.button('voice-stop').disabled = !this.native || (!this.pending && !this.voice.running);
    this.element.querySelector('[data-voice-status]')!.textContent = !this.native
      ? 'Hlasové testy jsou dostupné v nativní iOS aplikaci.'
      : `${phases[this.voice.phase] ?? phases.failed} · backend ${this.voice.mode === 'openai' ? 'AI TTS' : this.voice.mode === 'fake' ? 'FAKE' : 'neověřený'}${!this.voice.configured ? ' · přístup není nastaven' : ''}`;
    this.renderConversation();
  }

  private renderServer(): void {
    const server = this.voice.server;
    const busy = !this.native || !this.voice.configured || this.pending
      || this.voice.running || this.voice.session === true;
    const dialog = this.element.querySelector<HTMLSelectElement>('[data-server="dialog"]')!;
    const voice = this.element.querySelector<HTMLSelectElement>('[data-server="voice"]')!;
    if (server?.revision !== this.serverRevision) {
      dialog.value = server?.dialogMode ?? 'fake';
      voice.value = server?.voiceMode ?? 'fake';
      this.serverRevision = server?.revision;
    }
    dialog.options[1].disabled = !server?.dialogAvailable;
    voice.options[1].disabled = !server?.voiceAvailable;
    dialog.disabled = voice.disabled = busy || !server || server.activeConversations > 0;
    this.button('server-inspect').disabled ||= !this.voice.configured;
    this.button('server-apply').disabled ||= !server || server.activeConversations > 0;
    this.element.querySelector('[data-server-status]')!.textContent = server
      ? `Poslední ověřený stav: dialog ${server.dialogMode === 'responses' ? 'AI · Sol' : 'FAKE'}, hlas ${server.voiceMode === 'openai' ? 'AI · STT / TTS' : 'FAKE'}. Aktivní rozhovory: ${server.activeConversations}.`
      : this.native ? 'Nastavte přístup správce a obnovte stav serveru.' : 'Správa serveru je dostupná v nativní iOS aplikaci.';
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
    const errors: Record<string, string> = {
      cost_limit: 'Rozhovor dosáhl AI limitu serveru. Ukončete jej a zkontrolujte nastavený rozpočet.',
      turn_limit: 'Rozhovor dosáhl maximálního počtu odpovědí. Spusťte nový rozhovor.',
      dialogue_busy: 'Server právě zpracovává jiný rozhovor. Zkuste to za chvíli.',
      realtime_unavailable: 'Realtime hlas není na serveru dostupný. Obnovte stav serveru.',
      microphone_unavailable: 'Povolte mikrofon aplikaci v Nastavení iPhonu.',
      realtime_live_audio_overflow: 'Odesílání mikrofonu se zpozdilo a zaplnilo zvukovou frontu. Zkuste rozhovor znovu; příčinou může být aplikace nebo odezva serveru i při stabilním připojení.',
      realtime_live_audio_start_failed: 'iPhone nemohl spustit mikrofon a reproduktor. Ukončete rozhovor a zkuste jej znovu.',
      realtime_live_audio_interrupted: 'iOS přerušil zvuk nebo změnil mikrofon či reproduktor. Spusťte rozhovor znovu.',
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
      : this.voice.session
        ? `${phases[this.voice.phase] ?? phases.failed} · rozhovor je aktivní.`
        : selected != null
          ? 'Výběr skončil. Návrh je pouze simulovaný; ukončením smažete přepis.'
          : this.voice.phase === 'completed' && (this.voice.transcript?.length ?? 0) > 0
            ? 'Rozhovor skončil bez návrhu hůlky.'
          : 'Připravte simulovanou dostupnost a spusťte rozhovor.';
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

  private renderConversationControls(): void {
    const busy = !this.native || this.pending || this.voice.running;
    const session = this.voice.session === true;
    const server = this.voice.server;
    this.button('conversation-ai').disabled = busy || session || !this.voice.configured
      || this.voice.mode !== 'openai' || server?.dialogMode !== 'responses';
    this.button('conversation-fake').disabled = busy || session || !this.voice.configured
      || this.voice.mode !== 'fake' || server?.dialogMode !== 'fake';
    this.button('conversation-stop').disabled = !this.native
      || (!this.pending && !session && !this.voice.running
        && (this.voice.transcript?.length ?? 0) === 0 && this.voice.selected == null);

    this.element.querySelectorAll<HTMLInputElement>('[data-ready]').forEach((input) => {
      input.disabled = busy || session;
    });
    this.element.querySelector<HTMLSelectElement>('[data-conversation-voice]')!.disabled = busy || session;
    this.element.querySelector<HTMLSelectElement>('[data-conversation-speed]')!.disabled = busy || session;
    const engine = this.element.querySelector<HTMLSelectElement>('[data-conversation-engine]')!;
    engine.disabled = busy || session;
    const conversationVoice = this.element.querySelector<HTMLSelectElement>('[data-conversation-voice]')!;
    const onyx = conversationVoice.querySelector<HTMLOptionElement>('option[value="onyx"]')!;
    onyx.disabled = engine.value !== 'chained';
    this.element.querySelector<HTMLElement>('[data-answer-controls]')!.hidden = this.voice.handsFree === true
      || engine.value === 'realtime' && server?.dialogMode === 'responses';
    if (onyx.disabled && conversationVoice.value === 'onyx') { conversationVoice.value = 'cedar'; }
    const awaiting = session && this.voice.configured && !busy
      && !this.voice.recording && this.voice.phase === 'awaiting';
    const answer = this.element.querySelector<HTMLTextAreaElement>('[data-conversation-answer]')!;
    answer.disabled = !awaiting;
    this.button('conversation-send').disabled = !awaiting || answer.value.trim().length === 0;
    const recording = this.voice.recording === true;
    this.button('conversation-record').disabled = !this.native || !this.voice.configured
      || this.pending || !session
      || (!recording && (this.voice.running || this.voice.phase !== 'awaiting'));
    this.button('conversation-record').textContent = recording
      ? 'Dokončit nahrávání a odeslat' : 'Začít nahrávat odpověď';
  }

  private button(name: string): HTMLButtonElement {
    return this.element.querySelector<HTMLButtonElement>(`[data-debug="${name}"]`)!;
  }

  private async refreshVoiceAsync(): Promise<void> {
    const sequence = ++this.statusSequence;
    const previousPhase = this.voice.phase;
    let status: VoiceStatus;
    try { status = await bridge.status(); }
    catch { status = { configured: false, running: false, phase: 'failed', mode: 'unknown' }; }
    if (sequence !== this.statusSequence || document.hidden) { return; }
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
    if (action === 'configure' || action === 'forget-profile') { this.clearServerLogs(); }
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
    if (action === 'voice-stop' || action === 'conversation-stop') {
      this.statusSequence += 1;
      await bridge.stop().catch(() => undefined);
      this.element.querySelector<HTMLTextAreaElement>('[data-conversation-answer]')!.value = '';
      this.journal.record('voice_stopped'); await this.refreshVoiceAsync();
      if (!this.paused) { this.renderLog(); }
      return;
    }
    if (this.pending) { return; }
    const errorText = this.element.querySelector<HTMLElement>('[data-voice-error]')!;
    errorText.hidden = true;
    this.pending = true; this.renderVoice();
    let code: DebugCode | null = 'voice_started';
    try {
      if (action === 'configure') { await bridge.configure(); code = 'profile_changed'; }
      else if (action === 'forget-profile') {
        await bridge.forgetProfile(); code = 'profile_forgotten';
        this.element.querySelector<HTMLTextAreaElement>('[data-conversation-answer]')!.value = '';
      }
      else if (action === 'evenings-inspect') { await bridge.inspectEvenings(); code = 'evenings_checked'; }
      else if (action === 'evening-create' || action === 'evening-activate') {
        const list = this.voice.evenings;
        if (!list) { return; }
        if (action === 'evening-create') {
          const name = this.element.querySelector<HTMLInputElement>('[data-evening-name]')!;
          if (!name.value.trim() || [...name.value].length > 80) { return; }
          await bridge.createEvening({ name: name.value.trim(), expectedEveningId: list.activeEveningId });
          name.value = '';
        } else {
          const eveningId = this.element.querySelector<HTMLSelectElement>('[data-evening-selection]')!.value;
          await bridge.activateEvening({ eveningId, expectedEveningId: list.activeEveningId });
        }
        this.element.querySelector<HTMLTextAreaElement>('[data-conversation-answer]')!.value = '';
        code = 'evening_changed';
      }
      else if (action === 'server-inspect') { await bridge.inspectServer(); code = 'backend_checked'; }
      else if (action === 'server-apply') {
        const server = this.voice.server;
        if (!server) { return; }
        const dialogMode = this.element.querySelector<HTMLSelectElement>('[data-server="dialog"]')!.value;
        const voiceMode = this.element.querySelector<HTMLSelectElement>('[data-server="voice"]')!.value;
        await bridge.changeServerModes({ expectedRevision: server.revision, dialogMode, voiceMode });
        code = 'server_modes_changed';
      }
      else if (action === 'inspect') { await bridge.inspectBackend(); code = 'backend_checked'; }
      else if (action === 'system') { await bridge.speakSystem(); }
      else if (action === 'ai' || action === 'fake') {
        this.journal.record('voice_started');
        await bridge.runOpeningSpeech({ expectedMode: action === 'ai' ? 'openai' : 'fake' });
        code = null;
      }
      else if (action === 'conversation-ai' || action === 'conversation-fake') {
        const readyMask = Array.from(this.element.querySelectorAll<HTMLInputElement>('[data-ready]'))
          .reduce((mask, input) => mask | (input.checked ? 1 << (Number(input.dataset.ready) - 1) : 0), 0);
        const voice = this.element.querySelector<HTMLSelectElement>('[data-conversation-voice]')!.value as
          'cedar' | 'marin' | 'onyx' | 'coral';
        const speed = Number(this.element.querySelector<HTMLSelectElement>('[data-conversation-speed]')!.value);
        const engine = this.element.querySelector<HTMLSelectElement>('[data-conversation-engine]')!.value as 'realtime' | 'realtime-ptt' | 'chained';
        await bridge.startConversation({
          expectedMode: action === 'conversation-ai' ? 'openai' : 'fake',
          readyMask, voice, speed, engine: action === 'conversation-ai' ? engine : 'chained',
        });
      }
      else if (action === 'conversation-send') {
        const answer = this.element.querySelector<HTMLTextAreaElement>('[data-conversation-answer]')!;
        const text = answer.value.trim();
        if (!text || text.length > 1000) { return; }
        await bridge.answerConversation({ text });
        answer.value = '';
        code = null;
      }
      else if (action === 'conversation-record') {
        if (this.voice.recording) {
          await bridge.finishRecording();
        } else {
          await bridge.startRecording();
        }
        code = null;
      }
      if (code !== null) { this.journal.record(code); }
    } catch {
      // Never render provider/transport error text or credential-bearing URLs.
      const state = await bridge.status().catch(() => null);
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
      this.pending = false;
      await this.refreshVoiceAsync();
      if (!this.paused) { this.renderLog(); }
    }
  }
}

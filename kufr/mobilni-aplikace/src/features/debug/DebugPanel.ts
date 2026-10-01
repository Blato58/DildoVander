import { registerPlugin } from '@capacitor/core';
import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import { DebugJournal, type DebugCategory, type DebugCode } from './DebugJournal';

interface VoiceStatus {
  configured: boolean;
  running: boolean;
  phase: 'idle' | 'checking' | 'opening' | 'speech' | 'playing' | 'completed' | 'cancelled' | 'failed';
  mode: 'unknown' | 'fake' | 'openai';
}
interface DebugBridge {
  status(): Promise<VoiceStatus>;
  configure(): Promise<void>;
  inspectBackend(): Promise<void>;
  runOpeningSpeech(options: { expectedMode: 'fake' | 'openai' }): Promise<void>;
  speakSystem(): Promise<void>;
  stop(): Promise<void>;
  shareLog(options: { json: string }): Promise<void>;
}
const bridge = registerPlugin<DebugBridge>('KufrDebug');
const phases: Record<VoiceStatus['phase'], string> = {
  idle: 'Připraveno', checking: 'Ověřuji backend', opening: 'Čekám na úvodní repliku',
  speech: 'Generuji hlas', playing: 'Přehrávám na telefonu', completed: 'Dokončeno',
  cancelled: 'Zastaveno', failed: 'Test selhal; ověřte přístup, server a jeho AI limity',
};

export class DebugPanel {
  readonly element = document.createElement('section');
  readonly journal = new DebugJournal();
  private snapshot?: DeviceSnapshot;
  private paused = false;
  private visible = false;
  private category: DebugCategory | 'all' = 'all';
  private pending = false;
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
      <section class="debug-card" aria-labelledby="esp-heading"><h2 id="esp-heading">Běh ESP</h2>
        <p data-source></p><dl class="debug-metrics"></dl>
        <p data-freshness></p>
        <p class="footnote">Údaje pocházejí z přijatých snapshotů. GPIO, napájení ani fyzický pohyb z nich nelze ověřit.</p></section>
      <section class="debug-card" aria-labelledby="voice-heading"><h2 id="voice-heading">Hlas</h2>
        <p>AI test přehraje úvodní repliku na telefonu. Neovládá pohony ani reproduktor kufru.
          Vyžaduje samostatné testovací device ID a credential backendu; místní párovací token sem nepatří.
          Replika se může uložit do paměti testovacího večera a test může čerpat serverový AI limit.</p>
        <div class="actions">
          <button type="button" data-debug="system">Test reproduktoru telefonu</button>
          <button type="button" data-debug="configure">Nastavit testovací backend</button>
          <button type="button" data-debug="inspect">Ověřit backend</button>
          <button type="button" class="primary" data-debug="ai">Test AI hlasu</button>
          <button type="button" data-debug="fake">Test fake audio toku</button>
          <button type="button" data-debug="voice-stop">Zastavit hlas</button>
        </div>
        <p data-voice-status role="status"></p><p data-voice-error role="alert" hidden></p>
        <p class="footnote">Systémový hlas telefonu není AI TTS. Fake backend vrací tiché testovací WAV.
          Nastavení backendu žije jen v nativní paměti do pozadí nebo ukončení aplikace.</p></section>
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
    this.element.querySelector('select')!.addEventListener('change', (event) => {
      this.category = (event.target as HTMLSelectElement).value as DebugCategory | 'all';
      this.renderLog();
    });
    window.setInterval(() => {
      if (this.visible && !document.hidden) {
        this.renderDevice();
        if (this.native) { void this.refreshVoiceAsync(); }
      }
    }, 1000);
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) {
        this.journal.record('background');
        if (this.native) { void bridge.stop().catch(() => undefined); }
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
    const metrics = this.element.querySelector('dl')!;
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
    const list = this.element.querySelector('ol')!;
    list.replaceChildren();
    for (const entry of entries.reverse()) {
      const row = document.createElement('li');
      row.textContent = `${entry.time.slice(11, 23)} · ${entry.category} · ${entry.code} ${Object.entries(entry.values).map(([key, value]) => `${key}=${value}`).join(' ')}`;
      list.append(row);
    }
    this.element.querySelector('[data-log-count]')!.textContent = `${entries.length} z ${this.journal.read().length} událostí${this.paused ? ' · zobrazení pozastaveno, sběr pokračuje' : ''}`;
  }

  private renderVoice(): void {
    for (const name of ['system', 'configure', 'inspect', 'ai', 'fake']) {
      this.button(name).disabled = !this.native || this.pending || this.voice.running;
    }
    this.button('inspect').disabled ||= !this.voice.configured;
    this.button('ai').disabled ||= !this.voice.configured || this.voice.mode !== 'openai';
    this.button('fake').disabled ||= !this.voice.configured || this.voice.mode !== 'fake';
    this.button('voice-stop').disabled = !this.native || (!this.pending && !this.voice.running);
    this.element.querySelector('[data-voice-status]')!.textContent = !this.native
      ? 'Hlasové testy jsou dostupné v nativní iOS aplikaci.'
      : `${phases[this.voice.phase] ?? phases.failed} · backend ${this.voice.mode === 'openai' ? 'AI TTS' : this.voice.mode === 'fake' ? 'FAKE' : 'neověřený'}${!this.voice.configured ? ' · přístup není nastaven' : ''}`;
  }

  private button(name: string): HTMLButtonElement {
    return this.element.querySelector<HTMLButtonElement>(`[data-debug="${name}"]`)!;
  }

  private async refreshVoiceAsync(): Promise<void> {
    const previousPhase = this.voice.phase;
    try { this.voice = await bridge.status(); }
    catch { this.voice = { ...this.voice, configured: false, running: false, phase: 'failed', mode: 'unknown' }; }
    if (this.voice.phase !== previousPhase && this.voice.phase === 'completed') {
      this.journal.record('voice_finished');
      if (!this.paused) { this.renderLog(); }
    }
    this.renderVoice();
  }

  private async actionAsync(action: string): Promise<void> {
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
    if (action === 'voice-stop') {
      await bridge.stop().catch(() => undefined);
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
      else if (action === 'inspect') { await bridge.inspectBackend(); code = 'backend_checked'; }
      else if (action === 'system') { await bridge.speakSystem(); }
      else if (action === 'ai' || action === 'fake') {
        this.journal.record('voice_started');
        await bridge.runOpeningSpeech({ expectedMode: action === 'ai' ? 'openai' : 'fake' });
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
        errorText.textContent = 'Akce nebyla dokončena. Ověřte HTTPS adresu, testovací debug- identitu, credential a režim serveru. Backend musí podporovat /v1/diagnostics. Po neúspěšném úklidu nebo ztracené odpovědi může být nutné počkat na expiraci testovacího sezení.';
        errorText.hidden = false;
      }
    } finally {
      this.pending = false;
      await this.refreshVoiceAsync();
      if (!this.paused) { this.renderLog(); }
    }
  }
}

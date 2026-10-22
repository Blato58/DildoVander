import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { account, accountOrigin, hasRole } from '../identity/session';
import { LabError, PersonaLabClient, technicalDiagnostics } from './client';
import { LabVoice, type VoiceObserver, type VoicePhase } from './voice';
import { isSpokenEndCommand, SpokenEndInput } from './spokenControls';
import { directorStop, registerDirectorStop, clearDirectorStop } from '../director/directorStop';
import { prepareLabImage, type LabImage } from './image';
import { classifyLabFailure, describeLabFailure, type LabFailure } from './errors';
import { useLabHardware } from './useLabHardware';
import { systemDiagnosticsBundle } from './systemDiagnostics';
import { metricLabels, type Analytics, type Catalog, type Evaluation, type LabEngine, type LabSession, type LabSuite, type Metric, type Revision, type Scores, type SessionInput, type SystemDiagnostics } from './contracts';

export function usePersonaLab() {
  const identityKey = () => account.value ? `${account.value.id}:${[...account.value.roles].sort().join(',')}` : '';
  const client = new PersonaLabClient(identityKey);
  const catalog = shallowRef<Catalog | null>(null);
  const sessions = shallowRef<LabSession[]>([]);
  const selected = shallowRef<LabSession | null>(null);
  const analytics = shallowRef<Analytics | null>(null);
  const systemDiagnostics = shallowRef<SystemDiagnostics | null>(null); const diagnosticsLoading = ref(false);
  const suites = shallowRef<LabSuite[]>([]); const suiteScenarioIds = ref<string[]>([]);
  const loading = ref(false); const busy = ref(false); const error = ref(''); const notice = ref('');
  const failure = shallowRef<LabFailure | null>(null);
  const voicePhase = ref<VoicePhase>('idle'); const muted = ref(false); const recoverable = ref(false); const reconnects = ref(0);
  const recoveryPending = ref(false); const online = ref(navigator.onLine); const conversationPending = ref(false); const now = ref(Date.now());
  const manualVoiceRestart = ref(false);
  const partial = ref({ user: '', assistant: '' });
  const revisionId = ref(''); const engine = ref<LabEngine>('gpt-live-1'); const scenarioId = ref(''); const fixtureId = ref('');
  const label = ref(''); const comparisonId = ref(''); const text = ref(''); const recordContent = ref(false);
  const providerConsent = ref(false);
  const scores = ref<Partial<Scores>>({}); const comment = ref(''); const turnComments = ref<Record<string, string>>({});
  const draftName = ref(''); const draftInstructions = ref(''); const draftVoice = ref(''); const draftEngine = ref<LabEngine>('gpt-live-1');
  const configRevisionId = ref(''); const configConfirmed = ref(false);
  const image = shallowRef<LabImage | null>(null); const imageConsent = ref(false); const imagePreparing = ref(false);
  const imageAttachedSession = ref(''); let imageGeneration = 0; let imageController = new AbortController();
  let generation = 0; let disposed = false; let listGeneration = 0; let selectedReadGeneration = 0;
  let ageTimer: ReturnType<typeof setInterval> | null = null;
  let recoveryDelay: { timer: ReturnType<typeof setTimeout>; finish: (value: boolean) => void } | null = null;
  const admin = computed(() => hasRole('Administrator'));
  const voiceActive = computed(() => ['connecting', 'waiting', 'listening', 'speaking'].includes(voicePhase.value));
  const sessionAgeSeconds = computed(() => selected.value ? Math.max(0, Math.floor(((selected.value.endedAt ? Date.parse(selected.value.endedAt) : now.value) - Date.parse(selected.value.createdAt)) / 1000)) : null);
  const configRevision = computed(() => catalog.value?.versions.find(item => item.id === configRevisionId.value) ?? null);
  const validEvaluation = computed(() => (Object.keys(metricLabels) as Metric[]).every(key =>
    Number.isInteger(scores.value[key]) && scores.value[key]! >= 1 && scores.value[key]! <= 5));
  const engineReady = computed(() => !!catalog.value && (engine.value === 'gpt-live-1' ? catalog.value.capabilities.live : catalog.value.capabilities.realtime));
  const selectedEngineReady = computed(() => !!catalog.value && (selected.value?.engine === 'gpt-realtime-2.1' ? catalog.value.capabilities.realtime : catalog.value.capabilities.live));

  function remember(session: LabSession): void {
    if (session.ownerId !== account.value?.id) return;
    const existing = sessions.value.findIndex(item => item.id === session.id);
    sessions.value = existing < 0 ? [session, ...sessions.value] : sessions.value.map(item => item.id === session.id ? session : item);
    if (selected.value?.id === session.id) selected.value = session;
    if (session.hasImage === false && imageAttachedSession.value === session.id) imageAttachedSession.value = '';
  }
  function selectSession(session: LabSession): void {
    if (busy.value || voiceActive.value || recoveryPending.value || hardware.active.value || session.ownerId !== account.value?.id) return;
    clearImage(); selected.value = session; partial.value = { user: '', assistant: '' }; voicePhase.value = 'idle'; recoverable.value = false; reconnects.value = 0;
    scores.value = { ...session.evaluation?.scores }; comment.value = session.evaluation?.comment ?? '';
    turnComments.value = Object.fromEntries((session.evaluation?.turnComments ?? []).map(item => [item.turnId, item.comment]));
  }
  async function refreshSelected(): Promise<void> {
    const session = selected.value; const epoch = generation; const request = ++selectedReadGeneration;
    if (!session) return;
    try { const result = await client.session(session.id); if (epoch === generation && request === selectedReadGeneration) remember(result); }
    catch (cause) { if (epoch === generation && request === selectedReadGeneration && !(cause instanceof DOMException && cause.name === 'AbortError')) error.value = message(cause); }
  }
  interface VoiceContext { sessionId: string; identity: string; epoch: number; input: SpokenEndInput }
  let voiceContext: VoiceContext | null = null;
  let spokenEndTimer: ReturnType<typeof setTimeout> | null = null;
  let endingSession = '';
  function cancelSpokenEnd(): void {
    if (spokenEndTimer) clearTimeout(spokenEndTimer);
    spokenEndTimer = null;
  }
  function currentVoice(context: VoiceContext | null): boolean {
    return context === voiceContext && !disposed && (!context || context.epoch === generation
      && context.identity === identityKey() && selected.value?.id === context.sessionId);
  }
  function requestSpokenEnd(context: VoiceContext): void {
    const session = selected.value;
    if (!currentVoice(context) || !voiceActive.value || !session || session.status !== 'open'
      || session.ownerId !== account.value?.id || simulationBlocked.value || session.hardware || endingSession) return;
    // end() tears down local audio synchronously, then uses the authenticated session endpoint.
    void end().catch(cause => { if (!disposed) error.value = message(cause); });
  }
  function scheduleSpokenEnd(context: VoiceContext): void {
    cancelSpokenEnd();
    // Wait for adjacent Live deltas before interpreting the whole bounded utterance.
    spokenEndTimer = setTimeout(() => {
      spokenEndTimer = null;
      if (context.input.matches()) requestSpokenEnd(context);
    }, 500);
  }
  function voiceObserver(context: VoiceContext | null): VoiceObserver { return {
    phase: phase => { if (currentVoice(context)) voicePhase.value = phase; },
    transcript: (role, delta) => {
      if (!currentVoice(context)) return;
      partial.value = { ...partial.value, [role]: (partial.value[role] + delta).slice(-32000) };
      if (context && role === 'user' && selected.value?.engine === 'gpt-live-1') {
        context.input.append(delta); scheduleSpokenEnd(context);
      }
    },
    turn: turn => {
      if (!currentVoice(context) || !selected.value) return;
      if (context && turn.role === 'user') {
        cancelSpokenEnd(); context.input.reset();
        if (selected.value.engine === 'gpt-realtime-2.1' && isSpokenEndCommand(turn.text)) requestSpokenEnd(context);
      }
      if (context && turn.role === 'assistant') {
        cancelSpokenEnd();
        if (context.input.matches() || turn.decision === 'no_selection') requestSpokenEnd(context);
        context.input.reset();
      }
      const turns = selected.value.turns.filter(item => item.id !== turn.id);
      remember({ ...selected.value, turns: [...turns, turn] }); partial.value = { ...partial.value, [turn.role]: '' };
    },
    session: session => { if (currentVoice(context) && (!context || session.id === context.sessionId)) remember(session); },
    interrupted: () => { if (!currentVoice(context)) return; partial.value = { ...partial.value, assistant: '' }; },
    error: (code, canRecover) => {
      if (!currentVoice(context)) return;
      cancelSpokenEnd();
      const details = classifyLabFailure(code);
      failure.value = details;
      error.value = describeLabFailure(details);
      recoverable.value = canRecover && details.recoverable && online.value && reconnects.value < 2;
      manualVoiceRestart.value = details.manualRestart;
    },
    closed: () => { if (currentVoice(context)) { cancelSpokenEnd(); muted.value = false; void refreshSelected(); } },
  }; }
  let voice = new LabVoice(voiceObserver(null));
  function message(cause: unknown, code = 'unknown_client_error'): string {
    const details = cause instanceof LabError ? cause.failure : classifyLabFailure(code);
    failure.value = cause instanceof LabError || !(cause instanceof Error) ? details : { ...details, message: cause.message };
    return describeLabFailure(failure.value);
  }
  function clearFailure(): void { error.value = ''; failure.value = null; }
  const hardware = useLabHardware({ selected, catalog, busy, identity: identityKey, ownerId: () => account.value?.id ?? '',
    isolatedBusy: () => !providerConsent.value || voiceActive.value || conversationPending.value || recoveryPending.value || imagePreparing.value || !!selected.value?.hasImage || !!image.value,
    remember, failure: cause => { error.value = message(cause); }, notice: value => { notice.value = value; } });
  const simulationBlocked = computed(() => hardware.mode.value || hardware.bound.value || hardware.pending.value);
  const noticeTone = computed(() => hardware.mode.value || hardware.bound.value ? 'warning' as const : 'success' as const);
  async function run(action: () => Promise<void>): Promise<void> {
    if (busy.value || disposed) return;
    const epoch = generation; selectedReadGeneration++; listGeneration++; loading.value = false;
    busy.value = true; clearFailure(); notice.value = '';
    try { await action(); }
    catch (cause) { if (epoch === generation && !(cause instanceof DOMException && cause.name === 'AbortError')) error.value = message(cause); }
    finally { if (epoch === generation) busy.value = false; }
  }
  async function load(): Promise<void> {
    if (!account.value || disposed) return;
    const epoch = generation; const request = ++listGeneration; loading.value = true; clearFailure();
    try {
      const [nextCatalog, nextSessions, nextAnalytics, nextSuites] = await Promise.all([client.catalog(), client.sessions(), client.analytics(), client.suites()]);
      if (epoch !== generation || request !== listGeneration) return;
      if (nextCatalog.capabilities.defaultMode !== 'simulation'
        && !(nextCatalog.capabilities.simulationOnly === true && nextCatalog.capabilities.defaultMode === undefined))
        throw new Error('Server nepotvrdil výchozí simulační režim. Testování zůstává vypnuté.');
      catalog.value = nextCatalog; sessions.value = nextSessions.filter(item => item.ownerId === account.value?.id); analytics.value = nextAnalytics;
      suites.value = nextSuites.filter(item => item.ownerId === account.value?.id);
      if (!nextCatalog.versions.some(item => item.id === revisionId.value)) revisionId.value = nextCatalog.activeRevisionId ?? nextCatalog.versions[0]?.id ?? '';
      if (!configRevisionId.value) configRevisionId.value = revisionId.value;
      if (!scenarioId.value) scenarioId.value = nextCatalog.scenarios[0]?.id ?? '';
      if (!fixtureId.value) fixtureId.value = nextCatalog.memoryFixtures[0]?.id ?? '';
      if (selected.value) selected.value = sessions.value.find(item => item.id === selected.value!.id) ?? null;
    } catch (cause) { if (epoch === generation && request === listGeneration) error.value = message(cause); }
    finally { if (epoch === generation && request === listGeneration) loading.value = false; }
  }
  async function createSession(input: SessionInput): Promise<void> {
    if (!providerConsent.value || voiceActive.value || recoveryPending.value || hardware.active.value || !online.value || !input.revisionId || !input.scenarioId || !input.fixtureId) return;
    await run(async () => {
      const session = await client.createSession(input);
      remember(session); selected.value = session; scores.value = {}; comment.value = ''; turnComments.value = {};
      clearImage();
      partial.value = { user: '', assistant: '' }; voicePhase.value = 'idle'; reconnects.value = 0;
      notice.value = 'Sezení je připravené. Model se spustí odesláním textu nebo spuštěním hlasu.';
    });
  }
  async function create(): Promise<void> {
    await createSession({ revisionId: revisionId.value, engine: engine.value, scenarioId: scenarioId.value,
      fixtureId: fixtureId.value, label: label.value.trim() || 'Test persony', recordContent: recordContent.value,
      ...(comparisonId.value.trim() ? { comparisonId: comparisonId.value.trim() } : {}) });
  }
  const canCreateVisualTest = computed(() => {
    const session = selected.value;
    return providerConsent.value && !!session && session.status === 'open' && session.engine === 'gpt-live-1'
      && !busy.value && !voiceActive.value && !recoveryPending.value && !simulationBlocked.value
      && !session.hardware && online.value && !!catalog.value?.capabilities.realtime && !!catalog.value?.capabilities.imageRealtime
      && !!catalog.value.versions.some(item => item.id === session.config.id)
      && !!catalog.value.scenarios.some(item => item.id === session.scenarioId)
      && !!catalog.value.memoryFixtures.some(item => item.id === session.fixtureId);
  });
  async function createVisualTest(): Promise<void> {
    const session = selected.value;
    if (!session || !canCreateVisualTest.value) return;
    await createSession({ revisionId: session.config.id, engine: 'gpt-realtime-2.1', scenarioId: session.scenarioId,
      fixtureId: session.fixtureId, label: session.label, recordContent: session.recordContent,
      ...(session.comparisonId ? { comparisonId: session.comparisonId } : {}) });
  }
  async function send(): Promise<void> {
    const session = selected.value; const input = text.value.trim();
    if (!providerConsent.value || !session || session.status !== 'open' || !input || busy.value || voiceActive.value || recoveryPending.value || simulationBlocked.value || !online.value) return;
    const epoch = generation; conversationPending.value = true;
    try { await run(async () => { const result = await client.text(session.id, input); remember(result); text.value = ''; }); }
    finally { if (epoch === generation) conversationPending.value = false; }
  }
  function cancelRecovery(): void {
    if (recoveryDelay) { clearTimeout(recoveryDelay.timer); recoveryDelay.finish(false); recoveryDelay = null; }
    recoveryPending.value = false;
  }
  function waitForRecovery(milliseconds: number): Promise<boolean> {
    return new Promise(resolve => {
      const timer = setTimeout(() => { recoveryDelay = null; resolve(true); }, milliseconds);
      recoveryDelay = { timer, finish: resolve };
    });
  }
  async function startVoice(retry = false): Promise<void> {
    const session = selected.value;
    if (!providerConsent.value || !session || session.status !== 'open' || busy.value || voiceActive.value || recoveryPending.value || simulationBlocked.value || !online.value || !selectedEngineReady.value) return;
    if (retry && (!recoverable.value || reconnects.value >= 2)) return;
    if (retry) {
      reconnects.value++; recoveryPending.value = true; recoverable.value = false;
      notice.value = 'Připravuji nové spojení po krátké prodlevě. Nový modelový kontext obnoví jen archivované tahy; neuložený hlasový obsah se po odpojení neobnoví.';
      const epoch = generation;
      const proceed = await waitForRecovery(500 * 2 ** (reconnects.value - 1) + Math.floor(Math.random() * 250));
      if (epoch !== generation) return;
      recoveryPending.value = false;
      if (!proceed || !providerConsent.value || selected.value?.id !== session.id || !online.value) return;
    }
    recoverable.value = false; manualVoiceRestart.value = false; clearFailure(); muted.value = false;
    if (retry) notice.value = 'Nové hlasové spojení vytváří nový modelový kontext. Obnovují se pouze archivované tahy.';
    cancelSpokenEnd(); voice.dispose();
    voiceContext = { sessionId: session.id, identity: identityKey(), epoch: generation, input: new SpokenEndInput() };
    voice = new LabVoice(voiceObserver(voiceContext));
    await voice.connect(() => client.ticket(session.id, retry), session.id, accountOrigin.value || window.location.origin);
  }
  async function end(): Promise<void> {
    const session = selected.value; if (!session || endingSession === session.id) return;
    endingSession = session.id; cancelSpokenEnd();
    const hadHardware = hardware.bound.value || hardware.active.value;
    const localStopSubmitted = hardware.stopLocal(); hardware.clear(false, false); hardware.uncertain.value = hadHardware; hardware.mode.value = hadHardware;
    cancelRecovery();
    try { voice.end(); } catch { voice.dispose(); }
    voiceContext = null; voicePhase.value = 'ended'; recoverable.value = false; conversationPending.value = false; clearImage();
    // Stop must not wait behind a slow model response. Cancelling only fences
    // the old request; the explicit end call lets the server retire its worker.
    if (busy.value) { generation++; listGeneration++; client.invalidate(); busy.value = loading.value = false; }
    try { await run(async () => { remember(await client.end(session.id)); notice.value = hadHardware
      ? `Backend potvrdil ukončení testovací vazby. ${localStopSubmitted ? 'Místní Stop byl předán původnímu spojení.' : 'Místní Stop nemohl potvrdit původní spojení.'} Fyzické zastavení ani poloha tím nejsou ověřené.`
      : 'Sezení je ukončené. Můžete uložit hodnocení.'; }); }
    finally { if (endingSession === session.id) endingSession = ''; }
  }
  function stopVoice(refresh = true): void {
    cancelSpokenEnd(); cancelRecovery(); voice.dispose(); voiceContext = null; voicePhase.value = 'idle'; muted.value = false; recoverable.value = false;
    notice.value = 'Hlas je vypnutý. V tomto sezení můžete pokračovat textem.';
    if (refresh) void refreshSelected();
  }
  function toggleMute(): void { muted.value = !muted.value; voice.setMuted(muted.value); }
  function clearImage(clearAttachment = true): void {
    imageGeneration++; imageController.abort(); imageController = new AbortController();
    if (image.value) URL.revokeObjectURL(image.value.previewUrl);
    image.value = null; imageConsent.value = false; imagePreparing.value = false; if (clearAttachment) imageAttachedSession.value = '';
  }
  async function chooseImage(file: File): Promise<void> {
    if (busy.value || voiceActive.value || recoveryPending.value || simulationBlocked.value || selected.value?.status !== 'open'
      || !catalog.value?.capabilities.imageRealtime || selected.value?.engine !== 'gpt-realtime-2.1') return;
    imageController.abort(); imageController = new AbortController();
    const epoch = ++imageGeneration; const sessionId = selected.value?.id; imagePreparing.value = true; imageConsent.value = false; clearFailure();
    try {
      const prepared = await prepareLabImage(file, imageController.signal);
      if (epoch !== imageGeneration || selected.value?.id !== sessionId) { URL.revokeObjectURL(prepared.previewUrl); return; }
      if (image.value) URL.revokeObjectURL(image.value.previewUrl); image.value = prepared;
    } catch (cause) { if (epoch === imageGeneration) error.value = message(cause, 'image_prepare_failed'); }
    finally { if (epoch === imageGeneration) imagePreparing.value = false; }
  }
  async function attachImage(): Promise<void> {
    const session = selected.value; const prepared = image.value;
    if (!providerConsent.value || !session || session.status !== 'open' || !prepared || !imageConsent.value || voiceActive.value || recoveryPending.value
      || simulationBlocked.value || !online.value || !catalog.value?.capabilities.imageRealtime || session.engine !== 'gpt-realtime-2.1') return;
    await run(async () => { remember(await client.attachImage(session.id, prepared.dataBase64)); imageAttachedSession.value = session.id;
      imageConsent.value = false; notice.value = 'Fotografie je dočasně připojená k testu. Model ji dostane při další skutečné odpovědi; na serveru se nearchivuje.'; });
  }
  function discardImage(): void {
    clearImage(false);
    notice.value = 'Místní náhled byl odstraněn.';
  }
  async function removeImage(): Promise<void> {
    const session = selected.value;
    if (!session) return;
    const attached = session.hasImage || imageAttachedSession.value === session.id;
    clearImage(false);
    if (!attached) { notice.value = 'Místní náhled byl odstraněn.'; return; }
    stopVoice(false);
    // Withdrawing an attached photo must preempt a response using that photo.
    if (busy.value) {
      generation++; listGeneration++; selectedReadGeneration++; client.invalidate(); busy.value = loading.value = false;
      conversationPending.value = false;
    }
    if (!online.value) {
      error.value = 'Místní náhled je odstraněný. Odebrání fotografie ze serveru zatím není potvrzené; zkuste to znovu po obnovení spojení.';
      return;
    }
    await run(async () => { const result = await client.removeImage(session.id);
      if (result.hasImage !== false) throw new Error('Server nepotvrdil odebrání fotografie. Zkuste odebrání znovu.');
      remember(result); imageAttachedSession.value = '';
      notice.value = 'Server potvrdil odebrání fotografie z dalšího modelového vstupu.'; });
  }
  async function saveEvaluation(): Promise<void> {
    const session = selected.value; if (!session || !validEvaluation.value) return;
    const evaluation: Evaluation = { scores: { ...scores.value } as Scores, comment: comment.value,
      turnComments: Object.entries(turnComments.value).filter(([, value]) => value.trim()).map(([turnId, value]) => ({ turnId, comment: value })) };
    await run(async () => { remember(await client.evaluate(session.id, evaluation)); analytics.value = await client.analytics(); notice.value = 'Hodnocení je uložené u přesných tahů tohoto sezení.'; });
  }
  async function runSuite(): Promise<void> {
    if (!providerConsent.value || !suiteScenarioIds.value.length || voiceActive.value || recoveryPending.value || hardware.active.value || !online.value || !engineReady.value) return;
    await run(async () => {
      const suite = await client.createSuite({ revisionId: revisionId.value, engine: engine.value,
        fixtureId: fixtureId.value, scenarioIds: [...suiteScenarioIds.value], label: label.value.trim() || 'Scénářová sada', recordContent: recordContent.value });
      suites.value = [suite, ...suites.value.filter(item => item.id !== suite.id)];
      notice.value = 'Scénářová sada byla spuštěna. Výsledky obnovíte tlačítkem Obnovit přehled.';
    });
  }
  async function openSuiteSession(id: string): Promise<boolean> {
    if (!id || busy.value || voiceActive.value || recoveryPending.value || hardware.active.value) return false;
    const epoch = generation; let opened: LabSession | null = null;
    await run(async () => {
      let result: LabSession;
      try { result = await client.session(id); }
      catch (cause) {
        if (cause instanceof LabError && cause.status === 404)
          throw new Error('Sezení z výsledku sady už není dostupné. Obnovte přehled; toto hodnocení nebylo otevřené.');
        throw cause;
      }
      if (result.id !== id || result.ownerId !== account.value?.id) throw new LabError('invalid_response', 502);
      remember(result); opened = result;
    });
    if (!opened || epoch !== generation || busy.value || voiceActive.value || recoveryPending.value || hardware.active.value) return false;
    selectSession(opened);
    return selected.value === opened;
  }
  function editRevision(revision: Revision): void {
    configRevisionId.value = revision.id; draftName.value = revision.name; draftInstructions.value = revision.instructions;
    draftVoice.value = revision.voice; draftEngine.value = revision.engine; configConfirmed.value = false;
  }
  async function saveDraft(): Promise<void> {
    if (!admin.value || !configRevision.value || !draftName.value.trim() || !draftInstructions.value.trim()) return;
    await run(async () => {
      const epoch = generation;
      const revision = await client.draft({ parentId: configRevision.value!.id, name: draftName.value.trim(),
        instructions: draftInstructions.value, engine: draftEngine.value, voice: draftVoice.value.trim() });
      await load(); if (epoch !== generation) return;
      if (catalog.value && !catalog.value.versions.some(item => item.id === revision.id))
        catalog.value = { ...catalog.value, versions: [revision, ...catalog.value.versions] };
      editRevision(revision); revisionId.value = revision.id; notice.value = 'Nový draft je uložený a připravený k testu.';
    });
  }
  async function publish(rollback = false): Promise<void> {
    const revision = configRevision.value;
    if (!admin.value || !revision || !configConfirmed.value || !catalog.value) return;
    const expected = catalog.value.activeRevisionId; configConfirmed.value = false;
    await run(async () => {
      const epoch = generation;
      if (rollback) await client.rollback(revision.id, expected); else await client.publish(revision.id, expected);
      await load(); if (epoch !== generation) return;
      notice.value = rollback ? 'Aktivní konfigurace byla vrácena na vybranou verzi.' : 'Verze byla publikována.';
    });
  }
  function downloadDiagnostics(): void {
    if (!selected.value) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(technicalDiagnostics(selected.value, hardware.uncertain.value && !selected.value.hardware), null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = `persona-lab-${selected.value.id}-diagnostika.json`; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
  async function loadSystemDiagnostics(): Promise<void> {
    if (!admin.value || diagnosticsLoading.value || disposed || !online.value) return;
    const epoch = generation; diagnosticsLoading.value = true; systemDiagnostics.value = null; clearFailure();
    try {
      const result = await client.systemDiagnostics();
      if (epoch === generation && admin.value && !disposed) systemDiagnostics.value = result;
    } catch (cause) {
      if (epoch === generation && !(cause instanceof DOMException && cause.name === 'AbortError')) error.value = message(cause);
    } finally { if (epoch === generation) diagnosticsLoading.value = false; }
  }
  function downloadSystemDiagnostics(): void {
    if (!admin.value || !systemDiagnostics.value) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(systemDiagnosticsBundle(systemDiagnostics.value), null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'persona-lab-systemova-diagnostika.json'; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
  function clear(): void {
    cancelSpokenEnd(); voiceContext = null; hardware.clear(); generation++; listGeneration++; selectedReadGeneration++; client.invalidate(); cancelRecovery(); voice.dispose(); clearImage(); conversationPending.value = false;
    catalog.value = null; sessions.value = []; selected.value = null; analytics.value = null; suites.value = []; suiteScenarioIds.value = []; busy.value = loading.value = false;
    systemDiagnostics.value = null; diagnosticsLoading.value = false;
    scores.value = {}; comment.value = ''; turnComments.value = {}; text.value = ''; partial.value = { user: '', assistant: '' };
    draftName.value = draftInstructions.value = draftVoice.value = ''; configRevisionId.value = revisionId.value = '';
    scenarioId.value = fixtureId.value = label.value = comparisonId.value = ''; configConfirmed.value = false;
    clearFailure(); notice.value = ''; voicePhase.value = 'idle'; recoverable.value = false; manualVoiceRestart.value = false; reconnects.value = 0; recordContent.value = false; providerConsent.value = false;
  }
  watch(identityKey, () => { clear(); void load(); }, { flush: 'sync' });
  watch(revisionId, id => { const revision = catalog.value?.versions.find(item => item.id === id); if (revision) engine.value = revision.engine; });
  watch(configRevisionId, () => { const revision = configRevision.value; if (revision) editRevision(revision); });
  function visibilityChanged(): void {
    if (document.visibilityState === 'hidden' && hardware.active.value) { void end(); return; }
    if (document.visibilityState === 'hidden' && (voiceActive.value || recoveryPending.value)) {
      stopVoice(false); notice.value = 'Hlas byl při přechodu na pozadí vypnutý. Po návratu jej spusťte ručně; nové spojení používá nový modelový kontext.';
    }
  }
  function networkChanged(): void {
    online.value = navigator.onLine;
    if (!online.value) {
      const hadHardware = hardware.active.value; hardware.clear(); hardware.uncertain.value = hadHardware; hardware.mode.value = hadHardware;
      generation++; listGeneration++; selectedReadGeneration++; client.invalidate(); stopVoice(false);
      busy.value = loading.value = conversationPending.value = diagnosticsLoading.value = false;
      failure.value = classifyLabFailure('client_offline'); error.value = describeLabFailure(failure.value);
    } else notice.value = 'Síť je znovu dostupná. Obnovte přehled. Hlas spustíte výslovně v novém modelovém kontextu.';
  }
  const globalStopParticipant = {
    canStop: () => !disposed && selected.value?.status === 'open'
      && (voiceActive.value || conversationPending.value || recoveryPending.value || hardware.active.value),
    busy: () => busy.value && !conversationPending.value,
    stop: () => end(),
    clear: () => { cancelSpokenEnd(); cancelRecovery(); voice.dispose(); voiceContext = null; hardware.clear(); },
  };
  onMounted(() => {
    registerDirectorStop(globalStopParticipant);
    document.addEventListener('visibilitychange', visibilityChanged); window.addEventListener('online', networkChanged); window.addEventListener('offline', networkChanged);
    ageTimer = setInterval(() => { now.value = Date.now(); }, 1000); void load();
  });
  onBeforeUnmount(() => {
    if (directorStop.value === globalStopParticipant) clearDirectorStop();
    document.removeEventListener('visibilitychange', visibilityChanged); window.removeEventListener('online', networkChanged); window.removeEventListener('offline', networkChanged);
    if (ageTimer) clearInterval(ageTimer); disposed = true; clear();
  });
  return { account, admin, catalog, sessions, selected, analytics, suites, suiteScenarioIds, runSuite, loading, busy, error, failure, notice, voicePhase, voiceActive,
    hardware, simulationBlocked, noticeTone,
    recoveryPending, online, conversationPending, sessionAgeSeconds, manualVoiceRestart,
    muted, recoverable, reconnects, partial, revisionId, engine, engineReady, selectedEngineReady, scenarioId, fixtureId, label, comparisonId, recordContent, providerConsent, text,
    scores, comment, turnComments, validEvaluation, image, imageConsent, imagePreparing, imageAttachedSession, chooseImage, attachImage, discardImage, removeImage,
    draftName, draftInstructions, draftVoice, draftEngine, configRevisionId,
    configRevision, configConfirmed, load, create, createVisualTest, canCreateVisualTest, selectSession, openSuiteSession, send, startVoice, stopVoice, end, toggleMute, interrupt: () => voice.interrupt(),
    saveEvaluation, saveDraft, publish, downloadDiagnostics, systemDiagnostics, diagnosticsLoading, loadSystemDiagnostics, downloadSystemDiagnostics };
}

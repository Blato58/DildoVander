import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { account, accountOrigin, hasRole } from '../identity/session';
import { PersonaLabClient, technicalDiagnostics } from './client';
import { LabVoice, type VoicePhase } from './voice';
import { directorStop, registerDirectorStop, clearDirectorStop } from '../director/directorStop';
import { prepareLabImage, type LabImage } from './image';
import { classifyLabFailure, describeLabFailure } from './errors';
import { metricLabels, type Analytics, type Catalog, type Evaluation, type LabEngine, type LabSession, type LabSuite, type Metric, type Revision, type Scores } from './contracts';

export function usePersonaLab() {
  const identityKey = () => account.value ? `${account.value.id}:${[...account.value.roles].sort().join(',')}` : '';
  const client = new PersonaLabClient(identityKey);
  const catalog = shallowRef<Catalog | null>(null);
  const sessions = shallowRef<LabSession[]>([]);
  const selected = shallowRef<LabSession | null>(null);
  const analytics = shallowRef<Analytics | null>(null);
  const suites = shallowRef<LabSuite[]>([]); const suiteScenarioIds = ref<string[]>([]);
  const loading = ref(false); const busy = ref(false); const error = ref(''); const notice = ref('');
  const voicePhase = ref<VoicePhase>('idle'); const muted = ref(false); const recoverable = ref(false); const reconnects = ref(0);
  const recoveryPending = ref(false); const online = ref(navigator.onLine); const conversationPending = ref(false); const now = ref(Date.now());
  const manualVoiceRestart = ref(false);
  const partial = ref({ user: '', assistant: '' });
  const revisionId = ref(''); const engine = ref<LabEngine>('gpt-live-1'); const scenarioId = ref(''); const fixtureId = ref('');
  const label = ref(''); const comparisonId = ref(''); const text = ref(''); const recordContent = ref(false);
  const scores = ref<Partial<Scores>>({}); const comment = ref(''); const turnComments = ref<Record<string, string>>({});
  const draftName = ref(''); const draftInstructions = ref(''); const draftVoice = ref(''); const draftEngine = ref<LabEngine>('gpt-live-1');
  const configRevisionId = ref(''); const configConfirmed = ref(false);
  const image = shallowRef<LabImage | null>(null); const imageConsent = ref(false); const imagePreparing = ref(false);
  const imageAttachedSession = ref(''); let imageGeneration = 0; let imageController = new AbortController();
  let generation = 0; let disposed = false; let listGeneration = 0; let selectedReadGeneration = 0;
  let ageTimer: ReturnType<typeof setInterval> | null = null;
  let recoveryDelay: { timer: ReturnType<typeof setTimeout>; finish: (value: boolean) => void } | null = null;
  const admin = computed(() => hasRole('Administrator'));
  const voiceActive = computed(() => ['connecting', 'listening', 'speaking'].includes(voicePhase.value));
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
    if (busy.value || voiceActive.value || recoveryPending.value || session.ownerId !== account.value?.id) return;
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
  const voice = new LabVoice({
    phase: phase => { voicePhase.value = phase; },
    transcript: (role, delta) => { partial.value = { ...partial.value, [role]: (partial.value[role] + delta).slice(-32000) }; },
    turn: turn => {
      if (!selected.value) return;
      const turns = selected.value.turns.filter(item => item.id !== turn.id);
      remember({ ...selected.value, turns: [...turns, turn] }); partial.value = { ...partial.value, [turn.role]: '' };
    },
    session: session => remember(session),
    interrupted: () => { partial.value = { ...partial.value, assistant: '' }; },
    error: (code, canRecover) => {
      const details = classifyLabFailure(code);
      error.value = describeLabFailure(details);
      recoverable.value = canRecover && details.recoverable && online.value && reconnects.value < 2;
      manualVoiceRestart.value = details.manualRestart;
    },
    closed: () => { muted.value = false; void refreshSelected(); },
  });
  function message(cause: unknown): string { return cause instanceof Error ? cause.message : 'Požadavek se nepodařil.'; }
  async function run(action: () => Promise<void>): Promise<void> {
    if (busy.value || disposed) return;
    const epoch = generation; selectedReadGeneration++; listGeneration++; loading.value = false;
    busy.value = true; error.value = ''; notice.value = '';
    try { await action(); }
    catch (cause) { if (epoch === generation && !(cause instanceof DOMException && cause.name === 'AbortError')) error.value = message(cause); }
    finally { if (epoch === generation) busy.value = false; }
  }
  async function load(): Promise<void> {
    if (!account.value || disposed) return;
    const epoch = generation; const request = ++listGeneration; loading.value = true; error.value = '';
    try {
      const [nextCatalog, nextSessions, nextAnalytics, nextSuites] = await Promise.all([client.catalog(), client.sessions(), client.analytics(), client.suites()]);
      if (epoch !== generation || request !== listGeneration) return;
      if (nextCatalog.capabilities.simulationOnly !== true) throw new Error('Server nepotvrdil oddělený simulační režim. Testování zůstává vypnuté.');
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
  async function create(): Promise<void> {
    if (voiceActive.value || recoveryPending.value || !online.value || !revisionId.value || !scenarioId.value || !fixtureId.value) return;
    await run(async () => {
      const session = await client.createSession({ revisionId: revisionId.value, engine: engine.value,
        scenarioId: scenarioId.value, fixtureId: fixtureId.value, label: label.value.trim() || 'Test persony', recordContent: recordContent.value,
        ...(comparisonId.value.trim() ? { comparisonId: comparisonId.value.trim() } : {}) });
      remember(session); selected.value = session; scores.value = {}; comment.value = ''; turnComments.value = {};
      clearImage();
      partial.value = { user: '', assistant: '' }; voicePhase.value = 'idle'; reconnects.value = 0;
      notice.value = 'Sezení je připravené. Model se spustí odesláním textu nebo spuštěním hlasu.';
    });
  }
  async function send(): Promise<void> {
    const session = selected.value; const input = text.value.trim();
    if (!session || session.status !== 'open' || !input || busy.value || voiceActive.value || recoveryPending.value || !online.value) return;
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
    if (!session || session.status !== 'open' || busy.value || voiceActive.value || recoveryPending.value || !online.value || !selectedEngineReady.value) return;
    if (retry && (!recoverable.value || reconnects.value >= 2)) return;
    if (retry) {
      reconnects.value++; recoveryPending.value = true; recoverable.value = false;
      notice.value = 'Připravuji nové spojení po krátké prodlevě. Nový modelový kontext obnoví jen archivované tahy; neuložený hlasový obsah se po odpojení neobnoví.';
      const epoch = generation;
      const proceed = await waitForRecovery(500 * 2 ** (reconnects.value - 1) + Math.floor(Math.random() * 250));
      if (epoch !== generation) return;
      recoveryPending.value = false;
      if (!proceed || selected.value?.id !== session.id || !online.value) return;
    }
    recoverable.value = false; manualVoiceRestart.value = false; error.value = ''; muted.value = false;
    if (retry) notice.value = 'Nové hlasové spojení vytváří nový modelový kontext. Obnovují se pouze archivované tahy.';
    await voice.connect(() => client.ticket(session.id, retry), session.id, accountOrigin.value || window.location.origin);
  }
  async function end(): Promise<void> {
    const session = selected.value; if (!session) return;
    cancelRecovery(); voice.end(); recoverable.value = false; conversationPending.value = false; clearImage();
    // Stop must not wait behind a slow model response. Cancelling only fences
    // the old request; the explicit end call lets the server retire its worker.
    if (busy.value) { generation++; listGeneration++; client.invalidate(); busy.value = loading.value = false; }
    await run(async () => { remember(await client.end(session.id)); notice.value = 'Sezení je ukončené. Můžete uložit hodnocení.'; });
  }
  function stopVoice(refresh = true): void {
    cancelRecovery(); voice.dispose(); voicePhase.value = 'idle'; muted.value = false; recoverable.value = false;
    notice.value = 'Hlas je vypnutý. V tomto sezení můžete pokračovat textem.';
    if (refresh) void refreshSelected();
  }
  function toggleMute(): void { muted.value = !muted.value; voice.setMuted(muted.value); }
  function clearImage(): void {
    imageGeneration++; imageController.abort(); imageController = new AbortController();
    if (image.value) URL.revokeObjectURL(image.value.previewUrl);
    image.value = null; imageConsent.value = false; imagePreparing.value = false; imageAttachedSession.value = '';
  }
  async function chooseImage(file: File): Promise<void> {
    if (busy.value || voiceActive.value || recoveryPending.value || selected.value?.engine !== 'gpt-realtime-2.1') return;
    imageController.abort(); imageController = new AbortController();
    const epoch = ++imageGeneration; const sessionId = selected.value?.id; imagePreparing.value = true; imageConsent.value = false; error.value = '';
    try {
      const prepared = await prepareLabImage(file, imageController.signal);
      if (epoch !== imageGeneration || selected.value?.id !== sessionId) { URL.revokeObjectURL(prepared.previewUrl); return; }
      if (image.value) URL.revokeObjectURL(image.value.previewUrl); image.value = prepared;
    } catch (cause) { if (epoch === imageGeneration) error.value = message(cause); }
    finally { if (epoch === imageGeneration) imagePreparing.value = false; }
  }
  async function attachImage(): Promise<void> {
    const session = selected.value; const prepared = image.value;
    if (!session || !prepared || !imageConsent.value || voiceActive.value || recoveryPending.value || session.engine !== 'gpt-realtime-2.1') return;
    await run(async () => { remember(await client.attachImage(session.id, prepared.dataBase64)); imageAttachedSession.value = session.id;
      imageConsent.value = false; notice.value = 'Fotografie je dočasně připojená k testu. Model ji dostane při další skutečné odpovědi; na serveru se nearchivuje.'; });
  }
  async function removeImage(): Promise<void> {
    const session = selected.value; if (!session || busy.value) return;
    if (voiceActive.value) stopVoice(false);
    await run(async () => { remember(await client.removeImage(session.id)); clearImage(); notice.value = 'Fotografie byla odebrána z dalšího modelového vstupu.'; });
  }
  async function saveEvaluation(): Promise<void> {
    const session = selected.value; if (!session || !validEvaluation.value) return;
    const evaluation: Evaluation = { scores: { ...scores.value } as Scores, comment: comment.value,
      turnComments: Object.entries(turnComments.value).filter(([, value]) => value.trim()).map(([turnId, value]) => ({ turnId, comment: value })) };
    await run(async () => { remember(await client.evaluate(session.id, evaluation)); analytics.value = await client.analytics(); notice.value = 'Hodnocení je uložené u přesných tahů tohoto sezení.'; });
  }
  async function runSuite(): Promise<void> {
    if (!suiteScenarioIds.value.length || voiceActive.value || recoveryPending.value || !online.value || !engineReady.value) return;
    await run(async () => {
      const suite = await client.createSuite({ revisionId: revisionId.value, engine: engine.value,
        fixtureId: fixtureId.value, scenarioIds: [...suiteScenarioIds.value], label: label.value.trim() || 'Scénářová sada', recordContent: recordContent.value });
      suites.value = [suite, ...suites.value.filter(item => item.id !== suite.id)];
      notice.value = 'Scénářová sada byla spuštěna. Výsledky obnovíte tlačítkem Obnovit přehled.';
    });
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
    const url = URL.createObjectURL(new Blob([JSON.stringify(technicalDiagnostics(selected.value), null, 2)], { type: 'application/json' }));
    const anchor = document.createElement('a'); anchor.href = url; anchor.download = `persona-lab-${selected.value.id}-diagnostika.json`; anchor.click();
    setTimeout(() => URL.revokeObjectURL(url), 0);
  }
  function clear(): void {
    generation++; listGeneration++; selectedReadGeneration++; client.invalidate(); cancelRecovery(); voice.dispose(); clearImage(); conversationPending.value = false;
    catalog.value = null; sessions.value = []; selected.value = null; analytics.value = null; suites.value = []; suiteScenarioIds.value = []; busy.value = loading.value = false;
    scores.value = {}; comment.value = ''; turnComments.value = {}; text.value = ''; partial.value = { user: '', assistant: '' };
    draftName.value = draftInstructions.value = draftVoice.value = ''; configRevisionId.value = revisionId.value = '';
    scenarioId.value = fixtureId.value = label.value = comparisonId.value = ''; configConfirmed.value = false;
    error.value = notice.value = ''; voicePhase.value = 'idle'; recoverable.value = false; manualVoiceRestart.value = false; reconnects.value = 0; recordContent.value = false;
  }
  watch(identityKey, () => { clear(); void load(); }, { flush: 'sync' });
  watch(revisionId, id => { const revision = catalog.value?.versions.find(item => item.id === id); if (revision) engine.value = revision.engine; });
  watch(configRevisionId, () => { const revision = configRevision.value; if (revision) editRevision(revision); });
  function visibilityChanged(): void {
    if (document.visibilityState === 'hidden' && (voiceActive.value || recoveryPending.value)) {
      stopVoice(false); notice.value = 'Hlas byl při přechodu na pozadí vypnutý. Po návratu jej spusťte ručně; nové spojení používá nový modelový kontext.';
    }
  }
  function networkChanged(): void {
    online.value = navigator.onLine;
    if (!online.value) {
      generation++; listGeneration++; selectedReadGeneration++; client.invalidate(); stopVoice(false);
      busy.value = loading.value = conversationPending.value = false;
      error.value = 'Připojení k síti není dostupné. Audio je vypnuté; výsledek rozpracovaného požadavku není potvrzený. Po návratu sítě obnovte přehled.';
    } else notice.value = 'Síť je znovu dostupná. Obnovte přehled. Hlas spustíte výslovně v novém modelovém kontextu.';
  }
  const globalStopParticipant = {
    canStop: () => !disposed && selected.value?.status === 'open'
      && (voiceActive.value || conversationPending.value || recoveryPending.value),
    busy: () => busy.value && !conversationPending.value,
    stop: () => end(),
    clear: () => { cancelRecovery(); voice.dispose(); },
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
  return { account, admin, catalog, sessions, selected, analytics, suites, suiteScenarioIds, runSuite, loading, busy, error, notice, voicePhase, voiceActive,
    recoveryPending, online, conversationPending, sessionAgeSeconds, manualVoiceRestart,
    muted, recoverable, reconnects, partial, revisionId, engine, engineReady, selectedEngineReady, scenarioId, fixtureId, label, comparisonId, recordContent, text,
    scores, comment, turnComments, validEvaluation, image, imageConsent, imagePreparing, imageAttachedSession, chooseImage, attachImage, removeImage,
    draftName, draftInstructions, draftVoice, draftEngine, configRevisionId,
    configRevision, configConfirmed, load, create, selectSession, send, startVoice, stopVoice, end, toggleMute, interrupt: () => voice.interrupt(),
    saveEvaluation, saveDraft, publish, downloadDiagnostics };
}

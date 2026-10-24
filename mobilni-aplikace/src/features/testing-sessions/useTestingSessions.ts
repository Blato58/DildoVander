import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { account, accountOrigin } from '../identity/session';
import { PersonaLabClient } from '../persona-lab/client';
import { LabVoice, type VoicePhase } from '../persona-lab/voice';
import type { LabEngine, LabSession } from '../persona-lab/contracts';
import { TestingSessionsClient } from './client';
import type { CharacterInput, InputMode, TestCharacter, TestSession, TestSessionDetail } from './contracts';

export function useTestingSessions() {
  const route = useRoute(); const router = useRouter();
  const identity = () => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : '';
  const client = new TestingSessionsClient(identity); const lab = new PersonaLabClient(identity);
  const sessions = shallowRef<TestSession[]>([]); const characters = shallowRef<TestCharacter[]>([]);
  const selected = shallowRef<TestSessionDetail | null>(null);
  const error = ref(''); const notice = ref(''); const busy = ref(false); const loading = ref(false); const stale = ref(false);
  const engine = ref<LabEngine>('gpt-live-1'); const inputMode = ref<InputMode>('text'); const consent = ref(false);
  const text = ref(''); const outputEnabled = ref(false); const captureEnabled = ref(false); const voicePhase = ref<VoicePhase>('idle');
  const partial = ref({ user: '', assistant: '' }); const spokenBusy = ref(false); const providerConfirmed = ref(false);
  const permitted = computed(() => account.value?.roles.includes('Tester') === true);
  const voiceActive = computed(() => ['connecting', 'waiting', 'listening', 'speaking'].includes(voicePhase.value));
  const open = computed(() => selected.value?.session.status === 'open');
  let epoch = 0; let readEpoch = 0; let disposed = false; let refreshing = false;
  let operationId: string | null = null; let pendingSpokenText = ''; let retiringSessionId:string|null=null;
  let voice = makeVoice();
  function makeVoice(): LabVoice {
    const currentEpoch = epoch; const currentId = selected.value?.session.id;
    const current = () => !disposed && epoch === currentEpoch && selected.value?.session.id === currentId;
    return new LabVoice({
      phase: value => { if (current()) { voicePhase.value = value; if(value === 'listening') { if(captureEnabled.value)providerConfirmed.value=true; if(pendingSpokenText) { if(voice.sendText(pendingSpokenText)) { pendingSpokenText = ''; text.value = ''; } } else if(!captureEnabled.value)spokenBusy.value=false; } } },
      transcript: (role, delta) => { if (current()) partial.value = { ...partial.value, [role]: (partial.value[role] + delta).slice(-32000) }; },
      turn: turn => {
        if (!current() || !selected.value) return;
        updateConversation({ ...selected.value.conversation, turns: [...selected.value.conversation.turns.filter(item => item.id !== turn.id), turn] });
        partial.value = { ...partial.value, [turn.role]: '' };
      },
      session: value => { if (current()) { updateConversation(value); if(!captureEnabled.value)providerConfirmed.value=false; } },
      providerReady: () => { if (current()) providerConfirmed.value = true; },
      interrupted: () => { if (current()) partial.value = { ...partial.value, assistant: '' }; },
      error: code => { if (current()) {error.value = code; spokenBusy.value = false; } },
      closed: () => { if (current()) {spokenBusy.value = false; void refresh();} },
    });
  }
  function updateConversation(conversation: LabSession): void {
    if (selected.value?.conversation.id === conversation.id) selected.value = { ...selected.value, conversation };
  }
  function stopVoice(): void { if(voiceActive.value && selected.value)retiringSessionId=selected.value.session.id; pendingSpokenText = ''; spokenBusy.value = false; providerConfirmed.value = false; voice.dispose(); captureEnabled.value = false; voicePhase.value = 'idle'; partial.value = { user: '', assistant: '' }; }
  function invalidate(): void {
    epoch++; readEpoch++; client.invalidate(); lab.invalidate(); stopVoice();
    selected.value = null; sessions.value = []; characters.value = []; error.value = ''; notice.value = '';
    text.value = ''; busy.value = false; loading.value = false; operationId = null; retiringSessionId=null; consent.value = false;
  }
  function message(cause: unknown): string {
    if (cause && typeof cause === 'object' && 'code' in cause && typeof cause.code === 'string') return cause.code;
    return cause instanceof Error ? cause.message : 'testing_request_failed';
  }
  async function run(action: () => Promise<void>): Promise<void> {
    if (busy.value || !permitted.value) return;
    const current = epoch; busy.value = true; error.value = ''; notice.value = '';
    try { await action(); }
    catch (cause) { if (current === epoch && !(cause instanceof DOMException && cause.name === 'AbortError')) error.value = message(cause); }
    finally { if (current === epoch) busy.value = false; }
  }
  async function refresh(): Promise<void> {
    if (!permitted.value || disposed || refreshing) return;
    const current = epoch; const request = ++readEpoch; const id = selected.value?.session.id;
    refreshing = true;
    try {
      const [list, people, detail] = await Promise.all([client.sessions(), client.characters(), id ? client.session(id) : Promise.resolve(null)]);
      if (epoch !== current || request !== readEpoch) return;
      sessions.value = list; characters.value = people; stale.value = false;
      if (detail && selected.value?.session.id === id) selected.value = detail;
    } catch (cause) {
      if (epoch === current && request === readEpoch && !(cause instanceof DOMException && cause.name === 'AbortError')) { stale.value = true; error.value = message(cause); }
    } finally { refreshing = false; }
  }
  async function resume(id: string): Promise<void> {
    await run(async () => {
      client.invalidate(); stopVoice(); const current = epoch; const request = ++readEpoch; const detail = await client.session(id);
      if (current !== epoch || request !== readEpoch) return;
      selected.value = detail; inputMode.value = detail.session.inputMode; engine.value = detail.session.engine;
      text.value = ''; stale.value = false;
      await router.replace({ path: '/testing-sessions', query: { session: id } });
    });
  }
  async function start(): Promise<void> {
    if (!consent.value) { error.value = 'Potvrďte souhlas se skutečným zpracováním OpenAI a uložením testu.'; return; }
    await run(async () => {
      operationId ??= crypto.randomUUID();
      const session = await client.create(engine.value, inputMode.value, consent.value, operationId);
      operationId = null; stopVoice(); selected.value = await client.session(session.id);
      await router.replace({ path: '/testing-sessions', query: { session: session.id } });
      await refresh();
    });
  }
  async function saveCharacter(userInput: CharacterInput, character?: TestCharacter, longTermConsent = false): Promise<void> {
    if (!consent.value) { error.value = 'Pro odeslání postavy OpenAI potvrďte souhlas.'; return; }
    await run(async () => {
      const saved = character ? await client.editCharacter(character.id, userInput, character.profile.revision, true, longTermConsent)
        : await client.createCharacter(userInput, true, longTermConsent);
      const started=(selected.value?.conversation.turns.length??0)>0;
      if (selected.value && open.value && !started) selected.value = await client.select(selected.value.session.id, saved.id, selected.value.session.revision);
      await refresh(); notice.value = started ? 'Postava uložena. Probíhající rozhovor zachovává původní kontext; novou verzi použijte v novém testovacím sezení.' : 'Postava uložena. AI dohledání se zpracuje na pozadí.';
    });
  }
  async function selectCharacter(characterId: string): Promise<void> {
    if (!selected.value) return;
    await run(async () => {
      stopVoice(); selected.value = await client.select(selected.value!.session.id, characterId, selected.value!.session.revision);
      await refresh();
    });
  }
  async function enrich(): Promise<void> {
    if (!selected.value?.character || !consent.value) return;
    await run(async () => {
      const id=selected.value!.session.id;const characterId=selected.value!.character!.id;
      await client.enrich(characterId);
      if(selected.value!.conversation.turns.length===0){selected.value=await client.select(id,characterId,selected.value!.session.revision);}
      else notice.value='AI obohacení je uložené pro nové testovací sezení. Probíhající rozhovor zachovává původní kontext.';
      await refresh();
    });
  }
  async function awaitRetired(id:string):Promise<void> {
    const initialEpoch=epoch;const deadline=Date.now()+10000;
    for(;;){
      if(epoch!==initialEpoch || selected.value?.session.id!==id)throw new DOMException('Testing session changed.','AbortError');
      const detail=await client.session(id);
      if(epoch!==initialEpoch || selected.value?.session.id!==id)throw new DOMException('Testing session changed.','AbortError');
      if(detail.conversationActive!==true){selected.value=detail;if(retiringSessionId===id)retiringSessionId=null;return;}
      if(Date.now()>=deadline)throw new Error('Předchozí poskytovatelské spojení se ještě uzavírá. Obnovte stav a zkuste zprávu znovu.');
      await new Promise<void>(resolve=>setTimeout(resolve,200));
    }
  }
  async function send(): Promise<void> {
    if (!selected.value || !text.value.trim() || !open.value || !consent.value || spokenBusy.value || busy.value || stale.value) return;
    if (!voiceActive.value && selected.value.conversationActive) {
      error.value = 'Předchozí požadavek ještě probíhá. Vyčkejte a obnovte stav.';
      return;
    }
    const input = text.value.trim();
    if (outputEnabled.value) {
      if (voiceActive.value && !captureEnabled.value) {
        if (voice.sendText(input)) {text.value = '';spokenBusy.value = true;} else error.value = 'Výstupní spojení zatím není připraveno.';
      } else await startSpokenText(input);
      return;
    }
    const wasConnected=voiceActive.value || retiringSessionId===selected.value?.session.id;
    if(wasConnected)stopVoice();
    await run(async () => {
      if(wasConnected)await awaitRetired(selected.value!.session.id);
      const id = selected.value!.conversation.id;
      try {
        updateConversation(await lab.text(id, input)); text.value = ''; await refresh();
      } catch (cause) {
        // A gateway/provider failure may follow a saved turn. Read server truth without replaying it.
        await refresh();
        throw cause;
      }
    });
  }
  async function startVoice(): Promise<void> {
    if (!selected.value || !open.value || !consent.value || inputMode.value === 'text' || busy.value) return;
    const hadConnection=voiceActive.value || retiringSessionId===selected.value.session.id;stopVoice();
    if(hadConnection){try{await awaitRetired(selected.value.session.id);}catch(cause){error.value=message(cause);return;}}
    captureEnabled.value = true; voice = makeVoice(); voice.setOutputEnabled(outputEnabled.value); error.value = '';
    const id = selected.value.conversation.id;
    await voice.connect(() => lab.ticket(id, voicePhase.value === 'error'), id, accountOrigin.value || window.location.origin);
  }
  async function startSpokenText(input = ''): Promise<void> {
    if (!selected.value || !open.value || !consent.value || busy.value) return;
    const hadConnection=voiceActive.value || retiringSessionId===selected.value.session.id;stopVoice();
    if(hadConnection){try{await awaitRetired(selected.value.session.id);}catch(cause){error.value=message(cause);return;}}
    pendingSpokenText = input; spokenBusy.value = input.length > 0; captureEnabled.value = false; voice = makeVoice();
    voice.setOutputEnabled(true); error.value = '';
    const id = selected.value.conversation.id;
    await voice.connect(() => lab.ticket(id,false,true), id, accountOrigin.value || window.location.origin, {capture:false});
  }
  function changeMode(mode: InputMode): void {
    const wasConnected=voiceActive.value || retiringSessionId===selected.value?.session.id;
    if (wasConnected) stopVoice();
    if (!selected.value || !open.value) { inputMode.value = mode; return; }
    void run(async () => { if(wasConnected)await awaitRetired(selected.value!.session.id); await client.mode(selected.value!.session.id, mode, selected.value!.session.revision); selected.value = await client.session(selected.value!.session.id); inputMode.value = selected.value.session.inputMode; });
  }
  async function selectWand(): Promise<void> {
    if (!selected.value || !consent.value) return;
    await run(async () => { selected.value = await client.selectWand(selected.value!.session.id); });
  }
  async function generate(): Promise<void> {
    if (!selected.value || !consent.value) return;
    await run(async () => { selected.value = await client.generate(selected.value!.session.id); });
  }
  async function fault(value: string): Promise<void> {
    if (!selected.value) return;
    await run(async () => { selected.value = await client.fault(selected.value!.session.id, value); });
  }
  async function end(): Promise<void> {
    if (!selected.value) return;
    const id = selected.value.session.id; stopVoice(); client.invalidate(); lab.invalidate();
    await run(async () => { await client.end(id); selected.value = await client.session(id); await refresh(); });
  }
  async function cancel(): Promise<void> {
    if (!selected.value) return;
    const id = selected.value.session.id; stopVoice(); lab.invalidate(); client.invalidate(); error.value = '';
    try { await client.cancel(id); await refresh(); }
    catch (cause) { if (!(cause instanceof DOMException && cause.name === 'AbortError')) error.value = message(cause); }
  }
  async function remove(): Promise<void> {
    if (!selected.value) return;
    const id = selected.value.session.id; stopVoice();
    await run(async () => {
      await client.delete(id); selected.value = null; text.value = ''; await router.replace('/testing-sessions'); await refresh();
    });
  }
  async function load(): Promise<void> {
    if (!permitted.value) return;
    loading.value = true; await refresh(); loading.value = false;
    const id = route.query.session;
    if (typeof id === 'string') await resume(id);
  }
  watch(identity, () => { invalidate(); void load(); }, { immediate: true });
  watch(outputEnabled, value => voice.setOutputEnabled(value));
  const refreshTimer = setInterval(() => { if (!document.hidden && !busy.value) void refresh(); }, 3000);
  const hidden = () => { if (document.hidden) stopVoice(); else void refresh(); };
  const online = () => void refresh();
  document.addEventListener('visibilitychange', hidden); window.addEventListener('online', online);
  onBeforeUnmount(() => { disposed = true; invalidate(); clearInterval(refreshTimer); document.removeEventListener('visibilitychange', hidden); window.removeEventListener('online', online); });
  return { account, permitted, sessions, characters, selected, error, notice, busy, loading, stale, engine, inputMode, consent,
    text, outputEnabled, captureEnabled, spokenBusy, providerConfirmed, voicePhase, voiceActive, partial, open, client, run, refresh, resume, start, saveCharacter, selectCharacter,
    enrich, send, selectWand, generate, fault, startVoice, startSpokenText, stopVoice, changeMode, end, cancel, remove, interrupt: () => voice.interrupt() };
}

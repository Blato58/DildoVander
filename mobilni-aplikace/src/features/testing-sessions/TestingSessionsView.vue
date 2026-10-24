<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import AwStatus from '../../components/AwStatus.vue';
import GeneratedWand from '../archive/GeneratedWand.vue';
import TestingKeepsakes from './TestingKeepsakes.vue';
import TestingDuels from './TestingDuels.vue';
import VirtualSuitcasePanel from './VirtualSuitcasePanel.vue';
import { useTestingSessions } from './useTestingSessions';
import { technicalDiagnostics } from '../persona-lab/client';
import { classifyLabFailure, describeLabFailure } from '../persona-lab/errors';
import { prepareLabImage } from '../persona-lab/image';
import type { CharacterInput, InputMode } from './contracts';
const test = useTestingSessions();
const { account, permitted, sessions, characters, selected, error, notice, busy, loading, stale, engine, inputMode, consent,
  text, outputEnabled, captureEnabled, spokenBusy, providerConfirmed, voicePhase, voiceActive, partial, open } = test;
const characterDeleteConfirmed = ref(false);
const longTermConsent = ref(false); const photoConsent = ref(false); const photoFile = ref<File | null>(null); const photoKeepsake = ref('');
const diagnosticUrl = ref('');
const showCharacterForm = ref(false); const editingCharacter = ref(false); const deleteConfirmed = ref(false);
const draft = reactive<CharacterInput>({ name: '', world: '', version: '', description: '', language: 'cs' });
const selectedCharacter = computed(() => {
  const detail = selected.value;
  if (detail && open.value && !detail.conversation.turns.length) {
    return characters.value.find(character => character.id === detail.session.characterId) ?? detail.character;
  }
  return detail?.character;
});
type TestStep = 1 | 2 | 3 | 4;
const step = ref<TestStep>(1);
const steps = ['Sezení', 'Postava', 'Rozhovor', 'Výsledek'];
const characterPending = computed(() => ['queued', 'running'].includes(selectedCharacter.value?.profile.status ?? ''));
const errorMessage = computed(() => {
  if (/^lab_|^transport_error$/.test(error.value)) return describeLabFailure(classifyLabFailure(error.value, 0));
  if (/timeout|unavailable|testing_request_failed|Failed to fetch|NetworkError/i.test(error.value)) {
    return 'Požadavek se nepodařilo potvrdit. Obnovte stav a zkontrolujte, zda se operace dokončila.';
  }
  if (/active|busy|in_progress/i.test(error.value)) return 'Předchozí požadavek ještě probíhá. Vyčkejte a obnovte stav.';
  return error.value;
});
async function goToStep(value: TestStep): Promise<void> {
  const sessionId = selected.value?.session.id;
  if (value >= 3 && step.value < 3 && open.value && selectedCharacter.value && !selected.value?.conversation.turns.length) {
    if (characterPending.value) return;
    await test.selectCharacter(selectedCharacter.value.id);
    if (error.value || selected.value?.session.id !== sessionId) return;
  }
  step.value = value;
  void nextTick(() => document.getElementById('testing-step-title')?.focus());
}
const providerEvents = computed(() => selected.value?.conversation.events.filter(event => event.type.startsWith('provider_') || event.type.startsWith('connection_')) ?? []);
const providerState = computed(() => {
  const events = providerEvents.value;
  const failed = [...events].reverse().find(event => ['provider_failure', 'connection_interrupted'].includes(event.type));
  const connected = [...events].reverse().find(event => event.type === 'provider_session' && event.code === 'ready');
  if (failed && (!connected || Date.parse(failed.createdAt) >= Date.parse(connected.createdAt))) return `Chyba: ${failed.code ?? failed.type}`;
  if(voiceActive.value && !captureEnabled.value){
    if(providerConfirmed.value)return 'Potvrzený poskytovatel pro aktuální psanou zprávu';
    return spokenBusy.value ? 'Psaný požadavek / přehrávání — viz potvrzené události' : 'Audio transport připravený; OpenAI se připojí po zprávě';
  }
  if(voiceActive.value && providerConfirmed.value)return 'Potvrzené živé spojení';
  if (voicePhase.value === 'connecting') return 'Navazování — čeká na potvrzení poskytovatele';
  if (connected) return voiceActive.value ? 'Potvrzené živé spojení' : 'Provedeno — viz potvrzené požadavky';
  return 'Zatím bez potvrzeného spojení';
});
function beginCharacter(edit = false): void {
  editingCharacter.value = edit; longTermConsent.value = edit ? selectedCharacter.value?.longTermConsent === true : false;
  Object.assign(draft, edit && selectedCharacter.value ? selectedCharacter.value.profile.userInput : { name: '', world: '', version: '', description: '', language: 'cs' });
  showCharacterForm.value = true;
}
async function saveCharacter(): Promise<void> {
  await test.saveCharacter({ ...draft }, editingCharacter.value ? selectedCharacter.value ?? undefined : undefined, longTermConsent.value);
  if (!error.value) showCharacterForm.value = false;
}
function switchMode(event: Event): void { test.changeMode((event.target as HTMLSelectElement).value as InputMode); }
function selectCharacter(event: Event): void { const id = (event.target as HTMLSelectElement).value; if (id) void test.selectCharacter(id); }
watch(() => selected.value?.session.id, () => { photoFile.value = null; photoConsent.value = false; photoKeepsake.value = ''; if(diagnosticUrl.value)URL.revokeObjectURL(diagnosticUrl.value); diagnosticUrl.value=''; showCharacterForm.value = false; deleteConfirmed.value = false; });
watch(() => selected.value?.session.id, () => {
  step.value = selected.value ? (open.value && !selected.value.conversation.turns.length ? 2 : 3) : 1;
  if (selected.value && !selected.value.character) beginCharacter();
});
async function portrait(): Promise<void> {
  if(!selected.value || !photoFile.value || !photoConsent.value || !consent.value)return;
  await test.run(async()=>{
    const sessionId=selected.value!.session.id; const file=photoFile.value!; const keepsake=selected.value!.keepsakes.find(item=>item.id===photoKeepsake.value) ?? selected.value!.keepsakes[0];
    if(!keepsake?.editions[0])throw new Error('Nejprve dokončete skutečnou generaci příběhu.');
    const image=await prepareLabImage(file);
    try{const uploaded=await test.client.photo(sessionId,keepsake.id,image.dataBase64);await test.client.portrait(sessionId,uploaded.id,keepsake.editions[0].id);await test.refresh();photoFile.value=null;}finally{URL.revokeObjectURL(image.previewUrl);}
  });
}
async function deleteCharacter():Promise<void>{if(!selectedCharacter.value||!characterDeleteConfirmed.value)return;await test.run(async()=>{await test.client.deleteCharacter(selectedCharacter.value!.id);await test.refresh();characterDeleteConfirmed.value=false;});}
function choosePhoto(event:Event):void{photoFile.value=(event.target as HTMLInputElement).files?.[0]??null;}
function exportDiagnostics():void {
  if(!selected.value)return; if(diagnosticUrl.value)URL.revokeObjectURL(diagnosticUrl.value);
  const payload={schemaVersion:1,testSessionId:selected.value.session.id,scope:'isolated_testing',provider:technicalDiagnostics(selected.value.conversation),virtualHardware:selected.value.hardware.events?.map(({type,code,createdAt,compartment})=>({type,code,createdAt,compartment,source:'simulation'}))};
  diagnosticUrl.value=URL.createObjectURL(new Blob([JSON.stringify(payload,null,2)],{type:'application/json'}));
}
onBeforeUnmount(()=>{if(diagnosticUrl.value)URL.revokeObjectURL(diagnosticUrl.value);});
function localTime(value: string): string { return new Date(value).toLocaleString(); }
</script>

<template>
  <div class="testing-sessions">
    <header class="testing-header">
      <div><h1>Testing Sessions</h1><p>Celý příběh se skutečným backendem a OpenAI. Fyzický svět nahrazuje virtuální kufr.</p></div>
      <AwStatus tone="warning">Testovací prostředí</AwStatus>
    </header>
    <AwNotice v-if="!permitted" tone="warning">Tato oblast vyžaduje účet Tester. Testovací pohled neuděluje práva k produkčním zařízením ani soukromým datům.</AwNotice>
    <template v-else>
      <nav class="testing-steps" aria-label="Kroky testu">
        <button v-for="(label, index) in steps" :key="label" type="button" :aria-current="step === index + 1 ? 'step' : undefined"
          :disabled="busy || (index > 0 && !selected) || (index > 1 && (!selectedCharacter || characterPending))" @click="goToStep((index + 1) as TestStep)">
          <span>{{ index + 1 }}</span>{{ label }}
        </button>
      </nav>
      <h2 id="testing-step-title" tabindex="-1">{{ step }}. {{ steps[step - 1] }}</h2>
      <AwNotice v-if="error && step !== 3" tone="danger" title="Operace nebyla potvrzena"><p>{{ errorMessage }}</p><small v-if="errorMessage !== error">{{ error }}</small> <AwButton :disabled="busy" @click="test.refresh">Obnovit stav</AwButton></AwNotice>
      <AwNotice v-if="notice" tone="info">{{ notice }}</AwNotice>
      <AwNotice v-if="stale" tone="warning">Stav je zastaralý. Obnovení spojení nevytváří nový rozhovor ani neopakuje příkazy.</AwNotice>
      <section v-show="step === 1" class="testing-setup" aria-labelledby="testing-start-title">
        <div><h2 id="testing-start-title">{{ selected ? 'Aktivní test a nové sezení' : 'Začněte bez hardwaru' }}</h2>
          <p class="aw-muted">Přihlášeni jako {{ account?.displayName }}. Testovací postavy, rozhovory a výsledky zůstávají oddělené od produkce.</p>
        </div>
        <div class="testing-start-controls">
          <div class="testing-new-session">
          <AwField id="testing-model" label="AI model"><select id="testing-model" v-model="engine" class="aw-select" :disabled="busy || voiceActive">
            <option value="gpt-live-1">GPT Live · gpt-live-1</option><option value="gpt-realtime-2.1">Realtime 2.1 · gpt-realtime-2.1</option>
          </select></AwField>
          </div>
          <AwField id="testing-input" label="Vstup"><select id="testing-input" :value="inputMode" class="aw-select" :disabled="busy" @change="switchMode">
            <option value="text">Text — bez mikrofonu</option><option value="voice">Hlas</option><option value="hybrid">Hybrid — text a hlas</option>
          </select></AwField>

        </div>
        <label class="testing-consent"><input v-model="consent" type="checkbox" /> Souhlasím s odesláním testovací postavy, zpráv a zvoleného audia či fotografie OpenAI a s uložením historie. Skutečné požadavky čerpají placené API. Osobní produkční data do testu nevkládám.</label>
        <AwButton variant="primary" :busy="busy" :disabled="!consent || voiceActive" @click="test.start">Založit test a vybrat postavu</AwButton>
      </section>
      <label v-if="step !== 1 && !consent" class="testing-consent"><input v-model="consent" type="checkbox" /> Souhlasím s odesláním testovacích dat OpenAI a uložením historie. Požadavky čerpají placené API.</label>
      <p v-if="loading" role="status">Načítání testovacích sezení…</p>
      <div class="testing-workspace">
        <aside v-show="step === 1" class="testing-session-list" aria-labelledby="testing-history-title">
          <details :open="!selected"><summary id="testing-history-title">Vaše sezení · {{ sessions.length }}</summary>
          <p v-if="!sessions.length && !loading" class="aw-muted">Zatím žádné testy. Nové sezení připraví virtuální kufr automaticky.</p>
          <ul><li v-for="session in sessions" :key="session.id">
            <button type="button" :class="['testing-session-button', { 'is-current': selected?.session.id === session.id }]" :disabled="busy || voiceActive" :aria-current="selected?.session.id === session.id ? 'true' : undefined" @click="test.resume(session.id)">
              <strong>{{ session.status === 'open' ? 'Pokračovat v testu' : 'Prohlédnout dokončený test' }}</strong><span>{{ localTime(session.createdAt) }}</span><small>{{ session.engine }} · {{ session.status }}</small>
            </button>
          </li></ul>
          </details>
          <RouterLink v-if="selected" :to="{ path: '/persona-lab', query: { testingSession: selected.session.id } }">Otevřít Persona Lab</RouterLink>
          <p v-if="selected" class="aw-muted">Sezení zůstane uložené. Návrat přes tento seznam obnoví jeho kontext.</p>
        </aside>
        <div v-if="selected" v-show="step !== 1" class="testing-main">
          <section v-show="step === 2" class="testing-character" aria-labelledby="testing-character-title">
            <div class="testing-section-heading"><h2 id="testing-character-title">{{ selectedCharacter?.profile.userInput.name ?? 'Vyberte nebo vytvořte postavu' }}</h2>
              <AwStatus v-if="selectedCharacter">{{ selectedCharacter.profile.status }}</AwStatus>
            </div>
            <div v-if="open" class="testing-actions">
              <AwField id="testing-character-choice" label="Testovací postava"><select id="testing-character-choice" :value="selected.session.characterId ?? ''" class="aw-select" :disabled="busy || voiceActive" @change="selectCharacter">
                <option value="" disabled>Vybrat uloženou postavu</option><option v-for="character in characters" :key="character.id" :value="character.id">{{ character.profile.userInput.name }} · {{ character.profile.userInput.world }}</option>
              </select></AwField>
              <AwButton :disabled="busy || voiceActive" @click="beginCharacter()">Nová postava</AwButton>
              <AwButton v-if="selectedCharacter" :disabled="busy || voiceActive" @click="beginCharacter(true)">Upravit postavu</AwButton>
            </div>
            <form v-if="showCharacterForm && open" class="testing-character-form" @submit.prevent="saveCharacter">
              <AwField id="testing-character-name" label="Jméno"><input id="testing-character-name" v-model="draft.name" class="aw-input" maxlength="120" required :disabled="busy" /></AwField>
              <AwField id="testing-character-world" label="Svět / původ"><input id="testing-character-world" v-model="draft.world" class="aw-input" maxlength="160" required :disabled="busy" /></AwField>
              <p class="aw-muted">Stačí jméno a svět. AI podle nich dohledá příběh a vlastnosti postavy stejně jako při běžné návštěvě.</p>
              <details><summary>Doplňující údaje (nepovinné)</summary>
                <AwField id="testing-character-version" label="Verze postavy (nepovinné)"><input id="testing-character-version" v-model="draft.version" class="aw-input" maxlength="120" :disabled="busy" /></AwField>
                <AwField id="testing-character-description" label="Vlastní kontext (nepovinné)"><textarea id="testing-character-description" v-model="draft.description" class="aw-input" maxlength="1200" rows="4" :disabled="busy" /></AwField>
              </details>
              <label class="testing-consent"><input v-model="longTermConsent" type="checkbox" :disabled="busy" /> Povolit izolovanou paměť této testovací postavy při dalších návštěvách. Produkční paměť se nepoužije.</label>
              <div class="testing-actions"><AwButton type="submit" variant="primary" :busy="busy" :disabled="!consent">Uložit a dohledat postavu</AwButton><AwButton :disabled="busy" @click="showCharacterForm = false">Zrušit úpravu</AwButton></div>
            </form>
            <template v-if="selectedCharacter">
              <p>{{ selectedCharacter.profile.userInput.description }}</p>
              <p v-if="characterPending" role="status">AI dohledává postavu podle jména a světa. Můžete počkat zde; stav se průběžně obnovuje.</p>
              <p v-if="selectedCharacter.profile.enrichment">{{ selectedCharacter.profile.enrichment.summary }}</p>
              <AwNotice v-if="selectedCharacter.profile.errorCode" tone="danger">{{ selectedCharacter.profile.errorCode }}</AwNotice>
              <p v-if="selectedCharacter.profile.errorCode" class="aw-muted">Postava je uložená. Dohledání můžete zopakovat nebo pokračovat se zadanými údaji.</p>
              <details><summary>Kontext a AI obohacení postavy</summary>
                <template v-if="selectedCharacter.profile.enrichment"><p>{{ selectedCharacter.profile.enrichment.summary }}</p><p class="testing-code">{{ selectedCharacter.profile.enrichment.model }} · {{ selectedCharacter.profile.enrichment.evidenceKind }}</p></template>
                <p v-else class="aw-muted">Žádné potvrzené AI obohacení. Stav: {{ selectedCharacter.profile.status }}.</p>
                <AwButton v-if="open" :disabled="busy || !consent || voiceActive || characterPending" @click="test.enrich">Spustit skutečné AI obohacení</AwButton>
              </details>
            </template>
            <AwButton v-if="selectedCharacter && !showCharacterForm" variant="primary" :disabled="busy || characterPending" @click="goToStep(3)">Pokračovat k rozhovoru</AwButton>
          </section>
          <section v-show="step === 3" class="testing-conversation" aria-labelledby="testing-conversation-title">
            <div class="testing-section-heading"><h2 id="testing-conversation-title">Rozhovor s DildoVanderem</h2><AwStatus>{{ inputMode }}</AwStatus></div>
            <p v-if="selectedCharacter" class="aw-muted">{{ selectedCharacter.profile.userInput.name }} · {{ selectedCharacter.profile.userInput.world }}</p>
            <AwNotice v-if="error" tone="danger" title="Operace nebyla potvrzena"><p>{{ errorMessage }}</p><small v-if="errorMessage !== error">{{ error }}</small><p>Zpráva se sama znovu neodešle.</p><AwButton :disabled="busy" @click="test.refresh">Obnovit stav</AwButton></AwNotice>
            <p v-if="busy || spokenBusy || (selected.conversationActive && !voiceActive)" role="status">DildoVander připravuje odpověď…</p>
            <p v-if="!selected.conversation.turns.length" class="aw-muted">Napište první zprávu. Textový režim nikdy nevyžaduje mikrofon.</p>
            <ol class="testing-chat" aria-label="Uložený rozhovor"><li v-for="turn in selected.conversation.turns" :key="turn.id" :class="`testing-turn-${turn.role}`">
              <strong>{{ turn.role === 'user' ? selectedCharacter?.profile.userInput.name ?? 'Vy' : 'DildoVander' }}</strong><p>{{ turn.text || 'Text není uložený.' }}</p><small v-if="turn.latencyMs != null">{{ turn.latencyMs }} ms · skutečný backend</small>
            </li></ol>
            <p v-if="partial.user" class="testing-partial">Vy · {{ partial.user }}</p><p v-if="partial.assistant" class="testing-partial">DildoVander · {{ partial.assistant }}</p>
            <form v-if="open && inputMode !== 'voice'" class="testing-chat-form" @submit.prevent="test.send">
              <AwField id="testing-message" label="Vaše zpráva"><textarea id="testing-message" v-model="text" class="aw-input" rows="3" maxlength="4000" :disabled="busy || !selectedCharacter || (voiceActive && captureEnabled && inputMode !== 'hybrid')" /></AwField>
              <AwButton type="submit" variant="primary" :busy="busy || spokenBusy" :disabled="!text.trim() || !selectedCharacter || !consent || stale || (selected.conversationActive && !voiceActive)">Odeslat skutečnému AI</AwButton>
            </form>
            <label v-if="open" class="testing-consent"><input v-model="outputEnabled" type="checkbox" /> Přehrávat skutečný hlas asistenta (také pro psané zprávy, bez mikrofonu)</label>
            <template v-if="open && inputMode !== 'text'">
              <p class="aw-muted">Mikrofon se aktivuje až tlačítkem. Přepnutí do textu uzavře audio spojení a zachová uloženou historii.</p>

              <div class="testing-actions"><AwButton v-if="!voiceActive" variant="primary" :disabled="busy || !selectedCharacter || !consent || stale" @click="test.startVoice">Zapnout mikrofon a hlasové spojení</AwButton>
                <AwButton v-if="voiceActive" @click="test.interrupt">Přerušit odpověď</AwButton><AwButton v-if="voiceActive" @click="test.stopVoice">{{ captureEnabled ? 'Vypnout mikrofon' : 'Odpojit hlasový výstup' }}</AwButton></div>
            </template>
            <p v-if="inputMode === 'text'" class="aw-muted">Mikrofon se neaktivuje. Se zapnutým hlasovým výstupem zpráva použije stejný skutečný model a jeho audio spojení; po přepnutí se obnoví skutečně uložená historie.</p><div v-if="inputMode === 'text' && voiceActive" class="testing-actions"><AwButton @click="test.interrupt">Přerušit hlasovou odpověď</AwButton><AwButton @click="test.stopVoice">Odpojit hlasový výstup</AwButton></div>
            <AwButton :disabled="busy || spokenBusy" @click="goToStep(4)">Pokračovat k výsledku</AwButton>
          </section>
          <div v-show="step === 4"><VirtualSuitcasePanel :hardware="selected.hardware" :device-id="selected.session.virtualDeviceId" :stale="stale" /></div>
          <section v-show="step === 4" class="testing-results" aria-labelledby="testing-results-title">
            <h2 id="testing-results-title">Výsledky, paměť a památky</h2>
            <GeneratedWand v-if="selected.wand?.selectedWand" :wand="selected.wand.selectedWand" :show-name="true" :show-artwork="false" />
            <p v-else class="aw-muted">Zatím žádná potvrzená hůlka. Výsledek se objeví až po skutečném rozhodnutí aplikace.</p>
            <p v-if="selected.wand">Generování hůlek: {{ selected.wand.status }} <span v-if="selected.wand.errorCode">· {{ selected.wand.errorCode }}</span></p>
            <div v-if="open" class="testing-actions"><AwButton :busy="busy" :disabled="!consent || !selectedCharacter || voiceActive || stale" @click="test.selectWand">Vybrat hůlku skutečným AI</AwButton><AwButton :busy="busy" :disabled="!consent || !selected.wand?.selectedWand || voiceActive || stale" @click="test.generate">Generovat skutečné obrazy a památku</AwButton></div>
            <TestingKeepsakes @refresh="test.refresh" :key="selected.session.id" :session-id="selected.session.id" :items="selected.keepsakes" :client="test.client" />
            <details v-if="open && selected.keepsakes.some(item=>item.editions.length)"><summary>Fotografie místo kamery a skutečný AI portrét</summary><p class="aw-muted">Vyberte obrázek. Příprava odstraní metadata a zmenší snímek. Nenahrává se před potvrzením.</p><AwField id="testing-photo" label="Fotografie pro portrét"><input id="testing-photo" type="file" accept="image/jpeg,image/png,image/webp" :disabled="busy || selected.hardware.cameraAvailable === false" @change="choosePhoto" /></AwField><label class="testing-consent"><input v-model="photoConsent" type="checkbox" :disabled="busy" /> Souhlasím se soukromým uložením fotografie a jejím zpracováním OpenAI pro portrét v tomto testovacím sezení.</label><AwButton :busy="busy" :disabled="!photoFile || !photoConsent || !consent || selected.hardware.cameraAvailable === false" @click="portrait">Nahrát a generovat skutečný portrét</AwButton></details>
            <p v-if="!selected.keepsakes.length" class="aw-muted">Zatím žádné uložené testovací památky.</p>
            <details><summary>Uložená testovací paměť</summary><pre class="testing-json">{{ JSON.stringify(selected.memory, null, 2) }}</pre></details>
          </section>
          <div v-show="step === 4"><TestingDuels :key="selected.session.id" :session-id="selected.session.id" :sessions="sessions" :characters="characters" :history="selected.duels" :client="test.client" :enabled="!busy && !stale && consent" /></div>
          <section class="testing-lifecycle" aria-label="Řízení testovacího sezení">
            <div class="testing-actions"><AwButton v-if="open" variant="danger" @click="test.cancel">Stop / zrušit probíhající operaci</AwButton><AwButton v-if="open" :disabled="busy" @click="test.end">Ukončit a uložit sezení</AwButton><AwButton :disabled="busy" @click="test.refresh">Obnovit stav</AwButton></div>
            <details><summary>Smazat testovací data tohoto sezení</summary><label class="testing-consent"><input v-model="deleteConfirmed" type="checkbox" /> Trvale smazat toto testovací sezení. Produkční data nejsou cílem této akce.</label><AwButton variant="danger" :disabled="!deleteConfirmed || busy || voiceActive" @click="test.remove">Smazat testovací sezení</AwButton></details>
          </section>
          <details v-if="selectedCharacter"><summary>Smazat tuto testovací postavu a její testovací paměť</summary><label class="testing-consent"><input v-model="characterDeleteConfirmed" type="checkbox" /> Smazat postavu {{ selectedCharacter.profile.userInput.name }} a její izolovanou paměť. Propojená testovací sezení se ukončí.</label><AwButton variant="danger" :disabled="!characterDeleteConfirmed || busy || voiceActive" @click="deleteCharacter">Smazat testovací postavu</AwButton></details>
          <details class="testing-diagnostics"><summary>Diagnostika skutečných služeb a virtuálního hardwaru</summary>
            <div class="testing-state-strip" aria-label="Zdroje stavu">
              <div><span>Backend / API</span><strong>{{ stale ? 'Nedostupný — historická data' : 'Odpověď skutečného backendu' }}</strong></div>
              <div><span>OpenAI</span><strong>{{ providerState }}</strong></div>
              <div><span>Hardware</span><strong>Virtuální — žádné ESP32 ani BLE</strong></div>
              <div><span>Rozhovor</span><strong>{{ voiceActive ? voicePhase : selected.conversation.status }} · {{ selected.session.engine }}</strong></div>
            </div>
            <details v-if="open"><summary>Vložit poruchu pouze do virtuálního kufru</summary><p class="aw-muted">Výslovně vložené chyby simulace; neoznačují výpadek skutečného OpenAI ani backendu.</p><div class="testing-actions"><AwButton :disabled="busy" @click="test.fault('disconnect')">Simulovat odpojení</AwButton><AwButton :disabled="busy" @click="test.fault('servo_failure')">Simulovat chybu serva</AwButton><AwButton :disabled="busy" @click="test.fault('missing_camera')">Simulovat chybějící kameru</AwButton><AwButton :disabled="busy" @click="test.fault('recover')">Zotavit virtuální kufr</AwButton></div></details>
            <div class="testing-actions"><AwButton @click="exportDiagnostics">Připravit bezpečný diagnostický export</AwButton><a v-if="diagnosticUrl" :href="diagnosticUrl" download="testing-diagnostics.json">Stáhnout technické události</a></div><p class="aw-muted">Export neobsahuje text rozhovoru, obrázky, osobní paměť ani přístupové údaje.</p>
            <dl class="testing-identifiers"><div><dt>Účet</dt><dd>{{ account?.id }}</dd></div><div><dt>Testovací sezení</dt><dd>{{ selected.session.id }}</dd></div><div><dt>Rozhovor</dt><dd>{{ selected.conversation.id }}</dd></div><div><dt>Model</dt><dd>{{ selected.session.engine }}</dd></div></dl>
            <p class="aw-muted">Události níže pocházejí z backendu. Přijetí HTTP požadavku samo nepotvrzuje volání OpenAI ani fyzický pohyb.</p>
            <ol class="testing-timeline"><li v-for="event in selected.conversation.events" :key="event.id"><strong>{{ event.type }}</strong> · {{ event.code }}<small>{{ localTime(event.createdAt) }}<template v-if="event.latencyMs != null"> · {{ event.latencyMs }} ms</template><template v-if="event.inputTokens != null"> · {{ event.inputTokens }} vstupních tokenů</template><template v-if="event.outputTokens != null"> · {{ event.outputTokens }} výstupních tokenů</template><template v-if="event.costMicroUsd != null"> · {{ event.costMicroUsd }} µUSD</template></small><span v-if="event.providerSessionId" class="testing-code">Provider session: {{ event.providerSessionId }}</span></li></ol>
          </details>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped src="./testing-sessions.css"></style>

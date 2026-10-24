<script setup lang="ts">
import { computed, onBeforeUnmount, ref, shallowRef, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import SpellGlyph from '../archive/SpellGlyph.vue';
import { duelSpell, duelStates, type Duel, type DuelAction, type DuelSide } from '../archive/showContracts';
import type { TestCharacter, TestSession } from './contracts';
import type { TestingSessionsClient } from './client';
const props = defineProps<{ sessionId: string; sessions: TestSession[]; characters: TestCharacter[]; history?: Duel[]; client: TestingSessionsClient; enabled: boolean }>();
const current = shallowRef<Duel | null>(null); const partner = ref(''); const mode = ref('interactive');
const busy = ref(false); const error = ref(''); const consent = ref(false); const now = ref(Date.now());
let reading = false; let generation = 0; let operationId: string | null = null;
const partners = computed(() => props.sessions.filter(item => item.id !== props.sessionId && item.characterId));
const deadline = computed(() => current.value?.deadline ? Math.max(0, Math.ceil((Date.parse(current.value.deadline) - now.value) / 1000)) : null);
const actions: DuelAction[] = ['strike', 'trick', 'shield']; const sides: DuelSide[] = ['left', 'right'];
function name(session: TestSession): string { return props.characters.find(item => item.id === session.characterId)?.profile.userInput.name ?? session.id; }
function accept(value: Duel): void { if (!current.value || current.value.id !== value.id || value.revision >= current.value.revision) current.value = value; }
async function run(action: (operation: string) => Promise<Duel>): Promise<void> {
  if (busy.value || !props.enabled) return;
  const epoch = generation; busy.value = true; error.value = ''; operationId ??= crypto.randomUUID();
  try { const view = await action(operationId); if (epoch === generation) { accept(view); operationId = null; } }
  catch (cause) { if (epoch === generation && !(cause instanceof DOMException && cause.name === 'AbortError')) error.value = cause instanceof Error ? cause.message : 'testing_duel_failed'; }
  finally { if (epoch === generation) busy.value = false; }
}
function create(): void { if (partner.value && consent.value) void run(operation => props.client.duel(props.sessionId, partner.value, mode.value, operation)); }
async function refresh():Promise<void>{
  if(reading || document.hidden)return;const epoch=generation;reading=true;
  try { const list=await props.client.duels(props.sessionId); if(epoch!==generation)return; const item=current.value?list.find(item=>item.id===current.value!.id):list[0]; if(item)accept(item); }
  catch(cause){if(epoch===generation && !(cause instanceof DOMException && cause.name==='AbortError'))error.value=cause instanceof Error?cause.message:'testing_duel_read_failed';}
  finally{reading=false;}
}
function narrate():void{if(current.value)void run(()=>props.client.narrateDuel(props.sessionId,current.value!.id));}
function advance():void {if(current.value)void run(()=>props.client.advanceDuel(props.sessionId,current.value!.id));}
function start(): void { if (current.value) void run(operation => props.client.startDuel(props.sessionId, current.value!.id, current.value!.revision, operation)); }
function move(side: DuelSide, action: DuelAction): void { if (current.value) void run(operation => props.client.move(props.sessionId, current.value!.id, side, current.value!.round, current.value!.revision, action, operation)); }
watch(() => props.sessionId, () => { generation++; current.value = null; busy.value = false; error.value = ''; operationId = null; consent.value = false; });
watch(() => [partner.value, mode.value], () => { operationId = null; });
watch(() => props.history, history => { const view = current.value ? history?.find(item => item.id === current.value!.id) : history?.[0]; if (view) accept(view); }, { immediate: true });
void refresh(); const timer = setInterval(() => { now.value = Date.now(); }, 1000); const polling = setInterval(()=>void refresh(),3000); onBeforeUnmount(() => { generation++; clearInterval(timer);clearInterval(polling); });
</script>

<template>
  <section class="testing-duels" aria-labelledby="testing-duels-title">
    <h2 id="testing-duels-title">Souboj dvou testovacích postav</h2>
    <p class="aw-muted">Nejprve vyberte hůlku ve dvou vlastních testovacích sezeních. Oba účastníky můžete ovládat zde; dva fyzické kufry nejsou potřeba.</p>
    <AwNotice v-if="error" tone="danger">{{ error }}</AwNotice>
    <div class="testing-duel-setup">
      <AwField id="testing-duel-partner" label="Druhý účastník"><select id="testing-duel-partner" v-model="partner" class="aw-select" :disabled="busy || !enabled"><option value="" disabled>Vyberte jiné testovací sezení</option><option v-for="session in partners" :key="session.id" :value="session.id">{{ name(session) }}</option></select></AwField>
      <AwField id="testing-duel-mode" label="Režim souboje"><select id="testing-duel-mode" v-model="mode" class="aw-select" :disabled="busy || !enabled"><option value="interactive">Ovládat obě postavy</option><option value="autoplay">Automatické tahy pravidlového systému</option></select></AwField>
      <label><input v-model="consent" type="checkbox" :disabled="busy" /> Souhlasím s účastí obou vlastních testovacích postav.</label>
      <AwButton :disabled="!enabled || !partner || !consent" :busy="busy" @click="create">Připravit testovací souboj</AwButton>
    </div>
    <template v-if="current">
      <p role="status">{{ duelStates[current.state] ?? current.state }}<template v-if="deadline != null"> · {{ deadline }} s do uzavření tahu</template></p>
      <div class="testing-duel-participants"><section v-for="side in sides" :key="side">
        <h3>{{ current[side].displayName }}</h3><p>{{ current[side].wandName ?? current[side].wandId }}</p>
        <p v-if="current.state === 'Running' && current.mode === 'interactive'">{{ (side === 'left' ? current.leftChoiceLocked : current.rightChoiceLocked) ? 'Tah přijat backendem.' : 'Vyberte kouzlo pro toto kolo.' }}</p>
        <div v-if="current.state === 'Running' && current.mode === 'interactive'" class="testing-duel-spells">
          <AwButton v-for="action in actions" :key="action" :disabled="busy || !enabled || (side === 'left' ? current.leftChoiceLocked : current.rightChoiceLocked)" @click="move(side, action)"><SpellGlyph :action="action" />{{ duelSpell(current[side], action) }}</AwButton>
        </div>
      </section></div>
      <AwButton v-if="current.state === 'Ready'" variant="primary" :disabled="!enabled" :busy="busy" @click="start">Zahájit souboj</AwButton>
      <AwButton v-if="['Running','Completed'].includes(current.state)" :disabled="!enabled" :busy="busy" @click="advance">{{ current.state === 'Completed' ? 'Zobrazit potvrzené virtuální efekty' : 'Vyhodnotit stav kola a virtuální efekty' }}</AwButton>
      <ol v-if="current.rounds.length"><li v-for="round in current.rounds" :key="round.number"><strong>Kolo {{ round.number }} · {{ round.leftScore }} : {{ round.rightScore }}</strong><p>{{ current.commentary?.rounds.find(item => item.round === round.number)?.quip ?? round.quip }}</p></li></ol>
      <p v-if="current.result"><strong>{{ current.result.leftPoints }} : {{ current.result.rightPoints }}</strong> · {{ current.result.ending }}</p>
      <AwButton v-if="current.state === 'Completed' && !current.commentary" :disabled="!enabled" :busy="busy" @click="narrate">Vygenerovat skutečný AI komentář souboje</AwButton>
      <p class="aw-muted">Pravidla: {{ current.rulesVersion }}. Vyprávění: {{ current.commentary?.provider ?? 'Bez potvrzeného AI komentáře' }}. Fyzické efekty jsou simulované.</p>
    </template>
  </section>
</template>

<style scoped>
.testing-duel-setup { display: grid; gap: 1rem; }
.testing-duel-setup label { line-height: 1.5; }
.testing-duel-setup input[type='checkbox'] { min-width: 1.25rem; min-height: 1.25rem; vertical-align: middle; margin-right: .5rem; }
.testing-duel-participants { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 1.5rem; margin-block: 1rem; }
.testing-duel-spells { display: grid; gap: .5rem; }
.testing-duel-spells :deep(svg) { width: 1.5rem; height: 1.5rem; flex: 0 0 auto; }
.testing-duels li p { overflow-wrap: anywhere; }
@media(max-width: 38rem) { .testing-duel-participants { grid-template-columns: minmax(0,1fr); } }
</style>

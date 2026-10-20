<script setup lang="ts">
import { computed, reactive, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { metricLabels, type Metric } from './contracts';
import { usePersonaLab } from './usePersonaLab';
import { assertionLabel, observedTokens } from './observations';
import { chooseComparison, compareSessions } from './comparison';
import './persona-lab.css';

const lab = reactive(usePersonaLab());
const section = ref('playground');
const metrics = Object.entries(metricLabels) as [Metric, string][];
const leftId = ref(''); const rightId = ref('');
const filters = reactive({ engine: '', revision: '', scenario: '', from: '', to: '', kind: '', platform: '' });
const suiteSessions = computed(() => new Set(lab.suites.flatMap(suite => suite.results.map(result => result.sessionId))));
const filteredSessions = computed(() => lab.sessions.filter(session =>
  (!filters.engine || session.engine === filters.engine) && (!filters.revision || session.config.id === filters.revision)
  && (!filters.scenario || session.scenarioId === filters.scenario)
  && (!filters.platform || (session.platform ?? 'unknown') === filters.platform)
  && (!filters.from || session.createdAt.slice(0, 10) >= filters.from) && (!filters.to || session.createdAt.slice(0, 10) <= filters.to)
  && (!filters.kind || suiteSessions.value.has(session.id) === (filters.kind === 'suite'))));
const tokens = computed(() => observedTokens(filteredSessions.value));
const observed = computed(() => {
  const sessions = filteredSessions.value;
  const latencies = sessions.flatMap(session => session.turns.filter(turn => turn.role === 'assistant').map(turn => turn.latencyMs).filter((value): value is number => value != null && value >= 0 && Number.isFinite(value))).sort((a, b) => a - b);
  const durations = sessions.flatMap(session => session.endedAt ? [(Date.parse(session.endedAt) - Date.parse(session.createdAt)) / 1000] : []).filter(value => Number.isFinite(value) && value >= 0);
  const failures = sessions.filter(session => session.status === 'failed' || session.events.some(event => event.type === 'provider_failure' && !['cancelled', 'user_cancelled'].includes(event.code ?? ''))).length;
  const disconnections = sessions.filter(session => session.events.some(event => event.type === 'connection_interrupted')).length;
  return { total: sessions.length, latencyCount: latencies.length, min: latencies[0], p95: latencies[Math.max(0, Math.ceil(latencies.length * .95) - 1)],
    durationCount: durations.length, durationMean: durations.length ? durations.reduce((sum, value) => sum + value, 0) / durations.length : null,
    failures, disconnections };
});
const filteredScores = computed(() => metrics.map(([name, label]) => {
  const values = filteredSessions.value.map(session => session.evaluation?.scores[name]).filter((value): value is number => value != null && value >= 1 && value <= 5);
  return { name, label, count: values.length, mean: values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : null };
}));
const left = computed(() => lab.sessions.find(item => item.id === leftId.value));
const right = computed(() => lab.sessions.find(item => item.id === rightId.value));
const comparison = computed(() => left.value && right.value ? compareSessions(left.value, right.value, suiteSessions.value) : null);
function selectComparison(side: 'left' | 'right', event: Event): void {
  const pair = chooseComparison({ left: leftId.value, right: rightId.value }, side, (event.target as HTMLSelectElement).value);
  leftId.value = pair.left; rightId.value = pair.right;
}
async function openSuiteResult(id: string): Promise<void> {
  if (await lab.openSuiteSession(id)) section.value = 'evaluation';
}
function measured(value: number | null | undefined, unit = ''): string { return value == null ? 'Neuvedeno' : `${value.toFixed(1)}${unit}`; }
const fixture = computed(() => lab.catalog?.memoryFixtures.find(item => item.id === lab.fixtureId));
const scenario = computed(() => lab.catalog?.scenarios.find(item => item.id === lab.scenarioId));
const selectedScenario = computed(() => lab.selected?.scenarioSnapshot ?? lab.catalog?.scenarios.find(item => item.id === lab.selected?.scenarioId));
const phaseLabels = { idle: 'Připraveno', connecting: 'Připojuji hlas…', listening: 'Mikrofon aktivní · naslouchám', speaking: 'DildoVander mluví', ended: 'Hlas ukončen', error: 'Hlas přerušen' };
const diff = computed(() => {
  const base = lab.configRevision;
  if (!base) return [];
  return [
    { label: 'Název', before: base.name, after: lab.draftName },
    { label: 'Instrukce', before: base.instructions, after: lab.draftInstructions },
    { label: 'Model', before: base.engine, after: lab.draftEngine },
    { label: 'Hlas', before: base.voice, after: lab.draftVoice },
  ].filter(item => item.before !== item.after);
});
const publishedDiff = computed(() => {
  const active = lab.catalog?.versions.find(version => version.id === lab.catalog?.activeRevisionId);
  const selected = lab.configRevision;
  if (!active || !selected) return [];
  return [
    { label: 'Instrukce', before: active.instructions, after: selected.instructions },
    { label: 'Model', before: active.engine, after: selected.engine },
    { label: 'Hlas', before: active.voice, after: selected.voice },
  ].filter(change => change.before !== change.after);
});
function date(value: string): string { return new Date(value).toLocaleString('cs-CZ'); }
function platformLabel(value?: string): string { return value === 'ios' ? 'iOS' : value === 'web' ? 'Web' : 'Nezjištěná'; }
function availability(value: boolean | null): string { return value === null ? 'Neuvedeno' : value ? 'Dostupné' : 'Nedostupné'; }
function selectExisting(event: Event): void {
  const session = lab.sessions.find(item => item.id === (event.target as HTMLSelectElement).value);
  if (session) lab.selectSession(session);
}
function chooseImage(event: Event): void {
  const input = event.target as HTMLInputElement; const file = input.files?.[0]; input.value = '';
  if (file) void lab.chooseImage(file);
}
</script>

<template>
  <div class="persona-lab">
    <header class="lab-heading">
      <div><p class="lab-eyebrow">Vývoj postavy</p><h1>Persona Lab</h1><p>Vyzkoušejte rozhovor. Zachyťte konkrétní moment. Porovnejte verze.</p></div>
      <span class="aw-badge">{{ lab.hardware.mode || lab.hardware.bound ? 'SKUTEČNÝ KUFR · výslovný test' : 'SIMULACE · bez pohonů' }}</span>
    </header>
    <AwNotice :live="false" title="Oddělené testovací prostředí">
      Model skutečně odpovídá a spotřebovává API. Scénáře a paměť jsou testovací. Výchozí simulace neovládá kufr; skutečný režim vyžaduje zvláštní výběr a potvrzení přesného zařízení.
      Audio se nearchivuje. Ukládání přepisu zapínáte zvlášť pro každé sezení.
    </AwNotice>
    <template v-if="!lab.account">
      <AwNotice title="Nejprve se přihlaste">Persona Lab je dostupná přihlášeným testerům. Výsledky patří vašemu účtu.</AwNotice>
      <RouterLink :to="{ path: '/signin', query: { return: '/persona-lab' } }" class="aw-button aw-button--primary">Přihlásit se</RouterLink>
    </template>
    <template v-else>
      <nav class="lab-navigation" aria-label="Části Persona Lab">
        <AwButton :aria-pressed="section === 'playground'" @click="section = 'playground'">Playground</AwButton>
        <AwButton :aria-pressed="section === 'evaluation'" @click="section = 'evaluation'">Hodnocení</AwButton>
        <AwButton :aria-pressed="section === 'comparison'" @click="section = 'comparison'">Porovnání a přehled</AwButton>
        <AwButton v-if="lab.admin" :aria-pressed="section === 'configuration'" @click="section = 'configuration'">Konfigurace</AwButton>
      </nav>
      <AwNotice v-if="lab.error" tone="danger" title="Akce se nepodařila">
        {{ lab.error }}
        <RouterLink v-if="lab.failure?.reauthenticate" :to="{ path: '/signin', query: { reauth: '1', return: '/persona-lab' } }">Ověřit přihlášení</RouterLink>
      </AwNotice>
      <AwNotice v-if="lab.notice" :tone="lab.noticeTone">{{ lab.notice }}</AwNotice>
      <div v-if="lab.selected?.status === 'open' && (lab.voiceActive || lab.conversationPending || lab.recoveryPending || lab.hardware.active)" class="aw-card lab-livebar" role="region" aria-label="Probíhající test">
        <div><strong>{{ lab.selected.label }}</strong><p>{{ lab.hardware.active ? 'Skutečný kufr · stav backendu není fyzické potvrzení' : lab.recoveryPending ? 'Čekám na obnovu s novým kontextem' : lab.conversationPending ? 'Model zpracovává text' : phaseLabels[lab.voicePhase] }} · {{ lab.sessionAgeSeconds }} s od přípravy</p></div>
        <div class="lab-actions"><AwButton v-if="lab.voiceActive || lab.recoveryPending" @click="lab.stopVoice()">{{ lab.recoveryPending ? 'Zrušit obnovu' : 'Vypnout hlas' }}</AwButton><AwButton variant="danger" @click="lab.end">Ukončit test</AwButton></div>
      </div>
      <p v-if="lab.loading" role="status">Načítám verze a vaše výsledky…</p>
      <div class="lab-toolbar">
        <AwField id="lab-existing" label="Vaše sezení">
          <select class="aw-input" id="lab-existing" :value="lab.selected?.id ?? ''" :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active" @change="selectExisting">
            <option value="" disabled>Vyberte uložený test</option>
            <option v-for="item in lab.sessions" :key="item.id" :value="item.id">{{ item.label }} · {{ item.engine }} · {{ date(item.createdAt) }}</option>
          </select>
        </AwField>
        <AwButton :disabled="lab.busy || lab.loading || lab.voiceActive || lab.recoveryPending" @click="lab.load">Obnovit přehled</AwButton>
      </div>

      <section v-show="section === 'playground'" aria-labelledby="lab-playground-title">
        <h2 id="lab-playground-title">Playground</h2>
        <div class="lab-workspace">
          <form class="aw-card lab-setup" @submit.prevent="lab.create">
            <h3>Nový test</h3>
            <AwField id="lab-label" label="Název testu"><input class="aw-input" id="lab-label" v-model="lab.label" maxlength="100" placeholder="Např. návrat hosta · suchý humor" /></AwField>
            <AwField id="lab-revision" label="Verze persony">
              <select class="aw-input" id="lab-revision" v-model="lab.revisionId" required :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active">
                <option v-for="version in lab.catalog?.versions" :key="version.id" :value="version.id">{{ version.name }} · r{{ version.revision }} · {{ version.status }}</option>
              </select>
            </AwField>
            <AwField id="lab-engine" label="Konverzační model">
              <select class="aw-input" id="lab-engine" v-model="lab.engine" :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active">
                <option value="gpt-live-1">GPT Live · gpt-live-1</option><option value="gpt-realtime-2.1">GPT Realtime 2.1 · gpt-realtime-2.1</option>
              </select>
            </AwField>
            <p v-if="lab.catalog && !lab.engineReady" class="lab-muted">Model není na serveru připravený. Sezení lze připravit, odpověď vyžaduje dostupný model.</p>
            <AwField id="lab-scenario" label="Scénář">
              <select class="aw-input" id="lab-scenario" v-model="lab.scenarioId" required :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active"><option v-for="item in lab.catalog?.scenarios" :key="item.id" :value="item.id">{{ item.name }}</option></select>
            </AwField>
            <p class="lab-muted">{{ scenario?.description }}</p>
            <details v-if="scenario?.script?.length"><summary>Skript scénáře ({{ scenario.script.length }} vstupů)</summary><ol><li v-for="turn in scenario.script" :key="turn.index"><p>{{ turn.index }}. {{ turn.input }}</p><small class="lab-muted">Plánované kontroly: {{ turn.checks.join(', ') }}</small></li></ol><code v-if="scenario.scriptSha256">{{ scenario.scriptSha256 }}</code></details>
            <AwField id="lab-fixture" label="Testovací paměť">
              <select class="aw-input" id="lab-fixture" v-model="lab.fixtureId" required :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active"><option v-for="item in lab.catalog?.memoryFixtures" :key="item.id" :value="item.id">{{ item.name }}</option></select>
            </AwField>
            <details v-if="fixture"><summary>Co tento test pamatuje</summary><p>{{ fixture.description }}</p><dl class="lab-definition"><dt>Aktuální rozhovor</dt><dd>{{ fixture.current || 'Prázdná paměť' }}</dd><dt>Večer</dt><dd>{{ fixture.evening || 'Prázdná paměť' }}</dd><dt>Vrácený host</dt><dd>{{ fixture.returning || 'Prázdná paměť' }}</dd></dl></details>
            <details><summary>Společné porovnání</summary><AwField id="lab-comparison-id" label="Označení porovnání" hint="Stejné označení použijte u obou variant se stejným scénářem a pamětí."><input class="aw-input" id="lab-comparison-id" v-model="lab.comparisonId" maxlength="80" /></AwField></details>
            <label class="lab-check"><input v-model="lab.recordContent" type="checkbox" /><span>Ukládat přepis pro porovnání<small>Dobrovolné. Bez souhlasu zůstávají po testu jen technické údaje a vámi uložené hodnocení.</small></span></label>
            <AwButton type="submit" variant="primary" :busy="lab.busy" :disabled="!lab.catalog || lab.voiceActive || lab.recoveryPending || lab.hardware.active || !lab.online || !lab.revisionId">Připravit sezení</AwButton>
          </form>
          <div class="aw-card lab-conversation">
            <template v-if="lab.selected">
              <section class="lab-hardware" aria-labelledby="lab-hardware-title">
                <h3 id="lab-hardware-title">Prostředí tohoto testu</h3>
                <div class="lab-actions">
                  <label class="lab-check"><input v-model="lab.hardware.mode" type="radio" :value="false" :disabled="lab.hardware.bound || lab.hardware.pending" /><span>Simulace v aplikaci</span></label>
                  <label class="lab-check"><input v-model="lab.hardware.mode" type="radio" :value="true" :disabled="!lab.hardware.eligibility.target || lab.hardware.bound || lab.hardware.pending || lab.busy || lab.voiceActive || lab.conversationPending" /><span>Skutečný připojený kufr</span></label>
                </div>
                <p v-if="!lab.hardware.eligibility.ready && !lab.selected.hardware" class="lab-muted">{{ lab.hardware.eligibility.reason }}</p>
                <template v-if="lab.hardware.mode || lab.selected.hardware">
                  <AwNotice tone="warning" :live="false" title="SKUTEČNÉ ZAŘÍZENÍ">
                    Hlas používá mikrofon a reproduktor kufru, nikoli prohlížeče. Přijetí testu ani stav backendu nepotvrzují fyzický pohyb či zastavení. Fotografie se v tomto režimu neposílají.
                  </AwNotice>
                  <dl class="lab-definition">
                    <dt>Zařízení</dt><dd>{{ lab.selected.hardware?.deviceId || lab.hardware.eligibility.target?.deviceId || 'Nepotvrzeno' }}</dd>
                    <dt>Boot / výchozí revize</dt><dd>{{ lab.selected.hardware?.bootId || lab.hardware.eligibility.target?.bootId || 'Nepotvrzeno' }} / {{ lab.selected.hardware?.expectedRevision ?? lab.hardware.eligibility.target?.revision ?? 'Nepotvrzeno' }}</dd>
                    <template v-if="lab.selected.hardware"><dt>Pozorovaný stav backendu</dt><dd>{{ lab.selected.hardware.status }} · do {{ date(lab.selected.hardware.expiresAt) }}</dd><dt>Rozhovor</dt><dd>{{ lab.selected.hardware.conversationId || 'Dosud nepotvrzen' }}</dd></template>
                  </dl>
                  <label v-if="!lab.hardware.bound" class="lab-check"><input v-model="lab.hardware.confirmed" type="checkbox" :disabled="lab.hardware.pending || lab.busy" /><span>Výslovně potvrzuji test na tomto zařízení, bootu a revizi se zvoleným modelem a hlasem.</span></label>
                  <p v-if="lab.hardware.uncertain && !lab.selected.hardware" role="status">Výsledek vazby není potvrzený. Běžný text, hlas a fotografie jsou pro toto sezení zablokované. Obnovte přehled nebo ukončete test.</p>
                  <div class="lab-actions">
                    <AwButton v-if="!lab.hardware.bound" variant="primary" :busy="lab.hardware.pending" :disabled="!lab.hardware.confirmed || !lab.hardware.eligibility.ready || lab.busy || lab.voiceActive || lab.conversationPending || lab.recoveryPending || !!lab.image || !!lab.selected.hasImage" @click="lab.hardware.start">Spustit izolovaný test na kufru</AwButton>
                    <AwButton v-if="!lab.hardware.bound && lab.hardware.eligibility.canConfigure" :disabled="!lab.hardware.confirmed || lab.busy || lab.hardware.pending" @click="lab.hardware.configure">Nastavit zvolený model a hlas na kufru</AwButton>
                    <AwButton v-if="lab.hardware.bound" variant="danger" @click="lab.end">Ukončit vazbu a vyžádat místní Stop</AwButton>
                  </div>
                </template>
              </section>
              <div class="lab-conversation-heading"><div><h3>{{ lab.selected.label }}</h3><p class="lab-muted">{{ lab.selected.engine }} · {{ lab.selected.config.name }} · r{{ lab.selected.config.revision }}</p></div><span class="aw-badge">{{ lab.selected.status === 'open' ? phaseLabels[lab.voicePhase] : 'Sezení ukončené' }}</span></div>
              <p class="lab-muted">{{ lab.selected.recordContent ? 'Přepis se ukládá s vaším souhlasem.' : 'Přepis se nearchivuje. Po skončení může být dostupná jen technická stopa.' }}</p>
              <p>Délka od přípravy: <strong>{{ lab.sessionAgeSeconds }} s</strong><template v-if="lab.selected.endedAt"> · ukončené sezení</template></p>
              <details><summary>Přesný snímek konfigurace testu</summary><dl class="lab-definition"><dt>Verze / původ</dt><dd>{{ lab.selected.config.id }} · {{ lab.selected.config.sourceVersion || 'Původ neuveden' }}</dd><dt>Otisk instrukcí</dt><dd><code>{{ lab.selected.config.sha256 || 'Otisk není v této odpovědi dostupný' }}</code></dd><dt>Scénář / paměť</dt><dd>{{ selectedScenario?.name || lab.selected.scenarioId }} / {{ lab.selected.memorySnapshot?.name || lab.selected.fixtureId }}</dd><dt>Platforma testu</dt><dd>{{ platformLabel(lab.selected.platform) }}</dd><dt>Hlas pro test</dt><dd>{{ lab.selected.config.voice }}</dd></dl></details>
              <div class="lab-transcript" aria-label="Tahy rozhovoru" tabindex="0">
                <p v-if="!lab.selected.turns.length && !lab.partial.user && !lab.partial.assistant" class="lab-empty">Začněte textem nebo spusťte hlas. Žádná odpověď zatím nevznikla.</p>
                <article v-for="(turn, index) in lab.selected.turns" :key="turn.id" :class="['lab-turn', `lab-turn--${turn.role}`]">
                  <header><strong>{{ turn.role === 'user' ? 'Vy' : 'DildoVander' }}</strong><a :href="`#lab-turn-${turn.id}`" @click="section = 'evaluation'">Tah {{ index + 1 }}</a></header>
                  <p>{{ turn.text || 'Obsah tohoto tahu nebyl archivován.' }}</p>
                  <small v-if="turn.latencyMs != null">Odezva {{ turn.latencyMs }} ms</small>
                  <small v-if="turn.decision">Simulované rozhodnutí: {{ turn.decision }}<template v-if="turn.compartment"> · přihrádka {{ turn.compartment }}</template></small>
                </article>
                <article v-if="lab.partial.user" class="lab-turn lab-turn--user"><strong>Vy · průběžný přepis</strong><p>{{ lab.partial.user }}</p></article>
                <article v-if="lab.partial.assistant" class="lab-turn"><strong>DildoVander · průběžná odpověď</strong><p>{{ lab.partial.assistant }}</p></article>
              </div>
              <div v-if="lab.selected.status === 'open'" class="lab-actions">
                <AwButton v-if="!lab.simulationBlocked && !lab.voiceActive && (lab.voicePhase !== 'error' || lab.manualVoiceRestart)" :disabled="lab.busy || lab.recoveryPending || !lab.online || !lab.catalog?.capabilities.voice || !lab.selectedEngineReady" @click="lab.startVoice()">Spustit hlas</AwButton>
                <AwButton v-if="lab.voiceActive" :aria-pressed="lab.muted" @click="lab.toggleMute">{{ lab.muted ? 'Zapnout mikrofon' : 'Ztlumit mikrofon' }}</AwButton>
                <AwButton v-if="lab.voiceActive" :disabled="lab.voicePhase === 'connecting'" @click="lab.interrupt">Přerušit odpověď</AwButton>
                <AwButton v-if="lab.voiceActive" @click="lab.stopVoice">Vypnout hlas</AwButton>
                <AwButton v-if="lab.recoverable && !lab.voiceActive" :disabled="lab.busy" @click="lab.startVoice(true)">Zkusit hlas znovu ({{ lab.reconnects + 1 }}/2)</AwButton>
                <AwButton variant="danger" @click="lab.end">Ukončit test</AwButton>
              </div>
              <p v-if="lab.voiceActive" role="status">{{ phaseLabels[lab.voicePhase] }}. Mikrofon {{ lab.muted ? 'je ztlumený' : 'odesílá zvuk do testovacího modelu' }}.</p>
              <form class="lab-composer" @submit.prevent="lab.send">
                <AwField id="lab-message" label="Vaše zpráva" :hint="lab.voiceActive ? 'Text je během hlasového spojení pozastavený. Pro textové pokračování ukončete hlas.' : undefined"><textarea class="aw-input" id="lab-message" v-model="lab.text" rows="3" maxlength="4000" :disabled="lab.busy || lab.simulationBlocked || lab.voiceActive || lab.recoveryPending || !lab.online || lab.selected.status !== 'open'" placeholder="Napište hostův vstup…" /></AwField>
                <div class="lab-actions"><AwButton type="submit" variant="primary" :busy="lab.busy" :disabled="lab.simulationBlocked || !lab.text.trim() || lab.voiceActive || lab.recoveryPending || !lab.online || lab.selected.status !== 'open' || !lab.selectedEngineReady">Odeslat do modelu</AwButton><AwButton v-if="selectedScenario?.openingText" :disabled="lab.busy || lab.simulationBlocked || lab.voiceActive || lab.recoveryPending || !lab.online || lab.selected.status !== 'open'" @click="lab.text = selectedScenario.openingText">Vložit úvod scénáře</AwButton></div>
              </form>
              <details v-if="lab.selected.engine === 'gpt-realtime-2.1'" class="lab-image">
                <summary>Fotografie pro vizuální test</summary>
                <p>Realtime může při další odpovědi použít jeden dobrovolně připojený snímek. Fotografie zůstává v dočasném kontextu nejvýše 15 minut, nearchivuje se a není součástí diagnostického exportu.</p>
                <p v-if="!lab.catalog?.capabilities.imageRealtime" class="lab-muted">Server zatím obrazový vstup pro tento model nepotvrdil.</p>
                <template v-else>
                  <AwField id="lab-image-input" label="Vyfotit nebo vybrat obrázek" hint="JPEG, PNG nebo WebP do 8 MB. Před odesláním se snímek zmenší a odstraní se metadata.">
                    <input class="aw-input" id="lab-image-input" type="file" accept="image/jpeg,image/png,image/webp" capture="user" :disabled="lab.busy || lab.simulationBlocked || lab.imagePreparing || lab.voiceActive || lab.selected.status !== 'open'" @change="chooseImage" />
                  </AwField>
                  <p v-if="lab.imagePreparing" role="status">Připravuji soukromý náhled…</p>
                  <figure v-if="lab.image"><img :src="lab.image.previewUrl" alt="Vybraný testovací snímek před odesláním" /><figcaption>Náhled · {{ lab.image.width }} × {{ lab.image.height }} · {{ Math.ceil(lab.image.bytes / 1024) }} kB. Výběr snímek neodesílá.</figcaption></figure>
                  <label v-if="lab.image" class="lab-check"><input v-model="lab.imageConsent" type="checkbox" :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active" /><span>Mám souhlas zobrazených osob nebo používám syntetický snímek. Souhlasím s odesláním poskytovateli modelu pro tento test.</span></label>
                  <p v-if="lab.selected.hasImage || lab.imageAttachedSession === lab.selected.id" role="status">Server potvrdil dočasné připojení fotografie k tomuto testu.</p>
                  <div class="lab-actions"><AwButton :busy="lab.busy" :disabled="lab.simulationBlocked || !lab.image || !lab.imageConsent || lab.voiceActive || lab.selected.status !== 'open'" @click="lab.attachImage">Připojit fotografii k dalšímu vstupu</AwButton><AwButton :disabled="lab.busy" @click="lab.removeImage">Odebrat fotografii z testu</AwButton></div>
                  <p v-if="lab.voiceActive" class="lab-muted">Pro připojení nového snímku nejprve vypněte hlas. Odebrání snímku hlas vypne okamžitě.</p>
                </template>
              </details>
              <p v-else class="lab-muted">GPT Live v této laboratoři nemá potvrzený obrazový vstup. Pro vizuální test vyberte nové sezení s Realtime 2.1.</p>
            </template>
            <div v-else class="lab-empty"><h3>Prostor pro rozhovor</h3><p>Připravte nové sezení nebo vyberte svůj uložený test. Každý test uchovává přesnou verzi persony.</p></div>
          </div>
        </div>
        <details class="aw-card lab-suite">
          <summary>Spustit scénářovou sadu</summary>
          <p>Každý vybraný scénář provede více skriptovaných vstupů ve stejném sezení, nejvýše osm. Verze persony a testovací paměť se převezmou z výběru nahoře. Textový běh neověřuje mikrofon, přerušení hlasu ani obnovu skutečné sítě.</p>
          <div class="lab-suite-options">
            <label v-for="item in lab.catalog?.scenarios" :key="item.id" class="lab-check">
              <input v-model="lab.suiteScenarioIds" type="checkbox" :value="item.id" :disabled="lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active" />
              <span>{{ item.name }}<small>{{ item.description }} · {{ item.script ? `${item.script.length} vstupů` : 'Počet vstupů není uveden' }}</small></span>
            </label>
          </div>
          <AwButton :busy="lab.busy" :disabled="lab.hardware.active || !lab.suiteScenarioIds.length || !lab.engineReady || lab.voiceActive || lab.recoveryPending || !lab.online" @click="lab.runSuite">Spustit scénáře ({{ lab.suiteScenarioIds.length }})</AwButton>
          <article v-for="suite in lab.suites" :key="suite.id" class="lab-suite-result">
            <h3>{{ date(suite.createdAt) }} · {{ suite.engine }}</h3>
            <p>Stav: {{ suite.status === 'completed' ? 'Dokončeno' : suite.status === 'failed' ? 'Selhalo' : 'Probíhá' }}</p>
            <ul>
              <li v-for="result in suite.results" :key="result.scenarioId">
                <strong>{{ result.scenarioId }} · {{ result.status }}</strong>
                <AwButton :disabled="!result.sessionId || lab.busy || lab.voiceActive || lab.recoveryPending || lab.hardware.active" @click="openSuiteResult(result.sessionId)">Otevřít sezení a hodnotit</AwButton>
                <p v-if="result.scenarioSha256" class="lab-muted">Otisk skriptu: <code>{{ result.scenarioSha256 }}</code></p>
                <ul><li v-for="assertion in result.assertions" :key="assertion.name">{{ assertionLabel(assertion) }} · {{ assertion.name }}: {{ assertion.detail }}</li></ul>
                <details v-if="result.turnObservations?.length">
                  <summary>Pozorování jednotlivých tahů ({{ result.turnObservations.length }})</summary>
                  <ol>
                    <li v-for="turn in result.turnObservations" :key="turn.index">
                      <strong>Tah {{ turn.index }} · {{ turn.status === 'completed' ? 'Dokončen' : 'Selhal' }}</strong>
                      <p>Odezva: {{ turn.latencyMs == null ? 'Neuvedeno' : `${turn.latencyMs} ms` }} · audio: {{ turn.audioBytes == null ? 'Neuvedeno' : `${turn.audioBytes} B` }}</p>
                      <p class="lab-muted">Vstup {{ turn.userTurnId || 'Není potvrzen' }} · odpověď {{ turn.assistantTurnId || 'Není potvrzena' }}</p>
                      <ul><li v-for="assertion in turn.assertions" :key="assertion.name">{{ assertionLabel(assertion) }} · {{ assertion.name }}: {{ assertion.detail }}</li></ul>
                    </li>
                  </ol>
                </details>
              </li>
            </ul>
          </article>
        </details>
        <details v-if="lab.selected" class="aw-card lab-diagnostics"><summary>Technická časová osa · {{ lab.selected.events.length }} událostí</summary>
          <p>Výpis obsahuje skutečně přijaté události tohoto testu. Prázdná stopa nepotvrzuje úspěch modelu.</p>
          <ol v-if="lab.selected.events.length" class="lab-timeline">
            <li v-for="event in lab.selected.events" :key="event.id">
              <time>{{ date(event.createdAt) }}</time><strong>{{ event.type }}</strong>
              <span v-if="event.code">{{ event.code }}</span>
              <span v-if="event.latencyMs != null">{{ event.latencyMs }} ms</span>
              <span v-if="event.decision">{{ event.decision }} · {{ lab.selected.hardware ? 'návrh pro skutečný kufr, ne potvrzení pohybu' : 'simulace' }}</span>
              <span v-if="event.compartment != null">Návrh přihrádky {{ event.compartment }} · bez fyzického povelu</span>
              <span v-if="event.type === 'tool_result' && event.code === 'simulation_only'">Simulovaný výsledek nástroje; žádný pohon ani skutečná paměť nebyly touto událostí ověřeny.</span>
              <span v-if="event.type === 'provider_tokens' && event.code === 'observed'">Tokeny vstup / výstup: {{ event.inputTokens ?? 'Neuvedeno' }} / {{ event.outputTokens ?? 'Neuvedeno' }}</span>
              <code v-if="event.providerSessionId">{{ event.providerSessionId }}</code>
            </li>
          </ol>
          <p v-else class="lab-muted">Server zatím nevrátil žádné technické události.</p>
          <AwButton @click="lab.downloadDiagnostics">Stáhnout technickou diagnostiku</AwButton><p class="lab-muted">Export neobsahuje přepis, instrukce, hodnocení, audio ani přístupové údaje.</p>
        </details>
      </section>

      <section v-show="section === 'evaluation'" aria-labelledby="lab-evaluation-title">
        <h2 id="lab-evaluation-title">Hodnocení konkrétního testu</h2>
        <p>Sedm metrik od 1 (slabé) do 5 (výborné). Hodnoťte pozorovaný výsledek, v komentáři uveďte jeho důkaz.</p>
        <p v-if="!lab.selected" class="aw-card lab-empty">Nejprve vyberte nebo proveďte test.</p>
        <form v-else class="lab-evaluation" @submit.prevent="lab.saveEvaluation">
          <p><strong>{{ lab.selected.label }}</strong> · {{ lab.selected.engine }} · {{ lab.selected.config.name }}</p>
          <AwNotice :live="false">Komentáře ukládáte výslovným odesláním, i když záznam přepisu nebyl zapnutý. Nevkládejte osobní údaje hostů.</AwNotice>
          <div class="lab-metrics"><fieldset v-for="[key, title] in metrics" :key="key" class="aw-card"><legend>{{ title }}</legend><div class="lab-score"><label v-for="score in 5" :key="score"><input v-model="lab.scores[key]" type="radio" :name="`lab-score-${key}`" :value="score" /><span>{{ score }}</span></label></div></fieldset></div>
          <AwField id="lab-evaluation-comment" label="Celkový komentář" hint="Co zachovat a co změnit v další verzi?"><textarea class="aw-input" id="lab-evaluation-comment" v-model="lab.comment" rows="3" maxlength="4000" /></AwField>
          <details open><summary>Komentáře k přesným tahům ({{ lab.selected.turns.length }})</summary>
            <article v-for="(turn, index) in lab.selected.turns" :id="`lab-turn-${turn.id}`" :key="turn.id" class="lab-turn"><strong>Tah {{ index + 1 }} · {{ turn.role === 'user' ? 'Vy' : 'DildoVander' }}</strong><p>{{ turn.text || 'Přepis tohoto tahu nebyl uložen.' }}</p><AwField :id="`lab-comment-${turn.id}`" :label="`Komentář k tahu ${index + 1}`"><textarea class="aw-input" :id="`lab-comment-${turn.id}`" v-model="lab.turnComments[turn.id]" rows="2" maxlength="1000" /></AwField><small class="lab-muted">{{ turn.id }}</small></article>
            <p v-if="!lab.selected.turns.length">Komentáře k tahům budou dostupné po skutečné odpovědi modelu.</p>
          </details>
          <AwButton type="submit" variant="primary" :busy="lab.busy" :disabled="!lab.validEvaluation || !lab.selected.turns.length || lab.voiceActive">Uložit hodnocení</AwButton>
        </form>
      </section>

      <section v-show="section === 'comparison'" aria-labelledby="lab-comparison-title">
        <h2 id="lab-comparison-title">Porovnání a přehled</h2>
        <p>Přehled vašich skutečně uložených testů. Průměry jsou ruční hodnocení testerů, nikoli automatické měření kvality.</p>
        <div class="lab-summary"><div class="aw-card"><strong>{{ lab.analytics?.sessions ?? '—' }}</strong><span>Vašich testů</span></div><div class="aw-card"><strong>{{ lab.analytics?.evaluations ?? '—' }}</strong><span>Uložených hodnocení</span></div></div>
        <div class="aw-card lab-analytics" aria-labelledby="lab-token-title">
          <h3 id="lab-token-title">Pozorované tokeny po filtrování</h3>
          <dl class="lab-definition">
            <dt>Známá část vstupních tokenů</dt><dd>{{ tokens.input ?? 'Neuvedeno' }} · {{ tokens.inputSamples }} pozorování z {{ tokens.inputSessions }} / {{ tokens.totalSessions }} sezení</dd>
            <dt>Známá část výstupních tokenů</dt><dd>{{ tokens.output ?? 'Neuvedeno' }} · {{ tokens.outputSamples }} pozorování z {{ tokens.outputSessions }} / {{ tokens.totalSessions }} sezení</dd>
          </dl>
          <p class="lab-muted">Neuvedená hodnota není nula. Součet zahrnuje jen skutečně nahlášené přírůstky; z délky audia se tokeny neodvozují. Platforma je technický údaj klienta a sama neprokazuje ověření na iPhonu.</p>
        </div>
        <details open class="aw-card"><summary>Filtry skutečných pozorování</summary><div class="lab-filter-grid"><AwField id="lab-filter-engine" label="Model"><select class="aw-input" id="lab-filter-engine" v-model="filters.engine"><option value="">Všechny</option><option value="gpt-live-1">gpt-live-1</option><option value="gpt-realtime-2.1">gpt-realtime-2.1</option></select></AwField><AwField id="lab-filter-revision" label="Verze"><select class="aw-input" id="lab-filter-revision" v-model="filters.revision"><option value="">Všechny</option><option v-for="version in lab.catalog?.versions" :key="version.id" :value="version.id">{{ version.name }} · r{{ version.revision }}</option></select></AwField><AwField id="lab-filter-scenario" label="Scénář"><select class="aw-input" id="lab-filter-scenario" v-model="filters.scenario"><option value="">Všechny</option><option v-for="item in lab.catalog?.scenarios" :key="item.id" :value="item.id">{{ item.name }}</option></select></AwField><AwField id="lab-filter-platform" label="Platforma"><select class="aw-input" id="lab-filter-platform" v-model="filters.platform"><option value="">Všechny</option><option value="web">Web</option><option value="ios">iOS</option><option value="unknown">Nezjištěná</option></select></AwField><AwField id="lab-filter-kind" label="Typ testu"><select class="aw-input" id="lab-filter-kind" v-model="filters.kind"><option value="">Všechny</option><option value="suite">Scénářová sada</option><option value="manual">Ostatní sezení</option></select></AwField><AwField id="lab-filter-from" label="Od (UTC)"><input class="aw-input" id="lab-filter-from" v-model="filters.from" type="date" /></AwField><AwField id="lab-filter-to" label="Do (UTC)"><input class="aw-input" id="lab-filter-to" v-model="filters.to" type="date" /></AwField></div></details>
        <div class="lab-table-scroll" tabindex="0" aria-label="Technická spolehlivost filtrovaných testů"><table><caption>Technická pozorování · {{ observed.total }} sezení po filtrování</caption><tbody><tr><th scope="row">Nejkratší / p95 odezva</th><td>{{ observed.latencyCount ? `${observed.min} / ${observed.p95} ms` : 'Bez měření' }}</td><td>n={{ observed.latencyCount }} měřených tahů</td></tr><tr><th scope="row">Průměrná délka ukončeného sezení</th><td>{{ observed.durationMean == null ? 'Bez měření' : `${observed.durationMean.toFixed(1)} s` }}</td><td>n={{ observed.durationCount }} sezení</td></tr><tr><th scope="row">Selhaná sezení</th><td>{{ observed.failures }} / {{ observed.total }}</td><td>{{ observed.total ? `${(observed.failures / observed.total * 100).toFixed(1)} %` : 'Bez podkladů' }}</td></tr><tr><th scope="row">Zaznamenané odpojení</th><td>{{ observed.disconnections }} / {{ observed.total }}</td><td>Jen výslovné serverové události; chybějící událost nepotvrzuje nepřerušené spojení.</td></tr></tbody></table></div>
        <div class="aw-card lab-analytics"><h3>Hodnocení po filtrování</h3><div v-for="metric in filteredScores" :key="metric.name" class="lab-aggregate"><span>{{ metric.label }}</span><meter v-if="metric.mean != null" min="1" max="5" :value="metric.mean" :aria-label="metric.label" /><span v-else class="lab-muted">Bez hodnocení</span><span>{{ metric.mean == null ? '—' : metric.mean.toFixed(2) }} · n={{ metric.count }}</span></div></div>
        <details v-if="lab.analytics?.summary" class="aw-card"><summary>Souhrnná technická telemetrie · všechna vaše sezení</summary><dl class="lab-definition"><dt>Spojení potvrzená / pokusy</dt><dd>{{ lab.analytics.summary.establishedConnections }} / {{ lab.analytics.summary.connectionAttempts }}</dd><dt>Neočekávaná odpojení</dt><dd>{{ lab.analytics.summary.unexpectedDisconnections }} · {{ lab.analytics.summary.unexpectedDisconnectionRate == null ? 'Podíl není dostupný' : `${(lab.analytics.summary.unexpectedDisconnectionRate * 100).toFixed(1)} %` }}</dd><dt>Úspěšná zotavení / pokusy</dt><dd>{{ lab.analytics.summary.successfulRecoveries }} / {{ lab.analytics.summary.recoveryAttempts }}</dd><dt>Odhad zaznamenané části spotřeby</dt><dd>{{ lab.analytics.summary.estimatedCostUsd == null ? 'Bez cenových podkladů' : `${lab.analytics.summary.estimatedCostUsd.toFixed(4)} USD` }} · pokrytí {{ lab.analytics.summary.costMeasuredSessions }} sezení. Odhad je z dostupné telemetrie; nejde o vyúčtování.</dd></dl><ul v-if="lab.analytics.errors?.length"><li v-for="item in lab.analytics.errors" :key="item.code">{{ item.code }} · {{ item.count }}×</li></ul></details>
        <div class="lab-comparison-select"><AwField id="lab-compare-a" label="Varianta A"><select class="aw-input" id="lab-compare-a" :value="leftId" @change="selectComparison('left', $event)"><option value="">Vyberte test</option><option v-for="item in lab.sessions" :key="item.id" :value="item.id" :disabled="item.id === rightId">{{ item.label }} · {{ item.config.name }}</option></select></AwField><AwField id="lab-compare-b" label="Varianta B"><select class="aw-input" id="lab-compare-b" :value="rightId" @change="selectComparison('right', $event)"><option value="">Vyberte test</option><option v-for="item in lab.sessions" :key="item.id" :value="item.id" :disabled="item.id === leftId">{{ item.label }} · {{ item.config.name }}</option></select></AwField></div>
        <section v-if="comparison" class="aw-card" aria-label="Doporučení z vybraného páru">
          <h3>Doporučení z pozorování</h3><p>{{ comparison.recommendation }}</p>
          <ul v-if="comparison.reasons.length"><li v-for="reason in comparison.reasons" :key="reason">{{ reason }}</li></ul>
          <p v-if="comparison.modelChanged || comparison.voiceChanged">{{ comparison.modelChanged ? 'Liší se také model. ' : '' }}{{ comparison.voiceChanged ? 'Liší se také hlas. ' : '' }}Rozdíly nelze připsat pouze instrukcím persony.</p>
          <p class="lab-muted">Jeden pár není statistický důkaz. Technické hodnoty níže jsou pozorování, nikoli automatický soudce kvality. Doporučení nic nepublikuje.</p>
          <div class="lab-table-scroll"><table><caption>Technická pozorování vybraného páru</caption><thead><tr><th scope="col">Podklad</th><th scope="col">A</th><th scope="col">B</th></tr></thead><tbody>
            <tr><th scope="row">Průměr / p95 odezva</th><td>{{ measured(comparison.left.meanMs, ' ms') }} / {{ measured(comparison.left.p95Ms, ' ms') }} · n={{ comparison.left.latencyCount }}</td><td>{{ measured(comparison.right.meanMs, ' ms') }} / {{ measured(comparison.right.p95Ms, ' ms') }} · n={{ comparison.right.latencyCount }}</td></tr>
            <tr><th scope="row">Stav / události selhání poskytovatele</th><td>{{ comparison.left.status }} / {{ comparison.left.failures }}</td><td>{{ comparison.right.status }} / {{ comparison.right.failures }}</td></tr>
            <tr><th scope="row">Známá část vstupních / výstupních tokenů</th><td>{{ comparison.left.tokens.input ?? 'Neuvedeno' }} / {{ comparison.left.tokens.output ?? 'Neuvedeno' }} · n={{ comparison.left.tokens.inputSamples }}/{{ comparison.left.tokens.outputSamples }}</td><td>{{ comparison.right.tokens.input ?? 'Neuvedeno' }} / {{ comparison.right.tokens.output ?? 'Neuvedeno' }} · n={{ comparison.right.tokens.inputSamples }}/{{ comparison.right.tokens.outputSamples }}</td></tr>
            <tr><th scope="row">Odhad zaznamenané části ceny</th><td>{{ comparison.left.cost.estimatedUsd == null ? 'Bez cenových podkladů' : `${comparison.left.cost.estimatedUsd.toFixed(6)} USD` }} · n={{ comparison.left.cost.samples }}</td><td>{{ comparison.right.cost.estimatedUsd == null ? 'Bez cenových podkladů' : `${comparison.right.cost.estimatedUsd.toFixed(6)} USD` }} · n={{ comparison.right.cost.samples }}</td></tr>
          </tbody></table></div>
          <p class="lab-muted">n u tokenů je počet nahlášených přírůstků vstupu/výstupu. Neuvedeno není nula; nulový počet událostí selhání neprokazuje bezchybný provoz.</p>
          <p class="lab-muted">n u ceny je počet jedinečných platných cenových událostí. Cena pokrývá jen zaznamenané přírůstky; chybějící podklad není nula a odhad není úplná cena ani vyúčtování.</p>
        </section>
        <div v-if="left && right" class="lab-table-scroll" tabindex="0" aria-label="Srovnání vybraných testů"><table><caption>{{ left.label }} / {{ right.label }}</caption><thead><tr><th scope="col">Pozorování</th><th scope="col">A</th><th scope="col">B</th></tr></thead><tbody><tr><th scope="row">Verze</th><td>{{ left.config.name }} · r{{ left.config.revision }}</td><td>{{ right.config.name }} · r{{ right.config.revision }}</td></tr><tr><th scope="row">Model</th><td>{{ left.engine }}</td><td>{{ right.engine }}</td></tr><tr><th scope="row">Platforma</th><td>{{ platformLabel(left.platform) }}</td><td>{{ platformLabel(right.platform) }}</td></tr><tr><th scope="row">Scénář / paměť</th><td>{{ left.scenarioId }} / {{ left.fixtureId }}</td><td>{{ right.scenarioId }} / {{ right.fixtureId }}</td></tr><tr><th scope="row">Tahy</th><td>{{ left.turns.length }}</td><td>{{ right.turns.length }}</td></tr><tr v-for="[key, title] in metrics" :key="key"><th scope="row">{{ title }}</th><td>{{ left.evaluation?.scores[key] ?? 'Nehodnoceno' }}</td><td>{{ right.evaluation?.scores[key] ?? 'Nehodnoceno' }}</td></tr><tr><th scope="row">Komentář</th><td>{{ left.evaluation?.comment || 'Bez komentáře' }}</td><td>{{ right.evaluation?.comment || 'Bez komentáře' }}</td></tr></tbody></table></div>
        <details v-if="lab.analytics?.comparisons.length"><summary>Uložené skupiny porovnání</summary><ul><li v-for="group in lab.analytics.comparisons" :key="group.id">{{ group.id }} · {{ group.sessions.length }} testů</li></ul></details>
      </section>

      <section v-if="lab.admin" v-show="section === 'configuration'" aria-labelledby="lab-configuration-title">
        <h2 id="lab-configuration-title">Konfigurace persony</h2>
        <p>Správa verzí je dostupná pouze administrátorům. Každý test používá neměnný snímek verze; úprava vytvoří nový draft.</p>
        <form class="aw-card lab-config" @submit.prevent="lab.saveDraft">
          <AwField id="lab-config-base" label="Výchozí verze / historie"><select class="aw-input" id="lab-config-base" v-model="lab.configRevisionId" :disabled="lab.busy"><option v-for="version in lab.catalog?.versions" :key="version.id" :value="version.id">{{ version.name }} · r{{ version.revision }} · {{ version.status }}{{ version.id === lab.catalog?.activeRevisionId ? ' · aktivní' : '' }}</option></select></AwField>
          <p v-if="lab.configRevision" class="lab-muted">Vytvořeno {{ date(lab.configRevision.createdAt) }} · {{ lab.configRevision.id }}</p>
          <AwField id="lab-draft-name" label="Název nového draftu"><input class="aw-input" id="lab-draft-name" v-model="lab.draftName" :disabled="lab.busy" required maxlength="100" /></AwField>
          <AwField id="lab-draft-instructions" label="Instrukce persony"><textarea class="aw-input" id="lab-draft-instructions" v-model="lab.draftInstructions" :disabled="lab.busy" required rows="14" maxlength="12000" spellcheck="false" /></AwField>
          <div class="lab-comparison-select"><AwField id="lab-draft-engine" label="Výchozí model pro testování"><select class="aw-input" id="lab-draft-engine" v-model="lab.draftEngine" :disabled="lab.busy"><option value="gpt-live-1">gpt-live-1</option><option value="gpt-realtime-2.1">gpt-realtime-2.1</option></select></AwField><AwField id="lab-draft-voice" label="Výchozí hlas pro testování"><select class="aw-input" id="lab-draft-voice" v-model="lab.draftVoice" :disabled="lab.busy" required><option value="cedar">Cedar</option><option value="marin">Marin</option><option value="coral">Coral</option></select></AwField></div>
          <p class="lab-muted">Teplota není pro tyto konverzační modely nastavitelná.</p>
          <details open><summary>Rozdíl proti výchozí verzi ({{ diff.length }})</summary><p v-if="!diff.length">Žádné změny.</p><div v-for="change in diff" :key="change.label" class="lab-diff"><strong>{{ change.label }}</strong><div><span>Původní</span><pre>{{ change.before }}</pre></div><div><span>Nové</span><pre>{{ change.after }}</pre></div></div></details>
          <AwButton type="submit" variant="primary" :busy="lab.busy" :disabled="!lab.configRevision || !diff.length">Uložit nový draft</AwButton>
        </form>
        <div v-if="lab.configRevision" class="aw-card lab-publish"><h3>Publikovat nebo vrátit verzi</h3><p>Vybráno: <strong>{{ lab.configRevision.name }}</strong>. Publikace nastaví instrukce novým produkčním rozhovorům. Model a hlas zůstávají volbou konkrétního sezení. Existující sezení zachovají svůj snímek.</p><details><summary>Rozdíl vybrané uložené verze proti aktivní ({{ publishedDiff.length }})</summary><p v-if="!publishedDiff.length">Instrukce, model a hlas jsou shodné.</p><div v-for="change in publishedDiff" :key="change.label" class="lab-diff"><strong>{{ change.label }}</strong><div><span>Aktivní</span><pre>{{ change.before }}</pre></div><div><span>Vybrané</span><pre>{{ change.after }}</pre></div></div></details><label class="lab-check"><input v-model="lab.configConfirmed" type="checkbox" :disabled="lab.busy" /><span>Potvrzuji změnu aktivní konfigurace na vybranou verzi.</span></label><p v-if="diff.length" class="lab-muted">Nejprve uložte rozepsané změny jako nový draft. Publikovat lze pouze vybranou uloženou verzi.</p><div class="lab-actions"><AwButton v-if="lab.configRevision.status === 'draft'" variant="primary" :busy="lab.busy" :disabled="!lab.configConfirmed || !!diff.length" @click="lab.publish()">Publikovat vybraný draft</AwButton><AwButton v-else :busy="lab.busy" :disabled="!lab.configConfirmed || !!diff.length || lab.configRevision.id === lab.catalog?.activeRevisionId" @click="lab.publish(true)">Vrátit tuto verzi</AwButton></div></div>
        <details class="aw-card lab-system-diagnostics">
          <summary>Systémová diagnostika · administrátor</summary>
          <p>Technická metadata napříč účty. Načtení nic nespouští a nevolá model. Nejvýše 20 nejnovějších sezení a posledních 20 událostí každého sezení; chybějící událost nepotvrzuje úspěch.</p>
          <div class="lab-actions"><AwButton :busy="lab.diagnosticsLoading" :disabled="!lab.online" @click="lab.loadSystemDiagnostics">Načíst systémovou diagnostiku</AwButton><AwButton :disabled="!lab.systemDiagnostics" @click="lab.downloadSystemDiagnostics">Stáhnout systémovou diagnostiku</AwButton></div>
          <p v-if="!lab.systemDiagnostics && !lab.diagnosticsLoading" class="lab-muted">Data se načtou pouze na výslovný pokyn administrátora.</p>
          <template v-if="lab.systemDiagnostics">
            <dl class="lab-definition"><dt>Sezení / revize v úložišti</dt><dd>{{ lab.systemDiagnostics.storage.sessions ?? 'Neuvedeno' }} / {{ lab.systemDiagnostics.storage.revisions ?? 'Neuvedeno' }}</dd><dt>Aktivní spojení</dt><dd>{{ lab.systemDiagnostics.activeConnections ?? 'Neuvedeno' }}</dd><dt>GPT Live / GPT Realtime</dt><dd>{{ availability(lab.systemDiagnostics.liveAvailable) }} / {{ availability(lab.systemDiagnostics.realtimeAvailable) }}</dd><dt>Izolovaný hardware kontext serveru</dt><dd>{{ availability(lab.systemDiagnostics.hardwareContext) }} · neprokazuje schopnost připojeného firmware</dd></dl>
            <p v-if="lab.systemDiagnostics.storage.sessionsTruncated" class="lab-muted">Seznam sezení je zkrácený; nejde o úplnou historii serveru.</p>
            <p v-if="!lab.systemDiagnostics.storage.recentSessions.length">Server nevrátil žádná technická sezení.</p>
            <article v-for="session in lab.systemDiagnostics.storage.recentSessions" :key="session.sessionId" class="lab-suite-result">
              <h3>Sezení <code>{{ session.sessionId }}</code></h3>
              <dl class="lab-definition"><dt>Stav / prostředí</dt><dd>{{ session.status || 'Neuvedeno' }} / {{ session.testMode === 'hardware' ? 'Skutečný kufr · bez fyzického potvrzení' : session.testMode === 'simulation' ? 'Simulace' : 'Neuvedeno' }}</dd><dt>Model / verze persony</dt><dd>{{ session.engine || 'Neuvedeno' }} / {{ session.personaRevisionId || 'Neuvedeno' }}</dd><dt>Platforma</dt><dd>{{ platformLabel(session.platform) }} · údaj klienta</dd><dt>Začátek / konec</dt><dd>{{ session.createdAt ? date(session.createdAt) : 'Neuvedeno' }} / {{ session.endedAt ? date(session.endedAt) : 'Neukončeno / neuvedeno' }}</dd></dl>
              <details><summary>Technické události ({{ session.events.length }})</summary>
                <p v-if="session.eventsTruncated" class="lab-muted">Události jsou zkrácené na posledních 20; starší průběh zde není zachycený.</p>
                <ol class="lab-timeline"><li v-for="event in session.events" :key="event.id"><time>{{ date(event.createdAt) }}</time><strong>{{ event.type }}</strong><span v-if="event.code">{{ event.code }}</span><span v-if="event.latencyMs != null">{{ event.latencyMs }} ms</span><span v-if="event.decision">{{ event.decision }} · návrh není potvrzení pohybu</span><span v-if="event.compartment != null">Návrh přihrádky {{ event.compartment }}</span><span v-if="event.type === 'tool_result' && event.code === 'simulation_only'">Simulovaný výsledek nástroje, bez důkazu fyzické akce.</span><span v-if="event.type === 'provider_tokens' && event.code === 'observed'">Tokeny vstup / výstup: {{ event.inputTokens ?? 'Neuvedeno' }} / {{ event.outputTokens ?? 'Neuvedeno' }}</span><span v-if="event.type === 'provider_cost' && event.code === 'micro_usd' && event.costMicroUsd != null">Odhad části ceny: {{ (event.costMicroUsd / 1000000).toFixed(6) }} USD · nejde o vyúčtování</span><code v-if="event.providerSessionId">{{ event.providerSessionId }}</code></li></ol>
                <p v-if="!session.events.length">Bez technických událostí; úspěch není potvrzený.</p>
              </details>
            </article>
          </template>
          <p class="lab-muted">Stažení obsahuje pouze vyjmenovaná technická metadata. Neobsahuje identity účtů, přepisy, instrukce, paměť, hodnocení, komentáře, audio, obrázky ani přístupové kontexty. Cizí obsah zde nelze upravovat.</p>
        </details>
      </section>
    </template>
  </div>
</template>

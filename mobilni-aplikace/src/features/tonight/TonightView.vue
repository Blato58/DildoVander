<script setup lang="ts">
import { computed } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import type { CurrentSessionState, MemoryProjection, TonightProjection } from './tonightProjection';
import type { TonightViewModel } from './useTonight';

const props = defineProps<{ model: TonightViewModel }>();
const emit = defineEmits<{
  refresh: [];
  'next-visit': [];
  'quick-finale': [];
  'guest-leave': [];
}>();

const current = computed(() => props.model.projection.session.current);
const sessionStateLabel = computed(() => {
  const state = current.value?.state;
  const labels: Record<CurrentSessionState, string> = {
    starting: 'Kufr se připravuje',
    context_ready: 'Rozhovor se připravuje',
    running: 'Probíhá',
    recovering: 'Obnovuje se',
  };
  return state && state in labels ? labels[state as CurrentSessionState] : 'Stav se ověřuje';
});
const sessionFreshnessLabel = computed(() => freshnessLabel(props.model.projection.session.freshness,
  props.model.projection.session.receivedAtMs));
const memoryFreshnessLabel = computed(() => freshnessLabel(props.model.projection.memory.freshness,
  props.model.projection.memory.receivedAtMs));
const lastReceivedLabel = computed(() => props.model.clientReceivedAtMs === null
  ? 'Čas přijetí serverového snímku relace není známý'
  : `Serverový snímek relace přijat v tomto zařízení ${formatTime(props.model.clientReceivedAtMs)}`);
const deviceStatusLabel = computed(() => {
  const device = props.model.projection.device;
  if (device.freshness === 'fresh') {
    const busy = device.value?.busy === true ? ' · zaneprázdněný' : device.value?.busy === false ? ' · nezaneprázdněný' : '';
    return `${devicePhaseLabel(device.value?.phase)}${busy}`;
  }
  if (device.freshness === 'stale') return `Poslední známé hlášení zařízení: ${devicePhaseLabel(device.value?.phase)} · zastaralé`;
  return 'Stav zařízení neznámý';
});
const networkStatusLabel = computed(() => {
  const network = props.model.projection.network;
  if (network.freshness === 'unknown') return 'Stav sítě neznámý';
  const state = network.value?.state === 'connected' ? 'Připojeno' : 'Odpojeno';
  return network.freshness === 'stale' ? `Poslední známý stav sítě: ${state} · zastaralý` : state;
});
const audioStatusLabel = computed(() => {
  const audio = props.model.projection.audio;
  if (audio.freshness === 'unknown') return 'Fáze hlášeného zvuku neznámá';
  const phase = audioPhaseLabel(audio.value?.phase);
  return audio.freshness === 'stale' ? `Poslední známá fáze hlášeného zvuku: ${phase} · zastaralá` : `Fáze hlášeného zvuku: ${phase}`;
});

function freshnessLabel(freshness: 'fresh' | 'stale' | 'unknown', receivedAtMs?: number): string {
  if (freshness === 'unknown' || receivedAtMs === undefined) return 'Čas přijetí není známý';
  if (freshness === 'stale') return `Zastaralé · přijato ${formatTime(receivedAtMs)}`;
  const ageMs = Math.max(0, props.model.nowMs - receivedAtMs);
  const seconds = Math.floor(ageMs / 1000);
  return seconds === 0 ? 'Přijato právě teď' : `Přijato před ${seconds} s`;
}

function formatTime(value: number | string): string {
  const timestamp = typeof value === 'number' ? value : Date.parse(value);
  if (!Number.isFinite(timestamp)) return 'čas neznámý';
  return new Intl.DateTimeFormat('cs-CZ', { hour: '2-digit', minute: '2-digit', second: '2-digit' }).format(timestamp);
}

function statusLabel(projection: TonightProjection): string {
  const status = projection.session.state;
  if (projection.session.freshness !== 'fresh') return 'Stav rozhovoru se ověřuje';
  if (status === 'no_active_session') return 'Není potvrzen aktivní rozhovor';
  if (status === 'ambiguous') return 'Více možných rozhovorů · stav se ověřuje';
  if (status === 'active') return sessionStateLabel.value;
  return 'Stav rozhovoru se ověřuje';
}

function memoryStatusLabel(memory: MemoryProjection): string {
  if (memory.freshness === 'unknown' || memory.storageStatus === 'unknown') return 'Stav společného úložiště není známý';
  if (memory.freshness === 'stale') return 'Poslední známý stav společného úložiště';
  if (memory.storageStatus === 'full') return 'Plné';
  if (memory.storageStatus === 'warning') return 'Varování';
  return 'Dostupné';
}

function formatBytes(bytes: number): string {
  const mebibytes = new Intl.NumberFormat('cs-CZ', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
    .format(bytes / (1024 * 1024));
  const exact = new Intl.NumberFormat('cs-CZ', { maximumFractionDigits: 0 }).format(bytes);
  return `${mebibytes} MiB (${exact} B)`;
}

function rejectedWritesLabel(count: number | undefined): string {
  if (count === undefined) return 'Počet odmítnutých zápisů není známý';
  const mod10 = count % 10, mod100 = count % 100;
  const noun = count === 1 ? 'odmítnutý zápis' : mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14)
    ? 'odmítnuté zápisy' : 'odmítnutých zápisů';
  return `${new Intl.NumberFormat('cs-CZ').format(count)} ${noun}`;
}

function devicePhaseLabel(value: string | undefined): string {
  return ({ ready: 'Připraveno', running: 'Běží', stopped: 'Zastaveno', disconnected: 'Odpojeno' } as Record<string, string>)[value ?? ''] ?? 'Neznámý stav';
}

function operationLabel(value: string): string {
  const labels: Record<string, string> = {
    'next-visit': 'Další návštěva',
    'quick-finale': 'Krátké finále',
    'guest-leave': 'Host odešel',
  };
  return labels[value] ?? 'Povel';
}

function audioPhaseLabel(value: string | undefined): string {
  return ({
    idle: 'nečinný', starting: 'spouští se', photo_capture: 'snímá fotografii', photo_upload: 'odesílá fotografii',
    photo_wait: 'čeká na fotografii', listening: 'naslouchá', thinking: 'zpracovává', speaking: 'mluví',
    stopping: 'zastavuje se', completed: 'dokončeno', failed: 'selhalo', unavailable: 'nedostupný',
  } as Record<string, string>)[value ?? ''] ?? 'neznámá';
}

const memoryValue = computed(() => props.model.projection.memory);
</script>

<template>
  <section class="page-stack tonight-view" aria-labelledby="tonight-title">
    <header class="tonight-header">
      <div>
        <p class="eyebrow">Obsluha · vybraný kufr {{ model.scope?.deviceId ?? 'není vybraný' }}</p>
        <h1 id="tonight-title">Dnešní provoz</h1>
        <p>Přehled stavu bez zásahu do probíhajícího rozhovoru. Bezpečnostní Stop zůstává dostupný v horní liště aplikace.</p>
      </div>
      <AwButton :busy="model.refreshing" :disabled="model.access !== 'allowed'" aria-label="Obnovit stav" @click="emit('refresh')">
        Obnovit stav
      </AwButton>
    </header>

    <AwNotice v-if="model.access === 'denied'" tone="warning" title="Oprávnění k obsluze není potvrzené">
      Přehled zákaznického rozhovoru a společného úložiště zůstává skrytý.
    </AwNotice>
    <AwNotice v-else-if="model.access === 'unknown'" tone="info" title="Oprávnění k obsluze se ověřuje">
      Zatím se nezobrazují soukromé provozní údaje.
    </AwNotice>
    <AwNotice v-if="model.stale" tone="warning" title="Údaje mohou být zastaralé">
      Obnovte stav před dalším povelem. Znovu se odešle pouze čtení stavu.
    </AwNotice>
    <AwNotice v-if="model.error" tone="warning">Stav se nepodařilo ověřit. Obnovte jej ručně.</AwNotice>

    <section class="aw-card tonight-card" aria-labelledby="tonight-session-title">
      <div class="tonight-card-heading">
        <div>
          <p class="eyebrow">Zákaznická relace</p>
          <h2 id="tonight-session-title">{{ current?.characterName || 'Aktuální host' }}</h2>
        </div>
        <span class="tonight-state">{{ statusLabel(model.projection) }}</span>
      </div>
      <p>{{ sessionFreshnessLabel }}</p>
      <p>{{ lastReceivedLabel }}</p>
      <p v-if="current?.endRequested" class="tonight-pending-end" role="status">
        Požadavek na ukončení čeká. Stav rozhovoru zůstává {{ sessionStateLabel.toLocaleLowerCase('cs-CZ') }}.
      </p>
      <dl class="tonight-facts">
        <div><dt>ID relace</dt><dd>{{ current?.id ?? '—' }}</dd></div>
        <div><dt>ID operace relace</dt><dd>{{ current?.operationId ?? '—' }}</dd></div>
        <div><dt>Backendový stav</dt><dd>{{ sessionStateLabel }}</dd></div>
        <div><dt>Další host</dt><dd>Stav neznámý</dd></div>
        <div><dt>Památka po návštěvě</dt><dd>Stav připravenosti neznámý</dd></div>
      </dl>
    </section>

    <section class="aw-card tonight-card" aria-labelledby="tonight-device-title">
      <div class="tonight-card-heading">
        <div><p class="eyebrow">Pozorované zařízení</p><h2 id="tonight-device-title">Kufr a spojení</h2></div>
      </div>
      <p>{{ deviceStatusLabel }}</p>
      <p>{{ freshnessLabel(model.projection.device.freshness, model.projection.device.receivedAtMs) }}</p>
      <p>Síť: {{ networkStatusLabel }} · {{ freshnessLabel(model.projection.network.freshness, model.projection.network.receivedAtMs) }}</p>
      <p>{{ audioStatusLabel }} · {{ freshnessLabel(model.projection.audio.freshness, model.projection.audio.receivedAtMs) }}. Tato fáze nepotvrzuje slyšitelný zvuk.</p>
    </section>

    <section class="aw-card tonight-card" aria-labelledby="tonight-memory-title">
      <div class="tonight-card-heading">
        <div><p class="eyebrow">Paměť</p><h2 id="tonight-memory-title">Globální archiv všech zařízení a rozhovorů</h2></div>
        <span class="tonight-state">{{ memoryStatusLabel(memoryValue) }}</span>
      </div>
      <p>{{ memoryFreshnessLabel }}</p>
      <dl class="tonight-facts">
        <div><dt>Využitá kapacita</dt><dd>{{ memoryValue.storedBytes === undefined ? 'Neznámá' : formatBytes(memoryValue.storedBytes) }}</dd></div>
        <div><dt>Celková kapacita</dt><dd>{{ memoryValue.capacityBytes === undefined ? 'Neznámá' : formatBytes(memoryValue.capacityBytes) }}</dd></div>
        <div><dt>Odmítnuté zápisy</dt><dd>{{ rejectedWritesLabel(memoryValue.rejectedWrites) }}</dd></div>
      </dl>
      <p v-if="memoryValue.storageIncomplete" class="tonight-incomplete" role="status">Některé zápisy nebyly uloženy. Tato informace zůstává i po uvolnění kapacity.</p>
      <p v-if="memoryValue.lastStorageErrorCode === 'archive_capacity'">
        Poslední odmítnutý zápis se nevešel do tehdy dostupné kapacity<span v-if="memoryValue.lastStorageErrorAt"> · {{ formatTime(memoryValue.lastStorageErrorAt) }}</span>.
      </p>
    </section>

    <section class="aw-card tonight-card" aria-labelledby="tonight-actions-title">
      <p class="eyebrow">Ruční režie</p>
      <h2 id="tonight-actions-title">Zásahy do průběhu</h2>
      <p>Backendové přijetí nepotvrzuje fyzické provedení. Neznámý výsledek se automaticky neopakuje.</p>
      <div class="tonight-actions">
        <AwButton data-action="next-visit" :disabled="!model.canNextVisit" @click="emit('next-visit')">Další návštěva</AwButton>
        <AwButton data-action="quick-finale" :disabled="!model.canSessionCommand" @click="emit('quick-finale')">Krátké finále</AwButton>
        <AwButton data-action="guest-leave" :disabled="!model.canSessionCommand" @click="emit('guest-leave')">Host odešel</AwButton>
      </div>
      <p v-if="model.operation" role="status" data-operation-status>
        {{ operationLabel(model.operation.label) }} · {{ model.operation.status }} · ID {{ model.operation.id }}
      </p>
      <p v-if="model.remoteStop" role="status">Stop přes backend · {{ model.remoteStop.status }} · ID {{ model.remoteStop.id }}</p>
    </section>
  </section>
</template>

<style scoped>
.tonight-view { max-width: 76rem; }
.tonight-header, .tonight-card-heading { display: flex; align-items: start; justify-content: space-between; gap: var(--aw-space-4); }
.tonight-header > div { max-width: 58ch; }
.tonight-header p:not(.eyebrow), .tonight-card > p { color: var(--aw-muted); }
.tonight-card { display: grid; gap: var(--aw-space-3); min-width: 0; }
.tonight-card h2 { margin: 0; }
.tonight-card p { margin: 0; overflow-wrap: anywhere; }
.tonight-state { border: 1px solid var(--aw-border); border-radius: 999px; padding: .35rem .7rem; font-weight: 600; }
.tonight-facts { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 13rem), 1fr)); gap: var(--aw-space-3); margin: 0; }
.tonight-facts div { min-width: 0; }
.tonight-facts dt { color: var(--aw-muted); font-size: .85rem; }
.tonight-facts dd { margin: .25rem 0 0; overflow-wrap: anywhere; }
.tonight-pending-end, .tonight-incomplete { font-weight: 600; }
.tonight-actions { display: flex; flex-wrap: wrap; gap: var(--aw-space-3); }
@media (max-width: 560px) {
  .tonight-header, .tonight-card-heading { flex-direction: column; align-items: stretch; }
  .tonight-header > :deep(button) { align-self: start; }
}
</style>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { RouterLink } from 'vue-router';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountDeletionRequested, offlineSession, signingOut } from '../identity/session';
import { CompletionUpdatesClient, type CompletionUpdate, type CompletionUpdateState } from './completionUpdates';

const props = withDefaults(defineProps<{ locale?: 'cs' | 'en' }>(), { locale: 'cs' });

const copy = computed(() => props.locale === 'en' ? {
  title: 'Your results', refresh: 'Refresh', loading: 'Loading your results…', empty: 'No result updates yet.',
  signedOut: 'Sign in to see results saved to your account.', offline: 'Account results are unavailable in offline mode.',
  failure: 'Results are temporarily unavailable. Try refreshing.', push: 'These updates are available here. System notifications are not required.',
  keepsake: 'Keepsake', portrait: 'Portrait', unread: 'Unread', read: 'Read', markRead: 'Mark as read',
  open: 'Open result', loadMore: 'Load older updates', loadingMore: 'Loading older updates…', moreUnavailable: 'No older updates are available.',
  status: {
    pending: 'Waiting', processing: 'In progress', partial: 'Partially available', ready: 'Ready', failed: 'Could not be completed', unavailable: 'Unavailable',
  } as Record<CompletionUpdateState, string>,
  reason: {
    awaiting_capacity: 'Waiting for available capacity.', paused_preparing: 'Preparation is paused.',
    reconciliation_required: 'The result is being checked.', generation_failed: 'Generation failed. Any available part is shown below.',
    result_unavailable: 'This result is no longer available.',
  } as Record<string, string>,
  story: 'Story available', artwork: 'Artwork available',
  occurred: 'Update received',
} : {
  title: 'Vaše výsledky', refresh: 'Obnovit', loading: 'Načítám vaše výsledky…', empty: 'Zatím tu nejsou žádné aktualizace výsledků.',
  signedOut: 'Pro zobrazení výsledků uložených k účtu se přihlaste.', offline: 'Výsledky účtu nejsou v offline režimu dostupné.',
  failure: 'Výsledky jsou dočasně nedostupné. Zkuste je obnovit.', push: 'Aktualizace najdete zde. Systémová oznámení nejsou potřeba.',
  keepsake: 'Památka', portrait: 'Portrét', unread: 'Nepřečteno', read: 'Přečteno', markRead: 'Označit jako přečtené',
  open: 'Otevřít výsledek', loadMore: 'Načíst starší aktualizace', loadingMore: 'Načítám starší aktualizace…', moreUnavailable: 'Starší aktualizace nejsou dostupné.',
  status: {
    pending: 'Čeká', processing: 'Probíhá', partial: 'Částečně dostupné', ready: 'Připraveno', failed: 'Nepodařilo se dokončit', unavailable: 'Nedostupné',
  } as Record<CompletionUpdateState, string>,
  reason: {
    awaiting_capacity: 'Čeká se na volnou kapacitu.', paused_preparing: 'Příprava je pozastavená.',
    reconciliation_required: 'Výsledek se ověřuje.', generation_failed: 'Vytvoření selhalo. Dostupná část je uvedená níže.',
    result_unavailable: 'Tento výsledek už není dostupný.',
  } as Record<string, string>,
  story: 'Příběh je dostupný', artwork: 'Ilustrace je dostupná',
  occurred: 'Aktualizováno',
});

const updates = ref<CompletionUpdate[]>([]);
const busy = ref(false);
const loadingMore = ref(false);
const online = ref(typeof navigator !== 'undefined' && navigator.onLine);
const error = ref(false);
const nextCursor = ref<string | null>(null);
const moreFailed = ref(false);
const accountId = () => account.value?.id ?? null;
const canRead = () => Boolean(account.value && online.value && !offlineSession.value && !signingOut.value && !accountDeletionRequested.value);
let client = new CompletionUpdatesClient(accountId, canRead);
let requestGeneration = 0;
let contextGeneration = 0;
let automaticPolls = 0;
let pollTimer: ReturnType<typeof setTimeout> | undefined;
let active = false;
let activeAccountId: string | null = null;
let disposed = false;

const dateFormatter = computed(() => new Intl.DateTimeFormat(props.locale === 'en' ? 'en' : 'cs', { dateStyle: 'medium', timeStyle: 'short' }));
function formatDate(value: string): string {
  const date = new Date(value);
  return Number.isFinite(date.getTime()) ? dateFormatter.value.format(date) : '';
}
function resultLocation(item: CompletionUpdate) {
  return { path: '/keepsake/', query: { account: '1', k: item.resultLocator } };
}
function clearPoll(): void {
  if (pollTimer) clearTimeout(pollTimer);
  pollTimer = undefined;
}
function stopAndClear(): void {
  contextGeneration++;
  requestGeneration++;
  clearPoll();
  client.invalidate();
  updates.value = [];
  nextCursor.value = null;
  busy.value = false;
  loadingMore.value = false;
  error.value = false;
  moreFailed.value = false;
  automaticPolls = 0;
}
function hasPendingUpdates(): boolean {
  return updates.value.some(item => item.state === 'pending'
    || item.state === 'processing'
    || (item.state === 'partial' && item.reason !== 'generation_failed'));
}
function schedulePoll(): void {
  clearPoll();
  if (!active || disposed || !canRead() || !navigator.onLine || document.hidden || automaticPolls >= 12 || !hasPendingUpdates()) return;
  pollTimer = setTimeout(() => { void load(true); }, 5000);
}
function mergeOlderPage(pageItems: CompletionUpdate[]): void {
  updates.value = deduplicateUpdates([...updates.value, ...pageItems]).slice(0, 200);
}
function deduplicateUpdates(rows: CompletionUpdate[]): CompletionUpdate[] {
  const newest = new Map<string, CompletionUpdate>();
  for (const item of rows) {
    const key = `${item.kind}:${item.resultLocator}`;
    const existing = newest.get(key);
    if (!existing || item.sequence > existing.sequence) newest.set(key, item);
  }
  return [...newest.values()].sort((a, b) => b.sequence - a.sequence);
}

async function load(automatic = false): Promise<void> {
  if (!active || disposed || !canRead() || !navigator.onLine || document.hidden || busy.value || loadingMore.value) return;
  clearPoll();
  if (automatic) automaticPolls++;
  else automaticPolls = 0;
  const generation = ++requestGeneration;
  if (!automatic) {
    client.invalidate();
    nextCursor.value = null;
    moreFailed.value = false;
  }
  busy.value = true;
  error.value = false;
  try {
    const page = await client.readPage();
    if (generation !== requestGeneration || !canRead()) return;
    updates.value = deduplicateUpdates(page.items);
    nextCursor.value = page.nextCursor;
  } catch {
    if (generation === requestGeneration && canRead()) error.value = true;
  } finally {
    if (generation === requestGeneration) {
      busy.value = false;
      schedulePoll();
    }
  }
}

async function loadMore(): Promise<void> {
  const cursor = nextCursor.value;
  if (!cursor || updates.value.length >= 200 || loadingMore.value || busy.value || !canRead() || !navigator.onLine || document.hidden) return;
  const generation = requestGeneration;
  loadingMore.value = true;
  moreFailed.value = false;
  try {
    const page = await client.readPage(cursor);
    if (generation !== requestGeneration || !canRead()) return;
    mergeOlderPage(page.items);
    nextCursor.value = page.nextCursor;
  } catch {
    if (generation === requestGeneration && canRead()) moreFailed.value = true;
  } finally {
    if (generation === requestGeneration) {
      loadingMore.value = false;
      schedulePoll();
    }
  }
}

async function markRead(item: CompletionUpdate): Promise<void> {
  if (!canRead() || item.readAt !== null || !updates.value.some(row => row.id === item.id)) return;
  const generation = contextGeneration;
  try {
    await client.markRead(item.id);
    if (generation !== contextGeneration || !canRead()) return;
    const row = updates.value.find(candidate => candidate.id === item.id);
    if (row) row.readAt = new Date().toISOString();
  } catch {
    if (generation === contextGeneration && canRead()) error.value = true;
  }
}

function contextChanged(): void {
  const allowed = canRead();
  if (!allowed) {
    active = false;
    activeAccountId = null;
    stopAndClear();
    return;
  }
  if (active && activeAccountId !== accountId()) {
    active = false;
    activeAccountId = null;
    stopAndClear();
  }
  if (!active) {
    active = true;
    activeAccountId = accountId();
    void load();
  }
}

function onVisibilityChange(): void {
  clearPoll();
  if (!document.hidden && active && canRead() && online.value) {
    automaticPolls = 0;
    void load(true);
  }
}
function onConnectivityChange(): void {
  online.value = navigator.onLine;
  if (!online.value) clearPoll();
}

watch([() => account.value?.id, offlineSession, signingOut, accountDeletionRequested, online], contextChanged, { flush: 'sync' });
onMounted(() => {
  online.value = navigator.onLine;
  document.addEventListener('visibilitychange', onVisibilityChange);
  window.addEventListener('online', onConnectivityChange);
  window.addEventListener('offline', onConnectivityChange);
  contextChanged();
});
onBeforeUnmount(() => {
  disposed = true;
  active = false;
  stopAndClear();
  document.removeEventListener('visibilitychange', onVisibilityChange);
  window.removeEventListener('online', onConnectivityChange);
  window.removeEventListener('offline', onConnectivityChange);
});
</script>

<template>
  <section class="completion-updates aw-card" aria-labelledby="completion-updates-title">
    <header class="completion-updates__header">
      <div>
        <h2 id="completion-updates-title">{{ copy.title }}</h2>
        <p>{{ copy.push }}</p>
      </div>
      <AwButton :busy="busy" :disabled="!canRead() || !online" @click="load()">{{ copy.refresh }}</AwButton>
    </header>
    <AwNotice v-if="!account" tone="info" :live="false">{{ copy.signedOut }}</AwNotice>
    <AwNotice v-else-if="offlineSession || signingOut || accountDeletionRequested" tone="warning" :live="false">{{ offlineSession ? copy.offline : copy.signedOut }}</AwNotice>
    <AwNotice v-else-if="!online" tone="warning" :live="false">{{ copy.offline }}</AwNotice>
    <AwNotice v-else-if="error" tone="warning" :live="true">{{ copy.failure }}</AwNotice>
    <p v-else-if="busy && !updates.length" class="completion-updates__status" role="status">{{ copy.loading }}</p>
    <p v-else-if="!updates.length" class="completion-updates__empty">{{ copy.empty }}</p>
    <ol v-else class="completion-updates__list">
      <li v-for="item in updates" :key="item.id" :class="{ 'completion-updates__item--unread': !item.readAt }" class="completion-updates__item">
        <div class="completion-updates__content">
          <div class="completion-updates__labels">
            <strong>{{ item.kind === 'keepsake' ? copy.keepsake : copy.portrait }}</strong>
            <span class="completion-updates__state">{{ copy.status[item.state] }}</span>
            <span>{{ item.readAt ? copy.read : copy.unread }}</span>
          </div>
          <p v-if="item.reason" class="completion-updates__reason">{{ copy.reason[item.reason] }}</p>
          <ul v-if="item.hasStory || item.hasArtwork" class="completion-updates__parts">
            <li v-if="item.hasStory">{{ copy.story }}</li>
            <li v-if="item.hasArtwork">{{ copy.artwork }}</li>
          </ul>
          <time :datetime="item.occurredAt">{{ copy.occurred }}: {{ formatDate(item.occurredAt) }}</time>
          <div class="completion-updates__actions">
            <RouterLink :to="resultLocation(item)">{{ copy.open }}</RouterLink>
            <AwButton v-if="!item.readAt" :disabled="busy || loadingMore" @click="markRead(item)">{{ copy.markRead }}</AwButton>
          </div>
        </div>
      </li>
    </ol>
    <AwNotice v-if="moreFailed" tone="warning" :live="true">{{ copy.failure }}</AwNotice>
    <AwButton v-if="nextCursor && updates.length < 200" :busy="loadingMore" :disabled="busy || loadingMore" @click="loadMore">{{ loadingMore ? copy.loadingMore : copy.loadMore }}</AwButton>
    <p v-else-if="!nextCursor && updates.length" class="completion-updates__end">{{ copy.moreUnavailable }}</p>
  </section>
</template>

<style scoped>
.completion-updates { display: grid; gap: 1rem; min-width: 0; }
.completion-updates__header, .completion-updates__labels, .completion-updates__actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: .65rem 1rem; }
.completion-updates__header h2 { margin-bottom: .35rem; }
.completion-updates__header p, .completion-updates__reason, .completion-updates__content time, .completion-updates__end { margin: 0; color: var(--aw-muted); }
.completion-updates__list { list-style: none; display: grid; gap: .75rem; margin: 0; padding: 0; }
.completion-updates__item { min-width: 0; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); padding: 1rem; }
.completion-updates__item--unread { border-color: var(--aw-accent); }
.completion-updates__content { display: grid; gap: .7rem; min-width: 0; }
.completion-updates__labels { justify-content: flex-start; }
.completion-updates__state { padding: .15rem .55rem; border-radius: 999px; background: var(--aw-accent-soft); color: var(--aw-ivory); }
.completion-updates__parts { margin: 0; padding-inline-start: 1.25rem; }
.completion-updates__actions { justify-content: flex-start; }
.completion-updates__actions a { display: inline-flex; align-items: center; min-height: 44px; }
.completion-updates__status, .completion-updates__empty { margin: 0; }
@media (max-width: 520px) {
  .completion-updates__header { align-items: flex-start; }
  .completion-updates__header > :first-child { min-width: 0; }
  .completion-updates__actions { align-items: flex-start; }
}
</style>

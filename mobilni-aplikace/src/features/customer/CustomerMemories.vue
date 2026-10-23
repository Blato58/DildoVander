<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { account, accountDeletionRequested } from '../identity/session';
import { CustomerMemoryError, mutateCustomerMemory, readCustomerMemories, type CustomerMemoryItem, type CustomerMemoryList } from './customerMemory';

const headingId = useId();
const snapshot = ref<CustomerMemoryList | null>(null);
const busy = ref(false);
const verified = ref(false);
const notice = ref<CustomerCopyKey | ''>('');
const editor = ref<{ item: CustomerMemoryItem; text: string } | null>(null);
const forgetting = ref<CustomerMemoryItem | null>(null);
const page = ref(0);
const pageSize = 25;
const pageCount = computed(() => Math.max(1, Math.ceil((snapshot.value?.items.length ?? 0) / pageSize)));
const displayedItems = computed(() => snapshot.value?.items.slice(page.value * pageSize, (page.value + 1) * pageSize) ?? []);
const online = ref(typeof navigator === 'undefined' || navigator.onLine);
const visible = ref(typeof document === 'undefined' || document.visibilityState !== 'hidden');
const eligible = computed(() => Boolean(account.value && !accountDeletionRequested.value && online.value && visible.value));
const canAct = computed(() => eligible.value && verified.value && !busy.value);
let generation = 0;
let disposed = false;
let pending: AbortController | undefined;

function invalidate(clear = false): void {
  generation++;
  pending?.abort();
  pending = undefined;
  busy.value = false;
  verified.value = false;
  editor.value = null;
  forgetting.value = null;
  if (clear) { snapshot.value = null; notice.value = ''; page.value = 0; }
}

function errorCopy(error: unknown, mutation: boolean): CustomerCopyKey {
  if (error instanceof CustomerMemoryError) {
    if (error.status === 401 || error.status === 404) return 'memories.error-access';
    if (error.code === 'memory_revision_mismatch') return 'memories.error-revision';
    if (error.code === 'archive_capacity') return 'memories.error-capacity';
    if (error.status === 429) return 'memories.error-limit';
  }
  return mutation
    ? 'memories.error-mutation-unknown'
    : 'memories.error-read';
}

async function refresh(): Promise<void> {
  if (!eligible.value || busy.value) return;
  invalidate();
  const captured = generation;
  const accountId = account.value?.id;
  const controller = pending = new AbortController();
  const current = () => !disposed && captured === generation && eligible.value && account.value?.id === accountId;
  busy.value = true;
  notice.value = '';
  try {
    const result = await readCustomerMemories(controller.signal, current);
    if (!current()) return;
    snapshot.value = result;
    page.value = Math.min(page.value, pageCount.value - 1);
    verified.value = true;
  } catch (error) {
    if (!current()) return;
    if (error instanceof CustomerMemoryError && (error.status === 401 || error.status === 404)) snapshot.value = null;
    notice.value = errorCopy(error, false);
  } finally {
    if (current()) { busy.value = false; pending = undefined; }
  }
}

async function change(item: CustomerMemoryItem, value: { text: string } | { confirmed: true }): Promise<void> {
  if (!canAct.value || !snapshot.value?.items.some(row => row.id === item.id && row.revision === item.revision)) return;
  const captured = generation;
  const accountId = account.value?.id;
  const controller = pending = new AbortController();
  const current = () => !disposed && captured === generation && eligible.value && account.value?.id === accountId;
  busy.value = true;
  verified.value = false;
  editor.value = null;
  forgetting.value = null;
  notice.value = '';
  try {
    await mutateCustomerMemory(item, value, controller.signal, current);
    if (!current()) return;
    busy.value = false;
    await refresh();
  } catch (error) {
    if (current()) {
      if (error instanceof CustomerMemoryError && (error.status === 401 || error.status === 404)) snapshot.value = null;
      notice.value = errorCopy(error, true);
    }
  } finally {
    if (current()) { busy.value = false; pending = undefined; }
  }
}

function environmentChanged(): void {
  online.value = navigator.onLine;
  visible.value = document.visibilityState !== 'hidden';
}

function movePage(offset: number): void {
  page.value = Math.max(0, Math.min(pageCount.value - 1, page.value + offset));
  editor.value = null;
  forgetting.value = null;
}

watch(() => [account.value?.id, accountDeletionRequested.value], () => {
  invalidate(true);
  void refresh();
});
watch(eligible, value => {
  if (value) void refresh();
  else invalidate();
});
onMounted(() => {
  window.addEventListener('online', environmentChanged);
  window.addEventListener('offline', environmentChanged);
  document.addEventListener('visibilitychange', environmentChanged);
  void refresh();
});
onBeforeUnmount(() => {
  disposed = true;
  invalidate(true);
  window.removeEventListener('online', environmentChanged);
  window.removeEventListener('offline', environmentChanged);
  document.removeEventListener('visibilitychange', environmentChanged);
});
</script>

<template>
  <section class="customer-memories" :aria-labelledby="headingId">
    <h2 :id="headingId">{{ customerCopy.text('memories.title') }}</h2>
    <p>{{ customerCopy.text('memories.intro') }}</p>
    <AwNotice v-if="!account || accountDeletionRequested" tone="info">{{ customerCopy.text('memories.account-required') }}</AwNotice>
    <AwNotice v-else-if="!online" tone="warning">{{ customerCopy.text('memories.offline') }}</AwNotice>
    <AwNotice v-if="notice" tone="warning" role="status">{{ customerCopy.text(notice) }}</AwNotice>
    <p v-if="snapshot && !verified" role="status">{{ customerCopy.text('memories.last-loaded') }}</p>
    <AwButton :disabled="!eligible" :busy="busy" @click="refresh">{{ customerCopy.text('memories.refresh') }}</AwButton>
    <p v-if="verified && snapshot?.items.length === 0">{{ customerCopy.text('memories.none-readable-detail') }}</p>
    <ul v-if="snapshot?.items.length" class="customer-memories__list">
      <li v-for="item in displayedItems" :key="`${item.id}:${item.revision}`">
        <p class="customer-memories__text">{{ item.text }}</p>
        <p>{{ customerCopy.text(item.kind === 'ConfirmedSelection' ? 'memories.fact-confirmed' : 'memories.fact-user-reported') }}</p>
        <template v-if="editor?.item === item">
          <label>{{ customerCopy.text('memories.correction-label') }} <textarea v-model="editor.text" rows="3" :disabled="!canAct" /></label>
          <AwButton :disabled="!canAct || !editor.text.trim()" @click="change(item, { text: editor!.text })">{{ customerCopy.text('memories.save-correction') }}</AwButton>
          <AwButton @click="editor = null">{{ customerCopy.text('memories.cancel-correction') }}</AwButton>
        </template>
        <template v-else-if="forgetting === item">
          <p>{{ customerCopy.text('memories.forget-confirmation') }}</p>
          <AwButton variant="danger" :disabled="!canAct" @click="change(item, { confirmed: true })">{{ customerCopy.text('memories.confirm-forget-specific') }}</AwButton>
          <AwButton @click="forgetting = null">{{ customerCopy.text('memories.cancel') }}</AwButton>
        </template>
        <template v-else>
          <AwButton v-if="item.canCorrect" :disabled="!canAct" @click="editor = { item, text: item.text }; forgetting = null">{{ customerCopy.text('memories.correct') }}</AwButton>
          <AwButton v-if="item.canForget" :disabled="!canAct" @click="forgetting = item; editor = null">{{ customerCopy.text('memories.forget') }}</AwButton>
        </template>
      </li>
    </ul>
    <nav v-if="pageCount > 1" :aria-label="customerCopy.text('memories.pages-aria')">
      <AwButton :disabled="page === 0 || busy" @click="movePage(-1)">{{ customerCopy.text('memories.previous-page') }}</AwButton>
      <span aria-live="polite">{{ customerCopy.text('memories.page-info', { page: page + 1, pages: pageCount, count: snapshot?.items.length ?? 0 })}}</span>
      <AwButton :disabled="page >= pageCount - 1 || busy" @click="movePage(1)">{{ customerCopy.text('memories.next-page') }}</AwButton>
    </nav>
  </section>
</template>

<style scoped>
.customer-memories { display: grid; gap: var(--aw-space-3, 1rem); min-width: 0; }
.customer-memories__list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--aw-space-4, 1.5rem); }
.customer-memories__list li { padding: var(--aw-space-3, 1rem); border: 1px solid var(--aw-border, currentColor); border-radius: var(--aw-radius-card, .75rem); overflow-wrap: anywhere; }
.customer-memories__text { white-space: pre-wrap; }
textarea { display: block; width: 100%; box-sizing: border-box; font: inherit; color: inherit; background: transparent; }
</style>

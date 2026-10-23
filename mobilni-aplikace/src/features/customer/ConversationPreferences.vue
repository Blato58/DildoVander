<script setup lang="ts">
import { onBeforeUnmount, ref, useId, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
import { account, accountRequest } from '../identity/session';
import {
  conversationPreferencePath, parseConversationPreferences,
  type ConversationLanguage, type ConversationPreferenceView, type ConversationTone,
} from './conversationPreferences';

const props = defineProps<{ profileId: string; profileRevision: number }>();
const instanceId = `conversation-preferences-${useId()}`;
const current = ref<ConversationPreferenceView | null>(null);
const language = ref<ConversationLanguage>('cs-CZ');
const tone = ref<ConversationTone>('roast');
const busy = ref(false);
const saved = ref(false);
const error = ref<CustomerCopyKey | null>(null);
let request: AbortController | undefined;
let generation = 0;

async function loadOrSave(save = false) {
  const accountId = account.value?.id;
  if (!accountId || busy.value || (save && !current.value)) return;
  const attempt = ++generation;
  const profileId = props.profileId;
  const profileRevision = props.profileRevision;
  const controller = new AbortController();
  request = controller;
  busy.value = true;
  saved.value = false;
  error.value = null;
  const body = save ? {
    schemaVersion: 1, profileId, profileRevision,
    expectedPreferenceRevision: current.value!.preferenceRevision,
    conversationLanguage: language.value, tone: tone.value,
  } : undefined;
  try {
    const response = await accountRequest<unknown>(conversationPreferencePath, save ? 'PUT' : 'GET', body, controller.signal);
    if (attempt !== generation || controller.signal.aborted || account.value?.id !== accountId) return;
    const value = parseConversationPreferences(response, profileId, profileRevision);
    if (!value) throw new Error('Invalid conversation preferences');
    current.value = value;
    language.value = value.conversationLanguage;
    tone.value = value.tone;
    saved.value = save && value.persisted;
  } catch (failure) {
    if (attempt !== generation || controller.signal.aborted || account.value?.id !== accountId) return;
    current.value = null;
    const status = failure && typeof failure === 'object' && 'status' in failure ? failure.status : undefined;
    error.value = status === 409 ? 'preferences.conflict' : 'preferences.error';
  } finally {
    if (attempt === generation) { busy.value = false; request = undefined; }
  }
}

watch(() => [account.value?.id, props.profileId, props.profileRevision] as const, () => {
  ++generation;
  request?.abort();
  request = undefined;
  current.value = null;
  language.value = 'cs-CZ';
  tone.value = 'roast';
  saved.value = false;
  error.value = null;
  busy.value = false;
  void loadOrSave();
}, { immediate: true, flush: 'sync' });

onBeforeUnmount(() => { ++generation; request?.abort(); });
</script>

<template>
  <section class="aw-card page-stack" :aria-labelledby="`${instanceId}-title`">
    <h2 :id="`${instanceId}-title`">{{ customerCopy.text('preferences.title') }}</h2>
    <p>{{ customerCopy.text('preferences.intro') }}</p>
    <p v-if="busy && !current" role="status">{{ customerCopy.text('preferences.loading') }}</p>
    <AwNotice v-if="error" tone="danger" role="alert">{{ customerCopy.text(error) }}</AwNotice>
    <AwNotice v-if="saved" tone="success" role="status">{{ customerCopy.text('preferences.saved') }}</AwNotice>
    <form v-if="current" class="preference-form" @submit.prevent="loadOrSave(true)">
      <AwField :id="`${instanceId}-language`" :label="customerCopy.text('preferences.language')">
        <select :id="`${instanceId}-language`" v-model="language" class="aw-select" :disabled="busy">
          <option value="cs-CZ" lang="cs">Čeština</option>
          <option value="en-US" lang="en">English</option>
        </select>
      </AwField>
      <AwField :id="`${instanceId}-tone`" :label="customerCopy.text('preferences.tone')" :hint="customerCopy.text('preferences.limits')" v-slot="field">
        <select :id="field.id" v-model="tone" class="aw-select" :aria-describedby="field.describedby" :disabled="busy">
          <option value="gentle">{{ customerCopy.text('preferences.gentle') }}</option>
          <option value="cheeky">{{ customerCopy.text('preferences.cheeky') }}</option>
          <option value="roast">{{ customerCopy.text('preferences.roast') }}</option>
        </select>
      </AwField>
      <p v-if="!current.persisted" class="aw-muted">{{ customerCopy.text('preferences.defaults') }}</p>
      <AwButton type="submit" :busy="busy" :disabled="busy">{{ customerCopy.text('preferences.save') }}</AwButton>
    </form>
    <AwButton v-else-if="error" variant="secondary" :disabled="busy" @click="loadOrSave()">{{ customerCopy.text('preferences.retry') }}</AwButton>
  </section>
</template>

<style scoped>
.preference-form { display: grid; gap: 1rem; }
.preference-form select { width: 100%; }
.preference-form button { justify-self: start; }
</style>

<script setup lang="ts">
import { nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwDialog from '../../components/AwDialog.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { customerCopy } from '../../application/customerCopy';

export interface NewCharacterDraft {
  name: string;
  world: string;
  version: string;
  description: string;
  language: string;
  openAiConsent: boolean;
}

const props = defineProps<{
  open: boolean;
  busy: boolean;
  error?: string;
  currentCharacterName?: string;
  activeSession?: boolean;
}>();

const emit = defineEmits<{
  cancel: [];
  confirm: [draft: NewCharacterDraft];
}>();

const draft = reactive<NewCharacterDraft>(blankDraft());
const formElement = ref<HTMLFormElement>();
const useNativeDialog = typeof HTMLDialogElement !== 'undefined'
  && typeof HTMLDialogElement.prototype.showModal === 'function'
  && typeof HTMLDialogElement.prototype.close === 'function';
let previousInlineFocus: HTMLElement | null = null;
let cancelRequested = false;

watch(() => props.open, (open, wasOpen) => {
  if (open && !wasOpen) {
    Object.assign(draft, blankDraft());
    cancelRequested = false;
  }
}, { flush: 'sync' });

// Older supported iOS versions use an inline form, without claiming modality.
// Keep the same explicit identity decision and return focus when it is canceled.
watch(() => props.open, async (open, wasOpen, onCleanup) => {
  if (useNativeDialog) return;
  let canceled = false;
  onCleanup(() => { canceled = true; });
  if (open && !wasOpen) {
    previousInlineFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    await nextTick();
    if (!canceled && props.open) formElement.value?.querySelector<HTMLElement>('input')?.focus();
  } else if (!open && previousInlineFocus?.isConnected) {
    previousInlineFocus.focus();
  }
}, { flush: 'post', immediate: true });
onBeforeUnmount(() => {
  if (!useNativeDialog && previousInlineFocus?.isConnected) previousInlineFocus.focus();
});

function cancel(): void {
  if (props.busy || cancelRequested) return;
  cancelRequested = true;
  emit('cancel');
}

function confirm(): void {
  if (props.busy) return;
  emit('confirm', { ...draft });
}

function blankDraft(): NewCharacterDraft {
  return {
    name: '',
    world: '',
    version: '',
    description: '',
    language: '',
    openAiConsent: false,
  };
}
</script>

<template>
  <component :is="useNativeDialog ? AwDialog : 'section'" v-if="useNativeDialog || open"
    v-bind="useNativeDialog ? { open, title: customerCopy.text('new-character.title') } : { class: 'aw-card', 'aria-label': customerCopy.text('new-character.title') }"
    @close="cancel">
    <h2 v-if="!useNativeDialog">{{ customerCopy.text('new-character.title') }}</h2>
    <form ref="formElement" class="new-character-dialog" :aria-busy="busy || undefined" @submit.prevent="confirm">
      <p>{{ customerCopy.text('new-character.intro') }}</p>
      <p v-if="currentCharacterName" class="aw-muted">{{ customerCopy.text('new-character.current', { name: currentCharacterName }) }}</p>
      <p class="aw-muted">{{ customerCopy.text('new-character.unsaved') }}</p>
      <AwNotice v-if="activeSession" tone="info">{{ customerCopy.text('new-character.active-session') }}</AwNotice>
      <p v-if="busy" role="status">{{ customerCopy.text('new-character.saving') }}</p>

      <AwNotice v-if="error" tone="danger">{{ error }}</AwNotice>

      <AwField id="new-character-name" :label="customerCopy.text('new-character.name')">
        <input id="new-character-name" v-model="draft.name" class="aw-input" maxlength="120" required :disabled="busy" />
      </AwField>
      <AwField id="new-character-world" :label="customerCopy.text('new-character.world')">
        <input id="new-character-world" v-model="draft.world" class="aw-input" maxlength="160" required :disabled="busy" />
      </AwField>
      <AwField id="new-character-version" :label="customerCopy.text('new-character.version')">
        <input id="new-character-version" v-model="draft.version" class="aw-input" maxlength="120" :disabled="busy" />
      </AwField>
      <AwField id="new-character-description" :label="customerCopy.text('new-character.description')">
        <textarea id="new-character-description" v-model="draft.description" class="aw-input" maxlength="1200" rows="4" :disabled="busy" />
      </AwField>
      <AwField id="new-character-language" :label="customerCopy.text('new-character.language')">
        <input id="new-character-language" v-model="draft.language" class="aw-input" maxlength="35" :disabled="busy" />
      </AwField>
      <AwField id="new-character-openai-consent" :label="customerCopy.text('new-character.optional-ai')">
        <label class="new-character-consent">
          <input id="new-character-openai-consent" v-model="draft.openAiConsent" type="checkbox" :disabled="busy" />
          {{ customerCopy.text('new-character.consent') }}
        </label>
      </AwField>

      <div class="new-character-actions">
        <AwButton type="submit" variant="primary" :busy="busy">{{ customerCopy.text('new-character.create') }}</AwButton>
        <AwButton type="button" variant="secondary" :disabled="busy" @click="cancel">{{ customerCopy.text('new-character.cancel') }}</AwButton>
      </div>
    </form>
  </component>
</template>

<style scoped>
.new-character-dialog { display: grid; gap: 1rem; }
.new-character-dialog > p { margin: 0; }
.new-character-dialog input[type='checkbox'] { min-width: 1.25rem; min-height: 1.25rem; margin: .15rem .5rem 0 0; vertical-align: top; }
.new-character-consent { display: block; line-height: 1.5; }
.new-character-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-top: .5rem; }
@media (max-width: 32rem) {
  .new-character-actions { display: grid; }
  .new-character-actions :deep(.aw-button) { width: 100%; }
}
</style>

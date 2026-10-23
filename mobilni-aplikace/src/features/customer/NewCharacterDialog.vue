<script setup lang="ts">
import { nextTick, onBeforeUnmount, reactive, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwDialog from '../../components/AwDialog.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';

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
    v-bind="useNativeDialog ? { open, title: 'Vytvořit novou postavu' } : { class: 'aw-card', 'aria-label': 'Vytvořit novou postavu' }"
    @close="cancel">
    <h2 v-if="!useNativeDialog">Vytvořit novou postavu</h2>
    <form ref="formElement" class="new-character-dialog" :aria-busy="busy || undefined" @submit.prevent="confirm">
      <p>
        Nová postava vytvoří samostatnou identitu. Historie rozhovorů a památky
        předchozí postavy zůstanou uložené.
      </p>
      <p v-if="currentCharacterName" class="aw-muted">
        Aktuální postava: {{ currentCharacterName }}. Její úprava mění stávající profil;
        tato volba založí nový.
      </p>
      <p class="aw-muted">Neuložené úpravy aktuální postavy se do nové nepřenesou. Volbou Zrušit se k nim vrátíte.</p>
      <AwNotice v-if="activeSession" tone="info">Existující rozhovor dál používá svou původní postavu. Nová postava bude vybraná pro další rozhovor.</AwNotice>
      <p v-if="busy" role="status">Ukládám novou postavu. Vyčkejte na potvrzení serveru.</p>

      <AwNotice v-if="error" tone="danger">{{ error }}</AwNotice>

      <AwField id="new-character-name" label="Jméno postavy">
        <input id="new-character-name" v-model="draft.name" class="aw-input" maxlength="120" required :disabled="busy" />
      </AwField>
      <AwField id="new-character-world" label="Film, seriál, hra nebo kniha">
        <input id="new-character-world" v-model="draft.world" class="aw-input" maxlength="160" required :disabled="busy" />
      </AwField>
      <AwField id="new-character-version" label="Verze nebo období (nepovinné)">
        <input id="new-character-version" v-model="draft.version" class="aw-input" maxlength="120" :disabled="busy" />
      </AwField>
      <AwField id="new-character-description" label="Co je pro vás důležité? (nepovinné)">
        <textarea id="new-character-description" v-model="draft.description" class="aw-input" maxlength="1200" rows="4" :disabled="busy" />
      </AwField>
      <AwField id="new-character-language" label="Jazyk (nepovinné)">
        <input id="new-character-language" v-model="draft.language" class="aw-input" maxlength="35" :disabled="busy" />
      </AwField>
      <AwField id="new-character-openai-consent" label="Volitelná příprava pomocí AI">
        <label class="new-character-consent">
          <input id="new-character-openai-consent" v-model="draft.openAiConsent" type="checkbox" :disabled="busy" />
          Souhlasím s odesláním údajů této nové postavy společnosti OpenAI pro přípravu informací
          a nápadů k rozhovoru, případně s vyhledáním veřejných zdrojů. Bez zaškrtnutí uložíte
          profil bez AI přípravy.
        </label>
      </AwField>

      <div class="new-character-actions">
        <AwButton type="submit" variant="primary" :busy="busy">Vytvořit novou postavu</AwButton>
        <AwButton type="button" variant="secondary" :disabled="busy" @click="cancel">Zrušit</AwButton>
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

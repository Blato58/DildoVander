<script setup lang="ts">
import { onBeforeUnmount, onMounted, shallowRef, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountTransport } from '../identity/session';
import { AmbientPreviewPlayer, type AmbientPreviewState } from './AmbientPreviewPlayer';

const props = defineProps<{ deviceId: string; accountId: string; enabled: boolean }>();
const state = shallowRef<AmbientPreviewState>({ busy: false, message: '', error: '', text: '', synthetic: false });
let player: AmbientPreviewPlayer | undefined;
function stop() { player?.stop(); }
watch(() => [props.deviceId, props.accountId, props.enabled], () => {
  player?.stop('');
  player = props.enabled && props.deviceId && props.accountId
    ? new AmbientPreviewPlayer(props.deviceId, accountTransport, value => { state.value = value; }) : undefined;
}, { immediate: true, flush: 'sync' });
function visibility() { if (document.hidden) stop(); }
onMounted(() => document.addEventListener('visibilitychange', visibility));
onBeforeUnmount(() => { stop(); document.removeEventListener('visibilitychange', visibility); });
defineExpose({ stop });
</script>

<template>
  <section aria-labelledby="ambient-preview-title">
    <h3 id="ambient-preview-title">Poslech ambientu v telefonu · simulace</h3>
    <p>Tlačítko „Jednorázový ambient“ výše posílá povel kufru. Zde si přehrajete repliku přímo v telefonu, bez připojeného kufru. Zvuk připraví AI po stisknutí tlačítka.</p>
    <div class="ambient-preview-actions">
      <AwButton :disabled="!enabled || state.busy" :busy="state.busy" @click="player?.play()">Přehrát ambient v telefonu</AwButton>
      <AwButton :disabled="!state.busy" variant="danger" @click="stop">Zastavit poslech</AwButton>
    </div>
    <AwNotice v-if="state.error" tone="warning">{{ state.error }}</AwNotice>
    <p v-if="state.message" role="status">{{ state.message }}</p>
    <p v-if="state.synthetic">Syntetická zvuková fixture; hlas AI se nevolá.</p>
    <blockquote v-if="state.text">{{ state.text }}</blockquote>
  </section>
</template>

<style scoped>
.ambient-preview-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-block: .75rem; }
</style>

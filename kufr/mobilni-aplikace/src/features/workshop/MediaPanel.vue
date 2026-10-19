<script setup lang="ts">
import { markRaw, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountTransport, refreshIdentity } from '../identity/session';
import { twin } from './observation.mjs';
import { mediaController } from './media.mjs';

export interface MediaContext {
  active: boolean; replay: boolean; role: string; instanceId: string;
  mode: string; profile: string; bootId: string; status: string;
}
const props = defineProps<{ context: MediaContext }>();
const video = ref<HTMLVideoElement>();
const state = shallowRef<any>(null), controller = shallowRef<any>(null);
const failure = ref('');
let generation = 0;
const key = () => JSON.stringify([props.context.instanceId, props.context.bootId, props.context.mode, props.context.role, props.context.replay, props.context.profile]);

onMounted(() => {
  controller.value = markRaw(mediaController.createMediaController({
    video: () => video.value,
    onChange: (value: any) => { state.value = markRaw(value); },
    transport: {
      async request(instanceId: string, suffix: string, options: RequestInit = {}) {
        const path = `/v1/twin/instances/${encodeURIComponent(instanceId)}${suffix}`;
        const response = await accountTransport(path, options);
        if (!response.ok) throw await twin.httpFailure(response, path);
        if (response.status === 204) return null;
        return response.json();
      },
    },
  }));
  controller.value.sync({ ...props.context });
});
watch(() => JSON.stringify(props.context), () => {
  ++generation; failure.value = ''; controller.value?.sync({ ...props.context });
});
async function action(name: string) {
  if (!controller.value) return;
  const own = generation, target = key(); failure.value = '';
  try {
    if (['start', 'save'].includes(name)) await refreshIdentity();
    if (own !== generation || target !== key()) return;
    await controller.value.action(name);
  } catch (cause) { if (own === generation) failure.value = cause instanceof Error ? cause.message : String(cause); }
}
function selection(name: string, event: Event) {
  const input = event.target as HTMLInputElement;
  controller.value?.selection(name, input.type === 'checkbox' ? input.checked : input.value);
}
function invalidate(reason = 'Stop simulace: lokální média zastavena.') { return controller.value?.invalidate(reason); }
onBeforeUnmount(() => { ++generation; controller.value?.dispose(); controller.value = null; state.value = null; });
defineExpose({ invalidate });
</script>

<template>
  <section v-if="state?.visible" class="workshop-media" aria-labelledby="workshop-media-title">
    <h3 id="workshop-media-title">Rozhovor v integrační simulaci</h3>
    <p>{{ context.instanceId }} · {{ context.profile }}. Mikrofon, reproduktor i kamera se aktivují samostatně.</p>
    <p role="status">{{ state.phase ? `Nativní phase: ${state.phase}. ` : '' }}{{ state.notice }}</p>
    <AwNotice v-if="state.failure || failure" tone="warning">{{ state.failure || failure }}</AwNotice>
    <AwNotice v-if="context.profile !== 'integration'">Deterministický profil používá syntetické povely. Média vyžadují integrační instanci.</AwNotice>
    <details>
      <summary>Nastavení rozhovoru</summary>
      <form @submit.prevent="action('save')">
        <AwField id="media-engine" label="Engine">
          <select id="media-engine" :value="state.settings.engine" :disabled="state.disabled.engine" class="aw-input" @change="selection('engine', $event)">
            <option value="fake">Fake služby</option><option value="realtime">Realtime</option><option value="gpt-live">GPT-Live</option>
          </select>
        </AwField>
        <AwField id="media-voice" label="Hlas">
          <select id="media-voice" :value="state.settings.voice" :disabled="state.disabled.voice" class="aw-input" @change="selection('voice', $event)">
            <option v-for="voice in ['cedar', 'marin', 'coral']" :key="voice">{{ voice }}</option>
          </select>
        </AwField>
        <AwField id="media-speed" label="Rychlost hlasu">
          <select id="media-speed" :value="state.settings.speed" :disabled="state.disabled.speed" class="aw-input" @change="selection('speed', $event)">
            <option v-for="speed in ['0.8', '1', '1.2']" :key="speed">{{ speed }}</option>
          </select>
        </AwField>
        <AwField id="media-variant" label="Prezentace">
          <select id="media-variant" :value="state.settings.variant" :disabled="state.disabled.variant" class="aw-input" @change="selection('variant', $event)">
            <option value="main">main</option><option value="short">short</option>
          </select>
        </AwField>
        <label><input type="checkbox" :checked="state.settings.consent" :disabled="state.disabled.consent" @change="selection('consent', $event)" /> Souhlasím s jednou náhledovou fotkou</label>
        <div class="media-actions">
          <AwButton type="submit" :disabled="state.disabled.save">Uložit nastavení</AwButton>
          <AwButton :disabled="state.disabled.reload" @click="action('reload')">Načíst nastavení</AwButton>
        </div>
        <p v-if="state.configuration">Revision {{ state.configuration.revision }} · dostupnost providerů: {{ state.configuration.availability }}</p>
      </form>
    </details>
    <AwField v-for="device in (['mic', 'speaker', 'camera'] as const)" :id="`media-device-${device}`" :key="device" :label="{mic:'Mikrofon',speaker:'Reproduktor',camera:'Kamera'}[device]">
      <select :id="`media-device-${device}`" :value="state.settings[`${device}Device`]" :disabled="state.disabled[`${device}Device`]" class="aw-input" @change="selection(`${device}Device`, $event)">
        <option value="">Systémové zařízení</option><option v-for="item in state.devices[device]" :key="item.id" :value="item.id">{{ item.label }}</option>
      </select>
    </AwField>
    <div class="media-actions">
      <AwButton :disabled="state.disabled.devices" @click="action('devices')">Načíst lokální zařízení</AwButton>
      <AwButton :disabled="state.disabled.mic" @click="action('mic')">Aktivovat mikrofon</AwButton>
      <AwButton :disabled="state.disabled.speaker" @click="action('speaker')">Aktivovat reproduktor</AwButton>
      <AwButton variant="primary" :disabled="state.disabled.start" @click="action('start')">Spustit rozhovor</AwButton>
      <AwButton variant="danger" :disabled="state.disabled.stop" @click="action('stop')">Stop médií</AwButton>
    </div>
    <p>Zvuk dokládá software enqueue/drain, nikoli potvrzení slyšitelnosti. Nativní jádro vlastní poslechovou bránu i dokončení.</p>
    <div v-show="state.photoRequested" class="media-photo">
      <p>Jádro žádá jednu fotku. Aktivujte kameru, prohlédněte lokální náhled a výslovně potvrďte předání.</p>
      <AwButton :disabled="state.disabled.camera" @click="action('camera')">Aktivovat kameru</AwButton>
      <video ref="video" autoplay playsinline muted aria-label="Lokální náhled kamery" />
      <AwButton :disabled="state.disabled.capture" @click="action('capture')">Pořídit náhled</AwButton>
      <img v-if="state.previewUrl" :src="state.previewUrl" alt="Lokální náhled fotky před předáním" />
      <div class="media-actions">
        <AwButton variant="primary" :disabled="state.disabled.accept" @click="action('accept')">Přijmout a předat jednu fotku</AwButton>
        <AwButton :disabled="state.disabled.retake" @click="action('retake')">Vymazat náhled</AwButton>
        <AwButton :disabled="state.disabled.skipPhoto" @click="action('skip-photo')">Pokračovat bez fotky</AwButton>
      </div>
    </div>
    <ol class="media-dialogue" aria-label="Dočasný dialog" tabindex="0"><li v-for="(line, index) in state.dialogue" :key="index">{{ line }}</li></ol>
  </section>
</template>

<style scoped>
.workshop-media { padding-top: 1rem; margin-top: 1rem; border-top: 1px solid var(--aw-border-subtle); }
.media-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-block: .75rem; }
.media-photo video, .media-photo img { display: block; width: 100%; max-height: 20rem; object-fit: contain; margin-block: .75rem; }
.media-dialogue { max-height: 16rem; overflow: auto; padding-left: 1.25rem; }
</style>

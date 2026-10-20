<script setup lang="ts">
import { computed, markRaw, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountTransport, refreshIdentity } from '../identity/session';
import { twin } from './observation.mjs';
import { mediaController } from './media.mjs';
import { engineLabels, parseEngineCapabilities } from '../debug/conversationCapabilities';
import { throwIfAborted } from '../../application/cancellation.mjs';

export interface MediaContext {
  active: boolean; replay: boolean; role: string; instanceId: string;
  mode: string; profile: string; bootId: string; status: string;
}
const props = defineProps<{ context: MediaContext }>();
const video = ref<HTMLVideoElement>();
const state = shallowRef<any>(null), controller = shallowRef<any>(null);
const failure = ref('');
const selectedEngine = computed(() => state.value?.configuration?.engines?.engines.find((item: any) => item.engine === state.value?.settings.engine));
const engineOptions = computed(() => [
  { value: 'fake', label: 'Syntetická fixture · bez poskytovatele', disabled: state.value?.configuration?.availability.fake !== true },
  ...(['gpt-live', 'realtime'] as const).map(value => ({ value, label: engineLabels[value], disabled: !state.value?.configuration?.engines?.engines.some((item: any) => item.engine === value && item.ready && item.inputModes.includes('audio')) || state.value?.configuration?.availability[value === 'gpt-live' ? 'gptLive' : value] !== true })),
]);
const voices = computed<string[]>(() => state.value?.settings.engine === 'fake' ? ['cedar', 'marin', 'coral'] : selectedEngine.value?.voices || [state.value?.settings.voice].filter(Boolean));
const speeds = computed<number[]>(() => {
  if (state.value?.settings.engine === 'fake') return [.8, 1, 1.2];
  const speed = selectedEngine.value?.speed;
  return speed ? [...new Set<number>(speed.supported ? [speed.min, speed.default, speed.max] : [speed.default])].sort((a, b) => a - b) : [Number(state.value?.settings.speed || 1)];
});
let generation = 0;
let controllerEpoch = 0, disconnectedSinceAccountChange = false;
const reconnectRequired = ref(false);
const key = () => JSON.stringify([account.value?.id, props.context.instanceId, props.context.bootId, props.context.mode, props.context.role, props.context.replay, props.context.profile]);

function disposeController() {
  ++controllerEpoch;
  controller.value?.dispose(); controller.value = null; state.value = null;
}
function createController() {
  const owner = account.value?.id;
  if (!owner) return;
  const epoch = ++controllerEpoch;
  const current = () => epoch === controllerEpoch && owner === account.value?.id;
  controller.value = markRaw(mediaController.createMediaController({
    video: () => video.value,
    parseEngines: parseEngineCapabilities,
    onChange: (value: any) => { if (current()) state.value = markRaw(value); },
    transport: {
      async request(instanceId: string, suffix: string, options: RequestInit = {}) {
        const own = generation;
        const cleanup = suffix === '/media/cancel' && options.method === 'POST';
        const check = () => {
          throwIfAborted(options.signal);
          if (owner !== account.value?.id || !cleanup && (!current() || own !== generation)) throw new DOMException('Kontext médií se změnil.', 'AbortError');
        };
        check();
        const path = `/v1/twin/instances/${encodeURIComponent(instanceId)}${suffix}`;
        const response = await accountTransport(path, options);
        check();
        if (!response.ok) throw await twin.httpFailure(response, path);
        if (response.status === 204) return null;
        const value = await response.json();
        check();
        if (suffix !== '/settings') return value;
        const capabilityPath = `/app/v1/workshop/${encodeURIComponent(instanceId)}/engines`;
        const capabilities = await accountTransport(capabilityPath, { signal: options.signal });
        check();
        if (!capabilities.ok) throw await twin.httpFailure(capabilities, capabilityPath);
        const engines = await capabilities.json(); check();
        return { ...value, engines };
      },
    },
  }));
  controller.value.sync({ ...props.context });
}
onMounted(createController);
watch(() => account.value?.id, () => {
  ++generation; reconnectRequired.value = true; disconnectedSinceAccountChange = !props.context.active;
  failure.value = ''; disposeController();
}, { flush: 'sync' });
watch(() => JSON.stringify(props.context), () => {
  ++generation; failure.value = '';
  if (reconnectRequired.value) {
    if (!props.context.active) disconnectedSinceAccountChange = true;
    if (!disconnectedSinceAccountChange || !props.context.active || !account.value) return;
    reconnectRequired.value = false;
  }
  if (!controller.value) createController(); else controller.value.sync({ ...props.context });
});
async function action(name: string) {
  if (!controller.value || !account.value) return;
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
onBeforeUnmount(() => { ++generation; disposeController(); });
defineExpose({ invalidate });
</script>

<template>
  <AwNotice v-if="reconnectRequired" tone="warning">Účet se změnil nebo přístup skončil. Soukromá média byla vymazána; načtěte dostupné zdroje a znovu výslovně připojte pozorování.</AwNotice>
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
            <option v-for="engine in engineOptions" :key="engine.value" :value="engine.value" :disabled="engine.disabled">{{ engine.label }}</option>
          </select>
        </AwField>
        <AwField id="media-voice" label="Hlas">
          <select id="media-voice" :value="state.settings.voice" :disabled="state.disabled.voice" class="aw-input" @change="selection('voice', $event)">
            <option v-for="voice in voices" :key="voice">{{ voice }}</option>
          </select>
        </AwField>
        <AwField id="media-speed" label="Rychlost hlasu">
          <select id="media-speed" :value="state.settings.speed" :disabled="state.disabled.speed" class="aw-input" @change="selection('speed', $event)">
            <option v-for="speed in speeds" :key="speed" :value="String(speed)">{{ speed }}</option>
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
        <p v-if="selectedEngine">Model {{ selectedEngine.model }} · {{ selectedEngine.ready ? 'konfigurace připravena; přístup ověří provider handshake' : selectedEngine.unavailableReason || 'engine není připraven' }} · vstup {{ selectedEngine.inputModes.join(', ') }} · kamery {{ selectedEngine.cameraSources.join(', ') }}</p>
        <AwNotice v-else-if="state.settings.engine !== 'fake'" tone="warning">Metadata vybraného enginu nejsou dostupná. Výběr se nemění; provider nelze spustit.</AwNotice>
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

<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwField from '../../components/AwField.vue';
import AwNotice from '../../components/AwNotice.vue';
import { accountRequest, refreshIdentity } from '../identity/session';

const props = defineProps<{
  instanceId: string;
  profile: string;
  canControl: boolean;
  online: boolean;
  mode: string;
  replay: boolean;
}>();
const emit = defineEmits<{ submitted: []; stop: [] }>();
const slot = ref(1), variant = ref('main'), text = ref('Syntetický testovací vstup.');
const clockSpeed = ref(1), fault = ref('none');
const pending = ref(false), pendingStop = ref(false), notice = ref(''), error = ref('');
let generation = 0;
const enabled = computed(() => props.canControl && props.mode === 'simulation' && !props.replay && !!props.instanceId);
const ordinaryDisabled = computed(() => !enabled.value || !props.online || pending.value || pendingStop.value);
const newOperationId = () => crypto.randomUUID();
watch(() => [props.instanceId, props.mode, props.replay, props.canControl, props.profile], () => {
  ++generation; pending.value = false; pendingStop.value = false; notice.value = ''; error.value = '';
});
onBeforeUnmount(() => { ++generation; });

async function command(action: string, body: Record<string, unknown> = {}) {
  const urgent = action === 'stop';
  if (!enabled.value || (urgent ? pendingStop.value : ordinaryDisabled.value && action !== 'release')) return;
  if (action === 'clock' && props.profile !== 'deterministic') return;
  if (action === 'integrationText' || action === 'text' && props.profile !== 'deterministic') return;
  const own = generation, instance = props.instanceId;
  if (urgent) { pendingStop.value = true; emit('stop'); } else pending.value = true;
  error.value = ''; notice.value = '';
  try {
    await refreshIdentity();
    if (own !== generation || instance !== props.instanceId || !enabled.value) return;
    const payload = { action, ...body };
    if (new TextEncoder().encode(JSON.stringify(payload)).length > 2048) throw new Error('Povel přesáhl 2048 bajtů.');
    const result = await accountRequest<{ accepted: boolean; code?: string }>(`/v1/twin/instances/${encodeURIComponent(instance)}/commands`, 'POST', payload);
    if (own !== generation) return;
    if (!result.accepted) throw new Error(`Simulátor povel odmítl: ${result.code || 'unknown'}`);
    notice.value = action === 'release' ? 'Ovládání simulátoru bylo uvolněno.' : 'Povel přijal simulátor; výsledek ověřte ve snapshotu.';
    emit('submitted');
  } catch (cause) { if (own === generation) error.value = cause instanceof Error ? cause.message : String(cause); }
  finally { if (own === generation) { if (urgent) pendingStop.value = false; else pending.value = false; } }
}
function submitText() {
  if (props.profile !== 'deterministic' || !text.value.trim()) return;
  void command('text', {
    operationId: crypto.randomUUID(), slot: slot.value, variant: variant.value, text: text.value,
  });
}
</script>

<template>
  <section v-if="enabled" class="simulation-controls" aria-labelledby="simulation-controls-title">
    <h3 id="simulation-controls-title">Ovládání vybrané simulace</h3>
    <p>{{ instanceId }} · {{ profile }}. Oprávnění ověřuje server; tyto povely nejsou dostupné pro živý kufr ani replay.</p>
    <AwNotice v-if="profile === 'integration'">Textový backend byl ukončen. Rozhovor spustíte ve hlasovém panelu přes GPT Live nebo Realtime 2.1. Provider vyžaduje samostatnou autorizaci vlastníka.</AwNotice>
    <AwField id="simulation-slot" label="Přihrádka">
      <select id="simulation-slot" v-model.number="slot" class="aw-input"><option v-for="index in 6" :key="index" :value="index">{{ index }}</option></select>
    </AwField>
    <AwField id="simulation-variant" label="Varianta">
      <select id="simulation-variant" v-model="variant" class="aw-input"><option value="main">main</option><option value="short">short</option></select>
    </AwField>
    <AwField v-if="profile === 'deterministic'" id="simulation-text" label="Syntetický textový vstup">
      <textarea id="simulation-text" v-model="text" class="aw-input" maxlength="1024" rows="3" />
    </AwField>
    <div class="simulation-actions">
      <AwButton v-if="profile === 'deterministic'" :disabled="ordinaryDisabled" :busy="pending" @click="submitText">Spustit syntetický vstup</AwButton>
      <AwButton variant="danger" :disabled="!enabled" :busy="pendingStop" @click="command('stop')">Stop simulace</AwButton>
      <AwButton :disabled="ordinaryDisabled" @click="command('inspect')">Servisní inspekce simulace</AwButton>
      <AwButton :disabled="ordinaryDisabled" @click="command('dance', { operationId: newOperationId() })">Dance</AwButton>
      <AwButton :disabled="ordinaryDisabled" @click="command('danceStop')">Dance Stop</AwButton>
      <AwButton :disabled="!enabled || pending || pendingStop" @click="command('release')">Uvolnit ovládání</AwButton>
    </div>
    <template v-if="profile === 'deterministic'">
      <AwField id="simulation-speed" label="Rychlost fake clock">
        <select id="simulation-speed" v-model.number="clockSpeed" class="aw-input"><option :value=".25">0.25×</option><option :value="1">1×</option><option :value="4">4×</option></select>
      </AwField>
      <div class="simulation-actions">
        <AwButton :disabled="ordinaryDisabled" @click="command('clock', { paused: true })">Pozastavit clock</AwButton>
        <AwButton :disabled="ordinaryDisabled" @click="command('clock', { paused: false, speed: clockSpeed })">Pokračovat clock</AwButton>
        <AwButton :disabled="ordinaryDisabled" @click="command('clock', { paused: true, stepMs: 250 })">Krok 250 ms</AwButton>
      </div>
      <AwField id="simulation-fault" label="Syntetická porucha">
        <select id="simulation-fault" v-model="fault" class="aw-input"><option v-for="item in ['none', 'servo', 'led', 'journal', 'jam']" :key="item">{{ item }}</option></select>
      </AwField>
      <AwButton :disabled="ordinaryDisabled" @click="command('fault', { slot, fault })">Vložit poruchu simulace</AwButton>
    </template>
    <AwNotice v-if="error" tone="warning">{{ error }}</AwNotice>
    <AwNotice v-if="notice" tone="success">{{ notice }}</AwNotice>
  </section>
</template>

<style scoped>
.simulation-controls { margin-top: 1rem; padding-top: 1rem; border-top: 1px solid var(--aw-border-subtle); }
.simulation-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-block: .75rem; }
</style>

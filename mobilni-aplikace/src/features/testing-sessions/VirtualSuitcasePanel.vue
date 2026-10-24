<script setup lang="ts">
import { computed } from 'vue';
import AwStatus from '../../components/AwStatus.vue';
import type { VirtualSuitcase } from './contracts';
const props = defineProps<{ hardware: VirtualSuitcase; deviceId: string; stale: boolean }>();
const compartments = computed(() => Array.from({ length: 6 }, (_, index) => {
  const slot = index + 1;
  const observation = props.hardware.compartments?.find(item => item.slot === slot);
  return { slot, state: observation?.state ?? 'unknown', wandName: observation?.wandName, selected: props.hardware.selectedCompartment === slot };
}));
</script>

<template>
  <section class="virtual-suitcase" aria-labelledby="virtual-suitcase-title">
    <div class="testing-section-heading"><h2 id="virtual-suitcase-title">Virtuální kufr</h2><AwStatus tone="warning">Simulovaný hardware</AwStatus></div>
    <p class="aw-muted">Šest fyzických pozic; identitu hůlky určuje skutečné rozhodnutí aplikace. Zobrazená poloha není fyzické měření.</p>
    <dl class="virtual-status">
      <div><dt>Zdroj</dt><dd class="testing-code">{{ deviceId }}</dd></div>
      <div><dt>Stav</dt><dd>{{ stale ? 'Historická data — backend nedostupný' : hardware.state ?? 'unknown' }}</dd></div>
      <div><dt>Akce</dt><dd>{{ hardware.currentAction ?? 'Žádná potvrzená akce' }}</dd></div>
    </dl>
    <ol class="virtual-compartments" aria-label="Simulované přihrádky">
      <li v-for="slot in compartments" :key="slot.slot" :class="{ 'is-selected': slot.selected }">
        <strong>{{ slot.slot }}</strong><span>{{ slot.state }}</span><small v-if="slot.wandName">{{ slot.wandName }}</small><small v-if="slot.selected">Vybraná pozice</small>
      </li>
    </ol>
    <dl class="virtual-status virtual-effects">
      <div><dt>LED</dt><dd>{{ hardware.ledPattern ?? 'Žádný potvrzený příkaz' }}</dd></div>
      <div><dt>Servo</dt><dd>{{ hardware.servoCommand ?? 'Žádný potvrzený příkaz' }}</dd></div>
      <div><dt>Vibrace</dt><dd>{{ hardware.vibrationCommand ?? 'Žádný potvrzený příkaz' }}</dd></div>
    </dl>
    <details v-if="hardware.events?.length"><summary>Události virtuálního zařízení</summary>
      <ol class="testing-timeline"><li v-for="(event, index) in hardware.events" :key="event.sequence ?? index">
        <strong>{{ event.type }}</strong><span v-if="event.code"> · {{ event.code }}</span><span v-if="event.compartment"> · pozice {{ event.compartment }}</span>
        <small>Simulace hardwaru</small>
      </li></ol>
    </details>
  </section>
</template>

<style scoped>
.virtual-status { display: grid; gap: .75rem; }
.virtual-status > div { display: grid; grid-template-columns: 5rem minmax(0, 1fr); gap: .75rem; }
.virtual-status dt { color: var(--aw-muted); }
.virtual-status dd { margin: 0; overflow-wrap: anywhere; }
.virtual-compartments { list-style: none; margin: 1.5rem 0; padding: 0; display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: .5rem; }
.virtual-compartments li { min-height: 7rem; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: .5rem; padding: .5rem; border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); text-align: center; overflow-wrap: anywhere; }
.virtual-compartments strong { font-size: var(--aw-text-heading); }
.virtual-compartments span, .virtual-compartments small { font-size: var(--aw-text-caption); }
.virtual-compartments .is-selected { background: var(--aw-accent-soft); border-color: var(--aw-accent); }
.virtual-effects { border-top: 1px solid var(--aw-border-subtle); padding-top: 1rem; }
@media (max-width: 44rem) { .virtual-compartments { grid-template-columns: repeat(3, minmax(0, 1fr)); } }
</style>

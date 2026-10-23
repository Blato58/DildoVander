<script setup lang="ts">
import { computed, ref } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import {
  customerDemoSteps,
  getCustomerDemoStep,
  initialCustomerDemoStep,
  nextCustomerDemoStep,
  previousCustomerDemoStep,
} from './customerDemoModel';

const currentStepId = ref(initialCustomerDemoStep);
const currentStep = computed(() => getCustomerDemoStep(currentStepId.value));
const currentIndex = computed(() => customerDemoSteps.findIndex((step) => step.id === currentStepId.value));

function goNext() {
  currentStepId.value = nextCustomerDemoStep(currentStepId.value);
}

function goBack() {
  currentStepId.value = previousCustomerDemoStep(currentStepId.value);
}

function reset() {
  currentStepId.value = initialCustomerDemoStep;
}
</script>

<template>
  <section class="customer-demo aw-card" aria-labelledby="customer-demo-title">
    <p class="eyebrow">Ukázka pro nové návštěvníky</p>
    <h1 id="customer-demo-title">Příběh bez připojení</h1>

    <AwNotice tone="info" title="Syntetická ukázka">
      Všechny postavy a ukázky níže jsou smyšlené a zůstávají jen na této stránce.
      Ukázka nepřipojuje kufr, nepoužívá mikrofon ani neposílá data službám AI.
      Skutečný rozhovor zajišťuje samostatný kufr; telefon ukazuje stav a památku.
      Ukázka se neukládá ani nikam neodesílá. Profil skutečné postavy můžete uložit i bez AI přípravy;
      údaje skutečné postavy se společnosti OpenAI odešlou jen po vašem samostatném výslovném souhlasu.
    </AwNotice>

    <nav class="customer-demo__steps" aria-label="Kroky ukázky">
      <span v-for="(step, index) in customerDemoSteps" :key="step.id"
        :aria-current="index === currentIndex ? 'step' : undefined">
        {{ index + 1 }}. {{ step.label }}
      </span>
    </nav>

    <div class="customer-demo__content" aria-live="polite" aria-atomic="true">
      <h2>{{ currentStep.title }}</h2>
      <p>{{ currentStep.description }}</p>

      <ol v-if="currentStep.lines" class="customer-demo__dialogue" aria-label="Předem napsaný dialog">
        <li v-for="line in currentStep.lines" :key="line.speaker">
          <strong>{{ line.speaker }}:</strong> {{ line.text }}
        </li>
      </ol>
      <blockquote v-if="currentStep.result">„{{ currentStep.result }}“</blockquote>
      <dl v-if="currentStep.wandDesign" class="customer-demo__wand-design">
        <div><dt>Tvar</dt><dd>{{ currentStep.wandDesign.shape }}</dd></div>
        <div><dt>Rukojeť</dt><dd>{{ currentStep.wandDesign.handle }}</dd></div>
        <div><dt>Ornament</dt><dd>{{ currentStep.wandDesign.ornament }}</dd></div>
        <div><dt>Materiál</dt><dd>{{ currentStep.wandDesign.material }}</dd></div>
        <div><dt>Magické téma</dt><dd>{{ currentStep.wandDesign.magicalTheme }}</dd></div>
      </dl>
      <div v-if="currentStep.keepsake" class="customer-demo__keepsake">
        <p class="eyebrow">Fiktivní náhled památky</p>
        <strong>{{ currentStep.keepsake }}</strong>
      </div>
    </div>

    <div class="customer-demo__actions">
      <AwButton :disabled="currentIndex === 0" @click="goBack">Zpět</AwButton>
      <AwButton variant="primary" :disabled="currentIndex === customerDemoSteps.length - 1" @click="goNext">
        Další krok
      </AwButton>
      <AwButton @click="reset">Začít ukázku znovu</AwButton>
    </div>
  </section>
</template>

<style scoped>
.customer-demo { display: grid; gap: var(--aw-space-4); }
.customer-demo h1, .customer-demo h2, .customer-demo p { margin: 0; }
.customer-demo__steps, .customer-demo__actions { display: flex; flex-wrap: wrap; gap: var(--aw-space-2); }
.customer-demo__steps { color: var(--aw-muted); font-size: var(--aw-text-small); }
.customer-demo__steps [aria-current='step'] { color: var(--aw-ivory); font-weight: 700; }
.customer-demo__content { display: grid; gap: var(--aw-space-3); }
.customer-demo__dialogue { display: grid; gap: var(--aw-space-2); padding-inline-start: 1.5rem; }
.customer-demo blockquote { margin: 0; padding-inline-start: 1rem; border-inline-start: 3px solid var(--aw-accent); }
.customer-demo__wand-design { display: grid; gap: var(--aw-space-2); margin: 0; }
.customer-demo__wand-design div { display: flex; flex-wrap: wrap; gap: var(--aw-space-2); }
.customer-demo__wand-design dt { font-weight: 700; }
.customer-demo__wand-design dd { margin: 0; }
.customer-demo__keepsake { display: grid; gap: var(--aw-space-2); padding: var(--aw-space-4); border: 1px solid var(--aw-border-subtle); border-radius: var(--aw-radius-card); }
.customer-demo__keepsake .eyebrow { color: var(--aw-muted); font-size: var(--aw-text-small); }
</style>

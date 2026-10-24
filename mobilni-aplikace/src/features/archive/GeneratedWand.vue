<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import './archive.css';
import SpellGlyph from './SpellGlyph.vue';
import { combatSpecialty, projectGeneratedWand } from './generatedWand';
import { customerCopy } from '../../application/customerCopy';
const props = withDefaults(defineProps<{ wand?: unknown; showName?: boolean; imageUrl?: string; imagePending?: boolean; imageError?: boolean; showArtwork?: boolean; synthetic?: boolean; imageState?: string; retryBusy?: boolean; canRetry?: boolean; keepsakeTo?: string; historyTo?: string }>(), { showArtwork: true });
const emit = defineEmits<{ retry: [] }>();
const wand = computed(() => projectGeneratedWand(props.wand));
const en = computed(() => customerCopy.language.value === 'en');
const decodeError = ref(false);
watch(() => props.imageUrl, () => { decodeError.value = false; });
const failed = computed(() => props.imageError || decodeError.value || ['failed', 'provider_unknown', 'unknown'].includes(props.imageState ?? ''));
const pending = computed(() => props.imagePending || ['queued', 'running', 'pending'].includes(props.imageState ?? ''));
const actionLabels = computed(() => en.value ? { strike: 'Strike', trick: 'Trick', shield: 'Shield' } : { strike: 'Úder', trick: 'Lest', shield: 'Štít' });
</script>
<template>
  <section v-if="wand" class="archive-personal-wand" :class="{ 'archive-personal-wand--illustrated': showArtwork }" :aria-label="en ? 'Your personal wand' : 'Tvoje osobní hůlka'">
    <figure v-if="showArtwork" class="archive-personal-wand-art" :data-image-state="failed ? 'failed' : imageUrl ? 'ready' : pending ? 'pending' : 'missing'">
      <img v-if="imageUrl && !decodeError" :src="imageUrl" :alt="en ? `Digital illustration of the wand ${wand.name}` : `Digitální ilustrace hůlky ${wand.name}`" decoding="async" @error="decodeError = true" />
      <div v-else class="archive-art-state" role="status">
        <span class="archive-art-state-mark" aria-hidden="true">✦</span>
        <strong>{{ failed ? (en ? 'The magic needs another attempt' : 'Magie potřebuje další pokus') : pending ? (en ? 'Your artifact is taking shape' : 'Tvůj artefakt dostává podobu') : (en ? 'Your wand has a story' : 'Tvá hůlka má svůj příběh') }}</strong>
        <p>{{ failed ? (en ? 'The illustration is unavailable. Your wand and spells remain saved.' : 'Ilustrace není dostupná. Tvá hůlka i kouzla zůstávají uložené.') : pending ? (en ? 'Your wand illustration is being prepared.' : 'Ilustrace tvé hůlky se připravuje.') : (en ? 'This wand has no saved illustration yet.' : 'Tato hůlka zatím nemá uloženou ilustraci.') }}</p>
        <button v-if="canRetry" class="aw-button aw-button--secondary" :disabled="retryBusy" @click="emit('retry')">{{ en ? 'Retry illustration' : 'Zkusit ilustraci znovu' }}</button>
      </div>
      <figcaption class="archive-caption">{{ synthetic ? (en ? 'Synthetic test illustration · no paid generation' : 'Syntetická testovací ilustrace · bez placeného generování') : (en ? 'Original fictional digital illustration' : 'Originální fiktivní digitální ilustrace') }}</figcaption>
    </figure>
    <div class="archive-wand-identity">
      <p class="archive-wand-kicker">{{ en ? 'A bond of character and magic' : 'Pouto postavy a magie' }}</p>
      <h2 v-if="showName" class="archive-wand-name">{{ wand.name }}</h2>
      <p class="archive-wand-material">{{ wand.material }} <span aria-hidden="true">/</span> {{ wand.core }}</p>
      <p class="archive-wand-specialty">{{ combatSpecialty(wand.combatArchetype, en) }}</p>
      <p class="archive-wand-lore">{{ wand.lore }}</p>
      <dl class="archive-wand-facts">
        <dt>{{ en ? 'Temperament' : 'Povaha' }}</dt><dd>{{ wand.temperament }}</dd>
        <dt>{{ en ? 'Affinity' : 'Magické zaměření' }}</dt><dd>{{ wand.affinity }}</dd>
        <dt>{{ en ? 'Weakness' : 'Slabina' }}</dt><dd>{{ wand.weakness }}</dd>
      </dl>
      <details class="archive-wand-design"><summary>{{ en ? 'Craftsmanship & appearance' : 'Řemeslo a podoba' }}</summary><p>{{ wand.designDescription }}</p><p class="archive-caption">{{ en ? 'Personal digital wand. Its fictional appearance does not change the physical prop.' : 'Osobní digitální hůlka. Její fiktivní podoba nemění fyzickou rekvizitu.' }}</p></details>
      <div v-if="keepsakeTo || historyTo" class="archive-actions archive-wand-links"><RouterLink v-if="keepsakeTo" :to="keepsakeTo">{{ en ? 'Open your keepsake' : 'Otevřít svou památku' }}</RouterLink><RouterLink v-if="historyTo" :to="historyTo">{{ en ? 'Battle history' : 'Historie soubojů' }}</RouterLink></div>
    </div>
    <section class="archive-wand-repertoire" :aria-label="en ? 'Your three spells' : 'Tvoje tři kouzla'">
      <h3>{{ en ? 'Three spells. Your way of fighting.' : 'Tři kouzla. Tvůj způsob boje.' }}</h3>
      <ul class="archive-wand-spells archive-wand-spells--cards"><li v-for="spell in wand.spells" :key="spell.action" :data-spell="spell.action"><SpellGlyph :action="spell.action" /><span class="archive-spell-kind">{{ actionLabels[spell.action] }}</span><strong>{{ spell.name }}</strong><span>{{ spell.description }}</span></li></ul>
    </section>
  </section>
</template>

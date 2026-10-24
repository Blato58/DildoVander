<script setup lang="ts">
import { computed } from 'vue';
import './archive.css';
import { projectGeneratedWand } from './generatedWand';
import { customerCopy } from '../../application/customerCopy';
const props = defineProps<{ wand?: unknown; showName?: boolean }>();
const wand = computed(() => projectGeneratedWand(props.wand));
const en = computed(() => customerCopy.language.value === 'en');
</script>
<template>
  <section v-if="wand" class="archive-personal-wand" :aria-label="en ? 'Your personal wand' : 'Tvoje osobní hůlka'">
    <h2 v-if="showName">{{ wand.name }}</h2>
    <p class="archive-caption">{{ en ? 'Personal digital wand. Its fictional appearance does not change the physical prop.' : 'Osobní digitální hůlka. Její fiktivní podoba nemění fyzickou rekvizitu.' }}</p>
    <p>{{ wand.lore }}</p>
    <dl class="archive-wand-facts"><dt>{{ en ? 'Material and core' : 'Materiál a jádro' }}</dt><dd>{{ wand.material }} · {{ wand.core }}</dd><dt>{{ en ? 'Temperament' : 'Povaha' }}</dt><dd>{{ wand.temperament }}</dd><dt>{{ en ? 'Affinity' : 'Magické zaměření' }}</dt><dd>{{ wand.affinity }}</dd><dt>{{ en ? 'Weakness' : 'Slabina' }}</dt><dd>{{ wand.weakness }}</dd><dt>{{ en ? 'Digital design' : 'Digitální podoba' }}</dt><dd>{{ wand.designDescription }}</dd></dl>
    <h3>{{ en ? 'Your three spells' : 'Tvoje tři kouzla' }}</h3>
    <ul class="archive-wand-spells"><li v-for="spell in wand.spells" :key="spell.action"><strong>{{ spell.name }}</strong><span>{{ spell.description }}</span></li></ul>
  </section>
</template>

<script setup lang="ts">
import { customerCopy } from './customerCopy';
</script>

<template>
  <section class="page-stack"><p class="eyebrow">DildoVander</p><h1>{{ customerCopy.text('not-found.title') }}</h1>
    <p>{{ customerCopy.text('not-found.help') }}</p><RouterLink class="aw-button aw-button--primary" to="/">{{ customerCopy.text('not-found.home') }}</RouterLink>
  </section>
</template>

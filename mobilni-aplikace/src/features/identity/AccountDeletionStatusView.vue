<script setup lang="ts">
import AccountDeletion from './AccountDeletion.vue';
</script>

<template>
  <article class="page-stack" aria-labelledby="deletion-status-title">
    <h1 id="deletion-status-title">Stav smazání účtu</h1>
    <AccountDeletion receipt-only />
    <p><RouterLink to="/support">Kontaktovat podporu</RouterLink> · <RouterLink to="/privacy">Ochrana soukromí</RouterLink></p>
  </article>
</template>

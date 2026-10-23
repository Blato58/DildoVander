<script setup lang="ts">
import { RouterLink } from 'vue-router';
</script>

<template>
  <details class="aw-card lost-access-help">
    <summary>Ztratili jste přístup?</summary>
    <div class="lost-access-guidance">
      <p>Zkuste zálohovaný přístupový klíč ve svém správci hesel nebo jiný klíč registrovaný u účtu.</p>
      <p>Na důvěryhodném zařízení, kde jste stále přihlášeni, můžete v Nastavení účtu zkontrolovat své klíče. Přidání dalšího klíče může vyžadovat nové ověření.</p>
      <p>Pokud nemáte žádný použitelný klíč, <RouterLink to="/support">kontaktujte podporu</RouterLink>. Podpora vás provede ověřením vlastnictví účtu; jednorázový kód obnovy může vystavit pouze vlastník služby.</p>
      <p>Obnova používá jednorázový kód s omezenou platností. Po dokončení budou původní klíče odebrány a ostatní relace odhlášeny.</p>
      <p>Zobrazované jméno ani odkaz na památku vlastnictví účtu neprokazují. Nikomu neposílejte přístupové klíče ani obnovovací kódy.</p>
    </div>
  </details>
</template>

<style scoped>
.lost-access-help summary { cursor: pointer; font-weight: 600; }
.lost-access-guidance { display: grid; gap: 1rem; margin-top: 1rem; }
.lost-access-guidance p { margin: 0; }
</style>

<script setup lang="ts">
import { RouterLink } from 'vue-router';
import { customerCopy } from '../../application/customerCopy';
</script>

<template>
  <details class="aw-card lost-access-help">
    <summary>{{ customerCopy.text('signin.lost-access') }}</summary>
    <div class="lost-access-guidance">
      <p>{{ customerCopy.text('signin.try-backup-key') }}</p>
      <p>{{ customerCopy.text('signin.trusted-device') }}</p>
      <p>{{ customerCopy.text('signin.contact-support-before') }}<RouterLink to="/support">{{ customerCopy.text('signin.contact-support-link') }}</RouterLink>{{ customerCopy.text('signin.contact-support-after') }}</p>
      <p>{{ customerCopy.text('signin.recovery-effects') }}</p>
      <p>{{ customerCopy.text('signin.never-share-secrets') }}</p>
    </div>
  </details>
</template>

<style scoped>
.lost-access-help summary { cursor: pointer; font-weight: 600; }
.lost-access-guidance { display: grid; gap: 1rem; margin-top: 1rem; }
.lost-access-guidance p { margin: 0; }
</style>

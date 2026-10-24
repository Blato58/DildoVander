<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import { customerCopy } from '../../application/customerCopy';
import { applicationBuild } from '../../application/buildIdentity';
import { clearRequestDiagnostics, requestDiagnostics } from '../../application/requestDiagnostics';

const downloadUrl = ref('');
function revoke(): void {
  if (downloadUrl.value) URL.revokeObjectURL(downloadUrl.value);
  downloadUrl.value = '';
}
function prepare(): void {
  revoke();
  const payload = { schemaVersion: 1, exportedAt: new Date().toISOString(), build: applicationBuild, requests: requestDiagnostics.value };
  downloadUrl.value = URL.createObjectURL(new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' }));
}
watch(requestDiagnostics, entries => { if (!entries.length) revoke(); }, { flush: 'sync' });
onBeforeUnmount(revoke);
</script>

<template>
  <details class="aw-card">
    <summary>{{ customerCopy.text('settings.diagnostics') }}</summary>
    <p>{{ customerCopy.text('settings.diagnostics-help') }}</p>
    <p>{{ customerCopy.text('settings.diagnostics-count', { count: requestDiagnostics.length }) }}</p>
    <div class="button-row">
      <AwButton :disabled="!requestDiagnostics.length" @click="prepare">{{ customerCopy.text('settings.diagnostics-prepare') }}</AwButton>
      <a v-if="downloadUrl" :href="downloadUrl" download="application-diagnostics.json">{{ customerCopy.text('settings.diagnostics-download') }}</a>
      <AwButton :disabled="!requestDiagnostics.length" @click="clearRequestDiagnostics">{{ customerCopy.text('settings.diagnostics-clear') }}</AwButton>
    </div>
  </details>
</template>

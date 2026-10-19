<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import AwButton from '../components/AwButton.vue';
import AwNotice from '../components/AwNotice.vue';
import { usePreferences } from './preferences';
import { notifications, dismissNotice, notify } from './notifications';
import { account, refreshIdentity, hasRole } from '../features/identity/session';
import { deviceSession } from '../features/suitcase/deviceSession';
import { nativeDebug } from '../features/suitcase/nativeDebug';
import { directorStop, clearDirectorStop } from '../features/director/directorStop';
import { servoBench } from '../features/suitcase/servoBench';

const route = useRoute();
const router = useRouter();
const preferences = usePreferences();
const main = ref<HTMLElement>();
const navigationElement = ref<HTMLElement>();
const navigationHeight = ref('calc(5rem + env(safe-area-inset-bottom))');
let navigationObserver: ResizeObserver | undefined;
const viewError = ref('');
const navigation = [
  { to: '/', label: 'Domov', symbol: '⌂' },
  { to: '/suitcase', label: 'Kufr', symbol: '▣' },
  { to: '/workshop', label: 'Dílna', symbol: '◇' },
  { to: '/archive', label: 'Archiv', symbol: '▤' },
  { to: '/settings', label: 'Nastavení', symbol: '⚙' },
];
const connected = computed(() => Boolean(deviceSession.snapshot.value && deviceSession.snapshot.value.phase !== 'disconnected'));
const deviceCanStop = computed(() => connected.value && deviceSession.snapshot.value?.phase !== 'stopped');
const phoneActive = computed(() => nativeDebug.status.value.running || nativeDebug.status.value.session || nativeDebug.pending.value);
const directorCanStop = computed(() => directorStop.value?.canStop() ?? false);
const stopBusy = computed(() => (deviceCanStop.value || phoneActive.value || directorCanStop.value)
  && (!deviceCanStop.value || deviceSession.stopPending.value)
  && (!phoneActive.value || nativeDebug.cancellationPending.value)
  && (!directorCanStop.value || directorStop.value?.busy())
  && !servoBench.connected.value);
const targetLabel = computed(() => ({ unavailable: 'Kufr není připojen', demo: 'Ukázka · bez zařízení', simulator: 'Simulátor · bez fyzických pohonů', native: 'Místní kufr' })[deviceSession.target.value]);
const canAdmin = computed(() => hasRole('Owner') || hasRole('Administrator'));
async function stop(): Promise<void> {
  // Start independent cancellation paths together; a slow USB acknowledgement
  // cannot delay the already-permitted phone or suitcase Stop.
  const requests: Promise<unknown>[] = [Promise.resolve().then(() => deviceSession.stop())];
  if (servoBench.connected.value) requests.push(Promise.resolve().then(() => servoBench.stop()));
  if (phoneActive.value) requests.push(nativeDebug.cancel());
  if (directorCanStop.value && directorStop.value) requests.push(directorStop.value.stop());
  const results = await Promise.allSettled(requests);
  if (results.some(result => result.status === 'rejected')) notify('Stop se nepodařilo potvrdit. Zkontrolujte spojení a bezpečný stav zařízení.', 'danger');
}
watch(() => account.value ? `${account.value.id}:${account.value.roles.join(',')}` : '', (next, previous) => {
  if (next !== previous) clearDirectorStop();
  if (previous && next !== previous) {
    nativeDebug.invalidatePrivateState();
    void Promise.resolve(deviceSession.clearUserGrants()).catch(() => notify('Oprávnění této instalace se nepodařilo vyčistit. Odpojte ji před změnou uživatele.', 'danger'));
  }
}, { flush: 'sync' });
watch(() => route.path, () => {
  viewError.value = '';
  // Announce route changes while leaving same-route form/telemetry focus intact.
  requestAnimationFrame(() => main.value?.focus({ preventScroll: true }));
});
const removeRouterError = router.onError(() => { viewError.value = 'Stránku se nepodařilo načíst. Zkontrolujte připojení a zkuste načtení znovu.'; });
function refreshSession(): void {
  if (!document.hidden) void refreshIdentity().catch(() => { /* Offline local operation remains available. */ });
}
onMounted(() => {
  refreshSession();
  if (typeof ResizeObserver !== 'undefined' && navigationElement.value) {
    navigationObserver = new ResizeObserver(() => {
      navigationHeight.value = `${navigationElement.value?.getBoundingClientRect().height ?? 0}px`;
    });
    navigationObserver.observe(navigationElement.value);
  }
  document.addEventListener('visibilitychange', refreshSession);
  window.addEventListener('online', refreshSession);
});
onBeforeUnmount(() => {
  removeRouterError();
  navigationObserver?.disconnect();
  document.removeEventListener('visibilitychange', refreshSession);
  window.removeEventListener('online', refreshSession);
});
</script>

<template>
  <a class="skip-link" href="#main-content">Přejít na obsah</a>
  <div class="application-shell" :style="{ '--application-navigation-height': navigationHeight }">
    <aside class="application-sidebar" aria-label="Hlavní navigace">
      <RouterLink class="application-brand" to="/" aria-label="DildoVander, domov"><span class="brand-mark" aria-hidden="true">DV</span><span>DildoVander<small>Kufr. Dílna. Příběhy.</small></span></RouterLink>
      <nav ref="navigationElement" class="primary-navigation" aria-label="Sekce aplikace">
        <RouterLink v-for="item in navigation" :key="item.to" :to="item.to" :class="{ selected: item.to === '/' ? route.path === '/' : route.path.startsWith(item.to) }">
          <span aria-hidden="true">{{ item.symbol }}</span><span>{{ item.label }}</span>
        </RouterLink>
      </nav>
      <div class="sidebar-details"><span class="eyebrow">Vapour Chrome</span><p>Jeden prostor pro celý večer.</p><RouterLink v-if="canAdmin" to="/settings/admin">Správa aplikace</RouterLink></div>
    </aside>
    <div class="application-body">
      <header class="application-header">
        <div class="connection-context"><RouterLink to="/connections">{{ targetLabel }}</RouterLink><span v-if="servoBench.connected.value" class="context-detail">USB servo servis · Stop vypíná PWM, napájení není měřené</span><span v-else-if="phoneActive" class="context-detail">Aktivní telefonní zkouška · oddělená od hlasu kufru</span><span v-else-if="deviceSession.message.value" class="context-detail">{{ deviceSession.message.value }}</span></div>
        <div class="header-actions">
          <label class="theme-control"><span class="sr-only">Barevný režim</span><select v-model="preferences.theme" aria-label="Barevný režim"><option value="dark">Tmavý</option><option value="light">Světlý</option><option value="system">Systém</option></select></label>
          <RouterLink v-if="!account" class="account-link" to="/signin">Přihlásit se</RouterLink><RouterLink v-else class="account-link" to="/settings">{{ account.displayName }}</RouterLink>
          <AwButton variant="danger" :disabled="!deviceCanStop && !phoneActive && !directorCanStop && !servoBench.connected.value" :busy="stopBusy" @click="stop">Stop</AwButton>
        </div>
      </header>
      <div class="notification-stack" aria-label="Oznámení"><AwNotice v-for="notice in notifications" :key="notice.id" :tone="notice.tone"><div class="notification-content">{{ notice.message }}<button aria-label="Zavřít oznámení" @click="dismissNotice(notice.id)">×</button></div></AwNotice></div>
      <main id="main-content" ref="main" class="application-main" tabindex="-1">
        <AwNotice v-if="viewError" tone="danger"><p>{{ viewError }}</p><AwButton @click="router.go(0)">Načíst znovu</AwButton></AwNotice>
        <RouterView v-else v-slot="{ Component, route: renderedRoute }">
          <KeepAlive><component :is="Component" v-if="renderedRoute.meta.keepAlive" :key="renderedRoute.name" /></KeepAlive>
          <component :is="Component" v-if="!renderedRoute.meta.keepAlive" :key="renderedRoute.name" />
        </RouterView>
      </main>
      <footer class="application-footer"><span>DildoVander</span><span>Model, simulace a fyzický kufr mají vlastní stav ověření.</span></footer>
    </div>
  </div>
</template>

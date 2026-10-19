<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { account } from '../features/identity/session';
import { deviceSession } from '../features/suitcase/deviceSession';

const source = computed(() => deviceSession.target.value);
const greeting = computed(() => account.value ? `Vítejte, ${account.value.displayName}` : 'Všechno pro jeden večer.');
const evening = ref<{ label: string; latest?: string } | null>(null);
const eveningState = ref('Načítám přehled večera…');
const controller = new AbortController();
onMounted(async () => {
  try {
    const response = await fetch('/guest/v1/evening?limit=1', { credentials: 'omit', cache: 'no-store', redirect: 'error', signal: controller.signal });
    if (!response.ok) throw new Error();
    const value = await response.json();
    if (controller.signal.aborted) return;
    if (value.schemaVersion !== 1) throw new Error();
    if (value.evening && typeof value.evening.label === 'string') {
      evening.value = { label: value.evening.label.slice(0, 240), latest: typeof value.latest?.officialName === 'string' ? value.latest.officialName.slice(0, 240) : undefined };
      eveningState.value = 'Veřejný přehled aktivního večera';
    } else { eveningState.value = 'Veřejný večer zatím není otevřený.'; }
  } catch {
    if (!controller.signal.aborted) eveningState.value = 'Přehled večera je nyní nedostupný. Ostatní části aplikace zůstávají použitelné.';
  }
});
onBeforeUnmount(() => controller.abort());
</script>

<template>
  <div class="home-layout">
    <section class="home-introduction page-stack">
      <p class="eyebrow">DildoVander</p><h1>{{ greeting }}</h1>
      <p class="home-lead">Připojte kufr, prohlédněte sestavu nebo se vraťte k příběhu své hůlky.</p>
      <div class="home-primary-actions"><RouterLink class="aw-button aw-button--primary" :to="source === 'unavailable' ? '/connections' : '/suitcase'">{{ source === 'unavailable' ? 'Připojit kufr' : 'Otevřít kufr' }}</RouterLink><RouterLink class="aw-button aw-button--secondary" to="/archive">Najít příběh hůlky</RouterLink></div>
      <p v-if="!account" class="aw-muted">Veřejné příběhy a model si můžete prohlédnout rovnou. Účet zpřístupní oprávnění a správu.</p>
      <section class="home-evening" aria-label="Aktivní večer"><p class="eyebrow">{{ eveningState }}</p><template v-if="evening"><h2>{{ evening.label }}</h2><p v-if="evening.latest">Poslední připravená hůlka: {{ evening.latest }}</p><RouterLink to="/archive">Otevřít tento večer →</RouterLink></template></section>
    </section>
    <section class="home-readiness" aria-labelledby="readiness-title"><span class="eyebrow">Právě teď</span><h2 id="readiness-title">Váš kufr</h2>
      <template v-if="source === 'unavailable'"><p class="readiness-value">Čeká na připojení</p><p>Vyberte uložený kufr nebo ho bezpečně spárujte. Samotné přihlášení přístup k pohonům neuděluje.</p><RouterLink to="/connections">Připojení a párování →</RouterLink></template>
      <template v-else><p class="readiness-value">{{ source === 'demo' ? 'Ukázkový režim' : source === 'simulator' ? 'Simulátor' : 'Místní připojení' }}</p><p>{{ deviceSession.message.value || 'Aktuální stav a dostupné akce najdete u vybraného kufru.' }}</p><RouterLink to="/suitcase">Zobrazit stav →</RouterLink></template>
    </section>
    <section class="home-workspaces" aria-label="Co chcete udělat">
      <RouterLink class="workspace-link" to="/workshop"><span class="workspace-number">01 / DÍLNA</span><h2>Rozumět sestavě</h2><p>Model, montážní kroky a pozorování v jednom pracovním prostoru.</p><span class="workspace-action">Otevřít dílnu →</span></RouterLink>
      <RouterLink class="workspace-link" to="/archive"><span class="workspace-number">02 / ARCHIV</span><h2>Příběhy z večera</h2><p>Hůlky a veřejné památky. Soukromé fotografie zůstávají za vaším přístupem.</p><span class="workspace-action">Prohlédnout archiv →</span></RouterLink>
      <RouterLink class="workspace-link" to="/settings"><span class="workspace-number">03 / VÁŠ PROSTOR</span><h2>Účet a oprávnění</h2><p>Přístupové klíče, důvěryhodné instalace a bezpečné přihlášení.</p><span class="workspace-action">Otevřít nastavení →</span></RouterLink>
    </section>
  </div>
</template>

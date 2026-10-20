<script setup lang="ts">
import { offlineSession } from '../identity/session';
import AwButton from '../../components/AwButton.vue';
import { computed, onActivated, onBeforeUnmount, onDeactivated, onMounted, ref, watch } from 'vue';
import { deviceSession as device } from './deviceSession';
import DeviceAssociationPanel from './DeviceAssociationPanel.vue';
const address = ref('');
const deviceId = ref('');
const pin = ref('');
const profile = ref<'hotspot' | 'fallback'>('hotspot');
const destructive = ref<'delete-network' | 'forget-device' | 'install' | null>(null);
const saved = device.savedDevice;
const lastObserved = device.lastObservedAt;
const observedDate = computed(() => lastObserved.value === null ? null : new Date(lastObserved.value));
const observedText = computed(() => observedDate.value?.toLocaleString('cs-CZ', { dateStyle: 'medium', timeStyle: 'medium' }));
const ble = device.bleStatus;
watch(saved, value => { address.value = value.host ?? ''; destructive.value = null; });
function show(): void { device.showConnections(true); }
function hide(): void { device.showConnections(false); destructive.value = null; pin.value = ''; }
onMounted(() => { show(); void device.refreshSaved(); });
onActivated(show);
onDeactivated(hide);
onBeforeUnmount(hide);
async function pair(): Promise<void> { await device.pairManual(deviceId.value, address.value, pin.value); pin.value = ''; }
async function confirm(): Promise<void> {
  const action = destructive.value;
  destructive.value = null;
  if (action === 'forget-device') await device.forgetDevice();
  else if (action === 'delete-network') await device.networkCommand('delete', profile.value);
  else if (action === 'install') await device.networkCommand('ota-install', profile.value);
}
</script>

<template>
  <div class="connections-view">
    <header class="feature-heading"><p class="aw-eyebrow">Zařízení</p><h1>Připojení kufru</h1><p>Ověřte svůj kufr, nastavte společnou síť a připojte místní ovládání.</p></header>
    <p role="status" class="aw-notice">{{ device.message.value }}</p>
    <DeviceAssociationPanel v-if="!offlineSession" />
    <template v-if="device.native">
      <section class="aw-card" aria-labelledby="saved-heading"><h2 id="saved-heading">{{ saved.paired ? 'Uložený kufr' : 'První připojení' }}</h2>
        <template v-if="saved.paired"><p><strong>{{ saved.deviceId }}</strong> · {{ saved.host }}</p><p>Párování je v Keychain. Připojení používá uložený token a ověřený otisk certifikátu; nezačne přebírat řízení.</p><dl class="connection-metrics"><div><dt>Naposledy pozorován místním ovládáním</dt><dd><time v-if="observedDate" :datetime="observedDate.toISOString()">{{ observedText }}</time><span v-else>Dosud nepozorován v této relaci aplikace.</span></dd></div></dl><p>Potvrzený stav nebo odpověď přes místní ovládací spojení. Čas podle hodin tohoto telefonu, od otevření aplikace.</p><div class="connection-actions"><AwButton variant="primary" :disabled="device.pending.value" @click="device.connectSaved">Připojit uložený kufr</AwButton><AwButton :disabled="device.pending.value || !device.connected.value" @click="device.disconnect">Odpojit telefon</AwButton></div><RouterLink v-if="device.connected.value" to="/suitcase" class="aw-button aw-button--primary">Otevřít ovládání kufru</RouterLink></template>
        <p v-else>Zapněte kufr. Připravte PoP z vnitřního servisního štítku. Ověření a heslo Wi-Fi zadáte v nativním dialogu; přihlášení k účtu párování nenahrazuje.</p>
      </section>
      <section class="aw-card" aria-labelledby="ble-heading"><h2 id="ble-heading">1. Najít a ověřit přes Bluetooth</h2><p>BLE funguje v servisním nebo obnovovacím okně kufru. Vyberte svůj kufr a ověřte PoP.</p>
        <div class="connection-actions"><AwButton variant="primary" :disabled="device.pending.value || device.blePending.value" @click="device.discoverBle">Vyhledat Bluetooth kufry</AwButton><AwButton :disabled="device.blePending.value || !ble" @click="device.disconnectBle">Odpojit Bluetooth</AwButton><AwButton :disabled="device.pending.value" @click="device.openSettings">Nastavení iPhonu</AwButton></div>
        <ul v-if="device.bleDevices.value.length" class="connection-list" aria-label="Nalezené Bluetooth kufry"><li v-for="item in device.bleDevices.value" :key="item.name"><AwButton :disabled="device.blePending.value" @click="device.connectBle(item.name)">Ověřit {{ item.name }}</AwButton></li></ul>
        <p role="status">{{ device.bleMessage.value }}</p>
        <dl v-if="ble" class="connection-metrics"><div><dt>Zařízení / firmware</dt><dd>{{ ble.deviceId }} · {{ ble.firmwareVersion }}</dd></div><div><dt>Síť</dt><dd>{{ ble.network.phase }} · {{ ble.network.result }} · {{ ble.network.ipv4 || 'Adresa neznámá' }}</dd></div><div><dt>Backend</dt><dd>{{ ble.backendConnected ? 'Připojený' : 'Nepřipojený / neověřený' }}</dd></div></dl>
      </section>
      <section class="aw-card" aria-labelledby="wifi-heading"><h2 id="wifi-heading">2. Nastavit Wi-Fi a spárovat ovládání</h2><p>iPhone i kufr musí být ve stejné síti. Hotspot má přednost před záložní Wi-Fi. Hesla zůstávají v nativním dialogu a zabezpečeném BLE spojení.</p><label class="aw-field">Síť<select v-model="profile" class="aw-input" :disabled="device.blePending.value"><option value="hotspot">Osobní hotspot iPhonu</option><option value="fallback">Záložní Wi-Fi</option></select></label>
        <p v-if="ble">{{ profile === 'hotspot' ? ble.network.hotspot.ssid : ble.network.fallback.ssid }} · aktivní: {{ ble.network.activeProfile || 'žádná' }}</p>
        <div class="connection-actions"><AwButton :disabled="!ble || device.blePending.value" @click="device.configureNetwork(profile)">Nastavit síť</AwButton><AwButton :disabled="!ble || device.blePending.value" @click="device.networkCommand('test', profile)">Ověřit síť</AwButton><AwButton :disabled="!ble || device.blePending.value" @click="device.refreshBle">Obnovit stav</AwButton><AwButton variant="primary" :disabled="!ble || device.blePending.value" @click="device.pairLocal">Spárovat místní ovládání</AwButton></div><p>Po úspěšném párování použijte nahoře Připojit uložený kufr. Telefon automaticky převezme adresu, otisk a token do Keychain.</p>
      </section>
      <details class="aw-card"><summary>Obnova adresy a ruční párování</summary><p>Při změně Wi-Fi nejprve zkuste Bluetooth a obnovte připojení. Ručně měňte adresu pouze u svého uloženého kufru.</p><AwButton :disabled="device.pending.value" @click="device.discoverLocal">Hledat v místní síti (5 s)</AwButton><ul class="connection-list"><li v-for="item in device.discovered.value" :key="item.deviceId + item.host"><span>{{ item.deviceId }} · {{ item.host }}</span> <AwButton v-if="saved.paired" class="aw-button aw-button--secondary" :disabled="device.pending.value" @click="device.setAddress(item.host)">Použít adresu</AwButton></li></ul>
        <form v-if="saved.paired" @submit.prevent="device.setAddress(address)"><label class="aw-field">IP adresa nebo hostname<input v-model="address" class="aw-input" required autocomplete="off"></label><AwButton type="submit" :disabled="device.pending.value">Uložit adresu</AwButton></form>
        <form v-else class="manual-pairing" @submit.prevent="pair"><label class="aw-field">ID kufru<input v-model="deviceId" class="aw-input" required autocomplete="off"></label><label class="aw-field">IP adresa nebo hostname<input v-model="address" class="aw-input" required autocomplete="off"></label><label class="aw-field">Ověřený otisk SPKI SHA-256<input v-model="pin" class="aw-input" required autocomplete="off"></label><AwButton type="submit" :disabled="device.pending.value">Ověřit a spárovat</AwButton></form>
      </details>
      <details class="aw-card"><summary>Sítě, aktualizace a odebrání párování</summary><p>Aktualizace firmware je samostatná servisní operace. Před instalací ověřte bezpečný stav, napájení a cílovou verzi.</p><p v-if="ble">{{ ble.ota.state }} · {{ ble.ota.version }}<template v-if="ble.ota.availableVersion"> · dostupná {{ ble.ota.availableVersion }}</template> {{ ble.ota.error }}</p><div class="connection-actions"><AwButton :disabled="!ble || device.blePending.value" @click="device.networkCommand('ota-check', profile)">Ověřit aktualizace</AwButton><AwButton :disabled="!ble || device.blePending.value" @click="destructive = 'install'">Instalovat firmware…</AwButton><AwButton :disabled="!ble || device.blePending.value" @click="device.networkCommand('ota-policy', profile, true)">Zapnout automatické aktualizace</AwButton><AwButton :disabled="!ble || device.blePending.value" @click="device.networkCommand('ota-policy', profile, false)">Vypnout automatické aktualizace</AwButton><AwButton :disabled="!ble || device.blePending.value" @click="destructive = 'delete-network'">Smazat vybranou síť…</AwButton><AwButton :disabled="!saved.paired || device.pending.value" @click="destructive = 'forget-device'">Odebrat párování z iPhonu…</AwButton></div>
        <section v-if="destructive" class="aw-notice"><h3>{{ destructive === 'forget-device' ? 'Odebrat místní párování?' : destructive === 'install' ? 'Instalovat firmware do kufru?' : 'Smazat vybraný síťový profil?' }}</h3><p>{{ destructive === 'forget-device' ? 'Přístup tohoto iPhonu zmizí z Keychain. Data a síť kufru zůstanou zachované; nové připojení bude vyžadovat nové párování.' : destructive === 'install' ? 'Instalace změní firmware kufru. Potvrďte až po místní kontrole bezpečného stavu.' : `Odstraníte profil ${profile}. Připojení kufru se může přerušit.` }}</p><div class="connection-actions"><AwButton @click="destructive = null">Zrušit</AwButton><AwButton variant="primary" :disabled="device.pending.value || device.blePending.value" @click="confirm">Potvrdit</AwButton></div></section>
      </details>
    </template>
    <section v-else class="aw-card"><h2>Webové prostředí</h2><p>Bluetooth, Keychain a připnuté místní spojení vyžadují aplikaci na iPhonu. V prohlížeči se žádný skutečný kufr automaticky nepřipojuje.</p><a class="aw-button aw-button--primary" href="/ios/">Stáhnout aplikaci pro iPhone</a><div class="connection-actions"><AwButton :disabled="device.pending.value" @click="device.useDemo">Otevřít místní ukázku</AwButton><AwButton :disabled="device.pending.value" @click="device.useSimulator">Připojit vývojový E2 simulátor</AwButton></div><p>Ukázka je bez sítě a fyzických efektů. E2 vyžaduje skutečně spuštěný místní vývojový server.</p><RouterLink v-if="device.target.value !== 'unavailable'" to="/suitcase" class="aw-button aw-button--secondary">Otevřít označené ovládání</RouterLink></section>
  </div>
</template>

<style scoped>
.connections-view{display:grid;gap:1.5rem}.connection-actions{display:flex;flex-wrap:wrap;gap:.75rem;margin-block:1rem}.connection-list{list-style:none;padding:0;display:grid;gap:.75rem}.connection-metrics{display:grid;gap:.75rem}.connection-metrics dd{margin:0;overflow-wrap:anywhere}.aw-field,.manual-pairing{display:grid;gap:.5rem}.manual-pairing{margin-block:1rem;gap:1rem}summary{cursor:pointer;font-weight:600}form .aw-button{margin-block:1rem}
</style>

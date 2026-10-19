<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import AwButton from '../../components/AwButton.vue';
import AwNotice from '../../components/AwNotice.vue';
import { account, accountRequest, hasRole } from './session';
interface Modes { revision: string; dialogMode: string; voiceMode: string; dialogAvailable: boolean; voiceAvailable: boolean; activeConversations: number; personalityId: string; personalities: {id:string;name:string}[]; characterEnabled:boolean;characterTone:string;characterFinaleProfile:string;characterVersion:string }
interface Job { id:string;kind:string;state:string }
interface ArchiveItem { id:string;officialName:string;assetCount:number;storedBytes:number;jobs:Job[] }
interface Status {modes:Modes|null;readiness:{accounts:boolean;keepsakes:boolean;keepsakeGeneration:boolean;passkeyRelyingParty:string;nativeAssociationConfigured:boolean};archive:ArchiveItem[]|null}
interface LogPage {storeId:string;nextCursor:number;hasMore:boolean;truncated:boolean;droppedWrites:number;entries:{id:number;timestamp:string;level:string;eventCode:string;code?:string;traceId?:string;operationId?:string}[]}
const status = ref<Status|null>(null); const logs = ref<LogPage|null>(null);
const error = ref(''); const busy = ref(false); const level = ref('warning'); let epoch = 0;
const operationId = () => crypto.randomUUID();
async function load() {
  const request = ++epoch; error.value = ''; busy.value = true;
  try {
    const value = await accountRequest<Status>('/app/v1/admin/operations/status');
    if (request === epoch) status.value = value;
  } catch (e) { if (request === epoch) error.value = (e as Error).message; }
  finally { if (request === epoch) busy.value = false; }
}
async function readLogs(next = false) {
  const request = epoch;
  try {
    const previous = logs.value;
    const query = `?level=${level.value}${next && previous ? `&after=${previous.nextCursor}` : ''}`;
    const value = await accountRequest<LogPage>('/app/v1/admin/operations/logs' + query);
    if (request === epoch) logs.value = value;
  } catch (e) { if (request === epoch) error.value = (e as Error).message; }
}
async function mutate(path:string, method:string, body:object) {
  error.value=''; busy.value=true;
  try { await accountRequest(path, method, body); await load(); }
  catch(e) {error.value=(e as Error).message;}
  finally {busy.value=false;}
}
function saveModes() {
  const modes=status.value?.modes; if(!modes) return;
  void mutate('/app/v1/admin/operations/modes','PUT',{expectedRevision:modes.revision,dialogMode:modes.dialogMode,voiceMode:modes.voiceMode,personalityId:modes.personalityId,
    characterEnabled:modes.characterEnabled,characterTone:modes.characterTone,characterFinaleProfile:modes.characterFinaleProfile});
}
watch(()=>account.value?.id,()=>{++epoch;status.value=null;logs.value=null;error.value='';},{flush:'sync'});
onMounted(load);onBeforeUnmount(()=>{++epoch;status.value=null;logs.value=null;});
</script>
<template>
  <section v-if="hasRole('Administrator')" class="aw-card operations" aria-labelledby="operations-title">
    <h2 id="operations-title">Provoz serveru</h2>
    <p>Stav a technické logy se pouze čtou. Změny režimů vyžadují potvrzení a ověření účtu z posledních 10 minut. Zapnutí živého poskytovatele může při následném rozhovoru účtovat spotřebu.</p>
    <AwNotice v-if="error" tone="warning">{{error}} <RouterLink :to="{path:'/signin',query:{return:'/settings/admin'}}">Znovu ověřit účet</RouterLink></AwNotice>
    <AwButton :busy="busy" @click="load">Obnovit stav</AwButton>
    <template v-if="status">
      <dl><dt>Účty</dt><dd>{{status.readiness.accounts?'připravené':'nenakonfigurované'}}</dd><dt>Archiv</dt><dd>{{status.readiness.keepsakes?'zapnutý':'vypnutý'}}</dd><dt>Generování památek</dt><dd>{{status.readiness.keepsakeGeneration?'zapnuté':'vypnuté'}}</dd><dt>Nativní asociace domény</dt><dd>{{status.readiness.nativeAssociationConfigured?'konfigurace dostupná, zařízení vyžaduje ověření':'nenakonfigurovaná'}}</dd></dl>
      <form v-if="status.modes" class="mode-form" @submit.prevent="saveModes">
        <h3>Rozhovor a hlas</h3><p>Aktivní rozhovory: {{status.modes.activeConversations}}. Revize {{status.modes.revision}}.</p>
        <label for="operations-dialog">Dialog</label><select id="operations-dialog" v-model="status.modes.dialogMode" class="aw-select"><option value="fake">Místní testovací odpovědi</option><option value="responses" :disabled="!status.modes.dialogAvailable">OpenAI Responses</option></select>
        <label for="operations-voice">Hlas</label><select id="operations-voice" v-model="status.modes.voiceMode" class="aw-select"><option value="fake">Místní testovací hlas</option><option value="openai" :disabled="!status.modes.voiceAvailable">OpenAI hlas</option></select>
        <label for="operations-persona">Osobnost</label><select id="operations-persona" v-model="status.modes.personalityId" class="aw-select"><option v-for="persona in status.modes.personalities" :key="persona.id" :value="persona.id">{{persona.name}}</option></select>
        <p>Postava {{status.modes.characterVersion}} · {{status.modes.characterEnabled?'zapnutá':'vypnutá'}} · tón {{status.modes.characterTone}} · finále {{status.modes.characterFinaleProfile}}.</p>
        <AwButton type="submit" :busy="busy">Potvrdit změnu režimů a osobnosti</AwButton>
      </form>
      <p v-else>Režimy rozhovoru nejsou v tomto prostředí dostupné.</p>
      <h3>Technické logy</h3><label for="operations-log-level">Úroveň</label><select id="operations-log-level" v-model="level" class="aw-select"><option value="warning">Varování a chyby</option><option value="error">Chyby</option><option value="info">Všechny technické události</option></select><div class="operations-actions"><AwButton @click="readLogs()">Načíst logy</AwButton><AwButton v-if="logs?.hasMore" @click="readLogs(true)">Další stránka</AwButton></div>
      <template v-if="logs"><AwNotice v-if="logs.truncated || logs.droppedWrites" tone="warning">Výpis není úplný. Zahozené zápisy: {{logs.droppedWrites}}.</AwNotice><p v-if="!logs.entries.length">V této stránce nejsou žádné odpovídající události.</p><ol class="event-list"><li v-for="entry in logs.entries" :key="`${logs.storeId}-${entry.id}`"><time>{{entry.timestamp}}</time> · {{entry.level}} · {{entry.eventCode}}<p v-if="entry.code">{{entry.code}}</p><p v-if="entry.traceId">Trace {{entry.traceId}}</p><p v-if="entry.operationId">Operace {{entry.operationId}}</p></li></ol></template>
      <h3>Úlohy soukromého archivu</h3><p v-if="!status.archive">Archiv není v tomto prostředí nakonfigurován.</p><p v-else-if="!status.archive.length">Archiv neobsahuje žádné položky.</p>
      <ul v-else class="event-list"><li v-for="item in status.archive" :key="item.id"><strong>{{item.officialName}}</strong><p>{{item.assetCount}} souborů · {{(item.storedBytes/1048576).toFixed(1)}} MiB</p><ul><li v-for="job in item.jobs" :key="job.id">{{job.kind}} · {{job.state}} <AwButton v-if="['queued','pending','running'].includes(job.state)" :disabled="busy" @click="mutate(`/app/v1/admin/operations/keepsakes/${item.id}/jobs/${job.id}/cancel`,'POST',{operationId:operationId()})">Zrušit úlohu</AwButton></li></ul><AwButton :disabled="busy" @click="mutate(`/app/v1/admin/operations/keepsakes/${item.id}/revoke-shares`,'POST',{operationId:operationId()})">Zrušit sdílené odkazy</AwButton></li></ul>
    </template>
  </section>
</template>
<style scoped>.operations{display:grid;gap:1rem}.operations dl{display:grid;grid-template-columns:minmax(8rem,1fr) 2fr;gap:.5rem}.operations dd{margin:0}.mode-form{display:grid;gap:.75rem}.event-list{padding-left:1.25rem;max-height:30rem;overflow:auto;overflow-wrap:anywhere}.event-list>li{padding-block:.75rem;border-top:1px solid var(--aw-border-subtle)}.operations-actions{display:flex;gap:.75rem;flex-wrap:wrap}@media(max-width:480px){.operations dl{display:block}.operations dt{margin-top:.75rem}}</style>

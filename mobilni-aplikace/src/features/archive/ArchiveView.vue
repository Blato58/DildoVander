<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { GuestError, projectGallery, publicAssetPath, publicMedia, type GalleryItem } from './client';
import { createArchiveClient } from './nativeGuest';
import { useArchiveMedia } from './useArchiveMedia';
import ArchiveDialog from './ArchiveDialog.vue';
import ArchiveDownload from './ArchiveDownload.vue';
import AccountArchiveList from '../identity/AccountArchiveList.vue';
import WizardIdentity from './WizardIdentity.vue';
import GeneratedWand from './GeneratedWand.vue';
import './archive.css';
const client = createArchiveClient();
const media = useArchiveMedia(client);
const items = ref<GalleryItem[]>([]);
const selected = ref<GalleryItem | null>(null);
const latest = ref<GalleryItem | null>(null);
const eveningId = ref('');
const eveningLabel = ref('Aktivní večer');
const cursor = ref<string>();
const pageCursor = ref<string>();
const busy = ref(false);
const message = ref('Otevíráme archiv aktivního večera…');
const artworkOpen = ref(false);
let followsLatest = true;
let timer: ReturnType<typeof setTimeout> | undefined;
let refreshSequence = 0;
const paragraphs = computed(() => selected.value?.story.text.split(/\n\s*\n/) ?? []);
const key = (item: GalleryItem | null) => item ? `${item.locator}:${item.editionId}` : '';
function clear() { media.clear(); items.value = []; selected.value = null; latest.value = null; cursor.value = undefined; pageCursor.value = undefined; artworkOpen.value = false; }
function choose(item: GalleryItem, automatic = false) {
  if (!automatic) followsLatest = false;
  selected.value = item; media.clearFile(); artworkOpen.value = false;
  const art = item.assets.find(asset => asset.kind === 'base_image');
  void media.artwork(art ? publicAssetPath(eveningId.value, item, art) : undefined, true);
}
async function refresh(nextPage = false) {
  if (document.hidden) return;
  if (busy.value) { clearTimeout(timer); timer = setTimeout(() => refresh(), 15000); return; }
  clearTimeout(timer); busy.value = true;
  const sequence = ++refreshSequence; const check = client.capture(); const previousEvening = eveningId.value;
  const requestedCursor = nextPage ? cursor.value : pageCursor.value;
  try {
    const page = projectGallery(await client.json('/guest/v1/evening' + (requestedCursor ? `?cursor=${encodeURIComponent(requestedCursor)}` : ''), {}, true));
    check(); if (sequence !== refreshSequence) return;
    if (nextPage && page.evening?.id !== previousEvening) { clear(); eveningId.value = ''; followsLatest = true; return; }
    if ((page.evening?.id ?? '') !== eveningId.value) { clear(); followsLatest = true; }
    eveningId.value = page.evening?.id ?? ''; eveningLabel.value = page.evening?.label || 'Aktivní večer';
    pageCursor.value = requestedCursor;
    items.value = [...new Map(page.items.map(item => [key(item), item])).values()];
    latest.value = page.latest; cursor.value = page.nextCursor;
    const next = followsLatest ? page.latest ?? items.value[0] : items.value.find(item => key(item) === key(selected.value));
    if (next) {
      const changed = JSON.stringify(next) !== JSON.stringify(selected.value);
      if (changed) choose(next, true);
    } else { selected.value = null; media.clear(); }
    message.value = selected.value ? 'Aktivní večer · přehled se průběžně obnovuje.' : eveningId.value ? 'První příběh je ještě před námi.' : 'Večer ještě nezačal.';
  } catch (error) {
    if (sequence !== refreshSequence || (error as Error).name === 'AbortError') return;
    if (error instanceof GuestError && [403, 404, 409, 410].includes(error.status)) { clear(); eveningId.value = ''; followsLatest = true; }
    message.value = 'Přehled teď není dostupný. Zkus obnovit spojení.';
  } finally {
    if (sequence === refreshSequence) { busy.value = false; if (!document.hidden) timer = setTimeout(() => refresh(), 15000); }
  }
}
async function download(kind: string) {
  const item = selected.value; const asset = item?.assets.find(value => value.kind === kind);
  if (!item || !asset || busy.value) return;
  const check = client.capture(); const selectedKey = key(item); busy.value = true;
  try {
    const filename = { base_image: 'wand-art.png', export_story: 'story.txt', export_card: 'keepsake-card.png', export_zip: 'bundle.zip' }[kind] ?? 'keepsake';
    await media.download(publicAssetPath(eveningId.value, item, asset), filename, [publicMedia[kind]], true); check();
    if (key(selected.value) !== selectedKey) media.clearFile();
  } catch (error) { if ((error as Error).name !== 'AbortError') message.value = 'Soubor není dostupný. Obnov přehled a zkus dostupnou část edice.'; }
  finally { try { check(); busy.value = false; } catch { /* A new route owns the state. */ } }
}
function suspend() { refreshSequence++; client.invalidate(); clearTimeout(timer); busy.value = false; clear(); }
function visibility() { if (document.hidden) suspend(); else void refresh(); }
function pageShow(event: PageTransitionEvent) { if (event.persisted) void refresh(); }
onMounted(() => { document.addEventListener('visibilitychange', visibility); window.addEventListener('pagehide', suspend); window.addEventListener('pageshow', pageShow); void refresh(); });
onBeforeUnmount(() => { suspend(); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', suspend); window.removeEventListener('pageshow', pageShow); });
async function shareFile() { try { await media.shareFile(); } catch (error) { if ((error as Error).name !== 'AbortError') message.value = 'Systémové sdílení není dostupné. Použij stažení souboru.'; } }
</script>
<template>
  <section class="archive-view" aria-labelledby="archive-title">
    <div><h1 id="archive-title">Najdi svoji hůlku.</h1><p>Vyber jméno z přehledu. Obraz, příběh i památku k uložení najdeš hned vedle.</p></div>
    <AccountArchiveList />
    <div class="archive-actions"><button class="aw-button aw-button--secondary" :disabled="busy" @click="refresh()">{{ busy ? 'Načítám…' : 'Obnovit přehled' }}</button><RouterLink to="/keepsake/">Soukromá památka</RouterLink><RouterLink to="/archive/chronicle">Večerní kronika</RouterLink></div>
    <p class="aw-notice" role="status" aria-live="polite">{{ message }}</p>
    <div v-if="items.length" class="archive-workspace">
      <section aria-labelledby="archive-list-title"><h2 id="archive-list-title">Hůlky večera</h2><p class="archive-muted">{{ eveningLabel }}</p>
        <ol class="archive-index"><li v-for="item in items" :key="key(item)"><button class="aw-button aw-button--secondary" :aria-pressed="key(item) === key(selected)" @click="choose(item)">{{ item.generatedWand?.name ?? item.wizardIdentity?.generatedWand?.name ?? item.officialName }}<span v-if="item.synthetic"> · Syntetická ukázka</span></button></li></ol>
        <button v-if="cursor" class="aw-button aw-button--secondary" :disabled="busy" @click="refresh(true)">Načíst další hůlky</button>
        <button v-if="pageCursor" class="aw-button aw-button--ghost" :disabled="busy" @click="pageCursor = undefined; followsLatest = true; refresh()">Zpět na nejnovější přehled</button>
      </section>
      <article v-if="selected" aria-labelledby="public-wand-name">
        <div class="archive-actions"><p>{{ key(selected) === key(latest) ? 'Nejnovější připravená hůlka' : 'Vybraný příběh tohoto večera' }}</p><button v-if="latest && key(selected) !== key(latest)" class="aw-button aw-button--ghost" @click="followsLatest = true; choose(latest, true)">Zobrazit nejnovější</button></div>
        <div class="archive-edition"><div><button class="archive-art-button" :disabled="!media.artworkUrl.value" aria-label="Zvětšit digitální obraz hůlky" @click="artworkOpen = true"><img v-if="media.artworkUrl.value" :src="media.artworkUrl.value" class="archive-art" alt="Osobní digitální interpretace vybrané hůlky" @error="media.clearArtwork(); media.artworkError.value = true"><span v-else>{{ media.artworkError.value ? 'Obraz se nepodařilo zobrazit. Obnov přehled.' : 'Obraz se připravuje' }}</span></button><p class="archive-caption">Osobní AI interpretace · fantasy obraz, nikoli fotografie rekvizity</p></div>
          <div><h2 id="public-wand-name">{{ selected.generatedWand?.name ?? selected.wizardIdentity?.generatedWand?.name ?? selected.officialName }}</h2><p>Hůlka z přihrádky {{ selected.slot ?? '—' }}</p><h3>{{ selected.story.title }}</h3><p>{{ paragraphs[0] }}</p><p v-if="selected.synthetic" class="aw-notice">SYNTETICKÁ UKÁZKA · nejde o výstup placeného generování.</p>
            <div class="archive-actions"><button v-for="action in [{ kind: 'export_zip', label: 'Uložit památku ZIP' }, { kind: 'export_story', label: 'Příběh TXT' }, { kind: 'base_image', label: 'Obraz hůlky' }, { kind: 'export_card', label: 'Kartička PNG' }]" :key="action.kind" class="aw-button aw-button--secondary" :disabled="busy || !selected.assets.some(asset => asset.kind === action.kind)" @click="download(action.kind)">{{ action.label }}</button></div><p class="archive-muted">Nedostupný soubor se může ještě připravovat. Dostupné části zůstávají k uložení.</p>
          </div></div>
        <section class="archive-story"><h3>Příběh jedné nepravděpodobné hůlky</h3><p class="archive-caption">Kouzelnická fikce pro tento večer</p><p v-for="(paragraph, index) in paragraphs" :key="index">{{ paragraph }}</p></section>
        <GeneratedWand v-if="selected.generatedWand && !selected.wizardIdentity?.generatedWand" :wand="selected.generatedWand" />
        <WizardIdentity :identity="selected.wizardIdentity" />
      </article>
    </div>
    <p v-else>Jakmile kufr připraví první edici aktivního večera, objeví se tady. Tenhle odkaz si můžeš nechat otevřený.</p>
    <aside class="archive-muted">Hůlky a příběhy jsou společný přehled tohoto večera. Fotografie a kouzelnické portréty zůstávají soukromé. <RouterLink to="/keepsake/?privacy=1">Soukromí a uchování dat</RouterLink></aside>
    <ArchiveDialog :open="artworkOpen" title="Zvětšený obraz hůlky" @close="artworkOpen = false"><img v-if="media.artworkUrl.value" :src="media.artworkUrl.value" alt="Zvětšená digitální interpretace hůlky"></ArchiveDialog>
    <ArchiveDownload :url="media.fileUrl.value" :filename="media.fileName.value" :can-share="media.canShareFile()" :native="media.native" @close="media.clearFile" @share="shareFile" />
  </section>
</template>

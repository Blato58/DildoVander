const $ = id => document.getElementById(id);
const allowedKinds = new Set(['base_image', 'export_story', 'export_card', 'export_zip']);
const kindMediaTypes = { base_image: 'image/png', export_story: 'text/plain', export_card: 'image/png', export_zip: 'application/zip' };
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
let eveningId;
let items = [];
let latest;
let selected;
let selectedAssetBinding;
let followsLatest = true;
let nextCursor;
let pageCursor;
let requestEpoch = 0;
let selectionEpoch = 0;
let controller;
let artController;
let refreshTimer;
let refreshing = false;
let paging = false;
let olderPage = false;
let artworkUrl;
let currentArtwork;
const fileUrls = new Set();
const listNodes = new Map();
const pendingDownloads = new Set();

function text(id, value) { $(id).textContent = value ?? ''; }
function visible(id, shown) { $(id).hidden = !shown; }
function status(message) { text('evening-status', message); }
function itemKey(item) { return `${item.locator}:${item.editionId}`; }
function assetOf(item, kind) { return item?.assets.find(asset => asset.kind === kind); }
function assetSignature(asset) { return `${asset.id}:${asset.kind}:${asset.mediaType}:${asset.length}:${asset.sha256}`; }
function downloadStillCurrent(download) {
  if (!selected || eveningId !== download.eveningId || itemKey(selected) !== download.itemKey) return false;
  const asset = assetOf(selected, download.kind);
  return !!asset && assetSignature(asset) === download.assetSignature;
}
function hasPendingDownload(kind) {
  return [...pendingDownloads].some(download => download.kind === kind
    && !download.controller.signal.aborted && downloadStillCurrent(download));
}
function safeText(value, limit) { return typeof value === 'string' ? value.slice(0, limit) : ''; }
function mediaEssence(value) { return typeof value === 'string' ? value.split(';')[0].trim().toLowerCase() : ''; }
function projectItem(raw) {
  if (!raw || !uuid.test(raw.locator) || !uuid.test(raw.editionId)) return null;
  const assets = Array.isArray(raw.assets) ? raw.assets.filter(asset =>
    asset && uuid.test(asset.id) && allowedKinds.has(asset.kind)
      && mediaEssence(asset.mediaType) === kindMediaTypes[asset.kind]
  ).map(asset => ({ id: asset.id, kind: asset.kind, mediaType: mediaEssence(asset.mediaType), length: asset.length,
    sha256: safeText(asset.sha256, 64) })) : [];
  return {
    locator: raw.locator, editionId: raw.editionId,
    officialName: safeText(raw.officialName, 240), catalogId: safeText(raw.catalogId, 120),
    slot: Number.isInteger(raw.slot) && raw.slot >= 1 && raw.slot <= 6 ? raw.slot : null,
    createdAt: safeText(raw.createdAt, 40), synthetic: raw.synthetic === true,
    story: { title: safeText(raw.story?.title, 240), text: safeText(raw.story?.text, 32000) }, assets
  };
}
function assetPath(item, asset) {
  return `/guest/v1/evenings/${eveningId}/keepsakes/${item.locator}/editions/${item.editionId}/assets/${asset.id}`;
}
async function read(path, signal) {
  const response = await fetch(path, { credentials: 'omit', cache: 'no-store', redirect: 'error', signal });
  if (!response.ok) {
    const error = new Error('Public archive unavailable');
    error.status = response.status; error.retryAfterMs = Number(response.headers.get('Retry-After')) * 1000;
    throw error;
  }
  return response;
}
function clearArtwork() {
  artController?.abort();
  if (artworkUrl) URL.revokeObjectURL(artworkUrl);
  artworkUrl = undefined; currentArtwork = undefined;
  for (const id of ['public-art', 'public-full-art']) $(id).removeAttribute('src');
  visible('public-art', false); visible('public-art-status', true); $('public-art-open').disabled = true;
  $('public-art-dialog').close();
}
function clearPreparedFiles() {
  for (const dialog of document.querySelectorAll('[data-public-file]')) { dialog.close(); dialog.remove(); }
  for (const url of fileUrls) URL.revokeObjectURL(url);
  fileUrls.clear();
}
function setSelected(item) {
  const previousKey = selected && itemKey(selected);
  const binding = item ? `${itemKey(item)}:${item.assets.map(assetSignature).sort().join('|')}` : undefined;
  if (previousKey !== (item && itemKey(item))) selectionEpoch++;
  if (binding !== selectedAssetBinding) clearPreparedFiles();
  selected = item; selectedAssetBinding = binding;
  for (const download of pendingDownloads) if (!downloadStillCurrent(download)) download.controller.abort();
}
function clearScope() {
  selectionEpoch++; clearArtwork(); clearPreparedFiles();
  for (const download of pendingDownloads) download.controller.abort();
  items = []; latest = undefined; selected = undefined; nextCursor = undefined;
  selectedAssetBinding = undefined;
  pageCursor = undefined;
  olderPage = false;
  $('evening-list').replaceChildren(); listNodes.clear();
  for (const id of ['public-name', 'public-title', 'public-summary', 'public-story']) $(id).replaceChildren();
  delete $('public-story').dataset.content;
  visible('public-edition', false); visible('evening-list-section', false); visible('load-more', false); visible('first-page', false);
}
async function renderArtwork(item) {
  const asset = assetOf(item, 'base_image');
  const identity = asset ? `${eveningId}:${itemKey(item)}:${asset.id}` : undefined;
  if (identity && currentArtwork === identity) return;
  clearArtwork();
  if (!asset) { text('public-art-status', 'Obraz ještě není připravený. Dostupný příběh můžeš číst.'); return; }
  if (asset.mediaType !== 'image/png') { text('public-art-status', 'Obraz zatím není dostupný v podporovaném formátu.'); return; }
  currentArtwork = identity;
  text('public-art-status', 'Načítáme obraz této hůlky…');
  artController = new AbortController();
  const epoch = selectionEpoch; const scope = eveningId;
  try {
    const response = await read(assetPath(item, asset), artController.signal);
    const blob = await response.blob();
    if (epoch !== selectionEpoch || scope !== eveningId || currentArtwork !== identity) return;
    artworkUrl = URL.createObjectURL(blob);
    $('public-art').src = artworkUrl; $('public-full-art').src = artworkUrl;
    visible('public-art', true); visible('public-art-status', false); $('public-art-open').disabled = false;
  } catch (error) {
    if (error.name === 'AbortError' || epoch !== selectionEpoch || scope !== eveningId || currentArtwork !== identity) return;
    currentArtwork = undefined;
    text('public-art-status', 'Obraz se nepodařilo načíst. Zkus obnovit přehled.');
  }
}
function renderSelection() {
  if (!selected) { visible('public-edition', false); return; }
  visible('public-edition', true); visible('evening-empty', false);
  text('selected-label', latest && itemKey(selected) === itemKey(latest) ? 'Nejnovější připravená hůlka' : 'Vybraný příběh tohoto večera');
  text('public-catalog', selected.slot ? `Hůlka z přihrádky ${selected.slot}` : 'Hůlka z tohoto večera');
  text('public-name', selected.officialName || 'Název hůlky není potvrzený');
  text('public-title', selected.story.title);
  text('public-summary', selected.story.text.split(/\n\s*\n/)[0]);
  text('public-provenance', selected.synthetic
    ? 'SYNTETICKÁ UKÁZKA · testovací výstup neprokazuje generování OpenAI.'
    : 'Osobní AI interpretace · příběh a digitální návrh vznikly pro tento výběr.');
  const story = $('public-story');
  if (story.dataset.content !== selected.story.text) {
    story.dataset.content = selected.story.text; story.replaceChildren();
    for (const paragraph of (selected.story.text || 'Archivář ještě připravuje příběh této hůlky.').split(/\n\s*\n/)) {
      const node = document.createElement('p'); node.textContent = paragraph; story.append(node);
    }
  }
  for (const button of document.querySelectorAll('[data-public-download]')) {
    button.disabled = !assetOf(selected, button.dataset.publicDownload) || hasPendingDownload(button.dataset.publicDownload);
  }
  text('export-status', assetOf(selected, 'export_zip') ? 'ZIP obsahuje dostupné základní soubory této edice. Bez fotografií a portrétů.' : 'Balíček se ještě připravuje. Hotové jednotlivé soubory si můžeš stáhnout už teď.');
  visible('select-latest', !!latest && itemKey(selected) !== itemKey(latest));
  renderArtwork(selected);
  for (const [key, node] of listNodes) node.firstElementChild.setAttribute('aria-pressed', String(key === itemKey(selected)));
}
function select(item, manual = true) {
  if (!item || !eveningId) return;
  setSelected(item); followsLatest = !manual; renderSelection();
}
function renderList() {
  const wanted = new Set(items.map(itemKey));
  for (const [key, node] of listNodes) if (!wanted.has(key)) { node.remove(); listNodes.delete(key); }
  items.forEach((item, index) => {
    const key = itemKey(item);
    let node = listNodes.get(key);
    if (!node) {
      node = document.createElement('li');
      const button = document.createElement('button'); button.className = 'evening-item'; button.type = 'button';
      const name = document.createElement('strong'); const title = document.createElement('span'); title.className = 'item-title';
      const state = document.createElement('span'); state.className = 'item-state'; button.append(name, title, state);
      button.addEventListener('click', () => select(items.find(candidate => itemKey(candidate) === key)));
      node.append(button); listNodes.set(key, node);
    }
    const button = node.firstElementChild;
    button.children[0].textContent = item.officialName;
    button.children[1].textContent = item.story.title || 'Příběh se připravuje';
    button.children[2].textContent = item.synthetic ? 'SYNTETICKÁ UKÁZKA' : assetOf(item, 'base_image') ? 'Obraz připravený' : 'Obraz se připravuje';
    button.setAttribute('aria-pressed', String(!!selected && key === itemKey(selected)));
    const position = $('evening-list').children[index];
    if (position !== node) $('evening-list').insertBefore(node, position || null);
  });
  visible('evening-list-section', items.length > 0);
  text('item-count', `Zobrazeno ${items.length} příběhů`);
  visible('load-more', !!nextCursor); $('load-more').disabled = paging;
  visible('first-page', olderPage);
}
function schedule(delay = 10000) {
  clearTimeout(refreshTimer);
  if (!document.hidden) refreshTimer = setTimeout(refresh, Math.min(60000, Math.max(5000, delay)));
}
function handleUnavailable(error) {
  clearScope();
  visible('evening-empty', true);
  text('empty-title', 'Archiv je právě nedostupný');
  text('empty-copy', 'Zkus obnovit spojení. Tento přehled žádné generování ani nový výběr nespouští.');
  status(error.status === 429 ? 'Přehled dostává příliš mnoho požadavků. Za chvíli jej zkusíme znovu.' : 'Přehled se nepodařilo ověřit.');
}
async function refresh() {
  if (document.hidden || refreshing) return;
  refreshing = true; paging = false;
  $('refresh-evening').disabled = true; $('load-more').disabled = true;
  controller?.abort(); controller = new AbortController();
  const epoch = ++requestEpoch;
  try {
    const response = await read('/guest/v1/evening?limit=24' + (pageCursor ? `&cursor=${encodeURIComponent(pageCursor)}` : ''), controller.signal);
    const view = await response.json();
    if (epoch !== requestEpoch) return;
    if (view.schemaVersion !== 1 || (view.evening && !uuid.test(view.evening.id))) throw new Error('Invalid public schema');
    const changed = view.evening?.id !== eveningId;
    if (changed) { clearScope(); followsLatest = true; }
    eveningId = view.evening?.id;
    const firstPage = Array.isArray(view.items) ? view.items.slice(0, 24).map(projectItem).filter(Boolean) : [];
    latest = projectItem(view.latest);
    if (latest && !assetOf(latest, 'base_image')) latest = undefined;
    // Preserve a prior choice only while the freshly read page still publishes it.
    // Missing/deleted editions must not survive solely in an old browser buffer.
    const existingSelection = !changed && selected ? firstPage.find(item => itemKey(item) === itemKey(selected)) : null;
    items = firstPage; olderPage = !!pageCursor;
    nextCursor = typeof view.nextCursor === 'string' && view.nextCursor.length <= 2048 ? view.nextCursor : undefined;
    setSelected(followsLatest || !existingSelection
      ? latest || firstPage.find(item => assetOf(item, 'base_image')) || firstPage[0]
      : existingSelection);
    text('evening-label', safeText(view.evening?.label, 160) || 'Aktivní večer');
    renderList(); renderSelection();
    visible('evening-empty', !selected);
    text('empty-title', eveningId ? 'První příběh je ještě před námi' : 'Večer ještě nezačal');
    text('empty-copy', eveningId ? 'Archivář čeká na první připravenou hůlku. Tenhle odkaz si můžeš nechat otevřený.' : 'Jakmile obsluha zahájí večer, objeví se tady jeho hůlky a příběhy.');
    status(selected ? (latest && selected && itemKey(selected) !== itemKey(latest) ? 'Čteš dřívější hůlku. Nejnovější připravená edice je také v přehledu.' : 'Aktivní večer · přehled se průběžně obnovuje.') : 'Zatím žádná připravená edice.');
    schedule();
  } catch (error) {
    if (epoch !== requestEpoch || error.name === 'AbortError') return;
    if (error.status === 409 && pageCursor) {
      clearScope(); eveningId = undefined; followsLatest = true; refreshing = false;
      await refresh(); return;
    }
    handleUnavailable(error); schedule(error.retryAfterMs || 15000);
  } finally {
    if (epoch === requestEpoch) { refreshing = false; $('refresh-evening').disabled = false; $('load-more').disabled = false; }
  }
}
async function loadMore() {
  if (!nextCursor || paging || refreshing || !eveningId) return;
  paging = true; $('load-more').disabled = true;
  const epoch = requestEpoch; const previousEvening = eveningId;
  const requestedCursor = nextCursor;
  try {
    const response = await read(`/guest/v1/evening?limit=24&cursor=${encodeURIComponent(requestedCursor)}`, controller.signal);
    const view = await response.json();
    if (epoch !== requestEpoch) return;
    if (view.evening?.id !== previousEvening) { clearScope(); eveningId = undefined; followsLatest = true; await refresh(); return; }
    const incoming = Array.isArray(view.items) ? view.items.slice(0, 24).map(projectItem).filter(Boolean) : [];
    // Render one bounded page, keeping the chosen reading edition above it.
    items = incoming; olderPage = true; pageCursor = requestedCursor;
    nextCursor = typeof view.nextCursor === 'string' && view.nextCursor.length <= 2048 ? view.nextCursor : undefined;
    renderList();
  } catch (error) {
    if (epoch !== requestEpoch || error.name === 'AbortError') return;
    if (error.status === 409) { clearScope(); eveningId = undefined; followsLatest = true; await refresh(); }
    else status('Další příběhy se nepodařilo načíst. Zkus načtení znovu.');
  } finally { if (epoch === requestEpoch) { paging = false; $('load-more').disabled = false; } }
}
async function download(button) {
  const item = selected; const asset = assetOf(item, button.dataset.publicDownload);
  if (!asset || !eveningId || hasPendingDownload(asset.kind)) return;
  const transfer = { controller: new AbortController(), eveningId, itemKey: itemKey(item),
    kind: asset.kind, assetSignature: assetSignature(asset) };
  pendingDownloads.add(transfer);
  button.disabled = true;
  try {
    const response = await read(assetPath(item, asset), transfer.controller.signal); const blob = await response.blob();
    if (transfer.controller.signal.aborted || !downloadStillCurrent(transfer)) return;
    const mediaType = mediaEssence(asset.mediaType);
    if (mediaType !== kindMediaTypes[asset.kind]) throw new Error('Unsupported public file');
    const extension = { 'image/png': '.png', 'text/plain': '.txt', 'application/zip': '.zip' }[mediaType];
    if (!extension) throw new Error('Unsupported public file');
    const filename = { base_image: 'wand-art', export_story: 'story', export_card: 'keepsake-card', export_zip: 'bundle' }[asset.kind] + extension;
    const file = new File([blob], filename, { type: mediaType });
    const url = URL.createObjectURL(blob); fileUrls.add(url);
    const dialog = document.createElement('dialog'); dialog.dataset.publicFile = 'true';
    const title = document.createElement('h2'); title.textContent = 'Památka je připravená k uložení';
    const note = document.createElement('p'); note.textContent = 'Na iPhonu použij Sdílet → Uložit do Souborů; u obrázku případně Uložit obrázek. Uložení do Fotek potvrzuje až systém.';
    const link = document.createElement('a'); link.href = url; link.download = filename; link.className = 'aw-button'; link.textContent = 'Stáhnout soubor';
    dialog.append(title, note, link);
    if (navigator.canShare?.({ files: [file] })) {
      const share = document.createElement('button'); share.className = 'aw-button aw-button--secondary'; share.textContent = 'Sdílet soubor';
      share.addEventListener('click', async () => {
        try { await navigator.share({ files: [file] }); }
        catch (error) { if (error.name !== 'AbortError') status('Systémové sdílení není dostupné. Použij stažení souboru.'); }
      }); dialog.append(share);
    }
    const close = document.createElement('button'); close.className = 'text-button'; close.textContent = 'Zavřít'; close.addEventListener('click', () => dialog.close()); dialog.append(close);
    dialog.addEventListener('close', () => { URL.revokeObjectURL(url); fileUrls.delete(url); dialog.remove(); });
    document.body.append(dialog); dialog.showModal();
  } catch (error) {
    if (!transfer.controller.signal.aborted && downloadStillCurrent(transfer) && error.name !== 'AbortError') status('Soubor není dostupný. Obnov přehled a ověř dostupné části edice.');
  } finally {
    pendingDownloads.delete(transfer);
    button.disabled = !assetOf(selected, button.dataset.publicDownload) || hasPendingDownload(button.dataset.publicDownload);
  }
}
$('refresh-evening').addEventListener('click', refresh);
$('load-more').addEventListener('click', loadMore);
$('first-page').addEventListener('click', () => { pageCursor = undefined; refresh(); });
$('select-latest').addEventListener('click', () => select(latest, false));
$('public-art-open').addEventListener('click', () => $('public-art-dialog').showModal());
$('close-public-art').addEventListener('click', () => $('public-art-dialog').close());
$('public-art').addEventListener('error', () => { clearArtwork(); text('public-art-status', 'Obraz se nepodařilo zobrazit. Obnov přehled.'); });
for (const button of document.querySelectorAll('[data-public-download]')) button.addEventListener('click', () => download(button));
document.addEventListener('visibilitychange', () => {
  clearTimeout(refreshTimer);
  if (document.hidden) { requestEpoch++; controller?.abort(); refreshing = false; paging = false; clearScope(); }
  else refresh();
});
window.addEventListener('pagehide', () => { requestEpoch++; controller?.abort(); clearTimeout(refreshTimer); refreshing = false; paging = false; clearScope(); });
window.addEventListener('pageshow', event => { if (event.persisted) refresh(); });
await refresh();

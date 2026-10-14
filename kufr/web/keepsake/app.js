const $ = id => document.getElementById(id);
let entry = window.dv10Entry;
delete window.dv10Entry;
let csrfToken;
let routeEpoch = 0;
let controller;
let timer;
let locator;
let readOnly = false;
let sharedEditionId;
let record;
let edition;
let busy = false;
let localPhoto;
let previewUrl;
let deletionReceipt;
let deleteScope;
let linkKind;
let lastRender;
let pollStarted = Date.now();
const objectUrls = new Set();
const mutationKeys = new Map();

function text(id, value) { $(id).textContent = value ?? ''; }
function visible(id, shown) { $(id).hidden = !shown; }
function feedback(message) { text('feedback', message); visible('feedback', !!message); }
function owner() { return !!record && !readOnly && record.state !== 'deleted'; }
function apiPath(suffix = '') { return `/guest/v1/keepsakes/${encodeURIComponent(locator)}${suffix}`; }
function scoped(path) { return path + (readOnly ? '?view=share&editionId=' + encodeURIComponent(sharedEditionId || '') : ''); }
function releaseObjectUrls() {
  for (const url of objectUrls) URL.revokeObjectURL(url);
  objectUrls.clear();
  previewUrl = undefined;
}
function clearPrivateView() {
  record = undefined; edition = undefined; localPhoto = undefined; lastRender = undefined;
  delete $('story-body').dataset.content;
  releaseObjectUrls();
  for (const id of ['wand-art', 'full-art', 'photo-preview']) $(id).removeAttribute('src');
  for (const id of ['wand-name', 'edition-title', 'summary', 'story-body', 'portrait-current', 'portrait-history', 'link-value']) {
    const element = $(id);
    if (element instanceof HTMLInputElement) element.value = ''; else element.replaceChildren();
  }
  $('photo').value = ''; $('photo-consent').checked = false; $('include-portrait').checked = false;
  visible('issued-link', false); visible('keepsake', false);
  for (const dialog of document.querySelectorAll('dialog[open]')) dialog.close();
}
class GuestError extends Error {
  constructor(status, code, delay) { super('Guest request failed'); this.status = status; this.code = code; this.delay = delay; }
}
async function request(path, { method = 'GET', data, bytes, mediaType, consentVersion, signal } = {}) {
  const epoch = routeEpoch;
  const body = method === 'GET' ? undefined : bytes || JSON.stringify({ schemaVersion: 1, ...data });
  const mutationIdentity = method === 'GET' ? undefined : `${method}:${path}:${bytes ? `${bytes.name}:${bytes.size}:${bytes.lastModified}:${consentVersion}` : body}`;
  const headers = { Accept: 'application/json' };
  if (method !== 'GET') {
    if (!mutationKeys.has(mutationIdentity)) mutationKeys.set(mutationIdentity, crypto.randomUUID());
    headers['Idempotency-Key'] = mutationKeys.get(mutationIdentity);
    if (csrfToken) headers['X-DV10-CSRF'] = csrfToken;
    headers['Content-Type'] = mediaType || 'application/json';
    if (consentVersion != null) headers['X-DV10-Consent'] = String(consentVersion);
  }
  const response = await fetch(path, {
    method, headers, credentials: 'same-origin', cache: 'no-store', redirect: 'error',
    body, signal: signal || controller?.signal
  });
  if (epoch !== routeEpoch) throw new DOMException('Route changed', 'AbortError');
  if (!response.ok) {
    if (response.status < 500) mutationKeys.delete(mutationIdentity);
    let error = {};
    try { error = await response.json(); } catch { /* The error body is deliberately never displayed or logged. */ }
    throw new GuestError(response.status, error.errorCode || error.code, Number(response.headers.get('Retry-After')) * 1000);
  }
  const result = response.status === 204 ? {} : await response.json();
  if (epoch !== routeEpoch) throw new DOMException('Route changed', 'AbortError');
  mutationKeys.delete(mutationIdentity);
  return result;
}
async function bootstrap() {
  // Fresh tabs share a cookie jar. Serializing cookie initialization prevents
  // one tab from consuming a claim into a session replaced by another tab.
  if (!navigator.locks) throw new GuestError(503, 'browser_session_lock_unavailable');
  await navigator.locks.request('dv10-browser-session', async () => {
    const result = await request('/guest/v1/bootstrap', { method: 'POST' });
    if (result.schemaVersion !== 1 || !result.csrfToken) throw new GuestError(503);
    csrfToken = result.csrfToken;
  });
}
function showError(error, mutation = false) {
  if (error.name === 'AbortError') return;
  if ([401, 403, 404, 410].includes(error.status)) {
    clearPrivateView(); visible('entry', true);
    text('entry-title', 'Odkaz není dostupný');
    text('entry-copy', 'Použij platný osobní nebo návratový odkaz. Pokud jej nemáš, požádej obsluhu kufru.');
    visible('exchange', false); visible('retry', false);
    feedback('Přístup se nepodařilo ověřit.');
    return;
  }
  feedback(error.status === 429
    ? 'Archiv dostává příliš mnoho požadavků. Zkus to za chvíli.'
    : mutation ? 'Výsledek akce ještě nemáme potvrzený. Obnov stav památky; nové generování automaticky nespouštíme.'
      : 'Archiv je teď nedostupný. Zkus obnovit spojení.');
  visible('retry', !!locator);
}
async function action(button, work) {
  if (busy) return;
  busy = true;
  const label = button.textContent;
  button.disabled = true; button.textContent = 'Chvilku…';
  const epoch = routeEpoch;
  try { await work(); } catch (error) { if (epoch === routeEpoch) showError(error, true); }
  finally { busy = false; button.textContent = label; button.disabled = false; updateActions(); }
}
function assetUrl(id) { return scoped(apiPath(`/assets/${encodeURIComponent(id)}`)); }
function stateText(job, fallback) {
  if (!job) return fallback;
  const labels = {
    queued: 'Čeká na přípravu.', running: 'Připravuje se.', leased: 'Připravuje se.', dispatched: 'Připravuje se.',
    completed: 'Připraveno.', ready: 'Připraveno.', failed: 'Nepodařilo se dokončit.',
    cancelled: 'Příprava byla zastavena.', provider_unknown: 'Výsledek ještě nemáme potvrzený. Nespouštíme zbytečně druhou kopii.',
    outcome_unknown: 'Výsledek ještě nemáme potvrzený. Nespouštíme zbytečně druhou kopii.',
    awaiting_reconciliation: 'Výsledek ještě nemáme potvrzený. Nespouštíme zbytečně druhou kopii.',
    archived_private: 'Výstup je uchovaný v soukromé historii.'
  };
  return labels[job.state] || 'Stav přípravy není potvrzený.';
}
function activePortrait() {
  return record?.portraits?.filter(p => p.editionId === edition?.id
    && record.assets?.some(a => a.id === p.assetId && a.kind === 'portrait_image')).at(-1);
}
function sharedExport(format) {
  return record?.assets?.find(a => a.editionId === edition?.id && a.kind === 'export_' + (format === 'bundle' ? 'zip' : format));
}
function updateActions() {
  const baseJob = record?.jobs?.filter(j => j.kind === 'base').at(-1);
  visible('cancel-base', owner() && baseJob && ['queued', 'leased', 'dispatched', 'awaiting_reconciliation'].includes(baseJob.state));
  $('cancel-base').disabled = busy;
  for (const button of document.querySelectorAll('[data-export]')) {
    const format = button.dataset.export;
    button.disabled = busy || !edition || (format === 'art' && !edition.artworkAssetId)
      || (format === 'portrait' && (!owner() || !activePortrait()))
      || (readOnly && format !== 'art' && !sharedExport(format))
      || (format === 'card' && (!edition.story || !edition.artworkAssetId));
  }
  $('generate-portrait').disabled = busy || !owner() || !edition || !localPhoto || !$('photo-consent').checked;
  $('include-portrait').disabled = !owner() || !activePortrait();
  $('share-preview').disabled = busy || !owner() || !edition;
}
function render(view) {
  const signature = JSON.stringify(view);
  record = view;
  readOnly = new URLSearchParams(location.search).get('view') === 'share' || !!view.readOnly;
  edition = view.sharedEditionId
    ? view.editions?.find(item => item.id === view.sharedEditionId)
    : view.editions?.filter(e => e.publicationState !== 'private_history').at(-1);
  if (signature === lastRender) return;
  lastRender = signature;
  visible('entry', false); visible('keepsake', true); visible('deletion', false);
  visible('owner-tools', owner()); visible('portrait-section', owner());
  text('scope', readOnly ? 'Sdílená edice · pouze čtení' : 'Soukromá památka');
  text('wand-name', view.snapshot?.officialName || 'Název hůlky není potvrzený');
  text('catalog-label', view.snapshot?.slot ? `Hůlka z přihrádky ${view.snapshot.slot}` : 'Hůlka, která si tě vybrala');
  text('edition-title', edition?.story?.title);
  text('summary', edition?.story?.text?.split(/\n\s*\n/)[0]);
  const story = $('story-body');
  const storyText = edition?.story?.text || '';
  if (story.dataset.content !== storyText) {
    story.dataset.content = storyText;
    story.replaceChildren();
    if (storyText) {
      for (const paragraph of storyText.split(/\n\s*\n/)) {
        const node = document.createElement('p'); node.textContent = paragraph; story.append(node);
      }
    } else {
      const node = document.createElement('p');
      node.textContent = 'Archivář ještě píše její průšvihy. Dostupné části památky si můžeš uložit už teď.';
      story.append(node);
    }
  }
  const art = $('wand-art');
  const hasArt = !!edition?.artworkAssetId;
  visible('wand-art', hasArt); visible('art-status', !hasArt); $('art-open').disabled = !hasArt;
  if (hasArt) {
    const url = assetUrl(edition.artworkAssetId);
    if (art.getAttribute('src') !== url) art.src = url;
    $('full-art').src = url;
  } else { art.removeAttribute('src'); $('full-art').removeAttribute('src'); }
  const generationState = edition?.story && hasArt ? 'Edice je připravená.'
      : edition?.story ? 'Příběh už je hotový. Obraz se připravuje.'
        : hasArt ? 'Obraz je připravený. Příběh ještě není dokončený.'
        : view.state === 'paused_preparing' ? 'Příprava je pozastavená. Záznam hůlky zůstává uložený.'
          : view.state === 'awaiting_capacity' ? 'Příprava čeká na volnou kapacitu. Záznam hůlky zůstává uložený.'
            : stateText(view.jobs?.filter(job => job.kind === 'base').at(-1), 'Hůlku máme. Archivář ještě píše její průšvihy.');
  text('generation-status', (edition?.synthetic ? 'SYNTETICKÁ UKÁZKA · tento výstup neprokazuje generování OpenAI. ' : '') + generationState);
  const portrait = activePortrait();
  const holder = $('portrait-current'); holder.replaceChildren();
  if (owner() && portrait) {
    const image = document.createElement('img'); image.src = assetUrl(portrait.assetId); image.alt = 'Soukromý kouzelnický portrét'; holder.append(image);
    if (portrait.synthetic) { const label = document.createElement('p'); label.textContent = 'SYNTETICKÁ UKÁZKA PORTRÉTU'; holder.append(label); }
  }
  const portraitJob = view.jobs?.filter(job => job.kind === 'portrait').at(-1);
  text('portrait-status', stateText(portraitJob, portrait ? 'Portrét je připravený.' : 'Chceš i vlastní kouzelnický portrét?'));
  visible('cancel-portrait', owner() && ['queued', 'running', 'leased', 'dispatched', 'provider_unknown', 'awaiting_reconciliation'].includes(portraitJob?.state));
  updateActions();
}
function schedulePoll(delay) {
  clearTimeout(timer);
  if (document.hidden || !locator || !record) return;
  const pending = record.jobs?.some(job => !['completed', 'ready', 'failed', 'cancelled', 'archived_private'].includes(job.state));
  if (pending) timer = setTimeout(load, Math.min(30000, Math.max(3000, delay || (Date.now() - pollStarted < 30000 ? 3000 : 10000))) + Math.random() * 700);
}
async function load() {
  if (!locator || document.hidden) return;
  const epoch = routeEpoch;
  try {
    const view = await request(scoped(apiPath()), { signal: controller.signal });
    if (epoch !== routeEpoch) return;
    render(view); feedback(''); visible('retry', false); schedulePoll(view.nextPollAfterMs);
  } catch (error) {
    if (epoch !== routeEpoch) return;
    showError(error);
    if (error.status === 429 && record) schedulePoll(error.delay || 30000);
  }
}
async function route() {
  routeEpoch++; controller?.abort(); controller = new AbortController(); clearTimeout(timer);
  mutationKeys.clear();
  clearPrivateView(); $('privacy').close(); visible('deletion', false); visible('entry', true); feedback('');
  const query = new URLSearchParams(location.search);
  locator = query.get('k'); readOnly = query.get('view') === 'share'; sharedEditionId = query.get('editionId'); pollStarted = Date.now();
  if (entry?.token) {
    visible('public-overview', false);
    locator = undefined;
    const messages = {
      c: ['Památka čeká za pečetí', 'Otevři osobní příběh a digitální obraz hůlky z tohoto večera.', 'Otevřít moji památku'],
      r: ['Tvůj soukromý návrat', 'Tento klíč obnoví přístup ke správě památky i případnému portrétu. Použij jej pouze pro sebe.', 'Obnovit můj přístup'],
      s: ['Někdo ti posílá trochu kouzel', 'Otevřeš sdílenou hůlku a příběh. Fotografie a soukromý portrét zůstávají u autora.', 'Otevřít sdílenou edici']
    };
    const copy = messages[entry.kind]; text('entry-title', copy[0]); text('entry-copy', copy[1]); text('exchange', copy[2]); visible('exchange', true);
  } else { visible('exchange', false); visible('public-overview', !locator); if (locator) { text('entry-title', 'Otevíráme archivní list'); text('entry-copy', 'Ověřujeme přístup v tomto prohlížeči.'); await load(); } }
  if (query.has('privacy')) $('privacy').showModal();
}
$('exchange').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!entry?.token) return;
  await bootstrap();
  const kind = entry.kind;
  const paths = { c: 'claim', r: 'recover', s: 'share-claim' };
  const result = await request(`/guest/v1/${paths[kind]}`, { method: 'POST', data: { token: entry.token } });
  entry = undefined;
  const query = new URLSearchParams({ k: result.locator });
  if (kind === 's') { query.set('view', 'share'); query.set('editionId', result.editionId || ''); }
  history.replaceState(null, '', `/keepsake/?${query}`); await route();
}));
$('retry').addEventListener('click', () => load());
window.addEventListener('popstate', () => { entry = undefined; route(); });
document.addEventListener('visibilitychange', () => {
  clearTimeout(timer);
  if (document.hidden) { controller?.abort(); clearPrivateView(); }
  else { controller = new AbortController(); load(); }
});
window.addEventListener('pagehide', () => { routeEpoch++; controller?.abort(); clearTimeout(timer); clearPrivateView(); entry = undefined; });
window.addEventListener('pageshow', event => { if (event.persisted) route(); });
$('wand-art').addEventListener('error', () => {
  visible('wand-art', false); visible('art-status', true); text('art-status', 'Obraz se nepodařilo načíst. Obnov spojení.'); $('art-open').disabled = true;
});
$('art-open').addEventListener('click', () => $('art-dialog').showModal());
for (const button of document.querySelectorAll('[data-privacy]')) button.addEventListener('click', () => $('privacy').showModal());
for (const button of document.querySelectorAll('[data-close]')) button.addEventListener('click', () => button.closest('dialog').close());

async function downloadAsset(assetId, filename) {
  const epoch = routeEpoch;
  const response = await fetch(assetUrl(assetId), { credentials: 'same-origin', cache: 'no-store', redirect: 'error', signal: controller.signal });
  if (!response.ok) throw new GuestError(response.status);
  const blob = await response.blob();
  if (epoch !== routeEpoch) throw new DOMException('Route changed', 'AbortError');
  if (filename.endsWith('.png')) {
    const extension = { 'image/png': '.png', 'image/jpeg': '.jpg', 'image/webp': '.webp', 'image/svg+xml': '.svg' }[blob.type];
    if (extension) filename = filename.slice(0, -4) + extension;
  }
  const file = new File([blob], filename, { type: blob.type });
  const url = URL.createObjectURL(blob); objectUrls.add(url);
  const dialog = document.createElement('dialog');
  const heading = document.createElement('h2'); heading.textContent = 'Soubor je připravený'; dialog.append(heading);
  const note = document.createElement('p'); note.textContent = 'Na iPhonu použij Sdílet → Uložit do Souborů; u obrázku případně Uložit obrázek. Stažení nepotvrzuje uložení do Fotek.'; dialog.append(note);
  const anchor = document.createElement('a'); anchor.className = 'aw-button'; anchor.href = url; anchor.download = filename; anchor.textContent = 'Stáhnout soubor'; dialog.append(anchor);
  if (navigator.canShare?.({ files: [file] })) {
    const button = document.createElement('button'); button.className = 'aw-button aw-button--secondary'; button.textContent = 'Sdílet soubor';
    button.addEventListener('click', async () => {
      try { await navigator.share({ files: [file] }); } catch (error) { if (error.name !== 'AbortError') feedback('Systémové sdílení není dostupné. Použij stažení souboru.'); }
    }); dialog.append(button);
  }
  const close = document.createElement('button'); close.className = 'text-button'; close.textContent = 'Zavřít'; close.addEventListener('click', () => dialog.close()); dialog.append(close);
  dialog.addEventListener('close', () => { URL.revokeObjectURL(url); objectUrls.delete(url); dialog.remove(); });
  document.body.append(dialog); dialog.showModal();
}
for (const button of document.querySelectorAll('[data-export]')) button.addEventListener('click', event => action(event.currentTarget, async () => {
  if (!edition) return;
  const format = button.dataset.export;
  if (format === 'art') return downloadAsset(edition.artworkAssetId, 'wand-art.png');
  if (format === 'portrait') return downloadAsset(activePortrait().assetId, 'magical-portrait.png');
  if (readOnly) {
    const asset = sharedExport(format);
    if (asset) await downloadAsset(asset.id, { bundle: 'bundle.zip', card: 'keepsake-card.png', story: 'story.txt' }[format]);
    return;
  }
  await ensureCsrf();
  const result = await request(scoped(apiPath('/exports')), { method: 'POST', data: {
    editionId: edition.id, format: format === 'bundle' ? 'zip' : format,
    portraitId: owner() && format === 'bundle' && $('include-portrait').checked ? activePortrait()?.id : null
  } });
  await downloadAsset(result.asset.id, { bundle: 'bundle.zip', card: 'keepsake-card.png', story: 'story.txt' }[format]);
}));
async function ensureCsrf() { if (!csrfToken) await bootstrap(); }
function showLink(result, kind) {
  const url = new URL(result.url, location.origin);
  if (url.origin !== location.origin || url.pathname !== '/keepsake/' || !url.hash.startsWith(kind === 'share' ? '#s=' : '#r=')) throw new GuestError(503);
  linkKind = kind; $('link-value').value = url.href;
  text('link-purpose', kind === 'share' ? 'Pouze hůlka a příběh. Tento odkaz můžeš poslat ostatním.' : 'Soukromý klíč ke správě a portrétu. Ulož jej pouze pro sebe.');
  visible('issued-link', true); visible('native-share', kind === 'share' && !!navigator.share);
}
$('recovery').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner()) return; await ensureCsrf(); showLink(await request(apiPath('/recovery-links'), { method: 'POST' }), 'recovery');
}));
$('share-preview').addEventListener('click', () => {
  text('share-description', `${record.snapshot.officialName} · ${edition?.story?.title || 'Dostupná základní edice'}`); $('share-dialog').showModal();
});
$('issue-share').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner() || !edition) return; await ensureCsrf();
  showLink(await request(apiPath('/share-links'), { method: 'POST', data: { editionId: edition.id } }), 'share'); $('share-dialog').close();
}));
$('copy-link').addEventListener('click', async () => {
  try { await navigator.clipboard.writeText($('link-value').value); feedback('Odkaz byl zkopírován.'); }
  catch { $('link-value').select(); feedback('Označený odkaz zkopíruj ručně.'); }
});
$('native-share').addEventListener('click', async () => {
  if (linkKind !== 'share') return;
  try { await navigator.share({ title: 'Památka z kufru', url: $('link-value').value }); }
  catch (error) { if (error.name !== 'AbortError') feedback('Sdílení není dostupné. Zkopíruj odkaz.'); }
});
for (const [id, suffix] of [['revoke-shares', '/share-links'], ['revoke-recovery', '/recovery-links']]) {
  $(id).addEventListener('click', event => action(event.currentTarget, async () => {
    if (!owner()) return; await ensureCsrf(); await request(apiPath(suffix), { method: 'DELETE' });
    $('link-value').value = ''; visible('issued-link', false); feedback('Příslušné odkazy byly odvolány.');
  }));
}
$('revoke-access').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner() || !confirm('Odvolat všechny přístupy včetně tohoto prohlížeče a návratových odkazů? Památka zůstane uložená. Nový přístup pak musí obnovit obsluha kufru.')) return;
  await ensureCsrf();
  await request(apiPath('/revoke-access'), { method: 'POST', data: { confirm: 'delete', expectedRevision: record.revision } });
  clearPrivateView(); clearTimeout(timer); visible('entry', true);
  text('entry-title', 'Přístupy byly odvolány'); text('entry-copy', 'Památka zůstává uložená. Pro nový přístup požádej obsluhu kufru.');
}));
$('photo').addEventListener('change', () => {
  localPhoto = undefined;
  if (previewUrl) { URL.revokeObjectURL(previewUrl); objectUrls.delete(previewUrl); }
  visible('photo-preview', false); $('photo-preview').removeAttribute('src'); $('photo-consent').checked = false;
  const file = $('photo').files?.[0];
  if (file && (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size > 10 * 1024 * 1024)) {
    $('photo').value = ''; feedback('Vyber JPEG, PNG nebo WebP do 10 MB. HEIC nejprve převeď na JPEG.');
  } else if (file) {
    localPhoto = file; previewUrl = URL.createObjectURL(file); objectUrls.add(previewUrl);
    $('photo-preview').src = previewUrl; visible('photo-preview', true); feedback('Fotografie je zatím jen v tomto prohlížeči. Nebyla odeslána.');
  }
  updateActions();
});
$('photo-consent').addEventListener('change', updateActions);
$('generate-portrait').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner() || !localPhoto || !$('photo-consent').checked || !edition) return;
  const photo = localPhoto; const editionId = edition.id; await ensureCsrf();
  const consent = await request(apiPath('/portrait-consents'), { method: 'POST', data: { accepted: true, policyVersion: 'dv10-portrait-v1' } });
  const upload = await request(apiPath('/portrait-uploads'), { method: 'POST', bytes: photo, mediaType: photo.type, consentVersion: consent.consentVersion });
  await request(apiPath('/portraits'), { method: 'POST', data: { uploadId: upload.upload.id, editionId, variant: 'portrait_only', consentVersion: consent.consentVersion } });
  localPhoto = undefined; $('photo').value = ''; $('photo-consent').checked = false; visible('photo-preview', false);
  if (previewUrl) { URL.revokeObjectURL(previewUrl); objectUrls.delete(previewUrl); previewUrl = undefined; }
  feedback('Fotografie byla přijata. Portrét se připravuje.'); await load();
}));
$('cancel-portrait').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner()) return; const job = record.jobs.filter(item => item.kind === 'portrait').at(-1); if (!job) return;
  await ensureCsrf(); await request(apiPath(`/portraits/${encodeURIComponent(job.id)}/cancel`), { method: 'POST' }); await load();
}));
$('cancel-base').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner()) return; const job = record.jobs.filter(j => j.kind === 'base').at(-1); if (!job) return;
  await ensureCsrf(); await request(apiPath(`/generation/${encodeURIComponent(job.id)}/cancel`), { method: 'POST' });
  feedback('Příprava byla zastavena. Již vytvořený obsah zůstává uložený.'); await load();
}));
$('revoke-consent').addEventListener('click', () => { if (owner()) openDelete('portrait'); });
$('load-history').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner()) return;
  const history = await request(apiPath('/history'));
  const holder = $('portrait-history'); holder.replaceChildren();
  for (const portrait of history.portraits || []) {
    const button = document.createElement('button'); button.className = 'text-button';
    button.textContent = `${portrait.synthetic ? 'Syntetická ukázka · ' : ''}Portrét ${new Date(portrait.createdAt).toLocaleString('cs')}`;
    button.addEventListener('click', event => action(event.currentTarget, () => downloadAsset(portrait.assetId, 'magical-portrait.png'))); holder.append(button);
  }
  if (!holder.childNodes.length) holder.textContent = 'Žádné dřívější portréty.';
}));
function openDelete(scope) {
  deleteScope = scope; text('delete-title', scope === 'all' ? 'Smazat celou památku?' : 'Odvolat souhlas a smazat fotografie i portréty?');
  $('delete-confirm').value = ''; $('delete-submit').disabled = true; $('delete-dialog').showModal();
}
for (const [id, scope] of [['delete-portrait', 'portrait'], ['delete-all', 'all']]) $(id).addEventListener('click', () => openDelete(scope));
$('delete-confirm').addEventListener('input', () => { $('delete-submit').disabled = $('delete-confirm').value !== 'SMAZAT'; });
function renderDeletion(receipt) {
  deletionReceipt = receipt;
  text('deletion-copy', receipt.scope === 'all' ? 'Památka už není přístupná. Odstraňujeme její uložené kopie.' : 'Fotografie a portréty už nejsou přístupné. Základní památka zůstává.');
  const labels = {
    completed: 'odstraněno', deleted: 'odstraněno', pending: 'odstranění probíhá',
    failed: 'odstranění nebylo dokončeno', not_confirmed: 'odstranění nepotvrzeno',
    unknown_not_provider_confirmed: 'odstranění poskytovatelem nepotvrzeno',
    pending_independent_backup_reconciliation: 'čeká na nezávislé odstranění záloh',
    not_applicable: 'nevztahuje se', unavailable: 'stav nedostupný'
  };
  const label = state => labels[state] || 'stav nepotvrzený';
  text('deletion-status', `Místní kopie: ${label(receipt.localState)}. Poskytovatel: ${label(receipt.providerState)}. Zálohy: ${label(receipt.backupState)}.`);
  visible('deletion', true); visible('entry', false);
}
$('delete-submit').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!owner() || $('delete-confirm').value !== 'SMAZAT') return;
  await ensureCsrf(); const expectedRevision = record.revision;
  const result = await request(apiPath(deleteScope === 'portrait' ? '/portrait' : ''), { method: 'DELETE', data: { confirm: 'delete', expectedRevision } });
  $('delete-dialog').close(); clearPrivateView(); clearTimeout(timer); renderDeletion(result.receipt);
  if (deleteScope === 'portrait') { await load(); renderDeletion(result.receipt); }
}));
$('deletion-refresh').addEventListener('click', event => action(event.currentTarget, async () => {
  if (!deletionReceipt?.id) return;
  const result = await request(`/guest/v1/deletions/${encodeURIComponent(deletionReceipt.id)}`); renderDeletion(result.receipt);
}));
await route();

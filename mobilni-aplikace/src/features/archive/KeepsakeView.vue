<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { consumeGuestClaim } from '../../application/guestClaim';
import { account, accountRequest, offlineSession } from '../identity/session';
import AwDialog from '../../components/AwDialog.vue';
import { GuestError, deletionLabel, generationLabel, guestPath, isUuid, parseArchiveReadiness, parseClaim, safeIssuedLink, type ArchiveReadiness, type Keepsake, type Portrait, type DeletionReceipt } from './client';
import { createArchiveClient, guestOrigin, shareGuestLink } from './nativeGuest';
import { useArchiveMedia, type ArchiveMediaClient } from './useArchiveMedia';
import { AccountArchiveClient } from './accountArchive';
import ArchiveDialog from './ArchiveDialog.vue';
import ArchiveDownload from './ArchiveDownload.vue';
import WizardIdentity from './WizardIdentity.vue';
import PrivateProphecy from './PrivateProphecy.vue';
import { publicIdentity, ownerProphecy } from './showContracts';
import './archive.css';
const route = useRoute(); const router = useRouter();
const client = createArchiveClient();
const accountClient = new AccountArchiveClient(() => account.value?.id ?? '');
const activeClient: ArchiveMediaClient = {
  capture: () => accountRead.value ? accountClient.capture() : client.capture(),
  blob: (path, allowed, publicRead) => accountRead.value ? accountClient.blob(path, allowed) : client.blob(path, allowed, publicRead),
};
const media = useArchiveMedia(activeClient);
const portraitMedia = useArchiveMedia(activeClient);
const claim = ref(parseClaim(consumeGuestClaim()));
const record = ref<Keepsake>();
type AccountAccess = { accessKind: 'originalOwner' | 'linkedViewer'; canManage: boolean; canReadPrivate: boolean; canUnlink: boolean };
const accountAccess = ref<AccountAccess>();
const history = ref<Portrait[]>([]);
const busy = ref(false);
const message = ref('');
const readinessInvalid = ref(false);
const automaticRefreshes = ref(0);
const maxAutomaticRefreshes = 12;
const privacy = ref(false); const artworkOpen = ref(false); const sharePreview = ref(false);
const associationOpen = ref(false);
const unlinkOpen = ref(false); const unlinkConfirmation = ref('');
const issuedLink = ref(''); const linkKind = ref<'share' | 'recovery'>('share');
const photo = shallowRef<File>(); const previewUrl = ref(''); const photoConsent = ref(false); const includePortrait = ref(false);
const photoInput = ref<HTMLInputElement>();
const deletion = ref<DeletionReceipt>(); const deleteScope = ref<'all' | 'portrait'>(); const confirmation = ref('');
let timer: ReturnType<typeof setTimeout> | undefined; let loadSequence = 0; let actionGeneration = 0;
let readInFlight: Promise<void> | undefined;
let robots: HTMLMetaElement | undefined;
let ownsRobots = false;
const locator = computed(() => typeof route.query.k === 'string' && isUuid(route.query.k) ? route.query.k : '');
const shareEdition = computed(() => route.query.view === 'share' ? typeof route.query.editionId === 'string' ? route.query.editionId : '' : undefined);
const accountRead = computed(() => route.query.account === '1' && shareEdition.value === undefined);
const accountOwner = computed(() => accountRead.value && accountAccess.value?.accessKind === 'originalOwner' && accountAccess.value.canManage && accountAccess.value.canReadPrivate && !accountAccess.value.canUnlink);
const accountCanUnlink = computed(() => accountRead.value && accountAccess.value?.accessKind === 'linkedViewer' && !accountAccess.value.canManage && accountAccess.value.canUnlink);
const readOnly = computed(() => shareEdition.value !== undefined || record.value?.readOnly === true);
const owner = computed(() => !!record.value && (accountRead.value ? accountOwner.value : !readOnly.value) && record.value.state !== 'deleted');
const canReadPortrait = computed(() => owner.value || accountRead.value && accountCanUnlink.value && record.value?.accountLinked === true);
const readinessVisible = computed(() => !!record.value?.readiness && record.value.state !== 'deleted' && !readinessInvalid.value && shareEdition.value === undefined
  && (!accountRead.value || !!accountAccess.value));
const readinessNotice = computed(() => {
  if (readinessInvalid.value && record.value?.state !== 'deleted' && (!accountRead.value || !!accountAccess.value)) return 'Stav přípravy se nepodařilo ověřit.';
  if (readinessVisible.value) return readinessLabel(record.value!.readiness!);
  if (accountRead.value) return null;
  const hasStory = !!edition.value?.story;
  const hasArtwork = !!edition.value?.artworkAssetId;
  return hasStory && hasArtwork ? 'Edice je připravená.' : `${generationLabel(baseJob.value?.state ?? record.value?.state)} Dostupné části si můžeš uložit už teď.`;
});
const storyPlaceholder = computed(() => readinessPlaceholder('story'));
const artworkPlaceholder = computed(() => readinessPlaceholder('artwork'));
const canManuallyCheckReadiness = computed(() => readinessVisible.value && record.value?.readiness?.shouldPoll === true
  && automaticRefreshes.value >= maxAutomaticRefreshes && !offlineSession.value && navigator.onLine);
const edition = computed(() => record.value?.sharedEditionId ? record.value.editions.find(item => item.id === record.value?.sharedEditionId) : record.value?.editions.filter(item => item.publicationState !== 'private_history').at(-1));
const portrait = computed(() => record.value?.portraits.filter(item => isUuid(item.assetId) && item.editionId === edition.value?.id && record.value?.assets.some(asset => asset.id === item.assetId && asset.kind === 'portrait_image')).at(-1));
const paragraphs = computed(() => edition.value?.story?.text.split(/\n\s*\n/) ?? []);
const identity = computed(() => record.value?.wizardIdentities?.find(item => item.editionId === edition.value?.id));
const prophecy = computed(() => owner.value ? record.value?.prophecies?.find(item => item.editionId === edition.value?.id) : undefined);
const baseJob = computed(() => record.value?.jobs.filter(job => job.kind === 'base').at(-1));
const portraitJob = computed(() => record.value?.jobs.filter(job => job.kind === 'portrait').at(-1));
const pending = (state?: string) => !!state && !['completed', 'ready', 'failed', 'cancelled', 'archived_private'].includes(state);
function readinessPlaceholder(kind: 'story' | 'artwork') {
  const readiness = readinessVisible.value ? record.value?.readiness : undefined;
  const name = kind === 'story' ? 'Příběh' : 'Obraz';
  if (!readiness) return kind === 'story' ? 'Archivář ještě píše její průšvihy. Dostupné části památky si můžeš uložit už teď.' : 'Obraz se připravuje';
  if (readiness.state === 'failed' || readiness.reason === 'generation_failed') return `${name} se nepodařilo dokončit.`;
  if (readiness.state === 'unavailable' || readiness.reason === 'result_unavailable') return `${name} není dostupný.`;
  if (readiness.reason === 'reconciliation_required') return kind === 'story' ? 'Příběh se ověřuje.' : 'Výsledek obrazu se ještě ověřuje.';
  if (kind === 'story' && readiness.hasStory) return 'Příběh se načítá.';
  if (kind === 'artwork' && readiness.hasArtwork) return 'Obraz se načítá.';
  if (readiness.state === 'ready') return `${name} není dostupný.`;
  return `${name} se připravuje.`;
}
function readinessLabel(readiness: ArchiveReadiness) {
  if (readiness.reason === 'result_unavailable') return 'Výsledek přípravy není dostupný.';
  switch (readiness.state) {
    case 'pending':
      if (readiness.reason === 'awaiting_capacity') return 'Příprava čeká na volnou kapacitu.';
      if (readiness.reason === 'paused_preparing') return 'Příprava je pozastavená.';
      return 'Čekáme na výsledek přípravy.';
    case 'processing':
      return readiness.reason === 'reconciliation_required'
        ? 'Výsledek přípravy ještě ověřujeme. Nové generování nespouštíme.'
        : 'Příprava probíhá.';
    case 'partial':
      if (readiness.reason === 'reconciliation_required') return 'Příběh je připravený, výsledek obrazu ještě ověřujeme. Nové generování nespouštíme.';
      if (readiness.reason === 'generation_failed') return 'Příběh je připravený, obraz se nepodařilo dokončit.';
      if (readiness.hasStory) return 'Příběh je připravený, obraz se ještě připravuje.';
      if (readiness.hasArtwork) return 'Obraz je připravený, příběh se ještě připravuje.';
      return 'Část výsledku je připravená. Zkontroluj stav znovu později.';
    case 'ready': return 'Edice je připravená.';
    case 'failed': return 'Přípravu se nepodařilo dokončit.';
    case 'unavailable': return 'Výsledek přípravy není dostupný.';
  }
}
const path = (suffix = '') => guestPath(locator.value, suffix, shareEdition.value);
const associationPath = (suffix = '') => `/app/v1/archive/associations/${locator.value}${suffix}`;
const ownedPath = (suffix = '') => `/app/v1/archive/owned/${locator.value}${suffix}`;
const accountPath = () => accountOwner.value ? ownedPath() : associationPath();
const assetPath = (id: string) => { if (!isUuid(id)) throw new GuestError(400); return accountRead.value ? `${accountPath()}/assets/${id}` : path(`/assets/${id}`); };
function validateAccountAccess(value: Keepsake): AccountAccess | null {
  const projection = value as Keepsake & Partial<AccountAccess>;
  if (projection.accessKind === 'originalOwner'
    && value.readOnly === false
    && projection.canManage === true
    && projection.canReadPrivate === true
    && projection.canUnlink === false) {
    return { accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false };
  }
  if (projection.accessKind === 'linkedViewer'
    && value.readOnly === true
    && value.accountLinked === true
    && projection.canManage === false
    && projection.canReadPrivate === false
    && projection.canUnlink === true
    && Array.isArray(value.portraits)
    && Array.isArray((value as Keepsake & { uploads?: unknown[] }).uploads)
    && (value as Keepsake & { uploads?: unknown[] }).uploads?.length === 0) {
    return { accessKind: 'linkedViewer', canManage: false, canReadPrivate: false, canUnlink: true };
  }
  return null;
}
function mutateArchive<T>(suffix: string, accept: () => void, options: { method?: 'POST' | 'DELETE'; data?: object; bytes?: Blob; consentVersion?: number } = {}) {
  return accountRead.value
    ? accountClient.json<T>(ownedPath(suffix), { ...options, method: options.method ?? 'POST', accept })
    : client.mutate<T>(path(suffix), { ...options, accept });
}
function clearPhoto() {
  photo.value = undefined; photoConsent.value = false;
  if (previewUrl.value) URL.revokeObjectURL(previewUrl.value); previewUrl.value = '';
  if (photoInput.value) photoInput.value.value = '';
}
function clearPrivate() { record.value = undefined; accountAccess.value = undefined; readinessInvalid.value = false; history.value = []; issuedLink.value = ''; clearPhoto(); includePortrait.value = false; media.clear(); portraitMedia.clear(); artworkOpen.value = false; sharePreview.value = false; associationOpen.value = false; unlinkOpen.value = false; unlinkConfirmation.value = ''; deleteScope.value = undefined; confirmation.value = ''; }
function invalidate() { loadSequence++; actionGeneration++; client.invalidate(); accountClient.invalidate(); clearTimeout(timer); automaticRefreshes.value = 0; busy.value = false; clearPrivate(); }
function suspendNetworkReads(clearPrivateView = false) {
  loadSequence++; readInFlight = undefined; actionGeneration++; clearTimeout(timer); automaticRefreshes.value = 0; busy.value = false;
  if (clearPrivateView) clearPrivate();
}
function report(error: unknown, mutation = false) {
  if ((error as Error).name === 'AbortError') return;
  if (error instanceof GuestError && [401, 403, 404, 410].includes(error.status)) { invalidate(); claim.value = null; message.value = accountRead.value ? 'Tento účet nemá platný přístup k památce. Vrať se do svého archivu nebo se přihlas správným účtem.' : 'Přístup není dostupný. Použij platný osobní nebo návratový odkaz, případně požádej obsluhu kufru.'; }
  else message.value = mutation ? 'Výsledek akce ještě nemáme potvrzený. Obnov stav památky; nové generování automaticky nespouštíme.' : 'Archiv je teď nedostupný. Zkus obnovit spojení.';
}
function scheduleAutoRefresh(readiness: ArchiveReadiness | null, view: Keepsake, malformed: boolean) {
  const readinessAllowed = shareEdition.value === undefined && view.state !== 'deleted'
    && (!accountRead.value || !!accountAccess.value);
  const activePortraitJob = readinessAllowed && owner.value
    && view.jobs.some(job => job.kind === 'portrait' && pending(job.state));
  const lockedOwnerProphecy = readinessAllowed && owner.value
    && (view.prophecies ?? []).some(item => item.state === 'locked');
  const shouldPoll = readinessAllowed && (readiness?.shouldPoll === true || activePortraitJob || lockedOwnerProphecy)
    || (!readiness && !malformed && !accountRead.value && shareEdition.value === undefined
      && (view.jobs.some(job => pending(job.state)) || (view.prophecies ?? []).some(item => item.state === 'locked')));
  if (!shouldPoll || automaticRefreshes.value >= maxAutomaticRefreshes || document.hidden || offlineSession.value || !navigator.onLine) return;
  clearTimeout(timer);
  timer = setTimeout(() => {
    if (document.hidden || offlineSession.value || !navigator.onLine) return;
    if (busy.value || readInFlight) { scheduleAutoRefresh(readiness, view, malformed); return; }
    automaticRefreshes.value++;
    void load(true);
  }, 5000);
}
async function load(automatic = false) {
  if (!locator.value || claim.value || deletion.value?.scope === 'all' || document.hidden || offlineSession.value || !navigator.onLine) return;
  if (accountRead.value && !account.value) { message.value = 'Pro soukromý archiv se přihlas svým účtem.'; return; }
  if (!automatic) { automaticRefreshes.value = 0; clearTimeout(timer); }
  if (automatic && (busy.value || readInFlight)) {
    if (record.value) scheduleAutoRefresh(record.value.readiness ?? null, record.value, readinessInvalid.value);
    return;
  }
  clearTimeout(timer); const sequence = ++loadSequence; const check = activeClient.capture();
  const request = (async () => {
    try {
      const view = accountRead.value ? await accountClient.json<Keepsake>(accountPath()) : await client.json<Keepsake>(path()); check(); if (sequence !== loadSequence) return;
      if (view.schemaVersion !== 1 || view.locator !== locator.value || !Array.isArray(view.editions) || !Array.isArray(view.assets) || !Array.isArray(view.jobs) || !Array.isArray(view.portraits)) throw new GuestError(503);
      const rawReadiness = view.readiness;
      const readiness = parseArchiveReadiness(rawReadiness);
      const malformedReadiness = rawReadiness !== undefined && rawReadiness !== null && readiness === null;
      view.readiness = readiness;
      const access = accountRead.value ? validateAccountAccess(view) : null;
      if (accountRead.value && !access) { clearPrivate(); throw new GuestError(503); }
      const oldArt = edition.value?.artworkAssetId;
      const oldPortrait = portrait.value?.assetId;
      view.wizardIdentities = (view.wizardIdentities ?? []).map(publicIdentity).filter(item => item !== null);
      view.prophecies = (accountRead.value ? access?.canReadPrivate === true : shareEdition.value === undefined && view.readOnly !== true)
        ? (view.prophecies ?? []).map(ownerProphecy).filter(item => item !== null) : [];
      accountAccess.value = access ?? undefined;
      readinessInvalid.value = malformedReadiness;
      record.value = view;
      if (access?.accessKind === 'originalOwner') history.value = view.portraits;
      if (oldArt !== edition.value?.artworkAssetId || !media.artworkUrl.value) await media.artwork(edition.value?.artworkAssetId ? assetPath(edition.value.artworkAssetId) : undefined);
      check(); if (sequence !== loadSequence) return;
      if (oldPortrait !== portrait.value?.assetId || !portraitMedia.artworkUrl.value) await portraitMedia.artwork(portrait.value?.assetId ? assetPath(portrait.value.assetId) : undefined);
      check(); if (sequence !== loadSequence) return;
      scheduleAutoRefresh(readiness, view, malformedReadiness);
    } catch (error) { if (sequence === loadSequence) report(error); }
  })();
  readInFlight = request;
  try { await request; }
  finally { if (readInFlight === request) readInFlight = undefined; }
}
function manualCheckReadiness() { void load(); }
async function action(work: (check: () => void) => Promise<void>) {
  if (busy.value || document.hidden) return;
  if (readInFlight) { loadSequence++; readInFlight = undefined; }
  busy.value = true; clearTimeout(timer); const generation = actionGeneration; const transportCheck = activeClient.capture();
  const check = () => { transportCheck(); if (generation !== actionGeneration) throw new DOMException('Archive action context changed', 'AbortError'); };
  message.value = '';
  try { check(); clearTimeout(timer); await work(check); check(); }
  catch (error) { if (generation === actionGeneration) report(error, true); }
  finally {
    if (generation === actionGeneration) {
      busy.value = false;
      if (record.value) scheduleAutoRefresh(record.value.readiness ?? null, record.value, readinessInvalid.value);
    }
  }
}
async function exchange() {
  await action(async check => {
    const entry = claim.value; if (!entry) return;
    const endpoint = { c: 'claim', r: 'recover', s: 'share-claim' }[entry.kind];
    const result = await client.mutate<{ locator: string; editionId?: string }>(`/guest/v1/${endpoint}`, { data: { token: entry.token }, accept: check }); check();
    if (!isUuid(result.locator) || (entry.kind === 's' && !isUuid(result.editionId))) throw new GuestError(503);
    claim.value = null;
    await router.replace({ path: '/keepsake/', query: { k: result.locator, ...(entry.kind === 's' ? { view: 'share', editionId: result.editionId } : {}) } });
  });
}
function choosePhoto(event: Event) {
  const file = (event.target as HTMLInputElement).files?.[0]; clearPhoto();
  if (!file) return;
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size < 1 || file.size > 8 * 1024 * 1024) { message.value = 'Vyber JPEG, PNG nebo WebP nejvýše 8 MiB.'; return; }
  photo.value = file; previewUrl.value = URL.createObjectURL(file);
}
async function generatePortrait() {
  await action(async check => {
    if (!owner.value || !photo.value || !photoConsent.value || !edition.value) return;
    const file = photo.value; const editionId = edition.value.id;
    const consent = await mutateArchive<{ consentVersion: number }>('/portrait-consents', check, { data: { accepted: true, policyVersion: 'dv10-portrait-v1' } }); check();
    const upload = await mutateArchive<{ upload: { id: string } }>('/portrait-uploads', check, { bytes: file, consentVersion: consent.consentVersion }); check();
    await mutateArchive('/portraits', check, { data: { uploadId: upload.upload.id, editionId, variant: 'portrait_only', consentVersion: consent.consentVersion } }); check();
    clearPhoto(); message.value = 'Fotografie byla přijata. Portrét se připravuje.'; await load();
  });
}
async function cancelJob(kind: 'portrait' | 'base') {
  await action(async check => { const job = kind === 'base' ? baseJob.value : portraitJob.value; if (!owner.value || !job || !isUuid(job.id)) return;
    await mutateArchive(`/${kind === 'base' ? 'generation' : 'portraits'}/${job.id}/cancel`, check); check(); await load(); });
}
const exportNames: Record<string, string> = { art: 'wand-art.png', portrait: 'magical-portrait.png', story: 'story.txt', card: 'keepsake-card.png', zip: 'bundle.zip' };
function canExport(format: string) {
  if (!edition.value || busy.value) return false;
  if (format === 'art') return !!edition.value.artworkAssetId;
  if (format === 'portrait') return canReadPortrait.value && !!portrait.value;
  if (readOnly.value) return record.value?.assets.some(asset => asset.editionId === edition.value?.id && asset.kind === `export_${format}`);
  return format !== 'card' || !!edition.value.story && (!!edition.value.artworkAssetId || !!identity.value);
}
async function createIdentity() {
  await action(async check => {
    if (!owner.value || !edition.value || identity.value) return;
    await client.mutate(path('/wizard-identity'), { data: { editionId: edition.value.id }, accept: check }); check();
    await load(); check();
  });
}
async function exportFile(format: string) {
  await action(async check => {
    if (!edition.value) return;
    let id: string | undefined;
    if (format === 'art') id = edition.value.artworkAssetId;
    else if (format === 'portrait' && canReadPortrait.value) id = portrait.value?.assetId;
    else if (readOnly.value) id = record.value?.assets.find(asset => asset.editionId === edition.value?.id && asset.kind === `export_${format}`)?.id;
    else if (owner.value) {
      const result = await mutateArchive<{ asset: { id: string } }>('/exports', check, { data: { editionId: edition.value.id, format, portraitId: format === 'zip' && includePortrait.value ? portrait.value?.id ?? null : null } }); check(); id = result.asset.id;
    }
    if (id) await media.download(assetPath(id), exportNames[format], format === 'zip' ? ['application/zip'] : format === 'story' ? ['text/plain'] : ['image/png', 'image/jpeg', 'image/webp', 'image/svg+xml']);
  });
}
async function issueLink(kind: 'share' | 'recovery') {
  await action(async check => { if (!owner.value || (kind === 'share' && !edition.value)) return;
    const result = await mutateArchive<{ url: string }>(kind === 'share' ? '/share-links' : '/recovery-links', check, { data: kind === 'share' ? { editionId: edition.value!.id } : undefined }); check();
    const origin = await guestOrigin(); check();
    issuedLink.value = safeIssuedLink(result.url, kind, origin); linkKind.value = kind; sharePreview.value = false;
  });
}
async function revokeLinks(kind: 'share' | 'recovery') {
  await action(async check => { if (!owner.value) return; await mutateArchive(kind === 'share' ? '/share-links' : '/recovery-links', check, { method: 'DELETE' }); check(); issuedLink.value = ''; message.value = kind === 'share' ? 'Sdílené odkazy byly odvolány. Památka zůstává uložená.' : 'Návratové odkazy byly odvolány. Památka zůstává uložená.'; });
}
async function associateAccount() {
  await action(async check => {
    const targetAccountId = account.value?.id;
    if (!owner.value || !targetAccountId || !associationOpen.value) return;
    const ticket = await client.mutate<{ ticket: string }>(path('/account-associations'), { data: { targetAccountId }, accept: check }); check();
    if (account.value?.id !== targetAccountId) throw new DOMException('Account changed', 'AbortError');
    await accountRequest('/app/v1/archive/associations', 'POST', { ticket: ticket.ticket }); check();
    if (account.value?.id !== targetAccountId) throw new DOMException('Account changed', 'AbortError');
    associationOpen.value = false; message.value = 'Památka a soukromé výsledné portréty byly připojeny k tomuto účtu. Návštěvnický přístup zůstává zachovaný.';
  });
}
async function revokeAccess() {
  if (!owner.value || !confirm('Odvolat všechny návštěvnické a návratové přístupy? Památka a její soubory zůstanou uložené; účtu se tímto propojení také odebere.')) return;
  await action(async check => { await mutateArchive('/revoke-access', check, { data: { confirm: 'delete', expectedRevision: record.value!.revision } }); check(); invalidate(); message.value = 'Všechny přístupy byly odvolány. Památka zůstává uložená.'; });
}
async function loadHistory() {
  await action(async check => { if (!owner.value) return; if (accountRead.value) { history.value = record.value?.portraits ?? []; return; } const result = await client.json<Keepsake>(path('/history')); check(); history.value = result.portraits ?? []; });
}
async function downloadPortrait(item: Portrait) { await action(async () => { if (owner.value) await media.download(assetPath(item.assetId), 'magical-portrait.png', ['image/png', 'image/jpeg', 'image/webp']); }); }
async function deleteContent() {
  await action(async check => {
    if (!owner.value || confirmation.value !== 'SMAZAT' || !deleteScope.value) return;
    const scope = deleteScope.value;
    const result = await mutateArchive<{ receipt: DeletionReceipt }>(scope === 'all' ? '' : '/portrait', check, { method: 'DELETE', data: { confirm: 'delete', expectedRevision: record.value!.revision } }); check();
    invalidate(); deletion.value = result.receipt; if (scope === 'portrait') await load();
  });
}
async function refreshDeletion() {
  await action(async check => { if (!deletion.value || !isUuid(deletion.value.id)) return; const result = accountRead.value
    ? await accountClient.json<{ receipt: DeletionReceipt }>(`/app/v1/archive/owned/deletions/${deletion.value.id}`)
    : await client.json<{ receipt: DeletionReceipt }>(`/guest/v1/deletions/${deletion.value.id}`); check(); deletion.value = result.receipt; });
}
async function unlinkAccount() {
  await action(async check => {
    if (!accountCanUnlink.value || unlinkConfirmation.value !== 'ODPOJIT') return;
    await accountClient.json(associationPath(), { method: 'DELETE' }); check();
    invalidate(); message.value = 'Památka byla odpojena od tohoto účtu. Uložené soubory i návštěvnický přístup vlastníka zůstávají zachované.';
  });
}
async function copyLink() { const check = client.capture(); try { await navigator.clipboard.writeText(issuedLink.value); check(); message.value = 'Odkaz byl zkopírován.'; } catch { try { check(); message.value = 'Kopírování není dostupné. Označ odkaz a zkopíruj jej ručně.'; } catch { /* Inactive route. */ } } }
async function shareLink() {
  if (linkKind.value !== 'share' || !issuedLink.value) return;
  const check = client.capture();
  try { await shareGuestLink(issuedLink.value); check(); }
  catch (error) { try { check(); if ((error as Error).name !== 'AbortError') message.value = 'Sdílení není dostupné. Zkopíruj odkaz.'; } catch { /* Inactive route. */ } }
}
async function shareFile() {
  const check = client.capture();
  try { await media.shareFile(); check(); }
  catch (error) { try { check(); if ((error as Error).name !== 'AbortError') message.value = 'Systémové sdílení není dostupné. Použij stažení souboru.'; } catch { /* Inactive route. */ } }
}
function resetRoute() { invalidate(); deletion.value = undefined; message.value = ''; privacy.value = route.query.privacy !== undefined; if (accountRead.value) claim.value = null; if (!claim.value) void load(); }
function suspend() { invalidate(); claim.value = null; }
function visibility() { if (document.hidden) suspendNetworkReads(true); else void load(); }
function pageShow(event: PageTransitionEvent) { if (event.persisted) void load(); }
function networkOffline() { suspendNetworkReads(); }
function networkOnline() { if (!offlineSession.value) void load(); }
watch(() => route.fullPath, () => { claim.value = null; resetRoute(); });
watch(() => account.value?.id, () => {
  invalidate();
  if (!claim.value && !offlineSession.value) void load();
}, { flush: 'sync' });
watch(offlineSession, offline => {
  if (offline) suspendNetworkReads(true);
  else if (!claim.value) void load();
}, { flush: 'sync' });
onMounted(() => {
  robots = document.head.querySelector<HTMLMetaElement>('meta[name="robots"]') ?? undefined;
  if (!robots) { robots = document.createElement('meta'); robots.name = 'robots'; document.head.append(robots); ownsRobots = true; }
  robots.content = 'noindex, nofollow, noarchive';
  document.addEventListener('visibilitychange', visibility); window.addEventListener('pagehide', suspend); window.addEventListener('pageshow', pageShow);
  window.addEventListener('offline', networkOffline); window.addEventListener('online', networkOnline); resetRoute();
});
onBeforeUnmount(() => { suspend(); if (ownsRobots) robots?.remove(); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', suspend); window.removeEventListener('pageshow', pageShow); window.removeEventListener('offline', networkOffline); window.removeEventListener('online', networkOnline); });
</script>

<template>
  <section class="archive-view" aria-labelledby="keepsake-title">
    <p v-if="message" class="aw-notice" role="status" aria-live="polite">{{ message }}</p>
    <section v-if="!record && !deletion" class="aw-card">
      <h1 id="keepsake-title">{{ accountRead ? 'Soukromý archiv účtu' : claim?.kind === 'r' ? 'Tvůj soukromý návrat' : claim?.kind === 's' ? 'Někdo ti posílá trochu kouzel' : 'Památka čeká za pečetí' }}</h1>
      <p v-if="claim">{{ claim.kind === 's' ? 'Otevřeš sdílenou hůlku a příběh. Fotografie a soukromý portrét zůstávají u autora.' : 'Otevři osobní příběh a digitální obraz hůlky. Soukromý odkaz uchovávej jen pro sebe.' }}</p>
      <p v-else-if="accountRead">Památku otevře pouze účet, ke kterému ji její vlastník výslovně připojil.</p><p v-else-if="locator">Ověřujeme přístup v tomto prohlížeči.</p><p v-else>Použij svůj osobní nebo návratový odkaz, případně otevři veřejný přehled večera.</p>
      <button v-if="claim" class="aw-button" :disabled="busy" @click="exchange">{{ busy ? 'Ověřuji…' : claim.kind === 'r' ? 'Obnovit můj přístup' : claim.kind === 's' ? 'Otevřít sdílenou edici' : 'Otevřít moji památku' }}</button>
      <RouterLink v-else to="/archive">Otevřít hůlky večera</RouterLink>
      <RouterLink v-if="accountRead && !account" to="/signin">Přihlásit se</RouterLink>
    </section>
    <article v-if="record" aria-labelledby="keepsake-title">
      <p class="aw-badge">{{ accountOwner ? 'Soukromá památka · vlastník účtu' : accountCanUnlink ? 'Propojená památka · pouze čtení' : accountRead ? 'Ověřujeme oprávnění účtu' : readOnly ? 'Sdílená edice · pouze čtení' : 'Soukromá památka' }}</p>
      <div class="archive-edition"><div><button class="archive-art-button" :disabled="!media.artworkUrl.value" aria-label="Zvětšit digitální obraz hůlky" @click="artworkOpen = true"><img v-if="media.artworkUrl.value" :src="media.artworkUrl.value" alt="Osobní digitální interpretace vybrané hůlky" class="archive-art" @error="media.clearArtwork(); media.artworkError.value = true"><span v-else>{{ media.artworkError.value ? 'Obraz se nepodařilo načíst. Obnov spojení.' : artworkPlaceholder }}</span></button><p class="archive-caption">Osobní AI interpretace · fantasy obraz, nikoli fotografie rekvizity</p></div>
        <div><h1 id="keepsake-title">{{ record.snapshot.officialName }}</h1><p>Hůlka z přihrádky {{ record.snapshot.slot }}</p><h2>{{ edition?.story?.title }}</h2><p>{{ paragraphs[0] }}</p>
          <p class="aw-notice" role="status" aria-live="polite" :data-readiness-state="readinessInvalid ? 'invalid' : readinessVisible ? record.readiness?.state : undefined">{{ readinessNotice }}</p>
          <button v-if="canManuallyCheckReadiness" class="aw-button aw-button--secondary" :disabled="busy" @click="manualCheckReadiness">Zkontrolovat stav</button>
          <p v-if="edition?.synthetic" class="aw-notice">SYNTETICKÁ UKÁZKA · nejde o výstup placeného generování.</p>
          <div class="archive-actions"><button v-for="format in ['zip', 'story', 'art', 'card']" :key="format" class="aw-button aw-button--secondary" :disabled="!canExport(format)" @click="exportFile(format)">{{ ({ zip: 'Uložit památku ZIP', story: 'Příběh TXT', art: 'Obraz hůlky', card: 'Kartička PNG' } as Record<string, string>)[format] }}</button></div>
          <label v-if="owner"><input v-model="includePortrait" type="checkbox" :disabled="!portrait || busy">Přidat soukromý portrét do mého ZIP</label>
          <button v-if="owner && pending(baseJob?.state)" class="aw-button aw-button--secondary" :disabled="busy" @click="cancelJob('base')">Zastavit přípravu památky</button>
        </div></div>
      <WizardIdentity :identity="identity" />
      <button v-if="owner && !accountRead && edition && !identity" class="aw-button aw-button--secondary" :disabled="busy" @click="createIdentity">Vytvořit kouzelnickou identitu této edice</button>
      <p v-if="owner && !accountRead && edition && !identity" class="archive-caption">Doplní autorskou fikci pro tuto edici bez dalšího AI volání. Obrázek tím nevznikne.</p>
      <p v-if="identity && !edition?.artworkAssetId" class="archive-muted">Identita je připravená jako text a kartička. Obraz není potvrzený; nic jej nenahrazuje. ZIP a PNG mohou obsahovat pouze dostupné části.</p>
      <PrivateProphecy v-if="owner" :prophecy="prophecy" />
      <p v-if="accountRead && !accountOwner" class="archive-muted">Soukromé proroctví je dostupné jen vlastníkovi památky. Propojení účtu zpřístupňuje jen uložené výsledné portréty; zdrojové fotografie zůstávají soukromé.</p>
      <section class="archive-story"><h2>Příběh jedné nepravděpodobné hůlky</h2><p class="archive-caption">Kouzelnická fikce pro tento večer</p><p v-for="(paragraph, index) in paragraphs" :key="index">{{ paragraph }}</p><p v-if="!paragraphs.length">{{ storyPlaceholder }}</p></section>
      <section v-if="accountRead && canReadPortrait && portrait" class="aw-card"><h2>Soukromý kouzelnický portrét</h2><p>Výsledný portrét byl společně s památkou výslovně připojen k tomuto účtu. Zdrojová fotografie není součástí tohoto přístupu.</p><img v-if="portraitMedia.artworkUrl.value" :src="portraitMedia.artworkUrl.value" class="archive-private-preview" alt="Soukromý kouzelnický portrét"><p v-if="portrait.synthetic">SYNTETICKÁ UKÁZKA PORTRÉTU</p><button class="aw-button aw-button--secondary" :disabled="!canExport('portrait')" @click="exportFile('portrait')">Uložit portrét</button></section>
      <section v-if="owner" class="aw-card" aria-labelledby="portrait-title"><h2 id="portrait-title">Soukromý kouzelnický portrét</h2><p>Výběr fotografie vytvoří jen místní náhled. Teprve tlačítko a aktuální souhlas ji odešlou ke zpracování OpenAI. Veřejné sdílení portrét neobsahuje.</p>
        <label for="archive-photo">Fotografie JPEG, PNG nebo WebP do 8 MiB</label><input id="archive-photo" ref="photoInput" type="file" accept="image/jpeg,image/png,image/webp" :disabled="busy" @change="choosePhoto">
        <img v-if="previewUrl" :src="previewUrl" alt="Místní náhled vybrané fotografie" class="archive-private-preview">
        <label><input v-model="photoConsent" type="checkbox" :disabled="busy">Souhlasím s odesláním této fotografie a jejím zpracováním OpenAI pro kouzelnický portrét (dv10-portrait-v1).</label>
        <button class="aw-button" :disabled="busy || !photo || !photoConsent || !edition" @click="generatePortrait">Odeslat fotografii a vytvořit portrét</button>
        <p v-if="portraitJob">Příprava portrétu: {{ generationLabel(portraitJob.state) }}</p>
        <img v-if="portraitMedia.artworkUrl.value" :src="portraitMedia.artworkUrl.value" class="archive-private-preview" alt="Soukromý kouzelnický portrét">
        <div class="archive-actions"><button v-if="pending(portraitJob?.state)" class="aw-button aw-button--secondary" :disabled="busy" @click="cancelJob('portrait')">Zastavit přípravu portrétu</button><button class="aw-button aw-button--secondary" :disabled="!canExport('portrait')" @click="exportFile('portrait')">Uložit portrét</button><button class="aw-button aw-button--ghost" :disabled="busy" @click="loadHistory">Zobrazit historii portrétů</button></div>
        <p v-if="portrait?.synthetic">SYNTETICKÁ UKÁZKA PORTRÉTU</p>
        <ul v-if="history.length"><li v-for="item in history" :key="item.id"><button class="aw-button aw-button--ghost" :disabled="busy" @click="downloadPortrait(item)">{{ item.synthetic ? 'Syntetická ukázka · ' : '' }}Portrét {{ new Date(item.createdAt).toLocaleString('cs') }}</button></li></ul>
        <button class="aw-button aw-button--danger" :disabled="busy" @click="deleteScope = 'portrait'; confirmation = ''">Odvolat souhlas a smazat fotografie i portréty</button>
      </section>
      <section v-if="owner" class="aw-card"><h2>Přístup a uchování</h2><p>Památka zůstává uložená do ručního smazání. Expirace nebo odvolání odkazu ji nemaže.</p>
        <section v-if="!accountRead" aria-labelledby="account-association-title"><h3 id="account-association-title">Můj účet</h3><p v-if="account">Připojení zpřístupní hůlku, příběh a soukromé výsledné portréty účtu {{ account.displayName }}. Zdrojové fotografie ani správu návštěvnického přístupu účtu nepředává.</p><p v-else>Pro uchování přístupu ve svém účtu se nejprve přihlas. Připojení bude vyžadovat samostatné potvrzení.</p><button v-if="account" class="aw-button aw-button--secondary" :disabled="busy" @click="associationOpen = true">Připojit k mému účtu</button><RouterLink v-else to="/signin">Přihlásit se k účtu</RouterLink></section>
        <div class="archive-actions"><button class="aw-button aw-button--secondary" :disabled="busy || !edition" @click="sharePreview = true">Náhled sdílení</button><button class="aw-button aw-button--secondary" :disabled="busy" @click="issueLink('recovery')">Vytvořit soukromý návratový odkaz</button><button class="aw-button aw-button--ghost" :disabled="busy" @click="revokeLinks('share')">Odvolat sdílené odkazy</button><button class="aw-button aw-button--ghost" :disabled="busy" @click="revokeLinks('recovery')">Odvolat návratové odkazy</button><button class="aw-button aw-button--danger" :disabled="busy" @click="revokeAccess">Odvolat všechny přístupy</button><button class="aw-button aw-button--danger" :disabled="busy" @click="deleteScope = 'all'; confirmation = ''">Smazat celou památku</button></div>
        <div v-if="issuedLink"><p>{{ linkKind === 'share' ? 'Pouze hůlka a příběh. Odkaz můžeš poslat ostatním.' : 'Soukromý klíč ke správě a portrétu. Ulož jej pouze pro sebe.' }}</p><label for="archive-issued-link">{{ linkKind === 'share' ? 'Sdílený odkaz' : 'Soukromý návratový odkaz' }}</label><input id="archive-issued-link" class="aw-input" :value="issuedLink" readonly autocomplete="off" @focus="($event.target as HTMLInputElement).select()"><div class="archive-actions"><button class="aw-button aw-button--secondary" @click="copyLink">Zkopírovat odkaz</button><button v-if="linkKind === 'share'" class="aw-button aw-button--secondary" @click="shareLink">Sdílet odkaz</button></div></div>
      </section>
    </article>
    <section v-if="accountCanUnlink && record" class="aw-card" aria-labelledby="account-unlink-title"><h2 id="account-unlink-title">Propojení s účtem</h2><p>Odpojení odebere tuto památku z tohoto účtu. Obsah, uložené soubory a návštěvnický přístup původního vlastníka zůstanou zachované.</p><button class="aw-button aw-button--secondary" :disabled="busy" @click="unlinkOpen = true; unlinkConfirmation = ''">Odpojit tuto památku od účtu</button></section>
    <section v-if="deletion" class="aw-card"><h1 v-if="!record" id="keepsake-title">Požadavek na smazání</h1><h2 v-else>Požadavek na smazání</h2><p>{{ deletion.scope === 'all' ? 'Památka už není přístupná. Odstraňujeme její uložené kopie.' : 'Fotografie a portréty už nejsou přístupné. Základní památka zůstává.' }}</p><dl><dt>Místní kopie</dt><dd>{{ deletionLabel(deletion.localState) }}</dd><dt>Poskytovatel</dt><dd>{{ deletionLabel(deletion.providerState) }}</dd><dt>Zálohy</dt><dd>{{ deletionLabel(deletion.backupState) }}</dd></dl><p>Dokončení místního smazání nepotvrzuje odstranění u poskytovatele ani v nezávislých zálohách.</p><button class="aw-button aw-button--secondary" :disabled="busy" @click="refreshDeletion">Obnovit stav mazání</button></section>
    <div class="archive-actions"><button v-if="locator && !claim && !deletion" class="aw-button aw-button--secondary" :disabled="busy" @click="manualCheckReadiness">Obnovit stav památky</button><button class="aw-button aw-button--ghost" @click="privacy = true">Soukromí a uchování dat</button></div>
    <ArchiveDialog :open="privacy" title="Soukromí a uchování dat" @close="privacy = false"><p>Archiv se uchovává do ručního smazání. Expirace přístupu ani změna večera obsah nemažou. Veřejný přehled obsahuje jen základní hůlky a příběhy; fotografie, portréty a soukromé odkazy nejsou veřejné.</p><p>Vybraná fotografie zůstává místním náhledem, dokud výslovně neodsouhlasíš a nespustíš její odeslání. Odvolání souhlasu a ruční smazání mají samostatné potvrzení. Stav místních kopií, poskytovatele a záloh se ověřuje odděleně.</p></ArchiveDialog>
    <ArchiveDialog :open="artworkOpen" title="Zvětšený obraz hůlky" @close="artworkOpen = false"><img v-if="media.artworkUrl.value" :src="media.artworkUrl.value" alt="Zvětšená digitální interpretace hůlky"></ArchiveDialog>
    <ArchiveDialog :open="sharePreview" title="Co bude sdílené" @close="sharePreview = false"><p>{{ record?.snapshot.officialName }} · {{ edition?.story?.title }}</p><p>Pouze hůlka a příběh. Fotografie a soukromý portrét nejsou součástí sdílení.</p><button class="aw-button" :disabled="busy" @click="issueLink('share')">Vytvořit sdílený odkaz</button></ArchiveDialog>
    <AwDialog :open="associationOpen" title="Připojit soukromou památku k účtu?" @close="associationOpen = false"><p>Účet {{ account?.displayName }} získá přístup ke čtení této hůlky, příběhu a jejích soukromých výsledných portrétů. Zdrojové fotografie nepředáváme. Návštěvnické vlastnictví, souhlas s generováním a ruční mazání se nemění.</p><button class="aw-button" :disabled="busy || !account || !owner" @click="associateAccount">Potvrdit připojení včetně soukromých portrétů</button></AwDialog>
    <ArchiveDialog :open="unlinkOpen" title="Odpojit památku od účtu?" @close="unlinkOpen = false"><p>Odebere se pouze propojení s tímto účtem. Památka, soubory i návštěvnický přístup původního vlastníka zůstanou zachované.</p><label for="archive-unlink-confirm">Pro potvrzení napiš ODPOJIT</label><input id="archive-unlink-confirm" v-model="unlinkConfirmation" class="aw-input" autocomplete="off"><button class="aw-button aw-button--secondary" :disabled="busy || unlinkConfirmation !== 'ODPOJIT'" @click="unlinkAccount">Potvrdit odpojení od účtu</button></ArchiveDialog>
    <ArchiveDialog :open="!!deleteScope" :title="deleteScope === 'all' ? 'Smazat celou památku?' : 'Smazat fotografie a portréty?'" @close="deleteScope = undefined"><p>Tato akce odvolá příslušný přístup a zahájí ruční smazání. Stav poskytovatele a záloh bude uveden samostatně.</p><label for="archive-delete-confirm">Pro potvrzení napiš SMAZAT</label><input id="archive-delete-confirm" v-model="confirmation" class="aw-input" autocomplete="off"><button class="aw-button aw-button--danger" :disabled="busy || confirmation !== 'SMAZAT'" @click="deleteContent">Potvrdit smazání</button></ArchiveDialog>
    <ArchiveDownload :url="media.fileUrl.value" :filename="media.fileName.value" :can-share="media.canShareFile()" :native="media.native" @close="media.clearFile" @share="shareFile" />
  </section>
</template>

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
import { customerCopy, type CustomerCopyKey } from '../../application/customerCopy';
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
const message = ref<CustomerCopyKey | null>(null);
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
  if (readinessInvalid.value && record.value?.state !== 'deleted' && (!accountRead.value || !!accountAccess.value)) return customerCopy.text('keepsake.readiness-invalid');
  if (readinessVisible.value) return readinessLabel(record.value!.readiness!);
  if (accountRead.value) return null;
  const hasStory = !!edition.value?.story;
  const hasArtwork = !!edition.value?.artworkAssetId;
  return hasStory && hasArtwork ? customerCopy.text('keepsake.readiness-ready') : `${generationLabel(baseJob.value?.state ?? record.value?.state, customerCopy.language.value)} ${customerCopy.text('keepsake.generic-ready')}`;
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
  if (!readiness) return customerCopy.text(kind === 'story' ? 'keepsake.generic-ready' : 'keepsake.readiness-unconfirmed');
  if (readiness.state === 'failed' || readiness.reason === 'generation_failed') return generationLabel('failed', customerCopy.language.value);
  if (readiness.state === 'unavailable' || readiness.reason === 'result_unavailable') return customerCopy.text(kind === 'story' ? 'keepsake.story-unavailable' : 'keepsake.art-unavailable');
  if (readiness.reason === 'reconciliation_required') return customerCopy.text('keepsake.readiness-reconcile');
  if (kind === 'story' && readiness.hasStory) return customerCopy.text('keepsake.story-loading');
  if (kind === 'artwork' && readiness.hasArtwork) return customerCopy.text('keepsake.art-loading');
  if (readiness.state === 'ready') return customerCopy.text(kind === 'story' ? 'keepsake.story-unavailable' : 'keepsake.art-unavailable');
  return generationLabel('running', customerCopy.language.value);
}
function readinessLabel(readiness: ArchiveReadiness) {
  if (readiness.reason === 'result_unavailable') return customerCopy.text('keepsake.readiness-unavailable');
  switch (readiness.state) {
    case 'pending':
      if (readiness.reason === 'awaiting_capacity' || readiness.reason === 'paused_preparing') return generationLabel(readiness.reason, customerCopy.language.value);
      return customerCopy.text('keepsake.readiness-waiting');
    case 'processing':
      return readiness.reason === 'reconciliation_required' ? customerCopy.text('keepsake.readiness-reconcile') : generationLabel('running', customerCopy.language.value);
    case 'partial':
      if (readiness.reason === 'reconciliation_required') return customerCopy.text('keepsake.readiness-partial-reconciliation');
      if (readiness.reason === 'generation_failed') return customerCopy.text('keepsake.readiness-partial-failed');
      if (readiness.hasStory) return customerCopy.text('keepsake.readiness-story-ready');
      if (readiness.hasArtwork) return customerCopy.text('keepsake.readiness-art-ready');
      return customerCopy.text('keepsake.readiness-partial');
    case 'ready': return customerCopy.text('keepsake.readiness-ready');
    case 'failed': return generationLabel('failed', customerCopy.language.value);
    case 'unavailable': return customerCopy.text('keepsake.readiness-unavailable');
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
  if (error instanceof GuestError && [401, 403, 404, 410].includes(error.status)) { invalidate(); claim.value = null; message.value = accountRead.value ? 'keepsake.error-account' : 'keepsake.error-access'; }
  else message.value = mutation ? 'keepsake.error-action' : 'keepsake.error-load';
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
  if (accountRead.value && !account.value) { message.value = 'keepsake.error-signin'; return; }
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
  message.value = null;
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
  if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size < 1 || file.size > 8 * 1024 * 1024) { message.value = 'keepsake.error-upload-type'; return; }
  photo.value = file; previewUrl.value = URL.createObjectURL(file);
}
async function generatePortrait() {
  await action(async check => {
    if (!owner.value || !photo.value || !photoConsent.value || !edition.value) return;
    const file = photo.value; const editionId = edition.value.id;
    const consent = await mutateArchive<{ consentVersion: number }>('/portrait-consents', check, { data: { accepted: true, policyVersion: 'dv10-portrait-v1' } }); check();
    const upload = await mutateArchive<{ upload: { id: string } }>('/portrait-uploads', check, { bytes: file, consentVersion: consent.consentVersion }); check();
    await mutateArchive('/portraits', check, { data: { uploadId: upload.upload.id, editionId, variant: 'portrait_only', consentVersion: consent.consentVersion } }); check();
    clearPhoto(); message.value = 'keepsake.message-uploaded'; await load();
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
  await action(async check => { if (!owner.value) return; await mutateArchive(kind === 'share' ? '/share-links' : '/recovery-links', check, { method: 'DELETE' }); check(); issuedLink.value = ''; message.value = kind === 'share' ? 'keepsake.message-share-revoked' : 'keepsake.message-recovery-revoked'; });
}
async function associateAccount() {
  await action(async check => {
    const targetAccountId = account.value?.id;
    if (!owner.value || !targetAccountId || !associationOpen.value) return;
    const ticket = await client.mutate<{ ticket: string }>(path('/account-associations'), { data: { targetAccountId }, accept: check }); check();
    if (account.value?.id !== targetAccountId) throw new DOMException('Account changed', 'AbortError');
    await accountRequest('/app/v1/archive/associations', 'POST', { ticket: ticket.ticket }); check();
    if (account.value?.id !== targetAccountId) throw new DOMException('Account changed', 'AbortError');
    associationOpen.value = false; message.value = 'keepsake.message-linked';
  });
}
async function revokeAccess() {
  if (!owner.value || !confirm(customerCopy.text('keepsake.revoke-all-confirm'))) return;
  await action(async check => { await mutateArchive('/revoke-access', check, { data: { confirm: 'delete', expectedRevision: record.value!.revision } }); check(); invalidate(); message.value = 'keepsake.message-access-revoked'; });
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
    invalidate(); message.value = 'keepsake.message-unlink';
  });
}
async function copyLink() { const check = client.capture(); try { await navigator.clipboard.writeText(issuedLink.value); check(); message.value = 'keepsake.message-copied'; } catch { try { check(); message.value = 'keepsake.message-copy-failed'; } catch { /* Inactive route. */ } } }
async function shareLink() {
  if (linkKind.value !== 'share' || !issuedLink.value) return;
  const check = client.capture();
  try { await shareGuestLink(issuedLink.value); check(); }
  catch (error) { try { check(); if ((error as Error).name !== 'AbortError') message.value = 'keepsake.message-share-failed'; } catch { /* Inactive route. */ } }
}
async function shareFile() {
  const check = client.capture();
  try { await media.shareFile(); check(); }
  catch (error) { try { check(); if ((error as Error).name !== 'AbortError') message.value = 'keepsake.message-file-share-failed'; } catch { /* Inactive route. */ } }
}
function resetRoute() { invalidate(); deletion.value = undefined; message.value = null; privacy.value = route.query.privacy !== undefined; if (accountRead.value) claim.value = null; if (!claim.value) void load(); }
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
  <section class="archive-view keepsake-view" aria-labelledby="keepsake-title">
    <p v-if="message" class="aw-notice" role="status" aria-live="polite">{{ customerCopy.text(message) }}</p>
    <section v-if="!record && !deletion" class="aw-card">
      <h1 id="keepsake-title">{{ customerCopy.text(accountRead ? 'keepsake.landing-account-title' : claim?.kind === 'r' ? 'keepsake.landing-return-title' : claim?.kind === 's' ? 'keepsake.landing-share-title' : 'keepsake.landing-title') }}</h1>
      <p v-if="claim">{{ customerCopy.text(claim.kind === 's' ? 'keepsake.landing-share-copy' : 'keepsake.landing-private-copy') }}</p>
      <p v-else-if="accountRead">{{ customerCopy.text('keepsake.landing-account-copy') }}</p><p v-else-if="locator">{{ customerCopy.text('keepsake.landing-verifying') }}</p><p v-else>{{ customerCopy.text('keepsake.landing-link-help') }}</p>
      <button v-if="claim" class="aw-button" :disabled="busy" @click="exchange">{{ customerCopy.text(busy ? 'keepsake.landing-busy' : claim.kind === 'r' ? 'keepsake.landing-return' : claim.kind === 's' ? 'keepsake.landing-share' : 'keepsake.landing-open') }}</button>
      <RouterLink v-else to="/archive">{{ customerCopy.text('keepsake.landing-archive') }}</RouterLink>
      <RouterLink v-if="accountRead && !account" to="/signin">{{ customerCopy.text('keepsake.sign-in') }}</RouterLink>
    </section>
    <article v-if="record" aria-labelledby="keepsake-title">
      <p class="aw-badge">{{ accountOwner ? customerCopy.text('keepsake.private-owner') : accountCanUnlink ? customerCopy.text('keepsake.linked-readonly') : accountRead ? customerCopy.text('archive.unverified-access') : readOnly ? customerCopy.text('keepsake.shared-readonly') : customerCopy.text('keepsake.private-owner') }}</p>
      <div class="archive-edition"><div><button class="archive-art-button" :disabled="!media.artworkUrl.value" :aria-label="customerCopy.text('keepsake.enlarged-art-alt')" @click="artworkOpen = true"><img v-if="media.artworkUrl.value" :src="media.artworkUrl.value" :alt="customerCopy.text('keepsake.art-alt')" class="archive-art" @error="media.clearArtwork(); media.artworkError.value = true"><span v-else>{{ media.artworkError.value ? customerCopy.text('keepsake.art-error') : artworkPlaceholder }}</span></button><p class="archive-caption">{{ customerCopy.text('keepsake.art-caption') }}</p></div>
        <div><h1 id="keepsake-title">{{ record.snapshot.officialName }}</h1><p>{{ customerCopy.text('keepsake.wand-slot', { slot: record.snapshot.slot }) }}</p><h2>{{ edition?.story?.title }}</h2><p>{{ paragraphs[0] }}</p>
          <p class="aw-notice" role="status" aria-live="polite" :data-readiness-state="readinessInvalid ? 'invalid' : readinessVisible ? record.readiness?.state : undefined">{{ readinessNotice }}</p>
          <button v-if="canManuallyCheckReadiness" class="aw-button aw-button--secondary" :disabled="busy" @click="manualCheckReadiness">{{ customerCopy.text('keepsake.status-check') }}</button>
          <p v-if="edition?.synthetic" class="aw-notice">{{ customerCopy.text('keepsake.synthetic') }}</p>
          <div class="archive-actions"><button v-for="format in ['zip', 'story', 'art', 'card']" :key="format" class="aw-button aw-button--secondary" :disabled="!canExport(format)" @click="exportFile(format)">{{ customerCopy.text(({ zip: 'keepsake.export-zip', story: 'keepsake.export-story', art: 'keepsake.export-art', card: 'keepsake.export-card' } as Record<string, CustomerCopyKey>)[format]) }}</button></div>
          <label v-if="owner"><input v-model="includePortrait" type="checkbox" :disabled="!portrait || busy">{{ customerCopy.text('keepsake.include-portrait') }}</label>
          <button v-if="owner && pending(baseJob?.state)" class="aw-button aw-button--secondary" :disabled="busy" @click="cancelJob('base')">{{ customerCopy.text('keepsake.stop-preparation') }}</button>
        </div></div>
      <WizardIdentity :identity="identity" />
      <button v-if="owner && !accountRead && edition && !identity" class="aw-button aw-button--secondary" :disabled="busy" @click="createIdentity">{{ customerCopy.text('keepsake.create-identity') }}</button>
      <p v-if="owner && !accountRead && edition && !identity" class="archive-caption">{{ customerCopy.text('keepsake.identity-help') }}</p>
      <p v-if="identity && !edition?.artworkAssetId" class="archive-muted">{{ customerCopy.text('keepsake.identity-text-only') }}</p>
      <PrivateProphecy v-if="owner" :prophecy="prophecy" />
      <p v-if="accountRead && !accountOwner" class="archive-muted">{{ customerCopy.text('keepsake.linked-privacy') }}</p>
      <section class="archive-story"><h2>{{ customerCopy.text('keepsake.story-heading') }}</h2><p class="archive-caption">{{ customerCopy.text('keepsake.story-caption') }}</p><p v-for="(paragraph, index) in paragraphs" :key="index">{{ paragraph }}</p><p v-if="!paragraphs.length">{{ storyPlaceholder }}</p></section>
      <section v-if="accountRead && canReadPortrait && portrait" class="aw-card"><h2>{{ customerCopy.text('keepsake.private-portrait') }}</h2><p>{{ customerCopy.text('keepsake.linked-portrait-copy') }}</p><img v-if="portraitMedia.artworkUrl.value" :src="portraitMedia.artworkUrl.value" class="archive-private-preview" :alt="customerCopy.text('keepsake.private-portrait')"><p v-if="portrait.synthetic">{{ customerCopy.text('keepsake.synthetic-portrait') }}</p><button class="aw-button aw-button--secondary" :disabled="!canExport('portrait')" @click="exportFile('portrait')">{{ customerCopy.text('keepsake.save-portrait') }}</button></section>
      <section v-if="owner" class="aw-card" aria-labelledby="portrait-title"><h2 id="portrait-title">{{ customerCopy.text('keepsake.private-portrait') }}</h2><p>{{ customerCopy.text('keepsake.upload-description') }}</p>
        <label for="archive-photo">{{ customerCopy.text('keepsake.upload-label') }}</label><input id="archive-photo" ref="photoInput" type="file" accept="image/jpeg,image/png,image/webp" :disabled="busy" @change="choosePhoto">
        <img v-if="previewUrl" :src="previewUrl" :alt="customerCopy.text('keepsake.local-photo-alt')" class="archive-private-preview">
        <label><input v-model="photoConsent" type="checkbox" :disabled="busy">{{ customerCopy.text('keepsake.photo-consent') }}</label>
        <button class="aw-button" :disabled="busy || !photo || !photoConsent || !edition" @click="generatePortrait">{{ customerCopy.text('keepsake.create-portrait') }}</button>
        <p v-if="portraitJob">{{ customerCopy.text('keepsake.portrait-preparation') }} {{ generationLabel(portraitJob.state, customerCopy.language.value) }}</p>
        <img v-if="portraitMedia.artworkUrl.value" :src="portraitMedia.artworkUrl.value" class="archive-private-preview" :alt="customerCopy.text('keepsake.private-portrait')">
        <div class="archive-actions"><button v-if="pending(portraitJob?.state)" class="aw-button aw-button--secondary" :disabled="busy" @click="cancelJob('portrait')">{{ customerCopy.text('keepsake.stop-portrait') }}</button><button class="aw-button aw-button--secondary" :disabled="!canExport('portrait')" @click="exportFile('portrait')">{{ customerCopy.text('keepsake.save-portrait') }}</button><button class="aw-button aw-button--ghost" :disabled="busy" @click="loadHistory">{{ customerCopy.text('keepsake.history') }}</button></div>
        <p v-if="portrait?.synthetic">{{ customerCopy.text('keepsake.synthetic-portrait') }}</p>
        <ul v-if="history.length"><li v-for="item in history" :key="item.id"><button class="aw-button aw-button--ghost" :disabled="busy" @click="downloadPortrait(item)">{{ item.synthetic ? customerCopy.text('keepsake.synthetic-prefix') : '' }}{{ customerCopy.text('keepsake.portrait-prefix') }} {{ new Intl.DateTimeFormat(customerCopy.language.value === 'en' ? 'en-GB' : 'cs-CZ', { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(item.createdAt)) }}</button></li></ul>
        <button class="aw-button aw-button--danger" :disabled="busy" @click="deleteScope = 'portrait'; confirmation = ''">{{ customerCopy.text('keepsake.revoke-photo-consent') }}</button>
      </section>
      <section v-if="owner" class="aw-card"><h2>{{ customerCopy.text('keepsake.retention-title') }}</h2><p>{{ customerCopy.text('keepsake.retention-copy') }}</p>
        <section v-if="!accountRead" aria-labelledby="account-association-title"><h3 id="account-association-title">{{ customerCopy.text('keepsake.account-title') }}</h3><p v-if="account">{{ customerCopy.text('keepsake.account-linked-copy', { name: account.displayName }) }}</p><p v-else>{{ customerCopy.text('keepsake.account-signin-copy') }}</p><button v-if="account" class="aw-button aw-button--secondary" :disabled="busy" @click="associationOpen = true">{{ customerCopy.text('keepsake.link-account') }}</button><RouterLink v-else to="/signin">{{ customerCopy.text('keepsake.sign-in-account') }}</RouterLink></section>
        <div class="archive-actions"><button class="aw-button aw-button--secondary" :disabled="busy || !edition" @click="sharePreview = true">{{ customerCopy.text('keepsake.share-preview') }}</button><button class="aw-button aw-button--secondary" :disabled="busy" @click="issueLink('recovery')">{{ customerCopy.text('keepsake.create-recovery') }}</button><button class="aw-button aw-button--ghost" :disabled="busy" @click="revokeLinks('share')">{{ customerCopy.text('keepsake.revoke-shares') }}</button><button class="aw-button aw-button--ghost" :disabled="busy" @click="revokeLinks('recovery')">{{ customerCopy.text('keepsake.revoke-recovery') }}</button><button class="aw-button aw-button--danger" :disabled="busy" @click="revokeAccess">{{ customerCopy.text('keepsake.revoke-all') }}</button><button class="aw-button aw-button--danger" :disabled="busy" @click="deleteScope = 'all'; confirmation = ''">{{ customerCopy.text('keepsake.delete-all-action') }}</button></div>
        <div v-if="issuedLink"><p>{{ customerCopy.text(linkKind === 'share' ? 'keepsake.share-link-copy' : 'keepsake.recovery-link-copy') }}</p><label for="archive-issued-link">{{ customerCopy.text(linkKind === 'share' ? 'keepsake.share-link-label' : 'keepsake.recovery-link-label') }}</label><input id="archive-issued-link" class="aw-input" :value="issuedLink" readonly autocomplete="off" @focus="($event.target as HTMLInputElement).select()"><div class="archive-actions"><button class="aw-button aw-button--secondary" @click="copyLink">{{ customerCopy.text('keepsake.copy-link') }}</button><button v-if="linkKind === 'share'" class="aw-button aw-button--secondary" @click="shareLink">{{ customerCopy.text('keepsake.share-link') }}</button></div></div>
      </section>
    </article>
    <section v-if="accountCanUnlink && record" class="aw-card" aria-labelledby="account-unlink-title"><h2 id="account-unlink-title">{{ customerCopy.text('keepsake.unlink-account-title') }}</h2><p>{{ customerCopy.text('keepsake.unlink-description') }}</p><button class="aw-button aw-button--secondary" :disabled="busy" @click="unlinkOpen = true; unlinkConfirmation = ''">{{ customerCopy.text('keepsake.unlink-button') }}</button></section>
    <section v-if="deletion" class="aw-card"><h1 v-if="!record" id="keepsake-title">{{ customerCopy.text('keepsake.delete-title') }}</h1><h2 v-else>{{ customerCopy.text('keepsake.delete-title') }}</h2><p>{{ customerCopy.text(deletion.scope === 'all' ? 'keepsake.deleted-pending-all' : 'keepsake.deleted-pending-media') }}</p><dl><dt>{{ customerCopy.text('keepsake.local-copies') }}</dt><dd>{{ deletionLabel(deletion.localState, customerCopy.language.value) }}</dd><dt>{{ customerCopy.text('keepsake.provider') }}</dt><dd>{{ deletionLabel(deletion.providerState, customerCopy.language.value) }}</dd><dt>{{ customerCopy.text('keepsake.backups') }}</dt><dd>{{ deletionLabel(deletion.backupState, customerCopy.language.value) }}</dd></dl><p>{{ customerCopy.text('keepsake.local-not-global') }}</p><button class="aw-button aw-button--secondary" :disabled="busy" @click="refreshDeletion">{{ customerCopy.text('keepsake.delete-status') }}</button></section>
    <div class="archive-actions"><button v-if="locator && !claim && !deletion" class="aw-button aw-button--secondary" :disabled="busy" @click="manualCheckReadiness">{{ customerCopy.text('keepsake.refresh') }}</button><button class="aw-button aw-button--ghost" @click="privacy = true">{{ customerCopy.text('keepsake.privacy-open') }}</button></div>
    <ArchiveDialog :open="privacy" :title="customerCopy.text('keepsake.privacy-heading')" @close="privacy = false"><p>{{ customerCopy.text('keepsake.privacy-copy') }}</p><p>{{ customerCopy.text('keepsake.privacy-upload') }}</p></ArchiveDialog>
    <ArchiveDialog :open="artworkOpen" :title="customerCopy.text('keepsake.artwork-title')" @close="artworkOpen = false"><img v-if="media.artworkUrl.value" :src="media.artworkUrl.value" :alt="customerCopy.text('keepsake.artwork-enlarged-alt')"></ArchiveDialog>
    <ArchiveDialog :open="sharePreview" :title="customerCopy.text('keepsake.share-preview-title')" @close="sharePreview = false"><p>{{ record?.snapshot.officialName }} · {{ edition?.story?.title }}</p><p>{{ customerCopy.text('keepsake.share-preview-copy') }}</p><button class="aw-button" :disabled="busy" @click="issueLink('share')">{{ customerCopy.text('keepsake.share-create') }}</button></ArchiveDialog>
    <AwDialog :open="associationOpen" :title="customerCopy.text('keepsake.association-title')" @close="associationOpen = false"><p>{{ customerCopy.text('keepsake.association-copy', { name: account?.displayName ?? '' }) }}</p><button class="aw-button" :disabled="busy || !account || !owner" @click="associateAccount">{{ customerCopy.text('keepsake.association-confirm') }}</button></AwDialog>
    <ArchiveDialog :open="unlinkOpen" :title="customerCopy.text('keepsake.unlink-title')" @close="unlinkOpen = false"><p>{{ customerCopy.text('keepsake.unlink-help') }}</p><label for="archive-unlink-confirm">{{ customerCopy.text('keepsake.unlink-confirm-help') }}</label><input id="archive-unlink-confirm" v-model="unlinkConfirmation" class="aw-input" autocomplete="off"><button class="aw-button aw-button--secondary" :disabled="busy || unlinkConfirmation !== 'ODPOJIT'" @click="unlinkAccount">{{ customerCopy.text('keepsake.unlink-confirm') }}</button></ArchiveDialog>
    <ArchiveDialog :open="!!deleteScope" :title="customerCopy.text(deleteScope === 'all' ? 'keepsake.delete-all-title' : 'keepsake.delete-media-title')" @close="deleteScope = undefined"><p>{{ customerCopy.text('keepsake.delete-help') }}</p><label for="archive-delete-confirm">{{ customerCopy.text('keepsake.delete-confirm-help') }}</label><input id="archive-delete-confirm" v-model="confirmation" class="aw-input" autocomplete="off"><button class="aw-button aw-button--danger" :disabled="busy || confirmation !== 'SMAZAT'" @click="deleteContent">{{ customerCopy.text('keepsake.delete-confirm') }}</button></ArchiveDialog>
    <ArchiveDownload :url="media.fileUrl.value" :filename="media.fileName.value" :can-share="media.canShareFile()" :native="media.native" @close="media.clearFile" @share="shareFile" />
  </section>
</template>

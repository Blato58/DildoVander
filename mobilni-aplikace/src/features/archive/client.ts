export interface ArchiveAsset { id: string; kind: string; mediaType: string; length: number; sha256?: string; editionId?: string }
export interface Story { title: string; text: string }
import { secureUuid } from '../../application/identifiers.mjs';
import type { Prophecy } from './showContracts';
import { projectGeneratedWand, type GeneratedWand } from './generatedWand';
export interface WizardIdentity {
  editionId: string; visibility: 'public_fiction'; wizardAlias: string; houseOrGuild: string;
  dubiousSpecialization: string; signatureAbility: string; weakness: string; ministryAssessment: string;
  wandBond: string; motto: string; microStory: string; omen: string; generatedWand?: GeneratedWand;
}
const identityLimits = { wizardAlias: 60, houseOrGuild: 160, dubiousSpecialization: 160,
  signatureAbility: 160, weakness: 160, ministryAssessment: 160, wandBond: 160,
  motto: 120, microStory: 900, omen: 160 } as const;
/** Explicit public allowlist; private phases and provenance never enter the view. */
export function publicIdentity(raw: unknown): WizardIdentity | null {
  if (!raw || typeof raw !== 'object') return null;
  const source = raw as Record<string, unknown>;
  if (source.visibility !== 'public_fiction') return null;
  const result: Record<string, string> = { visibility: 'public_fiction', editionId: isUuid(source.editionId) ? source.editionId : '' };
  for (const [field, maximum] of Object.entries(identityLimits)) {
    if (typeof source[field] !== 'string' || (source[field] as string).length > maximum) return null;
    result[field] = source[field] as string;
  }
  const generatedWand = projectGeneratedWand(source.generatedWand);
  return { ...result, ...(generatedWand ? { generatedWand } : {}) } as unknown as WizardIdentity;
}
export interface GalleryItem { locator: string; editionId: string; officialName: string; catalogId: string; slot: number | null; synthetic: boolean; story: Story; assets: ArchiveAsset[]; wizardIdentity?: WizardIdentity | null; generatedWand?: GeneratedWand | null }
export interface GalleryPage { schemaVersion: number; evening: { id: string; label: string } | null; latest: GalleryItem | null; items: GalleryItem[]; nextCursor?: string }
export interface Edition { id: string; story?: Story; artworkAssetId?: string; synthetic: boolean; publicationState: string }
export interface Portrait { id: string; editionId: string; assetId: string; synthetic: boolean; createdAt: string }
export interface Job { id: string; kind: string; state: string }
export type ArchiveReadinessState = 'pending' | 'processing' | 'partial' | 'ready' | 'failed' | 'unavailable';
export type ArchiveReadinessReason = 'awaiting_result' | 'awaiting_capacity' | 'paused_preparing' | 'reconciliation_required' | 'generation_failed' | 'result_unavailable';
export interface ArchiveReadiness {
  state: ArchiveReadinessState;
  reason: ArchiveReadinessReason | null;
  canRetry: false;
  shouldPoll: boolean;
  hasStory: boolean;
  hasArtwork: boolean;
}
const readinessStates = new Set<ArchiveReadinessState>(['pending', 'processing', 'partial', 'ready', 'failed', 'unavailable']);
const readinessReasons = new Set<ArchiveReadinessReason>(['awaiting_result', 'awaiting_capacity', 'paused_preparing', 'reconciliation_required', 'generation_failed', 'result_unavailable']);
export function parseArchiveReadiness(value: unknown): ArchiveReadiness | null {
  if (!value || typeof value !== 'object') return null;
  const source = value as Record<string, unknown>;
  if (!readinessStates.has(source.state as ArchiveReadinessState)
    || (source.reason !== null && !readinessReasons.has(source.reason as ArchiveReadinessReason))
    || source.canRetry !== false
    || typeof source.shouldPoll !== 'boolean'
    || typeof source.hasStory !== 'boolean'
    || typeof source.hasArtwork !== 'boolean') return null;
  return {
    state: source.state as ArchiveReadinessState,
    reason: source.reason as ArchiveReadinessReason | null,
    canRetry: false,
    shouldPoll: source.shouldPoll,
    hasStory: source.hasStory,
    hasArtwork: source.hasArtwork,
  };
}
export interface Keepsake { artwork?: import('./wandArtwork').WandArtwork | null; schemaVersion: number; locator: string; revision: number; state: string; readOnly: boolean; wandPublicationApproved?: boolean; accountLinked?: boolean; sharedEditionId?: string; readiness?: ArchiveReadiness | null; snapshot: { officialName: string; slot: number; generatedWand?: GeneratedWand | null }; editions: Edition[]; portraits: Portrait[]; jobs: Job[]; assets: ArchiveAsset[]; wizardIdentities?: WizardIdentity[]; prophecies?: Prophecy[] }
export interface DeletionReceipt { id: string; scope: string; localState: string; providerState: string; backupState: string }
export class GuestError extends Error {
  readonly status: number;
  readonly code?: string;
  constructor(status: number, code?: string) { super('Archiv není dostupný.'); this.status = status; this.code = code; }
}
export const isUuid = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
export const mediaEssence = (value: string) => value.split(';')[0].trim().toLowerCase();
export const publicMedia: Record<string, string> = { base_image: 'image/png', export_story: 'text/plain', export_card: 'image/png', export_zip: 'application/zip' };
export function downloadName(name: string, mediaType: string) {
  const extensions: Record<string, string> = { 'image/png': '.png', 'image/jpeg': '.jpg', 'image/webp': '.webp', 'image/svg+xml': '.svg', 'application/zip': '.zip', 'text/plain': '.txt' };
  const extension = extensions[mediaEssence(mediaType)];
  if (!extension) throw new GuestError(415);
  return name.replace(/\.[^.]+$/, '') + extension;
}
export function generationLabel(state?: string, language: 'cs' | 'en' = 'cs') {
  const labels: Record<string, Record<string, string>> = {
    cs: { queued: 'Čeká na přípravu.', running: 'Připravuje se.', leased: 'Připravuje se.', dispatched: 'Připravuje se.', completed: 'Připraveno.', ready: 'Připraveno.', failed: 'Nepodařilo se dokončit.', cancelled: 'Příprava byla zastavena.',
    archived_private: 'Výstup je uchovaný v soukromé historii.', paused_preparing: 'Příprava je pozastavená.', awaiting_capacity: 'Příprava čeká na volnou kapacitu.',
    provider_unknown: 'Výsledek ještě nemáme potvrzený.', outcome_unknown: 'Výsledek ještě nemáme potvrzený.', awaiting_reconciliation: 'Výsledek ještě nemáme potvrzený.' },
    en: { queued: 'Waiting to be prepared.', running: 'Being prepared.', leased: 'Being prepared.', dispatched: 'Being prepared.', completed: 'Ready.', ready: 'Ready.', failed: 'Could not be completed.', cancelled: 'Preparation was stopped.',
    archived_private: 'The output is kept in private history.', paused_preparing: 'Preparation is paused.', awaiting_capacity: 'Preparation is waiting for capacity.',
    provider_unknown: 'The result is not confirmed yet.', outcome_unknown: 'The result is not confirmed yet.', awaiting_reconciliation: 'The result is not confirmed yet.' },
  };
  return state && labels[language][state] || (language === 'en' ? 'Preparation status is unconfirmed.' : 'Stav přípravy není potvrzený.');
}
export function deletionLabel(state: string, language: 'cs' | 'en' = 'cs') {
  const labels: Record<string, Record<string, string>> = {
    cs: { completed: 'odstraněno', deleted: 'odstraněno', pending: 'odstranění probíhá', failed: 'odstranění nebylo dokončeno', not_confirmed: 'odstranění nepotvrzeno', unknown_not_provider_confirmed: 'odstranění poskytovatelem nepotvrzeno', pending_independent_backup_reconciliation: 'čeká na nezávislé odstranění záloh', not_applicable: 'nevztahuje se', unavailable: 'stav nedostupný' },
    en: { completed: 'removed', deleted: 'removed', pending: 'removal in progress', failed: 'removal was not completed', not_confirmed: 'removal unconfirmed', unknown_not_provider_confirmed: 'removal by provider unconfirmed', pending_independent_backup_reconciliation: 'awaiting independent backup removal', not_applicable: 'not applicable', unavailable: 'status unavailable' },
  };
  return labels[language][state] || (language === 'en' ? 'status unconfirmed' : 'stav nepotvrzený');
}
const text = (value: unknown, limit: number) => typeof value === 'string' ? value.slice(0, limit) : '';

export function projectGalleryItem(raw: any): GalleryItem | null {
  if (!raw || !isUuid(raw.locator) || !isUuid(raw.editionId)) return null;
  const identity = publicIdentity(raw.wizardIdentity);
  return {
    locator: raw.locator, editionId: raw.editionId, generatedWand: projectGeneratedWand(raw.generatedWand), officialName: text(raw.officialName, 240), catalogId: text(raw.catalogId, 120),
    slot: Number.isInteger(raw.slot) && raw.slot >= 1 && raw.slot <= 6 ? raw.slot : null, synthetic: raw.synthetic === true,
    story: { title: text(raw.story?.title, 240), text: text(raw.story?.text, 32000) }, wizardIdentity: identity?.editionId === raw.editionId ? identity : null,
    assets: (Array.isArray(raw.assets) ? raw.assets : []).filter((asset: any) => asset && isUuid(asset.id)
      && publicMedia[asset.kind] && typeof asset.mediaType === 'string' && mediaEssence(asset.mediaType) === publicMedia[asset.kind])
      .map((asset: any) => ({ id: asset.id, kind: asset.kind, mediaType: mediaEssence(asset.mediaType), length: asset.length, sha256: text(asset.sha256, 64) }))
  };
}
export function projectGallery(raw: any): GalleryPage {
  if (raw?.schemaVersion !== 1 || (raw.evening && !isUuid(raw.evening.id)) || !Array.isArray(raw.items)) throw new GuestError(503);
  return { schemaVersion: 1, evening: raw.evening ? { id: raw.evening.id, label: text(raw.evening.label, 240) } : null,
    latest: projectGalleryItem(raw.latest), items: raw.items.map(projectGalleryItem).filter((item: GalleryItem | null): item is GalleryItem => !!item),
    nextCursor: typeof raw.nextCursor === 'string' && raw.nextCursor.length <= 2048 ? raw.nextCursor : undefined };
}
export function publicAssetPath(eveningId: string, item: GalleryItem, asset: ArchiveAsset) {
  if (![eveningId, item.locator, item.editionId, asset.id].every(isUuid) || !publicMedia[asset.kind]) throw new GuestError(400);
  return `/guest/v1/evenings/${eveningId}/keepsakes/${item.locator}/editions/${item.editionId}/assets/${asset.id}`;
}
export function guestPath(locator: string, suffix = '', shareEdition?: string) {
  if (!isUuid(locator) || (shareEdition !== undefined && !isUuid(shareEdition))) throw new GuestError(400);
  return `/guest/v1/keepsakes/${locator}${suffix}` + (shareEdition === undefined ? '' : `?view=share&editionId=${shareEdition}`);
}
export function parseClaim(fragment: string | null) {
  const match = fragment?.match(/^#?([crs])=([A-Za-z0-9_-]{32,256})$/);
  return match ? { kind: match[1] as 'c' | 'r' | 's', token: match[2] } : null;
}
export function safeIssuedLink(value: string, kind: 'share' | 'recovery', origin: string) {
  const url = new URL(value, origin);
  const claim = parseClaim(url.hash);
  if (url.origin !== origin || url.pathname !== '/keepsake/' || url.search || !claim || claim.kind !== (kind === 'share' ? 's' : 'r')) throw new GuestError(503);
  return url.href;
}

/** One route lifetime. Abort is advisory; every continuation also checks its generation. */
export class ArchiveClient {
  private generation = 0;
  private controller = new AbortController();
  private csrf?: string;
  private keys = new Map<string, string>();
  private uploadKeys = new WeakMap<Blob, string>();
  // Window.fetch requires its global receiver in real browsers. Calling a stored
  // native function as this.transport(...) binds ArchiveClient instead.
  constructor(private transport: typeof fetch = (input, init) => fetch(input, init), private sessionLock?: (action: () => Promise<void>) => Promise<void>) {}
  invalidate() { this.generation++; this.controller.abort(); this.controller = new AbortController(); this.csrf = undefined; this.keys.clear(); this.uploadKeys = new WeakMap(); }
  capture() {
    const generation = this.generation;
    return () => { if (generation !== this.generation || this.controller.signal.aborted) throw new DOMException('Route changed', 'AbortError'); };
  }
  async response(path: string, publicRead = false, options: { method?: string; data?: object; bytes?: Blob; consentVersion?: number; accept?: () => void } = {}) {
    const check = this.capture(); check();
    if (!path.startsWith('/guest/v1/') || path.includes('://') || path.includes('#')) throw new GuestError(400);
    const method = options.method ?? 'GET';
    if (publicRead && method !== 'GET') throw new GuestError(400);
    if (method !== 'GET') options.accept?.();
    const headers: Record<string, string> = { Accept: 'application/json' };
    const body = method === 'GET' ? undefined : options.bytes ?? JSON.stringify({ schemaVersion: 1, ...options.data });
    let identity = '';
    if (method !== 'GET') {
      if (options.bytes && !this.uploadKeys.has(options.bytes)) this.uploadKeys.set(options.bytes, secureUuid());
      identity = `${method}:${path}:${options.bytes ? this.uploadKeys.get(options.bytes) + ':' + options.consentVersion : body}`;
      if (!this.keys.has(identity)) this.keys.set(identity, secureUuid());
      headers['Idempotency-Key'] = this.keys.get(identity)!;
      headers['Content-Type'] = options.bytes?.type || 'application/json';
      if (this.csrf) headers['X-DV10-CSRF'] = this.csrf;
      if (options.consentVersion !== undefined) headers['X-DV10-Consent'] = String(options.consentVersion);
    }
    const response = await this.transport(path, { method, headers, body, signal: this.controller.signal,
      credentials: publicRead ? 'omit' : 'same-origin', cache: 'no-store', redirect: 'error' });
    check();
    if (!response.ok) {
      if (response.status < 500) this.keys.delete(identity);
      throw new GuestError(response.status);
    }
    return { response, check, complete: () => this.keys.delete(identity) };
  }
  async json<T>(path: string, options: { method?: string; data?: object; bytes?: Blob; consentVersion?: number; accept?: () => void } = {}, publicRead = false): Promise<T> {
    const { response, check, complete } = await this.response(path, publicRead, options);
    const value = response.status === 204 ? {} : await response.json(); check();
    // A caller may reject a late parsed response; keep its replay key until that acceptance check passes.
    if (options.method !== undefined && options.method !== 'GET') options.accept?.();
    complete(); return value as T;
  }
  async blob(path: string, allowedMedia: readonly string[], publicRead = false) {
    const { response, check } = await this.response(path, publicRead);
    const blob = await response.blob(); check();
    if (!allowedMedia.includes(mediaEssence(blob.type))) throw new GuestError(415);
    return blob;
  }
  async bootstrap() {
    const check = this.capture();
    const lock = this.sessionLock ?? (navigator.locks ? (action: () => Promise<void>) => navigator.locks.request('dv10-browser-session', action) : undefined);
    if (!lock) throw new GuestError(503, 'browser_session_lock_unavailable');
    await lock(async () => {
      check(); const result = await this.json<{ schemaVersion: number; csrfToken: string }>('/guest/v1/bootstrap', { method: 'POST' }); check();
      if (result.schemaVersion !== 1 || !result.csrfToken) throw new GuestError(503);
      this.csrf = result.csrfToken;
    }); check();
  }
  async mutate<T>(path: string, options: { method?: string; data?: object; bytes?: Blob; consentVersion?: number; accept?: () => void } = {}) {
    const check = this.capture();
    if (!this.csrf) await this.bootstrap(); check();
    return this.json<T>(path, { ...options, method: options.method ?? 'POST' });
  }
}

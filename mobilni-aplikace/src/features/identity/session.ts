import { ref, readonly, watch } from 'vue';
import { Capacitor, registerPlugin } from '@capacitor/core';
import { isTwinAccountPath, isShowAccountPath, isChronicleAdminPath } from './accountPaths';
import { throwIfAborted } from '../../application/cancellation.mjs';
import { secureUuid } from '../../application/identifiers.mjs';

export interface Account { id: string; displayName: string; roles: string[] }
export interface ResourceGrant { id: string; userId: string; resourceType: string; resourceId: string; permission: string }
export interface AccountSession { id: string; expiresAt: number; installationName: string; trusted: boolean; current: boolean }
export interface AccountDeviceAssociation { id: string; deviceId: string; alias: string; installationId: string; createdAt: number }
export interface AccountDetails { devices?: {deviceId: string; canOperate: boolean}[]; sessions: AccountSession[]; grants: ResourceGrant[]; associations: AccountDeviceAssociation[]; passkeyCount: number; passkeys: {id: string; name: string | null; createdAt: string; isBackedUp: boolean}[] }
interface IdentityBridge {
  request(options: { requestId: string; path: string; method: string; body?: string; confirmed?: boolean }): Promise<{ status: number; body: string }>;
  passkey(options: { mode: 'signin' | 'enroll' | 'customer-enroll' | 'add' | 'reauth'; token?: string; displayName?: string }): Promise<void>;
  accountTransport(options: { requestId: string; path: string; method: string; headers: Record<string, string>; bodyBase64?: string }): Promise<{ status: number; headers: Record<string, string>; bodyBase64: string }>;
  cancelAccountRequest(options: { requestId: string }): Promise<{ cancelled: boolean }>;
}
const nativeIdentity = registerPlugin<IdentityBridge>('KufrIdentity');
const native = Capacitor.isNativePlatform();
const archiveUuid = '[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}';
const ownedPortraitUpload = new RegExp(`^/app/v1/archive/owned/${archiveUuid}/portrait-uploads$`, 'i');
const archiveAsset = new RegExp(`^/app/v1/archive/(?:owned|associations)/${archiveUuid}/assets/${archiveUuid}$`, 'i');

function matchesArchivePath(pattern: RegExp, path: string): boolean {
  // JavaScript's $ anchor also matches before a final line terminator.
  return !/\s/.test(path) && pattern.test(path);
}

export function accountTransportBodyLimit(path: string, method: string, contentType: string | null): number {
  if (!matchesArchivePath(ownedPortraitUpload, path) || method !== 'POST') return 262144;
  const mediaType = contentType?.split(';', 1)[0].trim().toLowerCase();
  return ['image/jpeg', 'image/png', 'image/webp'].includes(mediaType ?? '') ? 8 * 1024 * 1024 : 262144;
}

export function accountTransportResponseLimit(path: string, method: string): number {
  return method === 'GET' && matchesArchivePath(archiveAsset, path) ? 32 * 1024 * 1024 : 1024 * 1024;
}

export function accountTransportResponseAllowed(path: string, method: string, size: number): boolean {
  return Number.isSafeInteger(size) && size >= 0 && size <= accountTransportResponseLimit(path, method);
}

function validPortraitConsent(path: string, method: string, value: string | null): boolean {
  if (value === null) return true;
  return matchesArchivePath(ownedPortraitUpload, path) && method === 'POST' && /^[0-9]{1,10}$/.test(value)
    && Number(value) > 0 && Number(value) <= 2147483647;
}
function encodeAccountBody(bytes: Uint8Array): string {
  const chunks: string[] = [];
  for (let offset = 0; offset < bytes.length; offset += 32768) {
    chunks.push(String.fromCharCode(...bytes.subarray(offset, offset + 32768)));
  }
  return btoa(chunks.join(''));
}
export const account = ref<Account | null>(null);
const deletionRequested = ref(false);
export const accountDeletionRequested = readonly(deletionRequested);
export const offlineSession = ref(false);
export const offlineSessionExpiresAt = ref(0);
let offlineExpiryTimer: ReturnType<typeof setTimeout> | undefined;
export const initialized = ref(false);
export const loading = ref(false);
export const signingOut = ref(false);
export const accountsEnabled = ref(false);
export const accountOrigin = ref('');
let csrfToken = '';
let authGeneration = 0;
let refreshPending: { generation: number; promise: Promise<void> } | null = null;
let logoutPending: Promise<void> | null = null;
let authenticationPending: Promise<void> | null = null;
class IdentityRequestError extends Error {
  constructor(message: string, readonly status: number, readonly code?: string, readonly rejectedGeneration?: number) { super(message); }
}
class AccountContextChangedError extends DOMException {
  constructor() { super('Požadavek patřil předchozímu ověření účtu.', 'AbortError'); }
}
function rethrowNativeAccountError(error: unknown): never {
  if (error && typeof error === 'object' && 'code' in error && error.code === 'ACCOUNT_CONTEXT_CHANGED') {
    throw new AccountContextChangedError();
  }
  if (error && typeof error === 'object' && 'code' in error && error.code === 'ACCOUNT_REQUEST_CANCELLED') {
    throw new DOMException('Požadavek byl zrušen.', 'AbortError');
  }
  throw error;
}
async function nativeAccountCall<T>(signal: AbortSignal | null | undefined, invoke: (requestId: string) => Promise<T>): Promise<T> {
  throwIfAborted(signal);
  const requestId = secureUuid();
  const cancel = () => {
    try {
      void nativeIdentity.cancelAccountRequest({ requestId }).catch(() => {});
    } catch {
      // The response fence still rejects aborted results if cancellation fails.
    }
  };
  signal?.addEventListener('abort', cancel, { once: true });
  try {
    throwIfAborted(signal);
    return await invoke(requestId);
  } catch (error) { return rethrowNativeAccountError(error); }
  finally { signal?.removeEventListener('abort', cancel); }
}

function advanceAuthentication(clearAccount = false): number {
  ++authGeneration;
  refreshPending = null;
  loading.value = false;
  if (clearAccount) {
    clearTimeout(offlineExpiryTimer); offlineExpiryTimer = undefined;
    offlineSession.value = false; offlineSessionExpiresAt.value = 0;
    account.value = null; csrfToken = ''; initialized.value = false;
  }
  return authGeneration;
}
function requireCurrentAuthentication(generation: number) {
  if (generation !== authGeneration) throw new DOMException('Přihlášení se mezitím změnilo.', 'AbortError');
}

export async function accountTransport(path: string, init: RequestInit = {}): Promise<Response> {
  const generation = authGeneration;
  const eveningPath = path === '/v1/evenings' && ['GET', 'POST'].includes(init.method ?? 'GET')
    || /^\/v1\/evenings\/[0-9a-f-]{36}\/active$/i.test(path) && init.method === 'PUT';
  if (!path.startsWith('/app/v1/') && !isTwinAccountPath(path) && !isShowAccountPath(path, init.method ?? 'GET') && !isChronicleAdminPath(path, init.method ?? 'GET') && !eveningPath) throw new Error('Neplatná cesta účtu.');
  throwIfAborted(init.signal);
  const method = init.method ?? 'GET';
  const headers = new Headers(init.headers);
  headers.set('Accept', 'application/json');
  if (method !== 'GET') headers.set('X-DV-CSRF', csrfToken);
  if (!native) {
    const response = await fetch(path, { ...init, method, headers, credentials: 'same-origin', cache: 'no-store' });
    requireCurrentAuthentication(generation);
    return response;
  }
  if (!validPortraitConsent(path, method, headers.get('X-DV10-Consent'))) throw new Error('Neplatná cesta účtu.');
  const bodyLimit = accountTransportBodyLimit(path, method, headers.get('Content-Type'));
  let bytes: Uint8Array | undefined;
  if (typeof init.body === 'string') bytes = new TextEncoder().encode(init.body);
  else if (init.body instanceof Blob) bytes = new Uint8Array(await init.body.arrayBuffer());
  else if (init.body instanceof ArrayBuffer) bytes = new Uint8Array(init.body);
  else if (ArrayBuffer.isView(init.body)) bytes = new Uint8Array(init.body.buffer, init.body.byteOffset, init.body.byteLength);
  else if (init.body != null) throw new Error('Nepodporovaný formát požadavku účtu.');
  if (bytes && bytes.length > bodyLimit) throw new Error('Požadavek překročil povolenou velikost.');
  const encoded = bytes ? encodeAccountBody(bytes) : undefined;
  requireCurrentAuthentication(generation);
  throwIfAborted(init.signal);
  const response = await nativeAccountCall(init.signal, requestId => nativeIdentity.accountTransport({ requestId, path, method, headers: Object.fromEntries(headers), bodyBase64: encoded }));
  requireCurrentAuthentication(generation);
  throwIfAborted(init.signal);
  const responseLimit = accountTransportResponseLimit(path, method);
  if (response.bodyBase64.length > Math.ceil(responseLimit / 3) * 4) throw new Error('Odpověď serveru je příliš velká.');
  const body = Uint8Array.from(atob(response.bodyBase64), char => char.charCodeAt(0));
  if (!accountTransportResponseAllowed(path, method, body.length)) throw new Error('Odpověď serveru je příliš velká.');
  return new Response([204, 205, 304].includes(response.status) ? null : body, { status: response.status, headers: response.headers });
}

export async function accountRequest<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
  const generation = authGeneration;
  const deletingAccountId = path === '/app/v1/account' && method === 'DELETE' ? account.value?.id : undefined;
  throwIfAborted(signal);
  if (!path.startsWith('/app/v1/') && !isTwinAccountPath(path) && !isShowAccountPath(path, method) && !isChronicleAdminPath(path, method)) throw new Error('Neplatná cesta účtu.');
  const serialized = body === undefined ? undefined : JSON.stringify(body);
  const sensitive = method !== 'GET' && path.startsWith('/app/v1/admin/')
    || method === 'DELETE' && path.startsWith('/app/v1/passkeys/')
    || method === 'DELETE' && path === '/app/v1/account';
  const confirmation = path === '/app/v1/account' && method === 'DELETE'
    ? 'Trvale smazat můj účet? Ztratíte přístup na všech zařízeních. Vaše soukromé fotografie, portréty, příběhy a postava budou nevratně smazány a vaše sdílené odkazy zneplatněny. Data ostatních hostů zůstanou zachována. Smazání nelze vrátit zpět.'
    : 'Potvrdit tuto změnu správy? Změna může ovlivnit přístup uživatelů nebo provoz serveru.';
  const confirmed = sensitive && window.confirm(confirmation);
  if (sensitive && !confirmed) throw new IdentityRequestError('Změna nebyla potvrzena.', 0, 'confirmation_cancelled');
  requireCurrentAuthentication(generation);
  if (path === '/app/v1/account' && method === 'DELETE' && (!deletingAccountId || deletingAccountId !== account.value?.id)) throw new AccountContextChangedError();
  let status: number;
  let text: string;
  if (native) {
    const response = await nativeAccountCall(signal, requestId => nativeIdentity.request({ requestId, path, method, body: serialized, confirmed }));
    status = response.status;
    text = response.body;
  } else {
    const response = await fetch(path, {
      method, credentials: 'same-origin', cache: 'no-store', signal,
      headers: { Accept: 'application/json', ...(serialized ? { 'Content-Type': 'application/json' } : {}),
        ...(method !== 'GET' ? { 'X-DV-CSRF': csrfToken } : {}), ...(confirmed ? { 'X-DV-Confirm': 'yes' } : {}) }, body: serialized,
    });
    status = response.status;
    text = await response.text();
  }
  // Cancellation cannot undo a request the server already received. Ignore late
  // results, including a 401 that belongs to a cancelled observer context.
  throwIfAborted(signal);
  let value: unknown;
  try { value = text ? JSON.parse(text) : undefined; } catch { throw new Error('Server nevrátil odpověď účtu.'); }
  if (status < 200 || status >= 300) {
    const rejectedGeneration = status === 401 && generation === authGeneration ? advanceAuthentication(true) : undefined;
    const code = (value as { code?: string } | undefined)?.code;
    const explanations: Record<string, string> = {
      accounts_not_configured: 'Správce musí nejprve nakonfigurovat přihlašování serveru.',
      invitation_expired_or_used: 'Pozvánka už není platná. Vyžádejte si novou od vlastníka.',
      ceremony_expired_or_used: 'Ověření vypršelo. Spusťte ho znovu.',
      last_owner_required: 'Alespoň jeden aktivní vlastník musí zůstat zachován.',
      last_passkey_required: 'Nejprve přidejte druhý přístupový klíč. Poslední klíč nelze odebrat.',
      invalid_passkey_name: 'Zadejte název klíče o délce 1 až 80 znaků.',
      csrf_required: 'Platnost stránky vypršela. Obnovte ji a zkuste akci znovu.',
      account_origin_required: 'Tato adresa není povolená pro přihlášení k serveru.',
      passkey_not_verified: 'Přístupový klíč se nepodařilo ověřit.',
      recent_passkey_required: 'Nejprve znovu ověřte přístupovým klíčem svou totožnost. Změny správy vyžadují ověření z posledních 10 minut.',
      same_account_passkey_required: 'Vyberte přístupový klíč právě přihlášeného účtu. Účet nebyl změněn.',
      confirmation_required: 'Změnu je nutné výslovně potvrdit.',
      model_releases_disabled: 'Výběr modelů není na tomto serveru nakonfigurován.',
      model_store_unavailable: 'Úložiště modelů není dostupné.',
      verified_model_required: 'Aktivovat lze pouze ověřenou neměnnou verzi modelu.',
      active_model_changed: 'Aktivní model se mezitím změnil. Obnovte seznam.',
      model_activation_busy: 'Jiný správce právě mění model. Zkuste to znovu.',
      model_release_invalid: 'Balíček modelu neprošel kontrolou integrity.',
      character_revision_mismatch: 'Profil postavy mezitím změnil jiný požadavek. Načtěte aktuální profil a zkuste úpravu znovu.',
      device_busy: 'Kufr právě obsluhuje jiný rozhovor. Zkuste to znovu za chvíli.',
      device_challenge_unavailable: 'Kufr neposkytl platnou výzvu pro bezpečné připojení. Odpojte ho a zkuste znovu.',
      customer_mode_disabled: 'Zákaznický režim tohoto kufru není povolen obsluhou.',
      start_ticket_expired: 'Připojení vypršelo dřív, než kufr rozhovor převzal. Připojte se a zkuste znovu.',
      customer_session_requires_reconciliation: 'Výsledek startu se ověřuje. Obnovte stav relace a nevytvářejte nový rozhovor.',
      customer_profile_revoked: 'Tento profil už není pro nový rozhovor platný. Obnovte profil postavy.',
      device_epoch_mismatch: 'Kufr se od připojení restartoval. Odpojte ho a připojte znovu.',
    };
    throw new IdentityRequestError(explanations[code ?? ''] ?? (status === 403 ? 'K této akci nemáte oprávnění.' : 'Akci účtu se nepodařilo dokončit.'), status, code, rejectedGeneration);
  }
  if (path === '/app/v1/account' && method === 'DELETE') {
    if (status !== 202 || (value as { status?: string } | undefined)?.status !== 'deletion_requested') {
      throw new Error('Server nepotvrdil přijetí žádosti o smazání účtu. Ověřte stav účtu před dalším pokusem.');
    }
  }
  if (path !== '/app/v1/session' || method !== 'DELETE') requireCurrentAuthentication(generation);
  return value as T;
}

export async function deleteAccount(expectedAccountId: string): Promise<void> {
  if (account.value?.id !== expectedAccountId || authenticationPending || logoutPending) throw new AccountContextChangedError();
  const generation = authGeneration;
  let contextChanged = false;
  const stop = watch(() => account.value?.id, () => { contextChanged = true; }, { flush: 'sync' });
  try {
    await accountRequest<{ status: 'deletion_requested' }>('/app/v1/account', 'DELETE');
    requireCurrentAuthentication(generation);
    if (contextChanged || account.value?.id !== expectedAccountId) throw new AccountContextChangedError();
    // Only a durable server acknowledgement revokes local account authority.
    deletionRequested.value = true;
    advanceAuthentication(true);
    initialized.value = true;
  } finally { stop(); }
}

export async function getAccountDeletionStatus(signal?: AbortSignal): Promise<'deletion_requested' | 'deleted' | null> {
  try {
    const result = await accountRequest<{ status: string }>('/app/v1/account/deletion', 'GET', undefined, signal);
    if (result.status !== 'deletion_requested' && result.status !== 'deleted') throw new Error('Server nevrátil platný stav smazání.');
    return result.status;
  } catch (error) {
    if (error instanceof IdentityRequestError && error.status === 404) return null;
    throw error;
  }
}

export function refreshIdentity(): Promise<void> {
  if (logoutPending) return logoutPending.then(() => refreshIdentity());
  if (refreshPending?.generation === authGeneration) return refreshPending.promise;
  const generation = authGeneration;
  loading.value = true;
  const pending = { generation, promise: Promise.resolve() };
  pending.promise = (async () => {
    const session = await accountRequest<{ enabled: boolean; account: Account | null; csrfToken: string | null; origin?: string; offline?: boolean; expiresAt?: number }>('/app/v1/session');
    if (generation !== authGeneration) return;
    const offline = native && session.offline === true;
    const expiry = Number(session.expiresAt) * 1000;
    if (offline && (!session.account || !Number.isFinite(expiry) || expiry <= Date.now())) throw new Error('Offline přihlášení vypršelo. Připojte se a ověřte účet.');
    clearTimeout(offlineExpiryTimer); offlineExpiryTimer = undefined;
    offlineSession.value = offline;
    offlineSessionExpiresAt.value = offline ? expiry : 0;
    account.value = session.account;
    if (session.account) deletionRequested.value = false;
    accountsEnabled.value = session.enabled;
    csrfToken = offline ? '' : session.csrfToken ?? '';
    accountOrigin.value = session.origin ?? '';
    initialized.value = true;
    if (offline) offlineExpiryTimer = setTimeout(() => {
      if (generation === authGeneration && offlineSession.value) advanceAuthentication(true);
    }, Math.min(expiry - Date.now(), 2147483647));
  })().catch(error => {
    // A native queue may retire this refresh before the ceremony promise reaches
    // JavaScript. Its cancelled scope does not establish loss of authentication.
    if (error instanceof AccountContextChangedError && authenticationPending) return;
    if (generation !== authGeneration && !(error instanceof IdentityRequestError && error.rejectedGeneration === authGeneration)) return;
    clearTimeout(offlineExpiryTimer); offlineExpiryTimer = undefined;
    offlineSession.value = false; offlineSessionExpiresAt.value = 0;
    account.value = null; csrfToken = ''; initialized.value = false; accountsEnabled.value = false;
    throw error;
  }).finally(() => { if (refreshPending === pending) { loading.value = false; refreshPending = null; } });
  refreshPending = pending;
  return pending.promise;
}

export function signOut(): Promise<void> {
  if (logoutPending) return logoutPending;
  const authentication = authenticationPending;
  // Without an active ceremony, dispatch with the current CSRF token before clearing it.
  const request = authentication ? null : accountRequest('/app/v1/session', 'DELETE');
  const generation = advanceAuthentication(true);
  signingOut.value = true;
  const operation = (async () => {
    if (authentication) {
      // An earlier completed assertion may still write a cookie. Revoke only after
      // it settles, using CSRF bound to the cookie that actually won that request.
      await authentication.catch(() => {});
      const session = await accountRequest<{csrfToken: string | null}>('/app/v1/session');
      csrfToken = session.csrfToken ?? '';
      await accountRequest('/app/v1/session', 'DELETE');
    } else await request;
  })();
  const pending = operation.finally(() => {
    if (logoutPending === pending) { logoutPending = null; signingOut.value = false; }
  });
  logoutPending = pending;
  return pending.then(async () => { if (generation === authGeneration) await refreshIdentity(); });
}
export function hasRole(role: string): boolean {
  return !!account.value && (account.value.roles.includes(role) || account.value.roles.includes('Owner'));
}
export function useIdentity() {
  return { account: readonly(account), initialized: readonly(initialized), loading: readonly(loading), signingOut: readonly(signingOut), accountsEnabled: readonly(accountsEnabled), refreshIdentity, signOut, hasRole };
}

function decode(value: string): ArrayBuffer {
  const bytes = Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
  return bytes.buffer;
}
function encode(value: ArrayBuffer): string {
  return btoa(String.fromCharCode(...new Uint8Array(value))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}
function credentialJson(credential: PublicKeyCredential): object {
  const response = credential.response;
  const common = { id: credential.id, rawId: encode(credential.rawId), type: credential.type, authenticatorAttachment: credential.authenticatorAttachment,
    clientExtensionResults: credential.getClientExtensionResults() };
  if (response instanceof AuthenticatorAttestationResponse) {
    return { ...common, response: { clientDataJSON: encode(response.clientDataJSON), attestationObject: encode(response.attestationObject), transports: response.getTransports() } };
  }
  const assertion = response as AuthenticatorAssertionResponse;
  return { ...common, response: { clientDataJSON: encode(assertion.clientDataJSON), authenticatorData: encode(assertion.authenticatorData),
    signature: encode(assertion.signature), userHandle: assertion.userHandle ? encode(assertion.userHandle) : null } };
}

export function authenticatePasskey(mode: 'signin' | 'enroll' | 'customer-enroll' | 'add' | 'reauth', token = '', displayName = ''): Promise<void> {
  if (authenticationPending) return Promise.reject(new Error('Ověření přístupovým klíčem už probíhá.'));
  const operation = (async () => {
    // Epochs protect Vue state; waiting also protects browser/native cookie ordering.
    if (logoutPending) await logoutPending.catch(() => {});
    await performPasskeyAuthentication(mode, token, displayName);
  })();
  const pending = operation.finally(() => { if (authenticationPending === pending) authenticationPending = null; });
  authenticationPending = pending;
  return pending;
}

async function performPasskeyAuthentication(mode: 'signin' | 'enroll' | 'customer-enroll' | 'add' | 'reauth', token: string, displayName: string): Promise<void> {
  const generation = advanceAuthentication();
  if (native) {
    await nativeIdentity.passkey({ mode, token, displayName });
    requireCurrentAuthentication(generation);
    advanceAuthentication();
    await refreshIdentity();
    return;
  }
  if (!window.isSecureContext || !window.PublicKeyCredential) throw new Error('Přístupové klíče vyžadují HTTPS a podporovaný prohlížeč.');
  await refreshIdentity();
  requireCurrentAuthentication(generation);
  const optionsPath = mode === 'reauth' ? '/passkeys/reauthentication-options' : mode === 'signin' ? '/passkeys/request-options' : mode === 'add' ? '/passkeys/options' : mode === 'customer-enroll' ? '/customer/enrollment/options' : '/enrollment/options';
  const raw = await accountRequest<Record<string, unknown>>('/app/v1' + optionsPath, 'POST', mode === 'enroll' ? { token, displayName } : mode === 'customer-enroll' ? { displayName } : undefined);
  requireCurrentAuthentication(generation);
  const options = { ...raw, challenge: decode(raw.challenge as string) };
  let credential: PublicKeyCredential | null;
  if (mode === 'signin' || mode === 'reauth') {
    const request = options as unknown as PublicKeyCredentialRequestOptions;
    if (Array.isArray(raw.allowCredentials)) request.allowCredentials = raw.allowCredentials.map((c: { id: string; type: 'public-key' }) => ({ ...c, id: decode(c.id) }));
    credential = await navigator.credentials.get({ publicKey: request }) as PublicKeyCredential | null;
  } else {
    const user = raw.user as { id: string; name: string; displayName: string };
    const creation = { ...options, user: { ...user, id: decode(user.id) } } as unknown as PublicKeyCredentialCreationOptions;
    if (Array.isArray(raw.excludeCredentials)) creation.excludeCredentials = raw.excludeCredentials.map((c: { id: string; type: 'public-key' }) => ({ ...c, id: decode(c.id) }));
    credential = await navigator.credentials.create({ publicKey: creation }) as PublicKeyCredential | null;
  }
  if (!credential) throw new Error('Ověření bylo zrušeno.');
  requireCurrentAuthentication(generation);
  const complete = mode === 'reauth' ? '/passkeys/reauthenticate' : mode === 'signin' ? '/passkeys/sign-in' : mode === 'add' ? '/passkeys/complete' : mode === 'customer-enroll' ? '/customer/enrollment/complete' : '/enrollment/complete';
  await accountRequest('/app/v1' + complete, 'POST', { credential: credentialJson(credential) });
  requireCurrentAuthentication(generation);
  advanceAuthentication();
  await refreshIdentity();
}

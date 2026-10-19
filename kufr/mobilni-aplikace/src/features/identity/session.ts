import { ref, readonly } from 'vue';
import { Capacitor, registerPlugin } from '@capacitor/core';
import { isTwinAccountPath, isShowAccountPath, isChronicleAdminPath } from './accountPaths';
import { throwIfAborted } from '../../application/cancellation.mjs';

export interface Account { id: string; displayName: string; roles: string[] }
export interface ResourceGrant { id: string; userId: string; resourceType: string; resourceId: string; permission: string }
export interface AccountSession { id: string; expiresAt: number; installationName: string; trusted: boolean; current: boolean }
export interface AccountDeviceAssociation { id: string; deviceId: string; alias: string; installationId: string; createdAt: number }
export interface AccountDetails { devices?: {deviceId: string; canOperate: boolean}[]; sessions: AccountSession[]; grants: ResourceGrant[]; associations: AccountDeviceAssociation[]; passkeyCount: number; passkeys: {id: string; name: string | null; createdAt: string; isBackedUp: boolean}[] }
interface IdentityBridge {
  request(options: { path: string; method: string; body?: string; confirmed?: boolean }): Promise<{ status: number; body: string }>;
  passkey(options: { mode: 'signin' | 'enroll' | 'add' | 'reauth'; token?: string; displayName?: string }): Promise<void>;
  accountTransport(options: { path: string; method: string; headers: Record<string, string>; bodyBase64?: string }): Promise<{ status: number; headers: Record<string, string>; bodyBase64: string }>;
}
const nativeIdentity = registerPlugin<IdentityBridge>('KufrIdentity');
const native = Capacitor.isNativePlatform();
export const account = ref<Account | null>(null);
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

function advanceAuthentication(clearAccount = false): number {
  ++authGeneration;
  refreshPending = null;
  loading.value = false;
  if (clearAccount) { account.value = null; csrfToken = ''; initialized.value = false; }
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
  let bytes: Uint8Array | undefined;
  if (typeof init.body === 'string') bytes = new TextEncoder().encode(init.body);
  else if (init.body instanceof Blob) bytes = new Uint8Array(await init.body.arrayBuffer());
  else if (init.body instanceof ArrayBuffer) bytes = new Uint8Array(init.body);
  else if (ArrayBuffer.isView(init.body)) bytes = new Uint8Array(init.body.buffer, init.body.byteOffset, init.body.byteLength);
  else if (init.body != null) throw new Error('Nepodporovaný formát požadavku účtu.');
  if (bytes && bytes.length > 262144) throw new Error('Požadavek překročil povolenou velikost.');
  const encoded = bytes ? btoa(Array.from(bytes, byte => String.fromCharCode(byte)).join('')) : undefined;
  const response = await nativeIdentity.accountTransport({ path, method, headers: Object.fromEntries(headers), bodyBase64: encoded });
  requireCurrentAuthentication(generation);
  throwIfAborted(init.signal);
  const archiveAsset = /^\/app\/v1\/archive\/associations\/[0-9a-f-]{36}\/assets\/[0-9a-f-]{36}$/i.test(path);
  if (response.bodyBase64.length > (archiveAsset ? 44739244 : 1398104)) throw new Error('Odpověď serveru je příliš velká.');
  const body = Uint8Array.from(atob(response.bodyBase64), char => char.charCodeAt(0));
  return new Response([204, 205, 304].includes(response.status) ? null : body, { status: response.status, headers: response.headers });
}

export async function accountRequest<T>(path: string, method = 'GET', body?: unknown, signal?: AbortSignal): Promise<T> {
  const generation = authGeneration;
  throwIfAborted(signal);
  if (!path.startsWith('/app/v1/') && !isTwinAccountPath(path) && !isShowAccountPath(path, method) && !isChronicleAdminPath(path, method)) throw new Error('Neplatná cesta účtu.');
  const serialized = body === undefined ? undefined : JSON.stringify(body);
  const sensitive = method !== 'GET' && path.startsWith('/app/v1/admin/')
    || method === 'DELETE' && path.startsWith('/app/v1/passkeys/');
  const confirmed = sensitive && window.confirm('Potvrdit tuto změnu správy? Změna může ovlivnit přístup uživatelů nebo provoz serveru.');
  if (sensitive && !confirmed) throw new Error('Změna nebyla potvrzena.');
  let status: number;
  let text: string;
  if (native) {
    const response = await nativeIdentity.request({ path, method, body: serialized, confirmed });
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
  // Native requests cannot cancel an in-flight bridge call. Ignore its result
  // after cancellation, including a late 401 that would otherwise clear identity.
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
    };
    throw new IdentityRequestError(explanations[code ?? ''] ?? (status === 403 ? 'K této akci nemáte oprávnění.' : 'Akci účtu se nepodařilo dokončit.'), status, code, rejectedGeneration);
  }
  if (path !== '/app/v1/session' || method !== 'DELETE') requireCurrentAuthentication(generation);
  return value as T;
}

export function refreshIdentity(): Promise<void> {
  if (logoutPending) return logoutPending.then(() => refreshIdentity());
  if (refreshPending?.generation === authGeneration) return refreshPending.promise;
  const generation = authGeneration;
  loading.value = true;
  const pending = { generation, promise: Promise.resolve() };
  pending.promise = (async () => {
    const session = await accountRequest<{ enabled: boolean; account: Account | null; csrfToken: string | null; origin?: string }>('/app/v1/session');
    if (generation !== authGeneration) return;
    account.value = session.account;
    accountsEnabled.value = session.enabled;
    csrfToken = session.csrfToken ?? '';
    accountOrigin.value = session.origin ?? '';
    initialized.value = true;
  })().catch(error => {
    if (generation !== authGeneration && !(error instanceof IdentityRequestError && error.rejectedGeneration === authGeneration)) return;
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

export function authenticatePasskey(mode: 'signin' | 'enroll' | 'add' | 'reauth', token = '', displayName = ''): Promise<void> {
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

async function performPasskeyAuthentication(mode: 'signin' | 'enroll' | 'add' | 'reauth', token: string, displayName: string): Promise<void> {
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
  const optionsPath = mode === 'reauth' ? '/passkeys/reauthentication-options' : mode === 'signin' ? '/passkeys/request-options' : mode === 'add' ? '/passkeys/options' : '/enrollment/options';
  const raw = await accountRequest<Record<string, unknown>>('/app/v1' + optionsPath, 'POST', mode === 'enroll' ? { token, displayName } : undefined);
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
  const complete = mode === 'reauth' ? '/passkeys/reauthenticate' : mode === 'signin' ? '/passkeys/sign-in' : mode === 'add' ? '/passkeys/complete' : '/enrollment/complete';
  await accountRequest('/app/v1' + complete, 'POST', { credential: credentialJson(credential) });
  requireCurrentAuthentication(generation);
  advanceAuthentication();
  await refreshIdentity();
}

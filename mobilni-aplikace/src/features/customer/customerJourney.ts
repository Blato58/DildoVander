import { isCustomerAccount } from '../../application/access';
import { safeReturnPath } from '../../application/navigation';
import type { Account } from '../identity/session';

const terminalSessionStates = new Set([
  'completed',
  'failed',
]);

const safeLocatorPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export type CustomerJourneyKey =
  | 'sign-in'
  | 'staff-home'
  | 'refresh-session'
  | 'resume-session'
  | 'create-character'
  | 'review-character'
  | 'review-preparation'
  | 'open-latest-result'
  | 'check-result'
  | 'continue-conversation';

export type CustomerJourneyMeaning =
  | 'authentication-required'
  | 'staff-destination'
  | 'verification-required'
  | 'active-session'
  | 'character-missing'
  | 'profile-review'
  | 'preparation-pending'
  | 'latest-result'
  | 'result-preparing'
  | 'conversation-ready';

export interface CustomerJourneyAction {
  key: CustomerJourneyKey;
  label: string;
  route: string;
  meaning: CustomerJourneyMeaning;
  /** UI hint only; never authority for device readiness, consent, proximity, or starting a session. */
  canStartConversation: boolean;
}

export interface CustomerJourneyInput {
  account: Account | null;
  /** A status returned by the account-owned session endpoint. Unknown nonempty values are active. */
  sessionState?: string | null;
  profile?: { status: string } | null;
  loading?: boolean;
  error?: boolean;
  stale?: boolean;
  /** The locator from the server's account-associations response; never derive this from a session. */
  latestAuthorizedResult?: { locator: string } | null;
  resultPending?: boolean;
}

/** Customer sign-in has a character-first default; staff keep the established settings default. */
export function defaultSignedInDestination(account: Account | null, explicitReturn?: unknown): string {
  if (!account) return '/signin';

  if (typeof explicitReturn === 'string' && explicitReturn.length > 0) {
    const safePath = safeReturnPath(explicitReturn);
    if (safePath !== '/' || explicitReturn === '/') return safePath;
    if (isAccountKeepsakeReturn(explicitReturn)) return explicitReturn;
  }
  return isCustomerAccount(account) ? '/me/character' : '/settings';
}

/** Selects a safe next action from already-loaded account data; this function performs no I/O. */
export function getCustomerJourneyAction(input: CustomerJourneyInput): CustomerJourneyAction {
  if (!input.account) {
    return action('sign-in', 'Přihlásit se', '/signin', 'authentication-required');
  }

  if (!isCustomerAccount(input.account)) {
    return action('staff-home', 'Otevřít nastavení účtu', defaultSignedInDestination(input.account), 'staff-destination');
  }

  if (hasActiveSession(input.sessionState)) {
    return action('resume-session', 'Pokračovat v rozhovoru', '/me/conversation', 'active-session');
  }

  if (input.loading || input.error || input.stale) {
    return action('refresh-session', 'Obnovit stav rozhovoru', '/me/conversation', 'verification-required');
  }

  if (!input.profile) {
    return action('create-character', 'Vytvořit postavu', '/me/character', 'character-missing');
  }

  if (input.profile.status === 'needs_clarification') {
    return action('review-character', 'Upřesnit postavu', '/me/character', 'profile-review');
  }

  if (input.profile.status === 'queued' || input.profile.status === 'running') {
    return action('review-preparation', 'Zkontrolovat přípravu postavy', '/me/character', 'preparation-pending');
  }

  if (input.profile.status !== 'ready' && input.profile.status !== 'saved') {
    return action('review-character', 'Zkontrolovat postavu', '/me/character', 'profile-review');
  }

  if (input.resultPending) {
    return action('check-result', 'Zkontrolovat památku', '/me/conversation', 'result-preparing');
  }

  const locator = input.latestAuthorizedResult?.locator;
  if (typeof locator === 'string' && safeLocatorPattern.test(locator)) {
    return action(
      'open-latest-result',
      'Otevřít poslední památku',
      `/keepsake/?account=1&k=${encodeURIComponent(locator)}`,
      'latest-result',
      true,
    );
  }

  // `saved` means usable without AI preparation or additional consent.
  return action('continue-conversation', 'Pokračovat k rozhovoru', '/me/conversation', 'conversation-ready', true);
}

function hasActiveSession(state: string | null | undefined): boolean {
  return typeof state === 'string' && state.length > 0 && !terminalSessionStates.has(state);
}

/** The existing return-path allowlist omits keepsakes; preserve only the exact account locator route. */
function isAccountKeepsakeReturn(value: string): boolean {
  if (!value.startsWith('/') || value.startsWith('//') || /[\\\u0000-\u0020]/.test(value)) return false;

  try {
    const destination = new URL(value, 'https://dildovander.invalid');
    const locators = destination.searchParams.getAll('k');
    const accountFlags = destination.searchParams.getAll('account');
    return destination.origin === 'https://dildovander.invalid'
      && destination.pathname === '/keepsake/'
      && destination.hash === ''
      && locators.length === 1
      && safeLocatorPattern.test(locators[0])
      && accountFlags.length === 1
      && accountFlags[0] === '1'
      && [...destination.searchParams.keys()].length === 2;
  } catch {
    return false;
  }
}

function action(
  key: CustomerJourneyKey,
  label: string,
  route: string,
  meaning: CustomerJourneyMeaning,
  canStartConversation = false,
): CustomerJourneyAction {
  return { key, label, route, meaning, canStartConversation };
}

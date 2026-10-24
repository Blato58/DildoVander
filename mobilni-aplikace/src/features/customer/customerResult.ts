import { projectGeneratedWand, type GeneratedWand } from '../archive/generatedWand';
import type { CustomerCopyKey } from '../../application/customerCopy';

/** Safe projection of the account-owned customer-session result endpoint. */

export type CustomerResultState = 'pending' | 'processing' | 'partial' | 'ready' | 'failed' | 'unavailable';
export type CustomerResultReason =
  | 'awaiting_result'
  | 'awaiting_capacity'
  | 'paused_preparing'
  | 'reconciliation_required'
  | 'generation_failed'
  | 'result_unavailable';

export interface CustomerResultProjection {
  valid: boolean;
  sessionId: string | null;
  state: CustomerResultState;
  reason: CustomerResultReason | null;
  guidance: string;
  /** Catalog key for reactive localization; it does not affect validity or server state. */
  guidanceKey: CustomerCopyKey;
  shouldPoll: boolean;
  hasStory: boolean;
  hasArtwork: boolean;
  /** A display route only; the endpoint still enforces authorization on every request. */
  route: string | null;
  locator: string | null;
  wandPublicationApproved?: boolean;
  generatedWand?: GeneratedWand;
  wandGeneration?: { status: string; canRetry: boolean };
}

const states = new Set<CustomerResultState>([
  'pending', 'processing', 'partial', 'ready', 'failed', 'unavailable',
]);
const reasons = new Set<CustomerResultReason>([
  'awaiting_result', 'awaiting_capacity', 'paused_preparing', 'reconciliation_required',
  'generation_failed', 'result_unavailable',
]);
const pollableStates = new Set<CustomerResultState>(['pending', 'processing', 'partial']);
const safeUuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Validates an untrusted JSON value and projects only safe UI facts. Invalid or future
 * contracts become a non-polling verification result. `route` is emitted only for the
 * server-verified original owner with private-read capability and a UUID locator.
 */
export function projectCustomerResult(value: unknown, expectedSessionId: string): CustomerResultProjection {
  const invalid = (): CustomerResultProjection => ({
    valid: false,
    sessionId: null,
    state: 'unavailable',
    reason: null,
    guidance: 'Stav památky se nepodařilo ověřit. Obnovte stav.',
    guidanceKey: 'result.guidance-verification',
    shouldPoll: false,
    hasStory: false,
    hasArtwork: false,
    route: null,
    locator: null,
  });

  if (!isRecord(value)
    || !isRecord(value.readiness)
    || value.schemaVersion !== 1
    || typeof expectedSessionId !== 'string'
    || value.sessionId !== expectedSessionId
    || typeof value.readiness.state !== 'string'
    || !states.has(value.readiness.state as CustomerResultState)
    || (value.readiness.reason !== null && (typeof value.readiness.reason !== 'string' || !reasons.has(value.readiness.reason as CustomerResultReason)))
    || typeof value.readiness.canRetry !== 'boolean'
    || typeof value.readiness.shouldPoll !== 'boolean'
    || typeof value.readiness.hasStory !== 'boolean'
    || typeof value.readiness.hasArtwork !== 'boolean'
    || typeof value.canReadPrivate !== 'boolean'
    || typeof value.canManage !== 'boolean'
    || typeof value.canUnlink !== 'boolean'
    || (value.accessKind !== null && typeof value.accessKind !== 'string')
    || (value.resultLocator !== null && typeof value.resultLocator !== 'string')) {
    return invalid();
  }

  const readiness = value.readiness;
  const state = readiness.state as CustomerResultState;
  const reason = readiness.reason as CustomerResultReason | null;
  const locator = value.resultLocator as string | null;
  const verifiedOwner = value.accessKind === 'originalOwner' && value.canReadPrivate === true;
  const route = state !== 'unavailable' && reason !== 'result_unavailable'
    && verifiedOwner && locator !== null && safeUuidPattern.test(locator)
    ? `/keepsake/?account=1&k=${encodeURIComponent(locator)}`
    : null;

  return {
    valid: true,
    sessionId: expectedSessionId,
    state,
    reason,
    guidance: guidanceFor(state, reason, readiness.hasStory === true),
    guidanceKey: guidanceKeyFor(state, reason, readiness.hasStory === true),
    shouldPoll: readiness.shouldPoll === true && pollableStates.has(state),
    hasStory: readiness.hasStory === true,
    hasArtwork: readiness.hasArtwork === true,
    route,
    locator: route ? locator : null,
    ...(verifiedOwner && typeof value.wandPublicationApproved === 'boolean' ? { wandPublicationApproved: value.wandPublicationApproved } : {}),
    ...(isRecord(value.wandGeneration) && ['queued', 'running', 'ready', 'selected', 'failed', 'unavailable', 'cancelled', 'stale'].includes(String(value.wandGeneration.status))
      ? { wandGeneration: { status: String(value.wandGeneration.status), canRetry: value.wandGeneration.canRetry === true } } : {}),
    ...(verifiedOwner && state !== 'unavailable' && projectGeneratedWand(value.generatedWand) ? { generatedWand: projectGeneratedWand(value.generatedWand)! } : {}),
  };
}

function guidanceKeyFor(
  state: CustomerResultState,
  reason: CustomerResultReason | null,
  hasStory: boolean,
): CustomerCopyKey {
  if (state === 'unavailable' || reason === 'result_unavailable') return 'result.guidance-unavailable';
  if (reason === 'reconciliation_required') return 'result.guidance-reconciliation';
  if (reason === 'generation_failed') {
    return hasStory ? 'result.guidance-generation-failed-with-story' : 'result.guidance-generation-failed';
  }
  if (reason === 'awaiting_capacity') return 'result.guidance-capacity';
  if (reason === 'paused_preparing') return 'result.guidance-paused';
  if (reason === 'awaiting_result') return 'result.guidance-awaiting';

  switch (state) {
    case 'pending': return 'result.guidance-awaiting';
    case 'processing': return 'result.guidance-processing';
    case 'partial': return hasStory ? 'result.guidance-partial-with-story' : 'result.guidance-partial';
    case 'ready': return 'result.guidance-ready';
    case 'failed': return 'result.guidance-failed';
  }
}

function guidanceFor(
  state: CustomerResultState,
  reason: CustomerResultReason | null,
  hasStory: boolean,
): string {
  if (state === 'unavailable' || reason === 'result_unavailable') {
    return 'Tato památka už není dostupná.';
  }
  if (reason === 'reconciliation_required') {
    return 'Výsledek čeká na ověření. Aplikace ho automaticky znovu nevytváří.';
  }
  if (reason === 'generation_failed') {
    return hasStory
      ? 'Text památky je dostupný, ilustraci se nepodařilo připravit.'
      : 'Památku se nepodařilo připravit.';
  }
  if (reason === 'awaiting_capacity') return 'Výsledek čeká na volnou kapacitu.';
  if (reason === 'paused_preparing') return 'Příprava je pozastavená. Stav můžete později znovu obnovit.';
  if (reason === 'awaiting_result') return 'Výsledek se připravuje.';

  switch (state) {
    case 'pending': return 'Výsledek se připravuje.';
    case 'processing': return 'Výsledek se právě připravuje.';
    case 'partial': return hasStory
      ? 'Text památky je dostupný a další část se připravuje.'
      : 'Část památky je dostupná a další část se připravuje.';
    case 'ready': return 'Památka je připravená.';
    case 'failed': return 'Památku se nepodařilo připravit.';
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

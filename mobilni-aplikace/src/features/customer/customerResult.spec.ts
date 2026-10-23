import { describe, expect, it } from 'vitest';
import { projectCustomerResult } from './customerResult';

const sessionId = '6d8c7912-48c7-499c-a61a-138d0ca7932d';
const locator = 'e513a7ce-1f2a-4320-9ca2-463fb179b0f4';

function response(overrides: Record<string, unknown> = {}) {
  const { readiness: readinessOverrides = {}, ...resultOverrides } = overrides;
  return {
    schemaVersion: 1,
    sessionId,
    resultLocator: locator,
    readiness: {
      state: 'processing',
      reason: null,
      canRetry: false,
      shouldPoll: true,
      hasStory: false,
      hasArtwork: false,
      ...readinessOverrides as Record<string, unknown>,
    },
    accessKind: 'originalOwner',
    canManage: true,
    canReadPrivate: true,
    canUnlink: false,
    ...resultOverrides,
  };
}

describe('customer session result projection', () => {
  it('accepts the matching schema and gates a direct route on verified server owner fields', () => {
    expect(projectCustomerResult(response(), sessionId)).toMatchObject({
      valid: true,
      sessionId,
      state: 'processing',
      shouldPoll: true,
      guidanceKey: 'result.guidance-processing',
      route: `/keepsake/?account=1&k=${locator}`,
    });
  });

  it('projects representative nested server payloads for pending, reconciliation, and ready results', () => {
    const pending = projectCustomerResult({
      schemaVersion: 1,
      sessionId,
      resultLocator: null,
      readiness: {
        state: 'pending', reason: 'awaiting_result', canRetry: false,
        shouldPoll: true, hasStory: false, hasArtwork: false,
      },
      accessKind: null, canManage: false, canReadPrivate: false, canUnlink: false,
    }, sessionId);
    expect(pending).toMatchObject({ valid: true, state: 'pending', shouldPoll: true, route: null });

    const reconciliation = projectCustomerResult({
      schemaVersion: 1,
      sessionId,
      resultLocator: locator,
      readiness: {
        state: 'partial', reason: 'reconciliation_required', canRetry: false,
        shouldPoll: true, hasStory: true, hasArtwork: false,
      },
      accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false,
    }, sessionId);
    expect(reconciliation).toMatchObject({ valid: true, state: 'partial', shouldPoll: true, hasStory: true });

    const ready = projectCustomerResult({
      schemaVersion: 1,
      sessionId,
      resultLocator: locator,
      readiness: {
        state: 'ready', reason: null, canRetry: false,
        shouldPoll: false, hasStory: true, hasArtwork: true,
      },
      accessKind: 'originalOwner', canManage: true, canReadPrivate: true, canUnlink: false,
    }, sessionId);
    expect(ready).toMatchObject({ valid: true, state: 'ready', shouldPoll: false, hasStory: true, hasArtwork: true });
  });

  it('rejects mismatched sessions, malformed responses and unknown future readiness data safely', () => {
    for (const [input, expected] of [
      [response(), 'different-session'],
      [response({ schemaVersion: 2 }), sessionId],
      [response({ readiness: { state: 'provider_internal_state' }, canReadPrivate: true }), sessionId],
      [response({ readiness: { reason: 'provider_timeout_secret=abc' } }), sessionId],
      [response({ readiness: { shouldPoll: 'yes' } }), sessionId],
      [null, sessionId],
    ] as const) {
      expect(projectCustomerResult(input, expected)).toMatchObject({
        valid: false,
        state: 'unavailable',
        guidance: 'Stav památky se nepodařilo ověřit. Obnovte stav.',
        guidanceKey: 'result.guidance-verification',
        shouldPoll: false,
        route: null,
      });
    }

    const malformed = projectCustomerResult(response({
      readiness: { state: 'future_state' },
      error: 'private-provider-diagnostic',
    }), sessionId);
    expect(JSON.stringify(malformed)).not.toContain('private-provider-diagnostic');
  });

  it.each([
    { accessKind: null, canReadPrivate: false },
    { accessKind: 'linkedAccount', canReadPrivate: true },
    { accessKind: 'originalOwner', canReadPrivate: false },
  ])('never offers a route without both original-owner and private-read server facts: %o', flags => {
    expect(projectCustomerResult(response(flags), sessionId).route).toBeNull();
  });

  it('rejects a tampered locator for navigation and never derives a fallback route', () => {
    for (const resultLocator of [
      null,
      `${locator}&redirect=https://evil.example`,
      'not-a-uuid',
    ]) {
      expect(projectCustomerResult(response({ resultLocator }), sessionId).route).toBeNull();
    }
  });

  it('preserves a partial story while stopping for definitive artwork failure', () => {
    const result = projectCustomerResult(response({
      readiness: {
        state: 'partial',
        reason: 'generation_failed',
        shouldPoll: false,
        hasStory: true,
        hasArtwork: false,
      },
    }), sessionId);

    expect(result).toMatchObject({
      valid: true,
      state: 'partial',
      shouldPoll: false,
      hasStory: true,
      hasArtwork: false,
      guidanceKey: 'result.guidance-generation-failed-with-story',
    });
    expect(result.guidance).toContain('Text památky je dostupný');
    expect(result.guidance).not.toMatch(/hotov|dokončen/i);
  });

  it('explains reconciliation without suggesting an automatic retry and only polls when both gates allow it', () => {
    const result = projectCustomerResult(response({
      readiness: {
        state: 'processing',
        reason: 'reconciliation_required',
        canRetry: true,
        shouldPoll: true,
      },
    }), sessionId);

    expect(result.shouldPoll).toBe(true);
    expect(result.guidanceKey).toBe('result.guidance-reconciliation');
    expect(result.guidance).toContain('automaticky znovu nevytváří');
    expect(result).not.toHaveProperty('canRetry');
    expect(projectCustomerResult(response({ readiness: { state: 'ready', shouldPoll: true } }), sessionId).shouldPoll).toBe(false);
    expect(projectCustomerResult(response({ readiness: { state: 'partial', shouldPoll: false } }), sessionId).shouldPoll).toBe(false);
  });

  it('keeps deleted or unbound results unavailable with cleared capability and locator projection', () => {
    const result = projectCustomerResult(response({
      resultLocator: null,
      readiness: {
        state: 'unavailable',
        reason: 'result_unavailable',
        shouldPoll: false,
        hasStory: false,
        hasArtwork: false,
      },
      accessKind: null,
      canManage: false,
      canReadPrivate: false,
      canUnlink: false,
    }), sessionId);

    expect(result).toMatchObject({
      valid: true,
      state: 'unavailable',
      shouldPoll: false,
      route: null,
      hasStory: false,
      hasArtwork: false,
      guidance: 'Tato památka už není dostupná.',
      guidanceKey: 'result.guidance-unavailable',
    });
  });

  it('rejects the obsolete flattened shape instead of interpreting it as authoritative', () => {
    const flattened = {
      ...response(),
      state: 'unavailable',
      reason: 'result_unavailable',
      shouldPoll: false,
      hasStory: false,
      hasArtwork: false,
      readiness: undefined,
    };
    expect(projectCustomerResult(flattened, sessionId)).toMatchObject({
      valid: false,
      guidance: 'Stav památky se nepodařilo ověřit. Obnovte stav.',
      route: null,
    });
  });

  it('suppresses a locator when an unavailable result contradicts retained capability flags', () => {
    expect(projectCustomerResult(response({ readiness: { state: 'unavailable', reason: 'result_unavailable' } }), sessionId).route).toBeNull();
    expect(projectCustomerResult(response({ readiness: { state: 'partial', reason: 'result_unavailable' } }), sessionId).route).toBeNull();
  });
});

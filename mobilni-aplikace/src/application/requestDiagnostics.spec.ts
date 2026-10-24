import { beforeEach, describe, expect, it } from 'vitest';
import { beginRequestDiagnostic, clearRequestDiagnostics, diagnosticRequestId, requestDiagnostics } from './requestDiagnostics';

beforeEach(clearRequestDiagnostics);
describe('safe request diagnostics', () => {
  it('keeps only bounded request metadata, never identifiers, query strings or error text', () => {
    beginRequestDiagnostic('/app/v1/testing-sessions/private-character/characters?token=secret#private', 'POST')
      .complete(503, '01234567-89ab-4cde-8f01-23456789abcd');
    beginRequestDiagnostic('/app/v1/account', 'GET').fail(new Error('password and prompt'));
    const exported = JSON.stringify(requestDiagnostics.value);
    expect(requestDiagnostics.value[0]).toMatchObject({ action: '/app/v1/testing-sessions/:id/characters', status: 503, outcome: 'http_error', requestId: '01234567-89ab-4cde-8f01-23456789abcd' });
    for (const secret of ['private-character', 'secret', 'token', 'password', 'prompt']) expect(exported).not.toContain(secret);
    expect(diagnosticRequestId('access-token')).toBeUndefined();
  });
  it('bounds history and fences late completions after clearing or changing accounts', () => {
    const late = beginRequestDiagnostic('/app/v1/account', 'GET');
    for (let index = 0; index < 205; index++) beginRequestDiagnostic('/app/v1/session', 'GET').complete(200);
    expect(requestDiagnostics.value).toHaveLength(200);
    clearRequestDiagnostics(); late.complete(500);
    expect(requestDiagnostics.value).toHaveLength(0);
    beginRequestDiagnostic('/app/v1/session', 'GET').fail(new DOMException('cancelled secret', 'AbortError'));
    expect(requestDiagnostics.value[0].outcome).toBe('cancelled');
  });
});

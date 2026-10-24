import { beforeEach, describe, expect, it, vi } from 'vitest';
import { PersonaLabClient, technicalDiagnostics } from './client';
import type { LabSession } from './contracts';
const transport = vi.hoisted(() => vi.fn());
const platform = vi.hoisted(() => vi.fn(() => 'web'));
vi.mock('../identity/session', () => ({ accountTransport: transport }));
vi.mock('./platform', () => ({ labClientPlatform: platform }));
beforeEach(() => { transport.mockReset(); platform.mockReset().mockReturnValue('web'); });

describe('Persona Lab account requests', () => {
  it('retains a server trace on a rejected turn for the tester support export', async () => {
    const requestId = '01234567-89ab-4cde-8f01-23456789abcd';
    transport.mockResolvedValue(Response.json({ code: 'lab_provider_no_reply', traceId: requestId }, { status: 504 }));
    await expect(new PersonaLabClient(() => 'tester').text('test', 'Private text'))
      .rejects.toMatchObject({ code: 'lab_provider_no_reply', requestId, status: 504 });
  });
  it('cancels the native-compatible request and rejects a late account response', async () => {
    let identity = 'tester-a'; let complete!: (value: Response) => void;
    transport.mockImplementation(() => new Promise<Response>(resolve => { complete = resolve; }));
    const client = new PersonaLabClient(() => identity);
    const pending = client.sessions();
    const signal = transport.mock.calls[0][1].signal as AbortSignal;
    identity = 'tester-b'; client.invalidate();
    expect(signal.aborted).toBe(true);
    complete(new Response('[]', { status: 200 }));
    await expect(pending).rejects.toMatchObject({ name: 'AbortError' });
  });
  it('sends explicit config confirmation and preserves conflict codes', async () => {
    transport.mockResolvedValue(new Response(JSON.stringify({ code: 'lab_revision_conflict' }), { status: 409 }));
    const client = new PersonaLabClient(() => 'tester');
    await expect(client.publish('draft', 'active')).rejects.toMatchObject({ status: 409, code: 'lab_revision_conflict', failure: { category: 'ClientLifecycle', recoverable: false } });
    expect(transport).toHaveBeenCalledWith('/app/v1/persona-lab/revisions/draft/publish', expect.objectContaining({
      method: 'POST', headers: expect.objectContaining({ 'X-DV-Confirm': 'yes' }), body: '{"expectedActiveRevisionId":"active"}',
    }));
  });
  it('projects diagnostics without content, comments or future payload fields', () => {
    const session = {
      id: 'test', engine: 'gpt-live-1', config: { id: 'revision', instructions: 'PRIVATE PROMPT' }, status: 'ended',
      createdAt: '2026-10-20T00:00:00Z', endedAt: null, platform: 'ios',
      turns: [{ id: 'turn', role: 'user', text: 'PRIVATE TRANSCRIPT', latencyMs: 120, token: 'SECRET' }],
      events: [
        { id: 'event', type: 'provider_tokens', code: 'observed', inputTokens: 0, outputTokens: null, providerSessionId: 'sess_safe-123', payload: 'PRIVATE FUTURE PAYLOAD' },
        { id: 'unsafe', type: 'turn', providerSessionId: 'SECRET authorization header' },
      ],
      evaluation: { comment: 'PRIVATE COMMENT' },
    } as unknown as LabSession;
    const output = JSON.stringify(technicalDiagnostics(session));
    expect(output).toContain('120'); expect(output).toContain('simulationOnly');
    expect(output).toContain('sess_safe-123');
    expect(output).not.toMatch(/PRIVATE|SECRET|instructions|payload|evaluation/);
    expect(JSON.parse(output).platform).toBe('ios');
    expect(JSON.parse(output).events[0]).toMatchObject({ inputTokens: 0, outputTokens: null });
  });
  it('does not admit requests without an account', async () => {
    const client = new PersonaLabClient(() => '');
    await expect(client.catalog()).rejects.toMatchObject({ name: 'AbortError' }); expect(transport).not.toHaveBeenCalled();
  });
  it('sends the prepared image only through explicit consented attachment', async () => {
    transport.mockResolvedValue(new Response('{}', { status: 200 }));
    const client = new PersonaLabClient(() => 'tester'); await client.attachImage('session', 'AA==');
    expect(transport).toHaveBeenCalledWith('/app/v1/persona-lab/sessions/session/image', expect.objectContaining({
      method: 'POST', body: '{"mimeType":"image/jpeg","dataBase64":"AA==","consent":true}',
    }));
  });
  it('preserves a real reauthentication rejection without turning it into a network retry', async () => {
    transport.mockResolvedValue(new Response(JSON.stringify({ code: 'recent_passkey_required' }), { status: 403 }));
    const client = new PersonaLabClient(() => 'tester');
    await expect(client.publish('draft', 'active')).rejects.toMatchObject({
      code: 'recent_passkey_required', failure: { category: 'Auth', recoverable: false },
    });
    expect(transport).toHaveBeenCalledTimes(1);
  });
  it('does not misdiagnose local request rejection or serialization failure as network trouble', async () => {
    const client = new PersonaLabClient(() => 'tester');
    transport.mockRejectedValue(new Error('Neplatná cesta účtu.'));
    await expect(client.catalog()).rejects.toMatchObject({ code: 'request_rejected_locally', failure: { category: 'ClientLifecycle', recoverable: false } });
    transport.mockClear();
    const circular: { parentId?: unknown } = {}; circular.parentId = circular;
    await expect(client.draft(circular as never)).rejects.toMatchObject({ code: 'client_request_invalid', failure: { category: 'ClientLifecycle', recoverable: false } });
    expect(transport).not.toHaveBeenCalled();
  });
  it('keeps opaque transport failures unknown while recognizing observed fetch network failures', async () => {
    const client = new PersonaLabClient(() => 'tester');
    transport.mockRejectedValueOnce(new Error('Unclassified native failure'));
    await expect(client.sessions()).rejects.toMatchObject({ failure: { category: 'Unknown', recoverable: false } });
    transport.mockRejectedValueOnce(new TypeError('Failed to fetch'));
    await expect(client.sessions()).rejects.toMatchObject({ failure: { category: 'Network', recoverable: true } });
    expect(transport).toHaveBeenCalledTimes(2);
  });
  it('propagates the observed client platform to sessions and suites as metadata', async () => {
    transport.mockImplementation(async () => new Response('{}'));
    const client = new PersonaLabClient(() => 'tester');
    await client.createSession({ revisionId: 'r', engine: 'gpt-live-1', scenarioId: 's', fixtureId: 'f', label: 'Test', recordContent: false, platform: 'ios' });
    expect(JSON.parse(transport.mock.calls[0][1].body).platform).toBe('web');
    platform.mockReturnValue('ios');
    await client.createSuite({ revisionId: 'r', engine: 'gpt-live-1', fixtureId: 'f', scenarioIds: ['s'], label: 'Suite', recordContent: false });
    expect(JSON.parse(transport.mock.calls[1][1].body)).toMatchObject({ platform: 'ios', executionPlatform: 'ios' });
  });
  it('reports the executing client platform with each admitted text and voice-ticket request', async () => {
    transport.mockImplementation(async () => new Response('{}'));
    const client = new PersonaLabClient(() => 'tester');
    platform.mockReturnValue('ios');
    await client.text('session', 'hello');
    expect(JSON.parse(transport.mock.calls[0][1].body)).toEqual({ text: 'hello', platform: 'ios' });
    await client.ticket('session', true);
    expect(JSON.parse(transport.mock.calls[1][1].body)).toMatchObject({ recovery: true, platform: 'ios' });
  });
  it('fails closed before text, ticket, and suite activity when the platform is unsupported', async () => {
    platform.mockReturnValue(undefined as unknown as string);
    const client = new PersonaLabClient(() => 'tester');
    await expect(client.text('session', 'hello')).rejects.toMatchObject({ code: 'client_platform_unsupported' });
    await expect(client.ticket('session')).rejects.toMatchObject({ code: 'client_platform_unsupported' });
    await expect(client.createSuite({ revisionId: 'r', engine: 'gpt-live-1', fixtureId: 'f', scenarioIds: ['s'], label: 'Suite', recordContent: false })).rejects.toMatchObject({ code: 'client_platform_unsupported' });
    expect(transport).not.toHaveBeenCalled();
  });
  it('projects only canonical bounded execution platform values into diagnostics', () => {
    const session = { id: 'test', engine: 'gpt-live-1', config: { id: 'revision' }, turns: [], events: [],
      executionPlatforms: ['ios', 'unknown', 'ios', 'android', 'SECRET'] } as unknown as LabSession;
    expect(technicalDiagnostics(session)).toMatchObject({ executionPlatforms: ['ios', 'unknown'] });
    expect(JSON.stringify(technicalDiagnostics(session))).not.toContain('SECRET');
    session.executionPlatforms = ['ios', 'unexpected'];
    expect(technicalDiagnostics(session)).toMatchObject({ executionPlatforms: ['ios', 'unknown'] });
    session.executionPlatforms = [...Array(32).fill('ios'), 'SECRET'];
    expect(technicalDiagnostics(session)).toMatchObject({ executionPlatforms: ['ios', 'unknown'] });
    expect(JSON.stringify(technicalDiagnostics(session))).not.toContain('SECRET');
  });
  it('exports hardware binding observations without a bearer capability or a false simulation claim', () => {
    const hardware = { deviceId: 'case', bootId: 'boot', expectedRevision: 2, conversationId: null, status: 'prepared',
      expiresAt: '2026-10-20T12:03:00Z', testContext: 'SECRET CAPABILITY' };
    const session = { id: 's', config: { id: 'r' }, turns: [], events: [], hardware } as unknown as LabSession;
    const diagnostics = technicalDiagnostics(session);
    expect(diagnostics).toMatchObject({ simulationOnly: false, hardwareBinding: { deviceId: 'case', expectedRevision: 2 } });
    expect(JSON.stringify(diagnostics)).not.toContain('SECRET');
    expect(technicalDiagnostics({ ...session, hardware: null }, true)).toMatchObject({ simulationOnly: null, hardwareUncertain: true });
  });
});

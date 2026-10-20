import { accountTransport } from '../identity/session';
import type { Analytics, Catalog, Evaluation, LabHardwareTicket, LabSession, LabSuite, Revision, RevisionInput, SessionInput, SocketTicket, SuiteInput } from './contracts';
import { classifyLabFailure, describeLabFailure, isNetworkTransportFailure, type LabFailure } from './errors';
import { labClientPlatform } from './platform';
import { normalizeExecutionPlatforms } from './executionPlatforms';
import { sanitizeSystemDiagnostics } from './systemDiagnostics';

const base = '/app/v1/persona-lab';
export class LabError extends Error {
  readonly failure: LabFailure;
  constructor(readonly code: string, readonly status: number) {
    const details = classifyLabFailure(code, status);
    super(describeLabFailure(details)); this.failure = details;
  }
}

export class PersonaLabClient {
  private controller = new AbortController();
  private generation = 0;
  constructor(private identity: () => string) {}
  invalidate(): void { this.generation++; this.controller.abort(); this.controller = new AbortController(); }
  private async request<T>(suffix: string, method = 'GET', body?: unknown, confirmed = false): Promise<T> {
    const generation = this.generation; const identity = this.identity(); const signal = this.controller.signal;
    const current = () => {
      if (!identity || identity !== this.identity() || generation !== this.generation || signal.aborted)
        throw new DOMException('Kontext laboratoře se změnil.', 'AbortError');
    };
    current();
    let serialized: string | undefined;
    try { serialized = body === undefined ? undefined : JSON.stringify(body); }
    catch { throw new LabError('client_request_invalid', 0); }
    let response: Response;
    try {
      response = await accountTransport(base + suffix, {
        method, signal, redirect: 'error', headers: { Accept: 'application/json',
          ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
          ...(confirmed ? { 'X-DV-Confirm': 'yes' } : {}) },
        body: serialized,
      });
    } catch (cause) {
      current();
      if (cause instanceof DOMException && cause.name === 'AbortError') throw cause;
      if (isNetworkTransportFailure(cause)) throw new LabError('transport_error', 0);
      if (cause && typeof cause === 'object' && 'code' in cause && typeof cause.code === 'string')
        throw new LabError(cause.code, 'status' in cause && typeof cause.status === 'number' ? cause.status : 0);
      if (cause instanceof Error && ['Neplatná cesta účtu.', 'Nepodporovaný formát požadavku účtu.', 'Požadavek překročil povolenou velikost.'].includes(cause.message))
        throw new LabError('request_rejected_locally', 0);
      throw new LabError('transport_unclassified', 0);
    }
    current();
    const value = await response.json().catch(() => null); current();
    if (!response.ok) throw new LabError(value?.code ?? 'invalid_response', response.status);
    if (value === null && response.status !== 204) throw new LabError('invalid_response', 502);
    return value as T;
  }
  catalog() { return this.request<Catalog>('/catalog'); }
  async systemDiagnostics() { return sanitizeSystemDiagnostics(await this.request<unknown>('/diagnostics')); }
  sessions() { return this.request<LabSession[]>('/sessions'); }
  session(id: string) { return this.request<LabSession>(`/sessions/${encodeURIComponent(id)}`); }
  hardwareTicket(id: string, target: { deviceId: string; bootId: string; expectedRevision: number }) {
    return this.request<LabHardwareTicket>(`/sessions/${encodeURIComponent(id)}/hardware-ticket`, 'POST', target);
  }
  createSession(input: SessionInput) {
    const platform = labClientPlatform();
    if (!platform) return Promise.reject(new LabError('client_platform_unsupported', 0));
    return this.request<LabSession>('/sessions', 'POST', { ...input, platform });
  }
  text(id: string, text: string) {
    const platform = labClientPlatform();
    if (!platform) return Promise.reject(new LabError('client_platform_unsupported', 0));
    return this.request<LabSession>(`/sessions/${encodeURIComponent(id)}/turns`, 'POST', { text, platform });
  }
  end(id: string) { return this.request<LabSession>(`/sessions/${encodeURIComponent(id)}/end`, 'POST', {}); }
  ticket(id: string, recovery = false) {
    const platform = labClientPlatform();
    if (!platform) return Promise.reject(new LabError('client_platform_unsupported', 0));
    const socketOrigin = window.location.protocol === 'capacitor:' && window.location.hostname === 'localhost'
      ? 'capacitor://localhost' : window.location.origin;
    return this.request<SocketTicket>(`/sessions/${encodeURIComponent(id)}/socket-ticket`, 'POST', { socketOrigin, recovery, platform });
  }
  evaluate(id: string, evaluation: Evaluation) { return this.request<LabSession>(`/sessions/${encodeURIComponent(id)}/evaluation`, 'PUT', evaluation); }
  attachImage(id: string, dataBase64: string) { return this.request<LabSession>(`/sessions/${encodeURIComponent(id)}/image`, 'POST', { mimeType: 'image/jpeg', dataBase64, consent: true }); }
  removeImage(id: string) { return this.request<LabSession>(`/sessions/${encodeURIComponent(id)}/image`, 'DELETE'); }
  analytics() { return this.request<Analytics>('/analytics'); }
  suites() { return this.request<LabSuite[]>('/suites'); }
  createSuite(input: SuiteInput) {
    const platform = labClientPlatform();
    if (!platform) return Promise.reject(new LabError('client_platform_unsupported', 0));
    return this.request<LabSuite>('/suites', 'POST', { ...input, platform, executionPlatform: platform });
  }
  draft(input: RevisionInput) { return this.request<Revision>('/revisions', 'POST', input, true); }
  publish(id: string, expectedActiveRevisionId: string | null) {
    return this.request<Revision>(`/revisions/${encodeURIComponent(id)}/publish`, 'POST', { expectedActiveRevisionId }, true);
  }
  rollback(revisionId: string, expectedActiveRevisionId: string | null) {
    return this.request<Revision>('/rollback', 'POST', { revisionId, expectedActiveRevisionId }, true);
  }
}

// Deliberately project known technical fields. Transcript, drafts and tickets never
// enter a diagnostic download even if future server events gain extra fields.
export function technicalDiagnostics(session: LabSession, hardwareUncertain = false): object {
  return {
    schemaVersion: 1, simulationOnly: hardwareUncertain ? null : !session.hardware, hardwareUncertain,
    sessionId: session.id, engine: session.engine, platform: session.platform ?? 'unknown',
    executionPlatforms: normalizeExecutionPlatforms(session.executionPlatforms),
    hardwareBinding: session.hardware ? { deviceId: session.hardware.deviceId, bootId: session.hardware.bootId,
      expectedRevision: session.hardware.expectedRevision, conversationId: session.hardware.conversationId,
      status: session.hardware.status, expiresAt: session.hardware.expiresAt } : null,
    revisionId: session.config.id, status: session.status, createdAt: session.createdAt, endedAt: session.endedAt,
    turns: session.turns.map(({ id, role, createdAt, latencyMs, audioBytes, decision, compartment }) =>
      ({ id, role, createdAt, latencyMs, audioBytes, decision, compartment })),
    events: session.events.map(({ id, type, createdAt, code, latencyMs, decision, compartment, costMicroUsd, providerSessionId, inputTokens, outputTokens }) =>
      ({ id, type, createdAt, code, latencyMs, decision, compartment, costMicroUsd, inputTokens, outputTokens,
        providerSessionId: typeof providerSessionId === 'string' && /^[A-Za-z0-9_-]{1,128}$/.test(providerSessionId) ? providerSessionId : null })),
  };
}

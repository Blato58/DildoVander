import type { LabEvent, SystemDiagnostics, SystemDiagnosticSession } from './contracts';

function object(value: unknown): Record<string, unknown> { return value !== null && typeof value === 'object' ? value as Record<string, unknown> : {}; }
function text(value: unknown, limit = 128): string | null { return typeof value === 'string' && value.length > 0 && value.length <= limit ? value : null; }
function count(value: unknown): number | null { return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null; }
function flag(value: unknown): boolean | null { return typeof value === 'boolean' ? value : null; }
function safeEvent(value: unknown): LabEvent | null {
  const event = object(value); const id = text(event.id); const type = text(event.type, 80);
  if (!id || !type) return null;
  const providerSessionId = text(event.providerSessionId);
  return { id, type, createdAt: text(event.createdAt, 64) ?? '', code: text(event.code),
    latencyMs: count(event.latencyMs), decision: text(event.decision, 64),
    compartment: typeof event.compartment === 'number' && Number.isInteger(event.compartment) && event.compartment >= 1 && event.compartment <= 6 ? event.compartment : null,
    inputTokens: count(event.inputTokens), outputTokens: count(event.outputTokens), costMicroUsd: count(event.costMicroUsd),
    providerSessionId: providerSessionId && /^[A-Za-z0-9_-]{1,128}$/.test(providerSessionId) ? providerSessionId : null };
}
/** Explicit projection is shared by display and export; future response fields are never retained. */
export function sanitizeSystemDiagnostics(value: unknown): SystemDiagnostics {
  const root = object(value); const storage = object(root.storage);
  const source = Array.isArray(storage.recentSessions) ? storage.recentSessions : [];
  const recentSessions = source.slice(0, 20).flatMap((value): SystemDiagnosticSession[] => {
    const session = object(value); const sessionId = text(session.sessionId); if (!sessionId) return [];
    const rawEvents = Array.isArray(session.events) ? session.events : [];
    const events = rawEvents.slice(-20).map(safeEvent).filter((event): event is LabEvent => event !== null);
    return [{ sessionId, engine: text(session.engine), personaRevisionId: text(session.personaRevisionId),
      platform: session.platform === 'web' || session.platform === 'ios' ? session.platform : 'unknown',
      createdAt: text(session.createdAt, 64), endedAt: text(session.endedAt, 64), status: text(session.status, 32),
      testMode: session.testMode === 'simulation' || session.testMode === 'hardware' ? session.testMode : null,
      eventsTruncated: session.eventsTruncated === true || rawEvents.length !== events.length, events }];
  });
  return { storage: { sessions: count(storage.sessions), revisions: count(storage.revisions), recentSessions,
    sessionsTruncated: storage.sessionsTruncated === true || source.length !== recentSessions.length },
    activeConnections: count(root.activeConnections), liveAvailable: flag(root.liveAvailable), realtimeAvailable: flag(root.realtimeAvailable),
    simulationOnly: flag(root.simulationOnly), hardwareContext: flag(root.hardwareContext) };
}
export function systemDiagnosticsBundle(value: SystemDiagnostics): object {
  return { schemaVersion: 1, scope: 'admin_technical_metadata', limits: { sessions: 20, eventsPerSession: 20 }, ...sanitizeSystemDiagnostics(value) };
}

export type LabEngine = 'gpt-live-1' | 'gpt-realtime-2.1';
export const metricLabels = {
  overall: 'Celková kvalita', humor: 'Humor', personaConsistency: 'Konzistence persony', naturalness: 'Přirozenost',
  responsiveness: 'Pohotovost', memoryAccuracy: 'Přesnost paměti', technicalReliability: 'Technická spolehlivost',
} as const;
export type Metric = keyof typeof metricLabels;
export type Scores = Record<Metric, number>;
export interface Revision {
  id: string; parentId: string | null; name: string; instructions: string; engine: LabEngine;
  voice: string; status: 'draft' | 'published' | 'baseline'; sha256?: string; sourceVersion?: string;
  createdAt: string; publishedAt: string | null; revision: number;
}
export interface Scenario { id: string; name: string; description: string; openingText: string }
export interface MemoryFixture { id: string; name: string; description: string; current: string; evening: string; returning: string }
export interface Catalog {
  capabilities: { live: boolean; realtime: boolean; voice: boolean; text: boolean; simulationOnly: boolean; imageRealtime?: boolean };
  activeRevisionId: string | null; versions: Revision[]; scenarios: Scenario[]; memoryFixtures: MemoryFixture[];
}
export interface Turn {
  id: string; role: 'user' | 'assistant'; text: string; createdAt: string;
  latencyMs?: number | null; audioBytes?: number | null; decision?: string | null; compartment?: number | null;
}
export interface LabEvent {
  id: string; type: string; createdAt: string; code?: string | null; latencyMs?: number | null;
  decision?: string | null; compartment?: number | null; costMicroUsd?: number | null;
  providerSessionId?: string | null;
}
export interface Evaluation { scores: Scores; comment: string; turnComments: { turnId: string; comment: string }[] }
export interface LabSession {
  id: string; label: string; ownerId: string; createdAt: string; endedAt: string | null;
  status: 'open' | 'ended' | 'failed'; engine: LabEngine; scenarioId: string; fixtureId: string;
  comparisonId?: string | null; recordContent: boolean; hasImage?: boolean; config: Revision; turns: Turn[]; events: LabEvent[]; evaluation?: Evaluation | null;
  memorySnapshot?: MemoryFixture | null; scenarioSnapshot?: Scenario | null;
}
export interface Analytics {
  sessions: number; evaluations: number; metrics: { name: string; mean: number | null; count: number }[];
  comparisons: { id: string; sessions: string[] }[];
  summary?: ReliabilitySummary;
  errors?: { code: string; count: number }[];
  models?: { model: string; sessions: number; summary: ReliabilitySummary }[];
  versions?: { versionId: string; sessions: number; summary: ReliabilitySummary; scores: { name: string; mean: number | null }[] }[];
}
export interface ReliabilitySummary {
  successfulSessions: number; failedSessions: number; preparedSessions: number;
  connectionAttempts: number; establishedConnections: number; unexpectedDisconnections: number;
  unexpectedDisconnectionRate: number | null; recoveryAttempts: number; successfulRecoveries: number; recoveryRate: number | null;
  latencySamples: number; averageResponseMs: number | null; p95ResponseMs: number | null; averageDurationSeconds: number | null;
  estimatedCostUsd: number | null; costMeasuredSessions: number;
}
export interface SessionInput { revisionId: string; engine: LabEngine; scenarioId: string; fixtureId: string; label: string; comparisonId?: string; recordContent: boolean }
export interface RevisionInput { parentId: string; name: string; instructions: string; engine: LabEngine; voice: string }
export interface SocketTicket { url: string; path: string; expiresAt: string }
export interface LabSuite {
  id: string; ownerId: string; status: 'running' | 'completed' | 'failed'; createdAt: string; completedAt: string | null;
  revisionId: string; engine: LabEngine;
  results: { scenarioId: string; sessionId: string; status: string; assertions: { name: string; passed: boolean; detail: string }[] }[];
}
export interface SuiteInput { revisionId: string; engine: LabEngine; fixtureId: string; scenarioIds: string[]; label: string; recordContent: boolean }

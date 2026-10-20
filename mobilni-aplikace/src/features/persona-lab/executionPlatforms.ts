import type { LabClientPlatform, LabPlatform, LabSession } from './contracts';

const allowedPlatforms: readonly LabPlatform[] = ['web', 'ios', 'unknown'];

export function normalizeExecutionPlatforms(value: unknown): LabPlatform[] {
  if (!Array.isArray(value)) return [];
  const platforms = new Set<LabPlatform>();
  if (value.length > 32) platforms.add('unknown');
  for (const item of value.slice(0, 32)) {
    platforms.add(typeof item === 'string' && allowedPlatforms.includes(item as LabPlatform) ? item as LabPlatform : 'unknown');
  }
  return allowedPlatforms.filter(platform => platforms.has(platform));
}

export type ExecutionPlatformGroup = LabClientPlatform | 'mixed' | 'unknown';

export function executionPlatformGroup(value: unknown): ExecutionPlatformGroup {
  const platforms = normalizeExecutionPlatforms(value);
  if (!platforms.length || platforms.length === 1 && platforms[0] === 'unknown') return 'unknown';
  if (platforms.length > 1) return 'mixed';
  return platforms[0] as LabClientPlatform;
}

export function executionPlatformLabel(value: unknown): string {
  const platforms = normalizeExecutionPlatforms(value);
  if (!platforms.length) return 'Neuvedeno';
  if (platforms.length === 1) return platforms[0] === 'ios' ? 'iOS' : platforms[0] === 'web' ? 'Web' : 'Neznámá';
  const labels = platforms.map(platform => platform === 'ios' ? 'iOS' : platform === 'web' ? 'Web' : 'neznámá');
  return `Smíšené (${labels.join(', ')})`;
}

export function comparableExecutionPlatform(session: LabSession): LabClientPlatform | null {
  const platforms = normalizeExecutionPlatforms(session.executionPlatforms);
  return platforms.length === 1 && platforms[0] !== 'unknown' ? platforms[0] : null;
}

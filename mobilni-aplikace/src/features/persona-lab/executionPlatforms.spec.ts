import { describe, expect, it } from 'vitest';
import { executionPlatformGroup, executionPlatformLabel, normalizeExecutionPlatforms } from './executionPlatforms';

describe('reported execution platforms', () => {
  it('accepts only bounded canonical values and returns a stable distinct order', () => {
    expect(normalizeExecutionPlatforms(['ios', 'ios', 'web', 'unknown', 'android', 4])).toEqual(['web', 'ios', 'unknown']);
    expect(normalizeExecutionPlatforms(['ios', ...Array.from({ length: 32 }, () => 'unknown')])).toEqual(['ios', 'unknown']);
    expect(normalizeExecutionPlatforms('ios')).toEqual([]);
  });

  it('does not turn missing, malformed, or legacy preparation data into execution evidence', () => {
    expect(executionPlatformGroup(undefined)).toBe('unknown');
    expect(executionPlatformGroup(['android'])).toBe('unknown');
    expect(executionPlatformLabel(undefined)).toBe('Neuvedeno');
    expect(executionPlatformLabel(['unknown'])).toBe('Neznámá');
    expect(normalizeExecutionPlatforms(['ios', 'unexpected', null])).toEqual(['ios', 'unknown']);
    expect(normalizeExecutionPlatforms([...Array(32).fill('ios'), 'unexpected'])).toEqual(['ios', 'unknown']);
  });

  it('labels mixed known and unknown execution reports explicitly', () => {
    expect(executionPlatformGroup(['ios', 'web'])).toBe('mixed');
    expect(executionPlatformGroup(['web', 'unknown'])).toBe('mixed');
    expect(executionPlatformLabel(['ios', 'web'])).toBe('Smíšené (Web, iOS)');
    expect(executionPlatformLabel(['ios', 'unknown'])).toBe('Smíšené (iOS, neznámá)');
  });
});

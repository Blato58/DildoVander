import { describe, expect, it } from 'vitest';
import { assertionLabel, observedTokens } from './observations';
import type { LabEvent, LabSession } from './contracts';
const session = (events: Partial<LabEvent>[], id = 's') => ({ id, events } as LabSession);
describe('observed Persona Lab evidence', () => {
  it('sums only unique incremental token events and retains partial coverage', () => {
    const usage = { id: 'u', type: 'provider_tokens', code: 'observed', inputTokens: 12, outputTokens: null };
    const result = observedTokens([session([usage, usage, { id: 'v', type: 'provider_tokens', code: 'observed', inputTokens: 0, outputTokens: 3 },
      { id: 'ignored', type: 'provider_cost', code: 'micro_usd', inputTokens: 9000 }]), session([], 'live')]);
    expect(result).toEqual({ input: 12, output: 3, inputSamples: 2, outputSamples: 1, inputSessions: 1, outputSessions: 1, totalSessions: 2 });
  });
  it('keeps observed zero distinct from missing token counts and does not infer Live usage', () => {
    expect(observedTokens([session([{ id: 'seconds', type: 'provider_usage', code: 'live_seconds', latencyMs: 1000 }])]).input).toBeNull();
    const result = observedTokens([session([{ id: 'zero', type: 'provider_tokens', code: 'observed', inputTokens: 0, outputTokens: null }])]);
    expect(result.input).toBe(0); expect(result.output).toBeNull();
  });
  it('does not promote an unexercised scripted assertion into a pass or failure', () => {
    expect(assertionLabel({ name: 'interrupt', passed: true, status: 'not_exercised', detail: 'No voice transport' })).toBe('Neověřeno textovým během');
    expect(assertionLabel({ name: 'input', passed: true, status: 'failed', detail: 'Failed assertion' })).toBe('Nesplněno');
  });
});

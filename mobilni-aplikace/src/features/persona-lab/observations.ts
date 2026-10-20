import type { LabAssertion, LabSession } from './contracts';

export function assertionLabel(assertion: LabAssertion): string {
  if (assertion.status === 'not_exercised') return 'Neověřeno textovým během';
  return (assertion.status ? assertion.status === 'passed' : assertion.passed) ? 'Splněno' : 'Nesplněno';
}

export function observedTokens(sessions: readonly LabSession[]) {
  const seen = new Set<string>(); const inputSessions = new Set<string>(); const outputSessions = new Set<string>();
  let input = 0; let output = 0; let inputSamples = 0; let outputSamples = 0;
  for (const session of sessions) {
    for (const event of session.events) {
      const key = `${session.id}:${event.id}`;
      if (event.type !== 'provider_tokens' || event.code !== 'observed' || seen.has(key)) continue;
      seen.add(key);
      if (event.inputTokens != null && Number.isSafeInteger(event.inputTokens) && event.inputTokens >= 0) {
        input += event.inputTokens; inputSamples++; inputSessions.add(session.id);
      }
      if (event.outputTokens != null && Number.isSafeInteger(event.outputTokens) && event.outputTokens >= 0) {
        output += event.outputTokens; outputSamples++; outputSessions.add(session.id);
      }
    }
  }
  return { input: inputSamples ? input : null, output: outputSamples ? output : null,
    inputSamples, outputSamples, inputSessions: inputSessions.size, outputSessions: outputSessions.size, totalSessions: sessions.length };
}

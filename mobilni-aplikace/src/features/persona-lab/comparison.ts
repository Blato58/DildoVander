import type { LabSession } from './contracts';
import { observedTokens } from './observations';

export function chooseComparison(pair: { left: string; right: string }, side: 'left' | 'right', id: string) {
  const next = { ...pair, [side]: id };
  if (id && next.left === next.right) next[side === 'left' ? 'right' : 'left'] = '';
  return next;
}

export function comparisonObservations(session: LabSession) {
  const latencies = session.turns.filter(turn => turn.role === 'assistant').flatMap(turn =>
    turn.latencyMs != null && Number.isFinite(turn.latencyMs) && turn.latencyMs >= 0 ? [turn.latencyMs] : []).sort((a, b) => a - b);
  const failures = session.events.filter(event => event.type === 'provider_failure' && !['cancelled', 'user_cancelled'].includes(event.code ?? '')).length;
  const costIds = new Set<string>(); let costMicroUsd = 0;
  for (const event of session.events) {
    if (event.type !== 'provider_cost' || event.code !== 'micro_usd' || event.costMicroUsd == null
      || !Number.isSafeInteger(event.costMicroUsd) || event.costMicroUsd < 0 || costIds.has(event.id)) continue;
    costIds.add(event.id); costMicroUsd += event.costMicroUsd;
  }
  return { latencyCount: latencies.length, meanMs: latencies.length ? latencies.reduce((sum, value) => sum + value, 0) / latencies.length : null,
    p95Ms: latencies.length ? latencies[Math.ceil(latencies.length * .95) - 1] : null,
    failures, status: session.status, tokens: observedTokens([session]),
    cost: { estimatedUsd: costIds.size && Number.isSafeInteger(costMicroUsd) ? costMicroUsd / 1_000_000 : null, samples: costIds.size } };
}

function memoryContent(session: LabSession): string | null {
  const memory = session.memorySnapshot;
  return memory ? JSON.stringify([memory.id, memory.current, memory.evening, memory.returning]) : null;
}
export function compareSessions(left: LabSession, right: LabSession, suiteSessionIds: ReadonlySet<string>) {
  const reasons: string[] = [];
  if (left.id === right.id) reasons.push('Vyberte dvě různá sezení.');
  const leftHash = left.scenarioSnapshot?.scriptSha256; const rightHash = right.scenarioSnapshot?.scriptSha256;
  if (left.scenarioId !== right.scenarioId || !leftHash || !rightHash || leftHash !== rightHash)
    reasons.push('Shodný scénář a přesný otisk skriptu nejsou potvrzené.');
  const leftMemory = memoryContent(left); const rightMemory = memoryContent(right);
  if (left.fixtureId !== right.fixtureId || leftMemory === null || rightMemory === null || leftMemory !== rightMemory)
    reasons.push('Shodný skutečný snímek paměti není potvrzený.');
  if (!!left.hardware !== !!right.hardware) reasons.push('Testy používají odlišné prostředí: simulaci a skutečný kufr.');
  if (left.platform === 'unknown' || right.platform === 'unknown' || !left.platform || !right.platform || left.platform !== right.platform)
    reasons.push('Shodná platforma není potvrzená.');
  const scripted = suiteSessionIds.has(left.id) && suiteSessionIds.has(right.id);
  if (!scripted) reasons.push('Oba testy nejsou doložené jako běhy stejného skriptu; ruční vstupy se mohly lišit.');
  if (left.status !== 'ended' || right.status !== 'ended') reasons.push('Oba testy musejí být dokončené bez selhání sezení.');
  if ([left, right].some(session => !session.scenarioSnapshot?.script?.length
    || session.turns.filter(turn => turn.role === 'assistant').length !== session.scenarioSnapshot.script.length))
    reasons.push('Úplný počet odpovědí skriptu není potvrzený.');
  const a = left.evaluation?.scores.overall; const b = right.evaluation?.scores.overall;
  if (![a, b].every(value => value != null && Number.isInteger(value) && value >= 1 && value <= 5)) reasons.push('Chybí platné lidské celkové hodnocení obou testů.');
  const modelChanged = left.engine !== right.engine;
  const voiceChanged = left.config.voice !== right.config.voice;
  const recommendation = reasons.length ? 'Nedostatek srovnatelných podkladů pro doporučení.'
    : a === b ? 'Lidské celkové hodnocení je shodné; tento pár neurčuje preferovanou variantu.'
      : `Pro další ověřovací běh předběžně ${a! > b! ? 'A' : 'B'}: má v tomto páru vyšší lidské celkové hodnocení (${a}/5 proti ${b}/5).`;
  return { reasons, recommendation, modelChanged, voiceChanged, left: comparisonObservations(left), right: comparisonObservations(right) };
}

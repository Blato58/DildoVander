import { GuestError, isUuid } from './client';
export { publicIdentity, type WizardIdentity } from './client';
export interface Prophecy {
  editionId: string; state: 'locked' | 'revealed'; unlockAt: string; revelation?: string; afterword?: string;
}
const object = (raw: unknown): Record<string, unknown> => raw !== null && typeof raw === 'object' ? raw as Record<string, unknown> : {};
const text = (raw: unknown, max = 2000) => typeof raw === 'string' && raw.length <= max ? raw : '';
const number = (raw: unknown) => typeof raw === 'number' && Number.isFinite(raw) && raw >= 0 ? raw : 0;

export function ownerProphecy(raw: unknown): Prophecy | null {
  const source = object(raw);
  if (!isUuid(source.editionId) || !['locked', 'revealed'].includes(String(source.state))) return null;
  const result: Prophecy = { editionId: source.editionId, state: source.state as Prophecy['state'], unlockAt: text(source.unlockAt, 80) };
  // Even a malformed locked response must not retain a future revelation in UI memory.
  if (result.state === 'revealed') {
    result.revelation = text(source.revelation, 16000); result.afterword = text(source.afterword, 4000);
  }
  return result;
}

export interface Chronicle {
  schemaVersion: 1; eveningId: string; revision: number; provisional: boolean; createdAt: string;
  completedVisits: number; confirmedPresentations: number; completedDuels: number;
  interruptedVisits: number; unresolvedVisits: number; mostPopularSlots: number[]; narrative: string;
  wands: { slot: number; officialName: string; confirmedPresentations: number }[];
}
export function projectChronicle(raw: unknown): Chronicle {
  const value = object(raw);
  if (value.schemaVersion !== 1 || !text(value.eveningId, 240).trim() || !Number.isInteger(value.revision) || !Array.isArray(value.wands)) throw new GuestError(503);
  return { schemaVersion: 1, eveningId: text(value.eveningId, 240), revision: number(value.revision), provisional: value.provisional === true,
    createdAt: text(value.createdAt, 80), completedVisits: number(value.completedVisits),
    confirmedPresentations: number(value.confirmedPresentations), completedDuels: number(value.completedDuels),
    interruptedVisits: number(value.interruptedVisits), unresolvedVisits: number(value.unresolvedVisits), narrative: text(value.narrative, 16000),
    mostPopularSlots: Array.isArray(value.mostPopularSlots) ? value.mostPopularSlots.filter(slot => Number.isInteger(slot) && slot >= 1 && slot <= 6) : [],
    wands: value.wands.slice(0, 6).map(raw => { const wand = object(raw); return { slot: number(wand.slot), officialName: text(wand.officialName, 240), confirmedPresentations: number(wand.confirmedPresentations) }; }) };
}
export type DuelSide = 'left' | 'right';
export type DuelAction = 'strike' | 'trick' | 'shield';
export interface DuelAccess { token: string; side: DuelSide }
export const duelStates: Record<string, string> = {
  AwaitingParticipants: 'Čekáme na přístupy obou stran.', Ready: 'Souboj připraven. Spouští ho obsluha.',
  Running: 'Souboj právě probíhá.', Completed: 'Souboj dokončen.', Cancelled: 'Souboj zrušen. Vítěz nebyl vyhlášen.', Interrupted: 'Souboj přerušen. Vítěz nebyl vyhlášen.',
};
export const duelActions: Record<DuelAction, string> = { strike: 'Úder', trick: 'Lest', shield: 'Štít' };
interface DuelIdentity { displayName: string; wandId: string; strength: number; cunning: number; defense: number }
export interface DuelRound { number: number; leftAction: string; rightAction: string; leftScore: number; rightScore: number; leftAutomatic: boolean; rightAutomatic: boolean; quip: string; mishap: string }
export interface Duel {
  id: string; revision: number; state: string; mode: string; round: number; deadline: string | null;
  left: DuelIdentity; right: DuelIdentity; leftChoiceLocked: boolean; rightChoiceLocked: boolean;
  rounds: DuelRound[]; narrationMode: string; narrationStatus: string;
  result: { ending: string; leftPoints: number; rightPoints: number; exhibition: boolean } | null;
  commentary: { provider: string; rounds: { round: number; quip: string; mishap: string }[] } | null;
}
export function projectDuel(raw: unknown, id: string): Duel {
  const source = object(raw);
  if (source.id !== id || !isUuid(id) || !Number.isInteger(source.revision) || number(source.revision) !== source.revision
    || !duelStates[String(source.state)] || !Array.isArray(source.rounds) || source.rounds.length > 3 || !source.left || !source.right) throw new GuestError(503);
  const identity = (raw: unknown): DuelIdentity => { const item = object(raw); return { displayName: text(item.displayName, 240), wandId: text(item.wandId, 240), strength: number(item.strength), cunning: number(item.cunning), defense: number(item.defense) }; };
  const result = object(source.result); const commentary = object(source.commentary);
  return { id, revision: source.revision as number, state: String(source.state), mode: text(source.mode, 40), round: number(source.round),
    deadline: typeof source.deadline === 'string' ? text(source.deadline, 80) : null, left: identity(source.left), right: identity(source.right),
    leftChoiceLocked: source.leftChoiceLocked === true, rightChoiceLocked: source.rightChoiceLocked === true,
    narrationMode: text(source.narrationMode, 80), narrationStatus: text(source.narrationStatus, 100),
    rounds: source.rounds.map(raw => { const item = object(raw); return { number: number(item.number), leftAction: text(item.leftAction, 20), rightAction: text(item.rightAction, 20), leftScore: number(item.leftScore), rightScore: number(item.rightScore), leftAutomatic: item.leftAutomatic === true, rightAutomatic: item.rightAutomatic === true, quip: text(item.quip), mishap: text(item.mishap) }; }),
    result: source.state === 'Completed' && source.result ? { ending: text(result.ending), leftPoints: number(result.leftPoints), rightPoints: number(result.rightPoints), exhibition: result.exhibition === true } : null,
    commentary: source.commentary && Array.isArray(commentary.rounds) ? { provider: text(commentary.provider, 80), rounds: commentary.rounds.slice(0, 3).map(raw => { const item = object(raw); return { round: number(item.round), quip: text(item.quip), mishap: text(item.mishap) }; }) } : null };
}
export function duelNarration(view: Duel) {
  if (view.narrationMode === 'web_only') return 'Pouze webový průběh. Mluvené přehrání v kufru není potvrzené.';
  if (view.narrationMode !== 'physical_device') return 'Přehrání v kufru zatím není potvrzené.';
  if (view.narrationStatus === 'choice') return 'Zařízení potvrdilo předchozí přehrání. Nyní je prostor pro tahy.';
  if (view.narrationStatus === 'playback_confirmed') return 'Zařízení potvrdilo přehrání úvodu, komentářů kol i závěru.';
  if (['interrupted', 'cancelled'].includes(view.narrationStatus)) return 'Další přehrávání bylo přerušeno nebo odvoláno.';
  return 'Čekáme na potvrzené přehrání ze zařízení.';
}

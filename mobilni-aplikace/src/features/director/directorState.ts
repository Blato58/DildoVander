import type { ShowState } from './DirectorClient';
export interface Identity { identityId: string; displayName: string; wandId: string; wandName?: string; spells?: { action: string; name: string; description: string }[] }
export interface Prophecy { outcomeId: string; omen: string; unlockAt: string; state: string }
export interface Duel {
  id: string; deviceId: string; eveningId: string; revision: number; state: string; round: number;
  mode: 'autoplay' | 'interactive' | 'exhibition';
  narrationMode?: 'unbound' | 'web_only' | 'physical_device';
  narrationStatus?: string; narrationUnavailableReason?: string | null; deadline?: string | null;
  requiresParticipantConsent?: boolean; leftJoined?: boolean; rightJoined?: boolean;
  left: { displayName: string; wandName?: string }; right: { displayName: string; wandName?: string };
  result?: { winner: string; ending: string } | null;
}

export function duelNarrationLabel(duel: Pick<Duel, 'narrationMode' | 'narrationStatus'>): string {
  if (duel.narrationMode === 'web_only') return 'Webový souboj. Hlas kufru se nespouští.';
  if (duel.narrationMode === 'physical_device') {
    return duel.narrationStatus === 'playback_confirmed'
      ? 'Zařízení potvrdilo dokončení přehrání. Slyšitelnost nebyla změřena.'
      : 'Vyprávění je určeno kufru. Dokončení přehrání zatím není potvrzené.';
  }
  return 'Způsob vyprávění zatím není potvrzený. Tento pohled nedokládá zvuk z kufru.';
}

export interface RemoteStopOperation {
  id: string;
  pending: boolean;
  status: 'sending' | 'accepted' | 'unknown' | 'rejected';
  expectedDeviceRevision?: number;
  acceptedAt?: string;
}

export function remoteStopStatus(state: ShowState | undefined, operation: RemoteStopOperation | undefined,
  snapshotReceivedAt: number, now = Date.now()): string {
  if (!operation) return '';
  if (operation.pending) return 'Stop odesílám přes backend. Výsledek zařízení zatím není potvrzený.';
  if (operation.status === 'unknown' || operation.status === 'sending') return 'Výsledek Stop přes backend je neznámý. Povel se automaticky neopakuje; ověřte kufr nebo použijte místní či fyzické zastavení.';
  if (operation.status === 'rejected') return 'Backend Stop nepřijal. Ověřte přístup; místní a fyzické zastavení mají vlastní cestu.';
  const report = state?.deviceSettings;
  const observedAt = report ? Date.parse(report.observedAt) : NaN;
  const acceptedAt = operation.acceptedAt ? Date.parse(operation.acceptedAt) : NaN;
  const fresh = now - snapshotReceivedAt < 12000 && now - observedAt < 12000 && observedAt <= now + 5000;
  const confirmed = operation.expectedDeviceRevision !== undefined && operation.expectedDeviceRevision > 0
    && state?.deviceStopRevision === operation.expectedDeviceRevision
    && report?.stopRevision === operation.expectedDeviceRevision && observedAt >= acceptedAt && fresh;
  return confirmed ? 'Zařízení potvrdilo uplatnění Stop. Fyzické zastavení nebylo změřeno.'
    : 'Backend přijal Stop. Čekám na aktuální potvrzení stejné revize zařízením; fyzické zastavení není ověřené.';
}

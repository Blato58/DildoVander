import type { DeviceSnapshot } from '../device_connection/FakeDeviceConnection';
import type { LabSession } from './contracts';

export interface LabHardwareTarget {
  deviceId: string; bootId: string; revision: number; connectionEpoch: number; mode: 'gpt-live' | 'realtime'; voice: 'cedar' | 'marin' | 'coral';
}
// Technical correlation only. The bearer context never enters observable UI state.
export async function hardwareContextId(testContext: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(testContext));
  return Array.from(new Uint8Array(digest), byte => byte.toString(16).padStart(2, '0')).join('');
}
export function hardwareEligibility(session: LabSession | null, snapshot: DeviceSnapshot | null, native: boolean,
  pairedDeviceId: string | undefined, canOperate: boolean, enabled: boolean, connectionEpoch?: number) {
  const reject = (reason: string, target: LabHardwareTarget | null = null, canConfigure = false) => ({ target, reason, ready: false, canConfigure });
  if (!enabled) return reject('Server nepovolil izolovaný testovací kontext pro skutečný kufr.');
  if (!native) return reject('Skutečný kufr vyžaduje připojenou nativní aplikaci na iPhonu.');
  if (!canOperate) return reject('Účet nemá potvrzené oprávnění operate pro tento kufr. Simulace zůstává dostupná.');
  if (!snapshot || snapshot.source !== 'native-device' || snapshot.phase === 'disconnected') return reject('Není připojený ověřený skutečný kufr.');
  if (!snapshot.capabilities?.includes('voiceTestContext')) return reject('Firmware nepředložil schopnost voiceTestContext. Produkční hlas se místo testu nespustí.');
  const identity = snapshot.diagnostics;
  if (!identity?.deviceId || identity.deviceId !== pairedDeviceId || !identity.bootId
    || !Number.isSafeInteger(snapshot.revision) || snapshot.revision! < 0 || !Number.isSafeInteger(connectionEpoch) || connectionEpoch! < 0)
    return reject('Chybí shodná identita spárovaného kufru, boot, revize nebo generace spojení.');
  if (!session || session.status !== 'open') return reject('Nejprve připravte otevřené testovací sezení.');
  if (session.hardware) return reject('Toto sezení už má vazbu na skutečný kufr. Pro nový start připravte nové sezení.');
  const voice = session.config.voice;
  if (!['cedar', 'marin', 'coral'].includes(voice)) return reject('Uložený hlas persony není podporovaný tímto kufrem.');
  const target: LabHardwareTarget = { deviceId: identity.deviceId, bootId: identity.bootId, revision: snapshot.revision!, connectionEpoch: connectionEpoch!,
    mode: session.engine === 'gpt-live-1' ? 'gpt-live' : 'realtime', voice: voice as LabHardwareTarget['voice'] };
  if (snapshot.voice?.mode !== target.mode || snapshot.voice.voice !== target.voice)
    return reject('Model nebo hlas kufru se liší od neměnného testovacího snímku. Změna vyžaduje výslovnou akci.', target, snapshot.voice?.canConfigure === true);
  if (!snapshot.voice.canStart || snapshot.busy || snapshot.phase !== 'ready') return reject('Kufr nepotvrdil připravenost pro nový hlasový test.', target);
  return { target, reason: '', ready: true, canConfigure: false };
}
export function sameHardwareConnection(snapshot: DeviceSnapshot | null, target: Pick<LabHardwareTarget, 'deviceId' | 'bootId' | 'connectionEpoch'>, connectionEpoch: number): boolean {
  return snapshot?.source === 'native-device' && snapshot.phase !== 'disconnected'
    && snapshot.diagnostics?.deviceId === target.deviceId && snapshot.diagnostics.bootId === target.bootId && target.connectionEpoch === connectionEpoch;
}

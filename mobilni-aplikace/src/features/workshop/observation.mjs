// Reuse the validated DV02 parsers/projection. Their legacy DOM mount is never called.
import * as twinModule from '../../../../mechanika/final/twin_core.js';
import * as timelineModule from '../../../../mechanika/final/timeline_core.js';
const runtime = typeof window === 'undefined' ? globalThis : window;
export const twin = twinModule.default || runtime.TwinObservation;
export const timeline = timelineModule.default || runtime.TimelineCore;

export function mappingCompatible(model, snapshot) {
  const mapping = model.twinManifest;
  return !!mapping?.revision && !!mapping.hardwareProfile &&
    snapshot?.source?.cadManifestRevision === mapping.revision &&
    snapshot?.source?.hardwareProfile === mapping.hardwareProfile;
}
/** Unknown/stale data is never drawn as measured physical position. */
export function projectObservation(model, envelope, now = performance.now(), acceptedAt = now, replay = false) {
  twin.validateSnapshot(envelope.snapshot);
  const projection = new twin.Projection(model.twinManifest);
  projection.select(envelope.snapshot.source.instanceId); projection.accept(envelope, acceptedAt);
  const compatible = mappingCompatible(model, envelope.snapshot);
  const fresh = replay || projection.status(now) === 'online';
  const canMap = compatible && fresh;
  const angles = Array.from({ length: model.mechanism.count }, (_, index) => canMap ? projection.angle(index + 1) : null);
  const uncertain = model.meshes.filter(mesh => {
    const slot = Number(mesh.name.match(/_([1-6])$/)?.[1]);
    return slot && (angles[slot - 1] === null || ['moving', 'unknown'].includes(projection.estimates[slot - 1]));
  }).map(mesh => mesh.id);
  const ledColors = {};
  if (canMap && Array.isArray(envelope.snapshot.reported.ledGrb)) {
    for (const entry of model.twinManifest.slots || []) {
      const { ledStart, ledCount } = entry.channels || {}, grb = envelope.snapshot.reported.ledGrb;
      if (Number.isInteger(ledStart) && ledCount > 0 && (ledStart + ledCount) * 3 <= grb.length) {
        const sum = [0, 0, 0];
        for (let pixel = ledStart; pixel < ledStart + ledCount; pixel++) {
          sum[0] += grb[pixel * 3 + 1]; sum[1] += grb[pixel * 3]; sum[2] += grb[pixel * 3 + 2];
        }
        ledColors[entry.components?.led] = sum.map(value => value / (255 * ledCount));
      }
    }
  }
  const reported = twin.reportedOutputs(canMap ? model.twinManifest : null, envelope.snapshot.reported);
  const outputs = canMap ? Object.fromEntries([...reported.vibrationMeshes.map(name => [name, 'vibration-commanded']), ...reported.audioMeshes.map(name => [name, reported.audioKind])]) : {};
  return { angles, uncertain, ledColors, outputs, audioEvidence: reported.audioEvidence, compatible, fresh, status: replay ? 'replay' : projection.status(now), compatibility: projection.compatibility(), estimates: projection.estimates };
}

export function validateObserverBase(value, origin = globalThis.location?.origin) {
  const url = new URL(value || origin);
  if (url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname))) throw new Error('Pozorovatel vyžaduje HTTPS nebo místní simulátor.');
  if (url.username || url.password || url.search || url.hash || !['/', ''].includes(url.pathname)) throw new Error('Zadejte pouze origin serveru bez tokenu nebo cesty.');
  return url.origin;
}

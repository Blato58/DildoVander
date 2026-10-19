/* Runtime observation is independent of the illustrative effect-plan player. */
(function(root) {
  'use strict';
  const MAX_BYTES = 8 * 1024 * 1024, MAX_SNAPSHOTS = 2048, MAX_EVENTS = 4096;
  const UINT64_MAX = 18446744073709551615n;
  async function httpFailure(response, path) {
    let code = '';
    try {
      const body = await response.json();
      if (typeof body?.code === 'string' && /^[a-z][a-z0-9_-]{0,95}$/.test(body.code)) code = body.code;
    } catch (_) { /* A proxy may return an empty or non-JSON error body. */ }
    const route = path.split('?')[0].split('/').slice(-2).join('/');
    const error = new Error(`Twin HTTP ${response.status} (${route}): ${code || (response.status === 401 || response.status === 403 ? 'session/oprávnění' : 'požadavek odmítnut')}`);
    error.status = response.status; error.code = code;
    return error;
  }
  function decimal(value, label) {
    if (typeof value !== 'string' || !/^(0|[1-9][0-9]*)$/.test(value) || BigInt(value) > UINT64_MAX) {
      throw new Error(label + ': očekávána přesná uint64 decimal string');
    }
    return BigInt(value);
  }
  function validateSnapshot(snapshot) {
    if (!snapshot || snapshot.schemaVersion !== 1 || !snapshot.source || !snapshot.reported ||
        !['simulation', 'live'].includes(snapshot.source.mode) ||
        typeof snapshot.source.instanceId !== 'string' || typeof snapshot.source.bootId !== 'string') {
      throw new Error('Nepodporovaný Digital Twin snapshot');
    }
    for (const name of ['sequence', 'timeMs', 'revision']) decimal(snapshot[name], name);
    if (snapshot.reported.physicalVerification !== 'unavailable') throw new Error('Digital Twin v1 neobsahuje fyzické měření');
    if (snapshot.reported.generation !== undefined) decimal(snapshot.reported.generation, 'generation');
    if (snapshot.transport?.droppedSnapshots !== undefined) decimal(snapshot.transport.droppedSnapshots, 'droppedSnapshots');
    if (snapshot.source.mode === 'live' && snapshot.model !== null) throw new Error('Live nesmí obsahovat virtuální model');
    if (snapshot.model !== null && snapshot.model !== undefined) {
      if (snapshot.model.evidence !== 'simulated' || !Array.isArray(snapshot.model.slots) || snapshot.model.slots.length > 6) {
        throw new Error('Neplatná evidence modelu');
      }
      const seen = new Set();
      for (const entry of snapshot.model.slots) {
        if (!Number.isInteger(entry.slot) || entry.slot < 1 || entry.slot > 6 || seen.has(entry.slot) ||
            !Number.isFinite(entry.position) || entry.position < 0 || entry.position > 1) throw new Error('Neplatná modelová poloha');
        seen.add(entry.slot);
      }
    }
    const events = snapshot.trace?.events || [];
    if (!Array.isArray(events) || events.length > MAX_EVENTS) throw new Error('Příliš mnoho trace událostí');
    for (const event of events) {
      // Existing ExportPresentationTrace uses a uint32 numeric event sequence.
      // Preserve that native type; uint64 strings remain exact and never Number-coerced.
      if (event.sequence !== undefined && typeof event.sequence === 'number') {
        if (!Number.isSafeInteger(event.sequence) || event.sequence < 0) throw new Error('Neplatná native trace sequence');
      } else if (event.sequence !== undefined) decimal(event.sequence, 'trace.sequence');
      for (const name of ['timeMs', 'dueAt', 'acceptedAt', 'commandedAt', 'endedAt', 'measuredAt']) {
        if (event[name] !== undefined && event[name] !== null) decimal(event[name], 'trace.' + name);
      }
    }
    return snapshot;
  }
  function envelope(value) {
    if (!value || !value.snapshot || !Number.isFinite(value.ageMs) || value.ageMs < 0 ||
        typeof value.receivedAtUtc !== 'string' || !Number.isFinite(Date.parse(value.receivedAtUtc))) throw new Error('Chybí serverový čas přijetí');
    validateSnapshot(value.snapshot);
    return value;
  }
  function traceSummary(trace, manifest) {
    const events = trace?.events || [], gaps = [];
    let previous = null;
    for (const event of events) {
      if (event.sequence === undefined) continue;
      const sequence = BigInt(event.sequence);
      if (previous !== null && sequence > previous + 1n) gaps.push(`${previous+1n}–${sequence-1n}`);
      previous = sequence;
    }
    const mismatches = manifest ? ['bundleHash', 'sourceHash', 'compiledHash'].filter(key => trace?.[key] && trace[key] !== manifest[key]) : [];
    return `${events.length} událostí · native droppedEvents ${trace?.droppedEvents ?? 'unknown'} · gaps ${gaps.join(', ') || 'none/unknown'} · ${mismatches.length ? 'NESHODA bundle: '+mismatches.join(', ') : trace?.bundleHash ? 'bundle identity dostupná' : 'bundle identity unknown'}`;
  }
  function parseRecording(text) {
    if (typeof text !== 'string' || new TextEncoder().encode(text).length > MAX_BYTES) throw new Error('Záznam přesahuje 8 MiB');
    const value = JSON.parse(text);
    if (value.schemaVersion !== 1 || !Array.isArray(value.snapshots) || value.snapshots.length === 0 || value.snapshots.length > MAX_SNAPSHOTS) {
      throw new Error('Nepodporovaný nebo prázdný záznam');
    }
    const checker = new Projection();
    envelope(value.snapshots[0]);
    checker.select(value.snapshots[0].snapshot.source.instanceId);
    for (const entry of value.snapshots) {
      envelope(entry);
      if (!checker.accept(entry, 0)) throw new Error('Záznam mění zdroj nebo vrací sequence/clock');
    }
    return value;
  }
  function ageStatus(ageMs) { return ageMs > 10000 ? 'offline' : ageMs > 2000 ? 'stale' : 'online'; }
  function reportedOutputs(manifest, reported = {}) {
    const vibrationMeshes = [];
    if (reported.outputsEnabled === true && Number.isInteger(reported.vibrationMask) && reported.vibrationMask >= 0 && reported.vibrationMask <= 63) {
      for (const slot of manifest?.slots || []) {
        const index = slot.channels?.vibrationPca - 6;
        if (index >= 0 && index < 6 && (reported.vibrationMask & (1 << index)) && slot.components?.u059) {
          vibrationMeshes.push(slot.components.u059);
        }
      }
    }
    const audioEvidence = reported.audio || 'unavailable';
    const audioKind = /voice-busy/.test(audioEvidence) ? 'voice-busy' : /cancel/.test(audioEvidence) ? 'cancel' :
      /enqueue/.test(audioEvidence) ? 'enqueue' : /drained/.test(audioEvidence) ? 'drained' : 'unknown';
    const audioMeshes = ['enqueue','cancel','voice-busy'].includes(audioKind) ?
      [...(manifest?.audio?.speakers || []), ...(manifest?.audio?.amplifiers || [])] : [];
    return {vibrationMeshes,audioMeshes,audioEvidence,audioKind,outputsEnabled:reported.outputsEnabled ?? null};
  }
  class Projection {
    constructor(manifest) { this.manifest = manifest; this.select(null); }
    select(instanceId) {
      this.instanceId = instanceId; this.current = null; this.epoch = null;
      this.retiredBoots = new Set(); this.positions = Array(6).fill(null);
      this.estimates = Array(6).fill('unknown'); this.acceptedAt = 0; this.lastContent = null;
    }
    accept(value, now) {
      envelope(value);
      const snapshot = value.snapshot;
      if (snapshot.source.instanceId !== this.instanceId || this.retiredBoots.has(snapshot.source.bootId)) return false;
      if (this.current && (snapshot.source.mode !== this.current.snapshot.source.mode ||
          snapshot.source.deviceId !== this.current.snapshot.source.deviceId)) return false;
      const epoch = snapshot.source.bootId;
      if (this.epoch === epoch && this.current) {
        const previous = this.current.snapshot;
        if (decimal(snapshot.sequence, 'sequence') < decimal(previous.sequence, 'sequence') ||
            decimal(snapshot.timeMs, 'timeMs') < decimal(previous.timeMs, 'timeMs') ||
            decimal(snapshot.revision, 'revision') < decimal(previous.revision, 'revision')) return false;
        if (snapshot.sequence === previous.sequence && JSON.stringify(snapshot) !== this.lastContent) return false;
      } else {
        if (this.epoch !== null) this.retiredBoots.add(this.epoch);
        this.positions.fill(null); this.estimates.fill('unknown'); this.epoch = epoch;
      }
      this.current = value; this.acceptedAt = now; this.lastContent = JSON.stringify(snapshot);
      this.estimates.fill('unknown');
      for (const servo of snapshot.reported.servo || []) {
        const index = servo.slot - 1;
        if (index < 0 || index >= 6) continue;
        this.estimates[index] = servo.estimate || 'unknown';
        // The live driver reports calibrated endpoints, never measured position.
        if (servo.estimate === 'rest') this.positions[index] = 0;
        if (servo.estimate === 'raised') this.positions[index] = 1;
      }
      if (snapshot.source.mode === 'simulation' && snapshot.model) {
        for (const entry of snapshot.model.slots) {
          this.positions[entry.slot - 1] = entry.position;
          this.estimates[entry.slot - 1] = entry.jammed ? 'simulated jam' : 'simulated';
        }
      }
      return true;
    }
    status(now) {
      if (!this.current) return 'waiting';
      const aged = ageStatus(this.current.ageMs + Math.max(0, now - this.acceptedAt));
      if (this.current.status === 'offline') return 'offline';
      if (this.current.status === 'stale' && aged === 'online') return 'stale';
      return aged;
    }
    angle(slot) {
      const position = this.positions[slot - 1], samples = this.manifest?.kinematics?.samples;
      if (position === null || !samples?.length) return null;
      const lift = position * this.manifest.kinematics.liftMaxMm;
      if (position === 0) return this.manifest.kinematics.angleMinDegrees;
      if (position === 1) return this.manifest.kinematics.angleMaxDegrees;
      const upper = samples.findIndex(sample => sample.liftMm >= lift);
      if (upper <= 0) return samples[0].angleDegrees;
      const low = samples[upper - 1], high = samples[upper];
      const fraction = (lift - low.liftMm) / (high.liftMm - low.liftMm);
      return low.angleDegrees + fraction * (high.angleDegrees - low.angleDegrees);
    }
    compatibility() {
      const source = this.current?.snapshot.source;
      if (!source) return 'Čeká na plný snapshot; CAD polohy unknown.';
      if (!this.manifest) return 'Chybí CAD manifest; runtime nelze spárovat.';
      if (!source.cadManifestRevision) return 'Chybí CAD revision zdroje; kompatibilita není ověřena.';
      if (source.cadManifestRevision !== this.manifest.revision || source.hardwareProfile !== this.manifest.hardwareProfile) {
        return 'NESHODA CAD revision/profilu; projekce je pouze orientační.';
      }
      return 'CAD revision odpovídá; polohy zůstávají modelem nebo kalibrovaným odhadem.';
    }
  }

  const api = {Projection, decimal, validateSnapshot, parseRecording, traceSummary, reportedOutputs, ageStatus, httpFailure, MAX_BYTES, MAX_SNAPSHOTS};
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.TwinObservation = api;
})(typeof window === "undefined" ? globalThis : window);

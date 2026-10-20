/* Read-only DV02 projection. Scheduling, safety and movement remain in the C++ core. */
(function (root) {
  'use strict';
  const MAX_BYTES = 1024 * 1024, MAX_EVENTS = 4096;
  const topFields = new Set('schemaVersion source coreVersion bundleHash sourceHash compiledHash sceneId sceneVersion sceneHash variant runtimeProfile calibrationId bootId operationId generation deviceRevision slot quality state evidence firstSequence lastSequence droppedEvents events observerPublishedAtMs observerSkippedSnapshots observerStale track audioGeneration audioPositionMs beatIndex'.split(' '));
  const eventFields = new Set('sequence kind phaseIndex phaseId clockDomain timeMs dueAt latenessMs evidence reason slot mask red green blue level vibration toneHz acceptedAt commandedAt endedAt measuredAt'.split(' '));
  const kinds = new Set('accepted commanded phase-ended skipped completed fault audio-requested'.split(' '));
  const evidenceKinds = new Set(['commanded', 'timed-complete', 'unknown', 'accepted', 'requested']);
  function fail(message) { throw new Error(message); }
  function object(value, fields) {
    if (!value || typeof value !== 'object' || Array.isArray(value)) fail('Expected an object');
    for (const key of Object.keys(value)) if (!fields.has(key)) fail('Unsupported field: ' + key);
  }
  function text(value, name, max = 160) {
    if (typeof value !== 'string' || value.length > max || /[\u0000-\u001f]/.test(value)) fail('Invalid ' + name);
    return value;
  }
  function integer(value, name, max = Number.MAX_SAFE_INTEGER) {
    if (!Number.isSafeInteger(value) || value < 0 || value > max) fail('Invalid ' + name);
    return value;
  }
  function time(value, name) {
    if (typeof value !== 'string' || !/^\d{1,16}$/.test(value)) fail('Invalid ' + name);
    return integer(Number(value), name);
  }
  function parseTrace(input) {
    if (typeof input !== 'string' || input.length > MAX_BYTES || new TextEncoder().encode(input).length > MAX_BYTES) fail('Trace exceeds 1 MiB');
    let trace;
    try { trace = JSON.parse(input); } catch { fail('Invalid JSON'); }
    object(trace, topFields);
    if (trace.schemaVersion !== 1 || trace.source !== 'firmware-core') fail('Unsupported trace schema/source');
    for (const key of ['coreVersion', 'bundleHash', 'sourceHash', 'compiledHash', 'sceneId', 'runtimeProfile', 'calibrationId', 'operationId', 'generation', 'deviceRevision', 'quality', 'state', 'evidence']) text(trace[key], key);
    if (trace.bootId !== undefined) text(trace.bootId, 'bootId');
    for (const key of ['sceneHash', 'variant']) if (trace[key] !== undefined) text(trace[key], key);
    if (trace.track !== undefined) text(trace.track, 'track');
    if (trace.beatIndex !== undefined) text(trace.beatIndex, 'beatIndex');
    for (const key of ['audioGeneration', 'audioPositionMs']) if (trace[key] !== undefined) integer(trace[key], key);
    if (trace.observerPublishedAtMs !== undefined) time(trace.observerPublishedAtMs, 'observerPublishedAtMs');
    if (trace.observerSkippedSnapshots !== undefined) integer(trace.observerSkippedSnapshots, 'observerSkippedSnapshots');
    if (trace.observerStale !== undefined && typeof trace.observerStale !== 'boolean') fail('Invalid observerStale');
    for (const key of ['sceneVersion', 'slot', 'firstSequence', 'lastSequence', 'droppedEvents']) integer(trace[key], key);
    if (trace.slot > 6) fail('Invalid selected slot');
    if (!['full', 'reduced', 'none'].includes(trace.quality) || !['idle', 'running', 'completed', 'fault', 'cancelled', 'skipped'].includes(trace.state)) fail('Unsupported snapshot state/quality');
    if (!evidenceKinds.has(trace.evidence)) fail('Unsupported evidence');
    if (!Array.isArray(trace.events) || trace.events.length > MAX_EVENTS) fail('Trace exceeds 4096 events');
    let previous = 0, previousTime = 0;
    const gaps = trace.firstSequence > 1 ? [[1, trace.firstSequence - 1]] : [];
    for (const event of trace.events) {
      object(event, eventFields);
      integer(event.sequence, 'sequence');
      if (event.sequence <= previous) fail('Event sequences must increase');
      if (previous && event.sequence > previous + 1) gaps.push([previous + 1, event.sequence - 1]);
      previous = event.sequence;
      if (!kinds.has(event.kind)) fail('Unsupported event kind');
      text(event.phaseId, 'phaseId'); text(event.reason, 'reason', 240);
      if (!['device-monotonic', 'audio-media'].includes(event.clockDomain)) fail('Unsupported clock domain; no cross-clock subtraction');
      if (event.clockDomain !== trace.events[0].clockDomain) fail('Mixed clock domains cannot be correlated');
      if (event.clockDomain === 'audio-media' && (trace.sceneId !== 'dance' || trace.audioGeneration === undefined)) fail('Audio clock requires Dance generation identity');
      if (!evidenceKinds.has(event.evidence)) fail('Unsupported event evidence');
      const eventTime = time(event.timeMs, 'timeMs');
      if (eventTime < previousTime) fail('Device-monotonic time moved backwards');
      previousTime = eventTime;
      for (const key of ['dueAt', 'acceptedAt', 'commandedAt', 'endedAt', 'measuredAt']) if (event[key] !== undefined) time(event[key], key);
      if (event.vibration !== undefined && typeof event.vibration !== 'boolean') fail('Invalid vibration');
      if (event.level !== undefined) integer(event.level, 'level', 64);
      for (const key of ['phaseIndex', 'latenessMs', 'slot', 'mask', 'red', 'green', 'blue', 'toneHz']) if (event[key] !== undefined) integer(event[key], key, key === 'toneHz' ? 24000 : key === 'latenessMs' ? Number.MAX_SAFE_INTEGER : 255);
      if (event.slot !== undefined && event.slot > 6) fail('Invalid event slot');
    }
    if (trace.events.length && (trace.events[0].sequence !== trace.firstSequence || previous !== trace.lastSequence)) fail('Trace sequence bounds disagree');
    if (!trace.events.length && (trace.firstSequence !== 0 || trace.lastSequence !== 0)) fail('Empty trace sequence bounds disagree');
    return { ...trace, gaps, incomplete: trace.droppedEvents > 0 || gaps.length > 0 || trace.firstSequence > 1 };
  }
  function compatibility(trace, manifest, plan) {
    const mismatches = ['bundleHash', 'sourceHash', 'compiledHash'].filter(key => trace[key] !== manifest?.[key]);
    if (manifest?.semanticsVersion !== undefined && trace.coreVersion !== manifest.semanticsVersion) mismatches.push('coreVersion');
    const scenes = (plan?.scenes || []).filter(scene => scene.sceneId === trace.sceneId);
    const matchingVersions = scenes.filter(scene => scene.sceneVersion === trace.sceneVersion);
    if (scenes.length && !matchingVersions.length) mismatches.push('sceneVersion');
    let candidates = matchingVersions;
    if (trace.variant !== undefined) {
      candidates = matchingVersions.filter(scene => scene.variant === trace.variant);
      if (matchingVersions.length && !candidates.length) mismatches.push('variant');
    }
    if (trace.sceneHash !== undefined) {
      const matchingHashes = candidates.filter(scene => scene.sceneHash === trace.sceneHash);
      if (candidates.length && !matchingHashes.length) mismatches.push('sceneHash');
      candidates = matchingHashes;
    }
    if (candidates.length && !candidates.some(scene => trace.events.every(event => scene.phases.some(phase => phase.id === event.phaseId)))) mismatches.push('phaseIds');
    return mismatches;
  }
  function traceWarnings(trace, plan, manifest) {
    const messages = [trace.incomplete
      ? `NEÚPLNÝ ZÁZNAM: drop ${trace.droppedEvents}; gaps ${trace.gaps.map(gap => gap.join('–')).join(', ') || 'unknown'}`
      : 'Bez hlášených mezer; absence události není měření.'];
    messages.push(trace.observerPublishedAtMs === undefined ? 'Observer aktuálnost unknown.'
      : `Observer publikace ${trace.observerPublishedAtMs} ms, vynechané snapshoty ${trace.observerSkippedSnapshots ?? 'unknown'}, ${trace.observerStale ? 'STALE: aktuální stav neznámý' : 'historický export; aktuálnost zařízení neověřena'}.`);
    const mismatches = compatibility(trace, manifest, plan);
    if (mismatches.length) messages.push('NESOULAD BUNDLE: ' + mismatches.join(', ') + '. Plán nelze přiřadit jako důkaz tohoto běhu.');
    else if (!(plan?.scenes || []).some(scene => scene.sceneId === trace.sceneId)) messages.push('Pro tuto scénu není kompilovaný plán; zobrazena pouze core stopa.');
    else if (trace.sceneHash === undefined || trace.variant === undefined) messages.push('ČÁSTEČNÁ IDENTITA: starší export nemá sceneHash nebo variant. Bundle a dostupné phase IDs umožňují jen částečnou kontrolu; chybějící identita zůstává unknown.');
    else messages.push('Identita plánu odpovídá exportu.');
    if (trace.sceneId === 'dance') messages.push(`Audio generace ${trace.audioGeneration ?? 'unknown'}, pozice ${trace.audioPositionMs ?? 'unknown'} ms, beat ${trace.beatIndex ?? 'unknown'}, track ${trace.track || 'unknown'}. Audio-media není akustické měření.`);
    messages.push('Poloha unknown; commanded je řídicí požadavek, timed-complete je modelový konec.');
    return messages.join(' ');
  }
  function meaningful(event) {
    return JSON.stringify([event.kind, event.phaseId, event.timeMs, event.dueAt, event.slot, event.mask, event.red, event.green, event.blue, event.level, event.vibration, event.toneHz, event.evidence, event.reason]);
  }
  function diffTraces(baseline, candidate) {
    const metadata = ['coreVersion', 'bundleHash', 'sceneId', 'sceneVersion', 'sceneHash', 'variant', 'runtimeProfile', 'calibrationId', 'slot', 'quality', 'state', 'track', 'audioGeneration'];
    const changes = metadata.filter(key => baseline[key] !== candidate[key]).map(key => key + ': ' + baseline[key] + ' → ' + candidate[key]);
    if (baseline.events[0]?.clockDomain !== candidate.events[0]?.clockDomain) {
      changes.push('Clock domains differ; event times cannot be correlated.');
      return {changes, incomplete:baseline.incomplete || candidate.incomplete, unavailable:true};
    }
    // Compare relative device-monotonic times only within each recorded run.
    const normalize = trace => {
      const origin = trace.events[0] ? Number(trace.events[0].timeMs) : 0;
      return trace.events.map(event => ({...event, timeMs:String(Number(event.timeMs) - origin), dueAt:event.dueAt === undefined ? undefined : String(Number(event.dueAt) - origin)}));
    };
    const left = normalize(baseline), right = normalize(candidate);
    for (let index = 0; index < Math.max(left.length, right.length); index++) {
      if (meaningful(left[index] || {}) !== meaningful(right[index] || {})) {
        const describe = event => event ? `${event.kind} ${event.phaseId}, +${event.timeMs} ms (due ${event.dueAt ?? '?'}), slot ${event.slot ?? '?'}, mask ${event.mask ?? '?'}, RGB input ${event.red ?? '?'}/${event.green ?? '?'}/${event.blue ?? '?'}, level ${event.level ?? 'unknown'}, pulse ${event.vibration ?? '?'}, tone ${event.toneHz ?? '?'}, ${event.evidence}, ${event.reason || 'no reason'}` : 'missing event';
        changes.push('Event ' + (index + 1) + ': ' + describe(left[index]) + ' → ' + describe(right[index]));
      }
    }
    return {changes, incomplete:baseline.incomplete || candidate.incomplete};
  }
  const api = {parseTrace, compatibility, diffTraces, traceWarnings, MAX_BYTES, MAX_EVENTS};
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.TimelineCore = api;
})(typeof window === "undefined" ? globalThis : window);

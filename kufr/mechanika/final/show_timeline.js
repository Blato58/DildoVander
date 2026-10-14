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
  function mount(document, plan, manifest) {
    const get = id => document.getElementById(id);
    if (!get('show-inspector')) return;
    let baseline, candidate, position = 0, playing = false, previousTime = 0, playbackGeneration = 0;
    const node = (tag, value) => { const element = document.createElement(tag); if (value !== undefined) element.textContent = value; return element; };
    const line = value => get('show-events').append(node('li', value));
    function stop() { playing = false; playbackGeneration++; get('show-play').textContent = 'Přehrát záznam'; }
    function render() {
      const trace = candidate || baseline;
      const scene = plan?.scenes?.[Number(get('show-scene').value) || 0];
      const track = get('show-track').value || 'all';
      get('show-events').replaceChildren();
      get('show-mode').textContent = trace ? 'REPLAY · HISTORICKÝ ZÁZNAM · READ-ONLY' : 'PLÁN · ILUSTRAČNÍ KALIBRACE · READ-ONLY';
      get('show-identity').textContent = trace
        ? `${trace.sceneId || 'unknown'} v${trace.sceneVersion} | variant ${trace.variant || 'unknown'} | scene ${trace.sceneHash || 'unknown'} | bundle ${trace.bundleHash} | core ${trace.coreVersion} | profil ${trace.runtimeProfile} | kalibrace ${trace.calibrationId || 'unknown'} | boot ${trace.bootId || 'unknown'} | operace ${trace.operationId || 'unknown'} / generace ${trace.generation} / revize ${trace.deviceRevision} | owner snapshot ${trace.state} / ${trace.evidence}`
        : `${scene?.sceneId || 'unknown'} v${scene?.sceneVersion ?? '?'} | scene ${scene?.sceneHash || 'unknown'} | bundle ${manifest?.bundleHash || 'unknown'} | ${plan?.semanticsVersion || 'unknown'} | kalibrace unknown`;
      const warning = trace ? traceWarnings(trace, plan, manifest)
        : 'Časy jsou plán a ilustrační odhad kalibrovaného pohybu. Nejde o telemetrii ani důkaz pohybu. Deadline: ' + (scene?.maxDurationMs ?? 'unknown') + ' ms.';
      if (get('show-warning').textContent !== warning) get('show-warning').textContent = warning;
      if (trace) {
        const start = Number(trace.events[0]?.timeMs || 0), end = Number(trace.events.at(-1)?.timeMs || start) - start;
        get('show-seek').max = String(end); position = Math.min(position, end); get('show-seek').value = String(position);
        get('show-time').textContent = `${Math.floor(position)} / ${end} ms · ${trace.events[0]?.clockDomain || 'unknown'} (relativní k první události)`;
        if (!trace.events.length) line('Prázdná stopa: dostupný je pouze snapshot; průběh ani poloha nejsou doložené.');
        for (const event of trace.events) {
          const relative = Number(event.timeMs) - start;
          if (relative > position) continue;
          const tracks = event.kind === 'commanded' ? ['motion', 'led', 'vibration'] : event.kind === 'audio-requested' || event.reason.startsWith('audio') || event.reason === 'playback-unverified' ? ['audio'] : ['owner', 'motion'];
          if (track !== 'all' && !tracks.includes(track)) continue;
          line(`${event.sequence} · ${relative} ms [${event.clockDomain}] · ${event.kind} / ${event.phaseId} · dueAt ${event.dueAt ?? 'unknown'}, acceptedAt ${event.acceptedAt ?? 'unknown'}, commandedAt ${event.commandedAt ?? 'unknown'}, endedAt ${event.endedAt ?? 'unknown'}, measuredAt ${event.measuredAt ?? 'unknown'} · ${event.evidence} · ${event.reason || 'bez důvodu'} · slot ${event.slot ?? '?'} / mask ${event.mask ?? '?'} / RGB vstup ${event.red ?? '?'},${event.green ?? '?'},${event.blue ?? '?'} / level ${event.level ?? 'unknown'} / vibrace ${event.vibration ?? '?'} / tón ${event.toneHz ?? '?'}`);
        }
      } else {
        get('show-time').textContent = 'Plán: relativní ilustrační čas, žádné porovnání wall/audio clock';
        for (const phase of scene?.phases || []) {
          line(`${phase.id} · ${phase.label || phase.id} · ${phase.illustrativeStartMs}–${phase.illustrativeStartMs + phase.illustrativeDurationMs} ms · ${JSON.stringify(phase.completion)}`);
          for (const cue of phase.cues) {
            const cueTrack = cue.kind.startsWith('led') ? 'led' : cue.kind.includes('vibr') ? 'vibration' : cue.kind.includes('tone') || cue.kind.includes('audio') ? 'audio' : 'motion';
            if (track !== 'all' && track !== cueTrack) continue;
            const item = node('li'), details = node('details'), summary = node('summary', `${cue.kind} · ${cue.id} · plán ${phase.illustrativeStartMs + (cue.timing?.offsetMs || 0)} ms`);
            details.append(summary, node('pre', JSON.stringify(cue, null, 2))); item.append(details); get('show-events').append(item);
          }
        }
      }
      get('show-play').disabled = !trace; get('show-seek').disabled = !trace;
      get('show-diff').replaceChildren();
      if (baseline && candidate) {
        const result = diffTraces(baseline, candidate);
        get('show-diff').append(node('p', result.unavailable ? 'Časové domény se liší; události nelze časově porovnat.' : result.incomplete ? 'Diff je částečný: alespoň jeden záznam má mezery.' : 'Diff významných řídicích událostí (relativní časy každého běhu).'));
        const list = node('ol');
        for (const change of result.changes) list.append(node('li', change));
        if (!result.changes.length) list.append(node('li', 'Žádný rozdíl v dostupné stopě; nejde o fyzickou akceptaci.'));
        get('show-diff').append(list);
      } else get('show-diff').append(node('p', 'Pro porovnání načtěte baseline i kandidáta.'));
    }
    async function loadFile(input, role) {
      try {
        const file = input.files?.[0]; if (!file) return;
        if (file.size > MAX_BYTES) fail('Trace exceeds 1 MiB');
        const trace = parseTrace(await file.text());
        if (role === 'baseline') baseline = trace; else candidate = trace;
        stop(); position = Number(trace.events.at(-1)?.timeMs || 0) - Number(trace.events[0]?.timeMs || 0);
        get('show-error').textContent = ''; render();
      } catch (error) { get('show-error').textContent = 'Import odmítnut: ' + error.message; }
      finally { input.value = ''; }
    }
    for (const [index, scene] of (plan?.scenes || []).entries()) { const option = node('option', `${scene.sceneId} / ${scene.variant}`); option.value = String(index); get('show-scene').append(option); }
    get('show-baseline').onchange = event => loadFile(event.target, 'baseline');
    get('show-candidate').onchange = event => loadFile(event.target, 'candidate');
    get('show-scene').onchange = render;
    get('show-track').onchange = render;
    get('show-reset').onclick = () => { stop(); baseline = candidate = undefined; position = 0; render(); };
    get('show-seek').oninput = () => { stop(); position = Number(get('show-seek').value); render(); };
    function frame(timestamp, generation) {
      if (!playing || generation !== playbackGeneration) return;
      position = Math.min(Number(get('show-seek').max), position + Math.max(0, timestamp - previousTime) * Number(get('show-speed').value));
      previousTime = timestamp; render();
      if (position >= Number(get('show-seek').max)) stop(); else root.requestAnimationFrame(timestamp => frame(timestamp, generation));
    }
    get('show-play').onclick = () => {
      if (playing) { stop(); return; }
      if (position >= Number(get('show-seek').max)) position = 0;
      playing = true;
      const generation = ++playbackGeneration;
      get('show-play').textContent = 'Pozastavit záznam';
      previousTime = root.performance.now();
      root.requestAnimationFrame(timestamp => frame(timestamp, generation));
    };
    function close() { stop(); get('show-inspector').hidden = true; get('show-open').setAttribute('aria-expanded', 'false'); get('show-open').focus(); }
    get('show-open').onclick = () => { get('show-inspector').hidden = false; get('show-open').setAttribute('aria-expanded', 'true'); get('show-close').focus(); };
    get('show-close').onclick = close;
    get('show-inspector').addEventListener('keydown', event => {
      if (event.key === 'Escape') close();
      if (event.key !== 'Tab') return;
      const controls = Array.from(get('show-inspector').querySelectorAll('button:not([disabled]),select,input:not([disabled]),summary,[tabindex="0"]'));
      const first = controls[0], last = controls.at(-1);
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    });
    render();
  }
  const api = { parseTrace, compatibility, diffTraces, mount, MAX_BYTES, MAX_EVENTS };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.ShowTimeline = api;
})(typeof window === 'undefined' ? globalThis : window);

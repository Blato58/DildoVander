/* Legacy distribution adapter. Canonical logic lives in timeline_core.js. */
(function(root) {
  "use strict";
  const {parseTrace, compatibility, diffTraces, traceWarnings, MAX_BYTES, MAX_EVENTS} = typeof module !== "undefined" && module.exports ? require("./timeline_core.js") : root.TimelineCore;
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
        if (file.size > MAX_BYTES) throw new Error('Trace exceeds 1 MiB');
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
    const inspector = root.VapourUI.bindDialog(get('show-inspector'), {
      initialFocus: get('show-close'),
      onClose: () => { stop(); get('show-open').setAttribute('aria-expanded', 'false'); },
    });
    get('show-open').onclick = () => {
      inspector.open(get('show-open'));
      get('show-open').setAttribute('aria-expanded', 'true');
    };
    get('show-close').onclick = () => inspector.close();
    render();
  }
  const api = { parseTrace, compatibility, diffTraces, mount, MAX_BYTES, MAX_EVENTS };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.ShowTimeline = api;
})(typeof window === 'undefined' ? globalThis : window);

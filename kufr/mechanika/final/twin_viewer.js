/* Runtime observation is independent of the illustrative effect-plan player. */
(function(root) {
  'use strict';
  const MAX_BYTES = 8 * 1024 * 1024, MAX_SNAPSHOTS = 2048, MAX_EVENTS = 4096;
  const UINT64_MAX = 18446744073709551615n;
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

  function mount(document, model, hooks) {
    const projection = new Projection(model.twinManifest);
    let browserMedia = null;
    let active = false, replay = false, selectedSlot = null, selectedMesh = null;
    let role = 'observer', base = root.location?.origin || '', timer = null, generation = 0;
    const sourceProfiles = new Map();
    const sourceModes = new Map();
    let selectedProfile = 'unknown', selectedMode = 'unknown', commandNotice = '';
    let recording = [], recordingBytes = 0, recordingDrops = 0, currentIndex = 0;
    let replayPlaying = false, replayFrame = null, replayStarted = 0, replayOffset = 0;
    let pending = false, pendingStop = false, failure = '', remoteCapabilities = [], lastTraceKey = '';
    const offline = root.location?.protocol === 'file:';
    const privatePath = /^\/twin(?:\/|$)/.test(root.location?.pathname || '');
    const local = !offline && !/^\/workshop(?:\/|$)/.test(root.location?.pathname || '') &&
      (privatePath || ['localhost', '127.0.0.1', '[::1]'].includes(root.location?.hostname || ''));
    const panel = document.createElement('section');
    panel.id = 'twin-panel'; panel.className = 'twin-panel aw-root';
    panel.setAttribute('aria-label', 'Digital Twin runtime');
    panel.innerHTML = `<details id="twin-details"><summary>Digital Twin · runtime / záznam</summary>
      <p>Plný snapshot core. LED je přijatý GRB frame, servo kalibrovaný odhad; fyzické ověření unavailable.</p>
      <form id="twin-session" ${local ? '' : 'hidden'}><label>Twin server · stejný origin<input id="twin-base" type="url" readonly autocomplete="off"></label><label>Scoped Twin session credential<input id="twin-credential" type="password" autocomplete="off"></label><button>Připojit session</button></form>
      ${offline ? '<p>Offline export podporuje záznam a návrh. Pro session otevřete soukromý /twin na originu serveru; cross-origin cookie není podporována.</p>' : ''}
      <div class="twin-row"><label>Zdroj<select id="twin-source"><option value="">Návrhový náhled</option></select></label><button id="twin-refresh" type="button">Načíst zdroje</button></div>
      <p id="twin-warning"></p><p id="twin-error" role="alert"></p>
      <details><summary>Instance / build / čas / evidence</summary><pre id="twin-metadata"></pre></details>
      <div class="twin-controls" id="twin-controls" hidden>
        <label>Přihrádka<select id="twin-slot">${Array.from({length:6},(_,i)=>`<option>${i+1}</option>`).join('')}</select></label>
        <label>Varianta<select id="twin-variant"><option value="main">main</option><option value="short">short</option></select></label>
        <label class="twin-text-label">Text pro integrační backend<textarea id="twin-text" rows="3" maxlength="1024" autocomplete="off"></textarea></label>
        <button data-twin-action="text">Syntetický textový vstup</button><button data-twin-action="stop" class="aw-button aw-button--danger">Stop</button><button data-twin-action="inspect">Servisní inspekce</button>
        <button data-twin-action="dance">Dance</button><button data-twin-action="danceStop">Dance Stop</button>
        <button data-twin-action="release">Uvolnit ovládání</button>
        <label>Fake clock<select id="twin-speed"><option value="0.25">0.25×</option><option value="1" selected>1×</option><option value="4">4×</option></select></label>
        <button id="twin-pause">Pozastavit clock</button><button id="twin-resume">Pokračovat clock</button><button id="twin-step">Krok 250 ms</button>
        <label>Fault<select id="twin-fault"><option>none</option><option>servo</option><option>led</option><option>journal</option><option>jam</option></select></label><button id="twin-inject">Vložit fault</button>
      </div>
      <p id="twin-capabilities"></p><p id="twin-selection"></p>
      <p id="twin-audio-status"></p>
      <p id="twin-trace-status"></p>
      <ol id="twin-trace" tabindex="0" aria-label="Doložené runtime události"></ol>
      <div class="twin-row"><button id="twin-export">Exportovat záznam</button><label>Import záznamu<input id="twin-import" type="file" accept="application/json,.json"></label></div>
      <div id="twin-replay" hidden><label>Snapshot záznamu<input id="twin-seek" type="range" min="0" max="0" value="0"></label><button id="twin-play">Přehrát záznam</button><label>Rychlost replay<select id="twin-replay-speed"><option>0.25</option><option selected>1</option><option>4</option></select></label><output id="twin-record-time"></output><button id="twin-exit-replay">Zavřít záznam</button></div>
      <p id="twin-recording-status"></p>
    </details>`;
    document.body.append(panel);
    const badge = document.createElement('div'); badge.id = 'twin-badge'; badge.className = 'twin-badge aw-root';
    badge.setAttribute('role', 'status'); document.body.append(badge);
    const get = id => document.getElementById(id);
    get('twin-base').value = base;
    function stopReplay() {
      replayPlaying = false;
      if (replayFrame !== null) root.cancelAnimationFrame(replayFrame);
      replayFrame = null; get('twin-play').textContent = 'Přehrát záznam';
    }
    function stopPolling() { generation++; if (timer !== null) root.clearTimeout(timer); timer = null; }
    function reset() {
      void browserMedia?.invalidate('Zdroj nebo session se mění; lokální média zastavena.');
      stopPolling(); stopReplay(); projection.select(null); selectedSlot = null;
      recording = []; recordingBytes = 0; recordingDrops = 0; currentIndex = 0;
      failure = ''; lastTraceKey = ''; remoteCapabilities = []; selectedProfile = 'unknown'; selectedMode = 'unknown';
      pending = false; pendingStop = false; commandNotice = ''; hooks.clear();
    }
    function display() {
      const snapshot = projection.current?.snapshot, status = replay ? 'replay' : projection.status(root.performance.now());
      const mode = snapshot?.source.mode || selectedMode;
      browserMedia?.sync({active,replay,role,instanceId:projection.instanceId,mode,profile:selectedProfile,
        bootId:snapshot?.source.bootId || null,status});
      const sourceMode = mode === 'live' ? 'ŽIVÝ KUFR · LIVE · ODHAD' : mode === 'simulation' ? 'SIMULACE' : 'ZDROJ';
      const cadState = !snapshot?.source.cadManifestRevision ? 'CAD unknown' : snapshot.source.cadManifestRevision !== model.twinManifest?.revision ? 'CAD NESHODA' : 'CAD match';
      badge.textContent = !active ? 'NÁVRHOVÝ NÁHLED · bez runtime' :
        `${replay ? 'REPLAY · ' : ''}${sourceMode} · ${selectedProfile} · ${replay ? 'HISTORIE' : status.toUpperCase()} · ${snapshot?.source.instanceId || projection.instanceId || 'čekání'} · ${cadState}`;
      badge.dataset.status = status;
      get('twin-warning').textContent = projection.compatibility();
      get('twin-error').textContent = failure;
      get('twin-capabilities').textContent = `Session: ${role}. Profile: ${selectedProfile}${selectedProfile === 'integration' ? ' · wall-clock; fake clock unavailable; návrh vybírá skutečný backend' : selectedProfile === 'deterministic' ? ' · injected fake clock; syntetický vstup' : ''}. Capabilities: ${(snapshot?.capabilities || remoteCapabilities).join(', ') || 'unavailable'}. ${commandNotice} ${replay ? 'Historický záznam; žádné povely.' : ''}`;
      get('twin-controls').hidden = !active || replay || snapshot?.source.mode !== 'simulation' || role !== 'control';
      for (const button of get('twin-controls').querySelectorAll('button')) {
        const urgentStop = button.dataset.twinAction === 'stop';
        button.disabled = urgentStop ? pendingStop : pending || pendingStop ||
          (button.dataset.twinAction !== 'release' && status !== 'online');
        if (button.dataset.twinAction === 'text') button.textContent = selectedProfile === 'integration' ? 'Odeslat integrační text' : 'Syntetický textový vstup';
      }
      for (const id of ['twin-speed','twin-pause','twin-resume','twin-step']) {
        get(id).disabled = pending || pendingStop || status !== 'online' || selectedProfile !== 'deterministic';
      }
      get('twin-variant').disabled = selectedProfile !== 'deterministic';
      get('twin-text').disabled = selectedProfile !== 'integration';
      if (get('twin-text').parentElement) get('twin-text').parentElement.hidden = selectedProfile !== 'integration';
      get('twin-replay').hidden = !replay;
      get('twin-export').disabled = recording.length === 0;
      get('twin-recording-status').textContent = `${recording.length} plných snapshotů, ${recordingDrops} vyřazených z lokálního bufferu. Limit 2048 / 8 MiB.`;
      get('twin-metadata').textContent = snapshot ? JSON.stringify({profile:selectedProfile,source:snapshot.source, sequence:snapshot.sequence, timeMs:snapshot.timeMs,
        revision:snapshot.revision, receivedAtUtc:projection.current.receivedAtUtc, receiptAgeMs:projection.current.ageMs,
        reported:snapshot.reported, modelEvidence:snapshot.model?.evidence || 'unavailable',
        traceDropped:snapshot.trace?.droppedEvents ?? 'unknown', transport:snapshot.transport,
        serverDroppedSnapshots:projection.current.serverDroppedSnapshots ?? 'unknown'}, null, 2) : 'unknown';
      const slot = selectedSlot, servo = snapshot?.reported.servo?.find(entry => entry.slot === slot);
      const position = slot ? projection.positions[slot - 1] : null;
      const outputs = reportedOutputs(model.twinManifest, snapshot?.reported);
      const selectedAudio = outputs.audioMeshes.includes(selectedMesh) || model.twinManifest?.audio?.speakers?.includes(selectedMesh) || model.twinManifest?.audio?.amplifiers?.includes(selectedMesh);
      get('twin-selection').textContent = `${selectedMesh || 'Vyberte objekt v CAD nebo událost timeline'}${slot ? ` · slot ${slot} · PCA servo ${slot-1}, vibrace ${slot+5} · poloha ${position === null ? 'unknown (průhledný referenční CAD)' : position.toFixed(3)} (${projection.estimates[slot-1]}) · pulse ${servo?.pulseUs ?? 'unknown'} µs · vibration mask ${snapshot?.reported.vibrationMask ?? 'unknown'} · outputs ${snapshot?.reported.outputsEnabled ?? 'unknown'}` : ''}${selectedAudio ? ` · audio ${outputs.audioEvidence}; enqueue/cancel/busy není měření slyšitelnosti` : ''}`;
      get('twin-audio-status').textContent = `Audio: ${outputs.audioEvidence}. Zvýraznění ukazuje enqueue/cancel/voice-busy evidence, nikdy slyšitelný zvuk. Vibrační zvýraznění = přijatý výstup při outputsEnabled; není měření vibrace.`;
      const events = snapshot?.trace?.events || [];
      get('twin-trace-status').textContent = traceSummary(snapshot?.trace, model.showManifest);
      const traceKey = JSON.stringify([events, selectedSlot, projection.epoch]);
      if (traceKey !== lastTraceKey) {
        lastTraceKey = traceKey; get('twin-trace').replaceChildren();
        for (const event of events) {
          const item = document.createElement('li'), button = document.createElement('button');
          button.textContent = `${event.sequence ?? '?'} · ${event.timeMs ?? 'unknown'} ms · ${event.kind || '?'} · ${event.phaseId || ''} · slot ${event.slot ?? '?'} · ${event.evidence || 'unknown'}`;
          button.setAttribute('aria-pressed', String(event.slot === selectedSlot));
          button.onclick = () => { selectedSlot = event.slot >= 1 && event.slot <= 6 ? event.slot : null; selectedMesh = selectedSlot ? `tray_${selectedSlot}` : null; if (selectedMesh) hooks.select(selectedMesh); display(); };
          item.append(button); get('twin-trace').append(item);
        }
      }
    }
    function project() {
      hooks.apply(projection.positions.map((_, index) => projection.angle(index+1)), projection.current?.snapshot.reported.ledGrb || [], projection.current?.snapshot.reported || {});
      display();
    }
    function remember(value) {
      const copy = JSON.parse(JSON.stringify(value)), bytes = new TextEncoder().encode(JSON.stringify(copy)).length;
      if (recording.at(-1)?.snapshot.sequence === copy.snapshot.sequence && recording.at(-1)?.snapshot.source.bootId === copy.snapshot.source.bootId) return;
      // Reserve JSON commas/wrapper metadata in addition to each envelope's bytes.
      const bufferLimit = MAX_BYTES - MAX_SNAPSHOTS - 1024;
      if (bytes > bufferLimit) { recordingDrops++; return; }
      recording.push(copy); recordingBytes += bytes;
      while (recording.length > MAX_SNAPSHOTS || recordingBytes > bufferLimit) {
        recordingBytes -= new TextEncoder().encode(JSON.stringify(recording.shift())).length; recordingDrops++;
      }
    }
    async function request(path, options = {}) {
      if (offline) throw new Error('Offline export: pro session otevřete /twin na originu serveru');
      const response = await root.fetch(base + path, {...options, credentials:'include', cache:'no-store'});
      if (!response.ok) throw new Error(`Twin HTTP ${response.status}: ${response.status === 401 || response.status === 403 ? 'session/oprávnění' : 'požadavek odmítnut'}`);
      if (response.status === 204) return null;
      return response.json();
    }
    async function list() {
      try {
        const value = await request('/v1/twin/instances'); role = value.role || 'observer';
        const chosen = get('twin-source').value;
        sourceProfiles.clear(); sourceModes.clear();
        get('twin-source').replaceChildren(new Option('Návrhový náhled', ''));
        for (const item of value.instances || []) {
          sourceProfiles.set(item.instanceId, ['deterministic','integration'].includes(item.profile) ? item.profile : 'unknown');
          sourceModes.set(item.instanceId, ['live','simulation'].includes(item.mode) ? item.mode : 'unknown');
          get('twin-source').add(new Option(`${item.mode} · ${item.profile || 'unknown'} · ${item.instanceId} · ${item.status || (item.online ? 'online' : 'offline')}`, item.instanceId));
        }
        if (active && !replay) {
          selectedProfile = sourceProfiles.get(projection.instanceId) || selectedProfile;
          selectedMode = sourceModes.get(projection.instanceId) || selectedMode;
        }
        if (chosen && !(value.instances || []).some(item => item.instanceId === chosen)) get('twin-source').add(new Option('Zdroj nedostupný · ' + chosen, chosen));
        get('twin-source').value = chosen; failure = '';
      } catch (error) { failure = error.message; }
      display();
    }
    async function poll(ownGeneration) {
      if (ownGeneration !== generation || replay || !active) return;
      try {
        const value = await request('/v1/twin/instances/' + encodeURIComponent(projection.instanceId));
        if (ownGeneration !== generation || replay) return;
        if (!projection.accept(value, root.performance.now())) throw new Error('Odmítnut starší nebo cizí snapshot');
        failure = ''; remember(value); project();
      } catch (error) { if (ownGeneration === generation) { failure = error.message; display(); } }
      if (ownGeneration === generation && !replay) timer = root.setTimeout(() => poll(ownGeneration), 250);
    }
    async function command(body) {
      const urgentStop = body.action === 'stop';
      if ((urgentStop ? pendingStop : pending || pendingStop) || replay || role !== 'control' || projection.current?.snapshot.source.mode !== 'simulation' ||
          (!urgentStop && body.action !== 'release' && projection.status(root.performance.now()) !== 'online') ||
          (body.action === 'clock' && selectedProfile !== 'deterministic') ||
          (body.action === 'integrationText' && selectedProfile !== 'integration') ||
          (body.action === 'text' && selectedProfile !== 'deterministic')) return;
      const commandJson = JSON.stringify(body);
      if (new TextEncoder().encode(commandJson).length > 2048) {
        failure = 'Povel přesahuje 2048 bajtů UTF-8 JSON.'; display(); return;
      }
      // The server admits urgent Stop independently of long integration work.
      // Keep one request of each kind, with no ordinary commands during Stop.
      if (urgentStop) pendingStop = true;
      else pending = true;
      failure = ''; commandNotice = ''; display();
      if (urgentStop) void browserMedia?.invalidate('Stop: mikrofon, kamera a lokální přehrávání zastaveny.');
      const ownGeneration = generation;
      try {
        const result = await request('/v1/twin/instances/' + encodeURIComponent(projection.instanceId) + '/commands', {
          method:'POST', headers:{'Content-Type':'application/json'}, body:commandJson});
        if (ownGeneration === generation && !result.accepted) failure = 'Povel odmítnut: ' + (result.code || 'unknown');
        if (ownGeneration === generation && result.accepted && body.action === 'release') commandNotice = 'Ovládání uvolněno; další povel může znovu požádat o vlastnictví.';
      } catch (error) { if (ownGeneration === generation) failure = error.message; }
      finally {
        if (ownGeneration === generation) {
          if (urgentStop) pendingStop = false;
          else pending = false;
          display();
        }
      }
    }
    get('twin-source').onchange = () => {
      const instance = get('twin-source').value; reset(); active = !!instance; replay = false;
      if (active) {
        selectedProfile = sourceProfiles.get(instance) || 'unknown'; selectedMode = sourceModes.get(instance) || 'unknown';
        hooks.activate(); projection.select(instance); poll(generation);
      } else hooks.deactivate();
      display();
    };
    get('twin-refresh').onclick = list;
    get('twin-exit-replay').onclick = () => { reset(); active = false; replay = false; hooks.deactivate(); display(); };
    get('twin-session').onsubmit = async event => {
      event.preventDefault(); if (!local) return;
      const credential = get('twin-credential').value; get('twin-credential').value = '';
      try {
        if (root.location.protocol !== 'https:' && !['localhost', '127.0.0.1', '[::1]'].includes(root.location.hostname)) throw new Error('Credential vyžaduje HTTPS nebo loopback');
        reset(); active = false; replay = false; hooks.deactivate(); base = root.location.origin;
        const session = await request('/v1/twin/session', {method:'POST', headers:{'Content-Type':'application/json'}, body:JSON.stringify({credential})});
        role = session.role || 'observer'; await list();
      } catch (error) { failure = error.message; display(); }
    };
    for (const button of panel.querySelectorAll('[data-twin-action]')) button.onclick = () => {
      const action = button.dataset.twinAction, body = {action};
      if (action === 'text' && selectedProfile === 'integration') {
        const text = get('twin-text').value.trim();
        if (!text || text.length > 1024) { failure = 'Integrační text musí mít 1–1024 znaků.'; display(); return; }
        body.action = 'integrationText'; body.text = text;
      } else if (action === 'text') {
        body.slot = +get('twin-slot').value; body.variant = get('twin-variant').value; body.text = 'synthetic service input';
      }
      if (action === 'text' || action === 'dance') body.operationId = root.crypto.randomUUID();
      command(body);
    };
    get('twin-pause').onclick = () => command({action:'clock', paused:true});
    get('twin-resume').onclick = () => command({action:'clock', paused:false, speed:+get('twin-speed').value});
    get('twin-step').onclick = () => command({action:'clock', paused:true, stepMs:250});
    get('twin-speed').onchange = () => command({action:'clock', speed:+get('twin-speed').value});
    get('twin-inject').onclick = () => command({action:'fault', slot:+get('twin-slot').value, fault:get('twin-fault').value});
    get('twin-export').onclick = () => {
      const value = {schemaVersion:1, profile:selectedProfile, cadManifestRevision:model.twinManifest?.revision ?? null, snapshots:recording};
      const url = root.URL.createObjectURL(new Blob([JSON.stringify(value)], {type:'application/json'}));
      const link = document.createElement('a'); link.href = url; link.download = 'digital-twin-recording.json'; link.click(); root.URL.revokeObjectURL(url);
    };
    function seek(index) {
      currentIndex = Math.max(0, Math.min(recording.length - 1, index));
      // Seeking may move backwards; reconstruct only recorded state, within its boot.
      projection.select(recording[currentIndex].snapshot.source.instanceId);
      const boot = recording[currentIndex].snapshot.source.bootId;
      for (let i=0; i<=currentIndex; i++) if (recording[i].snapshot.source.bootId === boot) projection.accept(recording[i], root.performance.now());
      get('twin-seek').value = currentIndex;
      get('twin-record-time').textContent = `${currentIndex+1}/${recording.length} · ${recording[currentIndex].snapshot.timeMs} ms (device)`;
      project();
    }
    get('twin-import').onchange = async () => {
      const file = get('twin-import').files[0]; if (!file) return;
      const ownGeneration = generation;
      try {
        if (file.size > MAX_BYTES) throw new Error('Záznam přesahuje 8 MiB');
        const value = parseRecording(await file.text());
        if (ownGeneration !== generation) return;
        reset(); active = true; replay = true;
        selectedProfile = ['deterministic','integration'].includes(value.profile) ? value.profile : 'unknown';
        selectedMode = value.snapshots[0].snapshot.source.mode;
        hooks.activate(); recording = value.snapshots; recordingBytes = file.size;
        get('twin-source').value = ''; get('twin-seek').max = recording.length - 1; seek(0);
      } catch (error) { failure = error.message; display(); }
      get('twin-import').value = '';
    };
    get('twin-seek').oninput = () => { stopReplay(); seek(+get('twin-seek').value); };
    function replayTick(now) {
      if (!replayPlaying || !replay) return;
      const elapsed = replayOffset + (now - replayStarted) * +get('twin-replay-speed').value;
      const first = Date.parse(recording[0].receivedAtUtc);
      let next = currentIndex;
      while (next + 1 < recording.length && Date.parse(recording[next+1].receivedAtUtc) - first <= elapsed) next++;
      if (next !== currentIndex) seek(next);
      if (currentIndex === recording.length - 1) stopReplay(); else replayFrame = root.requestAnimationFrame(replayTick);
    }
    get('twin-play').onclick = () => {
      if (replayPlaying) { stopReplay(); return; }
      if (!replay || !recording.length) return;
      if (currentIndex === recording.length - 1) seek(0);
      replayOffset = Date.parse(recording[currentIndex].receivedAtUtc) - Date.parse(recording[0].receivedAtUtc);
      replayStarted = root.performance.now(); replayPlaying = true; get('twin-play').textContent = 'Pozastavit záznam';
      replayFrame = root.requestAnimationFrame(replayTick);
    };
    get('twin-replay-speed').onchange = stopReplay;
    // Age continues while an HTTP request stalls; this timer never advances geometry.
    const ageTimer = root.setInterval(() => { if (active && !replay) display(); }, 250);
    panel.addEventListener('keydown', event => {
      if (event.key === 'Escape') { get('twin-details').open = false; get('twin-details').querySelector('summary').focus(); }
    });
    root.addEventListener('pagehide', () => { stopPolling(); stopReplay(); root.clearInterval(ageTimer); });
    if (typeof root.TwinMedia !== 'undefined') browserMedia = root.TwinMedia.mount(document, get('twin-details'), {
      request:(instanceId,path,options) => request('/v1/twin/instances/'+encodeURIComponent(instanceId)+path,options)
    });
    display();
    return {active:() => active, angle:slot => projection.angle(slot),
      selection:name => { if (selectedMesh === name) return; selectedMesh = name; selectedSlot = model.twinManifest?.slots.find(slot => Object.values(slot.components).includes(name) || slot.liftComponents.includes(name) || slot.rotationComponents.includes(name))?.slot || null; display(); },
      uncertainty:slot => projection.estimates[slot - 1] === 'unknown' || projection.estimates[slot - 1] === 'moving',
      state:() => ({active,replay,pending:pending || pendingStop,pendingOrdinary:pending,pendingStop,mode:selectedMode,profile:selectedProfile,instanceId:projection.instanceId,status:projection.status(root.performance.now()),selectedSlot,positions:[...projection.positions]})};
  }
  const api = {Projection, decimal, validateSnapshot, parseRecording, traceSummary, reportedOutputs, ageStatus, mount, MAX_BYTES, MAX_SNAPSHOTS};
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.DigitalTwin = api;
})(typeof window === 'undefined' ? globalThis : window);

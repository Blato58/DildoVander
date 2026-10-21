import { decodeMusic, MusicDance, MUSIC_FEATURES, waveformPaths } from './music.mjs';

export function mountMusicPanel(document, options) {
  const get = id => document.getElementById(`music-${id}`);
  const audio = get('audio');
  if (!audio) return null;
  for (let channel = 0; channel < 6; channel++) {
    const row = document.createElement('div'); row.className = 'music-channel';
    const label = document.createElement('label');
    const checkbox = document.createElement('input'); checkbox.type = 'checkbox'; checkbox.id = `music-channel-${channel}`;
    checkbox.checked = channel === 0; label.append(checkbox, ` Kanál ${channel}`);
    const feature = document.createElement('select'); feature.className = 'aw-input'; feature.id = `music-feature-${channel}`;
    feature.setAttribute('aria-label', `Zvuková složka kanálu ${channel}`);
    for (const [index, value] of MUSIC_FEATURES.entries()) {
      const option = document.createElement('option'); option.value = value.id; option.textContent = value.label;
      option.selected = index === channel; feature.append(option);
    }
    const laneLabel = document.createElement('div'); laneLabel.className = 'music-lane-label';
    const laneTitle = document.createElement('strong'); laneTitle.id = `music-label-${channel}`;
    const laneStatus = document.createElement('small'); laneStatus.id = `music-lane-status-${channel}`;
    laneLabel.append(laneTitle, laneStatus); get('lane-labels').append(laneLabel);
    const svg = name => document.createElementNS('http://www.w3.org/2000/svg', name);
    const lane = svg('g'); lane.id = `music-lane-${channel}`;
    lane.setAttribute('transform', `translate(0 ${4 + channel * 40}) scale(1 .4)`);
    const baseline = svg('line'); baseline.setAttribute('x1', '0'); baseline.setAttribute('x2', '1000');
    baseline.setAttribute('y1', '140'); baseline.setAttribute('y2', '140'); baseline.classList.add('music-baseline');
    const path = svg('path'); path.id = `music-motion-${channel}`; path.classList.add('music-motion');
    lane.append(baseline, path); get('lanes').append(lane);
    row.append(label, feature); get('channels').append(row);
  }
  const allChannels = () => Array.from({ length: 6 }, (_, channel) => ({ channel, feature: get(`feature-${channel}`).value }));
  const channels = () => allChannels().filter(value => get(`channel-${value.channel}`).checked);
  const dance = new MusicDance({ ...options, audio, onChange: render });
  let loading = false; let selection = 0; let url = null;
  let summary = 'Zvuk zůstává v tomto prohlížeči. Nic se nenahrává na server.';
  let plotSignature = '';
  function render() {
    get('file').disabled = loading || dance.state.active;
    for (const id of ['dance', 'consent', 'strength', 'lead', ...Array.from({ length: 6 }, (_, channel) => [`channel-${channel}`, `feature-${channel}`]).flat()]) get(id).disabled = dance.state.active;
    get('play').disabled = loading || !dance.analysis || dance.state.active;
    get('pause').disabled = !dance.state.active; get('stop').disabled = !dance.analysis && !loading;
    get('status').textContent = dance.state.message; get('summary').textContent = summary;
    get('progress').max = dance.analysis?.duration || 1; get('progress').value = dance.state.time;
    get('time').textContent = `${dance.state.time.toFixed(1)} / ${(dance.analysis?.duration || 0).toFixed(1)} s`;
    get('timing').textContent = `Předstih ${Number(get('lead').value)} ms + odhad RTT/2 · RTT ${dance.state.rttMs.toFixed(1)} ms · potvrzeno ${dance.state.updateHz.toFixed(1)} snímků/s · cíle PWM: ${dance.state.appliedWidths.map((value, index) => value ? `${index}: ${value} µs` : '').filter(Boolean).join(' · ') || 'zatím žádné'}`;
    const x = Math.min(1000, Math.max(0, dance.state.time / (dance.analysis?.duration || 1) * 1000)).toFixed(1);
    get('playhead').setAttribute('transform', `translate(${x} 0)`);
    const assignments = allChannels();
    const signature = `${selection}:${Number(get('strength').value)}:${assignments.map(value => `${value.feature}:${get(`channel-${value.channel}`).checked}`).join(',')}:${!!dance.analysis}`;
    if (signature !== plotSignature) {
      plotSignature = signature;
      get('wave').setAttribute('d', waveformPaths(dance.analysis).waveform);
      for (const value of assignments) {
        const enabled = get(`channel-${value.channel}`).checked;
        const feature = MUSIC_FEATURES.find(feature => feature.id === value.feature);
        get(`label-${value.channel}`).textContent = `${value.channel} · ${feature.label.split(' · ')[0]}`;
        get(`lane-status-${value.channel}`).textContent = enabled ? 'vybraný kanál' : 'vypnuto · náhled';
        get(`lane-${value.channel}`).classList.toggle('music-lane-off', !enabled);
        get(`motion-${value.channel}`).setAttribute('d', waveformPaths(dance.analysis, Number(get('strength').value), value.feature).motion);
      }
    }
    options.onChange?.();
  }
  for (const id of ['strength', 'lead', ...Array.from({ length: 6 }, (_, channel) => [`feature-${channel}`, `channel-${channel}`]).flat()]) get(id).addEventListener('input', render);
  get('file').addEventListener('change', async () => {
    const file = get('file').files?.[0]; const current = ++selection;
    dance.stop(); dance.analysis = null;
    if (url) URL.revokeObjectURL(url);
    url = null; audio.removeAttribute('src'); audio.load();
    if (!file) { summary = 'Vyberte místní zvuk.'; render(); return; }
    loading = true; dance.state.message = 'Analyzuji waveform, frekvenční pásma a akcenty…'; render();
    try {
      const analysis = await decodeMusic(file);
      if (selection !== current) return;
      dance.analysis = analysis; url = URL.createObjectURL(file); audio.src = url; audio.load();
      summary = `${file.name} · ${analysis.duration.toFixed(1)} s · ${analysis.bpm ? `odhad ${Math.round(analysis.bpm)} BPM` : 'bez zřetelného rytmu'} · ${analysis.accentCount} akcentů · FFT / 10 ms`;
      dance.state.message = 'Připraveno. Vyberte taneční kanály a jejich zvukové složky.';
    } catch (error) { if (selection === current) dance.state.message = `Zvuk nelze načíst: ${error.message}`; }
    finally { if (selection === current) { loading = false; render(); } }
  });
  get('play').addEventListener('click', () => {
    const consent = get('consent').checked; get('consent').checked = false;
    void dance.play({ dance: get('dance').checked, consent, channels: channels(), strength: Number(get('strength').value), leadMs: Number(get('lead').value) });
  });
  get('pause').addEventListener('click', () => dance.pause());
  get('stop').addEventListener('click', () => { ++selection; loading = false; dance.stop(); });
  document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'hidden') dance.pause('Stránka skryta; Play je nutné spustit znovu.'); });
  globalThis.addEventListener?.('pagehide', () => { ++selection; dance.stop(); });
  render(); return dance;
}

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
    row.append(label, feature); get('channels').append(row);
  }
  const channels = () => Array.from({ length: 6 }, (_, channel) => ({ channel, feature: get(`feature-${channel}`).value }))
    .filter(value => get(`channel-${value.channel}`).checked);
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
    const x = (dance.state.time / (dance.analysis?.duration || 1) * 1000).toFixed(1);
    get('cursor').setAttribute('x1', x); get('cursor').setAttribute('x2', x);
    const feature = get(`feature-${get('preview').value}`).value;
    const signature = `${selection}:${Number(get('strength').value)}:${feature}:${!!dance.analysis}`;
    if (signature !== plotSignature) {
      plotSignature = signature;
      const paths = waveformPaths(dance.analysis, Number(get('strength').value), feature);
      get('wave').setAttribute('d', paths.waveform); get('motion').setAttribute('d', paths.motion);
    }
    options.onChange?.();
  }
  for (const id of ['strength', 'lead', 'preview', ...Array.from({ length: 6 }, (_, channel) => `feature-${channel}`)]) get(id).addEventListener('input', render);
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

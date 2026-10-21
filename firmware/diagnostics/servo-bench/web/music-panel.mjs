import { decodeMusic, MusicDance } from "./music.mjs";

export function mountMusicPanel(document, options) {
  const get = id => document.getElementById(`music-${id}`);
  const audio = get("audio");
  if (!audio) return null;
  const dance = new MusicDance({ ...options, audio, onChange: render });
  let loading = false;
  let selection = 0;
  let url = null;
  let summary = "Zvuk zůstává v tomto prohlížeči. Nic se nenahrává na server.";
  function render() {
    get("file").disabled = loading || dance.state.active;
    for (const id of ["dance", "consent", "strength"]) get(id).disabled = dance.state.active;
    get("play").disabled = loading || !dance.analysis || dance.state.active;
    get("pause").disabled = !dance.state.active;
    get("stop").disabled = !dance.analysis && !loading;
    get("status").textContent = dance.state.message;
    get("summary").textContent = summary;
    get("progress").max = dance.analysis?.duration || 1;
    get("progress").value = dance.state.time;
    get("time").textContent = `${dance.state.time.toFixed(1)} / ${(dance.analysis?.duration || 0).toFixed(1)} s${dance.state.width ? ` · ${dance.state.width} µs` : ""}`;
    options.onChange?.();
  }
  get("file").addEventListener("change", async () => {
    const file = get("file").files?.[0];
    const current = ++selection;
    dance.stop(); dance.analysis = null;
    if (url) URL.revokeObjectURL(url);
    url = null; audio.removeAttribute("src"); audio.load();
    if (!file) { summary = "Vyberte místní zvuk."; render(); return; }
    loading = true; dance.state.message = "Analyzuji energii a odhad tempa…"; render();
    try {
      const analysis = await decodeMusic(file);
      if (selection !== current) return;
      dance.analysis = analysis;
      url = URL.createObjectURL(file); audio.src = url; audio.load();
      summary = `${file.name} · ${analysis.duration.toFixed(1)} s · ${analysis.bpm ? `odhad ${Math.round(analysis.bpm)} BPM` : "bez zřetelného rytmu"}`;
      dance.state.message = "Připraveno. Play spustí zvuk a volitelně tanec vybraného kanálu.";
    } catch (error) { if (selection === current) dance.state.message = `Zvuk nelze načíst: ${error.message}`; }
    finally { if (selection === current) { loading = false; render(); } }
  });
  get("play").addEventListener("click", () => {
    const consent = get("consent").checked;
    get("consent").checked = false;
    void dance.play({ dance: get("dance").checked, consent, channel: options.channel(), strength: Number(get("strength").value) });
  });
  get("pause").addEventListener("click", () => dance.pause());
  get("stop").addEventListener("click", () => { ++selection; loading = false; dance.stop(); });
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") dance.pause("Stránka skryta; Play je nutné spustit znovu."); });
  globalThis.addEventListener?.("pagehide", () => { ++selection; dance.stop(); if (url) URL.revokeObjectURL(url); });
  render();
  return dance;
}

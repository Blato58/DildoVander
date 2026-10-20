namespace Kufr.Backend.Features.Show;

internal sealed partial class ShowDeviceCueBridge
{
    private static string SceneKey(string device, string generation, string cue) => device + "\n" + generation + "\n" + cue;

    // The scene core reads this while holding its own gate. This lookup must
    // remain lock-free; enqueue is dispatched after that gate is released.
    internal string? ReadSceneCueStatus(string deviceId, string generation, string cueId) =>
        sceneStatuses.GetValueOrDefault(SceneKey(deviceId, generation, cueId));

    internal string EnqueueSceneCue(string deviceId, string generation, ShowCue cue)
    {
        if (!ValidSceneCue(cue)) { return "unsupported_capability"; }
        if (string.IsNullOrEmpty(generation) || cue.SessionGeneration != generation || !show.IsKnownDevice(deviceId)
            || cue.TargetSlot is not (null or >= 1 and <= 6))
        { return "stale"; }
        if (cue.Kind == "VibratePreset" && cue.TargetSlot is not (>= 1 and <= 6)) { return "stale"; }
        lock (gate)
        {
            var state = show.Read(deviceId);
            var box = Get(deviceId);
            Prune(deviceId, box, state);
            var identity = "scene/" + generation + "/" + cue.CueId;
            if (box.Known.TryGetValue(identity, out var previous))
            {
                return previous.CueType == cue.Kind && (previous.Preset ?? previous.ClipId) == cue.Value
                    && previous.Compartment == (cue.TargetSlot ?? state.SelectedSlot ?? 1)
                    ? previous.Status : "stale";
            }
            if (state.SessionId is null || state.SessionGeneration != generation || state.Quiet
                || state.Phase is "stopped" or "interrupted" || state.OccupiedBy is not null
                || box.Poll is not { AppliedQuiet: false } poll || !HasRecentDevicePoll(deviceId)) { return "stale"; }
            if (box.Queue.Count >= 4) { return "busy"; }
            var scene = cue.Kind == "LedPreset" ? cue.Value : "consider";
            var entry = new Entry(Guid.NewGuid(), null, 0, scene, cue.TargetSlot ?? state.SelectedSlot ?? 1, "",
                state.CancellationEpoch, poll, clock.GetTimestamp(), identity)
            {
                SceneGeneration = generation, SceneSource = cue.CueId, CueType = cue.Kind,
                Preset = cue.Kind == "PlayLocalClip" ? null : cue.Value,
                ClipId = cue.Kind == "PlayLocalClip" ? cue.Value : null,
                Speech = speech with { Voice = poll.Voice ?? speech.Voice }
            };
            box.Queue.Enqueue(entry);
            box.History.Add(entry.Id, entry);
            box.Known.Add(identity, entry);
            UpdateSceneStatus(deviceId, entry);
            TrimHistory(deviceId, box);
            return "queued";
        }
    }

    private static bool ValidSceneCue(ShowCue cue) => cue.Kind switch
    {
        "LedPreset" => cue.Value is "wake" or "consider" or "ambient" or "farewell",
        "VibratePreset" => cue.Value == "presentation-search-pulse",
        "PlayLocalClip" => cue.Value is "intro-1" or "intro-2" or "ending-1" or "ending-2"
            or "operation-1" or "operation-2" || Enumerable.Range(1, 6).Any(n => cue.Value == $"slot-{n}")
            || Enumerable.Range(1, 12).Any(n => cue.Value == $"ambient-{n}"),
        _ => false
    };

    private void UpdateSceneStatus(string deviceId, Entry entry)
    {
        if (entry.SceneGeneration is { } generation && entry.SceneSource is { } source)
        { sceneStatuses[SceneKey(deviceId, generation, source)] = entry.Status; }
    }
}

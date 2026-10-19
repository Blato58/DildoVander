namespace Kufr.Backend.Features.Show;

internal sealed record ShowCueProgress(string CueId, string Kind, string Status, string? Reason = null);
internal sealed record ShowSceneProgress(string SceneId, string SessionGeneration, bool PausePending,
    bool Completed, bool Cancelled, ShowCue? LastUiBeat, IReadOnlyList<ShowCueProgress> Cues, long Revision = 0);

// Executes optional dramaturgical boundaries only. A presentation remains a
// separate existing proposal/outcome path; no cue here dispatches an actuator.
internal sealed class ShowSceneExecution
{
    private readonly ShowScene scene;
    private readonly TimeProvider clock;
    private readonly long started;
    private readonly Dictionary<string, ShowCueProgress> progress;
    private long? pauseStarted;
    private int pauseDurationMs;
    private bool cancelled;
    private bool optionalRetired;
    private ShowCue? lastUiBeat;
    private bool audioStarted;
    private bool playbackFinished;
    private long revision;
    private readonly Func<ShowCue, string?>? adapterStatus;
    private readonly Dictionary<string, long> adapterStarted = new(StringComparer.Ordinal);
    private string? presentationOperation;
    internal bool IsCancelled => cancelled;

    internal ShowSceneExecution(ShowScene scene, TimeProvider clock, bool selectionValid,
        Func<ShowCue, string?>? adapterStatus = null)
    {
        ShowSceneLibrary.Validate(scene);
        this.scene = scene;
        this.clock = clock;
        this.adapterStatus = adapterStatus;
        started = clock.GetTimestamp();
        progress = scene.Cues.ToDictionary(c => c.CueId, c => new ShowCueProgress(c.CueId, c.Kind, "planned"));
        Event("scene_started");
        if (selectionValid) { Event("selection_validated"); }
        else
        {
            foreach (var cue in scene.Cues.Where(c => c.Binding == "selection_validated"))
            { Set(cue, "dropped", "no-current-selection"); }
        }
    }

    internal string ActorDirection => string.Join(" ", scene.Cues
        .Where(c => c.Kind == "ActorDirection" && progress[c.CueId].Status is "submitted" or "observed_proxy")
        .Select(c => c.Value));

    internal ShowSceneProgress Tick()
    {
        if (cancelled) { return Snapshot(); }
        if (clock.GetElapsedTime(started).TotalMilliseconds >= scene.MaximumDurationMs)
        {
            foreach (var cue in scene.Cues.Where(c => !Terminal(progress[c.CueId].Status)))
            { Set(cue, "dropped", "scene-deadline"); }
            pauseStarted = null;
            return Snapshot();
        }
        foreach (var cue in scene.Cues.Where(c => progress[c.CueId].Status is "ready_for_adapter" or "submitted" or "accepted").ToArray())
        {
            if (cue.Kind is not ("PlayLocalClip" or "LedPreset" or "VibratePreset")) { continue; }
            if (adapterStarted.TryGetValue(cue.CueId, out var admittedAt)
                && clock.GetElapsedTime(admittedAt).TotalMilliseconds >= cue.DeadlineMs)
            { Set(cue, "dropped", "cue-deadline"); }
            else if (progress[cue.CueId].Status != "ready_for_adapter" && adapterStatus?.Invoke(cue) is { } status)
            { ApplyAdapterResult(cue.CueId, status); }
        }
        if (pauseStarted is { } pause && clock.GetElapsedTime(pause).TotalMilliseconds >= pauseDurationMs)
        {
            foreach (var cue in scene.Cues.Where(c => c.Kind == "Pause" && progress[c.CueId].Status == "waiting"))
            { Set(cue, "completed", "monotonic-pause-elapsed"); }
            pauseStarted = null;
            Event("local_beat_advanced", single: true);
        }
        return Snapshot();
    }

    internal ShowSceneProgress TerminalAudioStarted()
    {
        Tick();
        if (cancelled || optionalRetired || audioStarted || Snapshot().Completed) { return Snapshot(); }
        audioStarted = true;
        foreach (var cue in scene.Cues.Where(c => c.Kind == "ActorDirection" && progress[c.CueId].Status == "submitted"))
        { Set(cue, "observed_proxy", "audio-submission-proxy-not-exact-words"); }
        Event("local_beat_advanced", single: true);
        return Snapshot();
    }

    internal ShowSceneProgress PlaybackConfirmed()
    {
        Tick();
        if (cancelled || optionalRetired || playbackFinished || !audioStarted || Snapshot().Completed) { return Snapshot(); }
        playbackFinished = true;
        foreach (var cue in scene.Cues.Where(c => c.Kind == "ActorDirection" && progress[c.CueId].Status == "observed_proxy"))
        { Set(cue, "completed", "correlated-player-drained-proxy"); }
        Event("playback_finished");
        Event("local_beat_advanced", single: true);
        return Snapshot();
    }

    internal ShowSceneProgress PresentationOutcomeObserved()
    {
        Tick();
        if (!cancelled)
        {
            foreach (var cue in scene.Cues.Where(c => c.Kind == "PresentSelected" && progress[c.CueId].Status == "accepted"))
            { Set(cue, "completed", "existing-operation-outcome:" + presentationOperation); }
            Event("existing_presentation_outcome");
        }
        return Snapshot();
    }

    internal ShowSceneProgress PresentationProposalBound(string operationId, string proposalId)
    {
        if (cancelled || presentationOperation is not null && presentationOperation != operationId) { return Snapshot(); }
        presentationOperation = operationId;
        foreach (var cue in scene.Cues.Where(c => c.Kind == "PresentSelected" && progress[c.CueId].Status == "planned"))
        { Set(cue, "accepted", "existing-operation:" + operationId + "/proposal:" + proposalId); }
        return Snapshot();
    }

    internal ShowCue[] TakeAdapterCues()
    {
        Tick();
        if (cancelled || optionalRetired) { return []; }
        var cues = scene.Cues.Where(c => progress[c.CueId].Status == "ready_for_adapter").ToArray();
        foreach (var cue in cues) { Set(cue, "submitted", "awaiting-device-admission"); }
        return cues;
    }

    internal void ApplyAdapterResult(string cueId, string status)
    {
        if (cancelled || !progress.TryGetValue(cueId, out var current) || Terminal(current.Status)) { return; }
        var mapped = status switch
        {
            "queued" or "submitted" => "submitted",
            "accepted" => "accepted",
            "completed" => "completed",
            "unsupported_capability" => "unsupported",
            "cancelled" => "cancelled",
            _ => "dropped"
        };
        if (current.Status == mapped && current.Reason == status) { return; }
        Set(scene.Cues.Single(c => c.CueId == cueId), mapped, status);
    }

    internal ShowSceneProgress Cancel()
    {
        if (cancelled) { return Snapshot(); }
        cancelled = true;
        revision++;
        pauseStarted = null;
        foreach (var cue in scene.Cues.Where(c => !Terminal(progress[c.CueId].Status)))
        { Set(cue, "cancelled", "owner-cancelled"); }
        lastUiBeat = null;
        return Snapshot();
    }

    internal ShowSceneProgress RetireOptional()
    {
        if (cancelled || optionalRetired) { return Snapshot(); }
        optionalRetired = true;
        pauseStarted = null;
        foreach (var cue in scene.Cues.Where(c => c.Kind != "PresentSelected"
            && c.Binding != "existing_presentation_outcome" && !Terminal(progress[c.CueId].Status)))
        { Set(cue, "cancelled", "dialogue-owner-completed"); }
        lastUiBeat = null;
        return Snapshot();
    }

    private void Event(string binding, bool single = false)
    {
        if (cancelled || optionalRetired && binding != "existing_presentation_outcome") { return; }
        foreach (var cue in scene.Cues.Where(c => c.Binding == binding && progress[c.CueId].Status == "planned"))
        {
            switch (cue.Kind)
            {
                case "ActorDirection": Set(cue, "submitted", "awaiting-existing-actor"); break;
                case "UiBeat": lastUiBeat = cue; Set(cue, "completed", "local-boundary"); break;
                case "Pause":
                    pauseDurationMs = int.Parse(cue.Value, System.Globalization.CultureInfo.InvariantCulture);
                    pauseStarted = clock.GetTimestamp();
                    Set(cue, "waiting", "monotonic-pause");
                    break;
                case "WaitForEvent": Set(cue, "completed", "existing-outcome-observed"); break;
                case "PlayLocalClip":
                case "LedPreset":
                case "VibratePreset":
                    adapterStarted[cue.CueId] = clock.GetTimestamp();
                    Set(cue, adapterStatus is null ? "unsupported" : "ready_for_adapter",
                        adapterStatus is null ? "unsupported_capability" : "bounded-device-adapter");
                    break;
                case "PresentSelected": Set(cue, "unsupported", "presentation-owned-by-existing-proposal-path"); break;
                default: throw new InvalidOperationException("Unsupported cue kind.");
            }
            if (single) { break; }
        }
    }

    private void Set(ShowCue cue, string status, string? reason)
    {
        progress[cue.CueId] = new(cue.CueId, cue.Kind, status, reason);
        revision++;
    }

    private static bool Terminal(string status) => status is "completed" or "dropped" or "cancelled" or "unsupported";

    private ShowSceneProgress Snapshot() => new(scene.SceneId, scene.Cues[0].SessionGeneration,
        pauseStarted is not null && !cancelled, progress.Values.All(c => Terminal(c.Status)), cancelled,
        lastUiBeat, scene.Cues.Select(c => progress[c.CueId]).ToArray(), revision);
}

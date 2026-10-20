using Kufr.Backend.Features.Character;

namespace Kufr.Backend.Features.Show;

internal sealed record TrunkMood(double Energy = 50, double Irritation = 20, double Confidence = 55,
    double Suspicion = 20, string Dominant = "amused", bool Locked = false);

internal sealed record ShowCue(string CueId, string Kind, string Binding, int OffsetMs, int DeadlineMs,
    string LatePolicy, string SessionGeneration, string Value, int? TargetSlot = null);

internal sealed record ShowScene(string SceneId, int Version, string Variant, string[] Phases,
    string[] RequiredCapabilities, int Priority, int MaximumDurationMs, string[] AllowedFinaleProfiles,
    string[] Preconditions, string Cancellation, string Fallback, ShowCue[] Cues);

internal sealed record ShowState(string DeviceId, string EveningId, long Revision, string? SessionId,
    string? SessionGeneration, string Phase, TrunkMood Mood, string Tone, string Tempo, bool Quiet,
    bool AmbientEnabled, string? SceneId, ShowCue? PendingCue, string? OccupiedBy, DateTimeOffset UpdatedAt,
    long CancellationEpoch = 0, string[]? RecentSceneIds = null, string[]? RecentAmbientIds = null,
    string[]? ScheduledAmbientIds = null, DateTimeOffset[]? AmbientSpeechWindow = null,
    DateTimeOffset? LastAmbientSpeechAt = null, TrunkMood? EveningMood = null,
    DateTimeOffset? SessionStartedAt = null, string MoodScope = "automatic",
    int ReadyMask = 0, bool SelectionLocked = true, int? SelectedSlot = null,
    ShowSceneProgress? SceneExecution = null, FinalePlan? FinalePlan = null, string? PublicOrigin = null,
    string[]? RecentWandLineIds = null, string[]? ScheduledWandLineIds = null, uint DeviceSettingsRevision = 0,
    DeviceCueAppliedSettings? DeviceSettings = null, uint DeviceStopRevision = 0,
    Dictionary<string, int>? ScheduledWandCounters = null);

internal sealed record ShowCommand(string CommandId, long ExpectedStateRevision, string Kind,
    string? SessionId = null, string? SessionGeneration = null, string? Value = null, int TtlSeconds = 30);

internal sealed record ShowCommandResult(string Status, string Code, ShowState State);
internal sealed record ShowControl(string Kind, string? Value, DateTimeOffset ExpiresAt);

internal static class MoodReactor
{
    internal static readonly string[] Names = ["amused", "bored", "irritated", "confident", "suspicious", "panicking"];

    internal static TrunkMood Set(string name, bool locked = false) => name switch
    {
        "amused" => new(Dominant: name, Locked: locked),
        "bored" => new(15, 20, 50, 20, name, locked),
        "irritated" => new(55, 80, 50, 20, name, locked),
        "confident" => new(65, 20, 90, 20, name, locked),
        "suspicious" => new(45, 25, 50, 85, name, locked),
        "panicking" => new(90, 60, 15, 80, name, locked),
        _ => throw new ArgumentException("Unknown mood.")
    };

    // Safe points use elapsed time, never transcript tokens or inferred emotion.
    internal static TrunkMood Advance(TrunkMood mood, TimeSpan elapsed, bool completed = false, bool waiting = false)
    {
        if (mood.Locked) { return mood; }
        var decay = Math.Clamp(elapsed.TotalMinutes / 20, 0, 1);
        double Blend(double value, double baseline, double delta = 0) =>
            Math.Clamp(value + (baseline - value) * decay + delta, 0, 100);
        var next = new TrunkMood(Blend(mood.Energy, 50, completed ? 4 : 0),
            Blend(mood.Irritation, 20, waiting ? 3 : -1), Blend(mood.Confidence, 55, completed ? 2 : 0),
            Blend(mood.Suspicion, 20), mood.Dominant);
        var dominant = next switch
        {
            { Energy: >= 80, Confidence: <= 30 } => "panicking",
            { Irritation: >= 70 } => "irritated",
            { Suspicion: >= 70 } => "suspicious",
            { Confidence: >= 80 } => "confident",
            { Energy: <= 25 } => "bored",
            _ => "amused"
        };
        // Retain a near-threshold mood to avoid alternating directions.
        if (dominant == "amused" && (mood.Dominant switch
            {
                "irritated" => next.Irritation > 60,
                "suspicious" => next.Suspicion > 60,
                "confident" => next.Confidence > 70,
                "bored" => next.Energy < 35,
                "panicking" => next.Energy > 70 && next.Confidence < 40,
                _ => false
            }))
        { dominant = mood.Dominant; }
        return next with { Dominant = dominant };
    }
}

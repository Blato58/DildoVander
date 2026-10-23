namespace Kufr.Backend.Features.Keepsakes;

internal enum KeepsakeReadinessState { Pending, Processing, Partial, Ready, Failed, Unavailable }
internal enum KeepsakeReadinessReason { None, AwaitingResult, AwaitingCapacity, PausedPreparing, ReconciliationRequired, GenerationFailed, ResultUnavailable }

internal sealed record KeepsakeReadiness
{
    private readonly KeepsakeReadinessState state;
    private readonly KeepsakeReadinessReason reason;

    private KeepsakeReadiness(KeepsakeReadinessState state, KeepsakeReadinessReason reason,
        bool canRetry, bool shouldPoll, bool hasStory, bool hasArtwork)
    {
        this.state = state;
        this.reason = reason;
        CanRetry = canRetry;
        ShouldPoll = shouldPoll;
        HasStory = hasStory;
        HasArtwork = hasArtwork;
    }

    public string State => state switch
    {
        KeepsakeReadinessState.Pending => "pending",
        KeepsakeReadinessState.Processing => "processing",
        KeepsakeReadinessState.Partial => "partial",
        KeepsakeReadinessState.Ready => "ready",
        KeepsakeReadinessState.Failed => "failed",
        _ => "unavailable"
    };
    public string? Reason => reason switch
    {
        KeepsakeReadinessReason.AwaitingResult => "awaiting_result",
        KeepsakeReadinessReason.AwaitingCapacity => "awaiting_capacity",
        KeepsakeReadinessReason.PausedPreparing => "paused_preparing",
        KeepsakeReadinessReason.ReconciliationRequired => "reconciliation_required",
        KeepsakeReadinessReason.GenerationFailed => "generation_failed",
        KeepsakeReadinessReason.ResultUnavailable => "result_unavailable",
        _ => null
    };
    public bool CanRetry { get; }
    public bool ShouldPoll { get; }
    public bool HasStory { get; }
    public bool HasArtwork { get; }

    internal static KeepsakeReadiness Pending(KeepsakeReadinessReason reason = KeepsakeReadinessReason.AwaitingResult) =>
        new(KeepsakeReadinessState.Pending, reason, false, true, false, false);

    internal static KeepsakeReadiness Unavailable() =>
        new(KeepsakeReadinessState.Unavailable, KeepsakeReadinessReason.ResultUnavailable, false, false, false, false);

    internal static KeepsakeReadiness From(string? jobState, bool needsBase,
        bool hasStory, bool hasArtwork, bool baseDispatchEnabled)
    {
        if (hasStory && hasArtwork) { return new(KeepsakeReadinessState.Ready, KeepsakeReadinessReason.None, false, false, true, true); }
        if (jobState == "awaiting_reconciliation")
        {
            return hasStory
                ? new(KeepsakeReadinessState.Partial, KeepsakeReadinessReason.ReconciliationRequired, false, true, true, false)
                : new(KeepsakeReadinessState.Processing, KeepsakeReadinessReason.ReconciliationRequired, false, true, false, false);
        }
        if (jobState is "failed" or "cancelled")
        {
            return hasStory
                ? new(KeepsakeReadinessState.Partial, KeepsakeReadinessReason.GenerationFailed, false, false, true, false)
                : new(KeepsakeReadinessState.Failed, KeepsakeReadinessReason.GenerationFailed, false, false, false, false);
        }
        if (hasStory) { return new(KeepsakeReadinessState.Partial, KeepsakeReadinessReason.None, false, true, true, false); }
        return jobState switch
        {
            "queued" or "leased" or "dispatched" => new(KeepsakeReadinessState.Processing, KeepsakeReadinessReason.None, false, true, false, false),
            _ when needsBase && !baseDispatchEnabled => Pending(KeepsakeReadinessReason.PausedPreparing),
            _ when needsBase => Pending(KeepsakeReadinessReason.AwaitingCapacity),
            _ => Pending()
        };
    }
}

internal sealed record CustomerSessionResult(int SchemaVersion, string SessionId, string? ResultLocator,
    KeepsakeReadiness Readiness, string? AccessKind, bool CanManage, bool CanReadPrivate, bool CanUnlink);

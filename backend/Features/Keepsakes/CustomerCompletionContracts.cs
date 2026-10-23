namespace Kufr.Backend.Features.Keepsakes;

internal enum CompletionUpdateKind { Keepsake, Portrait }
internal enum CompletionUpdateState { Pending, Processing, Partial, Ready, Failed, Unavailable }
internal enum CompletionUpdateReason { None, AwaitingCapacity, PausedPreparing, ReconciliationRequired, GenerationFailed, ResultUnavailable }

// Publication evidence belongs to the current job. An older ready edition must
// never make a newly queued replacement appear completed.
internal sealed record CompletionObservation(CompletionUpdateKind Kind, string? WorkId, string? JobState,
    bool NeedsGeneration, bool DispatchEnabled, bool PublishedStory, bool PublishedArtwork,
    bool PublishedPortrait, bool Accessible = true);

internal sealed record CompletionProjection(CompletionUpdateKind Kind, string? WorkId,
    CompletionUpdateState State, CompletionUpdateReason Reason, bool HasStory, bool HasArtwork)
{
    internal bool CanRetry => false;
}

internal sealed record CustomerCompletionUpdate(string Id, long Sequence, string Kind, string State,
    string? Reason, string ResultLocator, bool CanRetry, bool HasStory, bool HasArtwork,
    DateTimeOffset OccurredAt, DateTimeOffset? ReadAt);

internal sealed record CustomerCompletionPage(int SchemaVersion,
    IReadOnlyList<CustomerCompletionUpdate> Items, string? NextCursor);

internal sealed record CustomerCompletionReadRequest(int SchemaVersion);

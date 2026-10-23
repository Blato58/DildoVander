namespace Kufr.Backend.Features.Keepsakes;

internal static class CustomerCompletionPolicy
{
    internal static CompletionProjection Project(CompletionObservation source)
    {
        CompletionProjection State(CompletionUpdateState state,
            CompletionUpdateReason reason = CompletionUpdateReason.None) =>
            new(source.Kind, source.WorkId, state, reason,
                source.Kind == CompletionUpdateKind.Keepsake && source.PublishedStory,
                source.Kind == CompletionUpdateKind.Keepsake ? source.PublishedArtwork : source.PublishedPortrait);

        if (!source.Accessible || source.JobState is not null && string.IsNullOrEmpty(source.WorkId))
        {
            return new(source.Kind, source.WorkId, CompletionUpdateState.Unavailable,
                CompletionUpdateReason.ResultUnavailable, false, false);
        }
        if (source.JobState == "completed")
        {
            var published = source.Kind == CompletionUpdateKind.Keepsake
                ? source.PublishedStory && source.PublishedArtwork : source.PublishedPortrait;
            return published ? State(CompletionUpdateState.Ready)
                : State(CompletionUpdateState.Unavailable, CompletionUpdateReason.ResultUnavailable);
        }
        if (source.JobState is "failed" or "cancelled" or "archived_private")
        {
            return State(source.Kind == CompletionUpdateKind.Keepsake && source.PublishedStory
                ? CompletionUpdateState.Partial : CompletionUpdateState.Failed,
                CompletionUpdateReason.GenerationFailed);
        }
        if (source.JobState == "awaiting_reconciliation")
        {
            return State(source.Kind == CompletionUpdateKind.Keepsake && source.PublishedStory
                ? CompletionUpdateState.Partial : CompletionUpdateState.Processing,
                CompletionUpdateReason.ReconciliationRequired);
        }
        if (source.JobState is "queued" or "leased" or "dispatched")
        {
            return State(source.Kind == CompletionUpdateKind.Keepsake && source.PublishedStory
                ? CompletionUpdateState.Partial : CompletionUpdateState.Processing);
        }
        if (source.WorkId is null && source.Kind == CompletionUpdateKind.Keepsake && source.NeedsGeneration)
        {
            return State(CompletionUpdateState.Pending, source.DispatchEnabled
                ? CompletionUpdateReason.AwaitingCapacity : CompletionUpdateReason.PausedPreparing);
        }
        return State(CompletionUpdateState.Unavailable, CompletionUpdateReason.ResultUnavailable);
    }
}

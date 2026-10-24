namespace Kufr.Backend.Features.Wands;

internal sealed class WandGenerationService(WandGenerationStore store, IWandGenerationProvider provider)
{
    // Authority belongs to the authenticated caller. The callback must check its
    // current revocation/profile/operation fences; persisted input cannot grant it.
    internal async Task<WandGenerationSession> GenerateAsync(string accountId, string operationId,
        Func<CancellationToken, Task<bool>> authority, CancellationToken token = default, bool retry = false, IReadOnlyList<string>? evidence = null)
    {
        token.ThrowIfCancellationRequested();
        if (evidence?.Count > 32 || evidence?.Any(text => !WandGenerationValidation.Text(text, 1000)) == true)
            throw new WandGenerationException("invalid_wand_evidence", 400);
        var job = store.Claim(accountId, operationId, retry);
        if (job is null) return store.Read(accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            if (!await authority(timeout.Token) || !store.CanDispatch(job))
                throw new WandGenerationException("wand_generation_stale");
            var dispatch = job with { Request = job.Request with { Evidence = evidence?.ToArray() ?? [] } };
            var candidates = await provider.GenerateAsync(dispatch, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (!await authority(timeout.Token) || !store.CanDispatch(job))
                throw new WandGenerationException("wand_generation_stale");
            store.Complete(job, candidates);
        }
        catch (OperationCanceledException)
        {
            store.Fail(job, token.IsCancellationRequested ? "generation_cancelled" : "generation_timeout");
            token.ThrowIfCancellationRequested();
        }
        catch (WandGenerationException exception)
        {
            store.Fail(job, exception.Code);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Provider payloads and personal context must not appear in errors.
            store.Fail(job, exception is InvalidDataException or System.Text.Json.JsonException
                ? "generation_invalid_output" : "generation_provider_unavailable");
        }
        return store.Read(accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
    }
}

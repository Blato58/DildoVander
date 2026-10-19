namespace Kufr.Backend.Features.Show;

internal sealed partial class DuelStore
{
    internal void RecoverCommentaryClaims()
    {
        using var connection = Open();
        // A provider request may have been billed before process death. Do not
        // automatically retry it; the verified local script remains available.
        Execute(connection, null,
            "UPDATE show_duel_commentary SET state='fallback',audit=$audit WHERE state='claimed'",
            ("$audit", Serialize(new DuelCommentaryAudit("interrupted_usage_unknown", "not_confirmed"))));
    }

    internal DuelView? ClaimCommentary()
    {
        string? id;
        using (var connection = Open())
        using (var command = Command(connection, null, "SELECT duel_id FROM show_duel_commentary WHERE state='pending' ORDER BY rowid LIMIT 1"))
        {
            id = command.ExecuteScalar() as string;
        }
        if (id is null) { return null; }
        DuelView result;
        try { result = Read(id, publicView: true); }
        catch (DuelException)
        {
            CompleteCommentary(id, new(null, new("source_unavailable", "not_dispatched")), pending: true);
            return null;
        }
        using var claim = Open();
        return Execute(claim, null,
            "UPDATE show_duel_commentary SET state='claimed' WHERE duel_id=$id AND state='pending'", ("$id", id)) == 1 ? result : null;
    }

    internal void CompleteCommentary(string id, DuelCommentaryAttempt attempt, bool pending = false)
    {
        using var connection = Open();
        Execute(connection, null,
            "UPDATE show_duel_commentary SET state=$state,result=$result,audit=$audit WHERE duel_id=$id AND state=$expected",
            ("$state", attempt.Commentary is null ? "fallback" : "completed"),
            ("$result", attempt.Commentary is null ? null : Serialize(attempt.Commentary)),
            ("$audit", Serialize(attempt.Audit)), ("$id", id), ("$expected", pending ? "pending" : "claimed"));
    }

    private static DuelCommentary? ReadCommentary(Microsoft.Data.Sqlite.SqliteConnection connection, string id)
    {
        using var command = Command(connection, null,
            "SELECT result FROM show_duel_commentary WHERE duel_id=$id AND state='completed'", ("$id", id));
        return command.ExecuteScalar() is string json ? Deserialize<DuelCommentary>(json) : null;
    }
}

internal sealed class DuelCommentaryWorker(DuelStore store, DuelCommentaryService commentator,
    ILogger<DuelCommentaryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        store.RecoverCommentaryClaims();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = store.ClaimCommentary();
                if (result is not null)
                {
                    DuelCommentaryAttempt attempt;
                    try { attempt = await commentator.GenerateAsync(result, stoppingToken); }
                    catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                    {
                        attempt = new(null, new("provider_error_usage_unknown", "not_confirmed"));
                    }
                    if (stoppingToken.IsCancellationRequested) { break; }
                    store.CompleteCommentary(result.Id, attempt);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Duel commentary worker iteration failed ({ErrorType}).", exception.GetType().Name);
            }
            try { await timer.WaitForNextTickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}

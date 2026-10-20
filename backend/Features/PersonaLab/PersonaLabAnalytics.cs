namespace Kufr.Backend.Features.PersonaLab;

internal sealed record LabReliabilitySummary(int SuccessfulSessions, int FailedSessions, int PreparedSessions,
    int ConnectionAttempts, int EstablishedConnections, int UnexpectedDisconnections,
    double? UnexpectedDisconnectionRate, int RecoveryAttempts, int SuccessfulRecoveries, double? RecoveryRate,
    int LatencySamples, double? AverageResponseMs, double? P95ResponseMs, double? AverageDurationSeconds,
    decimal? EstimatedCostUsd, int CostMeasuredSessions);

internal static class LabAnalytics
{
    internal static object Summarize(LabSession[] sessions)
    {
        var evaluated = sessions.Where(x => x.Evaluation is not null).ToArray();
        return new
        {
            sessions = sessions.Length,
            evaluations = evaluated.Length,
            summary = Reliability(sessions),
            metrics = LabContract.Metrics.Select(metric => new
            {
                name = metric, count = evaluated.Length,
                mean = evaluated.Length == 0 ? (double?)null : evaluated.Average(x => x.Evaluation!.Scores[metric])
            }),
            errors = sessions.SelectMany(x => x.Events).Where(IsFailure)
                .GroupBy(x => x.Code ?? "unknown").Select(group => new { code = group.Key, count = group.Count() }),
            models = sessions.GroupBy(x => x.Engine).Select(group => new
            {
                model = group.Key, sessions = group.Count(), summary = Reliability(group.ToArray())
            }),
            versions = sessions.GroupBy(x => x.Config.Id).Select(group => new
            {
                versionId = group.Key, sessions = group.Count(), summary = Reliability(group.ToArray()),
                scores = LabContract.Metrics.Select(metric => new
                {
                    name = metric,
                    mean = group.Any(x => x.Evaluation is not null)
                        ? (double?)group.Where(x => x.Evaluation is not null).Average(x => x.Evaluation!.Scores[metric]) : null
                })
            }),
            comparisons = sessions.Where(x => !string.IsNullOrEmpty(x.ComparisonId)).GroupBy(x => x.ComparisonId)
                .Select(group => new { id = group.Key, sessions = group.Select(x => x.Id).ToArray() })
        };
    }

    internal static LabReliabilitySummary Reliability(LabSession[] sessions)
    {
        var events = sessions.SelectMany(x => x.Events).ToArray();
        var failures = sessions.Count(x => x.Events.Any(IsFailure));
        var successes = sessions.Count(x => !x.Events.Any(IsFailure)
            && x.Events.Any(e => e.Type == "provider_turn" && e.Code == "completed"));
        var attempts = events.Count(x => x.Type is "connection_started" or "provider_connecting");
        var connections = events.Count(x => x.Type is "connection_established" or "provider_connected");
        var disconnected = events.Count(x => x.Type == "connection_interrupted");
        var recoveryAttempts = events.Count(x => x.Type == "recovery_started");
        var recovered = events.Count(x => x.Type == "recovery_completed");
        var latency = sessions.SelectMany(x => x.Turns).Where(x => x.Role == "assistant" && x.LatencyMs >= 0)
            .Select(x => (double)x.LatencyMs!.Value).Order().ToArray();
        var durations = sessions.Where(x => x.EndedAt >= x.CreatedAt)
            .Select(x => (x.EndedAt!.Value - x.CreatedAt).TotalSeconds).ToArray();
        // The runtime converts each connection's cumulative usage into deltas.
        // Missing usage is unknown cost, never zero cost or a billing receipt.
        var costSessions = sessions.Where(x => x.Events.Any(e => e.Type == "provider_cost"
            && e.Code == "micro_usd" && e.CostMicroUsd >= 0)).ToArray();
        var costMicroUsd = costSessions.SelectMany(x => x.Events)
            .Where(x => x.Type == "provider_cost" && x.Code == "micro_usd" && x.CostMicroUsd >= 0)
            .Sum(x => (decimal)x.CostMicroUsd!.Value);
        return new(successes, failures, sessions.Length - successes - failures,
            attempts, connections, disconnected, connections == 0 ? null : (double)disconnected / connections,
            recoveryAttempts, recovered, recoveryAttempts == 0 ? null : (double)recovered / recoveryAttempts,
            latency.Length, latency.Length == 0 ? null : latency.Average(),
            latency.Length == 0 ? null : latency[(int)Math.Ceiling(latency.Length * .95) - 1],
            durations.Length == 0 ? null : durations.Average(),
            costSessions.Length == 0 ? null : costMicroUsd / 1_000_000m, costSessions.Length);
    }

    private static bool IsFailure(LabEvent item) => item.Type == "provider_failure"
        && item.Code is not ("cancelled" or "user_cancelled");
}

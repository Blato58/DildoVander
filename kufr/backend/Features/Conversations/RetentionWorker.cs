using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Conversations;

internal sealed class RetentionWorker(
    TimeProvider clock,
    DeviceAccessStore access,
    ConversationStore conversations,
    OperationJournal journal,
    IConfiguration configuration,
    ILogger<RetentionWorker> logger,
    EveningMemoryStore? memory = null,
    Memory.HybridMemoryService? hybridMemory = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var intervalSeconds = configuration.GetValue<int?>("Backend:RetentionSweepSeconds") ?? 60;
        if (intervalSeconds is < 1 or > 3600)
        {
            throw new InvalidOperationException("Invalid retention sweep interval.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(intervalSeconds), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                access.PurgeExpired();
                conversations.SweepExpired();
                journal.PurgeExpired();
                memory?.PurgeHybridMemory();
                hybridMemory?.ClearCache();
            }
            catch (Exception exception)
            {
                // Never log request bodies or credentials from the retention path.
                logger.LogError("Retention sweep failed: {ErrorType}", exception.GetType().Name);
            }
        }
    }
}

using Microsoft.Extensions.Hosting;

namespace Kufr.Backend.Features.TestingSessions;

internal sealed class TestingCharacterEnrichmentWorker(
    TestingResources resources,
    ILogger<TestingCharacterEnrichmentWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await resources.ProcessNextCharacterEnrichmentAsync(stoppingToken)) continue;
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                logger.LogError("Testing character enrichment worker failed ({ErrorType}).", error.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

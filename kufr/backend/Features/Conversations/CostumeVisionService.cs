namespace Kufr.Backend.Features.Conversations;

// Vision is a bounded supporting capability. It never authors spoken dialogue.
internal sealed class CostumeVisionService : IDisposable
{
    private readonly HttpClient client = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly CostumeImageClient? provider;
    private readonly Func<byte[], CancellationToken, Task<CostumeImageResponse>>? analyze;
    private readonly SemaphoreSlim slots = new(2, 2);

    public CostumeVisionService(IConfiguration configuration, IHostApplicationLifetime lifetime)
    {
        var key = configuration["OPENAI_API_KEY"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            provider = new CostumeImageClient(client, key,
                configuration["Backend:Vision:Model"] ?? ResponsesTextDialogBudget.Model);
        }
        Stopping = lifetime.ApplicationStopping;
    }

    internal CostumeVisionService(Func<byte[], CancellationToken, Task<CostumeImageResponse>> analyze)
    {
        this.analyze = analyze;
    }

    internal bool Available => provider is not null || analyze is not null;
    internal CancellationToken Stopping { get; }
    internal bool TryAcquire() => Available && slots.Wait(0);
    internal void Release() => slots.Release();
    internal Task<CostumeImageResponse> AnalyzeAsync(byte[] jpeg, CancellationToken cancellationToken) =>
        analyze is not null ? analyze(jpeg, cancellationToken)
            : provider is null ? throw new InvalidOperationException("vision_provider_unavailable")
                : provider.AnalyzeAsync(jpeg, cancellationToken);

    public void Dispose() => client.Dispose();
}

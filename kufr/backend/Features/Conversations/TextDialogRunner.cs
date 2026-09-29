using Kufr.Backend.Features.Usage;

namespace Kufr.Backend.Features.Conversations;

internal sealed class TextDialogRunner : IDisposable
{
    private readonly Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, Task<TextDialogResponse>>? generate;
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly HttpClient? httpClient;

    public TextDialogRunner(IConfiguration configuration, IHostApplicationLifetime lifetime)
    {
        var mode = configuration["Backend:Dialog:Mode"] ?? "fake";
        if (mode == "fake")
        {
            return;
        }

        if (mode != "responses")
        {
            throw new InvalidOperationException("Unsupported backend dialog mode.");
        }

        var apiKey = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey)
            || !CostLimits.Read(configuration).Enabled)
        {
            throw new InvalidOperationException(
                "Responses mode requires an API key and all three positive usage caps.");
        }

        httpClient = new HttpClient();
        var client = new ResponsesTextDialogClient(
            httpClient,
            apiKey,
            ResponsesTextDialogBudget.Model);
        generate = client.GenerateAsync;
        Stopping = lifetime.ApplicationStopping;
    }

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, Task<TextDialogResponse>> generate,
        CancellationToken stopping = default)
    {
        this.generate = generate;
        Stopping = stopping;
    }

    public bool Enabled => generate is not null;

    public CancellationToken Stopping { get; }

    public bool TryAcquire() => Enabled && slot.Wait(0);

    public Task<TextDialogResponse> GenerateAsync(
        string text,
        int readyMask,
        IReadOnlyList<DialogExchange> history,
        bool requireQuestion,
        CancellationToken cancellationToken)
    {
        return generate!(text, readyMask, history, requireQuestion, cancellationToken);
    }

    public void Release() => slot.Release();

    public void Dispose()
    {
        // Shutdown cancellation can still unwind a background request after
        // DI disposes this service; its finally block must be able to release.
        httpClient?.Dispose();
    }
}

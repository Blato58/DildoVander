
namespace Kufr.Backend.Features.Conversations;

internal sealed class TextDialogRunner : IDisposable
{
    private readonly Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, string, string, Task<TextDialogResponse>>? generate;
    private readonly ResponsesTextDialogClient? provider;
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly HttpClient? httpClient;
    private readonly Func<byte[], CancellationToken, Task<CostumeImageResponse>>? analyzeImage;
    private volatile bool enabled = true;

    public TextDialogRunner(IConfiguration configuration, IHostApplicationLifetime lifetime)
    {
        var mode = configuration["Backend:Dialog:Mode"] ?? "fake";
        if (mode is not ("fake" or "responses"))
        {
            throw new InvalidOperationException("Unsupported backend dialog mode.");
        }

        Model = configuration["Backend:Dialog:Model"] ?? ResponsesTextDialogBudget.Model;
        if (!ResponsesTextDialogBudget.IsSupportedModel(Model))
        {
            throw new InvalidOperationException("Only Sol is supported.");
        }

        var apiKey = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (mode == "fake")
            {
                return;
            }

            throw new InvalidOperationException(
                "Responses mode requires an API key.");
        }

        httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new ResponsesTextDialogClient(
            httpClient,
            apiKey,
            Model);
        provider = client;
        generate = (text, mask, history, question, token, context, personality) => client.GenerateAsync(text, mask, history, question, token, context, personality);
        analyzeImage = new CostumeImageClient(httpClient, apiKey, Model).AnalyzeAsync;
        enabled = mode == "responses";
        Stopping = lifetime.ApplicationStopping;
    }

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, Task<TextDialogResponse>> generate,
        CancellationToken stopping = default)
    {
        this.generate = (text, mask, history, question, token, _, _) => generate(text, mask, history, question, token);
        Stopping = stopping;
    }

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, string, Task<TextDialogResponse>> generate)
    {
        this.generate = (text, mask, history, question, token, context, _) => generate(text, mask, history, question, token, context);
    }

    internal TextDialogRunner(
        Func<string, int, IReadOnlyList<DialogExchange>, bool, CancellationToken, string, string, Task<TextDialogResponse>> generate,
        Func<byte[], CancellationToken, Task<CostumeImageResponse>>? analyzeImage = null)
    {
        this.generate = generate;
        this.analyzeImage = analyzeImage;
    }

    public string Model { get; } = ResponsesTextDialogBudget.Model;

    public bool Available => generate is not null;
    public bool Enabled => Available && enabled;
    internal void SetEnabled(bool value) => enabled = value;

    public CancellationToken Stopping { get; }

    public bool TryAcquire() => Enabled && slot.Wait(0);

    internal Task<CostumeImageResponse> AnalyzeImageAsync(byte[] jpeg, CancellationToken cancellationToken) =>
        analyzeImage is null
            ? throw new InvalidOperationException("Image provider unavailable.")
            : analyzeImage(jpeg, cancellationToken);

    public Task<TextDialogResponse> GenerateAsync(
        string text,
        int readyMask,
        IReadOnlyList<DialogExchange> history,
        bool requireQuestion,
        CancellationToken cancellationToken,
        string eveningContext = "",
        string personalityId = PersonalityCatalog.DefaultId,
        string costumeContext = "")
    {
        return provider is not null
            ? provider.GenerateAsync(text, readyMask, history, requireQuestion, cancellationToken, eveningContext, personalityId, costumeContext)
            : generate!(text, readyMask, history, requireQuestion, cancellationToken, eveningContext + costumeContext, personalityId);
    }

    public void Release() => slot.Release();

    public void Dispose()
    {
        // Shutdown cancellation can still unwind a background request after
        // DI disposes this service; its finally block must be able to release.
        httpClient?.Dispose();
    }
}

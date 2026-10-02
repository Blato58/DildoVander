using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.Usage;
using System.Security.Cryptography;

namespace Kufr.Backend.Features.VoiceTurns;

internal sealed class VoiceProviderRunner : IDisposable
{
    // Binary speech responses do not expose billable token usage. Retain these
    // conservative reservations even on success; never invent an actual charge.
    internal const long SpeechReservationMicroUsd = 50_000;
    internal const long TranscriptionReservationMicroUsd = 10_000;
    private readonly Func<byte[], CancellationToken, Task<string>>? transcribe;
    private readonly Func<string, SpeechOptions, CancellationToken, Task<byte[]>>? speak;
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly HttpClient? httpClient;
    private readonly TimeSpan timeout = TimeSpan.FromSeconds(15);
    private volatile bool enabled = true;

    public VoiceProviderRunner(
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILoggerFactory? loggerFactory = null)
    {
        var mode = configuration["Backend:Voice:Mode"] ?? "fake";
        if (mode is not ("fake" or "openai"))
        {
            throw new InvalidOperationException("Unsupported backend voice mode.");
        }

        var apiKey = configuration["OPENAI_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey) || !CostLimits.Read(configuration).Enabled)
        {
            if (mode == "fake")
            {
                return;
            }

            throw new InvalidOperationException("OpenAI voice requires an explicit mode, key and a positive session usage cap.");
        }

        httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        transcribe = new OpenAiTranscriptionClient(httpClient, apiKey).TranscribeAsync;
        speak = new OpenAiSpeechClient(
            httpClient,
            apiKey,
            loggerFactory?.CreateLogger<OpenAiSpeechClient>()).GenerateAsync;
        enabled = mode == "openai";
        Stopping = lifetime.ApplicationStopping;
    }

    internal VoiceProviderRunner(
        Func<byte[], CancellationToken, Task<string>> transcribe,
        Func<string, CancellationToken, Task<byte[]>> speak,
        CancellationToken stopping = default,
        TimeSpan? timeout = null)
    {
        this.transcribe = transcribe;
        this.speak = (reply, _, cancellationToken) => speak(reply, cancellationToken);
        Stopping = stopping;
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    internal VoiceProviderRunner(
        Func<byte[], CancellationToken, Task<string>> transcribe,
        Func<string, SpeechOptions, CancellationToken, Task<byte[]>> speak,
        CancellationToken stopping = default,
        TimeSpan? timeout = null)
    {
        this.transcribe = transcribe;
        this.speak = speak;
        Stopping = stopping;
        this.timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public bool Available => transcribe is not null && speak is not null;
    public bool Enabled => Available && enabled;
    internal void SetEnabled(bool value) => enabled = value;
    public CancellationToken Stopping { get; }
    public bool TryAcquire() => Enabled && slot.Wait(0);
    public void Release() => slot.Release();

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var transcript = await transcribe!(wav, deadline.Token);
        deadline.Token.ThrowIfCancellationRequested();
        return transcript;
    }

    public Task<byte[]> SpeakAsync(string reply, CancellationToken cancellationToken) =>
        SpeakAsync(reply, SpeechOptions.Default, cancellationToken);

    public async Task<byte[]> SpeakAsync(
        string reply,
        SpeechOptions options,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var wav = await speak!(reply, options, deadline.Token);
        if (deadline.IsCancellationRequested)
        {
            CryptographicOperations.ZeroMemory(wav);
            deadline.Token.ThrowIfCancellationRequested();
        }

        return wav;
    }

    public void Dispose() => httpClient?.Dispose();
}

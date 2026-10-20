using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;

namespace Kufr.Backend.Features.Show;

// Short authored show cues only. Conversation audio remains owned by Live/Realtime.
internal sealed class ShowSpeechProvider : IDisposable
{
    internal const long SpeechReservationMicroUsd = 50_000;
    private readonly HttpClient? http;
    private readonly SemaphoreSlim slot = new(1, 1);
    private readonly Func<bool> enabled;
    private readonly Func<bool> acquire;
    private readonly Action release;
    private readonly Func<string, SpeechOptions, CancellationToken, Task<byte[]>> speak;

    public ShowSpeechProvider(IConfiguration configuration, IHostApplicationLifetime lifetime,
        ILogger<ShowCueSpeechClient> logger)
    {
        var key = configuration["OPENAI_API_KEY"];
        enabled = () => !string.IsNullOrWhiteSpace(key);
        acquire = () => Enabled && slot.Wait(0);
        release = () => slot.Release();
        Stopping = lifetime.ApplicationStopping;
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var client = new ShowCueSpeechClient(http, key ?? "", logger);
        speak = client.GenerateAsync;
    }

    internal ShowSpeechProvider(VoiceProviderRunner fixture)
    {
        enabled = () => fixture.Enabled;
        acquire = fixture.TryAcquire;
        release = fixture.Release;
        speak = fixture.SpeakAsync;
        Stopping = fixture.Stopping;
    }

    internal bool Enabled => enabled();
    internal CancellationToken Stopping { get; }
    internal bool TryAcquire() => acquire();
    internal void Release() => release();
    internal Task<byte[]> SpeakAsync(string text, SpeechOptions options, CancellationToken token)
    {
        if (!Enabled) { throw new InvalidOperationException("Show narration is unavailable."); }
        return speak(text, options, token);
    }

    public void Dispose() => http?.Dispose();
}

using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

// One explicit diagnostic session: production transport/prompt, paced silence,
// no Responses calls, no stored audio or transcript. At most 35 seconds total.
internal static class GptLiveSmoke
{
    internal static async Task<int> RunAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var provider = new GptLiveClient(configuration);
        if (!provider.Available) { Console.WriteLine("GPT-Live smoke requires provider configuration."); return 1; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var counts = new Dictionary<string, int>();
        var audioBytes = 0;
        var speechBytes = 0;
        var transcriptCharacters = 0;
        var sentChunks = 0;
        var acknowledged = false;
        var finalized = false;
        double? seconds = null;
        string? failure = null;
        try
        {
            await using var connection = await provider.ConnectAsync(deadline.Token);
            var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
                PartyWandCatalog.Version, true, "", null, "cedar", 1);
            var turn = new RealtimeTurnContext(request, true, "", []);
            await connection.SendAsync(GptLiveProtocol.Start(turn), deadline.Token);
            using (var started = await connection.ReceiveAsync(deadline.Token))
            {
                if (started.RootElement.GetProperty("type").GetString() != "session.started")
                {
                    failure = ProviderCode(started.RootElement);
                    throw new InvalidDataException("startup");
                }
            }
            connection.AudioReady();
            await connection.SendAsync(new { type = "session.instructions.append", event_id = "kufr-opening",
                delegation_id = (string?)null, content = "Začni teď pozdravem a první otázkou podle úvodního námětu." }, deadline.Token);
            using var input = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            var uploading = Task.Run(async () =>
            {
                var silence = new byte[9600];
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
                while (await timer.WaitForNextTickAsync(input.Token))
                {
                    await connection.SendAudioAsync(silence, input.Token);
                    Interlocked.Increment(ref sentChunks);
                }
            }, input.Token);
            var clock = Stopwatch.StartNew();
            var closing = false;
            var closeAt = TimeSpan.Zero;
            Task<JsonDocument>? receive = null;
            try
            {
                while (!finalized)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!closing && clock.Elapsed >= TimeSpan.FromSeconds(20))
                    {
                        input.Cancel();
                        try { await uploading; } catch (OperationCanceledException) { }
                        await connection.CloseAsync(deadline.Token);
                        closing = true;
                        closeAt = clock.Elapsed;
                    }
                    if (closing && clock.Elapsed - closeAt > TimeSpan.FromSeconds(5))
                        throw new TimeoutException();
                    if (uploading.IsFaulted) await uploading;
                    receive ??= connection.ReceiveAsync(deadline.Token);
                    if (await Task.WhenAny(receive, Task.Delay(100, deadline.Token)) != receive) continue;
                    using var document = await receive;
                    receive = null;
                    var value = document.RootElement;
                    var type = value.GetProperty("type").GetString() ?? "unknown";
                    // Never print arbitrary provider text or exception messages.
                    var safeType = Regex.IsMatch(type, "^[a-z_.]{1,80}$") ? type : "unknown";
                    counts[safeType] = counts.GetValueOrDefault(safeType) + 1;
                    switch (type)
                    {
                        case "session.output_audio.delta":
                            var pcm = Convert.FromBase64String(value.GetProperty("delta").GetString()!);
                            audioBytes += pcm.Length;
                            if (GptLiveProtocol.HasSpeech(pcm)) speechBytes += pcm.Length;
                            break;
                        case "session.output_transcript.delta":
                            transcriptCharacters += value.GetProperty("delta").GetString()!.Length;
                            break;
                        case "session.instructions.appended": acknowledged = true; break;
                        case "session.closed":
                            finalized = true;
                            seconds = value.GetProperty("usage").GetProperty("seconds").GetDouble();
                            _ = GptLiveProtocol.VoiceCost(seconds.Value);
                            break;
                        case "error":
                            failure = ProviderCode(value);
                            break;
                    }
                }
            }
            finally
            {
                input.Cancel();
                try { await uploading; } catch (OperationCanceledException) { }
                deadline.Cancel();
                if (receive is not null)
                {
                    try { using var ignored = await receive; } catch (Exception) { }
                }
            }
        }
        catch (Exception error)
        {
            failure ??= error is OperationCanceledException or TimeoutException ? "diagnostic_timeout" : "diagnostic_failed";
        }
        Console.WriteLine(JsonSerializer.Serialize(new { counts, sentChunks, audioBytes, speechBytes,
            transcriptCharacters, acknowledged, finalized, seconds, failure }));
        return failure is null && finalized && acknowledged && speechBytes > 0 && transcriptCharacters > 0 ? 0 : 1;
    }

    private static string ProviderCode(JsonElement value) =>
        value.TryGetProperty("error", out var error) && error.TryGetProperty("code", out var code)
        && code.ValueKind == JsonValueKind.String && code.GetString() is { } text
        && Regex.IsMatch(text, "^[a-z_]{1,80}$") ? text : "provider_error";
}

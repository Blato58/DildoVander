using System.ClientModel;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

// Explicit paid transport diagnostic. No application conversation, guest data,
// archive, audio files, physical commands or automatic reconnect are involved.
internal static class PersonaProviderSoak
{
    internal static async Task<int> RunAsync(string secondsText)
    {
        if (!int.TryParse(secondsText, out var seconds) || seconds is < 60 or > 1800)
        {
            Console.WriteLine("Provider soak duration must be 60..1800 seconds.");
            return 2;
        }
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var provider = new OpenAiRealtimeClient(configuration);
        if (!provider.Available)
        {
            Console.WriteLine("Provider soak requires existing provider configuration.");
            return 2;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 10));
        var latency = new List<double>();
        long pcmBytes = 0;
        long estimatedCostMicroUsd = 0;
        var successful = 0;
        var failed = 0;
        var disconnects = 0;
        var connections = 0;
        double? connectionMs = null;
        string? failure = null;
        var boot = Guid.NewGuid();
        var sentences = new[]
        {
            "Toto je synteticky test. Rad opravuji stare hodiny. Zeptej se jednou kratkou vetou na dalsi detail.",
            "Jsem fiktivni host testu. Mam rad klid a trpelivost. Zeptej se jednou kratkou vetou na dalsi detail.",
            "V tomto syntetickem testu sbiram modre kaminky. Pokracuj jednou kratkou ceskou otazkou."
        };

        try
        {
            await using var connection = await provider.ConnectAsync(overall.Token);
            connections = 1;
            connectionMs = clock.Elapsed.TotalMilliseconds;
            for (var slot = 0; ; slot++)
            {
                var next = TimeSpan.FromSeconds(Math.Min(slot * 30, seconds));
                var wait = next - clock.Elapsed;
                if (wait > TimeSpan.Zero) { await Task.Delay(wait, overall.Token); }
                if (clock.Elapsed >= TimeSpan.FromSeconds(seconds)) { break; }

                using var turn = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
                var remaining = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
                turn.CancelAfter(remaining < TimeSpan.FromSeconds(25) ? remaining : TimeSpan.FromSeconds(25));
                var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 0, 63, 0,
                    PartyWandCatalog.Version, false, sentences[slot % sentences.Length], null, "marin", 1);
                var context = new RealtimeTurnContext(request, true, "", [], OutputTokenLimit: 256)
                {
                    InstructionOverride = "Jsi cesky hlasovy hostitel v syntetickem technickem testu. "
                        + "Odpovez vzdy jednou kratkou ceskou otazkou do dvaceti slov. "
                        + "Nevyvolavej zadne nastroje, nic nevybirej, neukoncuj rozhovor a netvrd zadny fyzicky pohyb."
                };
                var turnClock = Stopwatch.StartNew();
                try
                {
                    var reply = await connection.GenerateTurnAsync(context, null,
                        (pcm, _) => { pcmBytes += pcm.Length; return Task.CompletedTask; },
                        (_, _) => Task.CompletedTask, turn.Token);
                    successful++;
                    latency.Add(turnClock.Elapsed.TotalMilliseconds);
                    estimatedCostMicroUsd = checked(estimatedCostMicroUsd + reply.CostMicroUsd);
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        type = "persona_provider_soak_progress", model = RealtimeProtocol.Model,
                        elapsedSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3),
                        successfulTurns = successful, failedTurns = failed,
                        lastTurnMs = Math.Round(turnClock.Elapsed.TotalMilliseconds, 3), pcmBytes,
                        estimatedCostMicroUsd
                    }));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    failed++;
                    failure = FailureCode(error);
                    if (error is WebSocketException) { disconnects = 1; }
                    break;
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure ??= FailureCode(error);
            if (error is WebSocketException) { disconnects = 1; }
        }
        clock.Stop();
        latency.Sort();
        var p95 = latency.Count == 0 ? (double?)null : latency[(int)Math.Ceiling(latency.Count * 0.95) - 1];
        var passed = failure is null && failed == 0 && successful >= seconds / 30
            && clock.Elapsed >= TimeSpan.FromSeconds(seconds) && connections == 1;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            type = "persona_provider_soak_result", evidence = "paid_provider_transport_only",
            model = RealtimeProtocol.Model, requestedSeconds = seconds, startedAt,
            durationSeconds = Math.Round(clock.Elapsed.TotalSeconds, 3), connections,
            automaticReconnects = 0, successfulTurns = successful, failedTurns = failed, disconnects,
            connectionMs, averageTurnMs = latency.Count == 0 ? (double?)null : Math.Round(latency.Average(), 3),
            p95TurnMs = p95 is null ? (double?)null : Math.Round(p95.Value, 3), pcmBytes,
            outputAudioSeconds = Math.Round(pcmBytes / 48_000d, 3),
            estimatedCostMicroUsd, costEvidence = "application_conservative_estimate_from_provider_usage_not_billing_receipt",
            failure, passed
        }));
        return passed ? 0 : 1;
    }

    private static string FailureCode(Exception error) => error switch
    {
        OperationCanceledException or TimeoutException => "diagnostic_timeout",
        WebSocketException => "provider_transport_lost",
        ClientResultException { Status: 401 or 403 } => "provider_access_denied",
        ClientResultException { Status: 429 } => "provider_capacity",
        ClientResultException => "provider_request_failed",
        InvalidDataException => "provider_protocol_invalid",
        JsonException => "provider_event_invalid",
        _ => "diagnostic_failed"
    };
}

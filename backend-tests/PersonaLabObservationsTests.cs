using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.PersonaLab;
using SkiaSharp;

internal static class PersonaLabObservationsTests
{
    internal static void Run()
    {
        var now = DateTimeOffset.UtcNow;
        var config = new LabRevision("revision", null, "Test", "Czech persona", "gpt-live-1", "cedar",
            "draft", now, null, 1, "hash", "source");
        var empty = LabAnalytics.Reliability([]);
        Check(empty.EstimatedCostUsd is null && empty.P95ResponseMs is null && empty.RecoveryRate is null,
            "missing observations remain unknown");
        var session = new LabSession("session", "owner", "Test", "gpt-live-1", "first-visit", "empty", null,
            now, now.AddSeconds(60), "ended", config,
            Enumerable.Range(1, 20).Select(i => new LabTurn(i.ToString(), "assistant", "reply", now, i * 10)).ToArray(),
            [LabContract.Event("connection_started"), LabContract.Event("connection_established"),
                LabContract.Event("provider_turn", "completed"),
                LabContract.Event("provider_cost", "micro_usd", costMicroUsd: 1000),
                LabContract.Event("provider_cost", "micro_usd", costMicroUsd: 2000)], null, 1, false);
        var measured = LabAnalytics.Reliability([session]);
        Check(measured.P95ResponseMs == 190 && measured.AverageResponseMs == 105 && measured.LatencySamples == 20,
            "nearest rank p95 uses measured response samples");
        Check(measured.EstimatedCostUsd == .003m && measured.CostMeasuredSessions == 1,
            "incremental cost sums across provider connections");
        Check(measured.AverageDurationSeconds == 60 && measured.SuccessfulSessions == 1
            && measured.UnexpectedDisconnectionRate == 0, "observed session success and duration");
        var failed = session with { Id = "failed", Events = [LabContract.Event("provider_failure", "transport_error"),
            LabContract.Event("connection_interrupted"), LabContract.Event("recovery_started")] };
        var failure = LabAnalytics.Reliability([failed]);
        Check(failure.FailedSessions == 1 && failure.RecoveryRate == 0
            && failure.UnexpectedDisconnectionRate is null, "missing connection denominator is not fabricated");
        Check(measured.InputTokens is null && measured.OutputTokens is null && measured.TokenMeasuredTurns == 0,
            "cost and audio observations do not invent token usage");
        var tokenSession = session with { Events = [LabContract.Event("provider_tokens", "observed", inputTokens: 0, outputTokens: 2),
            LabContract.Event("provider_tokens", "observed", inputTokens: 3, outputTokens: 4),
            LabContract.Event("provider_tokens", "unknown")] };
        var tokens = LabAnalytics.Reliability([tokenSession]);
        Check(tokens.InputTokens == 3 && tokens.OutputTokens == 6 && tokens.TokenMeasuredTurns == 2,
            "observed zero and incremental per-turn token counts remain distinct from missing usage");

        var clock = new AdjustableTimeProvider(now);
        using var images = new PersonaLabImages(clock);
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.Gray);
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, 75);
        var data = encoded.ToArray();
        Check(CostumeJpeg.IsValid(data), "test jpeg follows existing metadata-free contract");
        var request = new LabImageRequest("image/jpeg", Convert.ToBase64String(data), true);
        images.Put("owner", "session", request);
        Check(images.Read("other", "session") is null && !images.HasImage("other", "session"), "image owner boundary");
        var read = images.Read("owner", "session")!;
        read.Jpeg[0] = 0;
        Check(images.Read("owner", "session")!.Jpeg[0] == 0xff, "provider receives a copy of volatile image");
        try { images.Put("owner", "session", request with { Consent = false }); throw new Exception("Consent bypass"); }
        catch (LabException error) when (error.Status == 400) { }
        clock.Advance(TimeSpan.FromMinutes(16));
        Check(images.Read("owner", "session") is null && !images.HasImage("owner", "session"), "image expires in RAM");
        images.Put("owner", "session", request);
        images.Clear("owner", "session");
        Check(!images.HasImage("owner", "session"), "explicit consent withdrawal removes image");
        Console.WriteLine("Persona Lab observations: analytics provenance, p95, incremental cost and volatile image boundaries passed.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}

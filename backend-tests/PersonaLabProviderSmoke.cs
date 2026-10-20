using System.Diagnostics;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

// Explicit paid diagnostic. Isolated Lab storage and fictional inputs only.
internal static class PersonaLabProviderSmoke
{
    internal static async Task<int> RunAsync()
    {
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        using var live = new GptLiveClient(configuration);
        using var realtime = new OpenAiRealtimeClient(configuration);
        if (!live.Available || !realtime.Available) { Console.WriteLine("Provider configuration unavailable."); return 2; }
        var root = Path.Combine(Path.GetTempPath(), "kufr-lab-provider-" + Guid.NewGuid().ToString("N"));
        var baseline = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions());
        var store = new PersonaLabStore(Path.Combine(root, "isolated.sqlite"), baseline);
        using var images = new PersonaLabImages(TimeProvider.System);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        using var runtime = new PersonaLabRuntime(store, live, realtime, images);
        var failed = 0;
        foreach (var model in new[] { GptLiveProtocol.Model, RealtimeProtocol.Model })
        {
            var session = store.CreateSession("synthetic-smoke", new(store.ActiveRevisionId, model,
                "first-visit", "returning", "Explicit provider smoke", RecordContent: false));
            var timer = Stopwatch.StartNew();
            string? failure = null;
            try
            {
                session = await runtime.TurnAsync(session.OwnerId, session.Id,
                    "Jsem fiktivni knihovnik Ada a hledam hulku. Zeptej se me jednou kratkou otazkou.", deadline.Token);
                session = await runtime.TurnAsync(session.OwnerId, session.Id,
                    "Pracuju v knihovne. Jakou praci jsem ti rekl v minulem tahu? Odpovez kratce.", deadline.Token);
                if (session.Turns.Count(turn => turn.Role == "assistant" && turn.Text.Length > 0) != 2)
                {
                    throw new LabException(502, "lab_smoke_missing_turn");
                }
            }
            catch (LabException error) { failure = error.Code; failed++; }
            catch (OperationCanceledException) { failure = "diagnostic_timeout"; failed++; }
            catch (Exception) { failure = "diagnostic_failed"; failed++; }
            finally
            {
                session = runtime.Detail(session.OwnerId, session.Id);
                runtime.End(session.OwnerId, session.Id);
            }
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                type = "persona_lab_provider_smoke", evidence = "paid_lab_runtime_not_browser_audio",
                model, version = session.Config.Id, promptSha256 = session.Config.Sha256,
                elapsedSeconds = Math.Round(timer.Elapsed.TotalSeconds, 3),
                assistantTurns = session.Turns.Count(turn => turn.Role == "assistant"),
                transcriptCharacters = session.Turns.Sum(turn => turn.Text.Length),
                summary = LabAnalytics.Reliability([session]), failure, passed = failure is null
            }));
        }
        // This private diagnostic directory contains no recorded content and is
        // removed only after every provider lifetime has been awaited.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(root, true);
        return failed == 0 ? 0 : 1;
    }
}

using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Wands;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class WandGenerationTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "generated-wands-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(directory, "wand.sqlite") }).Build();
            var clock = new Clock();
            var store = new WandGenerationStore(config, clock);
            var profile = Profile();
            var first = store.Begin("alice", "selection-a", profile, 0b100101, true, ["Permitted fictional evidence"]);
            Check(first.Status == "queued" && first.Candidates.Length == 0, "begin persists without provider work");
            Check(store.Begin("alice", "selection-a", profile with { CapturedAt = 5 }, 0b100101, true,
                ["Permitted fictional evidence"]).SessionId == first.SessionId, "idempotent operation ignores capture clock");
            Expect("wand_operation_conflict", () => store.Begin("alice", "selection-a", profile, 63, true));
            Expect("openai_consent_required", () => store.Begin("alice", "no-consent", profile, 63, false));
            Check(store.Read("bob", "selection-a") is null, "owner-scoped reads");

            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var provider = new Fake(async (job, token) =>
            {
                Check(job.Request.Evidence.SequenceEqual(new[] { "Ephemeral private constellation" }),
                    "fresh permitted evidence reaches only the claimed provider request");
                Interlocked.Increment(ref calls); started.SetResult();
                await release.Task.WaitAsync(token); return Candidates(job.Request.ReadyMask);
            });
            var service = new WandGenerationService(store, provider);
            var generation = service.GenerateAsync("alice", "selection-a", Allowed, evidence: ["Ephemeral private constellation"]);
            await started.Task;
            var secondStore = new WandGenerationStore(config, clock);
            var duplicate = await new WandGenerationService(secondStore, provider)
                .GenerateAsync("alice", "selection-a", Allowed);
            Check(duplicate.Status == "running" && calls == 1, "concurrent instance cannot dispatch duplicate provider request");
            release.SetResult();
            var ready = await generation;
            Check(ready.Status == "ready" && ready.Candidates.Select(wand => wand.PhysicalSlot).SequenceEqual(new[] { 1, 3, 6 }),
                "one server-mapped candidate for each eligible physical slot");
            Check(ready.Candidates.Select(wand => wand.WandId).Distinct().Count() == 3
                && ready.Candidates.All(wand => wand.ProfileId == profile.ProfileId && wand.ProfileRevision == 1),
                "server identities are distinct and bound to immutable character revision");
            Expect("wand_profile_revision_mismatch", () => store.Select("alice", "selection-a", profile.ProfileId, 2, 1));
            Expect("wand_compartment_unavailable", () => store.Select("alice", "selection-a", profile.ProfileId, 1, 3, 1));
            var selected = store.Select("alice", "selection-a", profile.ProfileId, 1, 3, 0b100101);
            Check(secondStore.LatestBonded("alice", profile.ProfileId, 1) is null,
                "selected wand is not a completed bond when optional keepsake tables do not exist");
            Check(secondStore.Read("alice", "selection-a")?.SelectedWand?.WandId == selected.WandId, "selection survives another store instance");
            Check(secondStore.LatestSelected("alice", profile.ProfileId, 1)?.WandId == selected.WandId
                && secondStore.LatestSelected("alice", profile.ProfileId, 2) is null
                && secondStore.LatestSelected("bob", profile.ProfileId, 1) is null,
                "profile assignment projection requires owner and exact character revision");
            Expect("wand_selection_locked", () => store.Select("alice", "selection-a", profile.ProfileId, 1, 6));
            Check((await service.GenerateAsync("alice", "selection-a", Allowed)).SelectedWand?.WandId == selected.WandId
                && calls == 1, "reopening selected assignment never regenerates");
            var publicJson = JsonSerializer.Serialize(selected.ToPublic(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Check(!publicJson.Contains("profileId") && !publicJson.Contains("sessionId") && !publicJson.Contains("profileRevision"),
                "public projection excludes private binding identifiers");

            store.Begin("alice", "selection-b", profile, 0b100101, true);
            var fresh = await new WandGenerationService(store, new Fake((job, _) => Task.FromResult(Candidates(job.Request.ReadyMask))))
                .GenerateAsync("alice", "selection-b", Allowed);
            Check(!fresh.Candidates.Select(wand => wand.WandId).Intersect(ready.Candidates.Select(wand => wand.WandId)).Any(),
                "new selection creates distinct wand identities even with identical physical slots");
            var bob = store.Begin("bob", "selection-a", profile with { ProfileId = "bob-character" }, 1, true);
            var bobResult = await new WandGenerationService(store, new Fake((job, _) => Task.FromResult(Candidates(job.Request.ReadyMask))))
                .GenerateAsync("bob", "selection-a", Allowed);
            Check(bob.SessionId != first.SessionId && bobResult.Candidates[0].WandId != ready.Candidates[0].WandId,
                "same slot and operation text cannot merge different accounts");

            store.Begin("alice", "deleted-operation", profile, 1, true);
            store.DeleteOperation("alice", "deleted-operation");
            Check(store.HasOperation("alice", "deleted-operation") && !store.HasOperation("bob", "deleted-operation"),
                "revoked generated operation remains distinguishable from a legacy session within owner scope");
            Expect("wand_identity_revoked", () => store.Begin("alice", "deleted-operation", profile, 1, true));

            await FailuresAndRecovery(store, config, profile, clock);
            using (var evidenceConnection = new SqliteConnection("Data Source=" + config["Backend:DatabasePath"]))
            {
                evidenceConnection.Open();
                using var query = evidenceConnection.CreateCommand();
                query.CommandText = "SELECT request_json FROM wand_generation_sessions WHERE account_id='alice' AND operation_id='selection-a'";
                var retained = (string)query.ExecuteScalar()!;
                Check(!retained.Contains("Permitted fictional evidence") && !retained.Contains("Ephemeral private constellation"),
                    "revocable conversation facts are never retained in generation requests or retries");
            }
            SchemaAndValidation(profile);
            store.DeleteProfile("alice", profile.ProfileId);
            Check(store.Read("alice", "selection-a") is null && store.Read("alice", "selection-b") is null,
                "profile revocation scrubs generated candidates and selected wand");
            Expect("wand_identity_revoked", () => store.Begin("alice", "resurrect", profile, 1, true));
            Check(store.Read("bob", "selection-a")?.Candidates.Length == 1, "deletion has no cross-account effect");
            store.DeleteAccount("bob");
            Expect("wand_identity_revoked", () => store.Begin("bob", "new", profile with { ProfileId = "new-id" }, 1, true));
            Console.WriteLine("Generated wand schema, ownership, immutable binding, concurrency, cancellation, recovery and privacy tests passed.");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static async Task FailuresAndRecovery(WandGenerationStore store, IConfiguration config,
        CharacterConversationSnapshot profile, Clock clock)
    {
        store.Begin("alice", "outage", profile, 1, true);
        var unavailable = await new WandGenerationService(store, new Fake((_, _) => throw new HttpRequestException("synthetic")))
            .GenerateAsync("alice", "outage", Allowed);
        Check(unavailable.Status == "unavailable" && unavailable.Candidates.Length == 0, "provider outage is explicit without legacy fallback");
        var restored = await new WandGenerationService(store, new Fake((job, _) => Task.FromResult(Candidates(job.Request.ReadyMask))))
            .GenerateAsync("alice", "outage", Allowed, retry: true);
        Check(restored.Status == "ready", "explicit retry recovers failed generation");
        store.Begin("alice", "restart", profile, 1, true);
        var abandoned = store.Claim("alice", "restart", false)!;
        store.RecordUsage(abandoned, "fake-model", "fake-prompt-v1", 0, 0, usageReported: false);
        clock.Advance(91);
        var restarted = new WandGenerationStore(config, clock);
        Check(restarted.Read("alice", "restart")?.ErrorCode == "generation_interrupted", "expired lease exposes interruption after restart");
        var recovered = restarted.Claim("alice", "restart", true)!;
        restarted.RecordUsage(recovered, "fake-model", "fake-prompt-v1", 20, 30);
        restarted.RecordUsage(recovered, "fake-model", "fake-prompt-v1", 20, 30);
        Expect("wand_generation_stale", () => store.Complete(abandoned, Candidates(1)));
        restarted.Complete(recovered, Candidates(1));
        Check(store.Read("alice", "restart")?.Status == "ready", "only current recovered lease commits");
        using (var auditConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = config["Backend:DatabasePath"] }.ToString()))
        {
            auditConnection.Open();
            using var audit = auditConnection.CreateCommand();
            audit.CommandText = "SELECT COUNT(*),SUM(input_tokens),SUM(output_tokens),SUM(usage_reported) FROM wand_generation_usage WHERE session_id=$session";
            audit.Parameters.AddWithValue("$session", abandoned.SessionId);
            using var reader = audit.ExecuteReader();
            reader.Read();
            Check(reader.GetInt32(0) == 2 && reader.GetInt32(1) == 20 && reader.GetInt32(2) == 30 && reader.GetInt32(3) == 1,
                "provider audit records distinct attempts, unknown interrupted usage and replay-safe acknowledged tokens");
        }

        store.Begin("alice", "stale-profile", profile, 1, true);
        var dispatches = 0;
        var staleService = new WandGenerationService(store, new Fake((job, _) =>
        { dispatches++; return Task.FromResult(Candidates(job.Request.ReadyMask)); }));
        var stale = await staleService.GenerateAsync("alice", "stale-profile", _ => Task.FromResult(false));
        Check(stale.Status == "unavailable" && dispatches == 0, "current authority rejects stale profile before paid work");
        store.Begin("alice", "late-profile", profile, 1, true);
        var checks = 0;
        var late = await staleService.GenerateAsync("alice", "late-profile", _ => Task.FromResult(++checks == 1));
        Check(late.Status == "unavailable" && late.Candidates.Length == 0, "authority rechecked after provider before binding");

        var cancelled = new CancellationTokenSource();
        store.Begin("alice", "cancel", profile, 1, true);
        var cancelService = new WandGenerationService(store, new Fake(async (_, token) =>
        { cancelled.Cancel(); await Task.Delay(1000, token); return []; }));
        try { await cancelService.GenerateAsync("alice", "cancel", Allowed, cancelled.Token); throw new Exception("Cancellation lost"); }
        catch (OperationCanceledException) { }
        Check(store.Read("alice", "cancel")?.ErrorCode == "generation_cancelled", "cancellation persists retryable state");

        store.Begin("alice", "revoked-in-flight", profile with { ProfileId = "revoked-profile" }, 1, true);
        var revokeService = new WandGenerationService(store, new Fake((job, _) =>
        { store.DeleteProfile("alice", "revoked-profile"); return Task.FromResult(Candidates(job.Request.ReadyMask)); }));
        try { await revokeService.GenerateAsync("alice", "revoked-in-flight", Allowed); }
        catch (WandGenerationException exception) when (exception.Code == "wand_session_unavailable") { }
        Check(store.Read("alice", "revoked-in-flight") is null, "late result cannot restore revoked personal data");
    }

    private static void SchemaAndValidation(CharacterConversationSnapshot profile)
    {
        var request = new WandGenerationRequest("private-account", "private-operation", profile, 1, true, []);
        var body = OpenAiWandGenerationProvider.BuildRequest(request, "fake-model");
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Check(!root.GetProperty("store").GetBoolean() && root.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean(),
            "strict structured outputs and store=false");
        Check(!body.Contains("private-account") && !body.Contains("private-operation") && !body.Contains(profile.ProfileId),
            "provider receives fictional context without authorization or ownership ids");
        var valid = Candidates(1);
        WandGenerationValidation.Validate(valid, 1);
        Invalid(() => WandGenerationValidation.Validate(valid, 3));
        Invalid(() => WandGenerationValidation.Validate([valid[0] with { Name = new string('x', 101) }], 1));
        Invalid(() => WandGenerationValidation.Validate([valid[0] with { Lore = "control\u0001text" }], 1));
        Invalid(() => WandGenerationValidation.Validate([valid[0] with { Name = "Poslední slovo" }], 1));
        Invalid(() => WandGenerationValidation.Validate([valid[0] with { Name = "Jasanova hulka Posledni slovo" }], 1));
        Invalid(() => WandGenerationValidation.Validate([valid[0] with { Name = "123 ---" }], 1));
        var pair = Candidates(3);
        Invalid(() => WandGenerationValidation.Validate([pair[0] with { Name = "Astra1" }, pair[1] with { Name = "Astra2" }], 3));
        Invalid(() => WandGenerationValidation.Validate([pair[0] with { Name = "Dubová hůlka Jiskra" }, pair[1] with { Name = "Zlatá hůlka Jiskra" }], 3));
        Invalid(() => WandGenerationValidation.Validate([valid[0] with { Spells = [valid[0].Spells[0], valid[0].Spells[0], valid[0].Spells[2]] }], 1));
        var speechPair = Candidates(3);
        Invalid(() => WandGenerationValidation.Validate([speechPair[0] with { Name = "Astra" }, speechPair[1] with { Name = "Stará Astra" }], 3));
        Invalid(() => WandGenerationValidation.Validate([speechPair[0] with { Name = "Hvězda" }, speechPair[1] with { Name = "Hvězdy" }], 3));
        var response = Response(valid);
        using var parsed = JsonDocument.Parse(response);
        Check(OpenAiWandGenerationProvider.Parse(parsed.RootElement, 1)[0].Name == valid[0].Name, "structured result semantic parse");
        var injectedText = JsonSerializer.Serialize(new { candidates = valid }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .Replace("\"name\":\"Wand Amber\"", "\"name\":\"Wand Amber\",\"hardwareCommand\":\"open\"");
        using var injected = JsonDocument.Parse(ResponseText(injectedText));
        try { OpenAiWandGenerationProvider.Parse(injected.RootElement, 1); throw new Exception("Unexpected provider field accepted"); }
        catch (JsonException) { }
    }

    private static string Response(WandCandidateContent[] candidates)
    {
        var text = JsonSerializer.Serialize(new { candidates }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return ResponseText(text);
    }
    private static string ResponseText(string text) =>
        JsonSerializer.Serialize(new { status = "completed", output = new[] { new { type = "message",
            content = new[] { new { type = "output_text", text } } } } });
    private static CharacterConversationSnapshot Profile() => new("private-profile", 1,
        new("Raven", "Original", Description: "Fictional astronomer", Language: "cs"), null, 1);
    private static WandCandidateContent[] Candidates(int mask) => WandGenerationValidation.Slots(mask).Select((_, index) =>
        new WandCandidateContent("Wand " + new[] { "Amber", "Violet", "Emerald", "Silver", "Scarlet", "Azure" }[index], "Moonwood", "Imagined ember", "Curious", "A fictional starlit history",
            "Digital constellation design", "Stars", "Impatience", new[] { "strike", "trick", "shield" }
                .Select(action => new WandSpell(action, $"{index} {action} constellation", "Fictional magical effect")).ToArray())).ToArray();
    private static Task<bool> Allowed(CancellationToken _) => Task.FromResult(true);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect(string code, Action action)
    { try { action(); throw new InvalidOperationException("Expected " + code); } catch (WandGenerationException exception) when (exception.Code == code) { } }
    private static void Invalid(Action action)
    { try { action(); throw new InvalidOperationException("Expected invalid generation"); } catch (InvalidDataException) { } }
    private sealed class Fake(Func<WandGenerationJob, CancellationToken, Task<WandCandidateContent[]>> generate) : IWandGenerationProvider
    { public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken cancellationToken) => generate(job, cancellationToken); }
    private sealed class Clock : TimeProvider
    { private DateTimeOffset now = DateTimeOffset.Parse("2026-10-24T12:00:00Z"); public override DateTimeOffset GetUtcNow() => now; internal void Advance(int seconds) => now = now.AddSeconds(seconds); }
}

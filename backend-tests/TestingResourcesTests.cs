using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Extensions.Configuration;

internal static class TestingResourcesTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "testing-resources-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var originalPath = Path.Combine(root, "production.sqlite");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            {
                ["Backend:DatabasePath"] = originalPath,
                ["Backend:Memory:CloudIndexingEnabled"] = "true",
                ["Backend:Memory:PersonalStoreId"] = "vs_production_personal",
                ["Backend:Memory:MotifStoreId"] = "vs_production_motifs",
                ["Keepsake:ProviderMode"] = "fake",
                ["Devices:0:CredentialSha256"] = "production-device-secret-hash",
                ["Keepsake:DataRoot"] = Path.Combine(root, "production-assets")
            }).Build();
            var doubleProvider = new EnrichmentDouble();
            using (var resources = new TestingResources(configuration, TimeProvider.System, null, null, doubleProvider, null))
            {
                Check(resources.Root == originalPath + ".testing", "test resource root is separate from production");
                Check(resources.Configuration["Keepsake:ProviderMode"] == "openai", "live composition never inherits fake provider mode");
                Check(resources.Configuration["Devices:0:CredentialSha256"] is null, "physical device credential configuration never enters virtual composition");
                Check(resources.HybridMemory.Options.Namespace == "kufr-testing:Testing"
                    && !resources.HybridMemory.Options.CloudIndexingEnabled
                    && resources.HybridMemory.Options.PersonalStoreId == ""
                    && resources.HybridMemory.Options.MotifStoreId == "", "test memory cannot access production vector stores");
                var a = resources.Profiles("alice", "character-one");
                var b = resources.Profiles("alice", "character-two");
                var other = resources.Profiles("bob", "character-one");
                var saved = await a.SaveAsync("alice", new(0, new("Marta", "Library", Description:"Repairs clocks"), OpenAiConsent:true));
                Check(await b.ReadAsync("alice") is null && await other.ReadAsync("bob") is null,
                    "multiple characters and account identities have independent production profile stores");
                Check(await resources.ProcessNextCharacterEnrichmentAsync(CancellationToken.None), "isolated worker claims authorized queued work");
                var enriched = await a.ReadAsync("alice");
                Check(doubleProvider.Calls == 1 && enriched?.Status == "ready" && enriched.Enrichment?.InputRevision == saved.Revision,
                    "queued worker persists actual adapter result with revision binding");
                await b.SaveAsync("alice", new(0, new("Without consent", "Library")));
                Check(!await resources.ProcessNextCharacterEnrichmentAsync(CancellationToken.None) && doubleProvider.Calls == 1,
                    "saving without consent never dispatches provider work");
                try
                {
                    await resources.GenerateKeepsakeAsync(Guid.NewGuid().ToString("D"), CancellationToken.None);
                    throw new Exception("Missing real provider key returned success.");
                }
                catch (KeepsakeException error) { Check(error.Code == "test_artifact_provider_key_missing", "missing provider credential returns explicit failure before dispatch"); }
                Check(!File.Exists(originalPath) && !Directory.Exists(Path.Combine(root,"production-assets")),
                    "isolated graph creates no production database or assets");
            }
            using (var resumed = new TestingResources(configuration, TimeProvider.System))
            {
                Check((await resumed.Profiles("alice", "character-one").ReadAsync("alice"))?.Status == "ready", "profile and enrichment persist after resource restart");
                var profile = resumed.Profiles("alice", "failure-case");
                await profile.SaveAsync("alice", new(0, new("No key", "Library"), OpenAiConsent:true));
                Check(await resumed.ProcessNextCharacterEnrichmentAsync(CancellationToken.None), "worker records real provider configuration failure");
                var failed = await profile.ReadAsync("alice");
                Check(failed?.Status == "failed" && failed.ErrorCode == "character_provider_unavailable",
                    "failed provider remains visible without creating enriched output");
            }
            using (var generated = new TestingResources(configuration, TimeProvider.System, null, new FakeKeepsakeProvider(), null, null))
            {
                var first = generated.Keepsakes.Issue(new(Guid.NewGuid().ToString("D"), "virtual-first", 1,
                    "fixture-catalog", "Fixture wand", "Fixture facts", "simulated physical evidence"), new("alice", "first-test"));
                var second = generated.Keepsakes.Issue(new(Guid.NewGuid().ToString("D"), "virtual-second", 2,
                    "fixture-catalog", "Fixture wand", "Fixture facts", "simulated physical evidence"), new("bob", "second-test"));
                await generated.GenerateKeepsakeAsync(first.Id, CancellationToken.None);
                var firstView = generated.Keepsakes.Read(first.Id, KeepsakeActor.ForAccount("alice", "fixture", () => true));
                var secondView = generated.Keepsakes.Read(second.Id, KeepsakeActor.ForAccount("bob", "fixture", () => true));
                Check(firstView.Editions.Count == 1 && secondView.Editions.Count == 0
                    && secondView.Jobs.All(job => job.State == "queued"),
                    "explicit generation uses the requested archive's production job pipeline and does not drain another account's work");
            }
            Console.WriteLine("Testing resource composition, isolation, enrichment and provider failure checks passed.");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
    private static void Check(bool condition,string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class EnrichmentDouble : ICharacterEnrichmentProvider
    {
        internal int Calls;
        public Task<CharacterEnrichment> EnrichAsync(CharacterJob job,CancellationToken token)
        {
            Calls++; token.ThrowIfCancellationRequested();
            return Task.FromResult(new CharacterEnrichment(job.UserInput.Name,job.UserInput.World,"Controlled offline fixture result",[],[],[],false,[],
                "controlled-double","test-v1",DateTimeOffset.UtcNow.ToUnixTimeSeconds(),job.Revision,"synthetic"));
        }
    }
}

using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.TestingSessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class TestingDiagnosticsTests
{
    internal static async Task RunAsync()
    {
        foreach (var outcome in new[] { "success", "invalid", "transport" })
        {
            var root = Path.Combine(Path.GetTempPath(), "testing-diagnostics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:DatabasePath"] = Path.Combine(root, "backend.sqlite")
                }).Build();
                var logs = new ServerLogStore(config, TimeProvider.System);
                using var resources = new TestingResources(config, TimeProvider.System, null, null, new Provider(outcome), null, logs);
                const string owner = "private-owner";
                var id = Guid.NewGuid().ToString("N");
                var profiles = resources.Profiles(owner, id);
                var profile = await profiles.SaveAsync(owner, new(0, new("private-character", "private-world"), OpenAiConsent: true));
                var trace = Guid.NewGuid().ToString();
                var operation = resources.TraceCharacterRequest(owner, new(id, owner, profile, false), trace);
                Require(Guid.TryParseExact(operation, "D", out _), "originating request uses the log reader's canonical durable job ID format");
                Require(await resources.ProcessNextCharacterEnrichmentAsync(default), "queued job is processed");
                var current = (await profiles.ReadAsync(owner))!;
                var page = logs.Read(null, 50, "info", null, operation);
                Require(page.Entries[0].EventCode == "testing_character_job_requested" && page.Entries[0].TraceId == trace,
                    "request trace remains discoverable by durable job ID");
                Require(page.Entries.Any(entry => entry.EventCode == "testing_character_job_started" && entry.Attempt == 1),
                    "worker start carries attempt and exact job correlation");
                var last = page.Entries[^1];
                if (outcome == "success")
                {
                    Require(current.Status == "ready" && last.EventCode == "testing_character_job_completed", "successful result is persisted and diagnosed");
                }
                else if (outcome == "invalid")
                {
                    Require(current.Status == "failed" && current.ErrorCode == "character_provider_invalid_response"
                        && last.Code == current.ErrorCode && last.EventCode == "testing_character_job_failed",
                        "invalid provider output retains a precise terminal failure");
                }
                else
                {
                    Require(current.Status == "queued" && last.EventCode == "testing_character_job_retry_scheduled"
                        && last.Code == "character_provider_transport_error", "transport failure retains a durable retry with safe reason");
                    Require(!await resources.ProcessNextCharacterEnrichmentAsync(default), "retry respects existing backoff");
                }
                Require(!JsonSerializer.Serialize(page).Contains("private", StringComparison.Ordinal), "diagnostics exclude inputs, identities and exception messages");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(root, true);
            }
        }
        Console.WriteLine("Testing provider diagnostics preserve job correlation, safe failure codes and retry behavior (offline).");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private sealed class Provider(string outcome) : ICharacterEnrichmentProvider
    {
        public Task<CharacterEnrichment> EnrichAsync(CharacterJob job, CancellationToken cancellationToken)
        {
            if (outcome == "invalid") { throw new InvalidDataException("private-response"); }
            if (outcome == "transport") { throw new HttpRequestException("private-provider-address"); }
            return Task.FromResult(new CharacterEnrichment(job.UserInput.Name, job.UserInput.World, "private-summary",
                [], [], [], false, [], "offline", "offline-v1", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), job.Revision, "synthetic"));
        }
    }
}

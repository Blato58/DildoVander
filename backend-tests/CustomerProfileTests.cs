using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

internal static class CustomerProfileTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-character-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            await PersistenceAndFencesAsync(root);
            await OpenAiConsentAsync(Path.Combine(root, "consent"));
            ProviderEvidence();
            await AccountIdentityTests.RunCustomerRegistrationAsync();
            Console.WriteLine("Customer profile revision, job, deletion and source-evidence tests passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) { Directory.Delete(root, true); }
        }
    }

    private static async Task PersistenceAndFencesAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var input = new CharacterUserInput("  Raven  ", "Original", Description: "My own invented character.");
        var first = await store.SaveAsync("alice", new(0, input, OpenAiConsent: true));
        Check(first.Revision == 1 && first.Status == "queued" && first.UserInput == input, "exact input and persistent queued save");
        var restarted = new CharacterProfileStore(root);
        Check((await restarted.ReadAsync("alice"))?.ProfileId == first.ProfileId, "profile survives store restart");
        Check(await restarted.ReadAsync("bob") is null, "account isolation");
        var job = (await restarted.ClaimAsync())!;
        Check(job.AccountId == "alice" && job.Revision == first.Revision, "job bound to account/profile/revision");
        var snapshot = (await store.CaptureAsync("alice", first.ProfileId, first.Revision))!;
        var second = await store.SaveAsync("alice", new(first.Revision, input with { Name = "New Raven" }, first.ProfileId, OpenAiConsent: true));
        Check(!await store.CompleteAsync(job, Result(job), null), "old revision job cannot publish onto new revision");
        Check(snapshot.UserInput.Name == "  Raven  " && snapshot.Revision == 1, "conversation snapshot is immutable across edits");
        await ExpectAsync("character_revision_conflict", () => store.SaveAsync("alice", new(first.Revision, input, first.ProfileId, OpenAiConsent: true)));
        var secondJob = (await store.ClaimAsync())!;
        Check(await store.CompleteAsync(secondJob, Result(secondJob), null), "current revision result publishes");
        var ready = (await store.ReadAsync("alice"))!;
        Check(ready.Status == "ready" && ready.Enrichment?.Model == "fake" && ready.UserInput == second.UserInput,
            "enrichment remains separate from UserInput");
        var corrected = await store.CorrectAsync("alice", new(ready.Revision, ready.ProfileId, OpenAiConsent: true));
        Check(corrected.Revision == ready.Revision + 1 && corrected.Enrichment is null && corrected.UserInput == ready.UserInput,
            "reject interpretation creates revision without rewriting input");
        var correctionJob = (await store.ClaimAsync())!;
        Check(correctionJob.RejectedInterpretation?.Contains("New Raven") == true, "correction carries rejected fictional interpretation");
        await store.DeleteAsync("alice", corrected.Revision, corrected.ProfileId);
        Check(await store.IsDeletedAsync("alice", corrected.ProfileId), "deletion fences startup before journal replay");
        try
        {
            await store.ReplayDeletionsAsync((_, _) => throw new IOException("Simulated journal outage"));
            throw new InvalidOperationException("Deletion replay failure was lost.");
        }
        catch (IOException) { }
        var deletionRestart = new CharacterProfileStore(root);
        Check((await deletionRestart.PendingDeletionsAsync()).Any(item => item.AccountId == "alice"),
            "journal outage leaves durable deletion intent across restart");
        var replayed = false;
        await deletionRestart.ReplayDeletionsAsync((account, profile) =>
        {
            Check(account == "alice" && profile == corrected.ProfileId, "replay preserves exact deleted identity");
            replayed = true;
            return Task.CompletedTask;
        });
        Check(replayed && (await deletionRestart.PendingDeletionsAsync()).Length == 0
            && await deletionRestart.IsDeletedAsync("alice", corrected.ProfileId),
            "successful replay acknowledges work while retaining permanent startup tombstone");
        Check(!await store.CompleteAsync(correctionJob, Result(correctionJob), null), "deleted profile rejects in-flight result");
        var recreated = await store.SaveAsync("alice", new(0, input, OpenAiConsent: true));
        Check(recreated.ProfileId != first.ProfileId && recreated.Revision == 1, "delete/recreate changes profile identity");
        await ExpectAsync("character_revision_conflict", () => store.SaveAsync("alice", new(1, input with { Name = "Old edit" }, first.ProfileId, OpenAiConsent: true)));
        Check(await store.CaptureAsync("alice", first.ProfileId, 1) is null, "old profile identity cannot produce new snapshot");
        var recreateJob = (await store.ClaimAsync())!;
        await store.FenceAccountDeletionAsync("alice");
        Check(await store.ReadAsync("alice") is null && !await store.CompleteAsync(recreateJob, Result(recreateJob), null),
            "account data fence removes persisted inputs and blocks late result");
        await ExpectAsync("account_data_deleted", () => store.SaveAsync("alice", new(0, input, OpenAiConsent: true)));
        var bob = await store.SaveAsync("bob", new(0, new("Unknown", "Unknown"), OpenAiConsent: true));
        var bobJob = (await store.ClaimAsync())!;
        Check(await store.CompleteAsync(bobJob, Result(bobJob) with { NeedsClarification = true }, null), "clarification result saves");
        Check((await store.ReadAsync("bob"))?.Status == "needs_clarification", "explicit clarification status");
        await store.SaveAsync("bob", new(bob.Revision, new("Changed", "Unknown"), bob.ProfileId, OpenAiConsent: true));
        var failedJob = (await store.ClaimAsync())!;
        await store.CompleteAsync(failedJob, null, "fixture_failure");
        Check((await store.ReadAsync("bob"))?.ErrorCode == "fixture_failure", "persistent failed state");
        var leaseProfile = await store.SaveAsync("lease-test", new(0, new("Lease", "Original"), OpenAiConsent: true));
        var previousWorker = (await store.ClaimAsync())!;
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "customer-characters.sqlite")
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE character_jobs SET lease_until=0 WHERE id=$id";
            command.Parameters.AddWithValue("$id", previousWorker.Id);
            command.ExecuteNonQuery();
        }
        var recoveryWorker = (await new CharacterProfileStore(root).ClaimAsync())!;
        Check(recoveryWorker.Attempt == 2 && recoveryWorker.Id == previousWorker.Id
            && recoveryWorker.LeaseToken != previousWorker.LeaseToken, "restart recovers expired persisted worker lease");
        Check(!await store.CompleteAsync(previousWorker, Result(previousWorker), null), "previous worker lease cannot publish after recovery");
        Check(await store.CompleteAsync(recoveryWorker, Result(recoveryWorker), null), "current recovery lease publishes");
        await store.SaveAsync("retry-test", new(0, input, OpenAiConsent: true));
        var retry = (await store.ClaimAsync())!;
        Check(await store.RetryAsync(retry), "transient provider failure schedules durable retry");
        Check(await store.ClaimAsync() is null, "backoff prevents immediate provider retry");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "customer-characters.sqlite") }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE character_jobs SET lease_until=0 WHERE id=$id";
            command.Parameters.AddWithValue("$id", retry.Id);
            command.ExecuteNonQuery();
        }
        var retried = (await new CharacterProfileStore(root).ClaimAsync())!;
        Check(retried.Id == retry.Id && retried.Attempt == retry.Attempt + 1,
            "restart consumes same durable retry job after backoff");
        Check(!await store.CompleteAsync(retry, Result(retry), null), "old retry lease cannot publish");
        Check(await store.CompleteAsync(retried, Result(retried), null), "new retry lease publishes");
        var original = await store.SaveAsync("new-character-test", new(0, input, OpenAiConsent: true));
        var originalJob = (await store.ClaimAsync())!;
        var originalSnapshot = (await store.CaptureAsync("new-character-test", original.ProfileId, original.Revision))!;
        var newCharacter = await store.SaveAsync("new-character-test",
            new(original.Revision, input, original.ProfileId, NewCharacter: true, OpenAiConsent: true));
        Check(newCharacter.ProfileId != original.ProfileId && newCharacter.Revision == 1,
            "explicit new character creates a new identity even with identical input");
        Check(!await store.CompleteAsync(originalJob, Result(originalJob), null),
            "replaced character job cannot publish onto new identity");
        Check(originalSnapshot.ProfileId == original.ProfileId && originalSnapshot.UserInput == input,
            "explicit new character preserves already captured historical snapshot");
    }

    private static async Task OpenAiConsentAsync(string root)
    {
        var store = new CharacterProfileStore(root);
        var input = new CharacterUserInput("Private fixture", "Original");
        var local = await store.SaveAsync("local", new(0, input));
        Check(local.Status == "saved" && await store.ClaimAsync() is null,
            "omitted OpenAI consent saves locally without creating provider work");
        await ExpectAsync("openai_consent_required", () => store.CorrectAsync("local", new(local.Revision, local.ProfileId)));
        var queued = await store.SaveAsync("local", new(local.Revision, input, local.ProfileId, OpenAiConsent: true));
        var consentJob = await store.ClaimAsync();
        Check(queued.Status == "queued" && consentJob is { OpenAiConsent: true } && await store.CanDispatchAsync(consentJob),
            "explicit consent queues a durable consent-bearing provider job");
        var saved = await store.SaveAsync("local", new(queued.Revision, input, queued.ProfileId));
        Check(saved.Status == "saved" && await store.ClaimAsync() is null && !await store.CanDispatchAsync(consentJob!),
            "saving without consent cancels unfinished enrichment work");
        var legacy = await store.SaveAsync("legacy", new(0, input, OpenAiConsent: true));
        using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "customer-characters.sqlite")))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE character_jobs SET openai_consent=0 WHERE account_id='legacy'";
            command.ExecuteNonQuery();
        }
        var restarted = new CharacterProfileStore(root);
        Check(await restarted.ClaimAsync() is null && (await restarted.ReadAsync("legacy"))?.Status == "saved",
            "legacy pending jobs without recorded consent never reach provider after restart");
        using var provider = new OpenAiCharacterEnrichmentProvider(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), restarted);
        try
        {
            await provider.EnrichAsync(new("job", "local", "profile", 1, input, null, "lease", 1), default);
            throw new InvalidOperationException("Provider admitted missing consent.");
        }
        catch (CharacterException error) when (error.Code == "openai_consent_required") { }
    }

    private static void ProviderEvidence()
    {
        var job = new CharacterJob("private-job", "private-account", "private-profile", 3,
            new("Wizard", "Fiction"), null, "private-lease", 1, OpenAiConsent: true);
        var request = OpenAiCharacterEnrichmentProvider.BuildRequest(job, "server-model", true);
        Check(!request.Contains("private-account") && !request.Contains("private-profile"), "provider receives fictional fields only");
        var source = new CharacterSource("Official fiction source", "https://example.com/character");
        var result = Result(job) with
        {
            EvidenceKind = "web_sources", Sources = [source], Facts = [new("A fictional fact", [source.Url])]
        };
        CharacterEnrichmentValidation.Validate(result, 3);
        try
        {
            CharacterEnrichmentValidation.Validate(result with { Facts = [new("Invented source", ["https://invented.example/fact"])] }, 3);
            throw new InvalidOperationException("Uncited factual claim accepted.");
        }
        catch (InvalidDataException) { }
        var response = JsonSerializer.SerializeToElement(new
        {
            status = "completed", model = "actual-model", usage = new { input_tokens = 10, output_tokens = 20 },
            output = new[]
            {
                new { type = "message", content = new[]
                {
                    new { type = "output_text", text = JsonSerializer.Serialize(new
                    {
                        recognizedName = "Wizard", world = "Fiction", summary = "Fictional context",
                        facts = new[] { new { text = "A fact", sourceUrls = new[] { source.Url } } },
                        conversationIdeas = new[] { "A humorous proposal" }, ambiguities = Array.Empty<string>(), needsClarification = false
                    }), annotations = new[] { new { type = "url_citation", title = source.Title, url = source.Url } } }
                } }
            }
        });
        var parsed = OpenAiCharacterEnrichmentProvider.Parse(response, 3, "requested-model");
        Check(parsed.Sources.Single().Url == source.Url && parsed.Model == "actual-model" && parsed.InputTokens == 10,
            "source citations and actual model/usage provenance parsed from provider response");
    }

    private static CharacterEnrichment Result(CharacterJob job) => new(job.UserInput.Name, job.UserInput.World,
        "Clearly synthetic fixture", [], ["A fictional idea"], [], false, [], "fake", "fake-v1",
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), job.Revision, "synthetic");

    private static async Task ExpectAsync(string code, Func<Task<CharacterProfile>> action)
    {
        try { await action(); }
        catch (CharacterException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected character error " + code);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Customer profiles: " + message); }
    }
}

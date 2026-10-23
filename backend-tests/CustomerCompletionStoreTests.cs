using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class CustomerCompletionStoreTests
{
    internal static async Task RunAsync()
    {
        VerifyProjectionPolicy();
        var root = Path.Combine(Path.GetTempPath(), "dv-customer-completions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new CompletionClock(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "keepsakes.sqlite"),
                ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
                ["Keepsake:DataProtectionRoot"] = Path.Combine(root, "keys"),
                ["Keepsake:Enabled"] = "true",
                ["Keepsake:GenerationEnabled"] = "false",
                ["Keepsake:PortraitEnabled"] = "true"
            }).Build();
            var store = new KeepsakeStore(configuration, clock);
            const string accountId = "completion-account-a";
            const string customerSessionId = "00000000-0000-0000-0000-000000000101";
            var accountSessionValid = true;
            var owner = KeepsakeActor.ForAccount(accountId, "auth-session-a", () => accountSessionValid);
            var issued = store.Issue(Snapshot("completion-outcome-a", "completion-device-a"),
                new KeepsakeCustomerOwner(accountId, customerSessionId));

            var initial = store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(initial.State == "pending" && initial.Kind == "keepsake",
                "original account owner sees durable pending completion for the issued keepsake");
            var pendingEvent = initial;
            var pendingSequence = MaxSequence(configuration["Backend:DatabasePath"]!);
            var repeated = store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(repeated.Id == pendingEvent.Id
                && MaxSequence(configuration["Backend:DatabasePath"]!) == pendingSequence,
                "repeated reads neither append events nor change the stable event identifier");

            var stranger = KeepsakeActor.ForAccount("completion-account-b", "auth-session-b", static () => true);
            Check(store.ReadCompletionUpdates(stranger).Items.Count == 0,
                "a different account cannot read another account's completion events");
            ExpectKeepsake("completion_not_found", () => store.MarkCompletionRead(stranger, pendingEvent.Id));
            ExpectKeepsake("keepsake_not_found", () => store.ReadCompletionUpdates("guest-session-token"));
            ExpectKeepsake("invalid_completion_limit", () => store.ReadCompletionUpdates(owner, 51));
            ExpectKeepsake("invalid_completion_cursor", () => store.ReadCompletionUpdates(owner, 25, "tampered"));

            configuration["Keepsake:GenerationEnabled"] = "true";
            store.EnqueueBase(issued.Id, owner, new KeepsakeBrief(), "completion-enqueue-a");
            var processing = store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(processing.State == "processing" && processing.Id != pendingEvent.Id,
                "queued current base work creates one changed processing projection");

            var lease = store.LeaseNext() ?? throw new InvalidOperationException("Expected base lease.");
            var providerRequest = store.Dispatch(lease) ?? throw new InvalidOperationException("Expected base dispatch.");
            var story = new KeepsakeStory("Synthetic completion",
                string.Join(" ", Enumerable.Repeat("synthetic story", 200)),
                new("slender", "spiral", "brass", "wood", "starlight"));
            var storyResponse = JsonSerializer.Serialize(new
            {
                status = "completed",
                model = "synthetic-completion-test",
                output = new[]
                {
                    new { type = "message", content = new[]
                    {
                        new { type = "output_text", text = JsonSerializer.Serialize(story,
                            new JsonSerializerOptions(JsonSerializerDefaults.Web)) }
                    } }
                }
            });
            Check(providerRequest.RecordStage!("story", "completed", storyResponse),
                "valid story stage is accepted before artwork publication");
            var partial = store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(partial.State == "partial" && partial.HasStory && !partial.HasArtwork,
                "published current story is partial until its matching base artwork is published");
            var beforeDuplicateStage = partial.Id;
            Check(providerRequest.RecordStage("story", "completed", storyResponse),
                "duplicate valid story stage remains accepted by the existing stage contract");
            Check(store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id).Id == beforeDuplicateStage,
                "duplicate provider stage with the same semantic projection keeps the event identifier");

            var output = await new FakeKeepsakeProvider().GenerateAsync(providerRequest, default);
            Check(store.Complete(lease, output), "synthetic base result publishes without external providers");
            var ready = store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(ready.State == "ready" && ready.HasStory && ready.HasArtwork && ready.Id != partial.Id,
                "only matching current job story and base image produce a ready completion event");
            store.MarkCompletionRead(owner, ready.Id);
            var firstReadAt = store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id).ReadAt;
            Check(firstReadAt is not null, "completion acknowledgement is durably shared in the account feed");
            var secondOwnerSession = KeepsakeActor.ForAccount(accountId, "auth-session-a2", static () => true);
            Check(store.ReadCompletionUpdates(secondOwnerSession).Items
                    .Single(item => item.ResultLocator == issued.Id).ReadAt == firstReadAt,
                "acknowledgement is shared across authenticated sessions of the same account");
            clock.Advance(TimeSpan.FromMinutes(5));
            store.MarkCompletionRead(secondOwnerSession, ready.Id);
            Check(store.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id).ReadAt == firstReadAt,
                "repeated acknowledgement preserves the earliest durable read time");

            var restarted = new KeepsakeStore(configuration, clock);
            var afterRestart = restarted.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(afterRestart.Id == ready.Id && afterRestart.ReadAt == firstReadAt,
                "latest completion identity and read state survive store restart");

            restarted.EnqueueBase(issued.Id, owner, new KeepsakeBrief(), "completion-enqueue-b");
            var replacement = restarted.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(replacement.State == "processing" && !replacement.HasArtwork && replacement.Id != ready.Id,
                "new current generation cannot inherit an older ready edition's artwork state");
            var replacementLease = restarted.LeaseNext() ?? throw new InvalidOperationException("Expected replacement lease.");
            var replacementRequest = restarted.Dispatch(replacementLease)
                ?? throw new InvalidOperationException("Expected replacement dispatch.");
            restarted.Cancel(issued.Id, owner, replacementLease.Id, "completion-cancel-b");
            var cancelled = restarted.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(cancelled.State == "failed" && !cancelled.HasArtwork,
                "cancelled current generation is terminal and cannot be projected as ready from old artwork");

            var lateOutput = await new FakeKeepsakeProvider().GenerateAsync(replacementRequest, default);
            Check(!restarted.Complete(replacementLease, lateOutput), "late cancelled result is retained only as private history");
            var late = restarted.ReadCompletionUpdates(owner).Items.Single(item => item.ResultLocator == issued.Id);
            Check(late.State == "failed" && !late.HasStory && !late.HasArtwork,
                "late private-history result never changes the owner completion into ready");

            var baseView = restarted.Read(issued.Id, owner);
            var currentEdition = baseView.Editions.Single(edition => edition.JobId == lease.Id);
            var consentVersion = restarted.SetPortraitConsent(issued.Id, owner, true,
                "dv10-portrait-v1", "completion-portrait-consent-on");
            var baseImage = restarted.ReadAsset(issued.Id, owner, currentEdition.ArtworkAssetId).Bytes;
            using var photoStream = new MemoryStream(baseImage);
            var upload = await restarted.AcceptPhotoAsync(issued.Id, owner, photoStream, "image/png",
                "completion-portrait-upload", consentVersion);
            restarted.EnqueuePortrait(issued.Id, owner, upload.Id, currentEdition.Id,
                "portrait_only", true, "completion-portrait-enqueue");
            var portraitLease = restarted.LeaseNext() ?? throw new InvalidOperationException("Expected portrait lease.");
            var portraitRequest = restarted.Dispatch(portraitLease)
                ?? throw new InvalidOperationException("Expected portrait dispatch.");
            var portraitOutput = await new FakeKeepsakeProvider().GenerateAsync(portraitRequest, default);
            Check(restarted.Complete(portraitLease, portraitOutput), "synthetic portrait result publishes");
            var portraitReady = restarted.ReadCompletionUpdates(owner).Items.Single(item => item.Kind == "portrait");
            Check(portraitReady.State == "ready" && portraitReady.HasArtwork && !portraitReady.HasStory,
                "portrait completion requires its current completed job and published portrait image");
            var firstPage = restarted.ReadCompletionUpdates(owner, 1);
            Check(firstPage.Items.Count == 1 && firstPage.Items[0].Kind == "portrait"
                && firstPage.NextCursor is not null,
                "bounded first page returns the newest completion and a protected continuation cursor");
            var nextPage = restarted.ReadCompletionUpdates(owner, 1, firstPage.NextCursor);
            Check(nextPage.Items.Count == 1 && nextPage.Items[0].ResultLocator == issued.Id
                && nextPage.Items[0].Kind == "keepsake" && nextPage.NextCursor is null,
                "keyset cursor continues in stable descending sequence order");
            ExpectKeepsake("invalid_completion_cursor", () => restarted.ReadCompletionUpdates(stranger, 1, firstPage.NextCursor));

            restarted.SetPortraitConsent(issued.Id, owner, false,
                "dv10-portrait-v1", "completion-portrait-consent-off");
            Check(restarted.ReadCompletionUpdates(owner).Items.Single(item => item.Kind == "portrait").Id == portraitReady.Id,
                "withdrawing generation consent retains the already accepted published portrait completion");
            restarted.Delete(issued.Id, owner, KeepsakeDeleteScope.PhotosAndPortraits,
                "completion-delete-portraits");
            Check(restarted.ReadCompletionUpdates(owner).Items.All(item => item.Kind != "portrait"),
                "explicit portrait deletion purges portrait completion history while retaining base completion");

            using (var connection = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE dv10_keepsakes SET account_access_epoch=account_access_epoch+1 WHERE id=$id";
                command.Parameters.AddWithValue("$id", issued.Id);
                command.ExecuteNonQuery();
            }
            Check(restarted.ReadCompletionUpdates(owner).Items.All(item => item.ResultLocator != issued.Id),
                "stale original-owner epoch makes previously recorded completion unavailable");
            ExpectKeepsake("completion_not_found", () => restarted.MarkCompletionRead(owner, late.Id));

            using (var connection = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO dv10_account_fences(owner_id) VALUES($owner)";
                command.Parameters.AddWithValue("$owner", accountId);
                command.ExecuteNonQuery();
            }
            ExpectKeepsake("account_deleted", () => restarted.ReadCompletionUpdates(owner));

            accountSessionValid = false;
            ExpectKeepsake("invalid_session", () => restarted.ReadCompletionUpdates(owner));
            Console.WriteLine("PASS customer completion journal projection, idempotence, current output, acknowledgement and owner fences");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); }
        }
    }

    private static KeepsakeIssueSnapshot Snapshot(string outcome, string device) =>
        new(outcome, device, 1, "completion-test-wand", "Synthetic completion wand",
            "Synthetic test catalog", "No device or provider is involved.");

    private static void VerifyProjectionPolicy()
    {
        var noWork = CustomerCompletionPolicy.Project(new(CompletionUpdateKind.Keepsake,
            null, "completed", false, true, true, true, false));
        Check(noWork.State == CompletionUpdateState.Unavailable,
            "job state without a current work identifier cannot establish readiness");

        var completedWithoutPublication = CustomerCompletionPolicy.Project(new(CompletionUpdateKind.Keepsake,
            "current-job", "completed", false, true, true, false, false));
        Check(completedWithoutPublication.State == CompletionUpdateState.Unavailable,
            "completed current job without matching published artwork is unavailable, not ready");

        var stagePartial = CustomerCompletionPolicy.Project(new(CompletionUpdateKind.Keepsake,
            "current-job", "dispatched", false, true, true, false, false));
        Check(stagePartial.State == CompletionUpdateState.Partial && stagePartial.HasStory && !stagePartial.HasArtwork,
            "accepted story stage remains partial until matching artwork publishes");

        var uncertain = CustomerCompletionPolicy.Project(new(CompletionUpdateKind.Keepsake,
            "current-job", "awaiting_reconciliation", false, true, true, false, false));
        Check(uncertain.State == CompletionUpdateState.Partial
            && uncertain.Reason == CompletionUpdateReason.ReconciliationRequired,
            "reconciliation-required stage keeps the explicit uncertainty reason");

        foreach (var terminal in new[] { "cancelled", "archived_private" })
        {
            var privateResult = CustomerCompletionPolicy.Project(new(CompletionUpdateKind.Keepsake,
                "current-job", terminal, false, true, false, false, false));
            Check(privateResult.State == CompletionUpdateState.Failed && !privateResult.HasStory && !privateResult.HasArtwork,
                "cancelled or private-history output never projects as ready");
        }
    }

    private static long MaxSequence(string databasePath)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(sequence),0) FROM dv10_completion_updates";
        return (long)command.ExecuteScalar()!;
    }

    private static void ExpectKeepsake(string code, Action action)
    {
        try
        {
            action();
            throw new InvalidOperationException("Expected keepsake error " + code + ".");
        }
        catch (KeepsakeException exception) when (exception.Code == code) { }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) { throw new InvalidOperationException("Customer completion store: " + label); }
    }

    private sealed class CompletionClock(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan delta) => now = now.Add(delta);
    }
}

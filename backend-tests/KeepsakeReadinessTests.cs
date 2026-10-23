using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

internal static class KeepsakeReadinessTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv10-keepsake-readiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await RunAsync(root); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }

    private static async Task RunAsync(string root)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "readiness.sqlite"),
            ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        var store = new KeepsakeStore(config, TimeProvider.System);
        const string account = "readiness-owner";
        const string session = "readiness-customer-session";
        var actor = KeepsakeActor.ForAccount(account, "live-account-session", () => true);

        var pending = store.ReadCustomerSessionResult("not-bound", actor);
        Check(pending.Readiness.State == "pending" && pending.Readiness.Reason == "awaiting_result"
            && pending.ResultLocator is null && pending.AccessKind is null && !pending.CanManage,
            "a customer session without an immutable result binding remains non-authorizing pending");

        var bound = store.Issue(Snapshot("bound-device"), new KeepsakeCustomerOwner(account, session));
        var initial = store.ReadCustomerSessionResult(session, actor);
        Check(initial.ResultLocator == bound.Id && initial.AccessKind == "originalOwner"
            && initial.CanManage && initial.CanReadPrivate && initial.Readiness.State == "processing",
            "the exact immutable owner/session binding grants current owner readiness");
        var beforeRepeat = store.Read(bound.Id, actor);
        _ = store.ReadCustomerSessionResult(session, actor);
        var afterRepeat = store.Read(bound.Id, actor);
        Check(afterRepeat.Revision == beforeRepeat.Revision
            && afterRepeat.Jobs.Select(item => (item.Id, item.State)).SequenceEqual(beforeRepeat.Jobs.Select(item => (item.Id, item.State))),
            "repeated readiness reads do not mutate keepsake revision or result state");
        Check(store.ReadCustomerSessionResult("different-session", actor).ResultLocator is null
            && store.ReadCustomerSessionResult(session, KeepsakeActor.ForAccount("other-account", "other-session", () => true)).ResultLocator is null,
            "another session or account cannot substitute a locator for the exact server binding");

        var job = store.LeaseNext() ?? throw new InvalidOperationException("Expected initial base job.");
        var request = store.Dispatch(job) ?? throw new InvalidOperationException("Expected dispatched base job.");
        var story = new KeepsakeStory("A readiness tale", string.Join(' ', Enumerable.Repeat("A small brave wizard discovers a bright and kind new path through the old forest.", 24)),
            new("slender", "oak", "silver", "wood", "starlight"));
        var response = JsonSerializer.Serialize(new
        {
            status = "completed",
            output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(story) } } } }
        });
        Check(request.RecordStage!("story", "dispatch", "{}"), "synthetic story stage dispatch is accepted");
        Check(request.RecordStage("story", "completed", response), "synthetic story stage result is accepted");
        var partial = store.ReadCustomerSessionResult(session, actor);
        Check(partial.Readiness.State == "partial" && partial.Readiness.HasStory && !partial.Readiness.HasArtwork,
            $"published story without base artwork is reported as partial (state={partial.Readiness.State}, story={partial.Readiness.HasStory}, artwork={partial.Readiness.HasArtwork}, view={store.Read(bound.Id, actor).State}, editions={store.Read(bound.Id, actor).Editions.Count})");
        var uncertainPartial = KeepsakeReadiness.From("awaiting_reconciliation", needsBase: false,
            hasStory: true, hasArtwork: false, baseDispatchEnabled: true);
        Check(uncertainPartial.State == "partial" && uncertainPartial.Reason == "reconciliation_required"
            && !uncertainPartial.CanRetry,
            "a story retained across an uncertain provider outcome stays partial without suggesting retry");
        foreach (var terminalState in new[] { "failed", "cancelled" })
        {
            var terminalPartial = KeepsakeReadiness.From(terminalState, needsBase: false,
                hasStory: true, hasArtwork: false, baseDispatchEnabled: true);
            Check(terminalPartial.State == "partial" && terminalPartial.Reason == "generation_failed"
                && !terminalPartial.ShouldPoll && !terminalPartial.CanRetry,
                "definitive terminal job state preserves story but stops polling for missing artwork");
        }
        var image = Png();
        Check(store.Complete(job, new(story, image, "image/png", "synthetic", "fixture", "fixture prompt", response, true)),
            "synthetic base image completion is ingested");
        var ready = store.ReadCustomerSessionResult(session, actor);
        Check(ready.Readiness.State == "ready" && ready.Readiness.HasStory && ready.Readiness.HasArtwork,
            "story and supported base artwork produce ready state");

        var link = store.CustomerAccountLink(account, bound.Id) ?? throw new InvalidOperationException("Expected owner association.");
        var linkedView = store.ReadForAccount(bound.Id, link.Epoch);
        Check(linkedView.Readiness?.State == "ready" && linkedView.ReadOnly
            && linkedView.Jobs.Count == 0 && linkedView.Uploads.Count == 0,
            "account association projection exposes safe readiness without private job or upload details");

        var duplicateA = store.Issue(Snapshot("ambiguous-a"), new KeepsakeCustomerOwner(account, "ambiguous-session"));
        _ = store.Issue(Snapshot("ambiguous-b"), new KeepsakeCustomerOwner(account, "ambiguous-session"));
        var ambiguous = store.ReadCustomerSessionResult("ambiguous-session", actor);
        Check(ambiguous.Readiness.State == "unavailable" && ambiguous.ResultLocator is null && !ambiguous.CanManage,
            "multiple immutable bindings fail closed without selecting a locator");

        var revoked = store.Issue(Snapshot("revoked-device"), new KeepsakeCustomerOwner(account, "revoked-session"));
        store.RevokeAllAccess(revoked.Id, actor);
        var revokedResult = store.ReadCustomerSessionResult("revoked-session", actor);
        Check(revokedResult.Readiness.State == "unavailable" && revokedResult.ResultLocator is null,
            "revoked owner epoch clears the result locator and capabilities");

        var deletedAccount = "deleted-readiness-owner";
        var deletedActor = KeepsakeActor.ForAccount(deletedAccount, "deleted-live-session", () => true);
        var deleted = store.Issue(Snapshot("deleted-device"), new KeepsakeCustomerOwner(deletedAccount, "deleted-session"));
        store.Delete(deleted.Id, deletedActor, KeepsakeDeleteScope.All, Guid.NewGuid().ToString("D"), store.Read(deleted.Id, deletedActor).Revision);
        var deletedResult = store.ReadCustomerSessionResult("deleted-session", deletedActor);
        Check(deletedResult.Readiness.State == "unavailable" && deletedResult.ResultLocator is null,
            "deleted keepsake row cannot expose a result locator");

        var revokedActor = KeepsakeActor.ForAccount("invalid-live-owner", "expired-session", () => false);
        try
        {
            store.ReadCustomerSessionResult("any-session", revokedActor);
            throw new InvalidOperationException("An expired account session must not read readiness.");
        }
        catch (KeepsakeException error) when (error.Code == "invalid_session") { }
        store.FenceAccountDeletion(deletedAccount);
        try
        {
            store.ReadCustomerSessionResult("deleted-session", deletedActor);
            throw new InvalidOperationException("An account deletion fence must block readiness.");
        }
        catch (KeepsakeException error) when (error.Code == "account_deleted") { }
        Check(duplicateA.Id != bound.Id, "fixtures use distinct immutable result locators");
        Console.WriteLine("PASS keepsake readiness policy: immutable session binding, read-only stages, ambiguity, revocation and fences");
    }

    private static KeepsakeIssueSnapshot Snapshot(string device) => new(Guid.NewGuid().ToString("D"), device,
        1, "fixture-wand", "Readiness fixture", "synthetic facts", "no physical evidence");

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
}

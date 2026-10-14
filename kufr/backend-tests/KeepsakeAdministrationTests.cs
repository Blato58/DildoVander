using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Extensions.Configuration;

internal static class KeepsakeAdministrationTests
{
    internal static async Task RunAsync(string directory)
    {
        var root = Path.Combine(directory, "admin-content");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "admin.db"), ["Keepsake:DataRoot"] = root,
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true", ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        var store = new KeepsakeStore(configuration, TimeProvider.System);
        var owner = store.Bootstrap();
        var first = IssueAndClaim(store, owner.Token, "admin-first");
        var second = IssueAndClaim(store, owner.Token, "admin-second");
        var provider = new FakeKeepsakeProvider();
        for (var index = 0; index < 2; index++)
        {
            var job = store.LeaseNext()!;
            store.Complete(job, await provider.GenerateAsync(store.Dispatch(job)!, default));
        }
        var metadata = store.AdminList();
        var serialized = JsonSerializer.Serialize(metadata, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Check(metadata.Count == 2 && metadata.All(item => item.StoredBytes > 0 && item.AssetCount > 0)
            && !serialized.Contains("\"prompt\"", StringComparison.OrdinalIgnoreCase)
            && !serialized.Contains("\"story\"", StringComparison.OrdinalIgnoreCase)
            && !serialized.Contains("\"photo\"", StringComparison.OrdinalIgnoreCase), "admin inventory contains only bounded technical metadata");
        var edition = store.Read(first, owner.Token).Editions.Single();
        var share = store.CreateShare(first, owner.Token, edition.Id, "admin-share");
        var reader = store.Bootstrap();
        store.ShareClaim(share.Token, reader.Token, "admin-share-claim");
        store.AdminRevokeShares(first, "admin-revoke");
        store.AdminRevokeShares(first, "admin-revoke");
        Check(!store.Authorize(first, reader.Token, KeepsakeScope.Read, true, edition.Id)
            && store.Read(first, owner.Token).Editions.Single().Id == edition.Id,
            "manual admin share revocation does not remove owner or content");
        var cancelled = store.EnqueueBase(first, owner.Token, new(), "admin-new-base");
        var cancelledLease = store.LeaseNext()!;
        var cancelledRequest = store.Dispatch(cancelledLease)!;
        store.AdminCancel(first, cancelled.Id, "admin-cancel");
        store.AdminCancel(first, cancelled.Id, "admin-cancel");
        store.Complete(cancelledLease, await provider.GenerateAsync(cancelledRequest, default));
        Check(store.Read(first, owner.Token).Assets.Any(asset => asset.Kind == "base_history_image")
            && store.Read(first, owner.Token).Editions.Any(item => item.Id == edition.Id),
            "admin cancel preserves existing content and legitimately returned private history");
        Expect(() => store.PreviewAdminDeletion([], KeepsakeDeleteScope.All), "invalid_admin_target_set");
        Expect(() => store.PreviewAdminDeletion([first, first], KeepsakeDeleteScope.All), "invalid_admin_target_set");
        var conflicted = store.PreviewAdminDeletion([first, second], KeepsakeDeleteScope.All);
        store.SetPortraitConsent(second, owner.Token, true, "dv10-portrait-v1", "admin-change-after-preview");
        Expect(() => store.ConfirmAdminDeletion(conflicted.Id, "delete", "admin-conflict", conflicted.Digest), "admin_preview_changed");
        Check(store.Authorize(first, owner.Token, KeepsakeScope.Read) && store.Authorize(second, owner.Token, KeepsakeScope.Read)
            && Directory.Exists(Path.Combine(root, first)) && Directory.Exists(Path.Combine(root, second)),
            "any changed target rejects full batch atomically before revoking or deleting another target");
        var preview = store.PreviewAdminDeletion([first, second], KeepsakeDeleteScope.All);
        Check(preview.Count == 2 && preview.TotalBytes > 0 && preview.TargetIds.ToHashSet().SetEquals([first, second]),
            "immutable preview exposes exact count, bytes and IDs");
        var third = IssueAndClaim(store, owner.Token, "admin-created-after-preview");
        Expect(() => store.ConfirmAdminDeletion(preview.Id, "delete", "admin-wrong-digest", "incorrect"), "admin_preview_changed");
        store.BeforeAdminCleanup = () => throw new InvalidOperationException("simulated_crash_after_batch_commit");
        try
        {
            store.ConfirmAdminDeletion(preview.Id, "delete", "admin-confirm", preview.Digest);
            throw new InvalidOperationException("Expected simulated crash.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "simulated_crash_after_batch_commit") { }
        Check(!store.Authorize(first, owner.Token, KeepsakeScope.Read) && !store.Authorize(second, owner.Token, KeepsakeScope.Read)
            && store.Authorize(third, owner.Token, KeepsakeScope.Read),
            "committed batch immediately revokes only snapshotted resources before resumable file cleanup");
        store = new KeepsakeStore(configuration, TimeProvider.System);
        var result = store.ConfirmAdminDeletion(preview.Id, "delete", "admin-confirm", preview.Digest);
        var retry = store.ConfirmAdminDeletion(preview.Id, "delete", "admin-confirm", preview.Digest);
        Check(result.Id == retry.Id && result.Count == 2 && result.State == "local_cleanup_complete"
            && result.DeletionReceiptIds.Count == 2 && result.Actor == "server_admin",
            "restart resumes durable batch closure and response replay returns same batch receipts");
        Check(!Directory.Exists(Path.Combine(root, first)) && !Directory.Exists(Path.Combine(root, second))
            && store.Read(third, owner.Token).Snapshot.OutcomeId == "admin-created-after-preview",
            "preview never expands to a later-issued keepsake");
        Check(result.DeletionReceiptIds.All(receiptId => store.AdminGetDeletionReceipt(receiptId).ProviderState.Contains("unknown")),
            "admin receipts preserve honest independent provider and backup cleanup state");
        foreach (var receiptId in result.DeletionReceiptIds)
        {
            Expect(() => store.GetDeletionReceipt(receiptId, owner.Token), "deletion_receipt_not_found");
        }
        var thirdJob = store.LeaseNext()!;
        store.Complete(thirdJob, await provider.GenerateAsync(store.Dispatch(thirdJob)!, default));
        var consent = store.SetPortraitConsent(third, owner.Token, true, "dv10-portrait-v1", "admin-photo-consent");
        using var input = new MemoryStream(KeepsakeCoreTests.Png());
        var upload = await store.AcceptPhotoAsync(third, owner.Token, input, "image/png", "admin-photo", consent);
        store.AfterAssetFlush = kind =>
        {
            if (kind == "photo_original") { throw new InvalidOperationException("admin_orphan_after_flush"); }
        };
        try
        {
            using var orphan = new MemoryStream(KeepsakeCoreTests.Png());
            await store.AcceptPhotoAsync(third, owner.Token, orphan, "image/png", "admin-orphan", consent);
        }
        catch (InvalidOperationException exception) when (exception.Message == "admin_orphan_after_flush") { }
        finally { store.AfterAssetFlush = null; }
        var photos = Path.Combine(root, third, "photos");
        Check(Directory.GetDirectories(photos).Length == 2, "photo scope fixture includes uncommitted orphan directory");
        var photoPreview = store.PreviewAdminDeletion([third], KeepsakeDeleteScope.PhotosAndPortraits);
        Check(photoPreview.TotalBytes >= store.ReadAsset(third, owner.Token, upload.OriginalAssetId).Asset.Length,
            "photo preview inventory includes actual scoped bytes");
        var photosDeleted = store.ConfirmAdminDeletion(photoPreview.Id, "delete", "admin-delete-photos", photoPreview.Digest);
        Check(photosDeleted.State == "local_cleanup_complete" && !Directory.EnumerateFiles(photos, "*", SearchOption.AllDirectories).Any()
            && store.Read(third, owner.Token).Editions.Count == 1 && store.Read(third, owner.Token).Uploads.Count == 0,
            "admin photos batch reuses full dependency closure including orphan directories while preserving base content");
        Console.WriteLine("Keepsake admin manual batch tests passed (synthetic, no inference).");
    }

    private static string IssueAndClaim(KeepsakeStore store, string session, string outcome)
    {
        var issue = store.Issue(new(outcome, "admin-device", 1, "admin-catalog-one", "Katalogová hůlka",
            "Pevný údaj", "timed_return_estimate_not_sensor"));
        var claim = store.IssueClaim(issue.Id, "admin-device");
        store.Claim(claim.Token, session, "claim-" + outcome);
        return issue.Id;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Keepsake admin assertion: " + message); }
    }
    private static void Expect(Action action, string code)
    {
        try { action(); } catch (KeepsakeException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected Keepsake admin error: " + code);
    }
}

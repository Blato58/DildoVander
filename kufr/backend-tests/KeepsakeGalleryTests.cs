using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class KeepsakeGalleryTests
{
    internal static async Task RunAsync(string directory)
    {
        var database = Path.Combine(directory, "gallery.sqlite");
        var root = Path.Combine(directory, "gallery-content");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database, ["Keepsake:DataRoot"] = root,
            ["Backend:Memory:Enabled"] = "false", ["Backend:Memory:LongTermPersonalEnabled"] = "false",
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        var clock = new GalleryClock();
        using var journal = new OperationJournal(config, clock);
        var evenings = new EveningMemoryStore(config, journal, clock);
        var store = new KeepsakeStore(config, clock);
        Check(evenings.ReadActiveEveningId("unknown") is null, "read-only lookup never creates a missing evening");
        var firstEvening = evenings.GetStatus("device-a").EveningId;
        var otherEvening = evenings.GetStatus("device-b").EveningId;
        Check(evenings.ReadContext("device-a", Guid.NewGuid().ToString("D"), "").EveningId == firstEvening,
            "technical evening capture remains available with personal/transcript memory disabled");
        var provider = new FakeKeepsakeProvider();

        var first = store.Issue(Snapshot("device-a", firstEvening));
        var owner = store.Bootstrap();
        store.Claim(store.IssueClaim(first.Id, "device-a").Token, owner.Token, "gallery-claim");
        var lateJob = store.LeaseNext()!;
        var lateRequest = store.Dispatch(lateJob)!;
        var lateOutput = await provider.GenerateAsync(lateRequest, default);
        clock.Utc = clock.Utc.AddMinutes(1);
        var second = store.Issue(Snapshot("device-a", firstEvening));
        await CompleteNext();
        clock.Utc = clock.Utc.AddMinutes(1);
        store.Complete(lateJob, lateOutput);
        var publishedAt = clock.Utc;

        var foreign = store.Issue(Snapshot("device-b", otherEvening));
        await CompleteNext();
        store.Issue(Snapshot("device-a", null));
        await CompleteNext();
        var cancelled = store.Issue(Snapshot("device-a", firstEvening));
        var cancelledJob = store.LeaseNext()!;
        var cancelledOutput = await provider.GenerateAsync(store.Dispatch(cancelledJob)!, default);
        store.AdminCancel(cancelled.Id, cancelledJob.Id, "gallery-cancel");
        store.Complete(cancelledJob, cancelledOutput);

        var page = store.ReadEveningGallery("device-a", firstEvening, 1);
        Check(page.Latest?.Locator == first.Id && page.Items.Single().Locator == first.Id
            && page.Latest.CreatedAt == publishedAt && page.NextCursor is not null,
            "latest means final image publication, even if an older accepted job finishes last");
        var next = store.ReadEveningGallery("device-a", firstEvening, 1, page.NextCursor);
        Check(next.Items.Single().Locator == second.Id && next.Latest?.Locator == first.Id && next.NextCursor is null,
            "bounded pagination retains latest and excludes unbound, foreign and cancelled editions");
        Check(store.ReadEveningGallery("device-b", otherEvening).Items.Single().Locator == foreign.Id,
            "each device has an independent authoritative active evening and gallery");
        Check(store.ReadEveningGallery("device-a", null).Evening is null, "missing active evening returns an empty gallery");

        var switched = evenings.Create("device-a", Guid.NewGuid(), "/fixture", "new-evening", firstEvening, "Second evening");
        Check(switched.Status == 201, "fixture activates another evening");
        var currentEvening = evenings.ReadActiveEveningId("device-a")!;
        Check(store.ReadEveningGallery("device-a", currentEvening).Items.Count == 0
            && store.Read(first.Id, owner.Token).Snapshot.EveningId == firstEvening,
            "activation never relabels an accepted snapshot");
        Expect(() => store.ReadEveningGallery("device-a", currentEvening, 1, page.NextCursor), 409);
        var secondEveningIssue = store.Issue(Snapshot("device-a", currentEvening));
        await CompleteNext();
        Check(store.ReadEveningGallery("device-a", currentEvening).Latest?.Locator == secondEveningIssue.Id,
            "newly published content belongs only to the new captured evening");
        Check(evenings.Activate("device-a", Guid.NewGuid(), "/fixture/active", "restore-evening", firstEvening, currentEvening).Status == 200,
            "reactivating the original evening restores its exact public scope");

        // Derivatives run locally and remain available when paid generation is disabled.
        config["Keepsake:GenerationEnabled"] = "false";
        while (store.ProcessOneBaseExport()) { }
        var ready = store.ReadEveningGallery("device-a", firstEvening).Items.Single(item => item.Locator == first.Id);
        Check(ready.Assets.Select(asset => asset.Kind).Order().SequenceEqual(
            new[] { "base_image", "export_card", "export_story", "export_zip" }.Order()), "mandatory local base derivatives are listed truthfully");
        var zip = ready.Assets.Single(asset => asset.Kind == "export_zip");
        var publicFile = store.OpenGalleryAsset("device-a", firstEvening, first.Id, ready.EditionId, zip.Id);
        using (publicFile.Stream)
        using (var archive = new ZipArchive(publicFile.Stream, ZipArchiveMode.Read))
        {
            Check(archive.Entries.Count <= 8 && archive.Entries.Any(entry => entry.FullName == "keepsake-card.png")
                && archive.Entries.Any(entry => entry.FullName == "story.txt")
                && archive.Entries.All(entry => !entry.FullName.Contains("portrait", StringComparison.OrdinalIgnoreCase)),
                "public ZIP contains base-only local card, story and artwork");
        }
        Check(store.CreateExport(first.Id, owner.Token, ready.EditionId, "zip", null, "reuse-public-zip").Id == zip.Id,
            "owner export reuses the same already retained base derivative");

        config["Keepsake:GenerationEnabled"] = "true";
        var consent = store.SetPortraitConsent(first.Id, owner.Token, true, "dv10-portrait-v1", "gallery-consent");
        using var photo = new MemoryStream(KeepsakeCoreTests.Png());
        var upload = await store.AcceptPhotoAsync(first.Id, owner.Token, photo, "image/png", "gallery-upload", consent);
        store.EnqueuePortrait(first.Id, owner.Token, upload.Id, ready.EditionId, "portrait_only", true, "gallery-portrait");
        await CompleteNext();
        var privateView = store.Read(first.Id, owner.Token);
        var portraitZip = store.CreateExport(first.Id, owner.Token, ready.EditionId, "zip", privateView.Portraits.Single().Id, "gallery-private-zip");
        var forbidden = privateView.Assets.Where(asset => asset.UploadId is not null || asset.Kind.StartsWith("provider_", StringComparison.Ordinal)
            || asset.Kind == "prompt").Select(asset => asset.Id).Append(portraitZip.Id).ToArray();
        foreach (var assetId in forbidden)
        {
            Expect(() => { using var stream = store.OpenGalleryAsset("device-a", firstEvening, first.Id, ready.EditionId, assetId).Stream; }, 404);
        }
        Expect(() => { using var stream = store.OpenGalleryAsset("device-b", firstEvening, first.Id, ready.EditionId, zip.Id).Stream; }, 404);
        Expect(() => { using var stream = store.OpenGalleryAsset("device-a", currentEvening, first.Id, ready.EditionId, zip.Id).Stream; }, 404);
        var projected = JsonSerializer.Serialize(store.ReadEveningGallery("device-a", firstEvening));
        Check(!projected.Contains("Prompt", StringComparison.Ordinal) && !projected.Contains("DeviceId", StringComparison.Ordinal)
            && !projected.Contains(upload.Id, StringComparison.Ordinal) && !projected.Contains(foreign.Id, StringComparison.Ordinal),
            "public DTO contains no private identifiers, photos, prompts or history");

        var before = Inventory(database, root);
        for (var n = 0; n < 10; n++)
        {
            store.ReadEveningGallery("device-a", firstEvening);
            using var stream = store.OpenGalleryAsset("device-a", firstEvening, first.Id, ready.EditionId, zip.Id).Stream;
            Check(Convert.ToHexString(SHA256.HashData(stream)).Equals(zip.Sha256, StringComparison.OrdinalIgnoreCase), "streamed derivative integrity");
        }
        Check(before == Inventory(database, root), "public read/download creates no job, derivative, grant or file");
        store.Delete(first.Id, owner.Token, KeepsakeDeleteScope.PhotosAndPortraits, "gallery-delete-photos");
        Check(store.ReadEveningGallery("device-a", firstEvening).Items.Single(item => item.Locator == first.Id).Assets.Count == 4,
            "manual photo closure preserves base-only public derivatives");
        store.Delete(first.Id, owner.Token, KeepsakeDeleteScope.All, "gallery-delete-all");
        Check(store.ReadEveningGallery("device-a", firstEvening).Items.All(item => item.Locator != first.Id), "manual full deletion revokes gallery immediately");
        Expect(() => { using var stream = store.OpenGalleryAsset("device-a", firstEvening, first.Id, ready.EditionId, zip.Id).Stream; }, 404);

        async Task CompleteNext()
        {
            var job = store.LeaseNext() ?? throw new Exception("Expected fixture job");
            store.Complete(job, await provider.GenerateAsync(store.Dispatch(job)!, default));
            clock.Utc = clock.Utc.AddSeconds(1);
        }
    }

    private static KeepsakeIssueSnapshot Snapshot(string device, string? evening) => new(Guid.NewGuid().ToString("D"), device, 1,
        "wand-1", "Oficiální hůlka", "Synthetic catalog facts", "completed_by_timing", EveningId: evening);
    private static void Check(bool condition, string detail) { if (!condition) { throw new Exception(detail); } }
    private static void Expect(Action action, int status)
    {
        try { action(); throw new Exception("Expected public scope rejection"); }
        catch (KeepsakeException exception) when (exception.Status == status) { }
    }
    private static string Inventory(string database, string root)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT (SELECT count(*) FROM dv10_jobs)||':'||(SELECT count(*) FROM dv10_grants)||':'||"
            + "(SELECT count(*) FROM dv10_derivatives)||':'||(SELECT sum(json_array_length(document,'$.assets')) FROM dv10_keepsakes)";
        return command.ExecuteScalar() + ":" + string.Join(";", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order().Select(path => path + ":" + new FileInfo(path).Length));
    }
    private sealed class GalleryClock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}

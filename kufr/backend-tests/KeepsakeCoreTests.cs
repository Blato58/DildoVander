using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

internal static class KeepsakeCoreTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dv10-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await WizardIdentityTests.RunAsync(directory);
            await LifecycleAsync(directory);
            await AdmissionBoundsAsync(directory);
            await UploadAdmissionAsync(directory);
            await KeepsakeAdministrationTests.RunAsync(directory);
            await KeepsakeGalleryTests.RunAsync(directory);
            await ProviderBoundaryAsync();
            CardLayout();
            Console.WriteLine("Keepsake core tests passed (synthetic offline fixtures; no paid calls).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task UploadAdmissionAsync(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "upload-admission.db"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "upload-admission-content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        var store = new KeepsakeStore(configuration, new Clock());
        var issue = store.Issue(Snapshot("upload-admission"));
        var owner = store.Bootstrap();
        var claim = store.IssueClaim(issue.Id, "device");
        store.Claim(claim.Token, owner.Token, "upload-admission-claim");
        var consent = store.SetPortraitConsent(issue.Id, owner.Token, true, "dv10-portrait-v1", "upload-admission-consent");
        var otherStore = new KeepsakeStore(configuration, new Clock());
        using var paused = new PausedReadStream();
        using var cancellation = new CancellationTokenSource();
        var first = store.AcceptPhotoAsync(issue.Id, owner.Token, paused, "image/png", "paused-upload", consent, cancellation.Token);
        using var rejected = new CountingReadStream(Png());
        var cancelled = false;
        try
        {
            await paused.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                await otherStore.AcceptPhotoAsync(issue.Id, owner.Token, rejected, "image/png", "rejected-upload", consent);
                throw new InvalidOperationException("Expected process-wide upload backpressure.");
            }
            catch (KeepsakeException exception) when (exception.Code == "upload_backpressure")
            {
                Check(exception.Status == 429 && rejected.ReadCalls == 0,
                    "second authorized upload is rejected across store instances before touching its stream");
            }
        }
        finally
        {
            cancellation.Cancel();
            try { await first; }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Check(cancelled && store.Read(issue.Id, owner.Token).Uploads.Count == 0,
            "cancellation releases buffered upload admission without accepting or deleting content");
        using var subsequent = new CountingReadStream(Png());
        var accepted = await otherStore.AcceptPhotoAsync(issue.Id, owner.Token, subsequent, "image/png", "subsequent-upload", consent);
        Check(subsequent.ReadCalls > 0 && otherStore.Read(issue.Id, owner.Token).Uploads.Single().Id == accepted.Id,
            "later upload succeeds after the cancelled request releases its process-wide slot");
        using var afterSuccess = new MemoryStream(Png());
        var next = await store.AcceptPhotoAsync(issue.Id, owner.Token, afterSuccess, "image/png", "after-success-upload", consent);
        Check(store.Read(issue.Id, owner.Token).Uploads.Count == 2 && next.Id != accepted.Id,
            "successful completion also releases admission while preserving accepted upload history");
    }

    private static async Task AdmissionBoundsAsync(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "bounded.db"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "bounded-content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:MaxQueuedJobs"] = "1"
        }).Build();
        var store = new KeepsakeStore(configuration, new Clock());
        var issued = Enumerable.Range(0, 3).Select(index => store.Issue(Snapshot("bounded-" + index))).ToArray();
        var owner = store.Bootstrap();
        for (var index = 0; index < issued.Length; index++)
        {
            var claim = store.IssueClaim(issued[index].Id, "device");
            store.Claim(claim.Token, owner.Token, "bounded-claim-" + index);
            Check(store.Read(issued[index].Id, owner.Token).Snapshot == Snapshot("bounded-" + index),
                "queue pressure never changes accepted immutable outcome snapshot");
        }
        var initial = issued.Select(item => store.Read(item.Id, owner.Token)).ToArray();
        Check(initial.Sum(item => item.Jobs.Count) == 1 && initial.Count(item => item.State == "awaiting_capacity") == 2,
            "three automatic issues with capacity one retain all keepsakes and admit only one bounded job");
        var provider = new FakeKeepsakeProvider();
        var completed = new HashSet<string>();
        for (var index = 0; index < issued.Length; index++)
        {
            var job = store.LeaseNext() ?? throw new InvalidOperationException("Expected deferred base job admission.");
            Check(completed.Add(job.KeepsakeId), "lease admits each held keepsake at most once");
            var live = issued.Select(item => store.Read(item.Id, owner.Token)).SelectMany(item => item.Jobs)
                .Count(item => item.State is "queued" or "leased" or "dispatched");
            Check(live <= 1, "automatic admission never exceeds configured pending-job cap");
            var request = store.Dispatch(job)!;
            store.Complete(job, await provider.GenerateAsync(request, default));
        }
        Check(issued.All(item => store.Read(item.Id, owner.Token).State == "ready") && store.LeaseNext() is null,
            "deferred base generation resumes same three resources without duplicate work");
        configuration["Keepsake:GenerationEnabled"] = "false";
        var disabled = store.Issue(Snapshot("issued-while-disabled"));
        var disabledClaim = store.IssueClaim(disabled.Id, "device");
        store.Claim(disabledClaim.Token, owner.Token, "disabled-claim");
        Check(store.Read(disabled.Id, owner.Token).State == "paused_preparing"
            && store.Read(disabled.Id, owner.Token).Jobs.Count == 0 && store.LeaseNext() is null,
            "issuance while generation is disabled retains snapshot without provider work or fake ready state");
        configuration["Keepsake:GenerationEnabled"] = "true";
        configuration["Keepsake:MinimumFreeDiskBytes"] = long.MaxValue.ToString();
        var diskHeld = store.Issue(Snapshot("issued-during-disk-pressure"));
        var diskClaim = store.IssueClaim(diskHeld.Id, "device");
        store.Claim(diskClaim.Token, owner.Token, "disk-claim");
        Check(store.Read(diskHeld.Id, owner.Token).State == "awaiting_capacity"
            && store.Read(diskHeld.Id, owner.Token).Jobs.Count == 0 && store.LeaseNext() is null,
            "disk admission pressure preserves accepted snapshot without adding or dispatching work");
        configuration["Keepsake:MinimumFreeDiskBytes"] = "0";
        var resumed = new HashSet<string>();
        for (var index = 0; index < 2; index++)
        {
            var job = store.LeaseNext()!;
            resumed.Add(job.KeepsakeId);
            store.Complete(job, await provider.GenerateAsync(store.Dispatch(job)!, default));
        }
        Check(resumed.SetEquals(new[] { disabled.Id, diskHeld.Id })
            && store.Issue(Snapshot("issued-while-disabled")).Id == disabled.Id,
            "flag and disk recovery admit held base work without reissuing or replacing outcomes");
    }

    private static async Task LifecycleAsync(string directory)
    {
        var clock = new Clock();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "backend.db"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        var store = new KeepsakeStore(configuration, clock);
        var provider = new FakeKeepsakeProvider();
        var first = store.Issue(Snapshot("one"));
        Check(store.Issue(Snapshot("one")).Id == first.Id, "issuance is idempotent by exact device/outcome");
        var owner = store.Bootstrap();
        Check(store.ResumeSession(owner.Token) == owner && store.ValidateSession(owner.Token, owner.CsrfToken), "session and CSRF survive repeat bootstrap");
        var firstClaim = store.IssueClaim(first.Id, "device", "issue-one");
        Check(store.IssueClaim(first.Id, "device", "issue-one").Token == firstClaim.Token, "operator lost claim response replays without rotation");
        var claim = store.IssueClaim(first.Id, "device", "issue-two");
        Expect(() => store.Claim(firstClaim.Token, owner.Token, "oldclaim"), "invalid_link");
        Check(store.Claim(claim.Token, owner.Token, "claim") == first.Id
            && store.Claim(claim.Token, owner.Token, "claim") == first.Id, "lost claim response retries same committed grant");
        var stranger = store.Bootstrap();
        Expect(() => store.Claim(claim.Token, stranger.Token, "other"), "claim_already_used");
        Check(!store.Authorize(first.Id, stranger.Token, KeepsakeScope.Read), "resource locator is not guest authority");
        var second = store.Issue(Snapshot("two"));
        var secondClaim = store.IssueClaim(second.Id, "device");
        store.Claim(secondClaim.Token, stranger.Token, "claim2");
        Check(!store.Authorize(second.Id, owner.Token, KeepsakeScope.Read), "two guest resources are isolated");
        var firstJob = store.LeaseNext()!;
        var request = store.Dispatch(firstJob)!;
        var output = await provider.GenerateAsync(request, default);
        Check(store.Complete(firstJob, output), "original bytes are published after scoped gate");
        var secondJob = store.LeaseNext()!;
        var secondRequest = store.Dispatch(secondJob)!;
        store.Complete(secondJob, await provider.GenerateAsync(secondRequest, default));
        var view = store.Read(first.Id, owner.Token);
        var edition = view.Editions.Single();
        var recovery = store.CreateRecovery(first.Id, owner.Token, "recovery");
        Check(store.CreateRecovery(first.Id, owner.Token, "recovery").Token == recovery.Token, "recovery response replay uses protected token");
        var share = store.CreateShare(first.Id, owner.Token, edition.Id, "share");
        var reader = store.Bootstrap();
        store.ShareClaim(share.Token, reader.Token, "share-claim");
        Check(!store.Authorize(first.Id, reader.Token, KeepsakeScope.Write)
            && store.Authorize(first.Id, reader.Token, KeepsakeScope.Read, true, edition.Id), "share grants are read-only");
        var consent = store.SetPortraitConsent(first.Id, owner.Token, true, "dv10-portrait-v1", "consent");
        var png = Png();
        using var input = new MemoryStream(png);
        var upload = await store.AcceptPhotoAsync(first.Id, owner.Token, input, "image/png", "upload", consent);
        Check(store.ReadAsset(first.Id, owner.Token, upload.OriginalAssetId).Bytes.SequenceEqual(png), "durable original retained byte-for-byte");
        var normal = store.ReadAsset(first.Id, owner.Token, upload.NormalizedAssetId).Bytes;
        using (var codec = SKCodec.Create(new MemoryStream(normal)))
        {
            Check(codec is not null && codec.EncodedFormat == SKEncodedImageFormat.Png, "real decoded normalized PNG");
            Check(codec!.Info.ColorSpace is { IsSrgb: true }, "normalized model copy declares verified sRGB");
        }
        using var bad = new MemoryStream([137, 80, 78, 71, 13, 10, 26, 10]);
        await ExpectAsync(() => store.AcceptPhotoAsync(first.Id, owner.Token, bad, "image/png", "broken", consent), "photo_decode_failed");
        var portraitJob = store.EnqueuePortrait(first.Id, owner.Token, upload.Id, edition.Id, "portrait_only", true, "portrait");
        var leasedPortrait = store.LeaseNext()!;
        var portraitRequest = store.Dispatch(leasedPortrait)!;
        Check(portraitRequest.NormalizedPhoto!.SequenceEqual(normal), "exact same guest normalized photo reaches edit");
        store.Complete(leasedPortrait, await provider.GenerateAsync(portraitRequest, default));
        var withPortrait = store.Read(first.Id, owner.Token);
        var portrait = withPortrait.Portraits.Single();
        Check(store.Read(first.Id, reader.Token, true, edition.Id).Portraits.Count == 0
            && store.Read(first.Id, reader.Token, true, edition.Id).Uploads.Count == 0, "portrait does not expand existing share");
        Expect(() => store.ReadAsset(first.Id, reader.Token, portrait.AssetId, true, edition.Id), "asset_not_found");
        var export = store.CreateExport(first.Id, owner.Token, edition.Id, "zip", null, "zip");
        using (var zip = new ZipArchive(new MemoryStream(store.ReadAsset(first.Id, owner.Token, export.Id).Bytes)))
        {
            Check(zip.Entries.Count <= 8 && zip.Entries.Any(item => item.FullName == "keepsake-card.png")
                && zip.Entries.Any(item => item.FullName == "manifest.json")
                && !zip.Entries.Any(item => item.FullName.Contains("portrait") || item.FullName.Contains("original")), "bounded default ZIP has no portrait or raw photo");
        }
        var portraitExport = store.CreateExport(first.Id, owner.Token, edition.Id, "zip", portrait.Id, "portraitzip");
        Expect(() => store.ReadAsset(first.Id, reader.Token, portraitExport.Id, true, edition.Id), "asset_not_found");
        store.EnqueuePortrait(first.Id, owner.Token, upload.Id, edition.Id, "portrait_only", true, "superseded-consent");
        var supersededLease = store.LeaseNext()!;
        var supersededRequest = store.Dispatch(supersededLease)!;
        store.SetPortraitConsent(first.Id, owner.Token, true, "dv10-portrait-v1", "new-consent");
        Check(!store.Complete(supersededLease, await provider.GenerateAsync(supersededRequest, default)), "new consent prevents old portrait publication");
        Check(store.Read(first.Id, owner.Token).Assets.Any(item => item.Kind == "portrait_history_image"), "superseded accepted portrait remains private until manual deletion");
        var lateJob = store.EnqueueBase(first.Id, owner.Token, new(), "late");
        var lateLease = store.LeaseNext()!;
        var lateRequest = store.Dispatch(lateLease)!;
        store.Cancel(first.Id, owner.Token, lateJob.Id, "cancel");
        Check(!store.Complete(lateLease, await provider.GenerateAsync(lateRequest, default)), "cancelled late result cannot publish");
        Check(store.Read(first.Id, owner.Token).Assets.Any(item => item.Kind == "base_history_image"), "cancel preserves accepted late content privately");
        store.EnqueueBase(first.Id, owner.Token, new(), "second-edition");
        var secondEditionLease = store.LeaseNext()!;
        var secondEditionRequest = store.Dispatch(secondEditionLease)!;
        store.Complete(secondEditionLease, await provider.GenerateAsync(secondEditionRequest, default));
        var newEdition = store.Read(first.Id, owner.Token).Editions.Last();
        var secondShare = store.CreateShare(first.Id, owner.Token, newEdition.Id, "share-new-edition");
        store.ShareClaim(secondShare.Token, reader.Token, "share-second-claim");
        Check(store.Read(first.Id, reader.Token, true, edition.Id).Editions.Single().Id == edition.Id
            && store.Read(first.Id, reader.Token, true, newEdition.Id).Editions.Single().Id == newEdition.Id,
            "two share links in one browser retain exact independent edition scopes");
        Expect(() => store.Read(first.Id, reader.Token, true), "keepsake_not_found");
        var restartedJob = store.EnqueueBase(first.Id, owner.Token, new(), "restart");
        var restartLease = store.LeaseNext()!;
        store.Dispatch(restartLease);
        store = new KeepsakeStore(configuration, clock);
        Check(store.Read(first.Id, owner.Token).Jobs.Single(item => item.Id == restartedJob.Id).State == "awaiting_reconciliation"
            && store.LeaseNext() is null, "dispatched attempt on restart cannot be replayed automatically");
        clock.Advance(TimeSpan.FromDays(365));
        Check(!store.ValidateSession(owner.Token), "access session expires independently of content");
        var returned = store.Bootstrap();
        store.Recover(recovery.Token, returned.Token, "recover");
        var retained = store.Read(first.Id, returned.Token);
        Check(retained.Uploads.Count == 1 && retained.Portraits.Count == 2 && retained.Editions.Count == 3
            && retained.RetentionMode == "manual_only", "aged accepted data survives unchanged via recovery with no inference");
        Check(store.ReadAsset(first.Id, returned.Token, upload.OriginalAssetId).Bytes.SequenceEqual(png)
            && retained.Assets.Any(item => item.Id == export.Id)
            && store.ReadAsset(first.Id, returned.Token, export.Id).Bytes.Length > 0,
            "aged retention includes exact original and durable previously created export bytes");
        var latePortraitJob = store.EnqueuePortrait(first.Id, returned.Token, upload.Id, edition.Id, "portrait_only", true, "lateportrait");
        var latePortrait = store.LeaseNext()!;
        var deletionRequest = store.Dispatch(latePortrait)!;
        store.AfterAssetFlush = kind =>
        {
            if (kind == "photo_original") { throw new InvalidOperationException("injected_after_flush_before_commit"); }
        };
        try
        {
            using var orphanInput = new MemoryStream(png);
            await store.AcceptPhotoAsync(first.Id, returned.Token, orphanInput, "image/png", "orphan", retained.PortraitConsentVersion);
            throw new InvalidOperationException("Expected injected storage failure.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "injected_after_flush_before_commit") { }
        finally { store.AfterAssetFlush = null; }
        var photosRoot = Path.Combine(directory, "content", first.Id, "photos");
        Check(Directory.GetDirectories(photosRoot).Length == 2, "fault injection leaves uncommitted photo directory outside document");
        var current = store.Read(first.Id, returned.Token);
        Expect(() => store.Delete(first.Id, returned.Token, KeepsakeDeleteScope.All, "wrongrev", current.Revision - 1), "revision_changed");
        var photoReceipt = store.Delete(first.Id, returned.Token, KeepsakeDeleteScope.PhotosAndPortraits, "deletephoto", current.Revision);
        Check(photoReceipt.LocalState == "local_cleanup_complete", "partial deletion finishes local closure");
        Check(!Directory.EnumerateFiles(photosRoot, "*", SearchOption.AllDirectories).Any(), "partial delete physically closes orphan and committed photo directories");
        Check(!store.Complete(latePortrait, await provider.GenerateAsync(deletionRequest, default)), "deleted photo epoch rejects late portrait bytes");
        var afterPhotoDelete = store.Read(first.Id, returned.Token);
        Check(afterPhotoDelete.Uploads.Count == 0 && afterPhotoDelete.Portraits.Count == 0
            && afterPhotoDelete.Assets.All(item => item.UploadId is null), "manual photo delete includes originals, normalized, portraits and portrait exports");
        var all = store.Delete(first.Id, returned.Token, KeepsakeDeleteScope.All, "deleteall", afterPhotoDelete.Revision);
        Check(all.LocalState == "local_cleanup_complete" && all.ProviderState.Contains("unknown")
            && store.GetDeletionReceipt(all.Id, returned.Token) == all, "receipt remains narrow and honest after access revocation");
        Check(!store.Authorize(first.Id, returned.Token, KeepsakeScope.Read)
            && !Directory.Exists(Path.Combine(directory, "content", first.Id)), "all delete revokes grants and physically removes scoped files");
        Check(!store.Complete(restartLease, output), "all delete rejects late history ingestion");
        Expect(() => store.GetDeletionReceipt(all.Id, stranger.Token), "invalid_session");
        Expect(() => store.Issue(Snapshot("one")), "outcome_deleted");
        var partial = store.Issue(Snapshot("partial"));
        var partialClaim = store.IssueClaim(partial.Id, "device");
        store.Claim(partialClaim.Token, returned.Token, "partial-claim");
        var partialLease = store.LeaseNext()!;
        var partialRequest = store.Dispatch(partialLease)!;
        var partialHandler = new ProviderHandler(Png()) { FailImage = true };
        using (var realAdapter = new OpenAiKeepsakeProvider(new HttpClient(partialHandler), "synthetic-key", "gpt-6-sol", "gpt-image-2.5-flare"))
        {
            try { await realAdapter.GenerateAsync(partialRequest, default); }
            catch (Exception) { store.MarkAmbiguous(partialLease, "synthetic_image_failure"); }
        }
        var partialView = store.Read(partial.Id, returned.Token);
        Check(partialView.State == "story_ready" && partialView.Editions.Single().PublicationState == "story_ready"
            && partialView.Editions.Single().ArtworkAssetId == "", "completed structured story remains usable after later image failure");
        var partialEdition = partialView.Editions.Single();
        var partialZip = store.CreateExport(partial.Id, returned.Token, partialEdition.Id, "zip", null, "partial-export");
        using (var zip = new ZipArchive(new MemoryStream(store.ReadAsset(partial.Id, returned.Token, partialZip.Id).Bytes)))
        {
            Check(zip.Entries.Count == 3 && zip.Entries.Any(item => item.FullName == "story.txt")
                && zip.Entries.Any(item => item.FullName == "keepsake-card.png")
                && zip.Entries.Any(item => item.FullName == "manifest.json"), "partial ZIP contains ready identity card, story and honest manifest without invented artwork");
        }
        var restoreKeepsake = store.Issue(Snapshot("restore"));
        var restoreInitialClaim = store.IssueClaim(restoreKeepsake.Id, "device", "restore-initial");
        store.Claim(restoreInitialClaim.Token, returned.Token, "restore-initial-claim");
        var restoreLease = store.LeaseNext()!;
        var restoreRequest = store.Dispatch(restoreLease)!;
        store.Complete(restoreLease, await provider.GenerateAsync(restoreRequest, default));
        var restoreEdition = store.Read(restoreKeepsake.Id, returned.Token).Editions.Single();
        var restoreShare = store.CreateShare(restoreKeepsake.Id, returned.Token, restoreEdition.Id, "restore-share");
        var restoreReader = store.Bootstrap();
        store.ShareClaim(restoreShare.Token, restoreReader.Token, "restore-share-claim");
        Expect(() => store.IssueClaim(restoreKeepsake.Id, "device", "restore-naive-reissue"), "operator_restore_required");
        var restoredClaim = store.RestoreOwnerClaim(restoreKeepsake.Id, "explicit-operator-restore");
        Check(store.RestoreOwnerClaim(restoreKeepsake.Id, "explicit-operator-restore").Token == restoredClaim.Token,
            "explicit operator restore response is idempotent");
        Check(!store.Authorize(restoreKeepsake.Id, returned.Token, KeepsakeScope.Read)
            && !store.Authorize(restoreKeepsake.Id, restoreReader.Token, KeepsakeScope.Read, true, restoreEdition.Id),
            "operator restore revokes previous owner and share authority atomically");
        var restoredOwner = store.Bootstrap();
        store.Claim(restoredClaim.Token, restoredOwner.Token, "restored-claim");
        var restoredView = store.Read(restoreKeepsake.Id, restoredOwner.Token);
        Check(restoredView.Editions.Single().Id == restoreEdition.Id && restoredView.Jobs.Count == 1,
            "operator restore reopens exact retained edition without additional inference");
        var flagConsent = store.SetPortraitConsent(restoreKeepsake.Id, restoredOwner.Token, true, "dv10-portrait-v1", "flags-consent");
        using var flagInput = new MemoryStream(png);
        var flagUpload = await store.AcceptPhotoAsync(restoreKeepsake.Id, restoredOwner.Token, flagInput, "image/png", "flags-photo", flagConsent);
        var pausedPortraitJob = store.EnqueuePortrait(restoreKeepsake.Id, restoredOwner.Token, flagUpload.Id, restoreEdition.Id,
            "portrait_only", true, "flags-portrait");
        configuration["Keepsake:PortraitEnabled"] = "false";
        Check(store.LeaseNext() is null && store.Read(restoreKeepsake.Id, restoredOwner.Token).Jobs
            .Single(item => item.Id == pausedPortraitJob.Id).State == "queued", "disabled portrait retains queued content without dispatch");
        store.EnqueueBase(restoreKeepsake.Id, restoredOwner.Token, new(), "flags-base");
        var flagBaseLease = store.LeaseNext()!;
        Check(flagBaseLease.Kind == "base", "base generation continues while portrait capability is disabled");
        configuration["Keepsake:GenerationEnabled"] = "false";
        Check(store.Dispatch(flagBaseLease) is null, "capability disabled after lease prevents actual provider dispatch");
        store = new KeepsakeStore(configuration, clock);
        Check(store.LeaseNext() is null && store.Read(restoreKeepsake.Id, restoredOwner.Token).Jobs
            .Single(item => item.Id == flagBaseLease.Id).State == "queued", "restart with provider generation disabled pauses retained queue");
        configuration["Keepsake:GenerationEnabled"] = "true";
        var resumedBase = store.LeaseNext()!;
        Check(resumedBase.Id == flagBaseLease.Id && resumedBase.Fence > flagBaseLease.Fence,
            "explicit capability restoration resumes the same never-dispatched durable job");
        var resumedRequest = store.Dispatch(resumedBase)!;
        store.Complete(resumedBase, await provider.GenerateAsync(resumedRequest, default));
    }

    private static async Task ProviderBoundaryAsync()
    {
        var handler = new ProviderHandler(Png());
        using var provider = new OpenAiKeepsakeProvider(new HttpClient(handler), "test-key-no-live-access", "gpt-6-sol", "gpt-image-2.5-flare");
        var request = new KeepsakeProviderRequest(Guid.NewGuid().ToString("D"), Snapshot("provider"), new(),
            Guid.NewGuid().ToString("D"), null, null, null, null, null);
        var result = await provider.GenerateAsync(request, default);
        Check(handler.Requests.Count == 2 && handler.Requests[0].Path.EndsWith("/responses")
            && handler.Requests[1].Path.EndsWith("/images/generations"), "actual official SDK performs story then image generate");
        using (var payload = JsonDocument.Parse(handler.Requests[1].Body))
        {
            Check(!payload.RootElement.TryGetProperty("image", out _) && !payload.RootElement.TryGetProperty("input_images", out _)
                && payload.RootElement.GetProperty("model").GetString() == "gpt-image-2.5-flare", "serialized wand generate has only text, configured model and supported parameters");
        }
        var editRequest = request with { EditionId = Guid.NewGuid().ToString("D"), UploadId = Guid.NewGuid().ToString("D"),
            Variant = "portrait_only", NormalizedPhoto = Png(), ExistingStory = result.Story };
        await provider.GenerateAsync(editRequest, default);
        Check(handler.Requests.Last().Path.EndsWith("/images/edits") && handler.Requests.Last().Body.Contains("visitor.png")
            && !handler.Requests.Last().Path.EndsWith("generations"), "portrait uses actual multipart photo edit, never text-to-image fallback");
        handler.Fail = true;
        try { await provider.GenerateAsync(request, default); } catch (Exception) { }
        Check(handler.FailedRequests == 1, "SDK retries disabled for ambiguous paid attempts");
    }

    internal static byte[] Png()
    {
        using var bitmap = new SKBitmap(24, 32);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
    private static void CardLayout()
    {
        using var stream = typeof(KeepsakeStore).Assembly.GetManifestResourceStream("Kufr.Backend.KeepsakeInter")!;
        using var typeface = SKTypeface.FromStream(stream);
        using var font = new SKFont(typeface, 42);
        var longName = new string('Ž', 160);
        var wrapped = KeepsakeStore.WrapMeasured(longName, font, 1060).ToArray();
        Check(wrapped.Length > 1 && wrapped.All(line => font.MeasureText(line) <= 1060)
            && string.Concat(wrapped) == longName, "measured title wrapping handles long unbroken Czech catalog text without clipping or loss");
        var snapshot = Snapshot("layout") with { OfficialName = longName };
        var story = new KeepsakeStory(longName, string.Join(" ", Enumerable.Repeat("Čarodějná dílna", 200)),
            new("shape", "handle", "ornament", "material", "theme"));
        var edition = new KeepsakeEdition(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), story, "fixture",
            true, "fake_synthetic", "synthetic", "fixture", DateTimeOffset.UtcNow);
        var card = KeepsakeStore.BuildPngCard(snapshot, edition, Png(), Png());
        using var codec = SKCodec.Create(new MemoryStream(card));
        Check(codec is not null && codec.Info.Width == 1200 && codec.Info.Height >= 2680
            && codec.EncodedFormat == SKEncodedImageFormat.Png, "long title plus portrait exports actual dynamically sized PNG with embedded font");
        using var rendered = SKBitmap.Decode(card);
        Check(rendered.GetPixel(0, 0) == SKColor.Parse("#1B2430")
            && rendered.GetPixel(30, 30) == SKColor.Parse("#8A94A6"),
            "rendered card uses Midnight background and Steel frame");
        using var titleFont = new SKFont(typeface, 30);
        var artworkTop = 110 + wrapped.Length * 52 + KeepsakeStore.WrapMeasured(story.Title, titleFont, 1060).Count() * 40;
        Check(rendered.GetPixel(600, artworkTop + 500) == SKColors.Teal,
            "card chrome preserves the synthetic artwork colors");

        var artwork = Png();
        var svg = XDocument.Parse(KeepsakeStore.BuildCard(snapshot, edition, "image/png", artwork, null));
        XNamespace svgNamespace = "http://www.w3.org/2000/svg";
        var group = svg.Root!.Element(svgNamespace + "g")!;
        var images = group.Elements(svgNamespace + "image").ToArray();
        Check(images.Length == 1 && images[0].Attribute("href")!.Value == "data:image/png;base64," + Convert.ToBase64String(artwork),
            "SVG export retains exact artwork bytes and omits an unrequested portrait");
        Check(svg.Root.Element(svgNamespace + "rect")!.Attribute("fill")!.Value == "#1B2430"
            && group.Attribute("fill")!.Value == "#EEF1F5"
            && group.Attribute("font-family")!.Value == "Inter,Arial,sans-serif"
            && group.Elements(svgNamespace + "text").ElementAt(1).Attribute("fill")!.Value == "#B8C0FF",
            "standalone SVG uses Vapour Chrome typography and frame hierarchy");
        Check(group.Elements(svgNamespace + "text").First().Value == snapshot.OfficialName
            && group.Elements(svgNamespace + "text").Last().Value == "SYNTHETICKÝ TEST — BEZ OPENAI",
            "catalog identity and synthetic provenance remain visible on the exported card");
    }
    private static KeepsakeIssueSnapshot Snapshot(string outcome) => new(outcome, "device", 2,
        "stable-wand-two", "Katalogová hůlka", "Neměnný katalogový údaj", "timed_return_estimate_not_sensor");
    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Keepsake assertion: " + message); }
    }
    private static void Expect(Action action, string code)
    {
        try { action(); } catch (KeepsakeException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected Keepsake error: " + code);
    }
    private static async Task ExpectAsync(Func<Task> action, string code)
    {
        try { await action(); } catch (KeepsakeException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected Keepsake error: " + code);
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset time = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => time;
        public void Advance(TimeSpan elapsed) => time += elapsed;
    }
    private sealed class PausedReadStream : Stream
    {
        public TaskCompletionSource<bool> ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult(true);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CountingReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public int ReadCalls { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            return base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class ProviderHandler(byte[] png) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];
        public bool Fail { get; set; }
        public bool FailImage { get; set; }
        public int FailedRequests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (Fail || (FailImage && path.Contains("/images/"))) { FailedRequests++; return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":{\"message\":\"synthetic failure\"}}") }; }
            object body;
            if (path.EndsWith("/responses"))
            {
                var story = new KeepsakeStory("Smyšlený příběh", string.Join(" ", Enumerable.Repeat("kouzlo", 210)),
                    new("shape", "handle", "ornament", "material", "theme"));
                body = new { id = "resp_fixture", status = "completed", model = "gpt-6-sol", output = new[]
                {
                    new { type = "message", id = "msg_fixture", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(story, new JsonSerializerOptions(JsonSerializerDefaults.Web)), annotations = Array.Empty<object>() } } }
                } };
            }
            else { body = new { created = 1, data = new[] { new { b64_json = Convert.ToBase64String(png) } } }; }
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}

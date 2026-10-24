using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Wands;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

// Real SDK serialization and durable lifecycle; all HTTP responses and artwork are synthetic.
internal static class WandArtworkTests
{
    internal static async Task RunAsync()
    {
        await ProviderBoundaries();
        var directory = Path.Combine(Path.GetTempPath(), "wand-artwork-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await Lifecycle(directory); await QueueRecovery(directory); }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
        Console.WriteLine("Canonical wand artwork: SDK image-only/story-only dispatch, immutable reuse, explicit recovery, restart fencing, publication and account isolation passed (synthetic providers).");
    }

    private static async Task Lifecycle(string directory)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "backend.sqlite"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "assets"), ["Keepsake:Enabled"] = "true",
            ["Keepsake:GenerationEnabled"] = "true", ["Keepsake:Origin"] = "https://synthetic.test"
        }).Build();
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(directory, "keys")));
        var store = new KeepsakeStore(configuration, TimeProvider.System, protection);
        var wands = new WandGenerationStore(configuration, TimeProvider.System);
        var owner = KeepsakeActor.ForAccount("alice", "alice-live-session", () => true);
        var foreign = KeepsakeActor.ForAccount("bob", "bob-live-session", () => true);
        var provider = new FakeKeepsakeProvider();
        var wand = await Select("alice");
        var evening = Guid.NewGuid().ToString("D");
        var snapshot = Snapshot(wand, "outcome-alice");
        Check(wands.LatestBonded("alice", wand.ProfileId, 1) is null, "selection is pending until physical outcome is issued");
        var issued = store.Issue(snapshot, new("alice", "creation-alice"));
        Check(store.Issue(snapshot, new("alice", "creation-alice")).Id == issued.Id, "physical outcome replay is idempotent");
        ExpectCode(() => store.Issue(snapshot with { OutcomeId = "another-outcome" }, new("alice", "creation-alice")), "wand_already_bonded");
        ExpectCode(() => store.EnqueueBase(issued.Id, owner, new(), "premature-story"), "wand_artwork_not_ready");
        Check(store.Read(issued.Id, owner).Editions.Count == 0, "issue does not wait for or invent a keepsake story");
        var initialJobs = store.Read(issued.Id, owner).Jobs;
        Check(initialJobs.Count(job => job.Kind == "wand_artwork") == 1, "six candidates produce only one selected-wand artwork job");
        Check(wands.LatestBonded("alice", wand.ProfileId, 1)?.WandId == wand.WandId, "confirmed issue establishes the bond before artwork completion");
        var first = store.LeaseNext()!;
        Check(first.Kind == "wand_artwork", "artwork has priority and does not depend on the later story");
        var firstRequest = store.Dispatch(first)!;
        Check(firstRequest.GenerationKind == "wand_artwork" && firstRequest.EditionId is null && firstRequest.Snapshot.GeneratedWand?.WandId == wand.WandId,
            "image-only job pins exact immutable selected wand");
        var delayedOutput = await provider.GenerateAsync(firstRequest, default);
        // Simulate process death after dispatch: startup must retain the uncertain attempt.
        store = new KeepsakeStore(configuration, TimeProvider.System, protection);
        Check(store.LeaseNext() is null, "restart does not replay an ambiguous paid image attempt or dispatch story before artwork");
        Check(store.Read(issued.Id, owner).Artwork?.CanRetry == true, "ambiguous provider attempt is visibly recoverable by explicit action");
        ExpectDenied(() => store.RetryWandArtwork(issued.Id, foreign, "foreign-retry"));
        var retry = store.RetryWandArtwork(issued.Id, owner, "explicit-retry");
        Check(store.RetryWandArtwork(issued.Id, owner, "explicit-retry").Id == retry.Id,
            "repeated explicit retry key queues one attempt");
        var retryLease = store.LeaseNext()!;
        Check(retryLease.Id == retry.Id && retryLease.Kind == "wand_artwork", "retry leases the single newly authorized image attempt");
        var retryRequest = store.Dispatch(retryLease)!;
        var output = await provider.GenerateAsync(retryRequest, default);
        Check(output.Story is null && output.ImageBytes.Length > 0, "artwork provider does no story generation");
        Check(store.Complete(retryLease, output), "image publishes before any keepsake edition exists");
        var early = store.Read(issued.Id, owner);
        var canonical = store.FindOwnedWandArtwork("alice", wand.WandId)!;
        Check(early.Editions.Count == 0 && canonical.AssetId is not null && early.Artwork?.AssetId == canonical.AssetId,
            "profile receives canonical artwork independently of keepsake story");
        var savedImage = store.ReadAsset(issued.Id, owner, canonical.AssetId!);
        using (var bitmap = SKBitmap.Decode(savedImage.Bytes)) Check(bitmap is not null && bitmap.Width > 1 && bitmap.Height > 1,
            "stored fake-provider artwork decodes into actual image pixels");
        Check(!store.Complete(first, delayedOutput), "late output from prior failed attempt cannot replace canonical image");
        Check(!store.Complete(retryLease, output), "duplicate provider completion cannot append a second canonical image");
        Check(store.RetryWandArtwork(issued.Id, owner, "after-success").Id == retry.Id,
            "recovery after success returns original job instead of replacing published illustration");
        Check(store.FindOwnedWandArtwork("bob", wand.WandId) is null, "foreign account cannot resolve artwork identifiers");
        ExpectDenied(() => store.ReadAsset(issued.Id, foreign, canonical.AssetId!));

        var baseJob = store.LeaseNext()!;
        Check(baseJob.Kind == "base", "keepsake story starts after canonical artwork is saved");
        var baseRequest = store.Dispatch(baseJob)!;
        Check(baseRequest.GenerationKind == "base" && baseRequest.CanonicalArtworkAssetId == canonical.AssetId,
            "story job is explicitly pinned to existing canonical artwork");
        var storyOutput = await provider.GenerateAsync(baseRequest, default);
        Check(storyOutput.ImageBytes.Length == 0 && storyOutput.Story is not null, "personalized story never generates duplicate artwork");
        Check(store.Complete(baseJob, storyOutput with { ImageBytes = DifferentPng() }),
            "base ingestion ignores contradictory unsolicited image bytes and reuses canonical asset");
        var ready = store.Read(issued.Id, owner);
        Check(ready.Editions.Single().ArtworkAssetId == canonical.AssetId
            && store.ReadCustomerSessionResult("creation-alice", owner).ArtworkAssetId == canonical.AssetId,
            "profile, customer result and keepsake all reference exact same saved artwork ID");
        var replacement = store.EnqueueBase(issued.Id, owner, new(), "story-edition-two");
        var replacementLease = store.LeaseNext()!;
        Check(replacementLease.Id == replacement.Id, "explicit story edition leases requested job");
        Check(store.Complete(replacementLease, await provider.GenerateAsync(store.Dispatch(replacementLease)!, default)), "second story edition completes");
        ready = store.Read(issued.Id, owner);
        Check(ready.Editions.Count == 2 && ready.Editions.All(edition => edition.ArtworkAssetId == canonical.AssetId)
            && ready.Assets.Count(asset => asset.Kind == "base_image") == 1,
            "new story editions preserve one immutable artwork asset per wand");
        Check(store.ReadEveningGallery("device", evening).Items.Count == 0, "canonical art is private before explicit edition publication");
        var latest = ready.Editions.Last();
        ready = store.SetWandPublication(issued.Id, owner, true, latest.Id, ready.Revision);
        while (store.ProcessOneBaseExport()) { }
        var gallery = store.ReadEveningGallery("device", evening).Items.Single();
        Check(gallery.Assets.Any(asset => asset.Kind == "export_card") && gallery.Assets.Any(asset => asset.Kind == "export_zip"),
            "local collectible derivatives export successfully from canonical artwork");
        Check(gallery.Assets.Single(asset => asset.Kind == "base_image").Id == canonical.AssetId,
            "approved public edition reuses canonical image even though asset predates story edition");
        using (var publicImage = store.OpenGalleryAsset("device", evening, issued.Id, latest.Id, canonical.AssetId!).Stream)
        {
            using var copy = new MemoryStream(); await publicImage.CopyToAsync(copy);
            Check(copy.ToArray().SequenceEqual(savedImage.Bytes), "public image bytes equal private canonical artwork bytes");
        }
        ready = store.Read(issued.Id, owner);
        ready = store.SetWandPublication(issued.Id, owner, false, latest.Id, ready.Revision);
        foreach (var publishedAsset in gallery.Assets)
            ExpectDenied(() => store.OpenGalleryAsset("device", evening, issued.Id, latest.Id, publishedAsset.Id).Stream.Dispose());
        Check(store.ReadAsset(issued.Id, owner, canonical.AssetId!).Bytes.SequenceEqual(savedImage.Bytes),
            "publication withdrawal removes public access while preserving owner artwork");

        // A pre-upgrade archive could contain two independently generated images.
        // Preserve the illustration already selected for publication and retire contradictory shares at startup.
        var firstEdition = ready.Editions.First();
        var obsoleteShare = store.CreateShare(issued.Id, owner, firstEdition.Id, "legacy-share");
        var obsoleteReader = store.Bootstrap();
        store.ShareClaim(obsoleteShare.Token, obsoleteReader.Token, "legacy-share-claim");
        var legacyAssetId = Guid.NewGuid().ToString("D");
        var legacyBytes = DifferentPng();
        var legacyAsset = new KeepsakeAsset(legacyAssetId, "base_image", "image/png", Convert.ToHexString(SHA256.HashData(legacyBytes)),
            legacyBytes.Length, latest.Id, null, DateTimeOffset.UtcNow);
        File.WriteAllBytes(Path.Combine(configuration["Keepsake:DataRoot"]!, issued.Id, legacyAssetId + ".bin"), legacyBytes);
        using (var connection = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]))
        {
            connection.Open();
            using var query = connection.CreateCommand(); query.CommandText = "SELECT document FROM dv10_keepsakes WHERE id=$id"; query.Parameters.AddWithValue("$id", issued.Id);
            var document = JsonNode.Parse((string)query.ExecuteScalar()!)!;
            document["artwork"] = null;
            document["currentArtworkJob"] = null;
            var editions = document["editions"]!.AsArray();
            editions.Single(edition => edition!["id"]!.GetValue<string>() == latest.Id)!["artworkAssetId"] = legacyAssetId;
            document["assets"]!.AsArray().Add(JsonSerializer.SerializeToNode(legacyAsset, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            using var update = connection.CreateCommand(); update.CommandText = "UPDATE dv10_keepsakes SET document=$document WHERE id=$id";
            update.Parameters.AddWithValue("$document", document.ToJsonString()); update.Parameters.AddWithValue("$id", issued.Id); update.ExecuteNonQuery();
        }
        store = new KeepsakeStore(configuration, TimeProvider.System, protection);
        var reconciled = store.Read(issued.Id, owner);
        Check(store.FindOwnedWandArtwork("alice", wand.WandId)?.AssetId == legacyAssetId
            && store.ReadCustomerSessionResult("creation-alice", owner).ArtworkAssetId == legacyAssetId,
            "startup adopts currently published legacy illustration consistently across profile and result");
        Check(reconciled.Editions.Single(edition => edition.Id == firstEdition.Id).PublicationState == "private_history"
            && reconciled.Editions.Where(edition => edition.PublicationState != "private_history").All(edition => edition.ArtworkAssetId == legacyAssetId),
            "contradictory legacy edition is retained privately and cannot substitute another public image");
        ExpectDenied(() => store.Read(issued.Id, obsoleteReader.Token, true, firstEdition.Id));
        Check(store.LeaseNext() is null && store.ReadAsset(issued.Id, owner, legacyAssetId).Bytes.SequenceEqual(legacyBytes),
            "legacy adoption saves existing bytes and never schedules a replacement illustration");

        var bobWand = await Select("bob");
        var bobIssued = store.Issue(Snapshot(bobWand, "outcome-bob"), new("bob", "creation-bob"));
        var bobJob = store.LeaseNext()!;
        Check(bobJob.Kind == "wand_artwork", "second character receives independent artwork job");
        var bobOutput = await provider.GenerateAsync(store.Dispatch(bobJob)!, default);
        Check(store.Complete(bobJob, bobOutput), "second independent artwork publishes");
        var bobArtwork = store.FindOwnedWandArtwork("bob", bobWand.WandId)!;
        Check(bobArtwork.AssetId != canonical.AssetId && bobArtwork.Locator != canonical.Locator
            && !store.ReadAsset(bobIssued.Id, foreign, bobArtwork.AssetId!).Bytes.SequenceEqual(savedImage.Bytes),
            "two characters sharing a physical slot have different wand identities and image assets/pixels");
        store.Delete(issued.Id, owner, KeepsakeDeleteScope.All, "delete-artwork");
        Check(store.FindOwnedWandArtwork("alice", wand.WandId) is null, "deletion removes canonical artwork projection");
        ExpectDenied(() => store.ReadAsset(issued.Id, owner, canonical.AssetId!));
        Check(!Directory.Exists(Path.Combine(configuration["Keepsake:DataRoot"]!, issued.Id)), "deletion removes stored image and provenance files");
        Check(store.ReadAsset(bobIssued.Id, foreign, bobArtwork.AssetId!).Bytes.Length > 0, "other account image survives deletion");
        // Account deletion uses the same asset inventory, tombstones and provider fencing.
        store.FenceAccountDeletion("bob");
        Check(store.DeleteAccountContent("bob"), "account deletion finishes retained artwork cleanup");
        Check(store.FindOwnedWandArtwork("bob", bobWand.WandId) is null
            && !Directory.Exists(Path.Combine(configuration["Keepsake:DataRoot"]!, bobIssued.Id)),
            "account deletion removes canonical artwork, provenance and pending story assets");
        ExpectDenied(() => store.ReadAsset(bobIssued.Id, foreign, bobArtwork.AssetId!));
        // A returned image after deletion must not recreate the deleted account's files.
        Check(!store.Complete(bobJob, bobOutput),
            "late provider completion cannot recreate deleted artwork");

        async Task<GeneratedWandSnapshot> Select(string account)
        {
            var profile = new CharacterConversationSnapshot(account + "-profile", 1,
                new(account, "Synthetic fantasy", Language: "cs"), null, 1);
            wands.Begin(account, "select-" + account, profile, 63, true);
            await new WandGenerationService(wands, new CandidateProvider()).GenerateAsync(account, "select-" + account, _ => Task.FromResult(true));
            return wands.Select(account, "select-" + account, profile.ProfileId, 1, 1);
        }
        KeepsakeIssueSnapshot Snapshot(GeneratedWandSnapshot selected, string outcome) => new(outcome, "device", selected.PhysicalSlot,
            selected.WandId, selected.Name, selected.Lore, "synthetic physical confirmation", "generated-wand-v1", evening,
            GeneratedWand: selected.ToPublic());
    }

    private static async Task QueueRecovery(string directory)
    {
        // Force deterministic queue ordering and a single slot to exercise admission under pressure.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "queue.sqlite"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "queue-assets"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:MaxQueuedJobs"] = "1", ["Keepsake:Origin"] = "https://synthetic.test"
        }).Build();
        var store = new KeepsakeStore(config, TimeProvider.System);
        var wands = new WandGenerationStore(config, TimeProvider.System);
        var provider = new FakeKeepsakeProvider();
        var a = await Issue("queue-a");
        var first = store.LeaseNext()!;
        Check(first.KeepsakeId == a && first.Kind == "wand_artwork", "single capacity starts first selected wand artwork");
        Check(store.Dispatch(first) is not null, "first queue image attempt is dispatched");
        var b = await Issue("queue-b");
        Check(store.LeaseNext() is null, "second selected wand stays deferred while one image dispatch consumes capacity");
        store.MarkAmbiguous(first, "provider_attempt_unknown");
        // A sorts first, so admitting B proves failed A does not starve later deferred jobs.
        SetSql("UPDATE dv10_keepsakes SET id='00000000-0000-4000-8000-000000000001' WHERE id=$id", a);
        SetSql("UPDATE dv10_jobs SET keepsake_id='00000000-0000-4000-8000-000000000001' WHERE keepsake_id=$id", a);
        var second = store.LeaseNext()!;
        Check(second.KeepsakeId == b && second.Kind == "wand_artwork", "failed earlier artwork does not starve another character's deferred image");
        Check(store.Complete(second, await provider.GenerateAsync(store.Dispatch(second)!, default)), "second deferred image completes without retrying first failure");
        var secondStory = store.LeaseNext()!;
        Check(store.Complete(secondStory, await provider.GenerateAsync(store.Dispatch(secondStory)!, default)), "second character completes story after canonical image");

        var c = await Issue("queue-upgrade");
        // Build the exact pre-upgrade state: one queued base, currentBaseJob set,
        // no artwork/job metadata and no edition. No provider has run for this fixture.
        SetSql("UPDATE dv10_jobs SET kind='base', request_json=json_set(request_json,'$.editionId','22222222-2222-4222-8222-222222222222') WHERE keepsake_id=$id", c);
        SetSql("UPDATE dv10_keepsakes SET document=json_set(json_remove(document,'$.currentArtworkJob','$.artwork'),'$.currentBaseJob',(SELECT id FROM dv10_jobs WHERE keepsake_id=$id LIMIT 1),'$.needsBaseGeneration',json('false')) WHERE id=$id", c);
        store = new KeepsakeStore(config, TimeProvider.System);
        var upgraded = store.LeaseNext();
        Check(upgraded?.KeepsakeId == c && upgraded.Kind == "wand_artwork", "upgrade admits required image ahead of old queued base even at capacity one");
        var upgradedRequest = store.Dispatch(upgraded!)!;
        Check(upgradedRequest.GenerationKind == "wand_artwork", "old story cannot dispatch until its canonical illustration exists");
        Check(store.Complete(upgraded!, await provider.GenerateAsync(upgradedRequest, default)), "upgraded artwork completes");
        var oldBase = store.LeaseNext()!;
        var oldBaseRequest = store.Dispatch(oldBase)!;
        Check(oldBase.KeepsakeId == c && oldBase.Kind == "base" && oldBaseRequest.CanonicalArtworkAssetId is not null,
            "old queued story resumes using exact newly saved canonical illustration");
        Check(store.Complete(oldBase, await provider.GenerateAsync(oldBaseRequest, default)), "old queued story completes without another image");

        async Task<string> Issue(string account)
        {
            var profile = new CharacterConversationSnapshot(account + "-profile", 1, new(account, "Synthetic fantasy"), null, 1);
            wands.Begin(account, "select", profile, 63, true);
            await new WandGenerationService(wands, new CandidateProvider()).GenerateAsync(account, "select", _ => Task.FromResult(true));
            var selected = wands.Select(account, "select", profile.ProfileId, 1, 1);
            return store.Issue(new(account, "device", 1, selected.WandId, selected.Name, selected.Lore,
                "synthetic", "generated-wand-v1", GeneratedWand: selected.ToPublic()), new(account, account + "-session")).Id;
        }
        void SetSql(string sql, string id)
        {
            using var connection = new SqliteConnection("Data Source=" + config["Backend:DatabasePath"]); connection.Open();
            using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id); command.ExecuteNonQuery();
        }
    }

    private static async Task ProviderBoundaries()
    {
        var wand = new GeneratedWandPublicSnapshot("canonical-wand", 1, "Astra", "Meteorite wood", "Tidal thread", "Cautiously sarcastic",
            "A fictional relic of a drowned observatory.", "Forked tip, carved lunar handle, silver inlaid rings", "Tides", "Static air",
            [new("strike", "Tidal lash", "A wave of light"), new("trick", "Moon lie", "A decoy"), new("shield", "Quiet harbor", "A barrier")]);
        var handler = new Handler(KeepsakeCoreTests.Png());
        using var provider = new OpenAiKeepsakeProvider(new HttpClient(handler), "synthetic-key-never-sent-to-network", "synthetic-story-model", "synthetic-image-model");
        var snapshot = new KeepsakeIssueSnapshot("private-outcome", "private-device", 1, wand.WandId, wand.Name, wand.Lore, "synthetic", GeneratedWand: wand);
        var request = new KeepsakeProviderRequest(Guid.NewGuid().ToString("D"), snapshot, new(), Guid.NewGuid().ToString("D"), null, null, null, null, null)
            { GenerationKind = "wand_artwork" };
        try
        {
            await provider.GenerateAsync(request with { GenerationKind = "base" }, default);
            throw new InvalidOperationException("Story dispatched without canonical image");
        }
        catch (KeepsakeException exception) when (exception.Code == "wand_artwork_not_ready") { }
        Check(handler.Requests.Count == 0, "missing artwork preflight blocks paid story request before SDK transport");
        var image = await provider.GenerateAsync(request, default);
        Check(handler.Requests.Count == 1 && handler.Requests[0].Path.EndsWith("/images/generations") && image.Story is null,
            "actual SDK illustration dispatch performs one image generation and no story request");
        using (var parsed = JsonDocument.Parse(handler.Requests[0].Body))
        {
            var payload = parsed.RootElement;
            var prompt = payload.GetProperty("prompt").GetString()!;
            Check(payload.GetProperty("model").GetString() == "synthetic-image-model" && !payload.TryGetProperty("image", out _),
                "image uses configured SDK generation model without another image pipeline");
            Check(new[] { wand.Name, wand.Material, wand.Core, wand.Temperament, wand.DesignDescription, wand.Affinity, wand.Weakness, wand.Lore }.All(prompt.Contains),
                "image prompt describes exact selected silhouette, craftsmanship, personality, lore and affinity");
            Check(!prompt.Contains("private-outcome") && !prompt.Contains("private-device"), "image prompt excludes private authorization and physical outcome IDs");
        }
        var story = await provider.GenerateAsync(request with { GenerationKind = "base", EditionId = Guid.NewGuid().ToString("D"), CanonicalArtworkAssetId = Guid.NewGuid().ToString("D") }, default);
        Check(handler.Requests.Count == 2 && handler.Requests[1].Path.EndsWith("/responses") && story.ImageBytes.Length == 0,
            "later personalized keepsake performs story-only SDK request and reuses canonical image reference");
        handler.Fail = true;
        try { await provider.GenerateAsync(request, default); throw new InvalidOperationException("Synthetic provider failure unexpectedly succeeded"); }
        catch (Exception exception) when (exception is not InvalidOperationException) { }
        Check(handler.Requests.Count == 3, "SDK performs no uncontrolled retry on ambiguous image failure");
    }

    private static byte[] DifferentPng()
    {
        using var bitmap = new SKBitmap(32, 48); bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException("Wand artwork: " + message); }
    private static void ExpectCode(Action action, string code)
    {
        try { action(); }
        catch (KeepsakeException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Wand artwork: expected " + code);
    }
    private static void ExpectDenied(Action action)
    {
        try { action(); }
        catch (KeepsakeException exception) when (exception.Status is 401 or 403 or 404 or 410) { return; }
        throw new InvalidOperationException("Wand artwork: unauthorized operation was not rejected");
    }
    private sealed class CandidateProvider : IWandGenerationProvider
    {
        public Task<WandCandidateContent[]> GenerateAsync(WandGenerationJob job, CancellationToken token) => Task.FromResult(
            new[] { "Amber", "Violet", "Emerald", "Silver", "Scarlet", "Azure" }.Select((name, index) => new WandCandidateContent(
                job.Request.Profile.UserInput.Name + " " + name, "Moonwood", "Imagined ember", "Curious", "A fictional starlit history",
                "Digital constellation design", "Stars", "Impatience", new[] { "strike", "trick", "shield" }.Select(action =>
                    new WandSpell(action, $"{index} {action} constellation", "Fictional magical effect")).ToArray())).ToArray());
    }
    private sealed class Handler(byte[] png) : HttpMessageHandler
    {
        internal List<(string Path, string Body)> Requests { get; } = [];
        internal bool Fail;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            if (Fail) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":{\"message\":\"synthetic failure\"}}") };
            object body = path.EndsWith("/responses") ? new
            {
                id = "resp_synthetic", status = "completed", model = "synthetic-story-model", output = new[] { new
                {
                    type = "message", id = "msg_synthetic", role = "assistant", status = "completed", content = new[] { new
                    {
                        type = "output_text", text = JsonSerializer.Serialize(new KeepsakeStory("Synthetic story", string.Join(" ", Enumerable.Repeat("magic", 210)),
                            new("shape", "handle", "ornament", "material", "theme")), new JsonSerializerOptions(JsonSerializerDefaults.Web)), annotations = Array.Empty<object>()
                    } }
                } }
            } : (object)new { created = 1, data = new[] { new { b64_json = Convert.ToBase64String(png) } } };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        }
    }
}

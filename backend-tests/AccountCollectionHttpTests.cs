using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunAccountCollectionHttpAsync()
    {
        await RunCompactProjectionCoverageAsync();
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var administrator = fixture.Client();
        using var customer = fixture.Client();
        using var viewer = fixture.Client();
        using var anonymous = fixture.Client();
        using var administratorKey = new VirtualPasskey();
        using var customerKey = new VirtualPasskey();
        using var viewerKey = new VirtualPasskey();

        Check((await anonymous.GetAsync("/app/v1/archive/collection")).StatusCode == HttpStatusCode.Unauthorized,
            "collection requires a live account session");
        await EnrollAsync(administrator, administratorKey, Fixture.Bootstrap, "Collection administrator");
        var administratorIdentity = await CsrfAsync(administrator);
        var administratorId = administratorIdentity.GetProperty("account").GetProperty("id").GetString()!;
        var profile = await CustomerAsync(customer, customerKey);
        var customerSession = await CsrfAsync(customer);
        var customerId = customerSession.GetProperty("account").GetProperty("id").GetString()!;

        var invitation = await JsonAsync(await administrator.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Viewer", recoveryUserId = (string?)null }));
        await EnrollAsync(viewer, viewerKey, invitation.GetProperty("token").GetString()!, "Collection linked viewer");
        var viewerId = (await CsrfAsync(viewer)).GetProperty("account").GetProperty("id").GetString()!;

        Check((await administrator.PutAsJsonAsync("/app/v1/customer/admin/devices/granted-device",
            new { enabled = true })).IsSuccessStatusCode, "fixture permits a synthetic customer session");
        var bootId = Guid.NewGuid().ToString("D");
        var challenge = new CustomerDeviceChallenge(1, bootId, Guid.NewGuid().ToString("D"), 0,
            PartyWandCatalog.Version, "1");
        var deviceToken = fixture.App.Services.GetRequiredService<DeviceAccessStore>().IssueToken("granted-device")!;
        using var device = fixture.Client();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken.AccessToken);
        Check((await device.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge)).IsSuccessStatusCode,
            "device obtains a fixture challenge over HTTPS");
        var operationId = Guid.NewGuid().ToString("D");
        var start = await JsonAsync(await customer.PostAsJsonAsync("/app/v1/customer/start-tickets", new
        {
            protocolVersion = 1, operationId, deviceId = "granted-device", bootId,
            challenge = challenge.Challenge, profileId = profile.GetProperty("profileId").GetString(),
            profileRevision = profile.GetProperty("revision").GetInt64(), openAiConsent = true
        }));
        var redemption = await JsonAsync(await device.PostAsJsonAsync("/v1/customer-sessions/redeem",
            new CustomerRedemption(1, start.GetProperty("ticket").GetString()!, operationId, bootId, challenge.Challenge)));
        var customerSessionId = redemption.GetProperty("customerSessionId").GetString()!;

        var store = fixture.App.Services.GetRequiredService<KeepsakeStore>();
        var guestKeepsake = store.Issue(new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"),
            "collection-guest-device", 1, "fixture-wand", "Explicit link fixture", "synthetic facts", "no physical evidence"));
        var guestOwner = store.Bootstrap();
        store.Claim(store.IssueClaim(guestKeepsake.Id, "collection-guest-device").Token,
            guestOwner.Token, Guid.NewGuid().ToString("D"));
        var accountProof = store.AuthorizeAccountAssociation(guestKeepsake.Id, guestOwner.Token);
        async Task<string> CreateAccountLinkTicketAsync()
        {
            using var scope = fixture.App.Services.CreateScope();
            return await AccountArchiveAssociations.IssueAsync(new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                RequestServices = scope.ServiceProvider
            }, administratorId, accountProof);
        }
        async Task<HttpResponseMessage> PostAccountLinkTicketAsync(string ticket)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/app/v1/archive/associations")
            {
                Content = JsonContent.Create(new { ticket })
            };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            return await administrator.SendAsync(request);
        }
        var firstLinkTicket = await CreateAccountLinkTicketAsync();
        var firstLinkResponse = await PostAccountLinkTicketAsync(firstLinkTicket);
        Check(firstLinkResponse.IsSuccessStatusCode,
            $"guest-owner ticket creates an explicit link through the authenticated account endpoint (HTTP {(int)firstLinkResponse.StatusCode}: {await firstLinkResponse.Content.ReadAsStringAsync()})");
        var firstCollectionRevision = await ReadAssociationRevisionAsync(fixture.App, administratorId, guestKeepsake.Id);
        var reclaimTicket = await CreateAccountLinkTicketAsync();
        var reclaimResponse = await PostAccountLinkTicketAsync(reclaimTicket);
        Check(reclaimResponse.IsSuccessStatusCode,
            $"a fresh guest-owner ticket can reclaim the same existing explicit row (HTTP {(int)reclaimResponse.StatusCode}: {await reclaimResponse.Content.ReadAsStringAsync()})");
        var reclaimedRevision = await ReadAssociationRevisionAsync(fixture.App, administratorId, guestKeepsake.Id);
        Check(reclaimedRevision > firstCollectionRevision,
            "reclaiming one explicit row advances its durable revision instead of preserving the old page position");

        var locators = new List<string>(101);
        string ownerSession = customerSessionId;
        for (var index = 0; index < 101; index++)
        {
            if (index == 1) { ownerSession = "unmatched-captured-session"; }
            var issued = store.Issue(new(Guid.NewGuid().ToString("D"), "collection-device", 1,
                    "collection-wand", "Collection keepsake " + index, "fixture catalog", "synthetic evidence"),
                new KeepsakeCustomerOwner(customerId, index == 0 ? customerSessionId : ownerSession));
            locators.Add(issued.Id);
        }
        var epoch = store.CustomerAccountLink(customerId, locators[0])!.Epoch;
        var databasePath = fixture.App.Configuration["Backend:DatabasePath"]!;
        using (var connection = new SqliteConnection("Data Source=" + databasePath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dv10_customer_owners SET linked_at=123456 WHERE owner_id=$owner";
            command.Parameters.AddWithValue("$owner", customerId);
            command.ExecuteNonQuery();
        }
        await SeedAsync(fixture.App, async db =>
        {
            var state = await db.ArchiveCollectionStates.SingleAsync(value => value.Id == 1);
            state.Revision = 3;
            db.ArchiveAssociations.Add(new AccountArchiveAssociation
            {
                UserId = viewerId, Locator = locators[0], Epoch = epoch, LinkedAt = 987654, CollectionRevision = 1
            });
            db.ArchiveAssociations.Add(new AccountArchiveAssociation
            {
                UserId = viewerId, Locator = locators[2], Epoch = epoch, LinkedAt = 887654, CollectionRevision = 2
            });
            db.ArchiveAssociations.Add(new AccountArchiveAssociation
            {
                UserId = customerId, Locator = locators[0], Epoch = epoch, LinkedAt = 999999, CollectionRevision = 2
            });
            await db.SaveChangesAsync();
        });

        var firstResponse = await customer.GetAsync("/app/v1/archive/collection?limit=10");
        var first = await JsonAsync(firstResponse);
        Check(first.GetProperty("schemaVersion").GetInt32() == 1
            && first.GetProperty("items").GetArrayLength() == 10
            && first.GetProperty("nextCursor").GetString() is { Length: > 0 },
            "collection returns the first bounded stable page for more than one hundred same-time owner bindings");
        var firstItem = first.GetProperty("items").EnumerateArray().First();
        Check(firstItem.GetProperty("accessKind").GetString() == "originalOwner"
            && firstItem.GetProperty("canManage").GetBoolean() && firstItem.GetProperty("canReadPrivate").GetBoolean()
            && !firstItem.GetProperty("canUnlink").GetBoolean(), "immutable owner binding grants only original-owner capabilities");
        var exactKeys = new HashSet<string>(StringComparer.Ordinal)
        {
            "locator", "linkedAt", "officialName", "storyTitle", "hasPortrait", "accessKind", "canManage",
            "canReadPrivate", "canUnlink", "readiness", "characterName", "eveningAt"
        };
        Check(firstItem.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal).SetEquals(exactKeys),
            "collection item exposes only the reviewed compact allowlist");

        var firstPageLocators = first.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("locator").GetString()!).ToHashSet(StringComparer.Ordinal);
        var revokedBetweenPages = locators.First(id => id != locators[0] && id != locators[2]
            && !firstPageLocators.Contains(id));
        var deletedBetweenPages = locators.First(id => id != locators[0] && id != locators[2] && id != revokedBetweenPages
            && !firstPageLocators.Contains(id));
        var raceActor = KeepsakeActor.ForAccount(customerId, "collection-race-session", () => true);
        store.RevokeAllAccess(revokedBetweenPages, raceActor);
        var deletingView = store.Read(deletedBetweenPages, raceActor);
        _ = store.Delete(deletedBetweenPages, raceActor, KeepsakeDeleteScope.All,
            "collection-delete-between-pages", deletingView.Revision);

        var ownedItems = new List<JsonElement>();
        var next = first.GetProperty("nextCursor").GetString();
        ownedItems.AddRange(first.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
        var ownedFirstResponse = await customer.GetAsync("/app/v1/archive/collection?access=owned&limit=10");
        var ownedFirst = await JsonAsync(ownedFirstResponse);
        Check(ownedFirst.GetProperty("items").GetArrayLength() == 10
            && ownedFirst.GetProperty("nextCursor").GetString() is { Length: > 0 },
            "the owned-only filter captures its own source watermark");
        var newItem = store.Issue(new(Guid.NewGuid().ToString("D"), "collection-device", 1,
                "collection-wand", "Inserted after page one", "fixture catalog", "synthetic evidence"),
            new KeepsakeCustomerOwner(customerId, "late-customer-session"));
        var originalSet = locators.Where(id => id != revokedBetweenPages && id != deletedBetweenPages)
            .ToHashSet(StringComparer.Ordinal);
        for (var pages = 0; next is not null && pages < 20; pages++)
        {
            using var response = await customer.GetAsync("/app/v1/archive/collection?limit=10&cursor=" + Uri.EscapeDataString(next));
            var page = await JsonAsync(response);
            ownedItems.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
            next = page.GetProperty("nextCursor").GetString();
        }
        Check(next is null && ownedItems.Count == 99
            && ownedItems.Select(item => item.GetProperty("locator").GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(originalSet)
            && !ownedItems.Any(item => item.GetProperty("locator").GetString() is var id
                && (id == newItem.Id || id == revokedBetweenPages || id == deletedBetweenPages)),
            "pagination excludes post-watermark inserts, revoked epochs, and content deleted between reads");
        var ownedOnlyItems = new List<JsonElement>();
        ownedOnlyItems.AddRange(ownedFirst.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
        next = ownedFirst.GetProperty("nextCursor").GetString();
        for (var pages = 0; next is not null && pages < 20; pages++)
        {
            var page = await JsonAsync(await customer.GetAsync("/app/v1/archive/collection?access=owned&limit=10&cursor="
                + Uri.EscapeDataString(next)));
            ownedOnlyItems.AddRange(page.GetProperty("items").EnumerateArray().Select(item => item.Clone()));
            next = page.GetProperty("nextCursor").GetString();
        }
        Check(next is null && ownedOnlyItems.Count == 99
            && !ownedOnlyItems.Any(item => item.GetProperty("locator").GetString() == newItem.Id),
            "owned-only continuation keeps post-watermark immutable bindings out even when the source sort key would place them first");
        Check((await viewer.GetAsync("/app/v1/archive/collection?limit=10&cursor="
                + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!))).StatusCode == HttpStatusCode.BadRequest,
            "continuation cursor is bound to the account that received it");
        Check((await customer.GetAsync("/app/v1/archive/collection?limit=9&cursor="
                + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!))).StatusCode == HttpStatusCode.BadRequest,
            "continuation cursor is bound to the original page size");
        Check((await customer.GetAsync("/app/v1/archive/collection?access=linked&cursor="
                + Uri.EscapeDataString(first.GetProperty("nextCursor").GetString()!))).StatusCode == HttpStatusCode.BadRequest,
            "continuation cursor is bound to the access filter");
        Check((await customer.GetAsync("/app/v1/archive/collection?cursor=invalid")).StatusCode == HttpStatusCode.BadRequest
            && (await customer.GetAsync("/app/v1/archive/collection?limit=51")).StatusCode == HttpStatusCode.BadRequest
            && (await customer.GetAsync("/app/v1/archive/collection?access=other")).StatusCode == HttpStatusCode.BadRequest
            && (await customer.GetAsync("/app/v1/archive/collection?limit=1&limit=2")).StatusCode == HttpStatusCode.BadRequest,
            "malformed cursors, repeated query keys and out-of-contract filters are rejected");

        var jobsBeforeRead = ReadJobCount(databasePath);
        fixture.App.Configuration["Keepsake:Enabled"] = "false";
        using (var disabledGeneration = await customer.GetAsync("/app/v1/archive/collection?access=owned&limit=1"))
        {
            Check(disabledGeneration.IsSuccessStatusCode
                && (await JsonAsync(disabledGeneration)).GetProperty("items").GetArrayLength() == 1
                && ReadJobCount(databasePath) == jobsBeforeRead,
                "collection remains readable and read-only when keepsake generation is disabled");
        }
        fixture.App.Configuration["Keepsake:Enabled"] = "true";

        var ownerCollection = await JsonAsync(await customer.GetAsync("/app/v1/archive/collection?limit=50&access=owned&q=Harry"));
        var namedOwner = ownerCollection.GetProperty("items").EnumerateArray().Single(item =>
            item.GetProperty("locator").GetString() == locators[0]);
        Check(namedOwner.GetProperty("characterName").GetString() == "Harry Potter"
            && namedOwner.GetProperty("eveningAt").ValueKind == JsonValueKind.Number,
            "owner character preview and evening time derive from the exact captured customer session");

        var linkedCollection = await JsonAsync(await viewer.GetAsync("/app/v1/archive/collection?access=linked"));
        var linked = linkedCollection.GetProperty("items").EnumerateArray().Single(item =>
            item.GetProperty("locator").GetString() == locators[0]);
        Check(linkedCollection.GetProperty("items").GetArrayLength() == 2,
            $"explicit links are paged independently from immutable ownership rows (count={linkedCollection.GetProperty("items").GetArrayLength()}, ids={string.Join(',', linkedCollection.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("locator").GetString()))})");
        Check(linked.GetProperty("locator").GetString() == locators[0]
            && linked.GetProperty("accessKind").GetString() == "linkedViewer"
            && !linked.GetProperty("canManage").GetBoolean() && !linked.GetProperty("canReadPrivate").GetBoolean()
            && linked.GetProperty("canUnlink").GetBoolean()
            && linked.GetProperty("characterName").ValueKind == JsonValueKind.Null
            && linked.GetProperty("eveningAt").ValueKind == JsonValueKind.Null,
            "linked viewers receive only safe preview metadata and never the owner's captured character details");
        var linkedLatestOwner = await customer.GetAsync("/app/v1/archive/associations/" + locators[0]);
        Check(linkedLatestOwner.IsSuccessStatusCode, "existing direct owner detail route remains available");

        var linkedFirstPage = await JsonAsync(await viewer.GetAsync("/app/v1/archive/collection?access=linked&limit=1"));
        var linkedCursor = linkedFirstPage.GetProperty("nextCursor").GetString()!;
        Check(linkedFirstPage.GetProperty("items")[0].GetProperty("locator").GetString() == locators[0],
            "linked collection first page uses stable linked-time ordering");
        await SeedAsync(fixture.App, async db =>
        {
            var state = await db.ArchiveCollectionStates.SingleAsync(value => value.Id == 1);
            state.Revision++;
            db.ArchiveAssociations.Add(new AccountArchiveAssociation
            {
                UserId = viewerId, Locator = locators[1], Epoch = epoch, LinkedAt = 2_000_000,
                CollectionRevision = state.Revision
            });
            await db.SaveChangesAsync();
        });
        Check((await viewer.DeleteAsync("/app/v1/archive/associations/" + locators[0])).StatusCode == HttpStatusCode.NoContent,
            "linked viewer can remove a link after reading page one");
        var linkedContinuation = await JsonAsync(await viewer.GetAsync("/app/v1/archive/collection?access=linked&limit=1&cursor="
            + Uri.EscapeDataString(linkedCursor)));
        Check(linkedContinuation.GetProperty("items").GetArrayLength() == 1
            && linkedContinuation.GetProperty("items")[0].GetProperty("locator").GetString() == locators[2],
            "unlink between pages disappears and a newly claimed explicit link above the watermark stays out");

        var profiles = fixture.App.Services.GetRequiredService<CharacterProfileStore>();
        await profiles.DeleteAsync(customerId, profile.GetProperty("revision").GetInt64(), profile.GetProperty("profileId").GetString()!);
        var redacted = await JsonAsync(await customer.GetAsync("/app/v1/archive/collection?access=owned&q=Harry"));
        Check(redacted.GetProperty("items").GetArrayLength() == 0,
            "a durable profile tombstone suppresses captured character-name matching before journal reconciliation");

        Check((await customer.DeleteAsync("/app/v1/archive/associations/" + locators[0])).StatusCode == HttpStatusCode.Conflict,
            "the established unlink endpoint continues to reject original-owner unlink attempts");
        Check((await viewer.DeleteAsync("/app/v1/archive/associations/" + locators[2])).StatusCode == HttpStatusCode.NoContent,
            "linked viewer can still unlink through the established route");
        Check((await viewer.DeleteAsync("/app/v1/archive/associations/" + locators[1])).StatusCode == HttpStatusCode.NoContent,
            "a post-watermark link can be unlinked after its privacy exclusion is verified");
        var unlinked = await JsonAsync(await viewer.GetAsync("/app/v1/archive/collection?access=linked"));
        Check(unlinked.GetProperty("items").GetArrayLength() == 0,
            "a link removed between collection reads disappears without resurrecting older associations");

        // Exercise the 200-candidate merge boundary with same-time keys. If
        // duplicate explicit rows are allowed into the bounded scan, the
        // matching owner can fall beyond its first window and its duplicate
        // cursor key can then skip it permanently.
        var boundaryItems = Enumerable.Range(1, 103).Select(index => store.Issue(
            new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"), "collection-boundary-device", 1,
                "collection-wand", $"Boundary target {index:D3}", "fixture catalog", "synthetic evidence"),
            new KeepsakeCustomerOwner(administratorId, "collection-boundary-session"))).ToArray();
        using (var connection = new SqliteConnection("Data Source=" + databasePath))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE dv10_customer_owners SET linked_at=1234567 WHERE owner_id=$owner";
            command.Parameters.AddWithValue("$owner", administratorId);
            command.ExecuteNonQuery();
        }
        var boundaryLocators = boundaryItems.Select(item => item.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var boundaryTarget = boundaryLocators[100];
        var boundaryTargetTitle = store.Read(boundaryTarget,
            KeepsakeActor.ForAccount(administratorId, "collection-boundary-session", () => true)).Snapshot.OfficialName;
        var boundaryLinks = boundaryLocators.Skip(1).Take(100).Select(locator =>
            (Locator: locator, Epoch: store.CustomerAccountLink(administratorId, locator)!.Epoch)).ToArray();
        await SeedAsync(fixture.App, async db =>
        {
            db.ArchiveAssociations.RemoveRange(db.ArchiveAssociations.Where(link => link.UserId == administratorId));
            var state = await db.ArchiveCollectionStates.SingleAsync(value => value.Id == 1);
            foreach (var link in boundaryLinks)
            {
                db.ArchiveAssociations.Add(new AccountArchiveAssociation
                {
                    UserId = administratorId, Locator = link.Locator, Epoch = link.Epoch,
                    LinkedAt = 1_234_567, CollectionRevision = ++state.Revision
                });
            }
            await db.SaveChangesAsync();
        });
        using (var boundaryFirstResponse = await administrator.GetAsync(
            "/app/v1/archive/collection?access=all&limit=1&q=" + Uri.EscapeDataString(boundaryTargetTitle)))
        {
            var boundaryFirst = await JsonAsync(boundaryFirstResponse);
            var boundaryMatches = new List<string>();
            boundaryMatches.AddRange(boundaryFirst.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("locator").GetString()!));
            var boundaryCursor = boundaryFirst.GetProperty("nextCursor").GetString();
            for (var pages = 0; boundaryCursor is not null && pages < 5; pages++)
            {
                var boundaryPage = await JsonAsync(await administrator.GetAsync("/app/v1/archive/collection?access=all&limit=1&q="
                    + Uri.EscapeDataString(boundaryTargetTitle) + "&cursor=" + Uri.EscapeDataString(boundaryCursor)));
                boundaryMatches.AddRange(boundaryPage.GetProperty("items").EnumerateArray()
                    .Select(item => item.GetProperty("locator").GetString()!));
                boundaryCursor = boundaryPage.GetProperty("nextCursor").GetString();
            }
            Check(boundaryCursor is null && boundaryMatches.Count(locator => locator == boundaryTarget) == 1,
                "same-time explicit duplicates are coalesced before the bounded merge scan, preserving the owner at its pagination boundary");
        }
        Console.WriteLine("PASS account collection live HTTPS pagination, source watermarks, private owner preview, linked redaction and legacy unlink parity");
    }

    private static async Task RunCompactProjectionCoverageAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv10-account-collection-projection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "projection.sqlite"),
            ["Keepsake:DataRoot"] = Path.Combine(root, "content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "false",
            ["Keepsake:PortraitEnabled"] = "true"
        }).Build();
        try
        {
            var store = new KeepsakeStore(config, TimeProvider.System);
            var actor = KeepsakeActor.ForAccount("projection-owner", "projection-session", () => true);
            var issued = store.Issue(new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"), "projection-device", 1,
                "fixture-wand", "Projection preview", "synthetic facts", "no physical evidence"),
                new KeepsakeCustomerOwner(actor.AccountId!, "captured-projection-session"));
            var metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            Check(metadata.Readiness.State == "pending" && metadata.Readiness.Reason == "paused_preparing",
                "compact projection preserves paused pre-generation readiness without adding work");

            config["Keepsake:GenerationEnabled"] = "true";
            _ = store.EnqueueBase(issued.Id, actor, new KeepsakeBrief(), "projection-first-base");
            var oldJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected first projection job.");
            var oldRequest = store.Dispatch(oldJob) ?? throw new InvalidOperationException("Expected first projection dispatch.");
            var oldStory = Story("Private history title");
            var provider = new FakeKeepsakeProvider();
            var oldOutput = (await provider.GenerateAsync(oldRequest, default)) with { Story = oldStory };
            _ = store.EnqueueBase(issued.Id, actor, new KeepsakeBrief(), "projection-current-base");
            Check(!store.Complete(oldJob, oldOutput), "a superseded completed result is retained only in private history");
            metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            Check(metadata.StoryTitle is null && metadata.Readiness.State == "processing",
                "private-history story and artwork are excluded from the compact published projection");

            var currentJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected current projection job.");
            var currentRequest = store.Dispatch(currentJob) ?? throw new InvalidOperationException("Expected current projection dispatch.");
            var currentStory = Story("Visible collection title");
            var stageResponse = StoryStageResponse(currentStory);
            Check(currentRequest.RecordStage!("story", "dispatch", "{}")
                && currentRequest.RecordStage("story", "completed", stageResponse),
                "synthetic provider stage publishes a story before base artwork");
            metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            var partialView = store.ReadForAccount(issued.Id, store.CustomerAccountLink(actor.AccountId!, issued.Id)!.Epoch);
            Check(metadata.StoryTitle == "Visible collection title" && metadata.Readiness.State == "partial"
                && metadata.Readiness.HasStory && !metadata.Readiness.HasArtwork
                && metadata.Readiness == partialView.Readiness,
                "SQL projection matches authoritative partial story readiness while excluding the older private edition");

            var currentOutput = (await provider.GenerateAsync(currentRequest, default)) with { Story = currentStory };
            Check(store.Complete(currentJob, currentOutput), "current synthetic base output publishes successfully");
            metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            var ownerLink = store.CustomerAccountLink(actor.AccountId!, issued.Id)!;
            var readyView = store.ReadForAccount(issued.Id, ownerLink.Epoch);
            Check(metadata.StoryTitle == "Visible collection title" && metadata.Readiness.State == "ready"
                && metadata.HasPortrait == (readyView.Portraits.Count > 0) && metadata.Readiness == readyView.Readiness,
                "ready compact projection agrees with the current owner view and ignores private-history editions");
            Check(metadata.GetType().GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal)
                    .SetEquals(["OfficialName", "StoryTitle", "HasPortrait", "Readiness"]),
                "compact projection has no full story, transcript, asset, or provider payload property");

            var consent = store.SetPortraitConsent(issued.Id, actor, true, "dv10-portrait-v1", "projection-consent");
            var baseEdition = readyView.Editions.Single(edition => edition.PublicationState != "private_history");
            var baseAsset = store.ReadAssetForAccount(issued.Id, ownerLink.Epoch, baseEdition.ArtworkAssetId).Bytes;
            using var photo = new MemoryStream(baseAsset);
            var upload = await store.AcceptPhotoAsync(issued.Id, actor, photo, "image/png", "projection-photo", consent);
            _ = store.EnqueuePortrait(issued.Id, actor, upload.Id, baseEdition.Id, "portrait_only", true, "projection-portrait");
            var portraitJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected portrait projection job.");
            var portraitRequest = store.Dispatch(portraitJob) ?? throw new InvalidOperationException("Expected portrait dispatch.");
            Check(store.Complete(portraitJob, await provider.GenerateAsync(portraitRequest, default)),
                "synthetic portrait output is accepted");
            metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            readyView = store.ReadForAccount(issued.Id, ownerLink.Epoch);
            Check(metadata.HasPortrait && readyView.Portraits.Count == 1,
                "compact portrait marker tracks a current supported published portrait");
            _ = store.SetPortraitConsent(issued.Id, actor, false, "dv10-portrait-v1", "projection-withdraw-consent");
            metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            readyView = store.ReadForAccount(issued.Id, ownerLink.Epoch);
            Check(metadata.HasPortrait && readyView.Portraits.Count == 1,
                "withdrawing consent preserves an already accepted published portrait");
            _ = store.Delete(issued.Id, actor, KeepsakeDeleteScope.PhotosAndPortraits,
                "projection-delete-photos", readyView.Revision);
            metadata = store.ReadCustomerCollectionMetadata(issued.Id, actor);
            readyView = store.ReadForAccount(issued.Id, ownerLink.Epoch);
            Check(!metadata.HasPortrait && readyView.Portraits.Count == 0,
                "photo-and-portrait deletion removes the compact portrait marker");

            var privatePortraitIssue = store.Issue(new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"), "projection-device", 1,
                "fixture-wand", "Private portrait preview", "synthetic facts", "no physical evidence"),
                new KeepsakeCustomerOwner(actor.AccountId!, "private-portrait-session"));
            var privatePortraitBaseJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected private portrait base job.");
            var privatePortraitBaseRequest = store.Dispatch(privatePortraitBaseJob)
                ?? throw new InvalidOperationException("Expected private portrait base dispatch.");
            Check(store.Complete(privatePortraitBaseJob, await provider.GenerateAsync(privatePortraitBaseRequest, default)),
                "private portrait fixture publishes base artwork");
            var privatePortraitLink = store.CustomerAccountLink(actor.AccountId!, privatePortraitIssue.Id)!;
            var privatePortraitView = store.ReadForAccount(privatePortraitIssue.Id, privatePortraitLink.Epoch);
            var privatePortraitEdition = privatePortraitView.Editions.Single(edition => edition.PublicationState != "private_history");
            var privatePortraitConsent = store.SetPortraitConsent(privatePortraitIssue.Id, actor, true,
                "dv10-portrait-v1", "private-portrait-consent");
            var privatePortraitBaseBytes = store.ReadAssetForAccount(privatePortraitIssue.Id, privatePortraitLink.Epoch,
                privatePortraitEdition.ArtworkAssetId).Bytes;
            using var privatePortraitPhoto = new MemoryStream(privatePortraitBaseBytes);
            var privateUpload = await store.AcceptPhotoAsync(privatePortraitIssue.Id, actor, privatePortraitPhoto,
                "image/png", "private-portrait-photo", privatePortraitConsent);
            _ = store.EnqueuePortrait(privatePortraitIssue.Id, actor, privateUpload.Id, privatePortraitEdition.Id,
                "portrait_only", true, "private-portrait-enqueue");
            var cancelledPortraitJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected private portrait job.");
            var cancelledPortraitRequest = store.Dispatch(cancelledPortraitJob)
                ?? throw new InvalidOperationException("Expected private portrait dispatch.");
            store.Cancel(privatePortraitIssue.Id, actor, cancelledPortraitJob.Id, "private-portrait-cancel");
            Check(!store.Complete(cancelledPortraitJob,
                    await provider.GenerateAsync(cancelledPortraitRequest, default)),
                "a canceled portrait completion remains private history");
            var privatePortraitOwnerMetadata = store.ReadCustomerCollectionMetadata(privatePortraitIssue.Id, actor);
            var privatePortraitLinkedMetadata = store.ReadLinkedCollectionMetadata(privatePortraitIssue.Id, privatePortraitLink.Epoch);
            var privatePortraitOwnerView = store.Read(privatePortraitIssue.Id, actor);
            var privatePortraitLinkedView = store.ReadForAccount(privatePortraitIssue.Id, privatePortraitLink.Epoch);
            Check(privatePortraitOwnerView.Assets.Any(asset => asset.Kind == "portrait_history_image")
                && !privatePortraitOwnerMetadata.HasPortrait && !privatePortraitLinkedMetadata.HasPortrait
                && privatePortraitLinkedView.Portraits.Count == 0,
                "cancelled portrait history is excluded from owner and linked markers and linked detail projection");

            var partialIssue = store.Issue(new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"), "projection-device", 1,
                "fixture-wand", "Partial failure preview", "synthetic facts", "no physical evidence"),
                new KeepsakeCustomerOwner(actor.AccountId!, "partial-projection-session"));
            var partialJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected partial projection job.");
            var partialRequest = store.Dispatch(partialJob) ?? throw new InvalidOperationException("Expected partial dispatch.");
            var failureStory = Story("Partial failed title");
            Check(partialRequest.RecordStage!("story", "dispatch", "{}")
                && partialRequest.RecordStage("story", "completed", StoryStageResponse(failureStory)),
                "partial failure fixture records a story before cancellation");
            store.Cancel(partialIssue.Id, actor, partialJob.Id, "projection-cancel-partial");
            metadata = store.ReadCustomerCollectionMetadata(partialIssue.Id, actor);
            var failureView = store.ReadForAccount(partialIssue.Id,
                store.CustomerAccountLink(actor.AccountId!, partialIssue.Id)!.Epoch);
            Check(metadata.Readiness.State == "partial" && metadata.Readiness.Reason == "generation_failed"
                && !metadata.Readiness.ShouldPoll && metadata.Readiness == failureView.Readiness,
                "terminal partial generation remains visible without a false polling signal");
            Console.WriteLine("PASS account collection compact SQL projection: paused, staged, ready, private history, portrait deletion and failed partial");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static KeepsakeStory Story(string title) => new(title,
        string.Join(' ', Enumerable.Repeat("A small brave wizard discovers a bright path through the old forest.", 24)),
        new("slender", "oak", "silver", "wood", "starlight"));

    private static string StoryStageResponse(KeepsakeStory story) => JsonSerializer.Serialize(new
    {
        status = "completed",
        output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(story) } } } }
    });

    private static async Task<long> ReadAssociationRevisionAsync(WebApplication app, string accountId, string locator)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        return await db.ArchiveAssociations.Where(link => link.UserId == accountId && link.Locator == locator)
            .Select(link => link.CollectionRevision).SingleAsync();
    }

    private static long ReadJobCount(string databasePath)
    {
        using var connection = new SqliteConnection("Data Source=" + databasePath);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM dv10_jobs";
        return (long)command.ExecuteScalar()!;
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    public static async Task RunKeepsakeOwnerHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.App.Services.GetRequiredService<IConfiguration>()["Keepsake:PortraitEnabled"] = "true";
        using var owner = fixture.Client();
        using var viewer = fixture.Client();
        using var anonymous = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        using var viewerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Keepsake owner");
        var ownerSession = await CsrfAsync(owner);
        var ownerId = ownerSession.GetProperty("account").GetProperty("id").GetString()!;
        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Viewer", recoveryUserId = (string?)null }));
        await EnrollAsync(viewer, viewerKey, invitation.GetProperty("token").GetString()!, "Linked viewer");
        var viewerId = (await CsrfAsync(viewer)).GetProperty("account").GetProperty("id").GetString()!;
        await CsrfAsync(anonymous);

        var store = fixture.App.Services.GetRequiredService<KeepsakeStore>();
        var provider = new FakeKeepsakeProvider();
        var issued = store.Issue(new(Guid.NewGuid().ToString("D"), "granted-device", 1,
            "test-wand", "Original owner test wand", "Synthetic catalog facts", "No hardware involved."),
            new(ownerId, "original-customer-session"));
        var guest = store.Bootstrap();
        store.Claim(store.IssueClaim(issued.Id, "granted-device").Token, guest.Token, Guid.NewGuid().ToString("D"));
        await CompleteOwnerFixtureJobAsync();
        var edition = store.Read(issued.Id, guest.Token).Editions.Single();
        var image = store.ReadAsset(issued.Id, guest.Token, edition.ArtworkAssetId).Bytes;
        var ownerPath = "/app/v1/archive/owned/" + issued.Id;
        var associationPath = "/app/v1/archive/associations/" + issued.Id;

        var detail = await JsonAsync(await owner.GetAsync(associationPath));
        Check(detail.GetProperty("accessKind").GetString() == "originalOwner"
            && detail.GetProperty("canManage").GetBoolean()
            && !detail.GetProperty("readOnly").GetBoolean(), "original customer owner receives management capability");
        Check((await owner.GetAsync(ownerPath)).IsSuccessStatusCode, "owner endpoint accepts original account without guest cookie");
        Check((await owner.DeleteAsync(associationPath)).StatusCode == HttpStatusCode.Conflict
            && (await owner.GetAsync(ownerPath)).IsSuccessStatusCode,
            "association unlink cannot revoke the immutable original account owner's management authority");
        Check((await anonymous.GetAsync(ownerPath)).StatusCode == HttpStatusCode.Unauthorized,
            "guest locator does not grant authenticated account authority");
        Check((await viewer.GetAsync(ownerPath + "?account=1&owner=" + ownerId)).StatusCode == HttpStatusCode.NotFound,
            "query identifiers cannot manufacture original ownership");

        var identity = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/wizard-identity",
            new { schemaVersion = 1, editionId = edition.Id }));
        Check(identity.GetProperty("keepsakeId").GetString() == issued.Id,
            "account owner can explicitly prepare the existing legacy wizard identity");
        var ticketKey = Guid.NewGuid().ToString("D");
        var associationTicket = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/account-associations",
            new { schemaVersion = 1, targetAccountId = viewerId }, ticketKey));
        var repeatedTicket = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/account-associations",
            new { schemaVersion = 1, targetAccountId = viewerId }, ticketKey));
        var ticket = associationTicket.GetProperty("ticket").GetString();
        Check(ticket == repeatedTicket.GetProperty("ticket").GetString(), "account owner association ticket retries preserve the exact target-bound capability");
        Check((await viewer.PostAsJsonAsync("/app/v1/archive/associations", new { ticket })).IsSuccessStatusCode,
            "viewer explicitly links content using an original-account-owner ticket");
        var linked = await JsonAsync(await viewer.GetAsync(associationPath));
        Check(linked.GetProperty("accessKind").GetString() == "linkedViewer"
            && !linked.GetProperty("canManage").GetBoolean()
            && linked.GetProperty("canUnlink").GetBoolean(), "linked account remains read-only");
        foreach (var suffix in new[] { "", "/share-links", "/recovery-links", "/revoke-access", "/portrait-consents", "/portraits", "/exports", "/wizard-identity", "/account-associations" })
        {
            using var denied = await OwnerMutationAsync(viewer, suffix.Length == 0 ? HttpMethod.Delete : HttpMethod.Post,
                ownerPath + suffix, new { schemaVersion = 1, confirm = "delete", expectedRevision = 0 });
            Check(denied.StatusCode == HttpStatusCode.NotFound, "linked viewer cannot invoke owner operation " + suffix);
        }
        Check((await viewer.GetAsync(ownerPath + "/assets/" + edition.ArtworkAssetId)).StatusCode == HttpStatusCode.NotFound,
            "linked account cannot use owner asset endpoint");
        Check((await viewer.DeleteAsync(associationPath)).StatusCode == HttpStatusCode.NoContent,
            "unlink removes only viewer association");
        Check((await viewer.GetAsync(associationPath)).StatusCode == HttpStatusCode.NotFound
            && (await owner.GetAsync(ownerPath)).IsSuccessStatusCode
            && store.Authorize(issued.Id, guest.Token, KeepsakeScope.Write), "unlink preserves original and legacy owner access");

        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        using (var response = await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/share-links",
            new { schemaVersion = 1, editionId = edition.Id }))
        {
            Check(response.StatusCode == HttpStatusCode.BadRequest, "account owner mutation requires CSRF");
        }
        await CsrfAsync(owner);
        using (var response = await owner.PostAsJsonAsync(ownerPath + "/share-links", new { schemaVersion = 1, editionId = edition.Id }))
        {
            Check(response.StatusCode == HttpStatusCode.BadRequest, "account owner mutation requires idempotency key");
        }
        var shareKey = Guid.NewGuid().ToString("D");
        var share = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/share-links",
            new { schemaVersion = 1, editionId = edition.Id }, shareKey));
        var replay = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/share-links",
            new { schemaVersion = 1, editionId = edition.Id }, shareKey));
        Check(share.GetProperty("url").GetString() == replay.GetProperty("url").GetString(),
            "account share retries return one durable link");
        Check((await OwnerMutationAsync(owner, HttpMethod.Delete, ownerPath + "/share-links", new { schemaVersion = 1 })).StatusCode == HttpStatusCode.NoContent,
            "original owner revokes shares independently of content deletion");
        Check(store.Authorize(issued.Id, guest.Token, KeepsakeScope.Write), "share revocation preserves legacy original owner");
        Check((await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/recovery-links", new { schemaVersion = 1 })).IsSuccessStatusCode,
            "account owner can issue recovery access");
        Check((await OwnerMutationAsync(owner, HttpMethod.Delete, ownerPath + "/recovery-links", new { schemaVersion = 1 })).StatusCode == HttpStatusCode.NoContent,
            "account owner can revoke recovery access");
        var export = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/exports",
            new { schemaVersion = 1, editionId = edition.Id, format = "story", portraitId = (string?)null }));
        var exportId = export.GetProperty("asset").GetProperty("id").GetString();
        Check((await owner.GetAsync(ownerPath + "/assets/" + exportId)).IsSuccessStatusCode, "account owner downloads private export");

        var consent = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/portrait-consents",
            new { schemaVersion = 1, accepted = true, policyVersion = "dv10-portrait-v1" }));
        var version = consent.GetProperty("consentVersion").GetInt32();
        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, ownerPath + "/portrait-uploads");
        uploadRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        uploadRequest.Headers.Add("X-DV10-Consent", version.ToString());
        uploadRequest.Content = new ByteArrayContent(image);
        uploadRequest.Content.Headers.ContentType = new("image/png");
        var upload = await JsonAsync(await owner.SendAsync(uploadRequest));
        Check((await OwnerMutationAsync(owner, HttpMethod.Post, ownerPath + "/portraits", new
        {
            schemaVersion = 1, uploadId = upload.GetProperty("upload").GetProperty("id").GetString(),
            editionId = edition.Id, variant = "portrait_only", consentVersion = version
        })).StatusCode == HttpStatusCode.Accepted, "account owner uses explicit consent for portrait generation");
        await CompleteOwnerFixtureJobAsync();
        Check(store.Read(issued.Id, guest.Token).Portraits.Count == 1, "account portrait uses existing owner generation pipeline");

        var beforePhotoDelete = store.Read(issued.Id, guest.Token);
        Check((await OwnerMutationAsync(owner, HttpMethod.Delete, ownerPath + "/portrait",
            new { schemaVersion = 1, confirm = "delete", expectedRevision = beforePhotoDelete.Revision })).StatusCode == HttpStatusCode.Accepted,
            "account owner can delete only photographs and portraits");
        var afterPhotoDelete = store.Read(issued.Id, guest.Token);
        Check(afterPhotoDelete.Portraits.Count == 0 && afterPhotoDelete.Uploads.Count == 0
            && afterPhotoDelete.Editions.Count == 1, "photo deletion preserves the original story and ownership");

        var beforeDelete = store.Read(issued.Id, guest.Token);
        using (var response = await OwnerMutationAsync(owner, HttpMethod.Delete, ownerPath,
            new { schemaVersion = 1, confirm = "delete", expectedRevision = beforeDelete.Revision - 1 }))
        {
            Check(response.StatusCode == HttpStatusCode.Conflict, "account deletion requires current revision");
        }
        var deleteKey = Guid.NewGuid().ToString("D");
        var deleted = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Delete, ownerPath,
            new { schemaVersion = 1, confirm = "delete", expectedRevision = beforeDelete.Revision }, deleteKey));
        var receiptId = deleted.GetProperty("receipt").GetProperty("id").GetString();
        var repeatedDeletion = await JsonAsync(await OwnerMutationAsync(owner, HttpMethod.Delete, ownerPath,
            new { schemaVersion = 1, confirm = "delete", expectedRevision = beforeDelete.Revision }, deleteKey));
        Check(repeatedDeletion.GetProperty("receipt").GetProperty("id").GetString() == receiptId,
            "a lost deletion response can be safely retried after content access was revoked");
        var receiptPath = "/app/v1/archive/owned/deletions/" + receiptId;
        Check((await owner.GetAsync(receiptPath)).IsSuccessStatusCode, "owner deletion receipt survives a fresh request actor");
        Check((await viewer.GetAsync(receiptPath)).StatusCode == HttpStatusCode.NotFound,
            "another account cannot read owner deletion receipt");
        Check((await owner.GetAsync(ownerPath)).StatusCode == HttpStatusCode.NotFound
            && !store.Authorize(issued.Id, guest.Token, KeepsakeScope.Read), "owned deletion revokes account and legacy content access");

        var viewerOwned = store.Issue(new(Guid.NewGuid().ToString("D"), "granted-device", 1,
            "test-wand", "Another customer's wand", "Synthetic facts", "No hardware involved."), new(viewerId, "viewer-original-session"));
        var viewerOwnedPath = "/app/v1/archive/owned/" + viewerOwned.Id;
        Check((await viewer.GetAsync(viewerOwnedPath)).IsSuccessStatusCode
            && (await owner.GetAsync(viewerOwnedPath)).StatusCode == HttpStatusCode.NotFound,
            "application roles do not override the exact original customer ownership record");

        var fenced = store.Issue(new(Guid.NewGuid().ToString("D"), "granted-device", 1,
            "test-wand", "Fenced test wand", "Synthetic facts", "No hardware involved."), new(ownerId, "original-session"));
        store.FenceAccountDeletion(ownerId);
        Check((await owner.GetAsync("/app/v1/archive/owned/" + fenced.Id)).StatusCode == HttpStatusCode.NotFound,
            "account deletion fence blocks ownership despite live cookie");
        await SeedAsync(fixture.App, db => db.Sessions.Where(session => session.UserId == viewerId).ExecuteDeleteAsync());
        Check((await viewer.GetAsync(ownerPath)).StatusCode == HttpStatusCode.Unauthorized,
            "stale account session is rejected before owner authorization");
        Console.WriteLine("Keepsake account owner HTTPS checks passed: original/linked/anonymous boundaries, CSRF, idempotency, consent, exports, unlink, deletion and stale access.");

        async Task CompleteOwnerFixtureJobAsync()
        {
            var job = store.LeaseNext() ?? throw new InvalidOperationException("Missing keepsake fixture job.");
            Check(store.Complete(job, await provider.GenerateAsync(store.Dispatch(job)!, default)), "synthetic owner fixture job completed");
        }
    }

    private static async Task<HttpResponseMessage> OwnerMutationAsync(HttpClient client, HttpMethod method,
        string path, object body, string? key = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("D"));
        return await client.SendAsync(request);
    }
}

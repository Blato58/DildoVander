using System.Net;
using System.Net.Http.Json;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerCompletionHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true,
            configureEndpoints: app => app.MapCustomerCompletionUpdates());
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Completion owner");
        var ownerId = (await CsrfAsync(owner)).GetProperty("account").GetProperty("id").GetString()!;
        using var viewer = fixture.Client();
        using var viewerKey = new VirtualPasskey();
        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Viewer", recoveryUserId = (string?)null }));
        await EnrollAsync(viewer, viewerKey, invitation.GetProperty("token").GetString()!, "Another account");
        await CsrfAsync(viewer);
        using var anonymous = fixture.Client();
        var store = fixture.App.Services.GetRequiredService<KeepsakeStore>();
        var result = store.Issue(new(Guid.NewGuid().ToString("D"), "completion-http-device", 1,
            "private-catalog-id", "PRIVATE_CHARACTER_MARKER", "PRIVATE_CATALOG_MARKER", "PRIVATE_TRANSCRIPT_MARKER"),
            new(ownerId, "completion-http-customer-session"));
        const string path = "/app/v1/customer/completion-updates";
        Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized,
            "completion reads require a live authenticated account");
        using var feedResponse = await owner.GetAsync(path);
        var wire = await feedResponse.Content.ReadAsStringAsync();
        Check(feedResponse.StatusCode == HttpStatusCode.OK && feedResponse.Headers.CacheControl?.NoStore == true,
            "authorized completion feed is available and never cacheable");
        Check(!wire.Contains("PRIVATE_", StringComparison.Ordinal) && !wire.Contains("private-catalog-id", StringComparison.Ordinal),
            "completion payload contains no character, catalog or transcript content");
        var feed = await JsonAsync(feedResponse);
        var item = feed.GetProperty("items").EnumerateArray().Single(entry => entry.GetProperty("resultLocator").GetString() == result.Id);
        var id = item.GetProperty("id").GetString()!;
        var readPath = path + "/" + id + "/read";
        var foreign = await JsonAsync(await viewer.GetAsync(path));
        Check(foreign.GetProperty("items").GetArrayLength() == 0,
            "another account cannot observe the original owner's completion metadata");
        Check((await viewer.PutAsJsonAsync(readPath, new { schemaVersion = 1 })).StatusCode == HttpStatusCode.NotFound,
            "another account cannot acknowledge a known event identifier");
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.PutAsJsonAsync(readPath, new { schemaVersion = 1 })).StatusCode == HttpStatusCode.BadRequest,
            "read acknowledgements require the existing CSRF proof");
        await CsrfAsync(owner);
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Check((await owner.PutAsJsonAsync(readPath, new { schemaVersion = 1 })).StatusCode == HttpStatusCode.Forbidden,
            "read acknowledgements reject an unrelated origin");
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://localhost");
        Check((await owner.PutAsJsonAsync(readPath, new { schemaVersion = 2 })).StatusCode == HttpStatusCode.BadRequest
            && (await owner.PutAsJsonAsync(readPath, new { schemaVersion = 1, ownerId })).StatusCode == HttpStatusCode.BadRequest,
            "acknowledgements accept only the versioned closed request shape");
        Check((await owner.PutAsJsonAsync(readPath, new { schemaVersion = 1 })).StatusCode == HttpStatusCode.NoContent
            && (await owner.PutAsJsonAsync(readPath, new { schemaVersion = 1 })).StatusCode == HttpStatusCode.NoContent,
            "repeated acknowledgement is naturally idempotent");
        var reread = await JsonAsync(await owner.GetAsync(path));
        var readItem = reread.GetProperty("items").EnumerateArray().Single(entry => entry.GetProperty("id").GetString() == id);
        Check(readItem.GetProperty("readAt").ValueKind == System.Text.Json.JsonValueKind.String,
            "read state is durably returned by the server after acknowledgement");
        Check((await owner.GetAsync(path + "?limit=0")).StatusCode == HttpStatusCode.BadRequest
            && (await owner.GetAsync(path + "?cursor=forged-cursor")).StatusCode == HttpStatusCode.BadRequest,
            "the endpoint rejects unbounded or forged pagination");
        var actor = KeepsakeActor.ForAccount(ownerId, "setup-only-session", static () => true);
        store.Delete(result.Id, actor, KeepsakeDeleteScope.All, Guid.NewGuid().ToString("D"));
        var deleted = await JsonAsync(await owner.GetAsync(path));
        Check(deleted.GetProperty("items").GetArrayLength() == 0
            && (await owner.PutAsJsonAsync(readPath, new { schemaVersion = 1 })).StatusCode == HttpStatusCode.NotFound,
            "deleted results disappear and old event links cannot be acknowledged");
        await SeedAsync(fixture.App, async db =>
        {
            var user = await db.Users.FindAsync(ownerId);
            user!.SecurityStamp = Guid.NewGuid().ToString("D");
            await db.SaveChangesAsync();
        });
        Check((await owner.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized,
            "a revoked security stamp cannot retain access to the completion feed");
        Console.WriteLine("PASS completion HTTPS account isolation, safe payload, read acknowledgement, CSRF/origin, deletion and revocation");
    }
}

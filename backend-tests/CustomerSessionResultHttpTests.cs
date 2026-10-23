using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerSessionResultHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        using var administrator = fixture.Client();
        using var customer = fixture.Client();
        using var viewer = fixture.Client();
        using var administratorKey = new VirtualPasskey();
        using var customerKey = new VirtualPasskey();
        using var viewerKey = new VirtualPasskey();

        await EnrollAsync(administrator, administratorKey, Fixture.Bootstrap, "Readiness administrator");
        await CsrfAsync(administrator);
        var customerProfile = await CustomerAsync(customer, customerKey);
        var customerIdentity = await CsrfAsync(customer);
        var customerId = customerIdentity.GetProperty("account").GetProperty("id").GetString()!;

        var invite = await JsonAsync(await administrator.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Viewer", recoveryUserId = (string?)null }));
        await EnrollAsync(viewer, viewerKey, invite.GetProperty("token").GetString()!, "Readiness linked viewer");
        var viewerId = (await CsrfAsync(viewer)).GetProperty("account").GetProperty("id").GetString()!;

        Check((await administrator.PutAsJsonAsync("/app/v1/customer/admin/devices/granted-device", new { enabled = true })).IsSuccessStatusCode,
            "administrator enables customer test device");
        var bootId = Guid.NewGuid().ToString("D");
        var challenge = new CustomerDeviceChallenge(1, bootId, Guid.NewGuid().ToString("D"), 0, PartyWandCatalog.Version, "1");
        var deviceToken = fixture.App.Services.GetRequiredService<DeviceAccessStore>().IssueToken("granted-device")!;
        using var device = fixture.Client();
        device.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken.AccessToken);
        Check((await device.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge)).IsSuccessStatusCode,
            "device registers synthetic readiness challenge over HTTPS");

        var operationId = Guid.NewGuid().ToString("D");
        var ticketResponse = await customer.PostAsJsonAsync("/app/v1/customer/start-tickets", new
        {
            protocolVersion = 1, operationId, deviceId = "granted-device", bootId,
            challenge = challenge.Challenge, profileId = customerProfile.GetProperty("profileId").GetString(),
            profileRevision = customerProfile.GetProperty("revision").GetInt64(), openAiConsent = true
        });
        var ticket = await JsonAsync(ticketResponse);
        var redeemed = await JsonAsync(await device.PostAsJsonAsync("/v1/customer-sessions/redeem",
            new CustomerRedemption(1, ticket.GetProperty("ticket").GetString()!, operationId, bootId, challenge.Challenge)));
        var sessionId = redeemed.GetProperty("customerSessionId").GetString()!;
        var resultPath = $"/app/v1/customer/sessions/{sessionId}/result";

        Check((await administrator.GetAsync(resultPath)).StatusCode == HttpStatusCode.NotFound,
            "a different authenticated account cannot inspect the session result");
        Check((await customer.GetAsync("/app/v1/customer/sessions/" + Guid.NewGuid().ToString("D") + "/result")).StatusCode
            == HttpStatusCode.NotFound, "unknown sessions do not create or reveal a result");

        var store = fixture.App.Services.GetRequiredService<KeepsakeStore>();
        var pending = await JsonAsync(await customer.GetAsync(resultPath));
        Check(pending.GetProperty("readiness").GetProperty("state").GetString() == "pending"
            && pending.GetProperty("readiness").GetProperty("reason").GetString() == "awaiting_result"
            && pending.GetProperty("resultLocator").ValueKind == JsonValueKind.Null
            && !pending.GetProperty("canManage").GetBoolean(),
            "live customer session without an immutable result remains pending and non-authorizing");
        var issued = store.Issue(new(Guid.NewGuid().ToString("D"), "readiness-http-device", 1,
            "readiness-wand", "Synthetic readiness result", "fixture catalog", "no physical evidence"),
            new KeepsakeCustomerOwner(customerId, sessionId));
        var ownerPath = await JsonAsync(await customer.GetAsync(resultPath));
        Check(ownerPath.GetProperty("resultLocator").GetString() == issued.Id
            && ownerPath.GetProperty("accessKind").GetString() == "originalOwner"
            && ownerPath.GetProperty("canManage").GetBoolean()
            && ownerPath.GetProperty("readiness").GetProperty("state").GetString() == "processing",
            "account-owned result route derives its locator from the immutable customer-session binding");
        var firstRevision = store.Read(issued.Id, KeepsakeActor.ForAccount(customerId, "test-account-session", () => true)).Revision;
        var repeated = await JsonAsync(await customer.GetAsync(resultPath));
        Check(repeated.GetProperty("resultLocator").GetString() == issued.Id
            && repeated.GetProperty("readiness").GetProperty("state").GetString() == "processing"
            && store.Read(issued.Id, KeepsakeActor.ForAccount(customerId, "test-account-session", () => true)).Revision == firstRevision,
            "reconnect polling returns stable readiness without changing archive revision");

        var ownerLink = store.CustomerAccountLink(customerId, issued.Id)!;
        var db = fixture.App.Services.GetRequiredService<AccountDatabase>();
        db.ArchiveAssociations.Add(new AccountArchiveAssociation
        { UserId = viewerId, Locator = issued.Id, Epoch = ownerLink.Epoch, LinkedAt = AccountEndpoints.Now() });
        await db.SaveChangesAsync();
        var linked = await JsonAsync(await viewer.GetAsync("/app/v1/archive/associations/" + issued.Id));
        Check(linked.GetProperty("accessKind").GetString() == "linkedViewer"
            && linked.GetProperty("readiness").GetProperty("state").GetString() == "processing"
            && linked.GetProperty("jobs").GetArrayLength() == 0 && linked.GetProperty("uploads").GetArrayLength() == 0
            && !linked.GetProperty("canManage").GetBoolean()
            && (await viewer.GetAsync(resultPath)).StatusCode == HttpStatusCode.NotFound,
            "linked account receives safe archive readiness but no customer-session result authority");

        var currentSessionId = await db.Sessions.AsNoTracking().Where(session => session.UserId == customerId)
            .OrderByDescending(session => session.LastSeenAt).Select(session => session.Id).FirstAsync();
        var actor = KeepsakeActor.ForAccount(customerId, currentSessionId, () => db.Sessions.AsNoTracking()
            .Any(session => session.Id == currentSessionId && session.UserId == customerId && session.ExpiresAt > AccountEndpoints.Now()));

        var job = store.LeaseNext() ?? throw new InvalidOperationException("Expected synthetic base generation job.");
        var providerRequest = store.Dispatch(job) ?? throw new InvalidOperationException("Expected synthetic provider request.");
        var story = new KeepsakeStory("Readiness story", string.Join(' ', Enumerable.Repeat("A kind wizard finds a safe path and helps a friend return home beneath the bright stars.", 24)),
            new("slender", "oak", "silver", "wood", "starlight"));
        var content = JsonSerializer.Serialize(new
        {
            status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(story) } } } }
        });
        Check(providerRequest.RecordStage!("story", "dispatch", "{}")
            && providerRequest.RecordStage("story", "completed", content), "synthetic story stage is durably recorded");
        var partial = await JsonAsync(await customer.GetAsync(resultPath));
        Check(partial.GetProperty("readiness").GetProperty("state").GetString() == "partial"
            && partial.GetProperty("readiness").GetProperty("hasStory").GetBoolean()
            && !partial.GetProperty("readiness").GetProperty("hasArtwork").GetBoolean(),
            "story without base artwork is reported as partial over HTTPS");
        Check(store.Complete(job, new(story, SyntheticPng(), "image/png", "synthetic", "fixture", "fixture prompt", content, true)),
            "synthetic provider completion is accepted for the session-bound result");
        var ready = await JsonAsync(await customer.GetAsync(resultPath));
        Check(ready.GetProperty("readiness").GetProperty("state").GetString() == "ready"
            && ready.GetProperty("readiness").GetProperty("hasArtwork").GetBoolean(),
            "published story and base artwork produce ready state");
        await device.PostAsJsonAsync($"/v1/customer-sessions/{sessionId}/device-state",
            new { bootId, state = "completed", conversationId = redeemed.GetProperty("conversationId").GetString() });
        bootId = Guid.NewGuid().ToString("D");
        challenge = challenge with { BootId = bootId, Challenge = Guid.NewGuid().ToString("D") };
        await device.PostAsJsonAsync("/v1/customer-sessions/device-challenge", challenge);
        await device.PostAsJsonAsync("/v1/customer-sessions/device-idle", new { bootId });
        var secondOperation = Guid.NewGuid().ToString("D");
        var secondTicket = await JsonAsync(await customer.PostAsJsonAsync("/app/v1/customer/start-tickets", new
        {
            protocolVersion = 1, operationId = secondOperation, deviceId = "granted-device", bootId,
            challenge = challenge.Challenge, profileId = customerProfile.GetProperty("profileId").GetString(),
            profileRevision = customerProfile.GetProperty("revision").GetInt64(), openAiConsent = true
        }));
        var secondSession = await JsonAsync(await device.PostAsJsonAsync("/v1/customer-sessions/redeem",
            new CustomerRedemption(1, secondTicket.GetProperty("ticket").GetString()!, secondOperation, bootId, challenge.Challenge)));
        var secondSessionId = secondSession.GetProperty("customerSessionId").GetString()!;
        var secondPath = $"/app/v1/customer/sessions/{secondSessionId}/result";
        var ambiguousIssue = store.Issue(new(Guid.NewGuid().ToString("D"), "readiness-http-reconcile-device", 1,
            "readiness-wand", "Reconciliation result", "fixture catalog", "no physical evidence"),
            new KeepsakeCustomerOwner(customerId, secondSessionId));
        var reconcileJob = store.LeaseNext() ?? throw new InvalidOperationException("Expected reconciliation fixture job.");
        Check(reconcileJob.KeepsakeId == ambiguousIssue.Id, "reconciliation fixture dispatches only its own bound result job");
        _ = store.Dispatch(reconcileJob) ?? throw new InvalidOperationException("Expected reconciliation provider request.");
        store.MarkAmbiguous(reconcileJob, "provider_attempt_unknown");
        var ambiguous = await JsonAsync(await customer.GetAsync(secondPath));
        Check(ambiguous.GetProperty("readiness").GetProperty("state").GetString() == "processing"
            && ambiguous.GetProperty("readiness").GetProperty("reason").GetString() == "reconciliation_required"
            && !ambiguous.GetProperty("readiness").GetProperty("canRetry").GetBoolean(),
            "ambiguous provider outcome exposes reconciliation without blind retry");
        var unrelated = store.Issue(new(Guid.NewGuid().ToString("D"), "readiness-unrelated-device", 1,
            "readiness-wand", "Unrelated result", "fixture catalog", "no physical evidence"));
        var ignoredLocator = await JsonAsync(await customer.GetAsync(resultPath + "?locator=" + unrelated.Id));
        Check(ignoredLocator.GetProperty("resultLocator").GetString() == issued.Id,
            "a client-supplied locator cannot replace the immutable session binding");

        store.Delete(issued.Id, actor, KeepsakeDeleteScope.All, Guid.NewGuid().ToString("D"), store.Read(issued.Id, actor).Revision);
        var cleared = await JsonAsync(await customer.GetAsync(resultPath));
        Check(cleared.GetProperty("readiness").GetProperty("state").GetString() == "unavailable"
            && cleared.GetProperty("resultLocator").ValueKind == JsonValueKind.Null
            && cleared.GetProperty("accessKind").ValueKind == JsonValueKind.Null
            && !cleared.GetProperty("canManage").GetBoolean(),
            "deleted result row clears locator and capabilities while preserving its session response");

        store.FenceAccountDeletion(customerId);
        Check((await customer.GetAsync(resultPath)).StatusCode == HttpStatusCode.NotFound,
            "account deletion fence blocks result readiness even while the identity session remains live");

        var accountDetails = await JsonAsync(await customer.GetAsync("/app/v1/account"));
        var sessionToRevoke = accountDetails.GetProperty("sessions").EnumerateArray()
            .Single(session => session.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        Check((await customer.DeleteAsync("/app/v1/sessions/" + sessionToRevoke)).IsSuccessStatusCode,
            "test revokes the live account session after result checks");
        var expiredResponse = await customer.GetAsync(resultPath);
        Check(expiredResponse.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.NotFound,
            "revoked live account session cannot read readiness");
        Console.WriteLine("PASS customer session results: HTTPS ownership, immutable binding, linked-viewer boundary, staged readiness, reconciliation and deletion/session fences");
    }

    private static byte[] SyntheticPng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(8, 8);
        bitmap.Erase(SkiaSharp.SKColors.CornflowerBlue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }
}

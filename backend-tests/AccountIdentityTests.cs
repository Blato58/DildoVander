using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.DigitalTwin;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.ModelReleases;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Show;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static partial class AccountIdentityTests
{
    public static async Task RunBrowserFixtureAsync()
    {
        await using var fixture = await Fixture.StartAsync(browserMode: true, deletionFeatures: true);
        Check(await AccountProvisioning.RunAsync(fixture.App, ["--account-bootstrap"]) == 0, "browser fixture bootstrap delivery");
        var deliveryRoot = Path.Combine(fixture.App.Services.GetRequiredService<AccountConfiguration>().DataRoot, "owner-delivery");
        Console.WriteLine("ACCOUNT_BROWSER_DELIVERY " + Directory.EnumerateFiles(deliveryRoot, "*.txt").Single());
        Console.WriteLine("ACCOUNT_BROWSER_FIXTURE " + fixture.App.Urls.Single().Replace("127.0.0.1", "localhost"));
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; cancellation.Cancel(); };
        _ = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync(cancellation.Token) is { } line)
                {
                    if (line == "stop") { cancellation.Cancel(); break; }
                }
            }
            catch (OperationCanceledException) { }
        });
        try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellation.Token); }
        catch (OperationCanceledException) { }
    }
    public static async Task RunAsync()
    {
        ServiceCredentialBoundaries();
        await DisabledIdentitySessionAsync();
        await AppleAppSiteAssociationAsync();
        await PersistentAccountAuthorizationAsync();
        await AdditionalPasskeyRecentAuthenticationAsync();
        await RunPasskeyLabelsAsync();
        await ModelReleaseTests.RunAsync();
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client();
        using var stranger = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        Check((await stranger.GetAsync("/app/v1/admin/users")).StatusCode == HttpStatusCode.Unauthorized, "anonymous admin denied");
        Check((await stranger.PostAsJsonAsync("/app/v1/passkeys/request-options", new { })).StatusCode == HttpStatusCode.BadRequest, "CSRF required before signin");
        await CsrfAsync(owner);
        Check((await owner.PostAsJsonAsync("/app/v1/enrollment/options", new { token = "incorrect-bootstrap-token-value", displayName = "Owner" })).StatusCode == HttpStatusCode.BadRequest, "no first anonymous bypass");
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        var ownerSession = await CsrfAsync(owner);
        var ownerId = ownerSession.GetProperty("account").GetProperty("id").GetString()!;
        await ModelReleaseTests.RunAuthorizedAsync(fixture.App, owner, stranger);
        Check(ownerSession.GetProperty("account").GetProperty("roles")[0].GetString() == "Owner", "bootstrap creates owner after verified attestation");
        var ownerCookie = fixture.Cookies(owner).GetCookies(owner.BaseAddress!).Cast<Cookie>().Single(x => x.Name == "__Host-dv-account");
        Check(ownerCookie.Secure && ownerCookie.HttpOnly, "account cookie secure and HttpOnly");
        Check((await owner.PostAsJsonAsync("/guest/v1/test", new { })).StatusCode == HttpStatusCode.NoContent, "signed in guest request retains independent CSRF contract");
        Check((await owner.PutAsJsonAsync($"/app/v1/admin/users/{ownerId}", new { role = "Viewer", disabled = false })).StatusCode == HttpStatusCode.Conflict, "last owner cannot be demoted");
        Check((await owner.PutAsJsonAsync($"/app/v1/admin/users/{ownerId}", new { role = "Owner", disabled = true })).StatusCode == HttpStatusCode.Conflict, "last owner cannot be disabled");
        var ownerDetails = await JsonAsync(await owner.GetAsync("/app/v1/account"));
        var currentSession = ownerDetails.GetProperty("sessions").EnumerateArray().Single(session => session.GetProperty("current").GetBoolean());
        Check(ownerSession.GetProperty("expiresAt").GetInt64() == currentSession.GetProperty("expiresAt").GetInt64(),
            "session response exposes actual database expiry for bounded native offline admission");
        var lastKeyId = ownerDetails.GetProperty("passkeys")[0].GetProperty("id").GetString();
        foreach (var invalidName in new[] { " ", new string('x', 81), "line\nbreak" })
        {
            Check((await owner.PutAsJsonAsync($"/app/v1/passkeys/{lastKeyId}", new { name = invalidName })).StatusCode == HttpStatusCode.BadRequest,
                "passkey name validates bounded nonblank printable metadata");
        }
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.PutAsJsonAsync($"/app/v1/passkeys/{lastKeyId}", new { name = "Laptop" })).StatusCode == HttpStatusCode.BadRequest, "passkey rename requires CSRF");
        await CsrfAsync(owner);
        Check((await owner.PutAsJsonAsync($"/app/v1/passkeys/{lastKeyId}", new { name = "Owner laptop" })).StatusCode == HttpStatusCode.NoContent, "own passkey renamed");
        using (var namedLogin = fixture.Client())
        {
            await SignInAsync(namedLogin, ownerKey);
            var namedDetails = await JsonAsync(await namedLogin.GetAsync("/app/v1/account"));
            Check(namedDetails.GetProperty("passkeys")[0].GetProperty("name").GetString() == "Owner laptop", "passkey name persists through real verifier sign-in and new database scope");
        }
        Check((await owner.DeleteAsync("/app/v1/passkeys/" + lastKeyId)).StatusCode == HttpStatusCode.Conflict, "last owner passkey cannot be removed");
        using (var crossOrigin = fixture.Client())
        {
            await CsrfAsync(crossOrigin);
            crossOrigin.DefaultRequestHeaders.Remove("Origin");
            crossOrigin.DefaultRequestHeaders.Add("Origin", "https://evil.invalid");
            Check((await crossOrigin.PostAsJsonAsync("/app/v1/passkeys/request-options", new { })).StatusCode == HttpStatusCode.Forbidden, "unlisted origin denied");
        }

        var invitationResponse = await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Operator", recoveryUserId = (string?)null });
        Check(invitationResponse.IsSuccessStatusCode, "owner invitation");
        var invitation = (await JsonAsync(invitationResponse)).GetProperty("token").GetString()!;
        using var operatorClient = fixture.Client();
        using var operatorKey = new VirtualPasskey();
        await EnrollAsync(operatorClient, operatorKey, invitation, "Operator");
        var operatorSession = await CsrfAsync(operatorClient);
        var operatorId = operatorSession.GetProperty("account").GetProperty("id").GetString()!;
        await ModelReleaseTests.RunDeniedAsync(operatorClient);
        Check((await operatorClient.GetAsync("/app/v1/admin/users")).StatusCode == HttpStatusCode.Forbidden, "operator admin denied");
        Check((await operatorClient.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Owner", recoveryUserId = (string?)null })).StatusCode == HttpStatusCode.Forbidden, "operator cannot mint owner");
        await CsrfAsync(stranger);
        Check((await stranger.PostAsJsonAsync("/app/v1/enrollment/options", new { token = invitation, displayName = "Replay" })).StatusCode == HttpStatusCode.BadRequest, "invitation replay denied");

        await SeedAsync(fixture.App, async db =>
        {
            db.Invitations.Add(new AccountInvitation { TokenHash = AccountEndpoints.Hash("expired-invitation-secret"), ExpiresAt = AccountEndpoints.Now() - 1 });
            db.ResourceGrants.Add(new AccountResourceGrant { Id = "device-grant", UserId = operatorId, ResourceType = "device", ResourceId = "granted-device", Permission = "operate" });
            db.ResourceGrants.Add(new AccountResourceGrant { Id = "workshop-grant", UserId = operatorId, ResourceType = "workshop", ResourceId = "granted-twin", Permission = "read" });
            await db.SaveChangesAsync();
        });
        Check((await stranger.PostAsJsonAsync("/app/v1/enrollment/options", new { token = "expired-invitation-secret", displayName = "Expired" })).StatusCode == HttpStatusCode.BadRequest, "expired invitation denied");
        operatorClient.DefaultRequestHeaders.Add("X-DV-Device-Id", "denied-device");
        Check((await operatorClient.GetAsync("/v1/test-device")).StatusCode == HttpStatusCode.Unauthorized, "other device denied");
        operatorClient.DefaultRequestHeaders.Remove("X-DV-Device-Id");
        operatorClient.DefaultRequestHeaders.Add("X-DV-Device-Id", "granted-device");
        var device = await operatorClient.GetAsync("/v1/test-device");
        Check(device.IsSuccessStatusCode && await device.Content.ReadAsStringAsync() == "granted-device", "exact granted device included");
        Check((await operatorClient.PostAsJsonAsync("/app/v1/devices/denied-device/association", new { alias = "Spoofed pairing" })).StatusCode == HttpStatusCode.Forbidden, "client pairing cannot grant unauthorized device");
        Check((await operatorClient.PostAsJsonAsync("/app/v1/devices/granted-device/association", new { alias = "My paired suitcase" })).IsSuccessStatusCode, "preauthorized target association");
        Check((await operatorClient.PutAsJsonAsync($"/app/v1/passkeys/{lastKeyId}", new { name = "Other account" })).StatusCode == HttpStatusCode.NotFound, "cross-account passkey rename denied without disclosure");
        await EveningScopeAsync(fixture, operatorClient);
        var twins = await JsonAsync(await operatorClient.GetAsync("/v1/twin/instances"));
        Check(twins.GetProperty("instances").GetArrayLength() == 1 && twins.GetProperty("instances")[0].GetProperty("instanceId").GetString() == "granted-twin", "workshop enumeration scoped to grant");
        Check((await operatorClient.GetAsync("/v1/twin/instances/denied-twin")).StatusCode == HttpStatusCode.Forbidden, "cross-user workshop target denied");
        Check((await operatorClient.PostAsJsonAsync("/v1/twin/instances/granted-twin/commands", new { action = "stop" })).StatusCode == HttpStatusCode.Forbidden, "observe cannot control live workshop");
        await AccountMigrationTests.RunAsync(fixture.App);
        await AccountArchiveHttpTests.RunAsync(fixture.App, owner, operatorClient, ownerId);
        await AccountShowTests.RunAsync(fixture.App, owner, operatorClient, stranger, operatorId);
        await SimulationAndStepUpAsync(fixture, owner, ownerKey, operatorClient, operatorKey, operatorId);
        await SeedAsync(fixture.App, db => db.ResourceGrants.Where(x => x.Id == "device-grant").ExecuteDeleteAsync());
        Check((await operatorClient.GetAsync("/v1/test-device")).StatusCode == HttpStatusCode.Unauthorized, "grant revocation immediate");
        Check((await operatorClient.GetAsync("/v1/evenings")).StatusCode == HttpStatusCode.Unauthorized, "evening grant revocation immediate");

        await SignInAsync(stranger, operatorKey);
        await CsrfAsync(stranger);
        var beforeRoleChange = await JsonAsync(await stranger.GetAsync("/app/v1/account"));
        Check(beforeRoleChange.GetProperty("sessions").GetArrayLength() == 2, "multiple revocable sessions persisted");
        Check((await owner.PutAsJsonAsync($"/app/v1/admin/users/{operatorId}", new { role = "Viewer", disabled = false })).IsSuccessStatusCode, "owner role change");
        Check((await stranger.GetAsync("/app/v1/account")).StatusCode == HttpStatusCode.Unauthorized, "role change revokes existing sessions");
        Check((await operatorClient.GetAsync("/v1/test-account-simulation/sim-auth")).StatusCode == HttpStatusCode.Unauthorized, "session revocation also removes private simulation authorization");
        Check((await operatorClient.GetAsync("/v1/evenings")).StatusCode == HttpStatusCode.Unauthorized, "revoked session cannot read evenings");
        Check((await operatorClient.GetAsync("/v1/show/devices/granted-device")).StatusCode == HttpStatusCode.Unauthorized, "revoked account session cannot observe Show");
        await SignInAsync(stranger, operatorKey);
        var fresh = await CsrfAsync(stranger);
        Check(fresh.GetProperty("account").GetProperty("roles")[0].GetString() == "Viewer", "signin reads current role");
        Check((await stranger.GetAsync("/app/v1/workshop/granted-twin/engines")).IsSuccessStatusCode, "Viewer retains scoped read-only canonical engine metadata");
        await ModelReleaseTests.RunDeniedAsync(stranger);
        Check((await stranger.DeleteAsync("/app/v1/session")).StatusCode == HttpStatusCode.NoContent, "signout succeeds");
        Check((await stranger.GetAsync("/app/v1/account")).StatusCode == HttpStatusCode.Unauthorized, "signout revokes session");
        await CeremonyReplayAndExpiryAsync(fixture, ownerKey);
        await RecoveryAndOwnerRaceAsync(fixture, owner, ownerId, ownerKey);
        Console.WriteLine("PASS account identity HTTPS WebAuthn registration/signin, authz, CSRF/origin, invitation replay/expiry, session revocation, device/workshop grants and last-owner protection");
    }

    private static async Task DisabledIdentitySessionAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ApplicationIdentity:Enabled"] = "false" });
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddAccountIdentity(builder.Configuration);
        await using var app = builder.Build();
        app.UseRateLimiter();
        app.UseAccountIdentity();
        app.MapAccountEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using var response = await client.GetAsync("/app/v1/session");
        Check(response.StatusCode == HttpStatusCode.OK, "disabled identity session endpoint remains available");
        var session = await response.Content.ReadFromJsonAsync<JsonElement>();
        Check(!session.GetProperty("enabled").GetBoolean() && session.GetProperty("account").ValueKind == JsonValueKind.Null,
            "disabled identity does not require an account database or authenticate a user");
    }

    private static void ServiceCredentialBoundaries()
    {
        const string service = "isolated-legacy-service-credential";
        const string device = "isolated-firmware-credential";
        var values = new Dictionary<string, string?>
        {
            ["ApplicationIdentity:Enabled"] = "true", ["Backend:Admin:CredentialSha256"] = AccountEndpoints.Hash(service),
            ["Devices:0:DeviceId"] = "firmware-device", ["Devices:0:CredentialSha256"] = AccountEndpoints.Hash(device),
            ["DigitalTwin:Credentials:0:Role"] = "observe", ["DigitalTwin:Credentials:0:Sha256"] = AccountEndpoints.Hash(service)
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Check(!new ServerAdminAccess(config).Verify(service), "account-enabled human administration has no default bearer bypass");
        Check(new DeviceAccessStore(config, TimeProvider.System).VerifyCredential(device) == "firmware-device", "firmware credential remains separate and valid");
        Check(new TwinAccess(config, TimeProvider.System).CreateSession(service) is null, "account-enabled Workshop has no default human credential bypass");
        values["Backend:Admin:LegacyServiceCredentialEnabled"] = "true";
        values["DigitalTwin:LegacyServiceSessionsEnabled"] = "true";
        var explicitService = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Check(new ServerAdminAccess(explicitService).Verify(service)
            && new TwinAccess(explicitService, TimeProvider.System).CreateSession(service) is not null,
            "explicit advanced service harness compatibility remains available");
    }

    private static async Task PersistentAccountAuthorizationAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client(); using var actor = fixture.Client();
        using var ownerKey = new VirtualPasskey(); using var actorKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Stream owner"); var ownerIdentity = await CsrfAsync(owner);
        var ownerId = ownerIdentity.GetProperty("account").GetProperty("id").GetString();
        var invite = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations", new {role = "Operator", recoveryUserId = (string?)null}));
        await EnrollAsync(actor, actorKey, invite.GetProperty("token").GetString()!, "Stream operator");
        var identity = await CsrfAsync(actor); var userId = identity.GetProperty("account").GetProperty("id").GetString()!;
        await SeedAsync(fixture.App, async db =>
        {
            db.ResourceGrants.Add(new AccountResourceGrant {Id = "stream-device", UserId = userId, ResourceType = "device", ResourceId = "granted-device", Permission = "read"});
            db.ResourceGrants.Add(new AccountResourceGrant {Id = "stream-workshop", UserId = userId, ResourceType = "workshop", ResourceId = "granted-twin", Permission = "read"});
            await db.SaveChangesAsync();
        });
        var engines = await JsonAsync(await actor.GetAsync("/app/v1/workshop/granted-twin/engines"));
        Check(engines.GetProperty("schemaVersion").GetInt32() == 1 && engines.GetProperty("engines").GetArrayLength() == 2
            && engines.GetProperty("engines")[0].GetProperty("model").GetString() == "gpt-live-1"
            && engines.GetProperty("engines")[1].GetProperty("model").GetString() == "gpt-realtime-2.1", "scoped Workshop capabilities expose exactly canonical engines");
        Check(!engines.TryGetProperty("personalities", out _), "Workshop capabilities expose no admin metadata");
        Check((await actor.GetAsync("/app/v1/workshop/denied-twin/engines")).StatusCode == HttpStatusCode.Forbidden, "Workshop capabilities enforce exact read grant");
        Check((await actor.GetAsync("/app/v1/workshop/missing/engines")).StatusCode == HttpStatusCode.Forbidden, "Workshop capability visibility does not expose ungranted instance existence");
        Check((await owner.GetAsync("/app/v1/workshop/missing/engines")).StatusCode == HttpStatusCode.NotFound, "Workshop capabilities require existing instance even for owner");
        Check((await actor.PutAsJsonAsync("/v1/admin/test-account-confirmation", new {})).StatusCode == HttpStatusCode.Forbidden, "device Operator cannot bind verified guest memory through admin role");
        Check((await actor.GetAsync("/v1/admin/test-account-read")).StatusCode == HttpStatusCode.Unauthorized, "device Operator cannot inspect admin memory");
        owner.DefaultRequestHeaders.Remove("X-DV-Confirm");
        Check((await owner.PutAsJsonAsync("/v1/admin/test-account-confirmation", new {})).StatusCode == HttpStatusCode.Conflict, "raw admin account mutation requires explicit confirmation");
        owner.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        Check((await owner.PutAsJsonAsync("/v1/admin/test-account-confirmation", new {})).IsSuccessStatusCode, "recent owner can use confirmed raw admin operation");
        await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId == ownerId).ExecuteUpdateAsync(u => u.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now() - 601)));
        Check((await owner.PutAsJsonAsync("/v1/admin/test-account-confirmation", new {})).StatusCode == HttpStatusCode.Forbidden, "raw admin mutation requires recent account verification");
        var reauthentication = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/passkeys/reauthentication-options", new {}));
        Check((await owner.PostAsJsonAsync("/app/v1/passkeys/reauthenticate", new {credential = ownerKey.Assert(reauthentication)})).IsSuccessStatusCode, "owner renews verification before raw admin changes");
        var modes = (await JsonAsync(await owner.GetAsync("/app/v1/admin/operations/status"))).GetProperty("modes");
        Check(!modes.TryGetProperty("dialogMode", out _) && !modes.TryGetProperty("voiceMode", out _), "account operations exposes no retired engine choices");
        Check((await owner.PutAsJsonAsync("/app/v1/admin/operations/modes", new {expectedRevision = modes.GetProperty("revision").GetString(), dialogMode = "fake"})).StatusCode == HttpStatusCode.Gone,
            "account operations never resurrect retired engine knobs");
        foreach (var retired in new[] {"turns", "opening", "transcriptions", "speech"})
        { Check((await actor.PostAsJsonAsync($"/v1/conversations/{Guid.NewGuid():D}/{retired}", new {})).StatusCode == HttpStatusCode.Gone, "retired conversation endpoint stays 410"); }
        await ExpectSocketDeniedAsync(fixture, actor, HttpStatusCode.Unauthorized);
        await SeedAsync(fixture.App, db => db.ResourceGrants.Where(x => x.Id == "stream-device").ExecuteUpdateAsync(update => update.SetProperty(x => x.Permission, "operate")));
        await ExpectSocketDeniedAsync(fixture, actor, HttpStatusCode.Forbidden, origin: "https://evil.invalid");
        await ExpectSocketDeniedAsync(fixture, actor, HttpStatusCode.BadRequest, includeCsrf: false);

        async Task RevokeAndClose(Func<AccountDatabase, Task> revoke, Func<AccountDatabase, Task>? restore = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var socket = AccountSocket(fixture, actor);
            await socket.ConnectAsync(SocketUri(actor), timeout.Token);
            await socket.SendAsync(new ArraySegment<byte>(new byte[] {1}), WebSocketMessageType.Binary, true, timeout.Token);
            var buffer = new byte[8]; var accepted = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            Check(accepted.MessageType == WebSocketMessageType.Binary, "authenticated account socket admits operate frame");
            await SeedAsync(fixture.App, revoke);
            await socket.SendAsync(new ArraySegment<byte>(new byte[] {2}), WebSocketMessageType.Binary, true, timeout.Token);
            var rejected = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
            Check(rejected.MessageType == WebSocketMessageType.Close && rejected.CloseStatus == WebSocketCloseStatus.PolicyViolation,
                "persistent account socket closes before accepting revoked authority frame");
            if (restore is not null) await SeedAsync(fixture.App, restore);
        }
        await RevokeAndClose(db => db.ResourceGrants.Where(x => x.Id == "stream-device").ExecuteUpdateAsync(u => u.SetProperty(x => x.Permission, "read")),
            db => db.ResourceGrants.Where(x => x.Id == "stream-device").ExecuteUpdateAsync(u => u.SetProperty(x => x.Permission, "operate")));
        string operatorRole = "", viewerRole = "";
        await SeedAsync(fixture.App, async db => { operatorRole = (await db.Roles.SingleAsync(x => x.Name == "Operator")).Id; viewerRole = (await db.Roles.SingleAsync(x => x.Name == "Viewer")).Id; });
        await RevokeAndClose(async db => { await db.UserRoles.Where(x => x.UserId == userId).ExecuteDeleteAsync(); db.UserRoles.Add(new IdentityUserRole<string> {UserId = userId, RoleId = viewerRole}); await db.SaveChangesAsync(); },
            async db => { await db.UserRoles.Where(x => x.UserId == userId).ExecuteDeleteAsync(); db.UserRoles.Add(new IdentityUserRole<string> {UserId = userId, RoleId = operatorRole}); await db.SaveChangesAsync(); });
        await RevokeAndClose(db => db.Users.Where(x => x.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Disabled, true)),
            db => db.Users.Where(x => x.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.Disabled, false)));
        string? securityStamp = null;
        await SeedAsync(fixture.App, async db => securityStamp = (await db.Users.SingleAsync(x => x.Id == userId)).SecurityStamp);
        await RevokeAndClose(db => db.Users.Where(x => x.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, Guid.NewGuid().ToString("N"))),
            db => db.Users.Where(x => x.Id == userId).ExecuteUpdateAsync(u => u.SetProperty(x => x.SecurityStamp, securityStamp)));
        var details = await JsonAsync(await actor.GetAsync("/app/v1/account"));
        var sessionId = details.GetProperty("sessions").EnumerateArray().Single(x => x.GetProperty("current").GetBoolean()).GetProperty("id").GetString();
        await RevokeAndClose(db => db.Sessions.Where(x => x.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAt, AccountEndpoints.Now() - 1)),
            db => db.Sessions.Where(x => x.Id == sessionId).ExecuteUpdateAsync(u => u.SetProperty(x => x.ExpiresAt, AccountEndpoints.Now() + 3600)));
        await RevokeAndClose(db => db.Sessions.Where(x => x.Id == sessionId).ExecuteDeleteAsync());
        Console.WriteLine("PASS persistent account socket operate/origin/CSRF admission, grant/role/disable/expiry/session revocation, canonical Workshop capabilities and retired-route/admin boundaries");
    }

    private static Uri SocketUri(HttpClient client) => new UriBuilder(client.BaseAddress!) {Scheme = "wss", Path = "/v1/test-account-stream"}.Uri;
    private static ClientWebSocket AccountSocket(Fixture fixture, HttpClient client, string origin = "https://localhost", bool includeCsrf = true)
    {
        var socket = new ClientWebSocket(); socket.Options.Cookies = fixture.Cookies(client);
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", origin); socket.Options.SetRequestHeader("X-DV-Device-Id", "granted-device");
        if (includeCsrf) socket.Options.SetRequestHeader("X-DV-CSRF", client.DefaultRequestHeaders.GetValues("X-DV-CSRF").Single());
        return socket;
    }
    private static async Task ExpectSocketDeniedAsync(Fixture fixture, HttpClient client, HttpStatusCode expected, string origin = "https://localhost", bool includeCsrf = true)
    {
        using var socket = AccountSocket(fixture, client, origin, includeCsrf);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await socket.ConnectAsync(SocketUri(client), timeout.Token); throw new InvalidOperationException("Unauthorized socket admitted"); }
        catch (WebSocketException) { Check(socket.HttpStatusCode == expected, $"account socket admission expected {(int)expected}, got {(int)socket.HttpStatusCode}"); }
    }

    private static async Task EveningScopeAsync(Fixture fixture, HttpClient client)
    {
        var details = await JsonAsync(await client.GetAsync("/app/v1/account"));
        Check(details.GetProperty("devices").GetArrayLength() == 1
            && details.GetProperty("devices")[0].GetProperty("deviceId").GetString() == "granted-device", "evening device selector excludes ungranted devices");
        var initial = await JsonAsync(await client.GetAsync("/v1/evenings"));
        var previousId = initial.GetProperty("activeEveningId").GetString();
        await SeedAsync(fixture.App, db => db.ResourceGrants.Where(x => x.Id == "device-grant")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Permission, "read")));
        Check((await client.GetAsync("/v1/evenings")).IsSuccessStatusCode, "read grant lists evenings");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        var body = new { name = "Account evening", expectedEveningId = previousId };
        Check((await client.PostAsJsonAsync("/v1/evenings", body)).StatusCode == HttpStatusCode.Unauthorized, "read grant cannot create or activate evening");
        await SeedAsync(fixture.App, db => db.ResourceGrants.Where(x => x.Id == "device-grant")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Permission, "operate")));
        var created = await JsonAsync(await client.PostAsJsonAsync("/v1/evenings", body));
        var activeId = created.GetProperty("activeEveningId").GetString();
        var replay = await JsonAsync(await client.PostAsJsonAsync("/v1/evenings", body));
        Check(activeId != previousId && replay.GetProperty("activeEveningId").GetString() == activeId, "evening creation idempotent under exact account device");
        Check((await client.PostAsJsonAsync("/v1/evenings", new { name = "Changed intent", expectedEveningId = previousId })).StatusCode == HttpStatusCode.Conflict, "evening idempotency key rejects changed intent");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        Check((await client.PutAsJsonAsync($"/v1/evenings/{previousId}/active", new { expectedEveningId = previousId })).StatusCode == HttpStatusCode.Conflict, "evening stale expected revision rejected");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://evil.invalid");
        Check((await client.PutAsJsonAsync($"/v1/evenings/{previousId}/active", new { expectedEveningId = activeId })).StatusCode == HttpStatusCode.Forbidden, "evening mutation exact origin enforced");
        client.DefaultRequestHeaders.Remove("Origin"); client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        client.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await client.PutAsJsonAsync($"/v1/evenings/{previousId}/active", new { expectedEveningId = activeId })).StatusCode == HttpStatusCode.BadRequest, "evening mutation requires account CSRF");
        await CsrfAsync(client);
        Check((await client.PutAsJsonAsync($"/v1/evenings/{previousId}/active", new { expectedEveningId = activeId })).IsSuccessStatusCode, "operate grant activates existing evening without provider call");
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
    }

    private static async Task SimulationAndStepUpAsync(Fixture fixture, HttpClient owner, VirtualPasskey ownerKey,
        HttpClient operatorClient, VirtualPasskey operatorKey, string operatorId)
    {
        await SeedAsync(fixture.App, async db =>
        {
            db.ResourceGrants.Add(new AccountResourceGrant { Id = "simulation-grant", UserId = operatorId, ResourceType = "workshop", ResourceId = "sim-auth", Permission = "read" });
            await db.SaveChangesAsync();
        });
        Check((await operatorClient.GetAsync("/v1/test-account-simulation/sim-auth")).StatusCode == HttpStatusCode.Forbidden,
            "account observe grant cannot authorize private simulation media");
        await SeedAsync(fixture.App, db => db.ResourceGrants.Where(x => x.Id == "simulation-grant")
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Permission, "operate")));
        var first = await JsonAsync(await owner.GetAsync("/v1/test-account-simulation/sim-auth"));
        var second = await JsonAsync(await operatorClient.GetAsync("/v1/test-account-simulation/sim-auth"));
        Check(first.GetProperty("sessionId").GetString() != second.GetProperty("sessionId").GetString(), "account A/B simulation media ownership stays separate");
        await SeedAsync(fixture.App, db => db.ResourceGrants.Where(x => x.Id == "simulation-grant").ExecuteDeleteAsync());
        Check((await operatorClient.GetAsync("/v1/test-account-simulation/sim-auth")).StatusCode == HttpStatusCode.Forbidden,
            "simulation media grant revocation effective immediately");
        await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId != operatorId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now() - 601)));
        Check((await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Viewer", recoveryUserId = (string?)null })).StatusCode == HttpStatusCode.Forbidden,
            "admin change requires recent passkey verification");
        var ownKey = (await JsonAsync(await owner.GetAsync("/app/v1/account"))).GetProperty("passkeys")[0].GetProperty("id").GetString();
        Check((await owner.PutAsJsonAsync($"/app/v1/passkeys/{ownKey}", new { name = "Expired authentication" })).StatusCode == HttpStatusCode.Forbidden, "passkey rename requires recent authentication");
        var wrongOptions = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/passkeys/reauthentication-options", new { }));
        Check((await owner.PostAsJsonAsync("/app/v1/passkeys/reauthenticate", new { credential = operatorKey.Assert(wrongOptions) })).StatusCode == HttpStatusCode.Forbidden,
            "step-up cannot silently switch to another account");
        var options = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/passkeys/reauthentication-options", new { }));
        Check((await owner.PostAsJsonAsync("/app/v1/passkeys/reauthenticate", new { credential = ownerKey.Assert(options) })).IsSuccessStatusCode,
            "same-account passkey step-up succeeds");
        owner.DefaultRequestHeaders.Remove("X-DV-Confirm");
        Check((await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Viewer", recoveryUserId = (string?)null })).StatusCode == HttpStatusCode.Conflict,
            "recent verification does not bypass explicit confirmation");
        owner.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
    }

    private static async Task RecoveryAndOwnerRaceAsync(Fixture fixture, HttpClient owner, string ownerId, VirtualPasskey ownerKey)
    {
        var response = await owner.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Owner", recoveryUserId = ownerId });
        var recoveryToken = (await JsonAsync(response)).GetProperty("token").GetString()!;
        using var recovery = fixture.Client();
        await CsrfAsync(recovery);
        var options = await recovery.PostAsJsonAsync("/app/v1/enrollment/options", new { token = recoveryToken, displayName = "" });
        Check(options.IsSuccessStatusCode, "recovery creation options");
        var recoveryIdentity = (await JsonAsync(options)).GetProperty("user");
        Check(recoveryIdentity.GetProperty("displayName").GetString() == "DildoVander Owner · Owner"
            && Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(recoveryIdentity.GetProperty("id").GetString()!)) == ownerId,
            "recovery uses the existing server-bound account name and stable user handle without requesting a new identity");
        Check((await recovery.PostAsJsonAsync("/app/v1/enrollment/complete", new { credential = new { } })).StatusCode == HttpStatusCode.BadRequest, "unverified recovery cannot remove original keys");
        using (var original = fixture.Client()) { await SignInAsync(original, ownerKey); }
        using var replacementKey = new VirtualPasskey();
        await EnrollAsync(recovery, replacementKey, recoveryToken, "Recovered");
        await CsrfAsync(recovery);
        Check((await owner.GetAsync("/app/v1/account")).StatusCode == HttpStatusCode.Unauthorized, "verified recovery revokes previous owner session");
        using (var lostKey = fixture.Client())
        {
            await CsrfAsync(lostKey);
            var lostOptions = await JsonAsync(await lostKey.PostAsJsonAsync("/app/v1/passkeys/request-options", new { }));
            Check((await lostKey.PostAsJsonAsync("/app/v1/passkeys/sign-in", new { credential = ownerKey.Assert(lostOptions) })).StatusCode == HttpStatusCode.Unauthorized, "recovery removes old passkeys");
        }
        Check((await recovery.PostAsJsonAsync("/app/v1/enrollment/options", new { token = recoveryToken, displayName = "Replay" })).StatusCode == HttpStatusCode.BadRequest, "recovery token single use");
        var secondInvite = await JsonAsync(await recovery.PostAsJsonAsync("/app/v1/admin/invitations", new { role = "Owner", recoveryUserId = (string?)null }));
        using var secondOwner = fixture.Client();
        using var secondKey = new VirtualPasskey();
        await EnrollAsync(secondOwner, secondKey, secondInvite.GetProperty("token").GetString()!, "Second owner");
        var second = await CsrfAsync(secondOwner);
        var secondId = second.GetProperty("account").GetProperty("id").GetString()!;
        var attempts = await Task.WhenAll(
            recovery.PutAsJsonAsync($"/app/v1/admin/users/{ownerId}", new { role = "Viewer", disabled = false }),
            secondOwner.PutAsJsonAsync($"/app/v1/admin/users/{secondId}", new { role = "Viewer", disabled = false }));
        Check(attempts.Count(x => x.StatusCode == HttpStatusCode.NoContent) == 1
            && attempts.Count(x => x.StatusCode == HttpStatusCode.Conflict) == 1, "concurrent demotion leaves one owner");
    }

    private static async Task CeremonyReplayAndExpiryAsync(Fixture fixture, VirtualPasskey key)
    {
        using var client = fixture.Client();
        await CsrfAsync(client);
        var options = await JsonAsync(await client.PostAsJsonAsync("/app/v1/passkeys/request-options", new { }));
        var credential = key.Assert(options);
        var ceremony = fixture.Cookies(client).GetCookies(client.BaseAddress!).Cast<Cookie>().Single(x => x.Name == AccountIdentity.CeremonyCookie).Value;
        Check((await client.PostAsJsonAsync("/app/v1/passkeys/sign-in", new { credential })).IsSuccessStatusCode, "valid assertion");
        await CsrfAsync(client);
        fixture.Cookies(client).Add(client.BaseAddress!, new Cookie(AccountIdentity.CeremonyCookie, ceremony, "/") { Secure = true, HttpOnly = true });
        Check((await client.PostAsJsonAsync("/app/v1/passkeys/sign-in", new { credential })).StatusCode == HttpStatusCode.BadRequest, "challenge replay denied despite restored cookie");
        await client.PostAsJsonAsync("/app/v1/passkeys/request-options", new { });
        await SeedAsync(fixture.App, db => db.Ceremonies.ExecuteUpdateAsync(update => update.SetProperty(x => x.ExpiresAt, AccountEndpoints.Now() - 1)));
        Check((await client.PostAsJsonAsync("/app/v1/passkeys/sign-in", new { credential })).StatusCode == HttpStatusCode.BadRequest, "expired server challenge denied");
    }

    private static async Task AdditionalPasskeyRecentAuthenticationAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var client = fixture.Client();
        using var anonymous = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(client, ownerKey, Fixture.Bootstrap, "Owner");
        var session = await CsrfAsync(client);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;
        await CsrfAsync(anonymous);
        var failures = new List<string>();
        foreach (var (optionsPath, completePath) in new[]
        {
            ("/app/v1/passkeys/options", "/app/v1/passkeys/complete"),
            ("/APP/V1/PASSKEYS/OPTIONS", "/APP/V1/PASSKEYS/COMPLETE"),
            ("/app/v1/passkeys/options/", "/app/v1/passkeys/complete/"),
            ("/App/v1/Passkeys/Options/", "/App/v1/Passkeys/Complete/")
        })
        {
            Check((await anonymous.PostAsJsonAsync(optionsPath, new { })).StatusCode == HttpStatusCode.Unauthorized,
                "anonymous additional passkey options denied: " + optionsPath);
            Check((await anonymous.PostAsJsonAsync(completePath, new { credential = new { } })).StatusCode == HttpStatusCode.Unauthorized,
                "anonymous additional passkey completion denied: " + completePath);
            await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId == ownerId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now() - 601)));
            string[] ceremonyIds = [];
            await SeedAsync(fixture.App, async db => { ceremonyIds = await db.Ceremonies.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync(); });
            using var staleOptions = await client.PostAsJsonAsync(optionsPath, new { });
            if (staleOptions.StatusCode != HttpStatusCode.Forbidden
                || !(await staleOptions.Content.ReadAsStringAsync()).Contains("recent_passkey_required", StringComparison.Ordinal))
            { failures.Add($"stale options {optionsPath}: HTTP {(int)staleOptions.StatusCode}"); }
            await SeedAsync(fixture.App, async db =>
            {
                var currentIds = await db.Ceremonies.OrderBy(x => x.Id).Select(x => x.Id).ToArrayAsync();
                if (!currentIds.SequenceEqual(ceremonyIds)) { failures.Add("stale options changed ceremonies: " + optionsPath); }
            });

            await SignInAsync(client, ownerKey);
            await CsrfAsync(client);
            using var additionalKey = new VirtualPasskey();
            var options = await JsonAsync(await client.PostAsJsonAsync(optionsPath, new { }));
            var credential = additionalKey.Create(options);
            var countBefore = (await JsonAsync(await client.GetAsync("/app/v1/account"))).GetProperty("passkeys").GetArrayLength();
            await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId == ownerId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now() - 601)));
            using var staleCompletion = await client.PostAsJsonAsync(completePath, new { credential });
            if (staleCompletion.StatusCode != HttpStatusCode.Forbidden
                || !(await staleCompletion.Content.ReadAsStringAsync()).Contains("recent_passkey_required", StringComparison.Ordinal))
            { failures.Add($"stale completion {completePath}: HTTP {(int)staleCompletion.StatusCode}"); }
            var countAfter = (await JsonAsync(await client.GetAsync("/app/v1/account"))).GetProperty("passkeys").GetArrayLength();
            if (countAfter != countBefore) { failures.Add("stale completion enrolled a passkey: " + completePath); }

            // Restore only the session timestamp so the same attestation proves
            // the rejected completion did not consume its pending ceremony.
            await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId == ownerId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now())));
            using var freshCompletion = await client.PostAsJsonAsync(completePath, new { credential });
            if (freshCompletion.StatusCode != HttpStatusCode.NoContent)
            { failures.Add($"fresh completion {completePath}: HTTP {(int)freshCompletion.StatusCode}"); }
        }
        Check(failures.Count == 0, "additional passkey recent authentication: " + string.Join("; ", failures));
        Console.WriteLine("PASS additional passkey canonical/case/slash routes: anonymous rejection, recent authentication, no stale ceremony issuance/consumption or enrollment, fresh P256 completion");
    }

    private static async Task EnrollAsync(HttpClient client, VirtualPasskey key, string token, string name)
    {
        await CsrfAsync(client);
        var response = await client.PostAsJsonAsync("/app/v1/enrollment/options", new { token, displayName = name });
        Check(response.IsSuccessStatusCode, "creation options: " + await response.Content.ReadAsStringAsync());
        var options = await JsonAsync(response);
        var completion = await client.PostAsJsonAsync("/app/v1/enrollment/complete", new { credential = key.Create(options) });
        Check(completion.IsSuccessStatusCode, "verified registration: " + await completion.Content.ReadAsStringAsync());
    }

    private static async Task SignInAsync(HttpClient client, VirtualPasskey key)
    {
        await CsrfAsync(client);
        var options = await JsonAsync(await client.PostAsJsonAsync("/app/v1/passkeys/request-options", new { }));
        var response = await client.PostAsJsonAsync("/app/v1/passkeys/sign-in", new { credential = key.Assert(options) });
        Check(response.IsSuccessStatusCode, "verified assertion: " + await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> CsrfAsync(HttpClient client)
    {
        var session = await JsonAsync(await client.GetAsync("/app/v1/session"));
        client.DefaultRequestHeaders.Remove("X-DV-CSRF");
        client.DefaultRequestHeaders.Add("X-DV-CSRF", session.GetProperty("csrfToken").GetString());
        return session;
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Check(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.Clone();
    }
    private static async Task SeedAsync(WebApplication app, Func<AccountDatabase, Task> action)
    {
        using var scope = app.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AccountDatabase>());
    }
    private static void Check(bool condition, string label) { if (!condition) { throw new InvalidOperationException("Account identity: " + label); } }

    private sealed class Fixture(WebApplication app, string root, X509Certificate2 certificate) : IAsyncDisposable
    {
        public const string Bootstrap = "offline-test-controlled-bootstrap-secret";
        public WebApplication App { get; } = app;
        private readonly Dictionary<HttpClient, CookieContainer> jars = [];
        public HttpClient Client()
        {
            var jar = new CookieContainer();
            var handler = new HttpClientHandler { CookieContainer = jar, UseProxy = false, ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
            var address = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var client = new HttpClient(handler) { BaseAddress = new Uri(address.Replace("127.0.0.1", "localhost")) };
            client.DefaultRequestHeaders.Add("Origin", "https://localhost");
            client.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
            jars[client] = jar; return client;
        }
        public CookieContainer Cookies(HttpClient client) => jars[client];
        public static async Task<Fixture> StartAsync(bool browserMode = false, bool customerFeatures = false,
            bool deletionFeatures = false, bool customerMemoryFeatures = false,
            bool accountDataExportFeatures = false, Action<WebApplication>? configureEndpoints = null,
            Action<IServiceCollection>? configureServices = null)
        {
            var root = Path.Combine(Path.GetTempPath(), "dv-account-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var notLaunched = Path.Combine(root, "authorization-only-native.stub");
            await File.WriteAllTextAsync(notLaunched, "Never launched: fixture exercises authorization only.");
            using var rsa = RSA.Create(2048);
            var certRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            certRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost");
            certRequest.CertificateExtensions.Add(san.Build());
            using var generated = certRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
                ["ApplicationIdentity:Enabled"] = "true", ["ApplicationIdentity:DataRoot"] = Path.Combine(root, "accounts"),
                ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "keys"), ["ApplicationIdentity:RelyingPartyId"] = "localhost",
                ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost", ["ApplicationIdentity:Bootstrap:TokenSha256"] = AccountEndpoints.Hash(Bootstrap),
                ["ApplicationIdentity:RequestsPerMinute"] = "120",
                ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
                ["Keepsake:Origin"] = "https://localhost", ["Keepsake:PublicDeviceId"] = "granted-device",
                ["Show:DeviceCueSpeechMode"] = "off",
                ["Keepsake:DataRoot"] = Path.Combine(root, "guest-content"), ["Keepsake:DataProtectionRoot"] = Path.Combine(root, "keys"),
                ["Backend:DatabasePath"] = Path.Combine(root, "guest.sqlite"),
                ["ModelReleases:Enabled"] = "true", ["ModelReleases:Root"] = Path.Combine(root, "models"),
                ["ApplicationIdentity:Bootstrap:ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
                ["Devices:0:DeviceId"] = "granted-device", ["Devices:0:CredentialSha256"] = new string('A', 64),
                ["Devices:1:DeviceId"] = "denied-device", ["Devices:1:CredentialSha256"] = new string('B', 64),
                ["DigitalTwin:DataRoot"] = Path.Combine(root, "twins"), ["DigitalTwin:Instances:granted-twin:Mode"] = "live",
                ["DigitalTwin:Instances:denied-twin:Mode"] = "live"
                , ["DigitalTwin:Instances:sim-auth:Mode"] = "simulation", ["DigitalTwin:Instances:sim-auth:Executable"] = notLaunched,
                ["DigitalTwin:Instances:sim-auth:BuildId"] = "auth-fixture"
            });
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
            builder.Services.AddAccountIdentity(builder.Configuration);
            builder.Services.AddModelReleases(builder.Configuration);
            builder.Services.AddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<DeviceAccessStore>();
            builder.Services.AddSingleton<ServerAdminAccess>();
            builder.Services.AddSingleton<OperationJournal>(); builder.Services.AddSingleton<EveningMemoryStore>();
            builder.Services.AddSingleton(services => new ConversationStore(TimeProvider.System,
                services.GetRequiredService<OperationJournal>(), eveningMemory: services.GetRequiredService<EveningMemoryStore>()));
            builder.Services.AddSingleton<KeepsakeStore>();
            builder.Services.AddSingleton(sp => new ShowCoordinator(builder.Configuration, sp.GetRequiredService<OperationJournal>(),
                sp.GetRequiredService<EveningMemoryStore>(), TimeProvider.System));
            builder.Services.AddSingleton<ShowSpeechProvider>(); builder.Services.AddSingleton<ShowDeviceCueBridge>();
            builder.Services.AddSingleton<IDuelIdentitySource, DuelIdentitySource>();
            builder.Services.AddSingleton<IDuelShowSlot, DuelShowSlot>(); builder.Services.AddSingleton<DuelStore>();
            builder.Services.AddSingleton<OfflineOutcomeStore>();
            builder.Services.AddRateLimiter(options => {
                options.AddPolicy("keepsake", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("fixture"));
                options.AddPolicy("keepsake-public", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("fixture"));
            });
            builder.Services.AddSingleton<TwinAccess>(); builder.Services.AddSingleton<TwinStore>(); builder.Services.AddSingleton<TwinHost>();
            if (customerFeatures || deletionFeatures || customerMemoryFeatures || accountDataExportFeatures)
            {
                builder.Services.AddSingleton<ICharacterEnrichmentProvider>(new CustomerTestEnrichmentProvider());
                builder.Services.AddCustomerCharacters(builder.Configuration);
            }
            if (customerMemoryFeatures || accountDataExportFeatures)
            {
                builder.Services.AddSingleton(provider => HybridMemoryOptions.Read(builder.Configuration,
                    provider.GetRequiredService<OperationJournal>()));
                builder.Services.AddSingleton(provider => new OpenAiVectorMemoryProvider(
                    new HttpClient(new OfflineProviderHandler()), null,
                    provider.GetRequiredService<HybridMemoryOptions>(), ownsHttp: true));
                builder.Services.AddSingleton<HybridMemoryService>();
                builder.Services.AddCustomerMemoryData();
                builder.Services.AddAccountDataExports(builder.Configuration);
            }
            if (deletionFeatures)
            {
                builder.Services.AddSingleton(_ => new Kufr.Backend.Features.PersonaLab.PersonaLabStore(
                    Path.Combine(root, "persona-lab.sqlite"),
                    Kufr.Backend.Features.GptLive.LivePromptSnapshot.Create(
                        PersonalityCatalog.OllivanderId, new Kufr.Backend.Features.GptLive.GptLiveOptions())));
                if (!customerMemoryFeatures && !accountDataExportFeatures)
                {
                    builder.Services.AddSingleton(services => new AccountDataExportStore(
                        services.GetRequiredService<AccountConfiguration>().DataRoot,
                        services.GetRequiredService<TimeProvider>()));
                }
            }
            configureServices?.Invoke(builder.Services);
            var app = builder.Build();
            await AccountProvisioning.MigrateAsync(app.Services);
            await app.InitializeAccountsAsync();
            app.UseWebSockets(); app.UseRateLimiter(); app.UseAccountIdentity(); app.MapAccountEndpoints(); app.MapDigitalTwin(); app.MapModelReleases();
            if (customerFeatures || customerMemoryFeatures || accountDataExportFeatures)
            {
                app.MapCustomerCharacters(); app.MapCustomerSessions();
                if (configureEndpoints is null) { app.MapCustomerConversationPreferences(); }
            }
            if (customerMemoryFeatures || accountDataExportFeatures) { app.MapCustomerMemoryEndpoints(); }
            if (accountDataExportFeatures) { app.MapAccountDataExports(); }
            app.MapConversations();
            app.MapShowEndpoints(); app.MapShowDeviceCues(); app.MapDuels(); app.MapShowArchiveEndpoints();
            app.MapOfflineOutcomeEndpoints(); app.MapShowChronicle();
            app.MapGet("/v1/test-device", (HttpContext context, DeviceAccessStore access) =>
                DeviceAccessEndpoints.Authenticate(context, access) is { } id ? Results.Text(id) : Results.Unauthorized());
            app.MapPost("/guest/v1/test", () => Results.NoContent());
            app.MapGet("/v1/admin/test-account-read", (HttpContext context, ServerAdminAccess admin) => admin.Authenticate(context) ? Results.Ok(new {safe = true}) : Results.Unauthorized());
            app.MapPut("/v1/admin/test-account-confirmation", (HttpContext context, ServerAdminAccess admin) => admin.Authenticate(context) ? Results.NoContent() : Results.Unauthorized());
            // Isolated transport harness exercises the same production live
            // authority helper over real HTTPS/WebSocket without any provider.
            app.MapGet("/v1/test-account-stream", async (HttpContext context, DeviceAccessStore access) =>
            {
                var deviceId = await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access);
                if (deviceId is null) { context.Response.StatusCode = 401; return; }
                using var socket = await context.WebSockets.AcceptWebSocketAsync(); var bytes = new byte[8];
                while (true)
                {
                    var frame = await socket.ReceiveAsync(new ArraySegment<byte>(bytes), context.RequestAborted);
                    if (frame.MessageType == WebSocketMessageType.Close) return;
                    if (await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) != deviceId)
                    { await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "account_access_revoked", CancellationToken.None); return; }
                    await socket.SendAsync(new ArraySegment<byte>(bytes, 0, frame.Count), WebSocketMessageType.Binary, true, context.RequestAborted);
                }
            });
            app.MapGet("/v1/test-account-simulation/{id}", async (string id, HttpContext context, TwinAccess access) =>
            {
                var authorization = await TwinMediaEndpoints.ControlAsync(context, access, id, false);
                return authorization.Error ?? Results.Ok(new { sessionId = authorization.Session!.Id });
            });
            if (browserMode)
            {
                var dist = Path.GetFullPath("mobilni-aplikace/dist");
                if (!File.Exists(Path.Combine(dist, "index.html"))) { throw new InvalidOperationException("Build the shared application before the isolated browser fixture."); }
                var staticOptions = new StaticFileOptions { FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(dist) };
                app.UseStaticFiles(staticOptions);
                app.MapFallbackToFile("index.html", staticOptions);
            }
            configureEndpoints?.Invoke(app);
            await app.StartAsync();
            app.Configuration["Keepsake:Origin"] = app.Urls.Single().Replace("127.0.0.1", "localhost");
            if (browserMode)
            {
                app.Services.GetRequiredService<AccountConfiguration>().AllowedOrigins[0] = app.Urls.Single().Replace("127.0.0.1", "localhost");
            }
            return new Fixture(app, root, certificate);
        }
        public async ValueTask DisposeAsync()
        {
            await App.StopAsync(); await App.DisposeAsync(); certificate.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    // Real P-256 WebAuthn evidence generated offline. No mocked Identity handler,
    // test login endpoint, paid provider, or production credential is involved.
    private sealed class VirtualPasskey : IDisposable
    {
        private readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly byte[] credentialId = RandomNumberGenerator.GetBytes(32);
        private string userId = "";
        private uint counter;
        public object Create(JsonElement options)
        {
            userId = options.GetProperty("user").GetProperty("id").GetString()!;
            var clientData = ClientData("webauthn.create", options);
            var publicKey = key.ExportParameters(false);
            using var cose = new MemoryStream();
            cose.Write([0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21]); CborBytes(cose, publicKey.Q.X!);
            cose.WriteByte(0x22); CborBytes(cose, publicKey.Q.Y!);
            using var auth = new MemoryStream();
            auth.Write(SHA256.HashData(Encoding.UTF8.GetBytes("localhost")));
            auth.Write([0x45, 0, 0, 0, 0]); auth.Write(new byte[16]); auth.Write([0, 32]); auth.Write(credentialId); auth.Write(cose.ToArray());
            using var attestation = new MemoryStream();
            attestation.WriteByte(0xA3); CborText(attestation, "fmt"); CborText(attestation, "none");
            CborText(attestation, "attStmt"); attestation.WriteByte(0xA0); CborText(attestation, "authData"); CborBytes(attestation, auth.ToArray());
            return new { id = B64(credentialId), rawId = B64(credentialId), type = "public-key", authenticatorAttachment = "platform", clientExtensionResults = new { },
                response = new { clientDataJSON = B64(clientData), attestationObject = B64(attestation.ToArray()), transports = new[] { "internal" } } };
        }
        public object Assert(JsonElement options)
        {
            var clientData = ClientData("webauthn.get", options);
            using var auth = new MemoryStream(); auth.Write(SHA256.HashData(Encoding.UTF8.GetBytes("localhost")));
            counter++; auth.Write([0x05, (byte)(counter >> 24), (byte)(counter >> 16), (byte)(counter >> 8), (byte)counter]);
            var authData = auth.ToArray();
            var signature = key.SignData(authData.Concat(SHA256.HashData(clientData)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new { id = B64(credentialId), rawId = B64(credentialId), type = "public-key", authenticatorAttachment = "platform", clientExtensionResults = new { },
                response = new { clientDataJSON = B64(clientData), authenticatorData = B64(authData), signature = B64(signature), userHandle = userId } };
        }
        private static byte[] ClientData(string type, JsonElement options) => JsonSerializer.SerializeToUtf8Bytes(new { type, challenge = options.GetProperty("challenge").GetString(), origin = "https://localhost", crossOrigin = false });
        private static string B64(byte[] value) => WebEncoders.Base64UrlEncode(value);
        private static void CborText(Stream stream, string value) { var bytes = Encoding.UTF8.GetBytes(value); CborLength(stream, 0x60, bytes.Length); stream.Write(bytes); }
        private static void CborBytes(Stream stream, byte[] value) { CborLength(stream, 0x40, value.Length); stream.Write(value); }
        private static void CborLength(Stream stream, int major, int length)
        {
            if (length < 24) { stream.WriteByte((byte)(major | length)); }
            else if (length <= 255) { stream.Write([(byte)(major | 24), (byte)length]); }
            else { stream.Write([(byte)(major | 25), (byte)(length >> 8), (byte)length]); }
        }
        public void Dispose() => key.Dispose();
    }
}

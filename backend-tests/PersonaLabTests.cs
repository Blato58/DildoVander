using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.WebSockets;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;

internal static class PersonaLabTests
{
    private static int checks;
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kufr-lab-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PersistenceAndOwnership(root);
            TicketBoundaries();
            TechnicalLimits();
            SourceBaselineRestart(root);
            ProductionPublication(root);
            await EndpointOwnershipAsync(root);
            Console.WriteLine($"Persona Lab: {checks} deterministic checks passed; no provider calls.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    private static LivePromptSnapshot Baseline() => LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions());
    private static void PersistenceAndOwnership(string root)
    {
        var path = Path.Combine(root, "isolated.sqlite");
        var store = new PersonaLabStore(path, Baseline());
        var baseline = store.ActiveRevisionId;
        Check(store.PublishedOverride() is null, "source baseline preserves existing production builder");
        var first = store.CreateSession("alice", new(baseline, "gpt-live-1", "first-visit", "empty", "A"));
        Check(!first.RecordContent, "transcript recording is explicit opt-in");
        Check(store.Sessions("bob").Length == 0, "session listing is tenant-scoped");
        Reject(() => store.Session("bob", first.Id), 404, "cross-account session detail concealed");
        Reject(() => store.Evaluate("bob", first.Id, Evaluation()), 404, "cross-account evaluation rejected");
        Reject(() => store.CreateSession("alice", new(baseline, "retired-text-model", "first-visit", "empty", "bad")), 400, "retired engine rejected");
        Reject(() => store.CreateSession("alice", new(baseline, "gpt-live-1", "first-visit", "production-guest-id", "bad")), 400, "arbitrary memory identity rejected");
        Reject(() => store.CreateSession("alice", new(null!, "gpt-live-1", "first-visit", "empty", "bad")), 400, "null session revision is a controlled bad request");
        Check(!LabContract.Memory(first).Contains("production-guest-id", StringComparison.Ordinal), "memory context uses server fixture only");
        var draft = store.CreateRevision(new(baseline, "Sharper", "Test exact instructions.", "gpt-realtime-2.1", "marin"));
        Check(store.ActiveRevisionId == baseline && store.PublishedOverride() is null, "draft cannot alter active production instructions");
        var published = store.Publish(draft.Id, baseline);
        Check(published.Status == "published" && store.PublishedOverride()?.Id == draft.Id, "explicit publication activates immutable revision");
        Reject(() => store.Publish(baseline, baseline), 409, "stale publication compare-and-swap rejected");
        Check(store.Session("alice", first.Id).Config.Id == baseline, "publication preserves existing lab snapshot");
        var reopened = new PersonaLabStore(path, Baseline());
        Check(reopened.ActiveRevisionId == draft.Id && reopened.Session("alice", first.Id).Config.Sha256 == first.Config.Sha256,
            "publication and exact session fingerprint survive restart");
        reopened.Publish(baseline, draft.Id, rollback: true);
        Check(reopened.PublishedOverride() is null, "rollback restores source baseline");
        var second = reopened.CreateRevision(new(baseline, "Unpublished", "Still a draft.", "gpt-live-1", "cedar"));
        Reject(() => reopened.Publish(second.Id, baseline, rollback: true), 409, "rollback cannot activate a draft");
        Reject(() => reopened.CreateRevision(new(baseline, "Too large", new string('a', 12001), "gpt-live-1", "cedar")), 400, "prompt limits enforced");
        var evaluation = reopened.Evaluate("alice", first.Id, Evaluation());
        Check(evaluation.Evaluation!.Scores.Count == 7, "seven requested human metrics persisted");
        Reject(() => reopened.Evaluate("alice", first.Id, Evaluation() with { Scores = new() { ["overall"] = 7 } }), 400, "invalid rubric scores rejected");
        Reject(() => reopened.Evaluate("alice", first.Id, Evaluation() with { TurnComments = [new("foreign-turn", "x")] }), 400,
            "turn comment cannot target another conversation");
        Check(LabContract.Scenarios.Length == 19 && LabContract.Fixtures.Length == 3, "nineteen runnable cases and isolated memory fixtures available");
        var suite = reopened.CreateSuite("alice", new(baseline, "gpt-live-1", "empty", ["first-visit", "stop"], "smoke"));
        Check(reopened.Suites("bob").Length == 0 && reopened.Suite("alice", suite.Id).Results.Length == 0, "suite storage ownership and no fabricated results");
        Reject(() => reopened.Suite("bob", suite.Id), 404, "cross-account suite concealed");
        Reject(() => reopened.CreateSuite("alice", new(baseline, "gpt-live-1", "empty", ["stop", "stop"], "bad")), 400, "duplicate suite cases rejected");
        Reject(() => reopened.CreateSuite("alice", new(null!, "gpt-live-1", "empty", ["stop"], "bad")), 400, "null suite revision is a controlled bad request");
        var observed = first with { Turns = [new("t", "assistant", "Ahoj.", DateTimeOffset.UtcNow)] };
        Check(LabRubric.Inspect(observed).All(x => x.Passed), "deterministic rubric uses actual observed output");
        Check(!LabRubric.Inspect(first).First(x => x.Name == "nonempty-output").Passed, "empty output cannot pass observed-response assertion");
    }
    private static LabEvaluation Evaluation() => new(LabContract.Metrics.ToDictionary(x => x, _ => 4), "Human observation", []);
    private static void SourceBaselineRestart(string root)
    {
        var path = Path.Combine(root, "baseline-restart.sqlite");
        var original = Baseline();
        var first = new PersonaLabStore(path, original);
        var originalId = first.ActiveRevisionId;
        var changedInstructions = original.Instructions + "\nNew source baseline for restart regression.";
        var changed = original with { Instructions = changedInstructions, Sha256 = LabContract.Hash(changedInstructions) };
        var restarted = new PersonaLabStore(path, changed);
        Check(restarted.ActiveRevisionId != originalId && restarted.PublishedOverride() is null,
            "source-default baseline advances to actual changed code fingerprint on restart");
        Check(restarted.Revisions().Single(x => x.Id == originalId).Instructions == original.Instructions,
            "old baseline instructions remain immutable history");
        restarted.Publish(originalId, restarted.ActiveRevisionId, rollback: true);
        Check(restarted.PublishedOverride()?.Instructions == original.Instructions,
            "explicit rollback to historical baseline applies its exact saved instructions");
        var rolledBackRestart = new PersonaLabStore(path, changed);
        Check(rolledBackRestart.ActiveRevisionId == originalId && rolledBackRestart.PublishedOverride()?.Sha256 == original.Sha256,
            "explicit historical baseline rollback survives restart without following new source");
        var draft = rolledBackRestart.CreateRevision(new(originalId, "Persistent publication", "Published after rollback.", "gpt-live-1", "cedar"));
        rolledBackRestart.Publish(draft.Id, originalId);
        var thirdInstructions = changedInstructions + "\nAnother source revision.";
        var publishedRestart = new PersonaLabStore(path, changed with { Instructions = thirdInstructions, Sha256 = LabContract.Hash(thirdInstructions) });
        Check(publishedRestart.ActiveRevisionId == draft.Id && publishedRestart.PublishedOverride()?.Instructions == draft.Instructions,
            "published override remains active when a later source build changes its baseline");
    }
    private static void TechnicalLimits()
    {
        var values = new Dictionary<string, string?>
        {
            ["PersonaLab:MaxConcurrentSessions"] = "8", ["PersonaLab:MaxConcurrentSuites"] = "3",
            ["PersonaLab:MaxSessionSeconds"] = "60", ["PersonaLab:MaxSuiteSeconds"] = "600"
        };
        var configured = PersonaLabOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        Check(configured.MaxConcurrentSessions == 8 && configured.MaxConcurrentSuites == 3
            && configured.MaxSessionSeconds == 60 && configured.MaxSuiteSeconds == 600, "configured technical limits parsed exactly");
        foreach (var (key, value) in new[] { ("MaxConcurrentSessions", "17"), ("MaxConcurrentSuites", "0"),
            ("MaxSessionSeconds", "181"), ("MaxSuiteSeconds", "1801") })
        {
            var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PersonaLab:" + key] = value }).Build();
            try { PersonaLabOptions.Read(invalid); throw new Exception("Invalid technical limit accepted."); }
            catch (InvalidOperationException) { checks++; }
        }
    }
    private static void TicketBoundaries()
    {
        var clock = new LabClock();
        var registry = new LabTicketRegistry(clock);
        var admission = new LabAdmission("alice", "session", "account-session", "stamp", "https://example.test", clock.GetUtcNow());
        var ticket = registry.Mint(admission);
        Check(ticket.Token.Length == 64 && ticket.ExpiresAt == clock.GetUtcNow().AddSeconds(30), "random ticket is short-lived");
        Check(registry.Take(ticket.Token, "session", "https://example.test").Owner == "alice", "ticket binds owner and session");
        Reject(() => registry.Take(ticket.Token, "session", "https://example.test"), 401, "ticket replay rejected");
        var foreignOrigin = registry.Mint(admission);
        Reject(() => registry.Take(foreignOrigin.Token, "session", "https://attacker.test"), 401, "cross-origin ticket rejected");
        var foreignSession = registry.Mint(admission);
        Reject(() => registry.Take(foreignSession.Token, "other-session", admission.Origin), 401, "ticket cannot target another session");
        var expired = registry.Mint(admission);
        clock.Advance(TimeSpan.FromSeconds(31));
        Reject(() => registry.Take(expired.Token, "session", admission.Origin), 401, "expired ticket rejected");
        var native = registry.Mint(admission with { Origin = "capacitor://localhost" });
        Check(registry.Take(native.Token, "session", "capacitor://localhost").Owner == "alice", "native origin explicitly bound");
        var recovery = registry.Mint(admission with { Recovery = true });
        Check(registry.Take(recovery.Token, "session", admission.Origin).Recovery, "explicit recovery admission is bound to the one-use server ticket");
    }
    private static void ProductionPublication(string root)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Backend:DatabasePath"] = Path.Combine(root, "production.sqlite") }).Build();
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var lab = new PersonaLabStore(Path.Combine(root, "publication.sqlite"), Baseline());
        using var dialog = new TextDialogRunner((_, _, _, _, _) => throw new Exception("Legacy Sol must not run."));
        using var voice = new VoiceProviderRunner((_, _) => throw new Exception("STT must not run."), (_, _) => throw new Exception("TTS must not run."));
        var store = new ConversationStore(TimeProvider.System, journal, dialog, voiceRunner: voice, personaLab: lab);
        var original = Create("baseline-device");
        var baseline = lab.ActiveRevisionId;
        var draft = lab.CreateRevision(new(baseline, "Published instructions", "Exact published actor instructions.", "gpt-live-1", "cedar"));
        lab.Publish(draft.Id, baseline);
        var published = Create("published-device");
        var oldCall = Admit("baseline-device", original);
        var newCall = Admit("published-device", published);
        Check(oldCall.Context.LivePrompt!.PersonaRevisionId is null, "publication does not mutate existing production conversation");
        Check(newCall.Context.LivePrompt!.PersonaRevisionId == draft.Id
            && newCall.Context.LivePrompt.Instructions == draft.Instructions, "new production conversation receives published exact instructions");
        lab.Publish(baseline, draft.Id, rollback: true);
        Check(newCall.Context.LivePrompt.Instructions == draft.Instructions, "rollback does not mutate in-flight provider context");
        var rolledBack = Create("rollback-device");
        var rollbackCall = Admit("rollback-device", rolledBack);
        Check(rollbackCall.Context.LivePrompt!.PersonaRevisionId is null, "new conversation after rollback uses source persona");
        foreach (var call in new[] { oldCall, newCall, rollbackCall }) { store.ReleaseRealtimeTurn(call, false, false); call.Cancellation.Dispose(); }

        (string Id, Guid Boot) Create(string device)
        {
            var boot = Guid.NewGuid();
            var created = store.Create(device, Guid.NewGuid(), "lab-publication-test"u8,
                new(device, boot, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false)));
            Check(created.Status == 201, "production conversation admitted without provider call");
            return (created.Response!.ConversationId, boot);
        }
        RealtimeCall Admit(string device, (string Id, Guid Boot) conversation)
        {
            var request = new RealtimeTurnRequest(Guid.NewGuid(), conversation.Boot, 0, 63, 0,
                PartyWandCatalog.Version, true, "", null, "marin", 1);
            var result = store.BeginRealtimeTurn(device, conversation.Id, Guid.NewGuid(), "lab-publish-admission"u8.ToArray(),
                request, true, default, default, handsFree: true, gptLive: true);
            return result.Call ?? throw new Exception("Publication admission failed: " + result.Code);
        }
    }
    private static async Task EndpointOwnershipAsync(string root)
    {
        using var rsa = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApplicationIdentity:Enabled"] = "true", ["ApplicationIdentity:DataRoot"] = Path.Combine(root, "http-accounts"),
            ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "http-keys"),
            ["ApplicationIdentity:RelyingPartyId"] = "localhost", ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost",
            ["ApplicationIdentity:RequestsPerMinute"] = "120"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        builder.Services.AddAccountIdentity(builder.Configuration);
        var store = new PersonaLabStore(Path.Combine(root, "http.sqlite"), Baseline());
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<GptLiveClient>();
        builder.Services.AddSingleton<OpenAiRealtimeClient>();
        builder.Services.AddSingleton<PersonaLabRuntime>();
        builder.Services.AddSingleton<PersonaLabSuites>();
        builder.Services.AddSingleton<PersonaLabImages>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<LabTicketRegistry>();
        await using var app = builder.Build();
        await AccountProvisioning.MigrateAsync(app.Services);
        await app.InitializeAccountsAsync();
        app.UseRateLimiter();
        app.UseWebSockets();
        app.UseAccountIdentity();
        app.MapGet("/app/v1/lab-test-csrf", (HttpContext http, IAntiforgery csrf) => Results.Json(new { token = csrf.GetAndStoreTokens(http).RequestToken }));
        app.MapPersonaLab();
        await app.StartAsync();
        var origin = new Uri(app.Urls.Single());
        app.Services.GetRequiredService<AccountConfiguration>().AllowedOrigins[0] = origin.GetLeftPart(UriPartial.Authority);
        using var anonymous = Client();
        Check((await anonymous.GetAsync("/app/v1/persona-lab/catalog")).StatusCode == HttpStatusCode.Unauthorized, "anonymous catalog denied by real Identity middleware");
        using var client = await AccountClientAsync("alice", AccountRoles.Viewer);
        Check((await client.GetAsync("/app/v1/persona-lab/catalog")).IsSuccessStatusCode, "Viewer can use lab without device grants");
        var first = store.CreateSession("bob", new(store.ActiveRevisionId, "gpt-live-1", "first-visit", "empty", "private"));
        Check((await client.GetAsync("/app/v1/persona-lab/sessions/" + first.Id)).StatusCode == HttpStatusCode.NotFound, "HTTP cross-account detail hidden");
        Check((await client.GetAsync("/app/v1/persona-lab/diagnostics")).StatusCode == HttpStatusCode.Forbidden, "global diagnostics admin-only");
        var sessionRequest = new LabSessionRequest(store.ActiveRevisionId, "gpt-live-1", "first-visit", "empty", "csrf");
        Check((await client.PostAsJsonAsync("/app/v1/persona-lab/sessions", sessionRequest)).StatusCode == HttpStatusCode.BadRequest,
            "real antiforgery rejects mutation without CSRF header");
        await CsrfAsync(client);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "https://attacker.test");
        Check((await client.PostAsJsonAsync("/app/v1/persona-lab/sessions", sessionRequest)).StatusCode == HttpStatusCode.Forbidden,
            "real account Origin guard rejects foreign origin");
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
        Check((await client.PostAsJsonAsync("/app/v1/persona-lab/sessions", sessionRequest)).IsSuccessStatusCode, "authenticated CSRF-protected session creation admitted");
        Check((await client.PostAsJsonAsync("/app/v1/persona-lab/revisions", new LabRevisionRequest(store.ActiveRevisionId,
            "unauthorized", "test", "gpt-live-1", "cedar"))).StatusCode == HttpStatusCode.Forbidden, "Viewer cannot publish configuration");
        var owned = store.CreateSession("alice", new(store.ActiveRevisionId, "gpt-live-1", "first-visit", "empty", "own"));
        Check((await client.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + owned.Id + "/turns", new { text = "Ahoj" })).StatusCode
            == HttpStatusCode.ServiceUnavailable, "unconfigured provider fails explicitly without fabricated reply");
        using var admin = await AccountClientAsync("admin", AccountRoles.Administrator);
        await CsrfAsync(admin);
        admin.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        var draftResponse = await admin.PostAsJsonAsync("/app/v1/persona-lab/revisions", new LabRevisionRequest(store.ActiveRevisionId,
            "http-admin", "Exact HTTP published instructions.", "gpt-live-1", "cedar"));
        Check(draftResponse.IsSuccessStatusCode, "real admin recent-session configuration draft admitted");
        var draft = (await draftResponse.Content.ReadFromJsonAsync<LabRevision>())!;
        Check((await admin.PostAsJsonAsync("/app/v1/persona-lab/revisions/" + draft.Id + "/publish", new { expectedActiveRevisionId = store.ActiveRevisionId })).IsSuccessStatusCode,
            "admin publication passes real CSRF, role and recent-session checks");
        var ticketResponse = await client.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + owned.Id + "/socket-ticket",
            new { socketOrigin = "capacitor://localhost" });
        Check(ticketResponse.IsSuccessStatusCode, "native socket ticket minted by authenticated protected mutation");
        var ticket = (await ticketResponse.Content.ReadFromJsonAsync<LabTicket>())!;
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.SetRequestHeader("Origin", "capacitor://localhost");
        await socket.ConnectAsync(new Uri(ticket.Url), CancellationToken.None);
        var bytes = new byte[4096];
        var frame = await socket.ReceiveAsync(bytes.AsMemory(), CancellationToken.None);
        using var message = JsonDocument.Parse(bytes.AsMemory(0, frame.Count));
        Check(message.RootElement.GetProperty("code").GetString() == "lab_provider_unavailable", "real WS ticket accepted then unavailable provider fails honestly");
        using var replay = new ClientWebSocket();
        replay.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        replay.Options.SetRequestHeader("Origin", "capacitor://localhost");
        try { await replay.ConnectAsync(new Uri(ticket.Url), CancellationToken.None); throw new Exception("Ticket replay accepted."); }
        catch (WebSocketException) { checks++; }
        var revokedResponse = await client.PostAsJsonAsync("/app/v1/persona-lab/sessions/" + owned.Id + "/socket-ticket",
            new { socketOrigin = "capacitor://localhost" });
        var revoked = (await revokedResponse.Content.ReadFromJsonAsync<LabTicket>())!;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var user = await db.Users.SingleAsync(x => x.Id == "alice");
            user.Disabled = true;
            await db.SaveChangesAsync();
        }
        using var disabled = new ClientWebSocket();
        disabled.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        disabled.Options.SetRequestHeader("Origin", "capacitor://localhost");
        try { await disabled.ConnectAsync(new Uri(revoked.Url), CancellationToken.None); throw new Exception("Disabled ticket accepted."); }
        catch (WebSocketException) { checks++; }
        await app.StopAsync();

        HttpClient Client(CookieContainer? cookies = null) => new(new HttpClientHandler
        {
            CookieContainer = cookies ?? new(), ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false
        }) { BaseAddress = origin };
        async Task<HttpClient> AccountClientAsync(string name, string role)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var user = new AccountUser { Id = name, UserName = name, DisplayName = name };
            Check((await users.CreateAsync(user)).Succeeded && (await users.AddToRoleAsync(user, role)).Succeeded, "real account fixture provisioned");
            var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AccountUser>>().CreateAsync(user);
            var sid = Guid.NewGuid().ToString("N");
            ((ClaimsIdentity)principal.Identity!).AddClaim(new(AccountIdentity.SessionClaim, sid));
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            db.Sessions.Add(new() { Id = sid, UserId = name, AuthenticatedAt = now, ExpiresAt = now + 3600 });
            await db.SaveChangesAsync();
            var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
            var value = options.TicketDataFormat.Protect(new AuthenticationTicket(principal,
                new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) }, IdentityConstants.ApplicationScheme));
            var cookies = new CookieContainer();
            cookies.Add(origin, new Cookie("__Host-dv-account", value, "/") { Secure = true, HttpOnly = true });
            var client = Client(cookies);
            client.DefaultRequestHeaders.Add("Origin", origin.GetLeftPart(UriPartial.Authority));
            return client;
        }
        async Task CsrfAsync(HttpClient target)
        {
            var result = (await target.GetFromJsonAsync<JsonElement>("/app/v1/lab-test-csrf")).GetProperty("token").GetString()!;
            target.DefaultRequestHeaders.Add("X-DV-CSRF", result);
            if (!target.DefaultRequestHeaders.Contains("Origin")) { target.DefaultRequestHeaders.Add("Origin", origin.GetLeftPart(UriPartial.Authority)); }
        }
    }
    private static void Check(bool condition, string message)
    { if (!condition) { throw new Exception("Persona Lab: " + message); } checks++; }
    private static void Reject(Action action, int status, string message)
    {
        try { action(); }
        catch (LabException error) when (error.Status == status) { checks++; return; }
        throw new Exception("Persona Lab: " + message);
    }
    private sealed class LabClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan elapsed) => now += elapsed;
    }
}

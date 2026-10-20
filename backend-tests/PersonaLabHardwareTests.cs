using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class PersonaLabHardwareTests
{
    private static int checks;
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "kufr-lab-hardware-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Exception? originalFailure = null;
        try
        {
            await EndpointShapesAsync(Path.Combine(root, "endpoint")).WaitAsync(TimeSpan.FromSeconds(25));
            await BrokerBoundariesAsync(Path.Combine(root, "broker")).WaitAsync(TimeSpan.FromSeconds(25));
            await ConversationIsolationAsync(Path.Combine(root, "isolation")).WaitAsync(TimeSpan.FromSeconds(25));
            await RevocationAndShutdownAsync(Path.Combine(root, "lifecycle")).WaitAsync(TimeSpan.FromSeconds(25));
            await VerificationFailureAsync(Path.Combine(root, "verification")).WaitAsync(TimeSpan.FromSeconds(25));
            await DurableOutcomeAsync(Path.Combine(root, "journal")).WaitAsync(TimeSpan.FromSeconds(15));
            await DisabledArchiveOutcomeAsync(Path.Combine(root, "disabled-archive")).WaitAsync(TimeSpan.FromSeconds(25));
            Console.WriteLine($"Persona Lab hardware: {checks} deterministic checks passed; no provider or physical calls.");
        }
        catch (Exception error) { originalFailure = error; throw; }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) when (originalFailure is not null) { }
        }
    }

    private static async Task EndpointShapesAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        using var http = fixture.Client();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Access.IssueToken("suitcase-a")!.AccessToken);
        var payload = new Dictionary<string, object?>
        {
            ["deviceId"] = "suitcase-a", ["bootId"] = fixture.Target.BootId, ["generation"] = 1,
            ["catalogVersion"] = PartyWandCatalog.Version, ["locale"] = "cs-CZ", ["consent"] = new { audio = true, image = false }
        };
        foreach (var invalid in new object?[] { null, "", 42, new string('a', 42), new string('!', 43), new string('a', 44) })
        {
            payload["testContext"] = invalid;
            http.DefaultRequestHeaders.Remove("Idempotency-Key");
            http.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
            using var response = await http.PostAsJsonAsync("/v1/conversations", payload);
            Check(response.StatusCode == HttpStatusCode.BadRequest, "present invalid testContext cannot choose production mode");
        }
        payload["testContext"] = new string('a', 43);
        http.DefaultRequestHeaders.Remove("Idempotency-Key");
        http.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using (var unknown = await http.PostAsJsonAsync("/v1/conversations", payload))
        { Check(unknown.StatusCode == HttpStatusCode.Unauthorized, "well-formed unknown ticket fails closed"); }
        Check(CountRows(fixture.Config["Backend:DatabasePath"]!, "conversation_creates") == 0
            && CountRows(fixture.Config["Backend:DatabasePath"]! + ".evening.sqlite", "show_visits") == 0,
            "invalid and unknown ticket requests leave journal and production evening untouched");
        payload.Remove("testContext");
        http.DefaultRequestHeaders.Remove("Idempotency-Key");
        http.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var normal = await http.PostAsJsonAsync("/v1/conversations", payload);
        Check(normal.StatusCode == HttpStatusCode.Created && CountRows(fixture.Config["Backend:DatabasePath"]!, "conversation_creates") == 1,
            "omitted carrier preserves normal production conversation creation");
    }

    private static async Task BrokerBoundariesAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        var viewer = await fixture.ActorAsync("viewer", AccountRoles.Viewer, "read");
        var denied = fixture.Session(viewer.UserId);
        await RejectAsync(() => fixture.Hardware.MintAsync(viewer.Http, denied.Id, fixture.Target), 403, "read-only viewer cannot arm hardware");
        var operatorWithoutGrant = await fixture.ActorAsync("no-grant", AccountRoles.Operator);
        await RejectAsync(() => fixture.Hardware.MintAsync(operatorWithoutGrant.Http,
            fixture.Session(operatorWithoutGrant.UserId).Id, fixture.Target), 403, "operator role alone is insufficient");
        var actor = await fixture.ActorAsync("operator", AccountRoles.Operator, "operate");
        var session = fixture.Session(actor.UserId);
        await RejectAsync(() => fixture.Hardware.MintAsync(actor.Http, session.Id,
            fixture.Target with { DeviceId = "suitcase-b" }), 403, "device grant is exact");
        var admin = await fixture.ActorAsync("admin", AccountRoles.Administrator);
        await RejectAsync(() => fixture.Hardware.MintAsync(admin.Http, session.Id, fixture.Target), 404, "admin cannot use another Lab owner's session");

        var ticket = await fixture.MintAsync(actor, session);
        Check(ticket.Token.Length == 43 && ticket.Token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "ticket is 256-bit base64url carrier");
        Check(ticket.ExpiresAt == fixture.Clock.GetUtcNow().AddSeconds(90), "unused ticket expiry is exactly 90 seconds");
        Check(fixture.Runtime.ActiveCount == 1, "hardware shares existing Lab admission");
        await RejectAsync(() => fixture.Runtime.TurnAsync(actor.UserId, session.Id, "Concurrent text", CancellationToken.None),
            409, "text cannot race an armed hardware session");
        var key = Guid.NewGuid();
        var request = fixture.CreateRequest(ticket.Token);
        await RejectAsync(() => fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, "same",
            request with { BootId = Guid.NewGuid() }, CancellationToken.None), 409, "ticket cannot bind another boot");
        var binding = await fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, "same", request, CancellationToken.None);
        Check(ReferenceEquals(binding, await fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, "same", request,
            CancellationToken.None)), "identical create redemption is idempotent");
        await RejectAsync(() => fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", Guid.NewGuid(), "same", request,
            CancellationToken.None), 409, "ticket cannot authorize another idempotency key");
        await RejectAsync(() => fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, "changed", request,
            CancellationToken.None), 409, "ticket cannot authorize a changed request body");
        fixture.Clock.Advance(TimeSpan.FromSeconds(91));
        await RejectAsync(() => fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, "same", request,
            CancellationToken.None), 409, "failed or incomplete create cannot redeem an expired ticket");
        await WaitAsync(() => fixture.Runtime.ActiveCount == 0);
        var expired = fixture.Store.Session(actor.UserId, session.Id);
        Check(expired.Hardware is { Status: "ended", CloseCode: "ticket_expired" }
            && expired.Events.Any(e => e.Type == "hardware_closed" && e.Code == "ticket_expired"), "ticket expiry has a safe observed closure cause");
        Check(!JsonSerializer.Serialize(expired).Contains(ticket.Token, StringComparison.Ordinal), "opaque ticket is never persisted in Lab metadata");
        Check(fixture.ProviderCalls == 0, "broker authorization never contacts a provider");
    }

    private static async Task ConversationIsolationAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        var actor = await fixture.ActorAsync("owner", AccountRoles.Operator, "operate");
        var session = fixture.Session(actor.UserId);
        var evening = fixture.Memory.GetStatus("suitcase-a").EveningId;
        fixture.Memory.Append("suitcase-a", evening, "seed", "production-conversation", "PRIVATE_GUEST_FACT", "PRIVATE_REPLY");
        var ticket = await fixture.MintAsync(actor, session);
        var request = fixture.CreateRequest(ticket.Token);
        var body = JsonSerializer.SerializeToUtf8Bytes(request);
        var key = Guid.NewGuid();
        var binding = await fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, Hash(body), request, CancellationToken.None);
        var created = fixture.Conversations.Create("suitcase-a", key, body, request, binding);
        Check(created.Status == 201 && created.Response is not null, "existing conversation owner admits explicit hardware context");
        var id = created.Response!.ConversationId;
        fixture.Hardware.Created(binding, id);
        Check(binding.ConversationId == id && created.Response.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(180), "binding is visible at admission and lifetime stays bounded");
        Check(fixture.Conversations.Create("suitcase-a", key, body, request, binding).Response == created.Response,
            "successful create replay preserves exact response");
        Check(fixture.Memory.ReadCapturedShowEvening("suitcase-a", id) is null, "new and replayed hardware creates never capture production evening");
        Check(fixture.Conversations.IsLabConversation("suitcase-a", id) && !fixture.Conversations.CanBindGuest("suitcase-a", id),
            "durable test provenance denies guest binding");

        var opening = fixture.Turn(opening: true);
        Check(fixture.Begin(id, opening with { DeviceRevision = fixture.Target.ExpectedRevision + 1 }).Code == "lab_hardware_configuration_mismatch",
            "first turn requires the exact admitted device revision");
        Check(fixture.Begin(id, opening, gptLive: true).Code == "lab_hardware_configuration_mismatch", "selected engine cannot be switched");
        var first = fixture.Begin(id, opening).Call!;
        Check(first.Context.Request.Voice == "marin" && first.Context.LivePrompt!.Instructions.Contains("FROZEN_LAB_PROMPT", StringComparison.Ordinal),
            "server derives frozen voice and persona instead of trusting device options");
        Check(first.EveningId is null && fixture.Conversations.LiveMemoryAccess(first) is null
            && first.Context.EveningContext == LabContract.Memory(session)
            && !first.Context.EveningContext.Contains("PRIVATE_GUEST_FACT", StringComparison.Ordinal), "both provider contexts contain only frozen synthetic memory");
        var prepared = await ConversationStartupMemory.PrepareAsync(first, fixture.Conversations, null!, first.Cancellation.Token);
        Check(prepared.Context.LiveInput.Count > 0 && prepared.Context.EveningContext == first.Context.EveningContext,
            "startup does not replace synthetic fixture with production memory");
        var simulated = await ConversationStartupMemory.ProcessInputAsync(first, fixture.Conversations, null!, "Zapamatuj si fiktivni udaj", first.Cancellation.Token);
        Check(simulated.Contains("Simulated", StringComparison.Ordinal), "remember is an explicit simulated write without production memory service");
        binding.ProviderReady("sess_hardware_fixture");
        binding.ProviderReady("sess_hardware_fixture");
        Check(fixture.Store.Session(actor.UserId, session.Id).Events.Count(e => e.Type == "connection_established") == 1,
            "verified provider metadata is deduplicated per connection");
        Finish(first, "ask", null, 3);
        long totalCost = 3;
        var final = fixture.Begin(id, fixture.Turn(opening: false)).Call!;
        for (var question = 0; question < 8 && final.Context.RequireQuestion; question++)
        {
            Finish(final, "ask", null, 5);
            totalCost += 5;
            final = fixture.Begin(id, fixture.Turn(opening: false)).Call!;
        }
        Check(!final.Context.RequireQuestion, "fixture honors the selected production personality's question gate");
        var completed = Finish(final, "propose", 1, 7);
        totalCost += 7;
        var proposal = (SelectionProposal)completed.Proposal!;
        Check(fixture.Journal.ReadKeepsakeSelection("suitcase-a", final.OperationId) is null,
            "hardware proposal never enters DV10 selection outbox");
        Check(fixture.Journal.ReadLabSelection("suitcase-a", final.OperationId)!.Value.Proposal!.Reason == "Persona Lab hardware test.",
            "technical proposal reason never archives assistant or visitor words");
        var persistent = fixture.Store.Session(actor.UserId, session.Id);
        Check(persistent.Turns.All(turn => turn.Text.Length == 0)
            && fixture.Runtime.Detail(actor.UserId, session.Id).Turns.Any(turn => turn.Text == "PRIVATE_TEST_REPLY"),
            "opt-out content is volatile while technical turn metadata persists");
        var summary = LabAnalytics.Reliability([persistent]);
        Check(summary.SuccessfulSessions == 1 && summary.EstimatedCostUsd == totalCost / 1_000_000m, "hardware completion and incremental cost feed existing analytics once");
        Check(fixture.Memory.GetStatus("suitcase-a").ExchangeCount == 1 && fixture.Memory.GuestPermission("suitcase-a", id) is null,
            "all completed test turns preserve production exchanges and guest identity");
        var image = new CostumeImageUpload(Guid.NewGuid(), fixture.Target.BootId, 1, 7, PartyWandCatalog.Version, []);
        Check(fixture.Conversations.SubmitImage("suitcase-a", id, Guid.NewGuid(), image).Status == 403
            && fixture.Conversations.CorrectCostume("suitcase-a", id, Guid.NewGuid(), image, "costume").Status == 403,
            "production image and costume APIs hard-deny test provenance before content processing");
        using var http = fixture.Client();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        using var deniedGuest = await http.PutAsJsonAsync("/v1/admin/memory/guest", new
        {
            deviceId = "suitcase-a", conversationId = id, guestId = "guest", identitySource = "verified-test",
            identityVerified = true, longTermAllowed = false, cloudExportAllowed = false, expectedVersion = 0
        });
        Check(deniedGuest.StatusCode == HttpStatusCode.Forbidden && fixture.Conversations.IsRealtimeTurnCurrent(final) == false
            && fixture.Memory.GuestPermission("suitcase-a", id) is null, "false-permission guest request is rejected before invalidation or binding");
        Check(fixture.Begin(id, fixture.Turn(opening: false)).Code == "conversation_finished", "denied guest request does not invalidate the live owner entry");
        Check(fixture.ProviderCalls == 0, "store and endpoint isolation tests make no provider calls");

        TurnResult Finish(RealtimeCall call, string decision, int? compartment, long cost)
        {
            Check(fixture.Conversations.StartRealtimeTurn(call), "test turn follows real journal lifecycle");
            var result = fixture.Conversations.CompleteRealtimeTurn(call,
                new("PRIVATE_TEST_VISITOR", "PRIVATE_TEST_REPLY", decision, "PRIVATE_TEST_REASON", compartment, cost));
            Check(result is not null, "validated fake provider result completes through production owner");
            fixture.Conversations.ReleaseRealtimeTurn(call, dispatched: true, completed: true);
            return result!;
        }
    }

    private static async Task RevocationAndShutdownAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        var actor = await fixture.ActorAsync("operator", AccountRoles.Operator, "operate");
        var session = fixture.Session(actor.UserId);
        var ticket = await fixture.MintAsync(actor, session);
        var request = fixture.CreateRequest(ticket.Token);
        var key = Guid.NewGuid();
        var body = JsonSerializer.SerializeToUtf8Bytes(request);
        var context = await fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", key, Hash(body), request, CancellationToken.None);
        var created = fixture.Conversations.Create("suitcase-a", key, body, request, context).Response!;
        // Cancel precisely between the owner admission and broker metadata update.
        Check(context.ConversationId == created.ConversationId, "conversation identity is atomically bound before entry publication");
        context.Lease.Cancel();
        Reject(() => fixture.Hardware.Created(context, created.ConversationId), 409, "canceled post-admission metadata cannot revive a context");
        await WaitAsync(() => fixture.Runtime.ActiveCount == 0);
        var next = fixture.Session(actor.UserId);
        var nextTicket = await fixture.MintAsync(actor, next);
        var nextRequest = fixture.CreateRequest(nextTicket.Token);
        var nextKey = Guid.NewGuid();
        var nextBody = JsonSerializer.SerializeToUtf8Bytes(nextRequest);
        var nextContext = await fixture.Hardware.BindAsync(nextTicket.Token, "suitcase-a", nextKey, Hash(nextBody), nextRequest, CancellationToken.None);
        var nextCreated = fixture.Conversations.Create("suitcase-a", nextKey, nextBody, nextRequest, nextContext);
        Check(nextCreated.Status == 201, "cancellation boundary leaves no orphan that blocks the device");
        fixture.Hardware.Created(nextContext, nextCreated.Response!.ConversationId);
        var call = fixture.Begin(nextCreated.Response.ConversationId, fixture.Turn(opening: true)).Call!;
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.ResourceGrants.Where(grant => grant.UserId == actor.UserId).ExecuteDeleteAsync();
        }
        var recorder = new RecordingInput();
        using var lifetime = new CancellationTokenSource();
        using var input = new RealtimeLiveSession("suitcase-a", nextCreated.Response.ConversationId, recorder, lifetime, labHardware: nextContext);
        input.Listen(call);
        await CanceledAsync(() => input.UploadAsync(call.Context.Request.ClientTurnId, 0, new byte[2], CancellationToken.None),
            "persistent PCM verifies current grant before provider send");
        Check(recorder.Sends == 0 && call.Cancellation.IsCancellationRequested, "revocation never forwards unapproved PCM");
        await WaitAsync(() => fixture.Runtime.ActiveCount == 0);
        var revoked = fixture.Store.Session(actor.UserId, next.Id);
        Check(revoked.Hardware is { Status: "failed", CloseCode: "authority_revoked" }
            && LabAnalytics.Reliability([revoked]).FailedSessions == 1, "authority closure preserves cause and existing failure analytics");
        fixture.Conversations.ReleaseRealtimeTurn(call, false, false);

        var admin = await fixture.ActorAsync("admin", AccountRoles.Administrator);
        var ended = fixture.Session(admin.UserId);
        await fixture.MintAsync(admin, ended);
        fixture.Runtime.End(admin.UserId, ended.Id);
        await WaitAsync(() => fixture.Runtime.ActiveCount == 0);
        var endedResult = fixture.Store.Session(admin.UserId, ended.Id);
        Check(endedResult.Hardware is { Status: "ended", CloseCode: "user_ended" }
            && !endedResult.Events.Any(e => e.Type == "provider_failure"), "explicit user end closes without a false provider failure");

        var guarded = fixture.Session(admin.UserId);
        using (var lease = fixture.Runtime.ReserveHardware(admin.UserId, guarded.Id))
        {
            var authorized = 1;
            var publicationContext = new LabHardwareContext(lease, fixture.Target, "test-session", "test-stamp", fixture.Runtime,
                _ => Task.FromResult(Volatile.Read(ref authorized) == 1));
            using var publishLifetime = new CancellationTokenSource();
            using var publishSession = new RealtimeLiveSession("suitcase-a", "test-conversation", new RecordingInput(), publishLifetime,
                labHardware: publicationContext);
            using var writer = new SemaphoreSlim(0, 1);
            var commits = 0;
            var publication = VisualResponsePublisher.PublishAsync(fixture.Conversations, publishSession, writer, 0, 0,
                () => { commits++; return new { type = "completed" }; }, (_, _) => Task.CompletedTask, CancellationToken.None);
            Volatile.Write(ref authorized, 0);
            writer.Release();
            await CanceledAsync(() => publication, "final publication rechecks authority after waiting for the writer");
            Check(commits == 0 && publicationContext.CloseCode == "authority_revoked", "revoked final proposal never enters owner commit");
        }
        var stopped = fixture.Session(admin.UserId);
        await fixture.MintAsync(admin, stopped);
        await fixture.Hardware.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(12));
        Check(fixture.Runtime.ActiveCount == 0 && fixture.Store.Session(admin.UserId, stopped.Id).Hardware is
            { Status: "ended", CloseCode: "backend_stopping" }, "host shutdown drains workers and releases admission with known cause");
        Check(fixture.ProviderCalls == 0, "revocation and shutdown make no provider calls");
    }

    private static async Task VerificationFailureAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root);
        var actor = await fixture.ActorAsync("operator", AccountRoles.Operator, "operate");
        var session = fixture.Session(actor.UserId);
        var ticket = await fixture.MintAsync(actor, session);
        var request = fixture.CreateRequest(ticket.Token);
        var context = await fixture.Hardware.BindAsync(ticket.Token, "suitcase-a", Guid.NewGuid(), "same", request, CancellationToken.None);
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AspNetUserRoles");
        }
        await CanceledAsync(() => context.EnsureAuthorizedAsync(CancellationToken.None), "failed persistent authority lookup cancels fail-closed");
        await WaitAsync(() => fixture.Runtime.ActiveCount == 0);
        var result = fixture.Store.Session(actor.UserId, session.Id);
        Check(result.Hardware is { Status: "failed", CloseCode: "authority_check_failed" }
            && result.Events.Any(e => e.Type == "provider_failure" && e.Code == "authority_check_failed"),
            "verification failure has a classified closure event without exception data");
    }

    private static async Task DurableOutcomeAsync(string root)
    {
        Directory.CreateDirectory(root);
        var config = Configuration(root);
        var clock = new MutableClock();
        string operation;
        SelectionProposal proposal;
        using (var journal = new OperationJournal(config, clock))
        {
            var created = journal.ReserveCreate("suitcase-a", Guid.NewGuid(), "create", "lab-session", "lab-revision",
                clock.GetUtcNow().AddMinutes(3), "owner").Response!;
            var correlation = new TurnCorrelation("suitcase-a", Guid.NewGuid(), created.ConversationId, Guid.NewGuid(), 1, 7);
            operation = journal.Reserve("suitcase-a", "route", Guid.NewGuid(), "body", correlation).OperationId;
            proposal = new(Guid.NewGuid().ToString("D"), created.ConversationId, correlation.ClientTurnId,
                PartyWandCatalog.Version, 7, 2, "PRIVATE_TECHNICAL_REASON", correlation.BootId, 1, clock.GetUtcNow().AddMinutes(1));
            Check(journal.MarkSelectionCompleted(operation, proposal), "technical test selection completes");
        }
        clock.Advance(TimeSpan.FromDays(2));
        using var restarted = new OperationJournal(config, clock);
        Check(restarted.IsLabConversation("suitcase-a", proposal.ConversationId)
            && restarted.ReadLabSelection("suitcase-a", operation)!.Value.Proposal!.Reason == "Persona Lab hardware test.",
            "test provenance and sanitized proposal survive owner restart and lifetime expiry");
        var deviceAccess = new DeviceAccessStore(config, clock);
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var outcome = new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"), proposal.ProposalId,
            Guid.NewGuid().ToString("D"), proposal.BootId.ToString("D"), 1, 7, "completed_by_timing");
        var first = await PublishAsync(outcome);
        var replay = await PublishAsync(outcome);
        Check(first.Status == 201 && replay.Status == 201 && !first.Body.Contains("locator", StringComparison.Ordinal)
            && !first.Body.Contains("url", StringComparison.Ordinal), "late test outcome ACK is idempotent and never creates archive or QR locator");
        Check((await PublishAsync(outcome with { PhysicalOperationId = Guid.NewGuid().ToString("D") })).Status == 409,
            "changed technical outcome retry conflicts");
        Check((await PublishAsync(outcome with { BootId = Guid.NewGuid().ToString("D") })).Status == 409,
            "late outcome must preserve proposal boot correlation");
        Check(restarted.ReadKeepsakeSelection("suitcase-a", operation) is null, "restart does not turn test evidence into DV10 eligibility");

        async Task<(int Status, string Body)> PublishAsync(KeepsakeOutcomeEndpoints.OutcomeRequest value)
        {
            var http = new DefaultHttpContext { RequestServices = services };
            http.Request.ContentType = "application/json";
            http.Request.Headers.Authorization = "Bearer " + deviceAccess.IssueToken("suitcase-a")!.AccessToken;
            http.Request.Headers["Idempotency-Key"] = Guid.NewGuid().ToString("D");
            http.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            http.Response.Body = new MemoryStream();
            var result = await KeepsakeOutcomeEndpoints.PublishAsync(http, operation, deviceAccess, restarted, config, null!, null!);
            await result.ExecuteAsync(http);
            http.Response.Body.Position = 0;
            return (http.Response.StatusCode, await new StreamReader(http.Response.Body).ReadToEndAsync());
        }
    }

    private static async Task DisabledArchiveOutcomeAsync(string root)
    {
        await using var fixture = await Fixture.CreateAsync(root, keepsakesEnabled: false);
        Check(fixture.Config["Keepsake:DataRoot"] is null && fixture.App.Services.GetService<KeepsakeStore>() is null,
            "disabled archive fixture has neither an archive root nor a KeepsakeStore service");
        var created = fixture.Journal.ReserveCreate("suitcase-a", Guid.NewGuid(), "lab-disabled-create", "test-lab",
            "test-revision", DateTimeOffset.UtcNow.AddMinutes(3), "test-owner").Response!;
        var correlation = new TurnCorrelation("suitcase-a", fixture.Target.BootId, created.ConversationId, Guid.NewGuid(), 1, 7);
        var operation = fixture.Journal.Reserve("suitcase-a", "test-route", Guid.NewGuid(), "test-body", correlation).OperationId;
        var proposal = new SelectionProposal(Guid.NewGuid().ToString("D"), created.ConversationId, correlation.ClientTurnId,
            PartyWandCatalog.Version, 7, 2, "PRIVATE_TEST_REASON", correlation.BootId, 1, DateTimeOffset.UtcNow.AddMinutes(1));
        Check(fixture.Journal.MarkSelectionCompleted(operation, proposal), "disabled archive still records technical Lab proposal provenance");
        var outcome = new KeepsakeOutcomeEndpoints.OutcomeRequest(1, Guid.NewGuid().ToString("D"), proposal.ProposalId,
            Guid.NewGuid().ToString("D"), proposal.BootId.ToString("D"), 1, 7, "completed_by_timing");
        using var http = fixture.Client();
        var route = "/v1/keepsakes/outcomes/" + operation;
        using (var unauthorized = await http.PostAsJsonAsync(route, outcome))
        { Check(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "unconditional technical route still requires device authority"); }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Access.IssueToken("suitcase-a")!.AccessToken);
        http.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("D"));
        using var first = await http.PostAsJsonAsync(route, outcome);
        using var replay = await http.PostAsJsonAsync(route, outcome);
        var body = await first.Content.ReadAsStringAsync();
        Check(first.StatusCode == HttpStatusCode.Created && replay.StatusCode == HttpStatusCode.Created
            && !body.Contains("locator", StringComparison.Ordinal) && !body.Contains("url", StringComparison.Ordinal),
            "actual mapped route acknowledges and replays a Lab outcome with DV10 disabled without a locator");
        using var ordinary = await http.PostAsJsonAsync("/v1/keepsakes/outcomes/" + Guid.NewGuid().ToString("D"), outcome);
        Check(ordinary.StatusCode == HttpStatusCode.NotFound, "ordinary production outcomes remain unavailable when archive is disabled");
        Check(Directory.GetDirectories(root).Length == 0 && fixture.App.Services.GetService<KeepsakeStore>() is null,
            "technical ACK creates no archive or data-protection directories and never resolves the archive store");
    }

    private static IConfigurationRoot Configuration(string root) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = Path.Combine(root, "operations.sqlite"), ["Keepsake:Enabled"] = "true",
        ["Backend:Admin:CredentialSha256"] = Hash(Encoding.UTF8.GetBytes("test-admin")),
        ["Devices:0:DeviceId"] = "suitcase-a", ["Devices:0:CredentialSha256"] = Hash(Encoding.UTF8.GetBytes("test-device-a")),
        ["Devices:1:DeviceId"] = "suitcase-b", ["Devices:1:CredentialSha256"] = Hash(Encoding.UTF8.GetBytes("test-device-b"))
    }).Build();

    private sealed class Fixture : IAsyncDisposable
    {
        internal required WebApplication App { get; init; }
        internal required IConfigurationRoot Config { get; init; }
        internal required OperationJournal Journal { get; init; }
        internal required EveningMemoryStore Memory { get; init; }
        internal required ConversationStore Conversations { get; init; }
        internal required PersonaLabStore Store { get; init; }
        internal required PersonaLabRuntime Runtime { get; init; }
        internal required PersonaLabHardware Hardware { get; init; }
        internal required MutableClock Clock { get; init; }
        internal LabHardwareRequest Target { get; } = new("suitcase-a", Guid.NewGuid(), 7);
        internal int ProviderCalls => readProviderCalls();
        internal required DeviceAccessStore Access { get; init; }
        private readonly List<AsyncServiceScope> actors = [];
        private string revision = "";

        internal static async Task<Fixture> CreateAsync(string root, bool keepsakesEnabled = true)
        {
            Directory.CreateDirectory(root);
            var config = Configuration(root);
            config["Keepsake:Enabled"] = keepsakesEnabled ? "true" : "false";
            var clock = new MutableClock();
            var journal = new OperationJournal(config, clock);
            WebApplication? app = null;
            PersonaLabHardware? hardware = null;
            PersonaLabRuntime? runtime = null;
            OpenAiRealtimeClient? realtime = null;
            GptLiveClient? live = null;
            OpenAiVectorMemoryProvider? vector = null;
            try
            {
                var memory = new EveningMemoryStore(config, journal);
                var store = new PersonaLabStore(Path.Combine(root, "lab.sqlite"), LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new()));
                var conversations = new ConversationStore(clock, journal, eveningMemory: memory, personaLab: store);
                var calls = 0;
                realtime = new OpenAiRealtimeClient(_ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("Provider calls are forbidden in deterministic hardware tests."); });
                live = new GptLiveClient(new ConfigurationBuilder().Build());
                runtime = new PersonaLabRuntime(store, live, realtime);
                var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Services.AddDbContext<AccountDatabase>(options => options.UseSqlite("Data Source=" + Path.Combine(root, "accounts.sqlite")));
                builder.Services.AddIdentityCore<AccountUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<AccountDatabase>();
                builder.Services.AddSingleton<IConfiguration>(config);
                builder.Services.AddSingleton(conversations);
                builder.Services.AddSingleton(journal);
                builder.Services.AddSingleton(store);
                builder.Services.AddSingleton(runtime);
                builder.Services.AddSingleton<TimeProvider>(clock);
                builder.Services.AddSingleton<PersonaLabHardware>();
                var access = new DeviceAccessStore(config, clock);
                builder.Services.AddSingleton(access);
                builder.Services.AddSingleton(memory);
                builder.Services.AddSingleton(new ServerAdminAccess(config));
                var memoryOptions = HybridMemoryOptions.Read(config, journal);
                vector = new OpenAiVectorMemoryProvider(new HttpClient(new RejectHttp()), null, memoryOptions, ownsHttp: true);
                builder.Services.AddSingleton(new HybridMemoryService(memory, vector, memoryOptions, TimeProvider.System));
                app = builder.Build();
                app.Urls.Clear();
                app.Urls.Add("http://127.0.0.1:0");
                await using (var scope = app.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                    await db.Database.EnsureCreatedAsync();
                    foreach (var role in new[] { AccountRoles.Operator, AccountRoles.Administrator, AccountRoles.Viewer })
                    { db.Roles.Add(new IdentityRole(role) { Id = role, NormalizedName = role.ToUpperInvariant() }); }
                    await db.SaveChangesAsync();
                }
                app.MapMemoryAdministration();
                app.MapConversations();
                app.MapKeepsakeOutcomes();
                await app.StartAsync();
                hardware = app.Services.GetRequiredService<PersonaLabHardware>();
                await hardware.StartAsync(CancellationToken.None);
                var fixture = new Fixture { App = app, Config = config, Journal = journal, Memory = memory,
                    Conversations = conversations, Store = store, Runtime = runtime, Hardware = hardware, Clock = clock, Access = access };
                fixture.revision = store.CreateRevision(new(store.ActiveRevisionId, "Hardware fixture", "FROZEN_LAB_PROMPT", RealtimeProtocol.Model, "marin")).Id;
                fixture.readProviderCalls = () => calls;
                fixture.disposeProviders = () => { vector.Dispose(); live.Dispose(); realtime.Dispose(); };
                return fixture;
            }
            catch
            {
                // Setup failures must not leak the journal's exclusive lock or
                // replace the useful exception with a directory-delete error.
                try { if (hardware is not null) { await hardware.StopAsync(CancellationToken.None); } } catch (Exception) { }
                try { hardware?.Dispose(); runtime?.Dispose(); } catch (Exception) { }
                try { if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); } } catch (Exception) { }
                try { vector?.Dispose(); live?.Dispose(); realtime?.Dispose(); } catch (Exception) { }
                journal.Dispose();
                throw;
            }
        }
        private Func<int> readProviderCalls = () => 0;
        private Action disposeProviders = () => { };
        internal LabSession Session(string owner) => Store.CreateSession(owner,
            new(revision, RealtimeProtocol.Model, "returning-visitor", "returning", "Hardware test"));
        internal CreateConversationRequest CreateRequest(string token) => new("suitcase-a", Target.BootId, 1, PartyWandCatalog.Version,
            "cs-CZ", new(true, false)) { TestContext = token };
        internal RealtimeTurnRequest Turn(bool opening) => new(Guid.NewGuid(), Target.BootId, 1, 63, 7,
            PartyWandCatalog.Version, opening, "", null, "cedar", 1);
        internal RealtimeAdmission Begin(string id, RealtimeTurnRequest request, bool gptLive = false) => Conversations.BeginRealtimeTurn(
            "suitcase-a", id, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(request), request, true,
            CancellationToken.None, CancellationToken.None, handsFree: true, gptLive: gptLive);
        internal async Task<(string Token, DateTimeOffset ExpiresAt)> MintAsync(Actor actor, LabSession session)
        {
            var value = JsonSerializer.SerializeToElement(await Hardware.MintAsync(actor.Http, session.Id, Target));
            return (value.GetProperty("testContext").GetString()!, value.GetProperty("expiresAt").GetDateTimeOffset());
        }
        internal HttpClient Client() => new() { BaseAddress = new Uri(App.Urls.Single()) };
        internal async Task<Actor> ActorAsync(string id, string role, string? permission = null)
        {
            var scope = App.Services.CreateAsyncScope();
            actors.Add(scope);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
            var user = new AccountUser { Id = id, UserName = id, DisplayName = id };
            Check((await users.CreateAsync(user)).Succeeded && (await users.AddToRoleAsync(user, role)).Succeeded, "real account fixture created");
            var sid = Guid.NewGuid().ToString("N");
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            db.Sessions.Add(new() { Id = sid, UserId = id, ExpiresAt = Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds() });
            if (permission is not null) { db.ResourceGrants.Add(new() { Id = Guid.NewGuid().ToString("N"), UserId = id,
                ResourceType = "device", ResourceId = "suitcase-a", Permission = permission }); }
            await db.SaveChangesAsync();
            var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id),
                new Claim(AccountIdentity.SessionClaim, sid), new Claim(users.Options.ClaimsIdentity.SecurityStampClaimType, user.SecurityStamp!) }, "test"));
            return new(id, new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = principal });
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Hardware.StopAsync(CancellationToken.None);
                Hardware.Dispose();
                Runtime.Dispose();
                foreach (var scope in actors) { await scope.DisposeAsync(); }
                await App.StopAsync();
            }
            finally
            {
                try { await App.DisposeAsync(); }
                finally
                {
                    try { disposeProviders(); }
                    finally { Journal.Dispose(); }
                }
            }
            Check(ProviderCalls == 0, "fixture teardown confirms no provider invocation");
        }
    }

    private sealed record Actor(string UserId, DefaultHttpContext Http);
    private sealed class MutableClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }
    private sealed class RecordingInput : IStreamingAudioInput
    {
        internal int Sends;
        public Task SendAudioAsync(byte[] pcm, CancellationToken cancellationToken) { Sends++; return Task.CompletedTask; }
    }
    private sealed class RejectHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("External requests are forbidden in deterministic hardware tests.");
    }
    private static long CountRows(string path, string table)
    {
        using var connection = new SqliteConnection("Data Source=" + path);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM " + table;
        return (long)command.ExecuteScalar()!;
    }
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Hardware Lab regression: " + message); } checks++; }
    private static void Reject(Action action, int status, string message)
    { try { action(); } catch (LabException error) when (error.Status == status) { Check(true, message); return; } throw new InvalidOperationException(message); }
    private static async Task RejectAsync(Func<Task> action, int status, string message)
    { try { await action(); } catch (LabException error) when (error.Status == status) { Check(true, message); return; } throw new InvalidOperationException(message); }
    private static async Task CanceledAsync(Func<Task> action, string message)
    { try { await action(); } catch (OperationCanceledException) { Check(true, message); return; } throw new InvalidOperationException(message); }
    private static async Task WaitAsync(Func<bool> predicate)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate()) { if (timer.Elapsed > TimeSpan.FromSeconds(5)) { throw new TimeoutException("Hardware lifecycle did not drain."); } await Task.Delay(20); }
    }
}

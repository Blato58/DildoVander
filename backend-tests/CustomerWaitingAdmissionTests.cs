using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class CustomerWaitingAdmissionTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-waiting-admission-" + Id());
        Directory.CreateDirectory(root);
        try
        {
            LegacyGoldenContracts(Configuration(root, "golden"));
            await TwoPhoneAdmissionAsync(Configuration(root, "race"));
            await ClaimVersusDirectAsync(Configuration(root, "claim-direct-race"));
            DurableBindingRestart(Configuration(root, "binding-restart"));
            DirectCreateFence(Configuration(root, "direct"));
            StaleTickets(Configuration(root, "stale"));
            LegacyActivation(Configuration(root, "activation"));
            TerminalSettlement(Configuration(root, "terminal"));
            FreshBootSettlement(Configuration(root, "boot"));
            AcceptedProfileRevocation(Configuration(root, "accepted-profile"));
            CommitAuthority(Configuration(root, "commit-authority"));
            RevocationAndDeletion(Configuration(root, "deletion"));
            Console.WriteLine("PASS waiting admission: golden legacy DTO/hash identity, actual SQLite two-phone ticket/redemption races, claim binding, direct Create fence, stale/pause/closed/reclaimed rejection, accepted replay, legacy activation fence, terminal settlement, suspend/revoke/delete isolation");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static void LegacyGoldenContracts(IConfiguration configuration)
    {
        const string boot = "11111111-1111-4111-8111-111111111111";
        const string operation = "22222222-2222-4222-8222-222222222222";
        const string challenge = "33333333-3333-4333-8333-333333333333";
        const string profile = "44444444-4444-4444-8444-444444444444";
        const string ticketJson = """{"ProtocolVersion":1,"OperationId":"22222222-2222-4222-8222-222222222222","DeviceId":"device","BootId":"11111111-1111-4111-8111-111111111111","Challenge":"33333333-3333-4333-8333-333333333333","ProfileId":"44444444-4444-4444-8444-444444444444","ProfileRevision":7,"PersonalMemoryConsent":false,"OpenAiConsent":true}""";
        const string ticketFingerprint = "61C346901FC3D16955A4744ECA373288BF185B25CF77CE0F2D92EE12BD648138";
        const string createJson = """{"DeviceId":"device","BootId":"11111111-1111-4111-8111-111111111111","Generation":0,"CatalogVersion":"catalog-v1","Locale":"cs-CZ","Consent":{"Audio":true,"Image":false},"TestContext":null}""";
        const string createFingerprint = "E5475D78B1AAB9EF5CE5021FB1D91AD5AE068767D2EED4699F6443BA5419B01A";
        var request = new CustomerTicketRequest(1, operation, "device", boot, challenge, profile, 7, OpenAiConsent: true);
        var create = new CreateConversationRequest("device", Guid.Parse(boot), 0, "catalog-v1", "cs-CZ", new(true, false));
        Check(JsonSerializer.Serialize(request) == ticketJson && OperationJournal.CustomerHash(ticketJson) == ticketFingerprint,
            "legacy ticket DTO bytes and fingerprint remain the static pre-change fixture");
        Check(JsonSerializer.Serialize(create) == createJson && OperationJournal.CustomerHash(createJson) == createFingerprint,
            "legacy Create DTO bytes and raw-body hash remain the static pre-change fixture");
        using var journal = new OperationJournal(configuration, new AdmissionClock());
        journal.SetCustomerMode("device", true);
        journal.RegisterCustomerChallenge("device", new(1, boot, challenge, 0, "catalog-v1", null));
        Expect("start_ticket_unavailable", () => journal.ReplayCustomerTicket("owner", request));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_start_tickets") == 0,
            "an obsolete receipt observation cannot fall through to issuing an empty new ticket");
        journal.IssueCustomerTicket("owner", request, "snapshot", OperationJournal.CustomerHash("golden-token"), "protected");
        Check(Text(configuration, "SELECT fingerprint FROM customer_start_tickets") == ticketFingerprint,
            "actual journal persists the unchanged ticket fingerprint");
        var session = journal.RedeemCustomerTicket("device", new(1, "golden-token", operation, boot, challenge));
        var reservation = journal.ReserveCreate("device", Guid.Parse(operation), createFingerprint, customerSessionId: session.Id, bootId: boot);
        Check(reservation.Response is not null && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_ticket_bindings") == 0,
            "never-configured waiting preserves legacy ticket redemption and Create admission");
    }

    private static async Task TwoPhoneAdmissionAsync(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration);
        var claimed = fixture.Claim("owner", "profile");
        var request = fixture.Request(claimed.Entry!.Claim!.Id, "profile");
        Expect("waiting_claim_required", () => fixture.Issue("stranger", request));
        Expect("waiting_claim_required", () => fixture.Issue("owner", request with { ProfileId = "other-profile" }));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => fixture.Issue("owner", request))));
        var storedProtected = attempts[0].Record.ProtectedToken;
        var minted = attempts.Single(attempt => attempt.Record.ProtectedToken == "protected:" + attempt.Token);
        Check(attempts.All(attempt => attempt.Record.ProtectedToken == storedProtected), "two phones share the first accepted immutable ticket");
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_start_tickets") == 1
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_ticket_bindings") == 1,
            "ticket row and exact claim binding commit once together");
        Check(Text(configuration, "SELECT claim_id FROM customer_waiting_ticket_bindings") == request.OperationId,
            "the existing operation id is exactly the explicit claim id");
        var sessions = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => fixture.Redeem(minted))));
        Check(sessions.All(session => session.Id == sessions[0].Id)
            && Scalar(configuration, "SELECT COUNT(*) FROM device_conversation_leases") == 1
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_sessions") == 1,
            "concurrent redemption consumes one claim and owns exactly one session/lease");
        Check(fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!.State == "accepted", "redemption atomically consumes claim");
        fixture.ChangeState("paused"); fixture.Clock.Advance(180);
        Check(fixture.Redeem(minted).Id == sessions[0].Id, "accepted redemption replays after queue pause and all expiries");
        Check(fixture.Journal.IssueCustomerTicket("owner", request, "different capture", "ignored-hash", "ignored-protected").SessionId == sessions[0].Id,
            "accepted ticket replay ignores later queue pause and does not generate another ticket");
        var create = fixture.Journal.ReserveCreate("device", Guid.Parse(request.OperationId), "same-legacy-body-hash",
            customerSessionId: sessions[0].Id, bootId: fixture.Boot);
        Check(create.Response is not null && !create.Conflict, "accepted customer's original Create remains admissible after pause");
        Check(fixture.Journal.ReserveCreate("device", Guid.Parse(request.OperationId), "same-legacy-body-hash",
            customerSessionId: sessions[0].Id, bootId: fixture.Boot).Existing, "accepted Create is still idempotent");
    }

    private static void DirectCreateFence(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration, waiting: false);
        var oldKey = Guid.NewGuid();
        var old = fixture.Journal.ReserveCreate("device", oldKey, "old-hash");
        fixture.Journal.ReleaseRemovedNoncustomerConversation("device", old.Response!.ConversationId);
        fixture.Open(); fixture.Claim("owner", "profile");
        Check(fixture.Journal.ReserveCreate("device", oldKey, "old-hash").Existing, "existing direct Create replay precedes the pending-claim fence");
        var blocked = fixture.Journal.ReserveCreate("device", Guid.NewGuid(), "fresh-hash");
        Check(blocked.Conflict && blocked.Response is null && Scalar(configuration, "SELECT COUNT(*) FROM device_conversation_leases") == 0,
            "unrelated direct/operator start returns ordinary conflict without acquiring a lease");
        fixture.Clock.Advance(90);
        Check(fixture.Journal.ReserveCreate("device", Guid.NewGuid(), "after-expiry").Response is not null,
            "expired claim no longer reserves the device from an explicit direct start");
    }

    private static async Task ClaimVersusDirectAsync(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new AdmissionClock());
        for (var iteration = 0; iteration < 12; iteration++)
        {
            var device = "race-device-" + iteration;
            var waitingEvent = journal.OpenCustomerWaiting(device, Id(), Id(), activationRevision: 1);
            var entry = journal.JoinCustomerWaiting("owner", device, new(Id(), waitingEvent.Id, "profile")).Entry!;
            using var start = new ManualResetEventSlim();
            var claim = Task.Run(() =>
            {
                start.Wait();
                try { return journal.ClaimCustomerWaiting("owner", device, Command(waitingEvent.Id, entry)).Entry!.Claim is not null; }
                catch (CustomerSessionException error) when (error.Code == "device_busy") { return false; }
            });
            var direct = Task.Run(() =>
            {
                start.Wait();
                return journal.ReserveCreate(device, Guid.NewGuid(), "direct-body").Response is not null;
            });
            start.Set();
            var winners = await Task.WhenAll(claim, direct);
            Check(winners.Count(winner => winner) == 1, "SQLite atomically arbitrates pending claim against simultaneous direct Create");
        }
    }

    private static void DurableBindingRestart(IConfiguration configuration)
    {
        Issued ticket;
        using (var fixture = new Fixture(configuration))
        {
            var claimed = fixture.Claim("owner", "profile");
            ticket = fixture.Issue("owner", fixture.Request(claimed.Entry!.Claim!.Id, "profile"));
        }
        string sessionId;
        using (var journal = new OperationJournal(configuration, new AdmissionClock()))
        {
            sessionId = journal.RedeemCustomerTicket("device", new(1, ticket.Token, ticket.Request.OperationId,
                ticket.Request.BootId, ticket.Request.Challenge)).Id;
            Check(journal.ReadCustomerWaiting("owner", "device").Entry!.State == "accepted",
                "durable ticket binding and live claim admit exactly once after process restart");
        }
        using (var journal = new OperationJournal(configuration, new AdmissionClock()))
        {
            var replay = journal.RedeemCustomerTicket("device", new(1, ticket.Token, ticket.Request.OperationId,
                ticket.Request.BootId, ticket.Request.Challenge));
            Check(replay.Id == sessionId && replay.State == "recovering" && journal.ActiveCustomerSession("device")!.Id == sessionId,
                "accepted replay after another restart retains the original uncertain runtime lease");
        }
    }

    private static void StaleTickets(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration);
        var first = fixture.Claim("owner", "profile");
        fixture.Clock.Advance(30);
        var stale = fixture.Issue("owner", fixture.Request(first.Entry!.Claim!.Id, "profile"));
        fixture.Clock.Advance(60);
        Expect("waiting_claim_unavailable", () => fixture.Redeem(stale));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_sessions") == 0, "expired claim rejects even while its newer ticket remains unexpired");
        var expiredEntry = fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!;
        var reclaimed = fixture.Journal.ClaimCustomerWaiting("owner", "device", Command(fixture.Event!.Id, expiredEntry));
        Expect("waiting_claim_unavailable", () => fixture.Redeem(stale));
        fixture.ChangeState("paused");
        var request = fixture.Request(reclaimed.Entry!.Claim!.Id, "profile");
        Expect("waiting_event_unavailable", () => fixture.Issue("owner", request));
        fixture.ChangeState("open");
        var live = fixture.Issue("owner", request);
        fixture.ChangeState("paused");
        Expect("waiting_event_unavailable", () => fixture.Redeem(live));
        fixture.ChangeState("open");
        fixture.Journal.LeaveCustomerWaiting("owner", "device", Command(fixture.Event!.Id, reclaimed.Entry));
        Expect("waiting_claim_unavailable", () => fixture.Redeem(live));
        var next = fixture.Claim("owner", "profile");
        var oldEventTicket = fixture.Issue("owner", fixture.Request(next.Entry!.Claim!.Id, "profile"));
        var closed = fixture.ChangeState("closed");
        Expect("waiting_event_unavailable", () => fixture.Redeem(oldEventTicket));
        fixture.Event = fixture.Journal.OpenCustomerWaiting("device", Id(), Id(), closed.Id, closed.Revision, activationRevision: 1);
        Expect("waiting_claim_unavailable", () => fixture.Redeem(oldEventTicket));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_sessions") == 0
            && Scalar(configuration, "SELECT COUNT(*) FROM device_conversation_leases") == 0,
            "expiry, pause, leave, close and new-event failures never partially accept a session or lease");
    }

    private static void LegacyActivation(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration, waiting: false);
        var request = fixture.Request(Id(), "profile");
        var unbound = fixture.Issue("owner", request);
        fixture.Open();
        Expect("waiting_claim_required", () => fixture.Redeem(unbound));
        Check(fixture.Journal.IssueCustomerTicket("owner", request, "snapshot", "unused-hash", "unused-protected") == unbound.Record,
            "legacy ticket issuance replay is preserved without conferring new admission authority");
        fixture.ChangeState("closed");
        Expect("waiting_event_unavailable", () => fixture.Issue("owner", request with { OperationId = Id() }));
        Expect("waiting_event_unavailable", () => fixture.Redeem(unbound));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_sessions") == 0, "closed waiting history never restores legacy admission");
    }

    private static void TerminalSettlement(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration);
        var claimed = fixture.Claim("owner", "profile");
        var ticket = fixture.Issue("owner", fixture.Request(claimed.Entry!.Claim!.Id, "profile"));
        var accepted = fixture.Redeem(ticket);
        var entry = fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!;
        fixture.Journal.RequestCustomerEnd(accepted.Id, "owner");
        fixture.Journal.ReportCustomerIdle("device", fixture.Boot);
        Check(fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!.Id == entry.Id
            && fixture.Journal.ActiveCustomerSession("device")!.Id == accepted.Id,
            "phone end intent and same-boot idle do not settle uncertain physical ownership");
        Expect("waiting_entry_unavailable", () => fixture.Journal.LeaveCustomerWaiting("owner", "device", Command(fixture.Event!.Id, entry)));
        fixture.Journal.ReportCustomerState("device", accepted.Id, new(fixture.Boot, "failed"));
        Check(fixture.Journal.ReadCustomerWaiting("owner", "device").Entry is null
            && fixture.Journal.ActiveCustomerSession("device") is null, "authoritative terminal session settles accepted participation");
        var next = fixture.Journal.JoinCustomerWaiting("owner", "device", new(Id(), fixture.Event!.Id, "profile"));
        Check(next.Entry!.Id != entry.Id && next.Entry.Sequence > entry.Sequence, "settled owner can explicitly rejoin the same event");
        Check(fixture.Redeem(ticket).Id == accepted.Id && fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!.Id == next.Entry.Id,
            "old accepted ticket replay never mutates the owner's new entry");
    }

    private static void FreshBootSettlement(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration);
        var claimed = fixture.Claim("owner", "profile");
        var ticket = fixture.Issue("owner", fixture.Request(claimed.Entry!.Claim!.Id, "profile"));
        var session = fixture.Redeem(ticket);
        var freshBoot = Id();
        fixture.Journal.RegisterCustomerChallenge("device", new(1, freshBoot, Id(), 0, "catalog-v1", null));
        fixture.Journal.ReportCustomerIdle("device", freshBoot);
        Check(fixture.Journal.CustomerSession(session.Id)!.State == "failed"
            && fixture.Journal.ReadCustomerWaiting("owner", "device").Entry is null
            && fixture.Journal.ActiveCustomerSession("device") is null,
            "actual old-boot idle retirement settles only the retired session and releases its existing lease");
        Check(fixture.Journal.JoinCustomerWaiting("owner", "device", new(Id(), fixture.Event!.Id, "profile")).Position == 1,
            "fresh-boot retirement permits explicit rejoin");
    }

    private static void RevocationAndDeletion(IConfiguration configuration)
    {
        using (var fixture = new Fixture(configuration))
        {
            var claimed = fixture.Claim("owner", "profile");
            var originalJoin = new CustomerWaitingJoin(Id(), fixture.Event!.Id, "profile");
            fixture.Journal.JoinCustomerWaiting("owner", "device", originalJoin);
            fixture.Issue("owner", fixture.Request(claimed.Entry!.Claim!.Id, "profile"));
            fixture.Journal.JoinCustomerWaiting("other-owner", "device", new(Id(), fixture.Event.Id, "other-profile"));
            fixture.Journal.RevokeCustomerProfile("owner", "profile");
            Check(fixture.Journal.ReadCustomerWaiting("other-owner", "device").Position == 1
                && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_ticket_bindings") == 0,
                "profile revocation removes only unredeemed claim eligibility and binding");
            fixture.Journal.JoinCustomerWaiting("owner", "device", new(Id(), fixture.Event.Id, "fresh-profile"));
            var other = fixture.Journal.ReadCustomerWaiting("other-owner", "device");
            var otherClaim = fixture.Journal.ClaimCustomerWaiting("other-owner", "device", Command(fixture.Event.Id, other.Entry!));
            fixture.Issue("other-owner", fixture.Request(otherClaim.Entry!.Claim!.Id, "other-profile"));
            fixture.Journal.SuspendCustomerAccount("other-owner");
            Check(fixture.Journal.ReadCustomerWaiting("owner", "device").Position == 1
                && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_ticket_bindings") == 0,
                "temporary suspend cancels unredeemed waiting eligibility and bindings");
            fixture.Journal.JoinCustomerWaiting("other-owner", "device", new(Id(), fixture.Event.Id, "other-profile"));
            var ownerEntry = fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!;
            var ownerClaim = fixture.Journal.ClaimCustomerWaiting("owner", "device", Command(fixture.Event.Id, ownerEntry));
            var acceptedTicket = fixture.Issue("owner", fixture.Request(ownerClaim.Entry!.Claim!.Id, "fresh-profile"));
            var accepted = fixture.Redeem(acceptedTicket);
            fixture.Journal.SuspendCustomerAccount("owner");
            Check(fixture.Journal.ActiveCustomerSession("device")!.Id == accepted.Id
                && fixture.Journal.ReadCustomerWaiting("owner", "device").Entry!.State == "accepted",
                "temporary suspend preserves accepted ownership and requests end through the existing flow");
            fixture.Journal.RevokeCustomerProfile("owner", "*");
            Check(fixture.Journal.ActiveCustomerSession("device")!.Id == accepted.Id && fixture.Journal.CustomerSession(accepted.Id)!.EndRequested,
                "permanent deletion never releases an active physical lease");
            Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_entries WHERE owner_id='owner'") == 0
                && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_receipts WHERE actor_id='owner:owner'") == 0
                && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_ticket_bindings") == 0,
                "permanent wildcard fence allows deletion of that account's queue rows, receipts and technical binding metadata");
            Check(fixture.Journal.ReadCustomerWaiting("other-owner", "device").Entry is not null, "other owner's entries survive deletion isolation");
            Expect("customer_profile_revoked", () => fixture.Journal.JoinCustomerWaiting("owner", "device", originalJoin));
            Expect("customer_profile_revoked", () => fixture.Issue("owner", acceptedTicket.Request));
            Check(fixture.Redeem(acceptedTicket).Id == accepted.Id, "accepted replay remains the same existing session after permanent revocation");
        }
        using var restarted = new OperationJournal(configuration, new AdmissionClock());
        Expect("customer_profile_revoked", () => restarted.JoinCustomerWaiting("owner", "device", new(Id(), restarted.ReadCustomerWaitingEvent("device")!.Id, "new-profile")));
        Check(restarted.ReadCustomerWaiting("other-owner", "device").Entry is not null
            && restarted.ActiveCustomerSession("device") is not null, "restart preserves durable deletion fence and unrelated participation/lease");
    }

    private static void AcceptedProfileRevocation(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration);
        var claimed = fixture.Claim("owner", "old-profile");
        var ticket = fixture.Issue("owner", fixture.Request(claimed.Entry!.Claim!.Id, "old-profile"));
        var session = fixture.Redeem(ticket);
        fixture.Journal.RevokeCustomerProfile("owner", "old-profile");
        Expect("waiting_entry_unavailable", () => fixture.Journal.JoinCustomerWaiting("owner", "device", new(Id(), fixture.Event!.Id, "fresh-profile")));
        var other = fixture.Journal.JoinCustomerWaiting("other-owner", "device", new(Id(), fixture.Event!.Id, "other-profile"));
        Check(other.Position == 1 && fixture.Journal.ActiveCustomerSession("device")!.Id == session.Id
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_entries WHERE owner_id='owner' AND state='accepted'") == 1,
            "revoked accepted profile produces typed rejection, retains its physical lease, and does not block other guests joining");
        fixture.Journal.ReportCustomerState("device", session.Id, new(fixture.Boot, "failed"));
        var rejoined = fixture.Journal.JoinCustomerWaiting("owner", "device", new(Id(), fixture.Event.Id, "fresh-profile"));
        Check(rejoined.Entry!.ProfileId == "fresh-profile" && rejoined.Entry.Id != claimed.Entry.Id && rejoined.Position == 2
            && fixture.Journal.ActiveCustomerSession("device") is null,
            "authoritative settlement of a revoked accepted profile allows explicit fresh-profile rejoin afterward");
    }

    private static void CommitAuthority(IConfiguration configuration)
    {
        using var fixture = new Fixture(configuration);
        var journal = fixture.Journal;
        RejectGuard(() => journal.OpenCustomerWaiting("other-device", Id(), Id(), activationRevision: 1, beforeCommit: ExpiredGuard));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_events") == 1
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_receipts") == 1, "failed final Open authority rolls back event and receipt");
        var join = new CustomerWaitingJoin(Id(), fixture.Event!.Id, "profile");
        RejectGuard(() => journal.JoinCustomerWaiting("owner", "device", join, beforeCommit: ExpiredGuard));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_entries") == 0, "failed final Join authority does not advance position");
        var joined = journal.JoinCustomerWaiting("owner", "device", join);
        RejectGuard(() => journal.JoinCustomerWaiting("owner", "device", join, beforeCommit: ExpiredGuard));
        var claim = Command(fixture.Event.Id, joined.Entry!);
        RejectGuard(() => journal.ClaimCustomerWaiting("owner", "device", claim, beforeCommit: ExpiredGuard));
        Check(journal.ReadCustomerWaiting("owner", "device").Entry == joined.Entry, "failed final Claim authority leaves no claim or revision advance");
        var claimed = journal.ClaimCustomerWaiting("owner", "device", claim);
        RejectGuard(() => journal.ClaimCustomerWaiting("owner", "device", claim, beforeCommit: ExpiredGuard));
        RejectGuard(() => journal.LeaveCustomerWaiting("owner", "device", Command(fixture.Event.Id, claimed.Entry!), beforeCommit: ExpiredGuard));
        RejectGuard(() => journal.CancelCustomerWaiting("device", Command(fixture.Event.Id, claimed.Entry!), beforeCommit: ExpiredGuard));
        var current = journal.ReadCustomerWaitingEvent("device")!;
        RejectGuard(() => journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), current.Revision, "paused", beforeCommit: ExpiredGuard));
        Check(journal.ReadCustomerWaitingEvent("device") == current && journal.ReadCustomerWaiting("owner", "device").Entry == claimed.Entry,
            "replay and rejected Leave/skip/pause authority preserve accepted key, claim and event revision");
        var request = fixture.Request(claimed.Entry!.Claim!.Id, "profile");
        var token = "commit-ticket-" + Id(); var hash = OperationJournal.CustomerHash(token);
        RejectGuard(() => journal.IssueCustomerTicket("owner", request, "snapshot", hash, "protected", beforeCommit: ExpiredGuard));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_start_tickets") == 0
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_ticket_bindings") == 0, "failed final ticket authority rolls back ticket and binding together");
        journal.IssueCustomerTicket("owner", request, "snapshot", hash, "protected");
        RejectGuard(() => journal.IssueCustomerTicket("owner", request, "unused", "unused", "unused", beforeCommit: ExpiredGuard));
        var redemption = new CustomerRedemption(1, token, request.OperationId, request.BootId, request.Challenge);
        RejectGuard(() => journal.RedeemCustomerTicket("device", redemption, beforeCommit: ExpiredGuard));
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_sessions") == 0
            && Scalar(configuration, "SELECT COUNT(*) FROM device_conversation_leases") == 0
            && journal.CustomerTicketOwner(hash)!.SessionId is null
            && journal.ReadCustomerWaiting("owner", "device").Entry == claimed.Entry,
            "failed final redemption authority rolls back session/lease/ticket acceptance and claim consumption");
        var accepted = journal.RedeemCustomerTicket("device", redemption);
        RejectGuard(() => journal.RedeemCustomerTicket("device", redemption, beforeCommit: ExpiredGuard));
        Check(journal.RedeemCustomerTicket("device", redemption).Id == accepted.Id
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_sessions") == 1
            && Scalar(configuration, "SELECT COUNT(*) FROM device_conversation_leases") == 1,
            "accepted replay revalidates final authority without regenerating or releasing existing ownership");
    }

    private static void ExpiredGuard() => throw new AdmissionGuardExpiredException();
    private static void RejectGuard(Action action)
    {
        try { action(); throw new InvalidOperationException("Expired final guard was accepted."); }
        catch (AdmissionGuardExpiredException) { }
    }
    private sealed class AdmissionGuardExpiredException : Exception;

    private static CustomerWaitingEntryCommand Command(string eventId, CustomerWaitingEntry entry) => new(Id(), eventId, entry.Id, entry.Revision);
    private static string Id() => Guid.NewGuid().ToString("D");
    private static IConfiguration Configuration(string root, string name) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Backend:DatabasePath"] = Path.Combine(root, name + ".sqlite") }).Build();
    private static string? Text(IConfiguration configuration, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]);
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()?.ToString();
    }
    private static long Scalar(IConfiguration configuration, string sql) => long.Parse(Text(configuration, sql)!);
    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (CustomerSessionException error) when (error.Code == code) { }
    }
    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
    private sealed class AdmissionClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
    private sealed record Issued(CustomerTicketRequest Request, string Token, CustomerTicketRecord Record);
    private sealed class Fixture : IDisposable
    {
        internal AdmissionClock Clock { get; } = new();
        internal OperationJournal Journal { get; }
        internal string Boot { get; } = Id();
        private string Challenge { get; } = Id();
        internal CustomerWaitingEvent? Event { get; set; }
        internal Fixture(IConfiguration configuration, bool waiting = true)
        {
            Journal = new(configuration, Clock);
            Journal.SetCustomerMode("device", true);
            Journal.RegisterCustomerChallenge("device", new(1, Boot, Challenge, 0, "catalog-v1", null));
            if (waiting) { Open(); }
        }
        internal void Open() => Event = Journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        internal CustomerWaitingView Claim(string owner, string profile)
        {
            var view = Journal.JoinCustomerWaiting(owner, "device", new(Id(), Event!.Id, profile));
            return Journal.ClaimCustomerWaiting(owner, "device", Command(Event.Id, view.Entry!));
        }
        internal CustomerTicketRequest Request(string operation, string profile) => new(1, operation, "device", Boot, Challenge, profile, 1, OpenAiConsent: true);
        internal Issued Issue(string owner, CustomerTicketRequest request)
        {
            var token = "ticket-" + Id();
            return new(request, token, Journal.IssueCustomerTicket(owner, request, "snapshot", OperationJournal.CustomerHash(token), "protected:" + token));
        }
        internal CustomerSessionRecord Redeem(Issued ticket) => Journal.RedeemCustomerTicket("device",
            new(1, ticket.Token, ticket.Request.OperationId, ticket.Request.BootId, ticket.Request.Challenge));
        internal CustomerWaitingEvent ChangeState(string state)
        {
            var current = Journal.ReadCustomerWaitingEvent("device")!;
            return Journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), current.Revision, state);
        }
        public void Dispose() => Journal.Dispose();
    }
}

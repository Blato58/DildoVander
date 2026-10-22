using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Realtime;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

internal static class CustomerSessionTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-sessions-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await RaceAndRestartAsync(root);
            ExpiredUnacceptedStart(root);
            await ExpiryWaitsForRedemptionAsync(root);
            await SharedStartupAsync(root);
            ConsentAdmission(root);
            NoncustomerCleanup(root);
            OwnershipAndRevocation(root);
            Console.WriteLine("PASS customer sessions: SQLite arbitration/restart, ticket bindings/replay/expiry, profile deletion, shared engine startup and private keepsake ownership");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task RaceAndRestartAsync(string root)
    {
        var config = Configuration(root, "race");
        var clock = new CustomerClock();
        CustomerSessionRecord winner;
        string token;
        CustomerTicketRequest first;
        var boot = Guid.NewGuid().ToString("D");
        using (var journal = new OperationJournal(config, clock))
        {
            journal.SetCustomerMode("device-a", true);
            journal.RegisterCustomerChallenge("device-a", Challenge(boot));
            first = Request(boot);
            var second = Request(boot);
            token = new string('a', 43);
            journal.IssueCustomerTicket("account-a", first, "private-original", OperationJournal.CustomerHash(token), "protected-a");
            journal.IssueCustomerTicket("account-b", second, "private-second", OperationJournal.CustomerHash(new string('b', 43)), "protected-b");
            var results = await Task.WhenAll(Task.Run(() => TryRedeem(journal, first, token)),
                Task.Run(() => TryRedeem(journal, second, new string('b', 43))));
            Check(results.Count(value => value is not null) == 1, "two phones obtain exactly one durable device lease");
            winner = results.Single(value => value is not null)!;
            Check(journal.CustomerSessions("unrelated-account").Length == 0, "session list is exact-account scoped");
            Check(journal.CustomerSession(winner.Id, "unrelated-account") is null, "UUID knowledge cannot read another session");
            Check(!journal.RequestCustomerEnd(winner.Id, "unrelated-account"), "stranger cannot request session end");
            Check(journal.ReserveCreate("device-a", Guid.NewGuid(), "legacy", bootId: boot).Conflict,
                "legacy/admin create cannot bypass customer reservation");
            clock.Advance(300);
            var winningRequest = winner.OwnerId == "account-a" ? first : second;
            var winningToken = winner.OwnerId == "account-a" ? token : new string('b', 43);
            Check(journal.RedeemCustomerTicket("device-a", Redeem(winningRequest, winningToken)).Id == winner.Id,
                "accepted retry survives ticket expiry without a second session");
            Expect("start_ticket_binding_mismatch", () => journal.RedeemCustomerTicket("device-a",
                Redeem(winningRequest, winningToken) with { OperationId = Guid.NewGuid().ToString("D") }));
            Expect("idempotency_conflict", () => journal.IssueCustomerTicket("account-a", first with { DeviceId = "other" },
                "", "unused", "unused"));
        }
        using (var journal = new OperationJournal(config, clock))
        {
            Check(journal.CustomerSession(winner.Id)!.State == "recovering", "restart retains uncertain start as recovering");
            Check(journal.ActiveCustomerSession("device-a")!.Id == winner.Id, "restart preserves physical device ownership");
            var newBoot = Guid.NewGuid().ToString("D");
            journal.RegisterCustomerChallenge("device-a", Challenge(newBoot));
            Expect("device_epoch_mismatch", () => journal.ReportCustomerState("device-a", winner.Id, new(boot, "running")));
            Expect("device_epoch_mismatch", () => journal.ReportCustomerIdle("device-a", boot));
            journal.ReportCustomerIdle("device-a", newBoot);
            Check(journal.CustomerSession(winner.Id)!.State == "failed" && journal.ActiveCustomerSession("device-a") is null,
                "authenticated physical idle at a fresh boot reconciles the previous owner without replay");
        }
    }

    private static CustomerSessionRecord? TryRedeem(OperationJournal journal, CustomerTicketRequest request, string token)
    {
        try { return journal.RedeemCustomerTicket("device-a", Redeem(request, token)); }
        catch (CustomerSessionException error) when (error.Code == "device_busy") { return null; }
    }

    private static async Task SharedStartupAsync(string root)
    {
        using var journal = new OperationJournal(Configuration(root, "startup"), new CustomerClock());
        var boot = Guid.NewGuid().ToString("D");
        journal.SetCustomerMode("device-a", true);
        journal.RegisterCustomerChallenge("device-a", Challenge(boot));
        var request = Request(boot);
        var token = new string('c', 43);
        const string profile = "{\"profile\":{\"userInput\":{\"name\":\"Harry fixture\",\"world\":\"fictional source\"}},\"personalMemoryConsent\":false,\"openAiConsent\":true}";
        journal.IssueCustomerTicket("account-a", request, profile, OperationJournal.CustomerHash(token), "protected");
        var session = journal.RedeemCustomerTicket("device-a", Redeem(request, token));
        var store = new ConversationStore(new CustomerClock(), journal);
        var create = new CreateConversationRequest("device-a", Guid.Parse(boot), 0, PartyWandCatalog.Version, "cs-CZ", new(true, false));
        var response = store.Create("device-a", Guid.Parse(request.OperationId), JsonSerializer.SerializeToUtf8Bytes(create), create,
            customerSessionId: session.Id).Response!;
        Check(response is not null, "customer reservation admits its exact conversation");
        var turn = new RealtimeTurnRequest(Guid.NewGuid(), Guid.Parse(boot), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var admission = store.BeginRealtimeTurn("device-a", response!.ConversationId, Guid.NewGuid(),
            JsonSerializer.SerializeToUtf8Bytes(turn), turn, true, default, default, handsFree: true, gptLive: true);
        Check(admission.Call is not null, "actual stream admission accepts captured customer context");
        var call = await ConversationStartupMemory.PrepareAsync(admission.Call!, store, null!, default);
        var live = JsonSerializer.Serialize(GptLiveProtocol.Start(call.Context));
        var realtime = RealtimeProtocol.ContextData(call.Context);
        Check(live.Contains("Harry fixture") && realtime.Contains("Harry fixture"), "both engines receive immutable profile before startup");
        Check(!live.Contains("account-a") && !realtime.Contains("account-a"), "provider context excludes account authority identifiers");
        var profiles = new CharacterProfileStore(Path.Combine(root, "startup-characters"));
        await profiles.DeleteAsync("account-a", 1, request.ProfileId);
        using var services = new ServiceCollection().AddSingleton(journal).AddSingleton(profiles).BuildServiceProvider();
        try
        {
            await ConversationStartupMemory.PrepareAsync(admission.Call!, store, null!, default, services);
            throw new InvalidOperationException("Deleted profile started provider before journal replay.");
        }
        catch (OperationCanceledException) { }
        Check(journal.CustomerSession(session.Id)!.Snapshot.Length > 0,
            "fault window fixture retains journal snapshot before durable deletion replay");
        await new CharacterProfileStore(Path.Combine(root, "startup-characters")).ReplayDeletionsAsync((owner, profileId) =>
        {
            journal.RevokeCustomerProfile(owner, profileId);
            store.InvalidateMemoryContext("device-a", response.ConversationId);
            return Task.CompletedTask;
        });
        try { store.CustomerStartupContext(call); throw new InvalidOperationException("Revoked profile context accepted."); }
        catch (OperationCanceledException) { }
        Expect("customer_profile_revoked", () => journal.IssueCustomerTicket("account-a", request with { OperationId = Guid.NewGuid().ToString("D") },
            profile, "late-hash", "late-protected"));
        Check(journal.CustomerSession(session.Id)!.Snapshot.Length == 0, "explicit profile deletion erases captured private data");
        Check(journal.ActiveCustomerSession("device-a") is not null, "revocation requests end but cannot release a possibly speaking device");
        await store.EndConversationAfterImageAsync("device-a", response.ConversationId, Guid.NewGuid(), default);
        journal.ReportCustomerState("device-a", session.Id, new(boot, "completed", response.ConversationId));
        Check(journal.ActiveCustomerSession("device-a") is null, "only device terminal acknowledgement releases customer lease");
    }

    private static void ExpiredUnacceptedStart(string root)
    {
        var clock = new CustomerClock();
        using var journal = new OperationJournal(Configuration(root, "expired-start"), clock);
        var boot = Guid.NewGuid().ToString("D");
        journal.SetCustomerMode("device-a", true);
        journal.RegisterCustomerChallenge("device-a", Challenge(boot));
        var rejected = Request(boot);
        var token = new string('e', 43);
        journal.IssueCustomerTicket("account-a", rejected, "snapshot", OperationJournal.CustomerHash(token), "protected");
        Check(journal.ExpiredCustomerOperations("account-a").Length == 0,
            "unknown but unexpired start cannot be retired by phone timeout");
        clock.Advance(91);
        Check(journal.ExpiredCustomerOperations("account-a").SequenceEqual([rejected.OperationId]),
            "journal confirms expired unredeemed operation without releasing any active lease");
        Check(journal.ExpiredCustomerOperations("other-account").Length == 0, "expired operation evidence is owner scoped");
        Expect("start_ticket_expired", () => journal.RedeemCustomerTicket("device-a", Redeem(rejected, token)));
        var fresh = Request(boot) with { Challenge = Guid.NewGuid().ToString("D") };
        journal.RegisterCustomerChallenge("device-a", Challenge(boot) with { Challenge = fresh.Challenge });
        var freshToken = new string('f', 43);
        journal.IssueCustomerTicket("account-a", fresh, "snapshot", OperationJournal.CustomerHash(freshToken), "fresh-protected");
        var accepted = journal.RedeemCustomerTicket("device-a", Redeem(fresh, freshToken));
        clock.Advance(91);
        Check(!journal.ExpiredCustomerOperations("account-a").Contains(fresh.OperationId)
            && journal.ActiveCustomerSession("device-a")!.Id == accepted.Id,
            "accepted operation is never retired as expired, even after its ticket expires");
    }

    private static async Task ExpiryWaitsForRedemptionAsync(string root)
    {
        var clock = new CustomerClock();
        using var journal = new OperationJournal(Configuration(root, "expiry-race"), clock);
        var boot = Guid.NewGuid().ToString("D");
        journal.SetCustomerMode("device-a", true);
        journal.RegisterCustomerChallenge("device-a", Challenge(boot));
        var request = Request(boot);
        journal.IssueCustomerTicket("account-a", request, "snapshot", OperationJournal.CustomerHash(new string('g', 43)), "protected");
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "expiry-race.sqlite") }.ToString());
        writer.Open();
        using var redemption = writer.BeginTransaction();
        using var command = writer.CreateCommand();
        command.Transaction = redemption;
        // Hold redemption at its commit boundary after it passed expiry and
        // persisted acceptance, while WAL readers still see the unredeemed row.
        command.CommandText = "UPDATE customer_start_tickets SET session_id=$session WHERE owner_id='account-a'";
        command.Parameters.AddWithValue("$session", Guid.NewGuid().ToString("D"));
        command.ExecuteNonQuery();
        clock.Advance(91);
        using var started = new ManualResetEventSlim();
        var evidence = Task.Run(() => { started.Set(); return journal.ExpiredCustomerOperations("account-a"); });
        Check(started.Wait(TimeSpan.FromSeconds(5)), "expiry reader starts concurrently");
        await Task.Delay(100);
        Check(!evidence.IsCompleted, "expiry proof waits for an in-flight acceptance commit");
        redemption.Commit();
        Check((await evidence).Length == 0, "committed accepted ticket is never reported as definitively unused");
    }

    private static void ConsentAdmission(string root)
    {
        var config = Configuration(root, "consent");
        var boot = Guid.NewGuid().ToString("D");
        using var journal = new OperationJournal(config, new CustomerClock());
        journal.SetCustomerMode("device-a", true);
        journal.RegisterCustomerChallenge("device-a", Challenge(boot));
        var request = Request(boot);
        Expect("openai_consent_required", () => journal.IssueCustomerTicket("account-a", request with { OpenAiConsent = false },
            "snapshot", "hash", "protected"));
        var token = new string('c', 43);
        journal.IssueCustomerTicket("account-a", request, "snapshot", OperationJournal.CustomerHash(token), "protected");
        using var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "consent.sqlite"));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE customer_start_tickets SET openai_consent=0";
        command.ExecuteNonQuery();
        Expect("openai_consent_required", () => journal.RedeemCustomerTicket("device-a", Redeem(request, token)));
        try
        {
            CustomerContextBuilder.Build("{\"profile\":{\"userInput\":{\"name\":\"Private\",\"world\":\"Original\"}}}");
            throw new InvalidOperationException("Legacy context admitted without consent.");
        }
        catch (InvalidDataException) { }
    }

    private static void OwnershipAndRevocation(string root)
    {
        var config = Configuration(root, "ownership");
        var store = new KeepsakeStore(config, TimeProvider.System);
        var snapshot = new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"), "device-a", 1,
            "fixture-wand", "Fixture wand", "public catalog facts", "completed_by_timing");
        var original = store.Issue(snapshot, new("account-a", "session-a"));
        Check(store.CustomerAccountLinks("account-a").Single().Locator == original.Id, "outcome belongs to original account before generation");
        Check(store.CustomerAccountLinks("account-b").Length == 0, "same character name never links another account");
        Check(store.Issue(snapshot, new("account-a", "session-a")).Id == original.Id, "late/retried outcome remains idempotent");
        store.RemoveCustomerAccountLink("account-a", original.Id);
        store.Issue(snapshot, new("account-a", "session-a"));
        Check(store.CustomerAccountLinks("account-a").Length == 0, "late outcome does not resurrect a deliberately removed association");
        try { store.Issue(snapshot, new("account-b", "session-b")); throw new InvalidOperationException("Owner was overwritten."); }
        catch (KeepsakeException error) when (error.Code == "outcome_owner_conflict") { }
    }

    private static void NoncustomerCleanup(string root)
    {
        var clock = new CustomerClock();
        using var journal = new OperationJournal(Configuration(root, "cleanup"), clock);
        var store = new ConversationStore(clock, journal);
        var request = new CreateConversationRequest("legacy-device", Guid.NewGuid(), 0, PartyWandCatalog.Version, "cs-CZ", new(true, false));
        Check(store.Create(request.DeviceId, Guid.NewGuid(), "first"u8, request).Response is not null, "legacy creation admitted");
        clock.Advance(901);
        var afterExpiry = store.Create(request.DeviceId, Guid.NewGuid(), "second"u8, request).Response;
        Check(afterExpiry is not null, "authoritative server expiration releases only its noncustomer lease");
        store.InvalidateMemoryContext(request.DeviceId, afterExpiry!.ConversationId);
        var afterRevocation = store.Create(request.DeviceId, Guid.NewGuid(), "third"u8, request).Response;
        Check(afterRevocation is not null, "memory revocation cleanup does not strand noncustomer lease");
    }

    private static IConfiguration Configuration(string root, string name) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Backend:DatabasePath"] = Path.Combine(root, name + ".sqlite"),
        ["Keepsake:DataRoot"] = Path.Combine(root, name + "-archive"), ["Keepsake:Enabled"] = "true"
    }).Build();
    private static CustomerDeviceChallenge Challenge(string boot) => new(1, boot, "01234567-89ab-cdef-0123-456789abcdef", 0, PartyWandCatalog.Version, null);
    private static CustomerTicketRequest Request(string boot) => new(1, Guid.NewGuid().ToString("D"), "device-a", boot,
        Challenge(boot).Challenge, Guid.NewGuid().ToString("D"), 1, OpenAiConsent: true);
    private static CustomerRedemption Redeem(CustomerTicketRequest request, string token) => new(1, token, request.OperationId, request.BootId, request.Challenge);
    private static void Check(bool condition, string message) { if (!condition) { throw new InvalidOperationException(message); } }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (CustomerSessionException error) when (error.Code == code) { }
    }
    private sealed class CustomerClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}

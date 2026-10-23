using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class CustomerWaitingActivationTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-waiting-activation-" + Id());
        Directory.CreateDirectory(root);
        try
        {
            MutationRevisions(Configuration(root, "mutations"));
            LegacyMemory(Configuration(root, "legacy-memory"));
            LegacyWaiting(Configuration(root, "legacy-waiting"));
            Overflow(Configuration(root, "overflow"));
            CorruptNumericRevision(Configuration(root, "numeric"));
            ReturningToEvening(Configuration(root, "return"));
            await GuardArbitrationAsync(Configuration(root, "guard"));
            await WalActivationReservationAsync(Configuration(root, "wal"));
            await ProximityTransactionAsync(Configuration(root, "proximity"));
            Console.WriteLine("PASS waiting activation: SQLite A-B-A/restart fence, atomic revision capture, Create/Activate/Clear CAS and replay, legacy fail-closed migration, Int64/type overflow safety, two-store writer arbitration, bounded contention/unwind and transactional fresh BLE proof");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static void MutationRevisions(IConfiguration configuration)
    {
        CustomerWaitingEveningScope last;
        using (var journal = new OperationJournal(configuration, TimeProvider.System))
        {
            var memory = new EveningMemoryStore(configuration, journal);
            var first = memory.GetStatus("device");
            var a = memory.ReadCustomerWaitingEvening("device");
            Check(a.ActivationRevision == 1 && a.Open, "new initial evening establishes exactly revision one");
            memory.GetStatus("device"); memory.List("device"); memory.ReadContext("device", "conversation", "name");
            Check(memory.ReadCustomerWaitingEvening("device") == a, "read-only existing Ensure calls do not advance activation");
            Check(!JsonSerializer.Serialize(first).Contains("activation", StringComparison.OrdinalIgnoreCase), "legacy memory status DTO has no new activation field");
            var createKey = Guid.NewGuid();
            Check(memory.Create("device", createKey, "create", "new-b", a.EveningId!, "Evening B").Status == 201, "Create accepted");
            var b = memory.ReadCustomerWaitingEvening("device");
            Check(b.ActivationRevision == 2 && b.EveningId != a.EveningId, "Create commits evening and revision together");
            Check(memory.Create("device", createKey, "create", "new-b", a.EveningId!, "Evening B").Status == 201
                && memory.ReadCustomerWaitingEvening("device") == b, "accepted Create replay does not increment revision");
            Check(memory.Create("device", Guid.NewGuid(), "create", "bad-cas", a.EveningId!, "No write").Status == 409
                && memory.Activate("device", Guid.NewGuid(), "activate", "bad-cas", a.EveningId!, a.EveningId!).Status == 409
                && memory.Activate("device", Guid.NewGuid(), "activate", "missing", Id(), b.EveningId!).Status == 404
                && memory.ReadCustomerWaitingEvening("device") == b, "failed CAS and missing target never advance revision");
            var activateKey = Guid.NewGuid();
            Check(memory.Activate("device", activateKey, "activate", "back-a", a.EveningId!, b.EveningId!).Status == 200, "Activate accepted");
            var returned = memory.ReadCustomerWaitingEvening("device");
            Check(returned.EveningId == a.EveningId && returned.ActivationRevision == 3, "return to A gets a new monotonic activation");
            Check(memory.Activate("device", activateKey, "activate", "back-a", a.EveningId!, b.EveningId!).Status == 200
                && memory.ReadCustomerWaitingEvening("device") == returned, "accepted Activate replay does not advance revision");
            Check(!memory.Clear("device", b.EveningId!) && memory.ReadCustomerWaitingEvening("device") == returned, "failed Clear CAS does not advance revision");
            Check(memory.Clear("device", returned.EveningId!), "Clear accepted");
            last = memory.ReadCustomerWaitingEvening("device");
            Check(last.ActivationRevision == 4 && last.EveningId != returned.EveningId
                && !memory.Clear("device", returned.EveningId!) && memory.ReadCustomerWaitingEvening("device") == last,
                "Clear atomically rotates identity and revision; retry is harmless");
        }
        using var restarted = new OperationJournal(configuration, TimeProvider.System);
        var restartedMemory = new EveningMemoryStore(configuration, restarted);
        Check(restartedMemory.ReadCustomerWaitingEvening("device") == last, "migration and restart do not reset the revision");
    }

    private static void LegacyMemory(IConfiguration configuration)
    {
        var evening = Id();
        Execute(configuration, memory: true, """
            CREATE TABLE evenings(device_id TEXT PRIMARY KEY,evening_id TEXT NOT NULL,dropped_count INTEGER NOT NULL DEFAULT 0);
            INSERT INTO evenings VALUES('device',$evening,0);
            """, ("$evening", evening));
        using (var journal = new OperationJournal(configuration, TimeProvider.System))
        {
            var memory = new EveningMemoryStore(configuration, journal);
            memory.GetStatus("device");
            var legacy = memory.ReadCustomerWaitingEvening("device");
            Check(legacy.EveningId == evening && legacy.ActivationRevision is null && !legacy.Open,
                "legacy migration and existing Ensure preserve unknown activation as unavailable");
            Expect("waiting_activation_required", () => memory.WithCustomerWaitingEvent("device", evening, null,
                () => throw new InvalidOperationException("Legacy callback ran.")));
            Check(memory.Activate("device", Guid.NewGuid(), "activate", "establish", evening, evening).Status == 200,
                "explicit accepted activation establishes revision for legacy evening");
            Check(memory.ReadCustomerWaitingEvening("device").ActivationRevision == 1, "legacy explicit activation establishes revision one");
        }
        using var restarted = new OperationJournal(configuration, TimeProvider.System);
        Check(new EveningMemoryStore(configuration, restarted).ReadCustomerWaitingEvening("device").ActivationRevision == 1,
            "legacy migration runs once and cannot reset established identity");
    }

    private static void LegacyWaiting(IConfiguration configuration)
    {
        var eventId = Id(); var evening = Id();
        Execute(configuration, memory: false, """
            CREATE TABLE customer_waiting_events(id TEXT PRIMARY KEY,device_id TEXT NOT NULL,evening_id TEXT NOT NULL,
                state TEXT NOT NULL,revision INTEGER NOT NULL,next_sequence INTEGER NOT NULL DEFAULT 1);
            INSERT INTO customer_waiting_events VALUES($event,'device',$evening,'open',1,1);
            """, ("$event", eventId), ("$evening", evening));
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        Check(journal.ReadCustomerWaitingEvent("device")!.ActivationRevision is null, "legacy waiting event has no invented activation revision");
        Expect("waiting_activation_required", () => journal.JoinCustomerWaiting("owner", "device", new(Id(), eventId, "profile")));
        Expect("waiting_activation_required", () => journal.ClaimCustomerWaiting("owner", "device", new(Id(), eventId, Id(), 1)));
        Expect("waiting_activation_required", () => journal.OpenCustomerWaiting("device", evening, Id(), eventId, 1));
        var boot = Id(); var challenge = Id();
        journal.SetCustomerMode("device", true); journal.RegisterCustomerChallenge("device", new(1, boot, challenge, 0, "catalog", null));
        Expect("waiting_activation_required", () => journal.IssueCustomerTicket("owner", new(1, Id(), "device", boot, challenge, "profile", 1, OpenAiConsent: true),
            "snapshot", "token-hash", "protected"));
        var next = journal.OpenCustomerWaiting("device", evening, Id(), eventId, 1, activationRevision: 1);
        Check(next.ActivationRevision == 1 && next.Id != eventId, "explicit validated restart creates a new immutable bound event");
    }

    private static void Overflow(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var memory = new EveningMemoryStore(configuration, journal);
        var evening = memory.GetStatus("device").EveningId;
        memory.Append("device", evening, "preserved-op", "conversation", "preserved visitor", "preserved reply");
        Execute(configuration, memory: true, "UPDATE evenings SET activation_revision=$revision WHERE device_id='device'", ("$revision", long.MaxValue - 1));
        var key = Guid.NewGuid();
        Check(memory.Activate("device", key, "activate", "last-integer", evening, evening).Status == 200
            && memory.ReadCustomerWaitingEvening("device").ActivationRevision == long.MaxValue, "last safe increment remains an exact Int64");
        var catalogs = Scalar(configuration, true, "SELECT COUNT(*) FROM evening_catalog");
        var mutations = Scalar(configuration, true, "SELECT COUNT(*) FROM evening_mutations");
        Check(memory.Create("device", Guid.NewGuid(), "create", "overflow", evening, "No creation").ErrorCode == "evening_activation_unavailable"
            && memory.Activate("device", Guid.NewGuid(), "activate", "overflow", evening, evening).ErrorCode == "evening_activation_unavailable"
            && !memory.Clear("device", evening), "Create, Activate and Clear fail closed at Int64 maximum");
        Check(Scalar(configuration, true, "SELECT COUNT(*) FROM evening_catalog") == catalogs
            && Scalar(configuration, true, "SELECT COUNT(*) FROM evening_mutations") == mutations
            && Scalar(configuration, true, "SELECT COUNT(*) FROM exchanges") == 1
            && Text(configuration, true, "SELECT typeof(activation_revision) FROM evenings") == "integer"
            && memory.ReadCustomerWaitingEvening("device").EveningId == evening, "overflow creates no partial catalog/content/receipt writes or REAL promotion");
        Check(memory.Activate("device", key, "activate", "last-integer", evening, evening).Status == 200,
            "accepted replay remains available at Int64 maximum");
        try
        {
            Execute(configuration, memory: true, "UPDATE evenings SET activation_revision=activation_revision+1");
            throw new InvalidOperationException("SQLite promotion escaped the column constraint.");
        }
        catch (SqliteException error) when (error.SqliteErrorCode == 19) { }
    }

    private static void CorruptNumericRevision(IConfiguration configuration)
    {
        var evening = Id();
        Execute(configuration, memory: true, """
            CREATE TABLE evenings(device_id TEXT PRIMARY KEY,evening_id TEXT NOT NULL,dropped_count INTEGER NOT NULL DEFAULT 0,activation_revision INTEGER);
            INSERT INTO evenings VALUES('device',$evening,0,9223372036854775808.0);
            """, ("$evening", evening));
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var memory = new EveningMemoryStore(configuration, journal);
        Check(memory.ReadCustomerWaitingEvening("device").ActivationRevision is null
            && memory.Create("device", Guid.NewGuid(), "create", "bad-number", evening, "No write").ErrorCode == "evening_activation_unavailable"
            && !memory.Clear("device", evening), "preexisting imprecise REAL epoch cannot be truncated or reset into a usable identity");
        Check(Text(configuration, true, "SELECT typeof(activation_revision) FROM evenings") == "real"
            && Scalar(configuration, true, "SELECT COUNT(*) FROM evening_mutations") == 0, "invalid numeric migration preserves data and fails closed without receipt writes");
    }

    private static void ReturningToEvening(IConfiguration configuration)
    {
        CustomerWaitingEvent oldEvent;
        CustomerTicketRequest request;
        var token = "ticket-" + Id();
        using (var journal = new OperationJournal(configuration, TimeProvider.System))
        {
            var memory = new EveningMemoryStore(configuration, journal);
            memory.GetStatus("device"); var a = memory.ReadCustomerWaitingEvening("device");
            oldEvent = memory.WithCustomerWaitingEvent("device", a.EveningId!, a.ActivationRevision,
                () => journal.OpenCustomerWaiting("device", a.EveningId!, Id(), activationRevision: a.ActivationRevision));
            var joined = memory.WithCustomerWaitingEvening("device", oldEvent.EveningId, oldEvent.ActivationRevision,
                () => journal.JoinCustomerWaiting("owner", "device", new(Id(), oldEvent.Id, "profile")));
            var claimed = memory.WithCustomerWaitingEvening("device", oldEvent.EveningId, oldEvent.ActivationRevision,
                () => journal.ClaimCustomerWaiting("owner", "device", Command(oldEvent.Id, joined.Entry!)));
            var boot = Id(); var challenge = Id();
            journal.SetCustomerMode("device", true); journal.RegisterCustomerChallenge("device", new(1, boot, challenge, 0, "catalog", null));
            request = new(1, claimed.Entry!.Claim!.Id, "device", boot, challenge, "profile", 1, OpenAiConsent: true);
            memory.WithCustomerWaitingTicket("device", oldEvent.EveningId, oldEvent.ActivationRevision,
                () => journal.IssueCustomerTicket("owner", request, "snapshot", OperationJournal.CustomerHash(token), "protected"));
            memory.Create("device", Guid.NewGuid(), "create", "b", a.EveningId!, "Evening B");
            var b = memory.ReadCustomerWaitingEvening("device");
            memory.Activate("device", Guid.NewGuid(), "activate", "back-a", a.EveningId!, b.EveningId!);
            var returned = memory.ReadCustomerWaitingEvening("device");
            Check(returned.EveningId == oldEvent.EveningId && returned.ActivationRevision != oldEvent.ActivationRevision,
                "A-B-A preserves evening ID but never activation identity");
            Expect("waiting_activation_changed", () => memory.WithCustomerWaitingSession("device", oldEvent.EveningId, oldEvent.ActivationRevision,
                () => journal.RedeemCustomerTicket("device", new(1, token, request.OperationId, boot, challenge))));
            Expect("waiting_activation_changed", () => memory.WithCustomerWaitingEvening("device", oldEvent.EveningId, oldEvent.ActivationRevision,
                () => throw new InvalidOperationException("Old claim callback ran.")));
            Check(Scalar(configuration, false, "SELECT COUNT(*) FROM customer_sessions") == 0
                && Scalar(configuration, false, "SELECT COUNT(*) FROM device_conversation_leases") == 0, "stale activation rejection precedes all journal admission writes");
        }
        using (var journal = new OperationJournal(configuration, TimeProvider.System))
        {
            var memory = new EveningMemoryStore(configuration, journal);
            var current = memory.ReadCustomerWaitingEvening("device");
            Expect("waiting_activation_changed", () => memory.WithCustomerWaitingSession("device", oldEvent.EveningId, oldEvent.ActivationRevision,
                () => journal.RedeemCustomerTicket("device", new(1, token, request.OperationId, request.BootId, request.Challenge))));
            var old = journal.ReadCustomerWaitingEvent("device")!;
            var next = memory.WithCustomerWaitingEvent("device", current.EveningId!, current.ActivationRevision,
                () => journal.OpenCustomerWaiting("device", current.EveningId!, Id(), old.Id, old.Revision, current.ActivationRevision));
            Check(next.ActivationRevision == current.ActivationRevision && next.Id != oldEvent.Id, "explicit restart captures the new activation and closes old waiting");
            Expect("waiting_claim_unavailable", () => journal.RedeemCustomerTicket("device", new(1, token, request.OperationId, request.BootId, request.Challenge)));
        }
    }

    private static async Task GuardArbitrationAsync(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var first = new EveningMemoryStore(configuration, journal);
        first.GetStatus("device"); var a = first.ReadCustomerWaitingEvening("device");
        first.Create("device", Guid.NewGuid(), "create", "b", a.EveningId!, "Evening B");
        var b = first.ReadCustomerWaitingEvening("device");
        first.Activate("device", Guid.NewGuid(), "activate", "a", a.EveningId!, b.EveningId!);
        a = first.ReadCustomerWaitingEvening("device");
        var second = new EveningMemoryStore(configuration, journal);
        var availability = new EveningMemoryStore(configuration, journal);
        var waitingEvent = first.WithCustomerWaitingEvent("device", a.EveningId!, a.ActivationRevision,
            () => journal.OpenCustomerWaiting("device", a.EveningId!, Id(), activationRevision: a.ActivationRevision));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var rotating = new ManualResetEventSlim();
        var admitted = 0;
        var guarded = Task.Run(() => first.WithCustomerWaitingEvening("device", a.EveningId!, a.ActivationRevision, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("Guard release was not signaled."); }
            var view = journal.JoinCustomerWaiting("owner", "device", new(Id(), waitingEvent.Id, "profile"));
            Interlocked.Exchange(ref admitted, 1);
            return view;
        }));
        Check(entered.Wait(TimeSpan.FromSeconds(2)), "guard entered synchronous callback");
        var rotation = Task.Run(() =>
        {
            rotating.Set();
            var result = second.Activate("device", Guid.NewGuid(), "activate", "b-again", b.EveningId!, a.EveningId!);
            Check(Volatile.Read(ref admitted) == 1, "other store activation cannot commit before the guarded journal callback");
            return result;
        });
        try
        {
            Check(rotating.Wait(TimeSpan.FromSeconds(2)), "second store attempted rotation");
            await Task.Delay(150);
            Check(!rotation.IsCompleted, "SQLite writer reservation excludes another store's activation despite distinct instance gates");
            Expect("waiting_temporarily_unavailable", () => first.ReadCustomerWaitingEvening("device"));
            Check(availability.ReadCustomerWaitingEvening("device") == a, "availability remains a read-only snapshot while guarded admission is pending");
        }
        finally
        {
            release.Set();
            await Task.WhenAll(guarded, rotation).WaitAsync(TimeSpan.FromSeconds(5));
        }
        await guarded.WaitAsync(TimeSpan.FromSeconds(5));
        Check((await rotation.WaitAsync(TimeSpan.FromSeconds(5))).Status == 200, "rotation proceeds after synchronous journal commit and guard unwind");
        Expect("waiting_evening_changed", () => first.WithCustomerWaitingEvening("device", a.EveningId!, a.ActivationRevision,
            () => throw new InvalidOperationException("Stale callback ran.")));
        var current = first.ReadCustomerWaitingEvening("device");
        try
        {
            first.WithCustomerWaitingEvening("device", current.EveningId!, current.ActivationRevision, () => throw new IOException("Lost response probe."));
            throw new InvalidOperationException("Callback exception was swallowed.");
        }
        catch (IOException) { }
        var oldEvent = journal.ReadCustomerWaitingEvent("device")!;
        var currentEvent = first.WithCustomerWaitingEvent("device", current.EveningId!, current.ActivationRevision,
            () => journal.OpenCustomerWaiting("device", current.EveningId!, Id(), oldEvent.Id, oldEvent.Revision, current.ActivationRevision));
        var durableJoin = new CustomerWaitingJoin(Id(), currentEvent.Id, "profile");
        try
        {
            first.WithCustomerWaitingEvening("device", current.EveningId!, current.ActivationRevision, () =>
            {
                journal.JoinCustomerWaiting("owner", "device", durableJoin);
                throw new IOException("Response lost after durable journal commit.");
            });
        }
        catch (IOException) { }
        var committed = journal.ReadCustomerWaiting("owner", "device");
        Check(committed.Entry is not null && journal.JoinCustomerWaiting("owner", "device", durableJoin).Entry == committed.Entry
            && first.ReadCustomerWaitingEvening("device") == current,
            "memory reservation unwind cannot roll back a separate committed journal; lost response remains replayable");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { first.WithCustomerWaitingEvening("device", current.EveningId!, current.ActivationRevision, () => throw new InvalidOperationException("Cancelled callback ran."), cancelled.Token); }
            catch (OperationCanceledException) { }
        }
        using (var writer = Connection(configuration, memory: true))
        using (var reservation = writer.BeginTransaction())
        {
            Expect("waiting_temporarily_unavailable", () => first.WithCustomerWaitingEvening("device", current.EveningId!, current.ActivationRevision,
                () => throw new InvalidOperationException("Contended callback ran.")));
        }
        Check(second.Activate("device", Guid.NewGuid(), "activate", "after-unwind", a.EveningId!, current.EveningId!).Status == 200,
            "exceptions, cancellation and writer contention release gate and SQLite reservation");
    }

    private static async Task WalActivationReservationAsync(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var memory = new EveningMemoryStore(configuration, journal);
        memory.GetStatus("device");
        var a = memory.ReadCustomerWaitingEvening("device");
        var waitingEvent = memory.WithCustomerWaitingEvent("device", a.EveningId!, a.ActivationRevision,
            () => journal.OpenCustomerWaiting("device", a.EveningId!, Id(), activationRevision: a.ActivationRevision));
        Execute(configuration, memory: true, "PRAGMA journal_mode=WAL");
        Check(Text(configuration, true, "PRAGMA journal_mode") == "wal", "explicit WAL fixture exercises reservation rather than reader locking");
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); using var attempted = new ManualResetEventSlim();
        var admitted = 0;
        var guarded = Task.Run(() => memory.WithCustomerWaitingEvening("device", a.EveningId!, a.ActivationRevision, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) { throw new TimeoutException("WAL guard release was not signaled."); }
            var joined = journal.JoinCustomerWaiting("owner", "device", new(Id(), waitingEvent.Id, "profile"));
            Interlocked.Exchange(ref admitted, 1);
            return joined;
        }));
        Check(entered.Wait(TimeSpan.FromSeconds(2)), "WAL guard acquired writer reservation");
        var externalActivation = Task.Run(() =>
        {
            using var connection = Connection(configuration, memory: true);
            attempted.Set();
            using var transaction = connection.BeginTransaction();
            Check(Volatile.Read(ref admitted) == 1, "external WAL activation waits for the journal commit boundary");
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE evenings SET activation_revision=$revision WHERE device_id='device'";
            command.Parameters.AddWithValue("$revision", checked(a.ActivationRevision!.Value + 1));
            command.ExecuteNonQuery(); transaction.Commit();
        });
        try
        {
            Check(attempted.Wait(TimeSpan.FromSeconds(2)), "external WAL activation attempted transaction");
            await Task.Delay(150);
            Check(!externalActivation.IsCompleted, "WAL reader coexistence cannot bypass the immediate writer reservation");
        }
        finally
        {
            release.Set();
            await Task.WhenAll(guarded, externalActivation).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Expect("waiting_activation_changed", () => memory.WithCustomerWaitingEvening("device", a.EveningId!, a.ActivationRevision,
            () => throw new InvalidOperationException("Old WAL activation callback ran.")));
    }

    private static async Task ProximityTransactionAsync(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var boot = Id(); var firstProof = new CustomerWaitingProximity(boot, Id());
        journal.SetCustomerMode("device", true); journal.RegisterCustomerChallenge("device", new(1, boot, firstProof.Challenge, 0, "catalog", null));
        var request = new CustomerWaitingJoin(Id(), waitingEvent.Id, "profile");
        var joined = journal.JoinCustomerWaiting("owner", "device", request, firstProof);
        var freshProof = firstProof with { Challenge = Id() };
        journal.RegisterCustomerChallenge("device", new(1, boot, freshProof.Challenge, 0, "catalog", null));
        Expect("device_challenge_unavailable", () => journal.JoinCustomerWaiting("owner", "device", request, firstProof));
        Check(journal.JoinCustomerWaiting("owner", "device", request, freshProof) == joined,
            "fresh proximity is checked even on replay but is excluded from command fingerprint");
        var claim = Command(waitingEvent.Id, joined.Entry!);
        using var writer = Connection(configuration, memory: false);
        using var transaction = writer.BeginTransaction();
        using (var replace = writer.CreateCommand())
        {
            replace.Transaction = transaction; replace.CommandText = "UPDATE customer_devices SET challenge=$challenge WHERE device_id='device'";
            replace.Parameters.AddWithValue("$challenge", Id()); replace.ExecuteNonQuery();
        }
        using var started = new ManualResetEventSlim();
        var racing = Task.Run(() =>
        {
            started.Set();
            Expect("device_challenge_unavailable", () => journal.ClaimCustomerWaiting("owner", "device", claim, freshProof));
        });
        Check(started.Wait(TimeSpan.FromSeconds(2)), "claim waiting on actual journal writer transaction");
        await Task.Delay(100); transaction.Commit();
        await racing.WaitAsync(TimeSpan.FromSeconds(5));
        Check(journal.ReadCustomerWaiting("owner", "device").Entry!.Claim is null, "challenge replacement wins atomically before claim writes");
    }

    private static CustomerWaitingEntryCommand Command(string eventId, CustomerWaitingEntry entry) => new(Id(), eventId, entry.Id, entry.Revision);
    private static string Id() => Guid.NewGuid().ToString("D");
    private static IConfiguration Configuration(string root, string name) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?> { ["Backend:DatabasePath"] = Path.Combine(root, name + ".sqlite") }).Build();
    private static SqliteConnection Connection(IConfiguration configuration, bool memory)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = configuration["Backend:DatabasePath"] + (memory ? ".evening.sqlite" : ""), Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Execute(IConfiguration configuration, bool memory, string sql, params (string Key, object Value)[] parameters)
    {
        using var connection = Connection(configuration, memory); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (key, value) in parameters) { command.Parameters.AddWithValue(key, value); }
        command.ExecuteNonQuery();
    }
    private static string? Text(IConfiguration configuration, bool memory, string sql)
    {
        using var connection = Connection(configuration, memory); using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
    private static long Scalar(IConfiguration configuration, bool memory, string sql) => long.Parse(Text(configuration, memory, sql)!);
    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (CustomerSessionException error) when (error.Code == code) { }
    }
    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
}

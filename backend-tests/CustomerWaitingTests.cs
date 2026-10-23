using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class CustomerWaitingTests
{
    internal static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "customer-waiting-" + Id());
        Directory.CreateDirectory(root);
        try
        {
            await ConcurrentOwnersAsync(Configuration(root, "concurrency"));
            ReplayAndIsolation(Configuration(root, "replay"));
            LifecycleAndRestart(Configuration(root, "lifecycle"));
            AcceptedParticipation(Configuration(root, "accepted"));
            Revocations(Configuration(root, "revocation"));
            OperatorPrivacy(Configuration(root, "operator"));
            Capacity(Configuration(root, "capacity", new(2, 3, 12, 4)));
            OwnerReceiptCapacity(Configuration(root, "owner-capacity", new(2, 3, 12, 3)));
            ConfiguredRetention(Configuration(root, "retention", new(1, 2, 16, 4)));
            InvalidConfiguration(root);
            Console.WriteLine("PASS customer waiting: actual SQLite concurrent joins/claims, multi-phone dedup, ordered leave/skip, replay/conflicts/stale revisions, pause/expiry/close/restart, lease exclusion, tiny configured capacities, startup validation and revocation isolation");
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }

    private static async Task ConcurrentOwnersAsync(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new WaitingClock());
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var phones = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() =>
            journal.JoinCustomerWaiting("same-owner", "device", new(Id(), waitingEvent.Id, "profile")))));
        Check(phones.All(view => view.Entry!.Id == phones[0].Entry!.Id && view.Position == 1 && view.Count == 1),
            "two phones and retries retain one entry and position");
        var joins = await Task.WhenAll(Enumerable.Range(0, 20).Select(index => Task.Run(() =>
            journal.JoinCustomerWaiting("owner-" + index, "device", new(Id(), waitingEvent.Id, "profile-" + index)))));
        var sequences = joins.Select(view => view.Entry!.Sequence).Order().ToArray();
        Check(sequences.SequenceEqual(Enumerable.Range(2, 20).Select(value => (long)value)), "SQLite assigns unique monotonic order under contention");
        var head = phones[0].Entry!;
        var commands = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            try { return journal.ClaimCustomerWaiting("same-owner", "device", new(Id(), waitingEvent.Id, head.Id, head.Revision)); }
            catch (CustomerSessionException error) when (error.Code == "waiting_revision_conflict") { return null; }
        })).ToArray();
        var claims = await Task.WhenAll(commands);
        Check(claims.Count(result => result?.Entry?.Claim is not null) == 1, "exactly one concurrent explicit claim succeeds");
        var second = joins.OrderBy(view => view.Entry!.Sequence).First();
        var secondOwner = "owner-" + second.Entry!.ProfileId[8..];
        Expect("waiting_not_head", () => journal.ClaimCustomerWaiting(secondOwner, "device", Command(waitingEvent.Id, second.Entry)));
        var claimed = claims.Single(result => result is not null)!;
        journal.LeaveCustomerWaiting("same-owner", "device", Command(waitingEvent.Id, claimed.Entry!));
        Check(journal.ReadCustomerWaiting(secondOwner, "device").Position == 1, "leaving head deterministically advances the next owner");
        journal.CancelCustomerWaiting("device", Command(waitingEvent.Id, second.Entry));
        Check(journal.ReadCustomerWaiting(secondOwner, "device").Entry is null && journal.ReadCustomerWaiting(secondOwner, "device").Count == 19,
            "operator skip removes exactly the selected revision");
    }

    private static void ReplayAndIsolation(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new WaitingClock());
        var openKey = Id(); var evening = Id();
        var waitingEvent = journal.OpenCustomerWaiting("device", evening, openKey, activationRevision: 1);
        Check(journal.OpenCustomerWaiting("device", evening, openKey, activationRevision: 1) == waitingEvent, "open command replays immutably");
        Expect("waiting_request_conflict", () => journal.OpenCustomerWaiting("device", Id(), openKey, activationRevision: 1));
        Expect("waiting_id_invalid", () => journal.JoinCustomerWaiting("owner", "device", new("bad-key", waitingEvent.Id, "profile")));
        var join = new CustomerWaitingJoin(Id(), waitingEvent.Id, "profile");
        var original = journal.JoinCustomerWaiting("owner", "device", join);
        var exact = journal.JoinCustomerWaiting("owner", "device", join);
        Check(JsonSerializer.Serialize(original) == JsonSerializer.Serialize(exact), "accepted join returns exact stored result");
        Expect("waiting_request_conflict", () => journal.JoinCustomerWaiting("owner", "device", join with { ProfileId = "other-profile" }));
        var duplicate = journal.JoinCustomerWaiting("owner", "device", join with { RequestId = Id(), ProfileId = "other-profile" });
        Check(duplicate.Entry == original.Entry, "fresh key does not change profile or position of an active entry");
        Check(journal.ReadCustomerWaiting("stranger", "device").Entry is null, "privacy view contains only the requesting account entry");
        Expect("waiting_entry_unavailable", () => journal.LeaveCustomerWaiting("stranger", "device", Command(waitingEvent.Id, original.Entry!)));
        Expect("waiting_event_unavailable", () => journal.JoinCustomerWaiting("owner", "other-device", join with { RequestId = Id() }));
        var leave = Command(waitingEvent.Id, original.Entry!);
        journal.LeaveCustomerWaiting("owner", "device", leave);
        var rejoined = journal.JoinCustomerWaiting("owner", "device", join with { RequestId = Id() });
        Check(rejoined.Entry!.Id != original.Entry!.Id && rejoined.Entry.Sequence > original.Entry.Sequence, "rejoining gets a fresh monotonic identity");
        journal.JoinCustomerWaiting("owner", "device", join);
        journal.LeaveCustomerWaiting("owner", "device", leave);
        Check(journal.ReadCustomerWaiting("owner", "device").Entry!.Id == rejoined.Entry.Id, "old join and leave replay never resurrect or cancel a newer entry");
        Expect("waiting_revision_conflict", () => journal.LeaveCustomerWaiting("owner", "device", Command(waitingEvent.Id, rejoined.Entry) with { ExpectedRevision = 999 }));
        var eventRevision = journal.ReadCustomerWaitingEvent("device")!.Revision;
        Expect("waiting_revision_conflict", () => journal.ChangeCustomerWaitingEvent("device", waitingEvent.Id, Id(), eventRevision - 1, "paused"));
        Check(journal.ReadCustomerWaitingEvent("device")!.Revision == eventRevision, "rejected stale operator command does not mutate state");
    }

    private static void LifecycleAndRestart(IConfiguration configuration)
    {
        var clock = new WaitingClock();
        CustomerWaitingEvent waitingEvent;
        CustomerWaitingEntry entry;
        CustomerWaitingEntryCommand claimRequest;
        CustomerWaitingClaim firstClaim;
        using (var journal = new OperationJournal(configuration, clock))
        {
            waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
            var joinRequest = new CustomerWaitingJoin(Id(), waitingEvent.Id, "profile");
            entry = journal.JoinCustomerWaiting("owner", "device", joinRequest).Entry!;
            var current = journal.ReadCustomerWaitingEvent("device")!;
            var paused = journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), current.Revision, "paused");
            Expect("waiting_event_unavailable", () => journal.JoinCustomerWaiting("new-owner", "device", new(Id(), current.Id, "new-profile")));
            var replayedJoin = journal.JoinCustomerWaiting("owner", "device", joinRequest);
            Check(replayedJoin.Entry!.Id == entry.Id && replayedJoin.Entry.Sequence == entry.Sequence
                && journal.ReadCustomerWaiting("owner", "device").Count == 1,
                "paused event rejects fresh joins while accepted join keys replay without moving the entry");
            Expect("waiting_event_unavailable", () => journal.ClaimCustomerWaiting("owner", "device", Command(current.Id, entry)));
            journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), paused.Revision, "open");
            Execute(configuration, "INSERT INTO device_conversation_leases VALUES('device','existing-operation',NULL,'existing-conversation','boot')");
            Expect("device_busy", () => journal.ClaimCustomerWaiting("owner", "device", Command(current.Id, entry)));
            Execute(configuration, "DELETE FROM device_conversation_leases WHERE device_id='device'");
            claimRequest = Command(current.Id, entry);
            var claim = journal.ClaimCustomerWaiting("owner", "device", claimRequest);
            entry = claim.Entry!; firstClaim = entry.Claim!;
            Check(firstClaim.ExpiresAt == clock.GetUtcNow().ToUnixTimeSeconds() + 90, "claim TTL is bounded to 90 seconds");
            Check(journal.ActiveCustomerSession("device") is null, "claim creates no physical lease or conversation");
        }
        using (var journal = new OperationJournal(configuration, clock))
        {
            Check(journal.ReadCustomerWaiting("owner", "device").Entry!.Claim == firstClaim, "unredeemed claim survives process restart");
            clock.Advance(90);
            var expired = journal.ReadCustomerWaiting("owner", "device");
            Check(expired.Entry!.Claim is null && expired.Entry.State == "waiting" && expired.Position == 1,
                "expiry invalidates claim without removing waiting order");
            Check(journal.ClaimCustomerWaiting("owner", "device", claimRequest).Entry!.Claim == firstClaim,
                "old accepted claim replay does not renew its expired authority");
            var reclaimed = journal.ClaimCustomerWaiting("owner", "device", Command(waitingEvent.Id, expired.Entry));
            Check(reclaimed.Entry!.Claim!.Id != firstClaim.Id && reclaimed.Entry.Sequence == entry.Sequence, "explicit reclaim has a new token at the same position");
            Execute(configuration, "INSERT INTO device_conversation_leases VALUES('device','active-operation',NULL,'active-conversation','boot')");
            var current = journal.ReadCustomerWaitingEvent("device")!;
            var restarted = journal.OpenCustomerWaiting("device", Id(), Id(), current.Id, current.Revision, activationRevision: 1);
            var previous = journal.ReadCustomerWaiting("owner", "device", current.Id);
            Check(previous.Event!.State == "closed" && previous.Entry is null && previous.Count == 0, "restart closes all old waiting without migration");
            Check(restarted.Id != current.Id && journal.ReadCustomerWaiting("owner", "device").Entry is null, "restart creates immutable fresh event identity");
            Check(Scalar(configuration, "SELECT COUNT(*) FROM device_conversation_leases") == 1, "restart never releases an existing physical lease");
            Expect("waiting_event_closed", () => journal.JoinCustomerWaiting("owner", "device", new(Id(), current.Id, "profile")));
            var closed = journal.ChangeCustomerWaitingEvent("device", restarted.Id, Id(), restarted.Revision, "closed");
            Expect("waiting_event_closed", () => journal.ChangeCustomerWaitingEvent("device", closed.Id, Id(), closed.Revision, "open"));
            var next = journal.OpenCustomerWaiting("device", Id(), Id(), closed.Id, closed.Revision, activationRevision: 1);
            Expect("waiting_event_exists", () => journal.OpenCustomerWaiting("device", Id(), Id(), closed.Id, closed.Revision, activationRevision: 1));
            Check(journal.ReadCustomerWaitingEvent("device")!.Id == next.Id, "stale restart cannot displace a newer live event");
        }
    }

    private static void AcceptedParticipation(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new WaitingClock());
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var entry = journal.JoinCustomerWaiting("owner", "device", new(Id(), waitingEvent.Id, "profile")).Entry!;
        var claimed = journal.ClaimCustomerWaiting("owner", "device", Command(waitingEvent.Id, entry));
        // Represent the future admission transaction's accepted state without granting a lease in this feature.
        Execute(configuration, "UPDATE customer_waiting_entries SET state='accepted',revision=revision+1,operation_id='accepted-operation'");
        var accepted = journal.ReadCustomerWaiting("owner", "device").Entry!;
        Expect("waiting_entry_unavailable", () => journal.LeaveCustomerWaiting("owner", "device", Command(waitingEvent.Id, accepted)));
        Expect("waiting_entry_unavailable", () => journal.CancelCustomerWaiting("device", Command(waitingEvent.Id, accepted)));
        var current = journal.ReadCustomerWaitingEvent("device")!;
        journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), current.Revision, "closed");
        Check(journal.ReadCustomerWaiting("owner", "device", current.Id).Entry!.State == "accepted"
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_entries WHERE operation_id='accepted-operation'") == 1,
            "close cannot cancel or alter accepted conversation ownership");
    }

    private static void Revocations(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new WaitingClock());
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var revokedJoin = new CustomerWaitingJoin(Id(), waitingEvent.Id, "revoked-profile");
        var revoked = journal.JoinCustomerWaiting("revoked-owner", "device", revokedJoin);
        var next = journal.JoinCustomerWaiting("other-owner", "device", new(Id(), waitingEvent.Id, "other-profile"));
        var revokedClaim = Command(waitingEvent.Id, revoked.Entry!);
        journal.ClaimCustomerWaiting("revoked-owner", "device", revokedClaim);
        journal.RevokeCustomerProfile("revoked-owner", "revoked-profile");
        Expect("customer_profile_revoked", () => journal.JoinCustomerWaiting("revoked-owner", "device", revokedJoin));
        Expect("customer_profile_revoked", () => journal.ClaimCustomerWaiting("revoked-owner", "device", revokedClaim));
        var revokedCurrent = journal.ReadCustomerWaiting("revoked-owner", "device");
        Check(revokedCurrent.Entry is null, "revoked entry is hidden from guest reads");
        var visible = journal.ReadCustomerWaiting("other-owner", "device");
        Check(visible.Position == 1 && visible.Count == 1, "revoked profiles cannot block the eligible head");
        var newProfile = journal.JoinCustomerWaiting("revoked-owner", "device", new(Id(), waitingEvent.Id, "new-profile"));
        Check(newProfile.Position == 2 && newProfile.Entry!.Id != revoked.Entry!.Id, "a new valid profile can rejoin after old profile revocation");
        journal.ClaimCustomerWaiting("other-owner", "device", Command(waitingEvent.Id, next.Entry!));
        journal.RevokeCustomerProfile("other-owner");
        Expect("customer_profile_revoked", () => journal.ReadCustomerWaiting("other-owner", "device"));
        Expect("customer_profile_revoked", () => journal.JoinCustomerWaiting("other-owner", "device", new(Id(), waitingEvent.Id, "new-profile")));
        var unrelated = journal.JoinCustomerWaiting("unrelated-owner", "device", new(Id(), waitingEvent.Id, "other-profile"));
        Check(unrelated.Position == 2, "account tombstone remains isolated from another account using same technical profile id");
    }

    private static void Capacity(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new WaitingClock());
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var originalRequest = new CustomerWaitingJoin(Id(), waitingEvent.Id, "profile");
        var original = journal.JoinCustomerWaiting("owner", "device", originalRequest);
        for (var index = 1; index < journal.WaitingLimits.EntriesPerEvent; index++)
        {
            journal.JoinCustomerWaiting("owner-" + index, "device", new(Id(), waitingEvent.Id, "profile"));
        }
        Expect("waiting_entry_capacity", () => journal.JoinCustomerWaiting("overflow", "device", new(Id(), waitingEvent.Id, "profile")));
        journal.LeaveCustomerWaiting("owner", "device", Command(waitingEvent.Id, original.Entry!));
        Expect("waiting_entry_capacity", () => journal.JoinCustomerWaiting("overflow", "device", new(Id(), waitingEvent.Id, "profile")));
        // Exercise finite receipt retention through real accepted commands, without seeding synthetic rows.
        while (Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_receipts") < journal.WaitingLimits.ReceiptsPerDevice)
        {
            var current = journal.ReadCustomerWaitingEvent("device")!;
            journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), current.Revision, "open");
        }
        Expect("waiting_receipt_capacity", () => journal.JoinCustomerWaiting("overflow", "device", new(Id(), waitingEvent.Id, "profile")));
        Check(journal.JoinCustomerWaiting("owner", "device", originalRequest).Entry == original.Entry, "capacity never evicts accepted idempotency keys");
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_entries") == journal.WaitingLimits.EntriesPerEvent
            && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_receipts") == journal.WaitingLimits.ReceiptsPerDevice,
            "failed commands do not grow bounded storage");
        var firstEvent = journal.OpenCustomerWaiting("event-capacity-device", Id(), Id(), activationRevision: 1);
        var eventCapacity = journal.OpenCustomerWaiting("event-capacity-device", Id(), Id(), firstEvent.Id, firstEvent.Revision, activationRevision: 1);
        Expect("waiting_event_capacity", () => journal.OpenCustomerWaiting("event-capacity-device", Id(), Id(), eventCapacity.Id, eventCapacity.Revision, activationRevision: 1));
        Check(journal.ReadCustomerWaitingEvent("event-capacity-device") == eventCapacity, "event capacity rollback preserves the current event");
    }

    private static void OperatorPrivacy(IConfiguration configuration)
    {
        var clock = new WaitingClock();
        using var journal = new OperationJournal(configuration, clock);
        Check(journal.ReadCustomerWaitingOperator("device") == new CustomerWaitingOperatorView(null, null, 0), "operator projection is empty before waiting activation");
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var first = journal.JoinCustomerWaiting("private-owner", "device", new(Id(), waitingEvent.Id, "private-profile")).Entry!;
        var second = journal.JoinCustomerWaiting("other-owner", "device", new(Id(), waitingEvent.Id, "other-profile")).Entry!;
        var visible = journal.ReadCustomerWaitingOperator("device");
        Check(visible.Head == new CustomerWaitingHead(first.Id, first.Revision, "waiting") && visible.Count == 2,
            "operator gets only the eligible exact head and logical waiting count");
        var claimed = journal.ClaimCustomerWaiting("private-owner", "device", Command(waitingEvent.Id, first)).Entry!;
        var serialized = JsonSerializer.Serialize(journal.ReadCustomerWaitingOperator("device"));
        Check(!serialized.Contains("private-owner") && !serialized.Contains("private-profile")
            && !serialized.Contains("other-owner") && !serialized.Contains("other-profile") && !serialized.Contains(claimed.Claim!.Id),
            "operator projection excludes owner/profile identifiers and claim authority");
        clock.Advance(90);
        Check(journal.ReadCustomerWaitingOperator("device").Head == new CustomerWaitingHead(claimed.Id, claimed.Revision, "waiting"),
            "operator expiry projection matches the guest's retained waiting order and exact revision");
        journal.RevokeCustomerProfile("private-owner", "private-profile");
        visible = journal.ReadCustomerWaitingOperator("device");
        Check(visible.Head!.EntryId == second.Id && visible.Count == 1, "revoked head is excluded using the same eligibility rule as guest position and claim");
        journal.CancelCustomerWaiting("device", Command(waitingEvent.Id, second));
        var current = journal.ReadCustomerWaitingEvent("device")!;
        journal.ChangeCustomerWaitingEvent("device", current.Id, Id(), current.Revision, "closed");
        visible = journal.ReadCustomerWaitingOperator("device");
        Check(visible.Event!.State == "closed" && visible.Head is null && visible.Count == 0,
            "operator projection retains the closed marker without an inventory of historical guests");
    }

    private static CustomerWaitingEntryCommand Command(string waitingEvent, CustomerWaitingEntry entry)
        => new(Id(), waitingEvent, entry.Id, entry.Revision);

    private static void OwnerReceiptCapacity(IConfiguration configuration)
    {
        using var journal = new OperationJournal(configuration, new WaitingClock());
        var waitingEvent = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
        var originalRequest = new CustomerWaitingJoin(Id(), waitingEvent.Id, "profile");
        var original = journal.JoinCustomerWaiting("owner", "device", originalRequest);
        for (var index = 1; index < journal.WaitingLimits.ReceiptsPerAccountDevice; index++)
        {
            var duplicate = journal.JoinCustomerWaiting("owner", "device", originalRequest with { RequestId = Id() });
            Check(duplicate.Entry == original.Entry, "fresh-key joins retain entry while consuming only the owner's bounded receipts");
        }
        Expect("waiting_owner_receipt_capacity", () => journal.JoinCustomerWaiting("owner", "device", originalRequest with { RequestId = Id() }));
        Check(journal.JoinCustomerWaiting("owner", "device", originalRequest) == original, "owner capacity preserves accepted-key replay");
        var other = journal.JoinCustomerWaiting("other-owner", "device", new(Id(), waitingEvent.Id, "other-profile"));
        Check(other.Count == 2 && other.Position == 2, "one guest cannot exhaust other accounts' command budgets");
        Check(Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_receipts") == journal.WaitingLimits.ReceiptsPerAccountDevice + 2,
            "rejected quota request creates no receipt and operator/other-owner receipts remain isolated");
    }

    private static void ConfiguredRetention(IConfiguration configuration)
    {
        CustomerWaitingEvent first;
        var openRequest = Id(); var evening = Id();
        using (var journal = new OperationJournal(configuration, new WaitingClock()))
        {
            first = journal.OpenCustomerWaiting("device", evening, openRequest, activationRevision: 1);
            var originalLimits = journal.WaitingLimits;
            configuration[CustomerWaitingLimits.ConfigurationPrefix + "EntriesPerEvent"] = "1";
            journal.JoinCustomerWaiting("owner-a", "device", new(Id(), first.Id, "profile"));
            journal.JoinCustomerWaiting("owner-b", "device", new(Id(), first.Id, "profile"));
            Check(journal.WaitingLimits == originalLimits && journal.WaitingLimits.EntriesPerEvent == 2,
                "journal captures immutable validated limits once, without runtime configuration changes");
            Expect("waiting_entry_capacity", () => journal.JoinCustomerWaiting("owner-c", "device", new(Id(), first.Id, "profile")));
            first = journal.ReadCustomerWaitingEvent("device")!;
            Expect("waiting_event_capacity", () => journal.OpenCustomerWaiting("device", Id(), Id(), first.Id, first.Revision, activationRevision: 1));
        }
        configuration[CustomerWaitingLimits.ConfigurationPrefix + "EventsPerDevice"] = "2";
        using (var journal = new OperationJournal(configuration, new WaitingClock()))
        {
            var next = journal.OpenCustomerWaiting("device", Id(), Id(), first.Id, first.Revision, activationRevision: 1);
            Check(next.Id != first.Id && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_events") == 2
                && Scalar(configuration, "SELECT COUNT(*) FROM customer_waiting_entries") == 2,
                "manual configuration increase at restart admits another event while retaining existing history");
            Check(journal.OpenCustomerWaiting("device", evening, openRequest, activationRevision: 1).Id == first.Id
                && journal.ReadCustomerWaitingEvent("device")!.Id == next.Id,
                "raising technical limits never evicts accepted keys or resurrects their old event");
        }
    }

    private static void InvalidConfiguration(string root)
    {
        var invalid = new (string Name, string Value)[]
        {
            ("EventsPerDevice", "0"), ("EventsPerDevice", "65537"),
            ("EntriesPerEvent", "-1"), ("EntriesPerEvent", "8193"),
            ("ReceiptsPerDevice", "0"), ("ReceiptsPerDevice", "1048577"),
            ("ReceiptsPerAccountDevice", "0"), ("ReceiptsPerAccountDevice", "65537"),
            ("EventsPerDevice", "1.5"), ("EntriesPerEvent", "invalid"),
            ("ReceiptsPerDevice", "999999999999999"), ("ReceiptsPerAccountDevice", "")
        };
        for (var index = 0; index < invalid.Length; index++)
        {
            var configuration = Configuration(root, "invalid-" + index);
            configuration[CustomerWaitingLimits.ConfigurationPrefix + invalid[index].Name] = invalid[index].Value;
            RejectInvalidConfiguration(configuration);
            var databasePath = configuration["Backend:DatabasePath"]!;
            Check(!File.Exists(databasePath) && !File.Exists(databasePath + ".instance.lock"),
                "invalid technical limits reject startup before database, lock or queue writes");
        }
        var ownerExceedsDevice = Configuration(root, "invalid-owner", new(1, 1, 2, 3));
        RejectInvalidConfiguration(ownerExceedsDevice);
        Check(!File.Exists(ownerExceedsDevice["Backend:DatabasePath"]), "account receipt quota cannot exceed the device quota");
        using (var minimum = new OperationJournal(Configuration(root, "minimum", new(1, 1, 1, 1)), new WaitingClock()))
        {
            var request = Id(); var evening = Id();
            var created = minimum.OpenCustomerWaiting("device", evening, request, activationRevision: 1);
            Check(minimum.OpenCustomerWaiting("device", evening, request, activationRevision: 1) == created, "limits of one are valid and accepted keys replay after exhaustion");
        }
        using var defaults = new OperationJournal(Configuration(root, "defaults"), new WaitingClock());
        Check(defaults.WaitingLimits == new CustomerWaitingLimits(4096, 256, 131072, 1024), "finite production defaults are explicit and stable");
        var maximumLimits = new CustomerWaitingLimits(65536, 8192, 1048576, 65536);
        using var maximum = new OperationJournal(Configuration(root, "maximum", maximumLimits), new WaitingClock());
        Check(maximum.WaitingLimits == maximumLimits, "inclusive finite upper bounds are valid without allocating capacity-sized rows");
        var existing = Configuration(root, "existing-invalid", new(2, 2, 8, 4));
        using (var journal = new OperationJournal(existing, new WaitingClock()))
        {
            var current = journal.OpenCustomerWaiting("device", Id(), Id(), activationRevision: 1);
            journal.JoinCustomerWaiting("owner", "device", new(Id(), current.Id, "profile"));
        }
        existing[CustomerWaitingLimits.ConfigurationPrefix + "EntriesPerEvent"] = "invalid";
        RejectInvalidConfiguration(existing);
        Check(Scalar(existing, "SELECT COUNT(*) FROM customer_waiting_entries") == 1
            && Scalar(existing, "SELECT revision FROM customer_waiting_events") == 2,
            "invalid restart configuration leaves an existing journal's queue entries and revisions unchanged");
    }

    private static void RejectInvalidConfiguration(IConfiguration configuration)
    {
        try
        {
            using var journal = new OperationJournal(configuration, new WaitingClock());
            throw new InvalidOperationException("Invalid waiting limits were accepted.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(CustomerWaitingLimits.ConfigurationPrefix, StringComparison.Ordinal)) { }
    }

    private static string Id() => Guid.NewGuid().ToString("D");
    private static IConfiguration Configuration(string root, string name, CustomerWaitingLimits? limits = null)
    {
        var settings = new Dictionary<string, string?> { ["Backend:DatabasePath"] = Path.Combine(root, name + ".sqlite") };
        if (limits is not null)
        {
            settings[CustomerWaitingLimits.ConfigurationPrefix + nameof(limits.EventsPerDevice)] = limits.EventsPerDevice.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[CustomerWaitingLimits.ConfigurationPrefix + nameof(limits.EntriesPerEvent)] = limits.EntriesPerEvent.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[CustomerWaitingLimits.ConfigurationPrefix + nameof(limits.ReceiptsPerDevice)] = limits.ReceiptsPerDevice.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[CustomerWaitingLimits.ConfigurationPrefix + nameof(limits.ReceiptsPerAccountDevice)] = limits.ReceiptsPerAccountDevice.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }
    private static void Execute(IConfiguration configuration, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]);
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static long Scalar(IConfiguration configuration, string sql)
    {
        using var connection = new SqliteConnection("Data Source=" + configuration["Backend:DatabasePath"]);
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!;
    }
    private static void Expect(string code, Action action)
    {
        try { action(); throw new InvalidOperationException("Expected " + code); }
        catch (CustomerSessionException error) when (error.Code == code) { }
    }
    private static void Check(bool value, string message)
    {
        if (!value) { throw new InvalidOperationException(message); }
    }
    private sealed class WaitingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(int seconds) => now = now.AddSeconds(seconds);
    }
}

using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class ChronicleTests
{
    internal static void Run()
    {
        CommonShowLifecycleTests.Run();
        MemoryShowDeletionTests.RunAsync().GetAwaiter().GetResult();
        var directory = Path.Combine(Path.GetTempPath(), "kufr-chronicle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var database = Path.Combine(directory, "journal.sqlite");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = database,
            ["Backend:Memory:Enabled"] = "true",
            ["Backend:Memory:LongTermPersonalEnabled"] = "true"
        }).Build();
        string originalEvening;
        int finalRevision;
        try
        {
            using (var journal = new OperationJournal(config, TimeProvider.System))
            {
                var memory = new EveningMemoryStore(config, journal);
                Check(memory.ReadActiveEveningId("new-device") is null, "read never initializes an evening");
                var evening = memory.GetStatus("device-a").EveningId;
                originalEvening = evening;
                var empty = memory.ReadChronicle("device-a", evening)!;
                Check(empty.CompletedVisits == 0 && empty.ConfirmedPresentations == 0 && empty.CompletedDuels == 0
                    && empty.MostPopularSlots.Count == 0 && empty.Narrative.Contains("nulu"), "zero state has factual authorial narrative without a winner");
                Check(empty.Wands.All(wand => wand.OfficialName.StartsWith("Přihrádka ")),
                    "new chronicles aggregate physical slots without inventing catalogue identities for generated assignments");
                Check(memory.ReadChronicle("device-b", evening) is null, "evening cannot be read through a different device");
                Check(memory.CaptureShowVisit("device-a", "alice") == evening, "visit captures original evening");
                Check(memory.CaptureShowVisit("device-a", "bob") == evening, "second visit starts independently");
                Check(memory.Append("device-a", evening, "private-op", "alice", "Jmenuji se Petr. Soukromé proroctví: tajemství.", "Soukromý příběh."), "personal transcript stored separately");
                Check(memory.ReadContext("device-a", "bob", "Jmenuji se Petr").Transcript == "", "same display name does not merge guests");
                var eventA = new ShowMemoryEvent("outcome-a", ShowMemoryEventKind.PresentationConfirmed,
                    "device-a", evening, "physical-a", 1, "device_outcome", "alice", 1, "completed_by_timing");
                Check(memory.RecordShowEvent(eventA) && memory.RecordShowEvent(eventA), "outcome retry is idempotent");
                Check(!memory.RecordShowEvent(eventA with { Slot = 2 }), "same outcome cannot change catalog wand");
                Check(memory.RecordShowEvent(new("alice-done", ShowMemoryEventKind.VisitCompleted,
                    "device-a", evening, "alice", 1, "server_completed", "alice")), "completed visit accepted");
                Check(!memory.RecordShowEvent(new("alice-done-again", ShowMemoryEventKind.VisitCompleted,
                    "device-a", evening, "arbitrary-source", 1, "server_completed", "alice")), "new event ID cannot double-count one visit");
                Check(memory.RecordShowEvent(new("bob-stopped", ShowMemoryEventKind.VisitInterrupted,
                    "device-a", evening, "bob", 1, "operator_stop", "bob")), "interrupted visit recorded");
                Check(!memory.RecordShowEvent(new("bob-outcome", ShowMemoryEventKind.PresentationConfirmed,
                    "device-a", evening, "physical-b", 1, "device_outcome", "bob", 2, "completed_by_timing")), "cancelled visit cannot become confirmed presentation");
                Check(!memory.RecordShowEvent(eventA with { EventId = "bad-evidence", SourceId = "physical-x", Evidence = "model_guessed" }), "model assertion is not physical evidence");
                memory.CaptureShowVisit("device-a", "carol");
                Check(memory.RecordShowEvent(new("outcome-c", ShowMemoryEventKind.PresentationConfirmed,
                    "device-a", evening, "physical-c", 1, "device_outcome", "carol", 2, "completed_by_timing")), "second wand presentation");
                var duel = new ShowMemoryEvent("duel-a", ShowMemoryEventKind.DuelCompleted,
                    "device-a", evening, "duel-a", 1, "duel_completed");
                Check(memory.RecordShowEvent(duel) && memory.RecordShowEvent(duel), "duel completion idempotent");
                Check(memory.RecordShowEvent(new("identity-a", ShowMemoryEventKind.IdentityCompleted,
                    "device-a", evening, "keepsake-a", 1, "model_claims_999999_presentations")), "identity lifecycle event");
                var running = memory.ReadChronicle("device-a", evening)!;
                Check(running.CompletedVisits == 1 && running.ConfirmedPresentations == 2 && running.CompletedDuels == 1
                    && running.InterruptedVisits == 1 && running.UnresolvedVisits == 1, "only authoritative completed outcomes counted");
                Check(running.MostPopularSlots.SequenceEqual(new[] { 1, 2 }) && running.Narrative.Contains("remíza"), "tie is explicit");
                var publicJson = JsonSerializer.Serialize(running);
                Check(!publicJson.Contains("tajemství") && !publicJson.Contains("Soukromý") && !publicJson.Contains("alice")
                    && !publicJson.Contains("keepsake-a") && !publicJson.Contains("999999"),
                    "public projection excludes private prose, source identities and unsupported model statistics");
                Check(!memory.RecordEveningJokeUse("device-a", evening, "carol", "invented_joke"), "joke IDs are allowlisted");
                Check(memory.BuildEveningLore("device-a", evening).Motifs.Count <= 3, "Live lore bounded at three safe motifs");
                Check(memory.RecordEveningJokeUse("device-a", evening, "carol", "wand_room_1"), "documented joke use");
                Check(memory.RecordEveningJokeUse("device-a", evening, "carol", "wand_room_1"), "same use retry does not increment cooldown");
                for (var visit = 1; visit <= 3; visit++)
                {
                    memory.CaptureShowVisit("device-a", "cooldown-" + visit);
                    Check(!memory.BuildEveningLore("device-a", evening).Motifs.Any(m => m.Id == "wand_room_1"), "three subsequent visits observe cooldown");
                    Check(!memory.RecordEveningJokeUse("device-a", evening, "cooldown-" + visit, "wand_room_1"), "cooldown also enforced at write boundary");
                }
                memory.CaptureShowVisit("device-a", "cooldown-4");
                Check(memory.BuildEveningLore("device-a", evening).Motifs.Any(m => m.Id == "wand_room_1"), "fourth later visit can use motif again");
                Check(memory.RecordEveningJokeUse("device-a", evening, "cooldown-4", "wand_room_1"), "second documented use");
                for (var visit = 5; visit <= 8; visit++) { memory.CaptureShowVisit("device-a", "cooldown-" + visit); }
                Check(memory.RecordEveningJokeUse("device-a", evening, "cooldown-8", "wand_room_1"), "third documented use");
                for (var visit = 9; visit <= 12; visit++) { memory.CaptureShowVisit("device-a", "cooldown-" + visit); }
                Check(!memory.BuildEveningLore("device-a", evening).Motifs.Any(m => m.Id == "wand_room_1")
                    && !memory.RecordEveningJokeUse("device-a", evening, "cooldown-12", "wand_room_1"), "motif has maximum three uses per evening");
                VerifyOwnReturn(memory, evening);
                var ended = memory.EndShowEvening("device-a", evening)!;
                Check(!ended.Provisional && ended.Revision == 1 && ended.UnresolvedVisits > 0 && ended.Retention == "manual_only", "end snapshots pending work and retains archive");
                Check(memory.EndShowEvening("device-a", evening)!.Revision == 1, "repeated closure never creates duplicate edition");
                Check(memory.CaptureShowVisit("device-a", "after-close") is null, "closed evening rejects new visit");
                Check(memory.CaptureShowVisit("device-a", "carol") == evening, "already active physical operation retains original capture");
                var next = memory.Create("device-a", Guid.NewGuid(), "test-create", "hash", evening, "Další večer").Response!.ActiveEveningId;
                Check(memory.CaptureShowVisit("device-a", "next-guest") == next, "new evening admits clean visitor");
                Check(memory.BuildEveningLore("device-a", next).Motifs.Count == 0, "next evening contains no previous lore");
                Check(memory.RecordShowEvent(new("carol-done", ShowMemoryEventKind.VisitCompleted,
                    "device-a", evening, "carol", 1, "late_completed", "carol")), "late completion accepted in captured original evening");
                Check(memory.ReadChronicle("device-a", evening)!.Revision == 2
                    && memory.ReadChronicle("device-a", evening)!.CompletedVisits == 2
                    && memory.ReadChronicle("device-a", next)!.CompletedVisits == 0, "late outcome creates edition without contaminating new evening");
                Check(!memory.RecordShowEvent(new("cross-evening", ShowMemoryEventKind.VisitCompleted,
                    "device-a", next, "carol", 1, "late_completed", "carol")), "captured conversation cannot migrate evenings");
                memory.RevokeShowSource("device-a", "physical-a");
                var redacted = memory.ReadChronicle("device-a", evening)!;
                Check(redacted.Revision == 3 && redacted.ConfirmedPresentations == 1
                    && redacted.MostPopularSlots.SequenceEqual(new[] { 2 }), "manual source deletion recalculates aggregates and publishes new edition");
                Check(!memory.RecordShowEvent(eventA with { EventId = "stale-job-restore" }), "old job cannot resurrect tombstoned source");
                memory.RevokeShowSource("device-a", "physical-a");
                Check(memory.ReadChronicle("device-a", evening)!.Revision == 3, "source deletion retry is idempotent");
                using (var connection = new SqliteConnection($"Data Source={database}.evening.sqlite"))
                {
                    connection.Open();
                    using var query = connection.CreateCommand();
                    query.CommandText = "SELECT COUNT(*) FROM show_chronicle_editions WHERE withdrawn=0";
                    Check(Convert.ToInt32(query.ExecuteScalar()) == 1, "old editions with revoked counts are withdrawn");
                }
                finalRevision = memory.RegenerateChronicle("device-a", evening)!.Revision;
                Check(finalRevision == 4, "explicit authorized regeneration creates edition");
                var imported = Guid.NewGuid().ToString("D");
                Check(memory.EnsureImportedShowEvening("device-a", imported, "Offline večer")
                    && memory.ReadActiveEveningId("device-a") == next, "offline import registers original evening without changing active visibility");
            }
            using (var journal = new OperationJournal(config, TimeProvider.System))
            {
                var memory = new EveningMemoryStore(config, journal);
                Check(memory.ReadChronicle("device-a", originalEvening)!.Revision == finalRevision, "restart preserves manual-only archived editions");
                Check(!memory.RecordShowEvent(new("restored", ShowMemoryEventKind.PresentationConfirmed,
                    "device-a", originalEvening, "physical-a", 1, "device_outcome", "alice", 1, "completed_by_timing")), "restart preserves source tombstone");
            }
            Console.WriteLine("Chronicle tests passed.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void VerifyOwnReturn(EveningMemoryStore memory, string evening)
    {
        var guestId = Guid.NewGuid().ToString("D");
        var verified = memory.SetGuestPermission("device-a", "alice", guestId, "admin_verified_claim", true, true, false, 0)!;
        var access = new MemoryAccess("device-a", evening, "alice", "ollivander", verified);
        var saved = memory.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.Personal,
            MemoryKind.UserReportedFact, "Host rád plánuje.", "explicit_owner_save"));
        Check(saved is not null, "verified owner can explicitly save personal memory");
        var stranger = new MemoryAccess("device-a", evening, "same-name", "ollivander", null);
        Check(memory.LocalMemory(stranger, MemoryScope.Personal).Count == 0, "same named new guest receives no personal history");
        var ownReturn = memory.SetGuestPermission("device-a", "own-return", guestId, "admin_verified_recovery", true, true, false, 0)!;
        var returned = stranger with { ConversationId = "own-return", Guest = ownReturn };
        Check(memory.LocalMemory(returned, MemoryScope.Personal).Count == 1, "verified permitted own return receives own memory");
        Check(memory.LocalMemory(returned with { DeviceId = "device-b" }, MemoryScope.Personal).Count == 0, "verified own memory still device-scoped");
        memory.SetGuestPermission("device-a", "own-return", guestId, "admin_revoked_return", true, false, false, 1);
        Check(memory.LocalMemory(returned, MemoryScope.Personal).Count == 0, "operator can revoke return link without deleting evening archive");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Chronicle regression: " + message); }
    }
}

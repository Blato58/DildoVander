using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.Extensions.Configuration;
using System.Text.Json;

internal static class KeepsakeOutcomeJournalTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv10-outcome-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new OutcomeClock();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"), ["Keepsake:Enabled"] = "true" }).Build();
            using var journal = new OperationJournal(configuration, clock);
            var correlation = new TurnCorrelation("device-a", Guid.NewGuid(), Guid.NewGuid().ToString("D"), Guid.NewGuid(), 3, 10);
            var proposal = new SelectionProposal(Guid.NewGuid().ToString("D"), correlation.ConversationId,
                correlation.ClientTurnId, PartyWandCatalog.Version, 10, 2, "Private reasoning must not enter outcome.",
                correlation.BootId, 3, clock.GetUtcNow().AddMinutes(1));
            var reservation = journal.Reserve("device-a", "/fixture", Guid.NewGuid(), new string('a', 64), correlation);
            var eveningId = Guid.NewGuid().ToString("D");
            Check(journal.MarkSelectionCompleted(reservation.OperationId, proposal, eveningId), "proposal completion committed");
            Check(journal.ReadKeepsakeEvening("device-a", reservation.OperationId) == eveningId,
                "captured evening is private immutable outbox metadata");
            Check(journal.ReadKeepsakeEvening("device-b", reservation.OperationId) is null, "evening metadata obeys producer isolation");
            Check(!JsonSerializer.Serialize(proposal).Contains("EveningId", StringComparison.Ordinal),
                "public selection proposal contract does not gain private evening metadata");
            Check(journal.ReadKeepsakeSelection("device-a", reservation.OperationId) == proposal with { Reason = "" }, "only technical proposal archived");
            Check(journal.ReadKeepsakeSelection("device-b", reservation.OperationId) is null, "producer owner is enforced");
            var outcome = Guid.NewGuid().ToString("D");
            Check(journal.ConfirmKeepsakeOutcome("device-a", reservation.OperationId, outcome, "completed_by_timing", "first") == outcome,
                "explicit physical outcome recorded");
            Check(journal.ConfirmKeepsakeOutcome("device-a", reservation.OperationId, Guid.NewGuid().ToString("D"), "completed_by_timing", "first") == outcome,
                "duplicate physical delivery retains one outcome");
            try { journal.ConfirmKeepsakeOutcome("device-a", reservation.OperationId, outcome, "completed_by_timing", "different"); throw new Exception("changed outcome accepted"); }
            catch (KeepsakeException e) when (e.Code == "outcome_conflict") { }
            clock.Utc = clock.Utc.AddDays(2); journal.PurgeExpired();
            Check(journal.ReadKeepsakeSelection("device-a", reservation.OperationId) is not null, "ordinary journal TTL cannot remove accepted outcome");
            var cancelled = journal.Reserve("device-a", "/fixture", Guid.NewGuid(), new string('b', 64), correlation with { ClientTurnId = Guid.NewGuid() });
            journal.CancelOperation("device-a", cancelled.OperationId, Guid.NewGuid());
            Check(!journal.MarkSelectionCompleted(cancelled.OperationId, proposal), "cancelled proposal cannot enter outcome outbox");
            Check(journal.ReadKeepsakeSelection("device-a", cancelled.OperationId) is null, "no cancelled issuance");
            Check(PartyWandCatalog.Available(63).Select(w => w.StableId).Distinct().Count() == 6, "explicit independent wand identities");
            Check(!JsonSerializer.Serialize(PartyWandCatalog.Available(63)).Contains("StableId", StringComparison.Ordinal),
                "DV10 identities do not change the DV01 candidate provider payload");
            Console.WriteLine("Keepsake outcome journal tests passed: correlation, atomic outbox, producer isolation, replay, cancellation and independent retention.");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool condition, string detail) { if (!condition) { throw new Exception(detail); } }
    private sealed class OutcomeClock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}

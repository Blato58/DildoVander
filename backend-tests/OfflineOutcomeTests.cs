using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class OfflineOutcomeTests
{
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "show-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
                ["Keepsake:DataRoot"] = Path.Combine(root, "archive"),
                ["Keepsake:Enabled"] = "true"
            }).Build();
            using var journal = new OperationJournal(config, TimeProvider.System);
            var memory = new EveningMemoryStore(config, journal);
            var keepsakes = new KeepsakeStore(config, TimeProvider.System);
            var store = new OfflineOutcomeStore(config, journal, keepsakes, memory);
            var active = memory.GetStatus("device-a").EveningId;
            var outcome = new OfflineOutcome(Guid.NewGuid().ToString("D"), "device-a",
                Guid.NewGuid().ToString("D"), 1, Guid.NewGuid().ToString("D"), null,
                "offline_scripted", 1, PartyWandCatalog.Version, 500, 5000,
                "boot_monotonic", "completed_by_timing");
            var first = store.Import("device-a", outcome, "device_report");
            var replay = store.Import("device-a", outcome, "device_report");
            Check(!first.Duplicate && replay.Duplicate && first.Locator == replay.Locator, "one archive result per outcome");
            Check(first.EveningId != active && memory.ReadActiveEveningId("device-a") == active,
                "import never activates or changes server evening");
            var snapshot = memory.ReadChronicle("device-a", first.EveningId)!;
            Check(snapshot.CompletedVisits == 1 && snapshot.ConfirmedPresentations == 1, "deduplicated chronicle facts");
            Expect("offline_outcome_conflict", () => store.Import("device-a", outcome with { Compartment = 2 }, "device_report"));
            Expect("offline_outcome_not_eligible", () => store.Import("device-b", outcome, "device_report"));
            Expect("offline_outcome_not_eligible", () => store.Import("device-a", outcome with { EveningId = active }, "device_report"));
            Expect("offline_outcome_not_eligible", () => store.Import("device-a", outcome with { CatalogVersion = "old" }, "device_report"));
            Expect("offline_outcome_not_eligible", () => store.Import("device-a", outcome with { Outcome = "pending" }, "device_report"));
            var second = store.Import("device-a", outcome with { OperationId = Guid.NewGuid().ToString("D"),
                LocalEveningId = Guid.NewGuid().ToString("D") }, "operator_relay");
            Check(first.EveningId != second.EveningId, "two local evenings remain distinct");
            Check(OfflineOutcomeStore.MapEvening("device-b", outcome.LocalEveningId) != first.EveningId, "device scoped mapping");
            var restarted = new OfflineOutcomeStore(config, journal, keepsakes, memory);
            Check(restarted.Import("device-a", outcome, "device_report").Locator == first.Locator, "restart replay stable");
            var closed = memory.EndShowEvening("device-a", first.EveningId)!;
            var late = restarted.Import("device-a", outcome with { OperationId = Guid.NewGuid().ToString("D") }, "operator_relay");
            var corrected = memory.ReadChronicle("device-a", first.EveningId)!;
            Check(late.EveningId == first.EveningId && !corrected.Provisional
                && corrected.CompletedVisits == 2 && corrected.ConfirmedPresentations == 2
                && corrected.Revision > closed.Revision, "late offline result updates closed chronicle before acknowledgement");
            Check(restarted.Import("device-a", outcome, "operator_relay").Duplicate,
                "retry keeps original provenance through a different authorized relay");
            memory.RevokeShowSource("device-a", outcome.OperationId);
            Expect("offline_memory_not_eligible", () => restarted.Import("device-a", outcome, "device_report"));
            Console.WriteLine("Offline outcome import tests passed (isolated SQLite; no device or provider).");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (KeepsakeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}

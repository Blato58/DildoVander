using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;
using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class OfflineOutcomeEpochTests
{
    internal static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "offline-epoch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
                ["Keepsake:DataRoot"] = Path.Combine(root, "archive"),
                ["Keepsake:Enabled"] = "true"
            }).Build();
            var outcome = new OfflineOutcome("abcdef01-2345-4678-9abc-def012345678", "epoch-device",
                Guid.NewGuid().ToString("D"), 1, Guid.NewGuid().ToString("D"), null,
                "offline_scripted", 1, PartyWandCatalog.Version, 500, 5000,
                "boot_monotonic", "completed_by_timing");
            AssertWireEpochParsing(outcome);
            const string maximum = "18446744073709551615";
            ImportedOfflineOutcome legacy;
            ImportedOfflineOutcome first;
            ImportedOfflineOutcome last;
            using (var session = new Session(config, outcome))
            {
                // This row and archive were created with the old DTO hash/table shape.
                legacy = session.Store.Import(outcome.DeviceId, outcome, "operator_relay");
                Check(legacy.Duplicate && legacy.Locator == session.SeededLocator,
                    "existing legacy row retains its original archive");
                var zero = session.Store.Import(outcome.DeviceId, outcome with { OperationEpoch = "0" }, "device_report");
                Check(zero.Duplicate && zero.Locator == legacy.Locator && zero.OperationEpoch == "0",
                    "explicit zero shares legacy identity while echoing its original wire representation");
                Check(Scalar<string>(config, "SELECT payload_hash FROM show_offline_outcomes") == LegacyHash(outcome),
                    "legacy payload hash and row remain unchanged");
                Check(!JsonSerializer.Serialize(outcome).Contains("OperationEpoch", StringComparison.Ordinal)
                    && !JsonSerializer.Serialize(legacy).Contains("OperationEpoch", StringComparison.Ordinal),
                    "absent epochs preserve legacy DTO JSON shape");

                first = session.Store.Import(outcome.DeviceId, outcome with { OperationEpoch = "1" }, "device_report");
                last = session.Store.Import(outcome.DeviceId, outcome with { OperationEpoch = maximum }, "device_report");
                Check(!first.Duplicate && !last.Duplicate && first.OperationId == outcome.OperationId
                    && first.OperationEpoch == "1" && last.OperationEpoch == maximum,
                    "scoped receipts echo original operation UUID and full uint64 epoch");
                Check(new[] { legacy.Locator, first.Locator, last.Locator }.Distinct().Count() == 3,
                    "same UUID in legacy and two epochs creates three distinct archives");
                var duplicate = session.Store.Import(outcome.DeviceId, outcome with { OperationEpoch = "1" }, "operator_relay");
                Check(duplicate.Duplicate && duplicate.Locator == first.Locator, "same scoped envelope is idempotent");
                var uppercase = session.Store.Import(outcome.DeviceId,
                    outcome with { OperationId = outcome.OperationId.ToUpperInvariant(), OperationEpoch = "1" }, "device_report");
                Check(uppercase.Duplicate && uppercase.Locator == first.Locator
                    && uppercase.OperationId == outcome.OperationId.ToUpperInvariant(),
                    "scoped UUID case alias shares canonical identity but receipt retains original UUID");
                Expect("offline_outcome_conflict", () => session.Store.Import(outcome.DeviceId,
                    outcome with { OperationEpoch = "1", Compartment = 2 }, "device_report"));
                Expect("offline_outcome_conflict", () => session.Store.Import(outcome.DeviceId,
                    outcome with { OperationEpoch = maximum, CompletedAtDeviceMs = 6000 }, "device_report"));
                foreach (var invalid in new[] { "", "00", "01", "-1", "+1", "1.0", "1e0", " 1", "1 ",
                    "18446744073709551616", "99999999999999999999", "184467440737095516150", "\u0661" })
                {
                    Expect("offline_outcome_not_eligible", () => session.Store.Import(outcome.DeviceId,
                        outcome with { OperationEpoch = invalid }, "device_report"));
                }
                AssertDistinctArchiveAndMemory(config, outcome, session.Memory, legacy, first, last);
                var receipt = JsonSerializer.Serialize(last, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using var json = JsonDocument.Parse(receipt);
                Check(json.RootElement.GetProperty("operationEpoch").GetString() == maximum,
                    "receipt JSON carries full uint64 as a string");
            }

            // Dispose the process lock and recreate every store against the real SQLite files.
            using (var reopened = new Session(config))
            {
                foreach (var pair in new[] { (Epoch: (string?)null, Receipt: legacy), (Epoch: "1", Receipt: first),
                    (Epoch: maximum, Receipt: last) })
                {
                    var replay = reopened.Store.Import(outcome.DeviceId,
                        outcome with { OperationEpoch = pair.Epoch }, "operator_relay");
                    Check(replay.Duplicate && replay.Locator == pair.Receipt.Locator
                        && replay.OperationEpoch == pair.Epoch, "reopen replays the same exact scoped receipt");
                }
                AssertDistinctArchiveAndMemory(config, outcome, reopened.Memory, legacy, first, last);
                Expect("offline_outcome_conflict", () => reopened.Store.Import(outcome.DeviceId,
                    outcome with { OperationEpoch = "1", StartedAtDeviceMs = 501 }, "device_report"));
                var otherDevice = reopened.Store.Import("other-epoch-device",
                    outcome with { DeviceId = "other-epoch-device", OperationEpoch = "1" }, "device_report");
                Check(otherDevice.Locator != first.Locator && otherDevice.EveningId != first.EveningId
                    && ArchiveOutcomeId(config, otherDevice.Locator) != ArchiveOutcomeId(config, first.Locator),
                    "same UUID and epoch on another device have independent archive and memory identities");
                Check(reopened.Memory.ReadChronicle("other-epoch-device", otherDevice.EveningId) is
                    { CompletedVisits: 1, ConfirmedPresentations: 1 }, "device-scoped memory remains independent");
            }
            Console.WriteLine("Offline outcome epoch tests passed: durable scoped identity, legacy compatibility, UUID reuse, canonical uint64 and receipt echo.");
            return Task.CompletedTask;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertWireEpochParsing(OfflineOutcome outcome)
    {
        var json = JsonSerializer.Serialize(new OfflineOutcomeRequest(1, outcome),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        static OfflineOutcomeRequest? Parse(string text)
        {
            using var body = JsonDocument.Parse(text);
            return OfflineOutcomeEndpoints.ParseRequest(body.RootElement);
        }
        static void Reject(string text)
        {
            try { Parse(text); }
            catch (JsonException) { return; }
            throw new InvalidOperationException("Invalid offline wire epoch was accepted.");
        }
        string WithEpoch(string token, string name = "operationEpoch") =>
            json[..^2] + ",\"" + name + "\":" + token + "}}";
        Check(Parse(json)?.Outcome.OperationEpoch is null, "missing wire epoch retains legacy identity");
        foreach (var epoch in new[] { "0", "1", "18446744073709551615" })
        {
            Check(Parse(WithEpoch(JsonSerializer.Serialize(epoch)))?.Outcome.OperationEpoch == epoch,
                "canonical wire epoch parses with its exact original string");
        }
        Reject(WithEpoch("\"1\"", "OperationEpoch"));
        Reject(WithEpoch("\"1\"", "operationepoch"));
        Reject(WithEpoch("\"1\"", "OPERATIONEPOCH"));
        foreach (var invalid in new[] { "null", "0", "true", "[]", "{}", "\"\"", "\"01\"", "\"-1\"",
            "\"+1\"", "\"1.0\"", "\"18446744073709551616\"" })
        {
            Reject(WithEpoch(invalid));
            Reject(WithEpoch(invalid, "OperationEpoch"));
        }
        Reject(WithEpoch("\"1\"")[..^2] + ",\"operationEpoch\":\"1\"}}");
        Reject(WithEpoch("\"1\"")[..^2] + ",\"OperationEpoch\":null}}");
        var outcomeAlias = json.Replace("\"outcome\":", "\"Outcome\":", StringComparison.Ordinal);
        Check(Parse(outcomeAlias)?.Outcome.OperationEpoch is null, "legacy outcome property alias remains compatible");
        Reject(WithEpoch("null").Replace("\"outcome\":", "\"Outcome\":", StringComparison.Ordinal));
        Reject(WithEpoch("\"1\"", "OperationEpoch").Replace("\"outcome\":", "\"Outcome\":", StringComparison.Ordinal));
        Check(Parse(json[..^1] + ",\"unknown\":true}")?.Outcome.OperationEpoch is null
            && Parse(json[..^2] + ",\"unknown\":true}}")?.Outcome.OperationEpoch is null,
            "new epoch validation preserves existing unrelated JSON field handling");
    }

    private static void AssertDistinctArchiveAndMemory(IConfiguration config, OfflineOutcome outcome,
        EveningMemoryStore memory, params ImportedOfflineOutcome[] receipts)
    {
        Check(Scalar<long>(config, "SELECT COUNT(*) FROM show_offline_outcomes") == 1,
            "legacy table retains exactly its original row");
        Check(Scalar<long>(config, "SELECT COUNT(*) FROM show_offline_scoped_outcomes") == 2,
            "scoped SQLite table retains both full uint64 epoch identities");
        Check(Scalar<long>(config, "SELECT COUNT(*) FROM dv10_keepsakes") == 3,
            "retries and conflicts never create duplicate archives");
        var backendOutcomeIds = receipts.Select(receipt => ArchiveOutcomeId(config, receipt.Locator)).ToArray();
        Check(backendOutcomeIds.Distinct().Count() == 3 && backendOutcomeIds[0] == outcome.OperationId
            && backendOutcomeIds.Skip(1).All(id => Guid.TryParseExact(id, "D", out _)),
            "scoped archive IDs are distinct stable UUIDs and legacy archive ID is untouched");
        using var connection = Open(config["Backend:DatabasePath"]! + ".evening.sqlite");
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT conversation_id FROM show_visits WHERE device_id=$device";
        command.Parameters.AddWithValue("$device", outcome.DeviceId);
        using var reader = command.ExecuteReader();
        var memoryIds = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read()) { memoryIds.Add(reader.GetString(0)); }
        Check(memoryIds.SetEquals(backendOutcomeIds), "memory and archive use identical scoped backend outcome IDs");
        var chronicle = memory.ReadChronicle(outcome.DeviceId, receipts[0].EveningId)!;
        Check(chronicle.CompletedVisits == 3 && chronicle.ConfirmedPresentations == 3,
            "same operation UUID across epochs produces distinct deduplicated memory facts");
    }

    private static string ArchiveOutcomeId(IConfiguration config, string locator)
    {
        using var connection = Open(config["Backend:DatabasePath"]!);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT document FROM dv10_keepsakes WHERE id=$id";
        command.Parameters.AddWithValue("$id", locator);
        using var json = JsonDocument.Parse((string)command.ExecuteScalar()!);
        return json.RootElement.GetProperty("snapshot").GetProperty("outcomeId").GetString()!;
    }

    private static string LegacyHash(OfflineOutcome record) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            record.OperationId, record.DeviceId, record.BootId, record.Generation, record.LocalEveningId,
            record.EveningId, record.SourceMode, record.Compartment, record.CatalogVersion,
            record.StartedAtDeviceMs, record.CompletedAtDeviceMs, record.TimeConfidence, record.Outcome
        })));

    private static string SeedLegacy(IConfiguration config, KeepsakeStore keepsakes, OfflineOutcome record)
    {
        var evening = OfflineOutcomeStore.MapEvening(record.DeviceId, record.LocalEveningId);
        var wand = PartyWandCatalog.Available(63).Single(candidate => candidate.Compartment == record.Compartment);
        var locator = keepsakes.Issue(new KeepsakeIssueSnapshot(record.OperationId, record.DeviceId,
            record.Compartment, wand.StableId, wand.DisplayName, wand.Description,
            "completed_by_timing", record.CatalogVersion, evening)).Id;
        using var connection = Open(config["Backend:DatabasePath"]!);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE show_offline_outcomes (
                device_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                local_evening_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                payload_hash TEXT NOT NULL, locator TEXT,
                provenance TEXT NOT NULL, retention TEXT NOT NULL DEFAULT 'manual_only',
                PRIMARY KEY(device_id, operation_id));
            INSERT INTO show_offline_outcomes
                (device_id,operation_id,local_evening_id,evening_id,payload_hash,locator,provenance)
                VALUES($device,$operation,$local,$evening,$hash,$locator,'device_report');
            """;
        command.Parameters.AddWithValue("$device", record.DeviceId);
        command.Parameters.AddWithValue("$operation", record.OperationId);
        command.Parameters.AddWithValue("$local", record.LocalEveningId);
        command.Parameters.AddWithValue("$evening", evening);
        command.Parameters.AddWithValue("$hash", LegacyHash(record));
        command.Parameters.AddWithValue("$locator", locator);
        command.ExecuteNonQuery();
        return locator;
    }

    private static T Scalar<T>(IConfiguration config, string sql)
    {
        using var connection = Open(config["Backend:DatabasePath"]!);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
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

    private sealed class Session : IDisposable
    {
        internal OperationJournal Journal { get; }
        internal EveningMemoryStore Memory { get; }
        internal OfflineOutcomeStore Store { get; }
        internal string? SeededLocator { get; }

        internal Session(IConfiguration config, OfflineOutcome? legacy = null)
        {
            Journal = new OperationJournal(config, TimeProvider.System);
            Memory = new EveningMemoryStore(config, Journal);
            var keepsakes = new KeepsakeStore(config, TimeProvider.System);
            if (legacy is not null) { SeededLocator = SeedLegacy(config, keepsakes, legacy); }
            Store = new OfflineOutcomeStore(config, Journal, keepsakes, Memory);
        }

        public void Dispose() => Journal.Dispose();
    }
}

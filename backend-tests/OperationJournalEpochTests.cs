using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

internal static class OperationJournalEpochTests
{
    private const string MaximumEpoch = "18446744073709551615";
    private const string Device = "epoch-fixture";

    internal static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "operation-epoch-" + Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new EpochClock();
            LegacyMigrationPreservesNullAndTombstones(Path.Combine(root, "legacy.sqlite"), clock);
            MissingCurrentEpochColumnFailsClosed(Path.Combine(root, "missing-epoch.sqlite"), clock);
            OriginalEpochSurvivesTerminalTransitions(Path.Combine(root, "operations.sqlite"), clock);
            InvalidStoredIdentityCannotReplayLegacy(root, clock);
            SelectionProofRetainsOriginalEpoch(Path.Combine(root, "selection.sqlite"), clock);
            Console.WriteLine("PASS operation journal original epochs: additive SQLite migration, restart, terminal correlation and retained selection proof");
            return Task.CompletedTask;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void LegacyMigrationPreservesNullAndTombstones(string path, EpochClock clock)
    {
        var correlation = Correlation();
        var operationId = Guid.NewGuid().ToString("D");
        var deletedId = Guid.NewGuid().ToString("D");
        var legacyProof = Proposal(correlation) with { Reason = "" };
        using (var connection = Open(path))
        {
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE turn_operations (
                    operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                    boot_id TEXT NOT NULL, client_turn_id TEXT NOT NULL, generation INTEGER NOT NULL,
                    device_revision INTEGER NOT NULL, status TEXT NOT NULL, expires_at_ms INTEGER NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'turn', fixture_metadata TEXT);
                CREATE TABLE keepsake_selection_outbox (
                    operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, proposal_json TEXT NOT NULL,
                    outcome_id TEXT, evidence TEXT, body_hash TEXT, deleted INTEGER NOT NULL DEFAULT 0,
                    evening_id TEXT, memory_deletion_epoch INTEGER NOT NULL DEFAULT 0,
                    personality_id TEXT NOT NULL DEFAULT 'party-original', fixture_metadata TEXT);
                PRAGMA user_version=7;
                """;
            create.ExecuteNonQuery();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO turn_operations VALUES($operation,$device,$conversation,$boot,$turn,3,10,
                    'completed',$expiry,'turn','retain-operation-metadata');
                INSERT INTO keepsake_selection_outbox
                    (operation_id,device_id,proposal_json,deleted,personality_id,fixture_metadata)
                    VALUES($operation,$device,$proposal,0,'party-original','retain-selection-metadata');
                INSERT INTO keepsake_selection_outbox
                    (operation_id,device_id,proposal_json,deleted,personality_id,fixture_metadata)
                    VALUES($deleted,$device,$proposal,1,'party-original','retain-deleted-tombstone');
                """;
            insert.Parameters.AddWithValue("$operation", operationId);
            insert.Parameters.AddWithValue("$deleted", deletedId);
            insert.Parameters.AddWithValue("$device", Device);
            insert.Parameters.AddWithValue("$conversation", correlation.ConversationId);
            insert.Parameters.AddWithValue("$boot", correlation.BootId.ToString("D"));
            insert.Parameters.AddWithValue("$turn", correlation.ClientTurnId.ToString("D"));
            insert.Parameters.AddWithValue("$expiry", clock.GetUtcNow().AddHours(12).ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$proposal", JsonSerializer.Serialize(legacyProof));
            insert.ExecuteNonQuery();
        }
        for (var reopen = 0; reopen < 2; reopen++)
        {
            using var journal = new OperationJournal(Configuration(path), clock);
            var operation = journal.GetOperation(Device, operationId).Operation;
            Check(operation is { Status: "failed" } && operation.Correlation.OperationEpoch is null,
                "legacy terminal fallback remains nullable after migration and reopen");
            Check(!JsonSerializer.Serialize(operation!.Correlation).Contains("OperationEpoch", StringComparison.Ordinal),
                "legacy JSON omits the absent epoch");
            Check(journal.ReadKeepsakeSelection(Device, operationId) == legacyProof,
                "legacy selection JSON remains unchanged");
            Check(journal.ReadKeepsakeSelection(Device, deletedId) is null, "deleted selection remains unavailable");
        }
        using var inspect = Open(path);
        Check(Scalar(inspect, "SELECT operation_epoch IS NULL FROM turn_operations") is long isNull && isNull == 1,
            "migration retains SQL NULL instead of assigning an epoch");
        Check(Scalar(inspect, "SELECT fixture_metadata FROM turn_operations") as string == "retain-operation-metadata",
            "additive migration retains unrelated operation metadata");
        Check(Scalar(inspect, "SELECT COUNT(*) FROM keepsake_selection_outbox WHERE deleted=1 AND fixture_metadata='retain-deleted-tombstone'") is long count && count == 1,
            "migration retains deleted selection tombstones and their metadata");
        Check(Scalar(inspect, "SELECT fixture_metadata FROM keepsake_selection_outbox WHERE deleted=0") as string == "retain-selection-metadata",
            "additive migration retains unrelated selection metadata");
    }

    private static void MissingCurrentEpochColumnFailsClosed(string path, EpochClock clock)
    {
        var id = Guid.NewGuid().ToString("D");
        var correlation = Correlation();
        using (var connection = Open(path))
        {
            using var create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE turn_operations (
                    operation_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                    boot_id TEXT NOT NULL, client_turn_id TEXT NOT NULL, generation INTEGER NOT NULL,
                    device_revision INTEGER NOT NULL, status TEXT NOT NULL, expires_at_ms INTEGER NOT NULL,
                    kind TEXT NOT NULL DEFAULT 'turn', fixture_metadata TEXT);
                INSERT INTO turn_operations VALUES($operation,$device,$conversation,$boot,$turn,3,10,
                    'pending',$expiry,'turn','retain-uninterpreted-identity');
                PRAGMA user_version=8;
                """;
            create.Parameters.AddWithValue("$operation", id);
            create.Parameters.AddWithValue("$device", Device);
            create.Parameters.AddWithValue("$conversation", correlation.ConversationId);
            create.Parameters.AddWithValue("$boot", correlation.BootId.ToString("D"));
            create.Parameters.AddWithValue("$turn", correlation.ClientTurnId.ToString("D"));
            create.Parameters.AddWithValue("$expiry", clock.GetUtcNow().AddHours(12).ToUnixTimeMilliseconds());
            create.ExecuteNonQuery();
        }
        var rejected = false;
        try { using var journal = new OperationJournal(Configuration(path), clock); }
        catch (InvalidOperationException error) when (error.Message.Contains("missing operation epoch storage", StringComparison.Ordinal))
        {
            rejected = true;
        }
        Check(rejected, "current schema missing its epoch column cannot be reinterpreted as legacy zero");
        using var inspect = Open(path);
        Check(Scalar(inspect, "PRAGMA user_version") is long version && version == 8,
            "rejected startup retains the original schema version");
        Check(Scalar(inspect, "SELECT COUNT(*) FROM pragma_table_info('turn_operations') WHERE name='operation_epoch'") is long columns && columns == 0,
            "rejected startup does not add a replacement epoch column");
        Check(Scalar(inspect, "SELECT COUNT(*) FROM turn_operations WHERE status='pending' AND fixture_metadata='retain-uninterpreted-identity'") is long count && count == 1,
            "rejected startup leaves the original operation and metadata untouched");
        Check(Scalar(inspect, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='server_settings'") is long tables && tables == 0,
            "failed migration rolls back unrelated startup schema changes");
    }

    private static void OriginalEpochSurvivesTerminalTransitions(string path, EpochClock clock)
    {
        var correlations = new[] { Correlation(MaximumEpoch), Correlation("9007199254740993"), Correlation("1"), Correlation(), Correlation("0") };
        var ids = new List<string>();
        var keys = new List<Guid>();
        using (var journal = new OperationJournal(Configuration(path), clock))
        {
            for (var index = 0; index < correlations.Length; index++)
            {
                var key = Guid.NewGuid();
                var reserved = journal.Reserve(Device, "/epoch", key, "same-fixture-hash", correlations[index]);
                ids.Add(reserved.OperationId); keys.Add(key);
                Check(journal.GetOperation(Device, reserved.OperationId).Operation?.Correlation == correlations[index],
                    "pending operation retains the original nullable epoch");
            }
            Check(journal.MarkCompleted(ids[0]), "complete maximum-epoch operation");
            Check(journal.MarkFailed(ids[1]), "fail operation");
            journal.CancelOperation(Device, ids[2], Guid.NewGuid());
            Check(journal.MarkRunning(ids[3]), "running legacy operation prepared for crash recovery");
            var retry = journal.Reserve(Device, "/epoch", keys[0], "same-fixture-hash", correlations[0]);
            Check(retry.Existing && !retry.Conflict && retry.OperationId == ids[0], "same-epoch retry retains its durable identity");
            var foreign = journal.Reserve(Device, "/epoch", keys[0], "same-fixture-hash", correlations[0] with { OperationEpoch = "2" });
            Check(foreign.Existing && foreign.Conflict, "a changed epoch cannot reuse a reservation even with a reused body hash");
            var zeroReplay = journal.Reserve(Device, "/epoch", keys[4], "same-fixture-hash", correlations[4] with { OperationEpoch = null });
            Check(zeroReplay.Existing && !zeroReplay.Conflict, "absent and explicit zero remain the same legacy identity");
            foreach (var invalid in new[] { "", "01", "-1", "1.0", "18446744073709551616", "1e1", "+1", " 1", "1 " })
            {
                var rejected = false;
                try { journal.Reserve(Device, "/epoch", Guid.NewGuid(), "invalid", Correlation(invalid)); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected, "journal rejects malformed or overflowing epoch before persistence");
            }
        }
        using (var reopened = new OperationJournal(Configuration(path), clock))
        {
            for (var index = 0; index < ids.Count; index++)
            {
                var operation = reopened.GetOperation(Device, ids[index]).Operation;
                Check(operation?.Correlation == correlations[index], "restart and terminal tombstones retain exact original correlation");
                Check(operation!.Result is null, "restart cannot synthesize a provider result or selection proof");
            }
            Check(reopened.GetOperation(Device, ids[0]).Operation?.Status == "failed", "completed result expires honestly on restart");
            Check(reopened.GetOperation(Device, ids[2]).Operation?.Status == "cancelled", "cancellation remains terminal");
            Check(reopened.GetOperation(Device, ids[3]).Operation?.Status == "unknown", "interrupted legacy work stays unknown");
            Check(reopened.GetOperation("another-device", ids[0]).Status == 403, "epoch does not weaken producer isolation");
        }
        using var inspect = Open(path);
        Check(Scalar(inspect, "SELECT COUNT(*) FROM turn_operations WHERE operation_epoch='18446744073709551615' AND typeof(operation_epoch)='text'") is long count && count == 1,
            "uint64 maximum persists as exact SQLite TEXT");
    }

    private static void InvalidStoredIdentityCannotReplayLegacy(string root, EpochClock clock)
    {
        foreach (var corruption in new[] { "blob", "invalid-text", "missing" })
        {
            var path = Path.Combine(root, "corrupt-" + corruption + ".sqlite");
            var correlation = Correlation();
            var key = Guid.NewGuid();
            string id;
            using (var journal = new OperationJournal(Configuration(path), clock))
            {
                id = journal.Reserve(Device, "/corrupt", key, "same-fixture-hash", correlation).OperationId;
                Check(journal.MarkFailed(id), "corruption fixture is terminal before reopening");
            }
            using (var connection = Open(path))
            {
                using var corrupt = connection.CreateCommand();
                corrupt.CommandText = corruption == "missing"
                    ? "DELETE FROM turn_operations WHERE operation_id=$operation"
                    : "UPDATE turn_operations SET operation_epoch=$epoch WHERE operation_id=$operation";
                corrupt.Parameters.AddWithValue("$operation", id);
                if (corruption != "missing")
                {
                    // ASCII zero as a BLOB must not be normalized into TEXT zero.
                    corrupt.Parameters.AddWithValue("$epoch", corruption == "blob" ? (object)new byte[] { 0x30 } : "01");
                }
                corrupt.ExecuteNonQuery();
            }
            using (var reopened = new OperationJournal(Configuration(path), clock))
            {
                var replay = reopened.Reserve(Device, "/corrupt", key, "same-fixture-hash", correlation);
                Check(replay.Existing && replay.Conflict && replay.OperationId == id,
                    "corrupt or missing durable identity cannot replay as legacy NULL");
                if (corruption == "missing")
                {
                    Check(reopened.GetOperation(Device, id).Status == 404, "lost operation is not recreated from its idempotency record");
                }
                else
                {
                    var rejected = false;
                    try { reopened.GetOperation(Device, id); }
                    catch (InvalidOperationException error) when (error.Message.Contains("Persisted operation epoch is invalid", StringComparison.Ordinal))
                    {
                        rejected = true;
                    }
                    Check(rejected, "terminal reads reject BLOB and malformed stored epochs without conversion");
                }
            }
            using var inspect = Open(path);
            Check(Scalar(inspect, "SELECT COUNT(*) FROM turn_idempotency") is long idempotencyCount && idempotencyCount == 1,
                "rejected replay retains the original idempotency fence");
            if (corruption == "missing")
            {
                Check(Scalar(inspect, "SELECT COUNT(*) FROM turn_operations") is long count && count == 0,
                    "rejected replay does not fabricate a missing source operation");
            }
            else
            {
                var expectedType = corruption == "blob" ? "blob" : "text";
                var expectedBytes = corruption == "blob" ? "30" : "3031";
                Check(Scalar(inspect, "SELECT typeof(operation_epoch) FROM turn_operations") as string == expectedType
                    && Scalar(inspect, "SELECT hex(operation_epoch) FROM turn_operations") as string == expectedBytes
                    && Scalar(inspect, "SELECT status FROM turn_operations") as string == "failed",
                    "rejected replay leaves the original stored bytes, type and terminal state untouched");
            }
        }
    }

    private static void SelectionProofRetainsOriginalEpoch(string path, EpochClock clock)
    {
        var correlation = Correlation(MaximumEpoch);
        var proposal = Proposal(correlation);
        string id;
        using (var journal = new OperationJournal(Configuration(path), clock))
        {
            id = journal.Reserve(Device, "/selection", Guid.NewGuid(), "selection", correlation).OperationId;
            Check(!journal.MarkSelectionCompleted(id, proposal with { OperationEpoch = null }),
                "missing legacy epoch cannot claim scoped selection proof");
            Check(!journal.MarkSelectionCompleted(id, proposal with { OperationEpoch = "1" }),
                "different epoch cannot claim scoped selection proof");
            Check(!journal.MarkSelectionCompleted(id, proposal with { OperationEpoch = "18446744073709551616" }),
                "overflowing epoch cannot enter proof JSON");
            Check(journal.GetOperation(Device, id).Operation?.Status == "pending", "rejected proof leaves admission pending");
            Check(journal.MarkSelectionCompleted(id, proposal), "original scoped selection proof commits atomically");
        }
        using (var reopened = new OperationJournal(Configuration(path), clock))
        {
            Check(reopened.ReadKeepsakeSelection(Device, id) == proposal with { Reason = "" },
                "durable technical proof retains exact epoch and existing reasoning redaction");
            var outcome = Guid.NewGuid().ToString("D");
            Check(reopened.ConfirmKeepsakeOutcome(Device, id, outcome, "completed_by_timing", "exact") == outcome,
                "original selection proof accepts one physical outcome");
            clock.Utc = clock.Utc.AddDays(2);
            reopened.PurgeExpired();
            Check(reopened.GetOperation(Device, id).Status == 404, "ordinary journal retirement keeps its existing TTL");
            Check(reopened.ReadKeepsakeSelection(Device, id)?.OperationEpoch == MaximumEpoch,
                "journal retirement cannot erase or restamp independently retained proof");
            Check(reopened.ConfirmKeepsakeOutcome(Device, id, Guid.NewGuid().ToString("D"), "completed_by_timing", "exact") == outcome,
                "retained proof keeps outcome replay identity after journal retirement");
        }
    }

    private static TurnCorrelation Correlation(string? epoch = null) => new(Device, Guid.NewGuid(),
        Guid.NewGuid().ToString("D"), Guid.NewGuid(), 3, 10, epoch);
    private static SelectionProposal Proposal(TurnCorrelation correlation) => new(Guid.NewGuid().ToString("D"),
        correlation.ConversationId, correlation.ClientTurnId, PartyWandCatalog.Version, correlation.DeviceRevision,
        2, "Private reasoning stays redacted.", correlation.BootId, correlation.Generation,
        new DateTimeOffset(2026, 10, 20, 12, 1, 0, TimeSpan.Zero), correlation.OperationEpoch);
    private static IConfiguration Configuration(string path) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = path,
            ["Keepsake:Enabled"] = "true"
        }).Build();
    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();
        return connection;
    }
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }
    private static void Check(bool condition, string detail)
    {
        if (!condition) { throw new InvalidOperationException("Operation journal epoch: " + detail); }
    }
    private sealed class EpochClock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Utc;
    }
}

using System.Text.Json;
using Kufr.Backend.Features.Show;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class EveningMemoryStore
{
    private static readonly JsonSerializerOptions ChronicleJson = new(JsonSerializerDefaults.Web);

    private static void MigrateShowChronicle(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction, """
            CREATE TABLE IF NOT EXISTS show_visits (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT, device_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                UNIQUE(device_id, conversation_id));
            CREATE TABLE IF NOT EXISTS show_evening_closures (
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL, closed_at_ms INTEGER NOT NULL,
                PRIMARY KEY(device_id, evening_id));
            CREATE TABLE IF NOT EXISTS show_memory_events (
                event_id TEXT PRIMARY KEY, device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                kind TEXT NOT NULL, source_id TEXT NOT NULL, source_revision INTEGER NOT NULL,
                provenance TEXT NOT NULL, conversation_id TEXT, slot INTEGER, evidence TEXT,
                joke_id TEXT, created_at_ms INTEGER NOT NULL, visibility TEXT NOT NULL DEFAULT 'anonymous',
                deduplication_key TEXT NOT NULL, revoked INTEGER NOT NULL DEFAULT 0,
                retention TEXT NOT NULL DEFAULT 'manual_only',
                UNIQUE(device_id, evening_id, deduplication_key));
            CREATE INDEX IF NOT EXISTS show_memory_evening ON show_memory_events(device_id, evening_id, revoked);
            CREATE TABLE IF NOT EXISTS show_source_tombstones (
                device_id TEXT NOT NULL, source_id TEXT NOT NULL, revoked_at_ms INTEGER NOT NULL,
                PRIMARY KEY(device_id, source_id));
            CREATE TABLE IF NOT EXISTS show_chronicle_editions (
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL, revision INTEGER NOT NULL,
                snapshot TEXT NOT NULL, withdrawn INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(device_id, evening_id, revision));
            """);
        command.ExecuteNonQuery();
    }

    // Offline sync registers its immutable original evening without changing visibility.
    internal bool EnsureImportedShowEvening(string deviceId, string eveningId, string name)
    {
        if (!ValidShowIdentifier(deviceId) || !Guid.TryParse(eveningId, out _) || !IsValidName(name))
        {
            return false;
        }
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                INSERT OR IGNORE INTO evening_catalog(device_id, evening_id, name, created_at_ms)
                VALUES($device,$evening,$name,$now)
                """, ("$device", deviceId), ("$evening", eveningId), ("$name", name),
                ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
            command.ExecuteNonQuery();
            return true;
        }
    }

    // Capture before starting a visit. A closed evening never admits a new guest;
    // an existing capture remains valid for late physical completion after rotation.
    internal string? CaptureShowVisit(string deviceId, string conversationId)
    {
        if (!ValidShowIdentifier(deviceId) || !ValidShowIdentifier(conversationId))
        {
            return null;
        }
        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            using var transaction = connection.BeginTransaction();
            if (ShowSourceRevoked(connection, transaction, deviceId, conversationId)
                || ShowMemoryScopeDeleted(connection, transaction, deviceId, "", conversationId))
            {
                return null;
            }
            using var existing = Command(connection, transaction,
                "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$conversation",
                ("$device", deviceId), ("$conversation", conversationId));
            if (existing.ExecuteScalar() is string captured)
            {
                return captured;
            }
            var evening = ReadActiveEvening(connection, transaction, deviceId);
            if (ShowClosed(connection, transaction, deviceId, evening)
                || ShowMemoryScopeDeleted(connection, transaction, deviceId, evening, conversationId))
            {
                return null;
            }
            InsertShowVisit(connection, transaction, deviceId, evening, conversationId);
            InsertShowEvent(connection, transaction, new($"visit:{deviceId}:{conversationId}",
                ShowMemoryEventKind.VisitStarted, deviceId, evening, conversationId, 1,
                "server_visit_capture", conversationId));
            transaction.Commit();
            return evening;
        }
    }

    // Admission linearizes against EndShowEvening under this gate. The callback
    // reserves only technical journal identity in a separate database; this is
    // deliberately not a distributed transaction. Replay must verify capture.
    internal string? TryAdmitShowVisit(string deviceId, Func<string?> reserveConversation)
    {
        if (!ValidShowIdentifier(deviceId)) { return null; }
        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            using var transaction = connection.BeginTransaction();
            var evening = ReadActiveEvening(connection, transaction, deviceId);
            if (ShowClosed(connection, transaction, deviceId, evening)
                || ShowMemoryScopeDeleted(connection, transaction, deviceId, evening)) { return null; }
            var conversationId = reserveConversation();
            if (!ValidShowIdentifier(conversationId) || ShowSourceRevoked(connection, transaction, deviceId, conversationId!)
                || ShowMemoryScopeDeleted(connection, transaction, deviceId, evening, conversationId)) { return null; }
            using var existing = Command(connection, transaction,
                "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$conversation",
                ("$device", deviceId), ("$conversation", conversationId));
            if (existing.ExecuteScalar() is string captured) { return captured; }
            InsertShowVisit(connection, transaction, deviceId, evening, conversationId!);
            InsertShowEvent(connection, transaction, new($"visit:{deviceId}:{conversationId}",
                ShowMemoryEventKind.VisitStarted, deviceId, evening, conversationId!, 1,
                "server_visit_capture", conversationId));
            transaction.Commit();
            return evening;
        }
    }

    internal bool RecordConfirmedShowOutcome(string deviceId, string conversationId, string eveningId,
        string outcomeId, int slot, string evidence) =>
        RecordConfirmedShowOutcomeCore(deviceId, conversationId, eveningId, outcomeId, slot, evidence, false);

    // Only the immutable validated selection outbox may reconstruct a pre-Show
    // visit. This is deliberately unavailable to generic Create/replay admission.
    internal bool RecordJournalConfirmedShowOutcome(OperationJournal journal, string deviceId,
        string operationId, string outcomeId, string evidence)
    {
        var proposal = journal.ReadKeepsakeSelection(deviceId, operationId);
        var originalEvening = journal.ReadKeepsakeEvening(deviceId, operationId);
        if (proposal is null || !Guid.TryParseExact(originalEvening, "D", out _)) { return false; }
        return RecordConfirmedShowOutcomeCore(deviceId, proposal.ConversationId, originalEvening!,
            outcomeId, proposal.Compartment, evidence, true);
    }

    private bool RecordConfirmedShowOutcomeCore(string deviceId, string conversationId, string eveningId,
        string outcomeId, int slot, string evidence, bool allowJournalBootstrap)
    {
        var presentation = new ShowMemoryEvent("presentation:" + outcomeId,
            ShowMemoryEventKind.PresentationConfirmed, deviceId, eveningId, outcomeId, 1,
            "confirmed_presentation_outcome", conversationId, slot, evidence);
        if (!ValidShowEvent(presentation)) { return false; }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var capture = Command(connection, transaction,
                "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$conversation",
                ("$device", deviceId), ("$conversation", conversationId));
            var captured = capture.ExecuteScalar() as string;
            if (ShowMemoryScopeDeleted(connection, transaction, deviceId, eveningId, conversationId)
                || ShowSourceRevoked(connection, transaction, deviceId, conversationId)
                || ShowSourceRevoked(connection, transaction, deviceId, outcomeId)
                || captured is not null && captured != eveningId) { return false; }
            if (captured is null)
            {
                if (!allowJournalBootstrap) { return false; }
                using var historicalEvening = Command(connection, transaction,
                    "INSERT OR IGNORE INTO evening_catalog(device_id,evening_id,name,created_at_ms) VALUES($device,$evening,$name,$now)",
                    ("$device", deviceId), ("$evening", eveningId), ("$name", "Archivovaný večer"),
                    ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
                historicalEvening.ExecuteNonQuery();
                InsertShowVisit(connection, transaction, deviceId, eveningId, conversationId);
                InsertShowEvent(connection, transaction, new($"visit:{deviceId}:{conversationId}",
                    ShowMemoryEventKind.VisitStarted, deviceId, eveningId, conversationId, 1,
                    "trusted_journal_upgrade_capture", conversationId));
            }
            using var terminal = Command(connection, transaction,
                "SELECT kind FROM show_memory_events WHERE device_id=$device AND evening_id=$evening "
                + "AND conversation_id=$conversation AND kind IN ('VisitCompleted','VisitInterrupted') AND revoked=0 LIMIT 1",
                ("$device", deviceId), ("$evening", eveningId), ("$conversation", conversationId));
            var existingTerminal = terminal.ExecuteScalar() as string;
            if (existingTerminal == "VisitInterrupted") { return false; }
            var matches = ShowEventMatches(connection, transaction, presentation);
            var inserted = !matches && InsertShowEvent(connection, transaction, presentation);
            if (!matches && !inserted) { return false; }
            // Both authoritative facts commit in this one memory transaction.
            // A crash cannot leave physical success followed by interruption.
            var completed = existingTerminal is null && InsertShowEvent(connection, transaction,
                new("visit-physical:" + deviceId + ":" + conversationId, ShowMemoryEventKind.VisitCompleted,
                    deviceId, eveningId, conversationId, 1, "confirmed_physical_outcome", conversationId));
            if ((inserted || completed) && ShowClosed(connection, transaction, deviceId, eveningId))
            {
                SaveChronicleEdition(connection, transaction, deviceId, eveningId);
            }
            transaction.Commit();
            return true;
        }
    }

    internal string? ReadCapturedShowEvening(string deviceId, string conversationId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$conversation",
                ("$device", deviceId), ("$conversation", conversationId));
            return command.ExecuteScalar() as string;
        }
    }

    internal bool RecordShowEvent(ShowMemoryEvent entry)
    {
        if (!ValidShowEvent(entry))
        {
            return false;
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var catalog = Command(connection, transaction,
                "SELECT 1 FROM evening_catalog WHERE device_id=$device AND evening_id=$evening",
                ("$device", entry.DeviceId), ("$evening", entry.EveningId));
            if (catalog.ExecuteScalar() is null || ShowSourceRevoked(connection, transaction, entry.DeviceId, entry.SourceId)
                || ShowMemoryScopeDeleted(connection, transaction, entry.DeviceId, entry.EveningId, entry.ConversationId))
            {
                return false;
            }
            if (ShowEventMatches(connection, transaction, entry))
            {
                return true;
            }
            if (entry.ConversationId is { } conversation)
            {
                using var visit = Command(connection, transaction,
                    "SELECT evening_id FROM show_visits WHERE device_id=$device AND conversation_id=$conversation",
                    ("$device", entry.DeviceId), ("$conversation", conversation));
                var captured = visit.ExecuteScalar() as string;
                if (captured is not null && captured != entry.EveningId)
                {
                    return false;
                }
                if (captured is null)
                {
                    if (entry.Kind != ShowMemoryEventKind.VisitStarted || ShowClosed(connection, transaction, entry.DeviceId, entry.EveningId))
                    {
                        return false;
                    }
                    InsertShowVisit(connection, transaction, entry.DeviceId, entry.EveningId, conversation);
                }
                if (ShowSourceRevoked(connection, transaction, entry.DeviceId, conversation))
                {
                    return false;
                }
                if (entry.Kind is ShowMemoryEventKind.VisitCompleted or ShowMemoryEventKind.VisitInterrupted)
                {
                    using var ended = Command(connection, transaction, """
                        SELECT 1 FROM show_memory_events WHERE device_id=$device AND evening_id=$evening
                        AND conversation_id=$conversation AND kind IN ('VisitCompleted','VisitInterrupted') AND revoked=0
                        """, ("$device", entry.DeviceId), ("$evening", entry.EveningId), ("$conversation", conversation));
                    if (ended.ExecuteScalar() is not null) { return false; }
                }
                if (entry.Kind == ShowMemoryEventKind.PresentationConfirmed)
                {
                    using var cancelled = Command(connection, transaction, """
                        SELECT 1 FROM show_memory_events WHERE device_id=$device AND evening_id=$evening
                        AND conversation_id=$conversation AND kind='VisitInterrupted' AND revoked=0
                        """, ("$device", entry.DeviceId), ("$evening", entry.EveningId), ("$conversation", conversation));
                    if (cancelled.ExecuteScalar() is not null) { return false; }
                }
            }
            if (entry.Kind == ShowMemoryEventKind.RunningJokeUsed
                && (!JokeAvailability(connection, transaction, entry.DeviceId, entry.EveningId, entry.JokeId!).Eligible
                    || !HasJokeFacts(BuildChronicleSnapshot(connection, transaction, entry.DeviceId, entry.EveningId, 0), entry.JokeId!)))
            {
                return false;
            }
            var inserted = InsertShowEvent(connection, transaction, entry);
            if (inserted && ShowClosed(connection, transaction, entry.DeviceId, entry.EveningId))
            {
                SaveChronicleEdition(connection, transaction, entry.DeviceId, entry.EveningId);
            }
            transaction.Commit();
            return inserted;
        }
    }

    internal ChronicleSnapshot? EndShowEvening(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var catalog = Command(connection, transaction,
                "SELECT 1 FROM evening_catalog WHERE device_id=$device AND evening_id=$evening",
                ("$device", deviceId), ("$evening", eveningId));
            if (catalog.ExecuteScalar() is null)
            {
                return null;
            }
            if (!ShowClosed(connection, transaction, deviceId, eveningId))
            {
                // No actuator or conversation abort: pending visits remain unresolved.
                using var close = Command(connection, transaction,
                    "INSERT INTO show_evening_closures VALUES($device,$evening,$now)",
                    ("$device", deviceId), ("$evening", eveningId), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
                close.ExecuteNonQuery();
                InsertShowEvent(connection, transaction, new($"close:{deviceId}:{eveningId}",
                    ShowMemoryEventKind.EveningClosed, deviceId, eveningId, eveningId, 1, "authorized_end_evening"));
                SaveChronicleEdition(connection, transaction, deviceId, eveningId);
            }
            var snapshot = ReadChronicleSnapshot(connection, transaction, deviceId, eveningId);
            transaction.Commit();
            return snapshot;
        }
    }

    internal ChronicleSnapshot? ReadChronicle(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var catalog = Command(connection, null,
                "SELECT 1 FROM evening_catalog WHERE device_id=$device AND evening_id=$evening",
                ("$device", deviceId), ("$evening", eveningId));
            return catalog.ExecuteScalar() is null ? null : ReadChronicleSnapshot(connection, null, deviceId, eveningId);
        }
    }

    internal ChronicleAdministration? ReadChronicleAdministration(string deviceId, string eveningId)
    {
        lock (gate)
        {
            var snapshot = ReadChronicle(deviceId, eveningId);
            if (snapshot is null)
            {
                return null;
            }
            using var connection = Open();
            using var count = Command(connection, null,
                "SELECT COUNT(*) FROM show_chronicle_editions WHERE device_id=$device AND evening_id=$evening",
                ("$device", deviceId), ("$evening", eveningId));
            return new(snapshot, Convert.ToInt32(count.ExecuteScalar()));
        }
    }

    internal ChronicleSnapshot? RegenerateChronicle(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!ShowClosed(connection, transaction, deviceId, eveningId))
            {
                return null;
            }
            SaveChronicleEdition(connection, transaction, deviceId, eveningId);
            var snapshot = ReadChronicleSnapshot(connection, transaction, deviceId, eveningId);
            transaction.Commit();
            return snapshot;
        }
    }

    internal void RevokeShowSource(string deviceId, string sourceId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RevokeShowSources(connection, transaction, deviceId, [sourceId]);
            transaction.Commit();
        }
    }

    // Reuses the caller's memory transaction: canonical raw-memory deletion and
    // withdrawal of all derived show facts become visible together.
    private void RevokeShowSources(SqliteConnection connection, SqliteTransaction transaction,
        string deviceId, IEnumerable<string> sourceIds)
    {
        var sources = sourceIds.ToHashSet(StringComparer.Ordinal);
        var evenings = new HashSet<string>(StringComparer.Ordinal);
        var pendingSources = new Queue<string>(sources);
        var inspectedSources = new HashSet<string>(StringComparer.Ordinal);
        while (pendingSources.TryDequeue(out var sourceId))
        {
            if (!inspectedSources.Add(sourceId)) { continue; }
            using var affected = Command(connection, transaction,
                "SELECT DISTINCT evening_id,source_id FROM show_memory_events WHERE device_id=$device AND revoked=0 AND (source_id=$source OR conversation_id=$source)",
                ("$device", deviceId), ("$source", sourceId));
            using var reader = affected.ExecuteReader();
            while (reader.Read())
            {
                evenings.Add(reader.GetString(0));
                if (sources.Add(reader.GetString(1))) { pendingSources.Enqueue(reader.GetString(1)); }
            }
        }
        foreach (var sourceId in sources)
        {
            using var revoke = Command(connection, transaction,
                "INSERT OR IGNORE INTO show_source_tombstones VALUES($device,$source,$now); "
                + "UPDATE show_memory_events SET revoked=1 WHERE device_id=$device AND (source_id=$source OR conversation_id=$source);",
                ("$device", deviceId), ("$source", sourceId), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
            revoke.ExecuteNonQuery();
        }
        foreach (var evening in evenings)
        {
            using var withdraw = Command(connection, transaction,
                "UPDATE show_chronicle_editions SET withdrawn=1 WHERE device_id=$device AND evening_id=$evening",
                ("$device", deviceId), ("$evening", evening));
            withdraw.ExecuteNonQuery();
            if (ShowClosed(connection, transaction, deviceId, evening))
            {
                SaveChronicleEdition(connection, transaction, deviceId, evening);
            }
        }
    }

    private void RevokeDeletedShowEvening(SqliteConnection connection, SqliteTransaction transaction,
        string deviceId, string eveningId)
    {
        using var deleted = Command(connection, transaction,
            "INSERT OR IGNORE INTO memory_deleted_evenings VALUES($namespace,$device,$evening)",
            ("$namespace", memoryOptions.Namespace), ("$device", deviceId), ("$evening", eveningId));
        deleted.ExecuteNonQuery();
        using var command = Command(connection, transaction,
            "SELECT source_id FROM show_memory_events WHERE device_id=$device AND evening_id=$evening "
            + "UNION SELECT conversation_id FROM show_visits WHERE device_id=$device AND evening_id=$evening",
            ("$device", deviceId), ("$evening", eveningId));
        var sources = new HashSet<string>(StringComparer.Ordinal) { eveningId };
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) { sources.Add(reader.GetString(0)); }
        }
        RevokeShowSources(connection, transaction, deviceId, sources);
    }

    private bool ShowMemoryScopeDeleted(SqliteConnection connection, SqliteTransaction? transaction,
        string deviceId, string eveningId, string? conversationId = null)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM memory_deleted_evenings WHERE app_namespace=$namespace AND device_id=$device AND evening_id=$evening "
            + "UNION ALL SELECT 1 FROM memory_deleted_conversations WHERE app_namespace=$namespace AND device_id=$device AND conversation_id=$conversation LIMIT 1",
            ("$namespace", memoryOptions.Namespace), ("$device", deviceId), ("$evening", eveningId), ("$conversation", conversationId));
        return command.ExecuteScalar() is not null;
    }

    private static bool ValidShowIdentifier(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 240 && !value.Any(char.IsControl);

    private static bool ValidShowEvent(ShowMemoryEvent entry) => Enum.IsDefined(entry.Kind)
        && ValidShowIdentifier(entry.EventId) && ValidShowIdentifier(entry.DeviceId)
        && ValidShowIdentifier(entry.EveningId) && ValidShowIdentifier(entry.SourceId)
        && ValidShowIdentifier(entry.Provenance) && entry.SourceRevision > 0
        && (entry.ConversationId is null || ValidShowIdentifier(entry.ConversationId))
        && (entry.Kind != ShowMemoryEventKind.PresentationConfirmed || entry.Slot is >= 1 and <= 6
            && entry.Evidence is "completed_by_timing" or "timed_completed" or "sensor_verified" or "device_reported_completed" or "host_confirmed")
        && (entry.Kind is not (ShowMemoryEventKind.VisitStarted or ShowMemoryEventKind.VisitCompleted or ShowMemoryEventKind.VisitInterrupted)
            || entry.ConversationId is not null)
        && (entry.Kind != ShowMemoryEventKind.RunningJokeUsed || IsKnownJoke(entry.JokeId));

    private static bool ShowClosed(SqliteConnection connection, SqliteTransaction? transaction, string device, string evening)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM show_evening_closures WHERE device_id=$device AND evening_id=$evening",
            ("$device", device), ("$evening", evening));
        return command.ExecuteScalar() is not null;
    }

    private static bool ShowSourceRevoked(SqliteConnection connection, SqliteTransaction? transaction, string device, string source)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM show_source_tombstones WHERE device_id=$device AND source_id=$source",
            ("$device", device), ("$source", source));
        return command.ExecuteScalar() is not null;
    }

    private static void InsertShowVisit(SqliteConnection connection, SqliteTransaction transaction, string device, string evening, string conversation)
    {
        using var command = Command(connection, transaction,
            "INSERT INTO show_visits(device_id,evening_id,conversation_id) VALUES($device,$evening,$conversation)",
            ("$device", device), ("$evening", evening), ("$conversation", conversation));
        command.ExecuteNonQuery();
    }

    private bool InsertShowEvent(SqliteConnection connection, SqliteTransaction transaction, ShowMemoryEvent entry)
    {
        // A producer revision may correct metadata, but cannot count the same
        // physical outcome or completed visit a second time with a fresh event ID.
        var deduplication = $"{entry.Kind}:{(entry.Kind is ShowMemoryEventKind.VisitStarted or ShowMemoryEventKind.VisitCompleted or ShowMemoryEventKind.VisitInterrupted ? entry.ConversationId : entry.SourceId)}";
        using var command = Command(connection, transaction, """
            INSERT OR IGNORE INTO show_memory_events
                (event_id,device_id,evening_id,kind,source_id,source_revision,provenance,conversation_id,slot,evidence,joke_id,created_at_ms,deduplication_key)
            VALUES($id,$device,$evening,$kind,$source,$revision,$provenance,$conversation,$slot,$evidence,$joke,$now,$dedup)
            """, ("$id", entry.EventId), ("$device", entry.DeviceId), ("$evening", entry.EveningId),
            ("$kind", entry.Kind.ToString()), ("$source", entry.SourceId), ("$revision", entry.SourceRevision),
            ("$provenance", entry.Provenance), ("$conversation", (object?)entry.ConversationId ?? DBNull.Value),
            ("$slot", (object?)entry.Slot ?? DBNull.Value), ("$evidence", (object?)entry.Evidence ?? DBNull.Value),
            ("$joke", (object?)entry.JokeId ?? DBNull.Value), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$dedup", deduplication));
        return command.ExecuteNonQuery() == 1;
    }

    private static bool ShowEventMatches(SqliteConnection connection, SqliteTransaction? transaction, ShowMemoryEvent entry)
    {
        using var command = Command(connection, transaction, """
            SELECT 1 FROM show_memory_events WHERE device_id=$device AND evening_id=$evening
                AND kind=$kind AND source_id=$source AND source_revision=$revision AND provenance=$provenance
                AND conversation_id IS $conversation AND slot IS $slot AND evidence IS $evidence AND joke_id IS $joke AND revoked=0
            """, ("$device", entry.DeviceId), ("$evening", entry.EveningId), ("$kind", entry.Kind.ToString()),
            ("$source", entry.SourceId), ("$revision", entry.SourceRevision), ("$provenance", entry.Provenance),
            ("$conversation", (object?)entry.ConversationId ?? DBNull.Value), ("$slot", (object?)entry.Slot ?? DBNull.Value),
            ("$evidence", (object?)entry.Evidence ?? DBNull.Value), ("$joke", (object?)entry.JokeId ?? DBNull.Value));
        return command.ExecuteScalar() is not null;
    }

    private ChronicleSnapshot ReadChronicleSnapshot(SqliteConnection connection, SqliteTransaction? transaction, string device, string evening)
    {
        using var latest = Command(connection, transaction, """
            SELECT snapshot FROM show_chronicle_editions WHERE device_id=$device AND evening_id=$evening
                AND withdrawn=0 ORDER BY revision DESC LIMIT 1
            """, ("$device", device), ("$evening", evening));
        if (latest.ExecuteScalar() is string json)
        {
            return JsonSerializer.Deserialize<ChronicleSnapshot>(json, ChronicleJson)!;
        }
        return BuildChronicleSnapshot(connection, transaction, device, evening, 0);
    }

    private void SaveChronicleEdition(SqliteConnection connection, SqliteTransaction transaction, string device, string evening)
    {
        using var version = Command(connection, transaction,
            "SELECT COALESCE(MAX(revision),0)+1 FROM show_chronicle_editions WHERE device_id=$device AND evening_id=$evening",
            ("$device", device), ("$evening", evening));
        var revision = Convert.ToInt32(version.ExecuteScalar());
        var snapshot = BuildChronicleSnapshot(connection, transaction, device, evening, revision);
        using var save = Command(connection, transaction,
            "INSERT INTO show_chronicle_editions(device_id,evening_id,revision,snapshot) VALUES($device,$evening,$revision,$json)",
            ("$device", device), ("$evening", evening), ("$revision", revision),
            ("$json", JsonSerializer.Serialize(snapshot, ChronicleJson)));
        save.ExecuteNonQuery();
    }

    private ChronicleSnapshot BuildChronicleSnapshot(SqliteConnection connection, SqliteTransaction? transaction, string device, string evening, int revision)
    {
        using var command = Command(connection, transaction, """
            SELECT kind,slot,COUNT(*) FROM show_memory_events WHERE device_id=$device AND evening_id=$evening
                AND revoked=0 GROUP BY kind,slot
            """, ("$device", device), ("$evening", evening));
        var counts = new Dictionary<ShowMemoryEventKind, int>();
        var presentations = new Dictionary<int, int>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var kind = Enum.Parse<ShowMemoryEventKind>(reader.GetString(0));
                var count = reader.GetInt32(2);
                counts[kind] = counts.GetValueOrDefault(kind) + count;
                if (kind == ShowMemoryEventKind.PresentationConfirmed && !reader.IsDBNull(1))
                {
                    presentations[reader.GetInt32(1)] = count;
                }
            }
        }
        var wands = PartyWandCatalog.Available(63).Select(w => new ChronicleWand(w.Compartment,
            w.StableId, w.DisplayName, presentations.GetValueOrDefault(w.Compartment))).ToArray();
        var maximum = presentations.Values.DefaultIfEmpty().Max();
        var leaders = maximum == 0 ? [] : wands.Where(w => w.ConfirmedPresentations == maximum).Select(w => w.Slot).ToArray();
        var visits = counts.GetValueOrDefault(ShowMemoryEventKind.VisitCompleted);
        var confirmed = counts.GetValueOrDefault(ShowMemoryEventKind.PresentationConfirmed);
        var duels = counts.GetValueOrDefault(ShowMemoryEventKind.DuelCompleted);
        var interrupted = counts.GetValueOrDefault(ShowMemoryEventKind.VisitInterrupted);
        // Count unresolved per captured visit, so duplicate or corrected terminal
        // records cannot hide another guest's still-running operation.
        using var pending = Command(connection, transaction, """
            SELECT COUNT(*) FROM show_memory_events started WHERE started.device_id=$device
              AND started.evening_id=$evening AND started.kind='VisitStarted' AND started.revoked=0
              AND NOT EXISTS(SELECT 1 FROM show_memory_events ended WHERE ended.device_id=started.device_id
                AND ended.evening_id=started.evening_id AND ended.conversation_id=started.conversation_id
                AND ended.kind IN ('VisitCompleted','VisitInterrupted') AND ended.revoked=0)
            """, ("$device", device), ("$evening", evening));
        var unresolved = Convert.ToInt32(pending.ExecuteScalar());
        var popular = leaders.Length switch
        {
            0 => "Žádná hůlka zatím nemá potvrzenou prezentaci. Ministerstvo popularity musí odložit razítko.",
            1 => $"Nejvíc potvrzených prezentací má {wands.Single(w => w.Slot == leaders[0]).OfficialName}: {maximum}. Vlastní šatna zatím nebyla schválena.",
            _ => $"Na prvním místě je remíza: {string.Join(", ", wands.Where(w => leaders.Contains(w.Slot)).Select(w => w.OfficialName))}. Každá má {maximum} potvrzených prezentací; ministerstvo odmítá dodat další stupínek."
        };
        var narrative = visits == 0 && confirmed == 0 && duels == 0
            ? "Kronika katastrof zatím eviduje nulu dokončených návštěv, nulu potvrzených prezentací a nulu dokončených duelů. DildoVander slavnostně přežil přípravy. " + popular
            : $"Večerní kniha eviduje dokončené návštěvy: {visits}, potvrzené prezentace: {confirmed}, dokončené duely: {duels}. " + popular;
        if (unresolved > 0)
        {
            narrative += $" Neuzavřené návštěvy: {unresolved}. Jejich výsledek se zatím nezapočítává.";
        }
        return new(1, evening, revision, !ShowClosed(connection, transaction, device, evening), memoryClock.GetUtcNow(),
            visits, confirmed, duels, interrupted, unresolved, wands, leaders, narrative);
    }
}

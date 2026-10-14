using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// Content lives separately from the technical journal and its retention/backups.
internal sealed partial class EveningMemoryStore
{
    internal const int MaxExchanges = 2000;
    internal const int MaxContentBytes = 2_000_000;
    internal const int MaxContextCharacters = 16_000;
    internal const int MaxEvenings = 100;
    internal const int MaxMutationsPerDevice = 1024;
    internal static readonly TimeSpan MutationRetention = TimeSpan.FromHours(24);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly JsonSerializerOptions MutationJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly Lock gate = new();
    private readonly Memory.HybridMemoryOptions memoryOptions;
    private readonly TimeProvider memoryClock;

    public EveningMemoryStore(IConfiguration configuration, OperationJournal journal, TimeProvider? clock = null)
    {
        // Resolving the journal first acquires the single-backend instance lock.
        _ = journal;
        memoryOptions = Memory.HybridMemoryOptions.Read(configuration, journal);
        memoryClock = clock ?? TimeProvider.System;
        var databasePath = configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Backend:DatabasePath must be configured.");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath) + ".evening.sqlite",
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var migration = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = migration;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS evenings (
                device_id TEXT PRIMARY KEY, evening_id TEXT NOT NULL,
                dropped_count INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS exchanges (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                operation_id TEXT NOT NULL UNIQUE, conversation_id TEXT NOT NULL,
                visitor_text TEXT NOT NULL, reply TEXT NOT NULL, content_bytes INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS exchanges_device ON exchanges(device_id, sequence);
            CREATE INDEX IF NOT EXISTS exchanges_device_evening ON exchanges(device_id, evening_id, sequence);
            CREATE TABLE IF NOT EXISTS evening_catalog (
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                name TEXT NOT NULL, created_at_ms INTEGER NOT NULL,
                dropped_count INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (device_id, evening_id));
            CREATE TABLE IF NOT EXISTS evening_mutations (
                device_id TEXT NOT NULL, key TEXT NOT NULL,
                route TEXT NOT NULL, body_hash TEXT NOT NULL,
                status_code INTEGER NOT NULL, response_json TEXT NOT NULL,
                created_at_ms INTEGER NOT NULL,
                PRIMARY KEY (device_id, key));
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT OR IGNORE INTO evening_catalog
                (device_id, evening_id, name, created_at_ms, dropped_count)
            SELECT device_id, evening_id, 'Původní večer', $now, dropped_count FROM evenings;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        MigrateHybridMemory(connection, migration);
        migration.Commit();
    }

    internal static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var runes = name.EnumerateRunes().ToArray();
        return runes.Length is >= 1 and <= 80
            && runes.All(rune => Rune.GetUnicodeCategory(rune) is not
                (UnicodeCategory.Control or UnicodeCategory.Format
                    or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator));
    }

    public EveningMemoryStatus GetStatus(string deviceId)
    {
        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            return ReadStatus(connection, deviceId);
        }
    }

    public string? ReadActiveEveningId(string deviceId)
    {
        lock (gate)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder(connectionString)
                { Mode = SqliteOpenMode.ReadOnly }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT evening_id FROM evenings WHERE device_id=$device";
            command.Parameters.AddWithValue("$device", deviceId);
            return command.ExecuteScalar() as string;
        }
    }

    public EveningsList List(string deviceId)
    {
        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            return ReadList(connection, null, deviceId);
        }
    }

    public bool Contains(string deviceId, string eveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT 1 FROM evening_catalog WHERE device_id = $device AND evening_id = $evening;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$evening", eveningId);
            return command.ExecuteScalar() is not null;
        }
    }

    public EveningMutationResult? FindMutation(string deviceId, Guid key, string route, string bodyHash)
    {
        lock (gate)
        {
            using var connection = Open();
            return ReadMutation(connection, null, deviceId, key, route, bodyHash);
        }
    }

    public EveningMutationResult Create(
        string deviceId,
        Guid key,
        string route,
        string bodyHash,
        string expectedEveningId,
        string name)
    {
        if (!IsValidName(name))
        {
            return new(400, null, "invalid_request");
        }

        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            using var transaction = connection.BeginTransaction();
            var replay = ReadMutation(connection, transaction, deviceId, key, route, bodyHash);
            if (replay is not null)
            {
                return replay;
            }

            PruneExpiredMutations(connection, transaction, deviceId);

            if (ReadActiveEvening(connection, transaction, deviceId) != expectedEveningId)
            {
                return new(409, null, "evening_conflict");
            }

            using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM evening_catalog WHERE device_id = $device";
            count.Parameters.AddWithValue("$device", deviceId);
            if ((long)count.ExecuteScalar()! >= MaxEvenings)
            {
                return new(429, null, "evening_limit");
            }

            if (!HasMutationCapacity(connection, transaction, deviceId))
            {
                return new(429, null, "evening_mutation_limit");
            }

            var next = Guid.NewGuid().ToString("D");
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO evening_catalog(device_id, evening_id, name, created_at_ms)
                VALUES ($device, $evening, $name, $now);
                UPDATE evenings SET evening_id = $evening, dropped_count = 0 WHERE device_id = $device;
                """;
            insert.Parameters.AddWithValue("$device", deviceId);
            insert.Parameters.AddWithValue("$evening", next);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
            var response = ReadList(connection, transaction, deviceId);
            WriteMutation(connection, transaction, deviceId, key, route, bodyHash, 201, response);
            transaction.Commit();
            return new(201, response, null);
        }
    }

    public EveningMutationResult Activate(
        string deviceId,
        Guid key,
        string route,
        string bodyHash,
        string targetEveningId,
        string expectedEveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            using var transaction = connection.BeginTransaction();
            var replay = ReadMutation(connection, transaction, deviceId, key, route, bodyHash);
            if (replay is not null)
            {
                return replay;
            }

            PruneExpiredMutations(connection, transaction, deviceId);

            using var target = connection.CreateCommand();
            target.Transaction = transaction;
            target.CommandText = "SELECT 1 FROM evening_catalog WHERE device_id = $device AND evening_id = $evening";
            target.Parameters.AddWithValue("$device", deviceId);
            target.Parameters.AddWithValue("$evening", targetEveningId);
            if (target.ExecuteScalar() is null)
            {
                return new(404, null, "evening_not_found");
            }

            if (ReadActiveEvening(connection, transaction, deviceId) != expectedEveningId)
            {
                return new(409, null, "evening_conflict");
            }

            if (!HasMutationCapacity(connection, transaction, deviceId))
            {
                return new(429, null, "evening_mutation_limit");
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE evenings SET evening_id = $evening,
                    dropped_count = (SELECT dropped_count FROM evening_catalog
                        WHERE device_id = $device AND evening_id = $evening)
                WHERE device_id = $device;
                """;
            update.Parameters.AddWithValue("$device", deviceId);
            update.Parameters.AddWithValue("$evening", targetEveningId);
            update.ExecuteNonQuery();
            var response = ReadList(connection, transaction, deviceId);
            WriteMutation(connection, transaction, deviceId, key, route, bodyHash, 200, response);
            transaction.Commit();
            return new(200, response, null);
        }
    }

    public EveningContext ReadContext(string deviceId, string conversationId, string text)
    {
        lock (gate)
        {
            using var connection = Open();
            EnsureEvening(connection, deviceId);
            var status = ReadStatus(connection, deviceId);
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT sequence, conversation_id, visitor_text, reply FROM exchanges
                WHERE device_id = $device AND evening_id = $evening ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$evening", status.EveningId);
            using var reader = command.ExecuteReader();
            var rows = new List<MemoryExchange>();
            while (reader.Read())
            {
                rows.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            }

            // Rank whole conversations so a matching answer retains its speaker's
            // introduction. This is lexical recall, not inferred identity or facts.
            var query = Words(text + " " + string.Join(" ", rows
                .Where(row => row.ConversationId == conversationId)
                .Select(row => row.VisitorText)));
            var groups = rows.GroupBy(row => row.ConversationId)
                .OrderByDescending(group => group.Key == conversationId)
                .ThenByDescending(group => group.Sum(row => Words(row.VisitorText).Intersect(query).Count()))
                .ThenByDescending(group => group.Max(row => row.Sequence));
            var selected = new List<MemoryExchange>();
            var remaining = MaxContextCharacters - 2;
            foreach (var group in groups)
            {
                // Never include an old answer without the start of its conversation.
                if (JsonSerializer.Serialize(group.First(), JsonOptions).Length + 1 > remaining)
                {
                    continue;
                }

                foreach (var row in group)
                {
                    var size = JsonSerializer.Serialize(row, JsonOptions).Length + 1;
                    if (size <= remaining)
                    {
                        selected.Add(row);
                        remaining -= size;
                    }
                }
            }

            var transcript = selected.Count == 0
                ? ""
                : JsonSerializer.Serialize(selected.OrderBy(row => row.Sequence), JsonOptions);
            return new(status.EveningId, transcript);
        }
    }

    public bool Append(string deviceId, string eveningId, string operationId,
        string conversationId, string visitorText, string reply)
    {
        if (visitorText.Length > 1000 || reply.Length > 500)
        {
            throw new ArgumentException("Evening exchange exceeds its bounds.");
        }

        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO exchanges
                    (device_id, evening_id, operation_id, conversation_id, visitor_text, reply, content_bytes)
                SELECT $device, $evening, $operation, $conversation, $visitor, $reply, $bytes
                WHERE EXISTS (SELECT 1 FROM evenings WHERE device_id = $device AND evening_id = $evening);
                """;
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$evening", eveningId);
            command.Parameters.AddWithValue("$operation", operationId);
            command.Parameters.AddWithValue("$conversation", conversationId);
            command.Parameters.AddWithValue("$visitor", visitorText);
            command.Parameters.AddWithValue("$reply", reply);
            command.Parameters.AddWithValue("$bytes", Encoding.UTF8.GetByteCount(visitorText + reply));
            var inserted = command.ExecuteNonQuery() == 1;
            if (inserted)
            {
                using var trim = connection.CreateCommand();
                trim.Transaction = transaction;
                trim.CommandText = """
                    DELETE FROM exchanges WHERE sequence IN (
                        SELECT sequence FROM (
                            SELECT sequence, ROW_NUMBER() OVER (ORDER BY sequence DESC) AS row_number,
                                SUM(content_bytes) OVER (ORDER BY sequence DESC) AS running_bytes
                            FROM exchanges WHERE device_id = $device AND evening_id = $evening
                        ) WHERE row_number > $maxRows OR running_bytes > $maxBytes
                    );
                    """;
                trim.Parameters.AddWithValue("$device", deviceId);
                trim.Parameters.AddWithValue("$evening", eveningId);
                trim.Parameters.AddWithValue("$maxRows", MaxExchanges);
                trim.Parameters.AddWithValue("$maxBytes", MaxContentBytes);
                var dropped = trim.ExecuteNonQuery();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE evening_catalog SET dropped_count = dropped_count + $count
                    WHERE device_id = $device AND evening_id = $evening;
                    UPDATE evenings SET dropped_count = dropped_count + $count
                    WHERE device_id = $device AND evening_id = $evening;
                    """;
                update.Parameters.AddWithValue("$count", dropped);
                update.Parameters.AddWithValue("$device", deviceId);
                update.Parameters.AddWithValue("$evening", eveningId);
                update.ExecuteNonQuery();
            }

            transaction.Commit();
            return inserted;
        }
    }

    // Compare-and-clear makes retries harmless even after new content is written.
    public bool Clear(string deviceId, string expectedEveningId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE evenings SET evening_id = $next, dropped_count = 0
                WHERE device_id = $device AND evening_id = $expected;
                """;
            var nextEveningId = Guid.NewGuid().ToString("D");
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$expected", expectedEveningId);
            command.Parameters.AddWithValue("$next", nextEveningId);
            var changed = command.ExecuteNonQuery() == 1;
            if (changed)
            {
                InvalidateEveningMemory(connection, transaction, deviceId, expectedEveningId);
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = """
                    DELETE FROM exchanges WHERE device_id = $device AND evening_id = $expected;
                    DELETE FROM evening_catalog WHERE device_id = $device AND evening_id = $expected;
                    INSERT INTO evening_catalog(device_id, evening_id, name, created_at_ms)
                    VALUES ($device, $next, 'Nový večer', $now);
                    """;
                delete.Parameters.AddWithValue("$device", deviceId);
                delete.Parameters.AddWithValue("$expected", expectedEveningId);
                delete.Parameters.AddWithValue("$next", nextEveningId);
                delete.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
            return changed;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA secure_delete=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureEvening(SqliteConnection connection, string deviceId)
    {
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO evenings(device_id, evening_id) VALUES ($device, $id);
            INSERT OR IGNORE INTO evening_catalog(device_id, evening_id, name, created_at_ms, dropped_count)
            SELECT device_id, evening_id, 'Večer 1', $now, dropped_count
            FROM evenings WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static EveningMemoryStatus ReadStatus(SqliteConnection connection, string deviceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT active.evening_id, catalog.dropped_count,
                (SELECT COUNT(*) FROM exchanges WHERE device_id = $device AND evening_id = active.evening_id),
                (SELECT COALESCE(SUM(content_bytes), 0) FROM exchanges
                    WHERE device_id = $device AND evening_id = active.evening_id)
            FROM evenings AS active
            JOIN evening_catalog AS catalog
                ON catalog.device_id = active.device_id AND catalog.evening_id = active.evening_id
            WHERE active.device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetString(0), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(1), MaxExchanges, MaxContentBytes);
    }

    private static string ReadActiveEvening(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT evening_id FROM evenings WHERE device_id = $device";
        command.Parameters.AddWithValue("$device", deviceId);
        return (string)command.ExecuteScalar()!;
    }

    private static EveningsList ReadList(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId)
    {
        var active = ReadActiveEvening(connection, transaction, deviceId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT catalog.evening_id, catalog.name, catalog.created_at_ms,
                (SELECT COUNT(*) FROM exchanges AS exchange
                    WHERE exchange.device_id = catalog.device_id
                      AND exchange.evening_id = catalog.evening_id),
                catalog.dropped_count
            FROM evening_catalog AS catalog
            WHERE catalog.device_id = $device
            ORDER BY catalog.created_at_ms, catalog.evening_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$limit", MaxEvenings);
        using var reader = command.ExecuteReader();
        var evenings = new List<EveningSummary>();
        while (reader.Read())
        {
            evenings.Add(new(
                reader.GetString(0),
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }

        return new(active, evenings);
    }

    private static EveningMutationResult? ReadMutation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId,
        Guid key,
        string route,
        string bodyHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT route, body_hash, status_code, response_json FROM evening_mutations
            WHERE device_id = $device AND key = $key AND created_at_ms > $cutoff;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue(
            "$cutoff", DateTimeOffset.UtcNow.Subtract(MutationRetention).ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        if (reader.GetString(0) != route || reader.GetString(1) != bodyHash)
        {
            return new(409, null, "idempotency_conflict");
        }

        var response = JsonSerializer.Deserialize<EveningsList>(reader.GetString(3), MutationJsonOptions)
            ?? throw new InvalidDataException("Persisted evening mutation is invalid.");
        return new(reader.GetInt32(2), response, null);
    }

    private static void PruneExpiredMutations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM evening_mutations WHERE device_id = $device AND created_at_ms <= $cutoff;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue(
            "$cutoff", DateTimeOffset.UtcNow.Subtract(MutationRetention).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static bool HasMutationCapacity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM evening_mutations WHERE device_id = $device";
        command.Parameters.AddWithValue("$device", deviceId);
        return (long)command.ExecuteScalar()! < MaxMutationsPerDevice;
    }

    private static void WriteMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        Guid key,
        string route,
        string bodyHash,
        int status,
        EveningsList response)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO evening_mutations
                (device_id, key, route, body_hash, status_code, response_json, created_at_ms)
            VALUES ($device, $key, $route, $hash, $status, $response, $now);
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$hash", bodyHash);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$response", JsonSerializer.Serialize(response, MutationJsonOptions));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static HashSet<string> Words(string text)
    {
        var normalized = new string(text.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray()).ToLowerInvariant();
        return Regex.Matches(normalized, @"\p{L}{3,}")
            .Select(match => match.Value)
            .Where(word => word is not ("jsem" or "jako" or "chci" or "hulku" or "tady" or "jeste" or "tak" or "ale" or "pro"))
            .ToHashSet(StringComparer.Ordinal);
    }
}

internal sealed record EveningContext(string EveningId, string Transcript);
internal sealed record EveningMemoryStatus(string EveningId, int ExchangeCount, int ContentBytes,
    int DroppedExchangeCount, int MaxExchanges, int MaxContentBytes);
internal sealed record EveningSummary(
    string EveningId,
    string Name,
    DateTimeOffset CreatedAt,
    int ExchangeCount,
    int DroppedCount);
internal sealed record EveningsList(string ActiveEveningId, List<EveningSummary> Evenings);
internal sealed record EveningMutationResult(int Status, EveningsList? Response, string? ErrorCode);
internal sealed record MemoryExchange(long Sequence, string ConversationId, string VisitorText, string Reply);

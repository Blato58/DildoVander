using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// Content lives separately from the technical journal and its retention/backups.
internal sealed class EveningMemoryStore
{
    internal const int MaxExchanges = 2000;
    internal const int MaxContentBytes = 2_000_000;
    internal const int MaxContextCharacters = 16_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string connectionString;
    private readonly Lock gate = new();

    public EveningMemoryStore(IConfiguration configuration, OperationJournal journal)
    {
        // Resolving the journal first acquires the single-backend instance lock.
        _ = journal;
        var databasePath = configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Backend:DatabasePath must be configured.");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath) + ".evening.sqlite",
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
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
            """;
        command.ExecuteNonQuery();
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
                WHERE device_id = $device ORDER BY sequence;
                """;
            command.Parameters.AddWithValue("$device", deviceId);
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
                            FROM exchanges WHERE device_id = $device
                        ) WHERE row_number > $maxRows OR running_bytes > $maxBytes
                    );
                    """;
                trim.Parameters.AddWithValue("$device", deviceId);
                trim.Parameters.AddWithValue("$maxRows", MaxExchanges);
                trim.Parameters.AddWithValue("$maxBytes", MaxContentBytes);
                var dropped = trim.ExecuteNonQuery();
                using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE evenings SET dropped_count = dropped_count + $count WHERE device_id = $device";
                update.Parameters.AddWithValue("$count", dropped);
                update.Parameters.AddWithValue("$device", deviceId);
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
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$expected", expectedEveningId);
            command.Parameters.AddWithValue("$next", Guid.NewGuid().ToString("D"));
            var changed = command.ExecuteNonQuery() == 1;
            if (changed)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM exchanges WHERE device_id = $device";
                delete.Parameters.AddWithValue("$device", deviceId);
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
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO evenings(device_id, evening_id) VALUES ($device, $id)";
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
        command.ExecuteNonQuery();
    }

    private static EveningMemoryStatus ReadStatus(SqliteConnection connection, string deviceId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT evening_id, dropped_count,
                (SELECT COUNT(*) FROM exchanges WHERE device_id = $device),
                (SELECT COALESCE(SUM(content_bytes), 0) FROM exchanges WHERE device_id = $device)
            FROM evenings WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.GetString(0), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(1), MaxExchanges, MaxContentBytes);
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
internal sealed record MemoryExchange(long Sequence, string ConversationId, string VisitorText, string Reply);

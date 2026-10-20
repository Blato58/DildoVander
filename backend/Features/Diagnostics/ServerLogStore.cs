using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Diagnostics;

// Only explicitly selected technical metadata enters this database. Never call
// an arbitrary ILogger formatter or serialize a request/provider response here.
internal sealed class ServerLogStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly int capacity;
    private readonly string storeId;
    private long droppedWrites;
    internal const int MaxPageSize = 200;

    public ServerLogStore(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        capacity = Math.Clamp(configuration.GetValue("Backend:Logs:Capacity", 5000), 200, 20000);
        var path = configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Backend:DatabasePath must be configured.");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path) + ".logs.sqlite", DefaultTimeout = 1
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS log_identity (id TEXT NOT NULL);
            INSERT INTO log_identity SELECT $id WHERE NOT EXISTS (SELECT 1 FROM log_identity);
            CREATE TABLE IF NOT EXISTS server_logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT, timestamp_ms INTEGER NOT NULL,
                severity INTEGER NOT NULL, trace_id TEXT, operation_id TEXT, payload TEXT NOT NULL);
            """;
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.ExecuteNonQuery();
        command.CommandText = "SELECT id FROM log_identity LIMIT 1";
        storeId = (string)command.ExecuteScalar()!;
    }

    public void Write(string level, string eventCode, string? traceId = null, string? route = null,
        string? method = null, int? status = null, double? durationMs = null,
        string? operationId = null, string? conversationId = null, string? code = null)
    {
        lock (gate)
        {
            try
            {
                var time = clock.GetUtcNow();
                var entry = new ServerLogEntry(0, time.ToString("O", CultureInfo.InvariantCulture),
                    level, eventCode, traceId, route, method, status, durationMs, operationId, conversationId, code);
                using var connection = Open();
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO server_logs(timestamp_ms, severity, trace_id, operation_id, payload)
                    VALUES ($time, $severity, $trace, $operation, $payload);
                    """;
                command.Parameters.AddWithValue("$time", time.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$severity", Severity(level));
                command.Parameters.AddWithValue("$trace", (object?)traceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$operation", (object?)operationId ?? DBNull.Value);
                command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(entry));
                command.ExecuteNonQuery();
                Prune(connection, transaction);
                transaction.Commit();
            }
            catch (Exception error) when (error is SqliteException or IOException)
            {
                // Logging failure must not break a live audio session or trigger
                // recursive logging. Expose the loss to diagnostic readers.
                droppedWrites++;
            }
        }
    }

    public ServerLogPage Read(long? after, int limit, string level, string? traceId, string? operationId)
    {
        lock (gate)
        {
            using var connection = Open();
            Prune(connection, null);
            using var bounds = connection.CreateCommand();
            bounds.CommandText = "SELECT COALESCE(MIN(id),0), COALESCE(MAX(id),0) FROM server_logs";
            long oldest, latest;
            using (var reader = bounds.ExecuteReader())
            {
                reader.Read(); oldest = reader.GetInt64(0); latest = reader.GetInt64(1);
            }
            var truncated = after.HasValue && (after > latest || after > 0 && after < oldest - 1);
            var tail = !after.HasValue || after > latest;
            using var command = connection.CreateCommand();
            command.CommandText = $"""
                SELECT id, payload FROM server_logs
                WHERE severity >= $severity AND ($trace IS NULL OR trace_id = $trace)
                    AND ($operation IS NULL OR operation_id = $operation) AND id > $after
                ORDER BY id {(tail ? "DESC" : "ASC")} LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$severity", Severity(level));
            command.Parameters.AddWithValue("$trace", (object?)traceId ?? DBNull.Value);
            command.Parameters.AddWithValue("$operation", (object?)operationId ?? DBNull.Value);
            command.Parameters.AddWithValue("$after", tail ? 0 : after!.Value);
            command.Parameters.AddWithValue("$limit", limit + 1);
            var entries = new List<ServerLogEntry>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    entries.Add(JsonSerializer.Deserialize<ServerLogEntry>(reader.GetString(1))! with { Id = reader.GetInt64(0) });
            }
            var hasMore = !tail && entries.Count > limit;
            if (entries.Count > limit) entries.RemoveAt(entries.Count - 1);
            if (tail) entries.Reverse();
            return new(storeId, oldest, latest, hasMore ? entries[^1].Id : latest,
                hasMore, truncated, droppedWrites, entries);
        }
    }

    internal static int Severity(string level) => level switch
    {
        "info" => 0, "warning" => 1, "error" => 2,
        _ => throw new ArgumentException("Invalid log level.")
    };

    private void Prune(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM server_logs WHERE timestamp_ms < $cutoff OR id <=
                COALESCE((SELECT id FROM server_logs ORDER BY id DESC LIMIT 1 OFFSET $capacity), 0);
            """;
        command.Parameters.AddWithValue("$cutoff", clock.GetUtcNow().AddHours(-24).ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$capacity", capacity);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

internal sealed record ServerLogEntry(long Id, string Timestamp, string Level, string EventCode,
    string? TraceId, string? Route, string? Method, int? Status, double? DurationMs,
    string? OperationId, string? ConversationId, string? Code);
internal sealed record ServerLogPage(string StoreId, long OldestCursor, long LatestCursor,
    long NextCursor, bool HasMore, bool Truncated, long DroppedWrites, IReadOnlyList<ServerLogEntry> Entries);

using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// This journal stores only technical identities and body hashes, never visitor text.
internal sealed class OperationJournal : IDisposable
{
    private readonly string connectionString;
    private readonly FileStream instanceLock;
    private readonly TimeProvider clock;

    public OperationJournal(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        var databasePath = configuration["Backend:DatabasePath"];
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("Backend:DatabasePath must be configured.");
        }

        var absolutePath = Path.GetFullPath(databasePath);
        if (!Directory.Exists(Path.GetDirectoryName(absolutePath)))
        {
            throw new InvalidOperationException("Backend database directory does not exist.");
        }

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = absolutePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();

        try
        {
            // Startup recovery assumes exactly one backend process owns this database.
            instanceLock = new FileStream(
                absolutePath + ".instance.lock",
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Backend database is already owned by another process.", exception);
        }

        try
        {
            Initialize();
        }
        catch
        {
            instanceLock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        instanceLock.Dispose();
    }

    private void Initialize()
    {
        using var connection = Open();
        using var version = connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        var schemaVersion = (long)version.ExecuteScalar()!;
        if (schemaVersion is not 0 and not 1 and not 2 and not 3)
        {
            throw new InvalidOperationException("Unsupported backend database schema version.");
        }

        using var migration = connection.BeginTransaction();
        using var schema = connection.CreateCommand();
        schema.Transaction = migration;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS turn_operations (
                operation_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                boot_id TEXT NOT NULL,
                client_turn_id TEXT NOT NULL,
                generation INTEGER NOT NULL,
                device_revision INTEGER NOT NULL,
                status TEXT NOT NULL,
                expires_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS turn_idempotency (
                device_id TEXT NOT NULL,
                route TEXT NOT NULL,
                key TEXT NOT NULL,
                body_hash TEXT NOT NULL,
                operation_id TEXT NOT NULL,
                expires_at_ms INTEGER NOT NULL,
                PRIMARY KEY (device_id, route, key)
            );
            CREATE TABLE IF NOT EXISTS conversation_turn_usage (
                conversation_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                reserved_turns INTEGER NOT NULL CHECK (reserved_turns BETWEEN 0 AND 10),
                expires_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS conversation_creates (
                device_id TEXT NOT NULL,
                key TEXT NOT NULL,
                body_hash TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                response_expires_at_ms INTEGER NOT NULL,
                record_expires_at_ms INTEGER NOT NULL,
                PRIMARY KEY (device_id, key)
            );
            CREATE INDEX IF NOT EXISTS ix_turn_operations_expiry ON turn_operations(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_turn_idempotency_expiry ON turn_idempotency(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_conversation_turn_usage_expiry ON conversation_turn_usage(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_conversation_creates_expiry ON conversation_creates(record_expires_at_ms);
            PRAGMA user_version=3;
            """;
        schema.ExecuteNonQuery();
        migration.Commit();

        // A crash after reservation must never trigger automatic provider replay.
        using var recover = connection.CreateCommand();
        recover.CommandText = "UPDATE turn_operations SET status = 'unknown' WHERE status IN ('pending', 'running')";
        recover.ExecuteNonQuery();
    }

    public JournalReservation? FindExisting(string deviceId, string route, Guid key, string bodyHash)
    {
        using var connection = Open();
        return FindExisting(connection, null, deviceId, route, key, bodyHash);
    }

    public JournalCreateReservation? FindExistingCreate(string deviceId, Guid key, string bodyHash)
    {
        using var connection = Open();
        return FindExistingCreate(connection, null, deviceId, key, bodyHash);
    }

    public JournalCreateReservation ReserveCreate(string deviceId, Guid key, string bodyHash)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM conversation_creates WHERE record_expires_at_ms <= $now";
            cleanup.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
            cleanup.ExecuteNonQuery();
        }

        var existing = FindExistingCreate(connection, transaction, deviceId, key, bodyHash);
        if (existing is not null)
        {
            transaction.Commit();
            return existing;
        }

        var now = clock.GetUtcNow();
        var response = new ConversationResponse(
            Guid.NewGuid().ToString("D"),
            now.AddMinutes(15),
            new ConversationLimits(10, 1000, 900, 1800));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_creates
                (device_id, key, body_hash, conversation_id, response_expires_at_ms, record_expires_at_ms)
            VALUES ($device, $key, $hash, $conversation, $responseExpiry, $recordExpiry)
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$hash", bodyHash);
        command.Parameters.AddWithValue("$conversation", response.ConversationId);
        command.Parameters.AddWithValue("$responseExpiry", response.ExpiresAt.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$recordExpiry", now.AddHours(24).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        transaction.Commit();
        return new JournalCreateReservation(response, false, false);
    }

    public JournalReservation Reserve(
        string deviceId,
        string route,
        Guid key,
        string bodyHash,
        TurnCorrelation correlation)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = """
                DELETE FROM turn_idempotency WHERE expires_at_ms <= $now;
                DELETE FROM turn_operations WHERE expires_at_ms <= $now;
                DELETE FROM conversation_turn_usage WHERE expires_at_ms <= $now;
                """;
            cleanup.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
            cleanup.ExecuteNonQuery();
        }

        var existing = FindExisting(connection, transaction, deviceId, route, key, bodyHash);
        if (existing is not null)
        {
            transaction.Commit();
            return existing;
        }

        using (var usage = connection.CreateCommand())
        {
            usage.Transaction = transaction;
            usage.CommandText = """
                SELECT reserved_turns FROM conversation_turn_usage
                WHERE conversation_id = $conversation AND device_id = $device
                """;
            usage.Parameters.AddWithValue("$conversation", correlation.ConversationId);
            usage.Parameters.AddWithValue("$device", deviceId);
            var reservedCount = usage.ExecuteScalar();
            if (reservedCount is not null && Convert.ToInt32(reservedCount) >= 10)
            {
                transaction.Commit();
                return new JournalReservation(string.Empty, false, false, true);
            }
        }

        var operationId = Guid.NewGuid().ToString("D");
        var expiresAt = clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds();

        using (var usage = connection.CreateCommand())
        {
            usage.Transaction = transaction;
            usage.CommandText = """
                INSERT INTO conversation_turn_usage
                    (conversation_id, device_id, reserved_turns, expires_at_ms)
                VALUES ($conversation, $device, 1, $expires)
                ON CONFLICT(conversation_id) DO UPDATE SET
                    reserved_turns = reserved_turns + 1
                WHERE device_id = excluded.device_id
                """;
            usage.Parameters.AddWithValue("$conversation", correlation.ConversationId);
            usage.Parameters.AddWithValue("$device", deviceId);
            usage.Parameters.AddWithValue("$expires", expiresAt);
            if (usage.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("Conversation usage has a different owner.");
            }
        }

        using (var operation = connection.CreateCommand())
        {
            operation.Transaction = transaction;
            operation.CommandText = """
                INSERT INTO turn_operations
                    (operation_id, device_id, conversation_id, boot_id, client_turn_id,
                     generation, device_revision, status, expires_at_ms)
                VALUES ($operation, $device, $conversation, $boot, $turn,
                        $generation, $revision, 'pending', $expires)
                """;
            operation.Parameters.AddWithValue("$operation", operationId);
            operation.Parameters.AddWithValue("$device", deviceId);
            operation.Parameters.AddWithValue("$conversation", correlation.ConversationId);
            operation.Parameters.AddWithValue("$boot", correlation.BootId.ToString("D"));
            operation.Parameters.AddWithValue("$turn", correlation.ClientTurnId.ToString("D"));
            operation.Parameters.AddWithValue("$generation", correlation.Generation);
            operation.Parameters.AddWithValue("$revision", correlation.DeviceRevision);
            operation.Parameters.AddWithValue("$expires", expiresAt);
            operation.ExecuteNonQuery();
        }

        using (var idempotency = connection.CreateCommand())
        {
            idempotency.Transaction = transaction;
            idempotency.CommandText = """
                INSERT INTO turn_idempotency
                    (device_id, route, key, body_hash, operation_id, expires_at_ms)
                VALUES ($device, $route, $key, $hash, $operation, $expires)
                """;
            idempotency.Parameters.AddWithValue("$device", deviceId);
            idempotency.Parameters.AddWithValue("$route", route);
            idempotency.Parameters.AddWithValue("$key", key.ToString("D"));
            idempotency.Parameters.AddWithValue("$hash", bodyHash);
            idempotency.Parameters.AddWithValue("$operation", operationId);
            idempotency.Parameters.AddWithValue("$expires", expiresAt);
            idempotency.ExecuteNonQuery();
        }

        transaction.Commit();
        return new JournalReservation(operationId, false, false, false);
    }

    public void MarkCompleted(string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE turn_operations SET status = 'completed' WHERE operation_id = $operation";
        command.Parameters.AddWithValue("$operation", operationId);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException("Reserved operation was not found.");
        }
    }

    public GetOperationResult GetOperation(string deviceId, string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, conversation_id, boot_id, client_turn_id,
                   generation, device_revision, status
            FROM turn_operations
            WHERE operation_id = $operation AND expires_at_ms > $now
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new GetOperationResult(404, null);
        }

        if (reader.GetString(0) != deviceId)
        {
            return new GetOperationResult(403, null);
        }

        var status = reader.GetString(6);
        var correlation = new TurnCorrelation(
            deviceId,
            Guid.Parse(reader.GetString(2)),
            reader.GetString(1),
            Guid.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetInt32(5));
        var operation = status == "completed"
            ? new TurnOperation(operationId, "turn", "failed", correlation, null, new { code = "result_expired" })
            : new TurnOperation(operationId, "turn", status, correlation, null, null);
        return new GetOperationResult(200, operation);
    }

    private JournalReservation? FindExisting(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId,
        string route,
        Guid key,
        string bodyHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT body_hash, operation_id
            FROM turn_idempotency
            WHERE device_id = $device AND route = $route AND key = $key
              AND expires_at_ms > $now
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new JournalReservation(reader.GetString(1), true, reader.GetString(0) != bodyHash, false);
    }

    private JournalCreateReservation? FindExistingCreate(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId,
        Guid key,
        string bodyHash)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT body_hash, conversation_id, response_expires_at_ms
            FROM conversation_creates
            WHERE device_id = $device AND key = $key AND record_expires_at_ms > $now
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        if (reader.GetString(0) != bodyHash)
        {
            return new JournalCreateReservation(null, true, true);
        }

        var response = new ConversationResponse(
            reader.GetString(1),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
            new ConversationLimits(10, 1000, 900, 1800));
        return new JournalCreateReservation(response, true, false);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

internal sealed record JournalReservation(string OperationId, bool Existing, bool Conflict, bool LimitExceeded);
internal sealed record JournalCreateReservation(ConversationResponse? Response, bool Existing, bool Conflict);

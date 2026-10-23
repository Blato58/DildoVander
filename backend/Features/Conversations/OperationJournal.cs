using System.Security.Cryptography;
using Kufr.Backend.Features.Usage;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// Operation tables contain technical identities/body hashes. The explicitly
// named visual_media archive is consent-scoped content with manual deletion.
internal sealed partial class OperationJournal : IDisposable
{
    private readonly string connectionString;
    private readonly FileStream instanceLock;
    private readonly TimeProvider clock;

    public OperationJournal(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        waitingLimits = Kufr.Backend.Features.Customers.CustomerWaitingLimits.Read(configuration);
        keepsakesEnabled = configuration.GetValue<bool>("Keepsake:Enabled");
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
        if (schemaVersion is not 0 and not 1 and not 2 and not 3 and not 4 and not 5 and not 6 and not 7 and not 8)
        {
            throw new InvalidOperationException("Unsupported backend database schema version.");
        }

        using var migration = connection.BeginTransaction();
        using var schema = connection.CreateCommand();
        schema.Transaction = migration;
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS server_settings (
                name TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
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
            CREATE TABLE IF NOT EXISTS mutation_idempotency (
                device_id TEXT NOT NULL,
                route TEXT NOT NULL,
                key TEXT NOT NULL,
                body_hash TEXT NOT NULL,
                expires_at_ms INTEGER NOT NULL,
                PRIMARY KEY (device_id, route, key)
            );
            CREATE TABLE IF NOT EXISTS cost_reservations (
                operation_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                conversation_id TEXT NOT NULL,
                day_utc TEXT NOT NULL,
                month_utc TEXT NOT NULL,
                reserved_micro_usd INTEGER NOT NULL CHECK (reserved_micro_usd > 0),
                actual_micro_usd INTEGER CHECK (actual_micro_usd >= 0),
                status TEXT NOT NULL CHECK (status IN ('reserved', 'settled')),
                created_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cost_session_usage (
                conversation_id TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                charged_micro_usd INTEGER NOT NULL CHECK (charged_micro_usd >= 0),
                updated_at_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS cost_period_usage (
                period_kind TEXT NOT NULL CHECK (period_kind IN ('day', 'month')),
                period_key TEXT NOT NULL,
                charged_micro_usd INTEGER NOT NULL CHECK (charged_micro_usd >= 0),
                updated_at_ms INTEGER NOT NULL,
                PRIMARY KEY (period_kind, period_key)
            );
            CREATE INDEX IF NOT EXISTS ix_turn_operations_expiry ON turn_operations(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_turn_idempotency_expiry ON turn_idempotency(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_conversation_turn_usage_expiry ON conversation_turn_usage(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_conversation_creates_expiry ON conversation_creates(record_expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_mutation_idempotency_expiry ON mutation_idempotency(expires_at_ms);
            CREATE INDEX IF NOT EXISTS ix_cost_reservations_created ON cost_reservations(created_at_ms);
            CREATE INDEX IF NOT EXISTS ix_cost_session_usage_updated ON cost_session_usage(updated_at_ms);
            CREATE INDEX IF NOT EXISTS ix_cost_period_usage_updated ON cost_period_usage(updated_at_ms);
            """;
        schema.ExecuteNonQuery();
        if (schemaVersion < 5)
        {
            schema.CommandText = "ALTER TABLE turn_operations ADD COLUMN kind TEXT NOT NULL DEFAULT 'turn';";
            schema.ExecuteNonQuery();
        }

        schema.CommandText = "PRAGMA table_info(turn_operations)";
        using (var columns = schema.ExecuteReader())
        {
            var hasOperationEpoch = false;
            while (columns.Read())
            {
                hasOperationEpoch |= columns.GetString(1) == "operation_epoch";
            }
            columns.Close();
            if (!hasOperationEpoch)
            {
                if (schemaVersion >= 8)
                {
                    throw new InvalidOperationException("Backend database schema is missing operation epoch storage.");
                }
                schema.CommandText = "ALTER TABLE turn_operations ADD COLUMN operation_epoch TEXT";
                schema.ExecuteNonQuery();
            }
        }

        schema.CommandText = "PRAGMA user_version=8";
        schema.ExecuteNonQuery();
        migration.Commit();

        EnsureLabProvenance(connection);
        InitializeCustomerSessions(connection);
        InitializeCustomerWaiting(connection);
        InitializeCustomerWaitingAdmission(connection);

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

    public JournalCreateReservation ReserveCreate(string deviceId, Guid key, string bodyHash,
        string? labSessionId = null, string? labRevisionId = null, DateTimeOffset? labExpiresAt = null, string? labOwnerId = null,
        string? customerSessionId = null, string? bootId = null)
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

        try
        {
            RequireNoCustomerWaitingClaim(connection, transaction, deviceId);
        }
        catch (CustomerSessionException error) when (error.Code == "waiting_claim_reserved")
        {
            return new JournalCreateReservation(null, false, true);
        }

        using (var lease = CustomerCommand(connection, transaction,
            "SELECT operation_id,customer_session_id,conversation_id FROM device_conversation_leases WHERE device_id=$device", ("$device", deviceId)))
        using (var reader = lease.ExecuteReader())
        {
            if (reader.Read() && (customerSessionId is null || reader.GetString(0) != key.ToString("D")
                || reader.IsDBNull(1) || reader.GetString(1) != customerSessionId || !reader.IsDBNull(2)))
            { return new JournalCreateReservation(null, false, true); }
        }

        var now = clock.GetUtcNow();
        var response = new ConversationResponse(
            Guid.NewGuid().ToString("D"),
            labExpiresAt is { } labExpiry
                ? DateTimeOffset.FromUnixTimeMilliseconds(labExpiry.ToUnixTimeMilliseconds()) : now.AddMinutes(15),
            new ConversationLimits(10, 1000, labExpiresAt is null ? 900 : 180, labExpiresAt is null ? 1800 : 180));
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
        CustomerExecute(connection, transaction, """
            INSERT INTO device_conversation_leases(device_id,operation_id,customer_session_id,conversation_id,boot_id)
                VALUES($device,$key,$session,$conversation,$boot)
            ON CONFLICT(device_id) DO UPDATE SET conversation_id=$conversation;
            """, ("$device", deviceId), ("$key", key.ToString("D")), ("$session", customerSessionId),
            ("$conversation", response.ConversationId), ("$boot", bootId));
        if (customerSessionId is not null)
        {
            CustomerExecute(connection, transaction,
                "UPDATE customer_sessions SET conversation_id=$conversation,state='context_ready' WHERE id=$session AND state='starting'",
                ("$conversation", response.ConversationId), ("$session", customerSessionId));
        }
        if (labSessionId is not null)
        {
            RecordLabCreate(connection, transaction, deviceId, response.ConversationId, labSessionId, labRevisionId!, labOwnerId!);
        }
        transaction.Commit();
        return new JournalCreateReservation(response, false, false);
    }

    internal long ReadSessionCharge(string conversationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT charged_micro_usd FROM cost_session_usage WHERE conversation_id = $id";
        command.Parameters.AddWithValue("$id", conversationId);
        var charged = command.ExecuteScalar() is long amount ? amount : 0;
        return charged;
    }

    public JournalReservation Reserve(
        string deviceId,
        string route,
        Guid key,
        string bodyHash,
        TurnCorrelation correlation,
        string kind = "turn",
        bool consumesTurn = true,
        long estimatedCostMicroUsd = 0)
    {
        if (!OperationEpoch.IsValid(correlation.OperationEpoch))
        {
            throw new ArgumentException("Invalid operation epoch.", nameof(correlation));
        }
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
            if (!existing.Conflict)
            {
                using var identity = connection.CreateCommand();
                identity.Transaction = transaction;
                identity.CommandText = "SELECT operation_epoch FROM turn_operations WHERE operation_id=$operation";
                identity.Parameters.AddWithValue("$operation", existing.OperationId);
                using var storedIdentity = identity.ExecuteReader();
                if (!storedIdentity.Read()
                    || !TryReadOperationEpoch(storedIdentity, 0, out var storedEpoch)
                    || !OperationEpoch.Same(storedEpoch, correlation.OperationEpoch))
                {
                    existing = existing with { Conflict = true };
                }
            }
            transaction.Commit();
            return existing;
        }

        if (consumesTurn)
        {
            using var usage = connection.CreateCommand();
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
        if (!CostReservations.TryReserve(
            connection,
            transaction,
            clock.GetUtcNow(),
            operationId,
            deviceId,
            correlation.ConversationId,
            estimatedCostMicroUsd))
        {
            transaction.Commit();
            return new JournalReservation(string.Empty, false, false, false, true);
        }

        if (consumesTurn)
        {
            using var usage = connection.CreateCommand();
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
                     generation, device_revision, status, expires_at_ms, kind, operation_epoch)
                VALUES ($operation, $device, $conversation, $boot, $turn,
                        $generation, $revision, 'pending', $expires, $kind, $operationEpoch)
                """;
            operation.Parameters.AddWithValue("$operation", operationId);
            operation.Parameters.AddWithValue("$device", deviceId);
            operation.Parameters.AddWithValue("$conversation", correlation.ConversationId);
            operation.Parameters.AddWithValue("$boot", correlation.BootId.ToString("D"));
            operation.Parameters.AddWithValue("$turn", correlation.ClientTurnId.ToString("D"));
            operation.Parameters.AddWithValue("$generation", correlation.Generation);
            operation.Parameters.AddWithValue("$revision", correlation.DeviceRevision);
            operation.Parameters.AddWithValue("$expires", expiresAt);
            operation.Parameters.AddWithValue("$kind", kind);
            operation.Parameters.AddWithValue("$operationEpoch", (object?)correlation.OperationEpoch ?? DBNull.Value);
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

        RecordLabOperation(connection, transaction, deviceId, correlation.ConversationId, operationId);
        transaction.Commit();
        return new JournalReservation(operationId, false, false, false);
    }

    public bool MarkCompleted(string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE turn_operations SET status = 'completed'
            WHERE operation_id = $operation AND status IN ('pending', 'running')
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkRunning(string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE turn_operations SET status = 'running'
            WHERE operation_id = $operation AND status = 'pending'
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool MarkFailed(string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE turn_operations SET status = 'failed'
            WHERE operation_id = $operation AND status IN ('pending', 'running')
            """;
        command.Parameters.AddWithValue("$operation", operationId);
        return command.ExecuteNonQuery() == 1;
    }

    public bool SettleCost(string operationId, long actualMicroUsd)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var settled = CostReservations.Settle(
            connection,
            transaction,
            operationId,
            actualMicroUsd,
            clock.GetUtcNow());
        transaction.Commit();
        return settled;
    }

    public MutationResult? FindMutation(string deviceId, string route, Guid key)
    {
        using var connection = Open();
        return FindMutation(connection, null, deviceId, route, key);
    }

    public MutationResult CancelOperation(string deviceId, string operationId, Guid key)
    {
        var route = $"DELETE:/v1/operations/{operationId}";
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var previous = FindMutation(connection, transaction, deviceId, route, key);
        if (previous is not null)
        {
            transaction.Commit();
            return previous;
        }

        using (var owner = connection.CreateCommand())
        {
            owner.Transaction = transaction;
            owner.CommandText = """
                SELECT device_id FROM turn_operations
                WHERE operation_id = $operation AND expires_at_ms > $now
                """;
            owner.Parameters.AddWithValue("$operation", operationId);
            owner.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
            var ownerId = owner.ExecuteScalar() as string;
            if (ownerId is null)
            {
                transaction.Commit();
                return new MutationResult(404, false);
            }

            if (ownerId != deviceId)
            {
                transaction.Commit();
                return new MutationResult(403, false);
            }
        }

        using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = "UPDATE turn_operations SET status = 'cancelled' WHERE operation_id = $operation";
            cancel.Parameters.AddWithValue("$operation", operationId);
            cancel.ExecuteNonQuery();
        }

        RecordMutation(connection, transaction, deviceId, route, key);
        transaction.Commit();
        return new MutationResult(204, false);
    }

    public MutationResult EndConversation(string deviceId, string conversationId, Guid key)
    {
        var route = $"DELETE:/v1/conversations/{conversationId}";
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var previous = FindMutation(connection, transaction, deviceId, route, key);
        if (previous is not null)
        {
            transaction.Commit();
            return previous;
        }

        using (var cancel = connection.CreateCommand())
        {
            cancel.Transaction = transaction;
            cancel.CommandText = """
                UPDATE turn_operations SET status = 'cancelled'
                WHERE device_id = $device AND conversation_id = $conversation
                  AND status IN ('pending', 'running', 'completed')
                """;
            cancel.Parameters.AddWithValue("$device", deviceId);
            cancel.Parameters.AddWithValue("$conversation", conversationId);
            cancel.ExecuteNonQuery();
        }

        CustomerExecute(connection, transaction,
            "DELETE FROM device_conversation_leases WHERE device_id=$device AND conversation_id=$conversation AND customer_session_id IS NULL",
            ("$device", deviceId), ("$conversation", conversationId));
        RecordMutation(connection, transaction, deviceId, route, key);
        transaction.Commit();
        return new MutationResult(204, false);
    }

    public void PurgeExpired()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM turn_idempotency WHERE expires_at_ms <= $now;
            DELETE FROM turn_operations WHERE expires_at_ms <= $now;
            DELETE FROM conversation_turn_usage WHERE expires_at_ms <= $now;
            DELETE FROM conversation_creates WHERE record_expires_at_ms <= $now;
            DELETE FROM mutation_idempotency WHERE expires_at_ms <= $now;
            """;
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
        CostReservations.Purge(connection, transaction, clock.GetUtcNow());
        transaction.Commit();
    }

    public GetOperationResult GetOperation(string deviceId, string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, conversation_id, boot_id, client_turn_id,
                   generation, device_revision, status, kind, operation_epoch
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
        var kind = reader.GetString(7);
        if (!TryReadOperationEpoch(reader, 8, out var operationEpoch))
        {
            throw new InvalidOperationException("Persisted operation epoch is invalid.");
        }
        var correlation = new TurnCorrelation(
            deviceId,
            Guid.Parse(reader.GetString(2)),
            reader.GetString(1),
            Guid.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetInt32(5),
            operationEpoch);
        var operation = status switch
        {
            "completed" => new TurnOperation(
                operationId, kind, "failed", correlation, null, new { code = "result_expired" }),
            "failed" => new TurnOperation(
                operationId, kind, "failed", correlation, null, new { code = "provider_unavailable" }),
            _ => new TurnOperation(operationId, kind, status, correlation, null, null)
        };
        return new GetOperationResult(200, operation);
    }

    private static bool TryReadOperationEpoch(SqliteDataReader reader, int ordinal, out string? operationEpoch)
    {
        operationEpoch = null;
        if (reader.IsDBNull(ordinal))
        {
            return true;
        }
        // Provider string conversion can normalize a corrupt BLOB into valid
        // legacy text. Only an actual SQL NULL or TEXT identity is admissible.
        if (reader.GetValue(ordinal) is not string text || !OperationEpoch.IsValid(text))
        {
            return false;
        }
        operationEpoch = text;
        return true;
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
            SELECT body_hash, conversation_id, response_expires_at_ms,
                EXISTS (SELECT 1 FROM persona_lab_conversations lab
                    WHERE lab.conversation_id=conversation_creates.conversation_id
                    AND lab.device_id=conversation_creates.device_id)
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
            reader.GetInt64(3) != 0 ? new ConversationLimits(10, 1000, 180, 180)
                : new ConversationLimits(10, 1000, 900, 1800));
        return new JournalCreateReservation(response, true, false);
    }

    private MutationResult? FindMutation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string deviceId,
        string route,
        Guid key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT body_hash FROM mutation_idempotency
            WHERE device_id = $device AND route = $route AND key = $key
              AND expires_at_ms > $now
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        var existingHash = command.ExecuteScalar() as string;
        if (existingHash is null)
        {
            return null;
        }

        return new MutationResult(existingHash == EmptyBodyHash ? 204 : 409, true);
    }

    private void RecordMutation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string deviceId,
        string route,
        Guid key)
    {
        using var cleanup = connection.CreateCommand();
        cleanup.Transaction = transaction;
        cleanup.CommandText = "DELETE FROM mutation_idempotency WHERE expires_at_ms <= $now";
        cleanup.Parameters.AddWithValue("$now", clock.GetUtcNow().ToUnixTimeMilliseconds());
        cleanup.ExecuteNonQuery();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mutation_idempotency (device_id, route, key, body_hash, expires_at_ms)
            VALUES ($device, $route, $key, $hash, $expires)
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$route", route);
        command.Parameters.AddWithValue("$key", key.ToString("D"));
        command.Parameters.AddWithValue("$hash", EmptyBodyHash);
        command.Parameters.AddWithValue("$expires", clock.GetUtcNow().AddHours(24).ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static string EmptyBodyHash { get; } = Convert.ToHexString(SHA256.HashData([]));

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

internal sealed record JournalReservation(
    string OperationId,
    bool Existing,
    bool Conflict,
    bool LimitExceeded,
    bool CostRejected = false);
internal sealed record JournalCreateReservation(ConversationResponse? Response, bool Existing, bool Conflict);
internal sealed record MutationResult(int Status, bool Existing);

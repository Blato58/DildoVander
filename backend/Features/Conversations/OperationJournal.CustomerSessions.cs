using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// This ledger shares the operation journal's single durable owner. A phone's
// timeout never releases a device: only its authenticated runtime can do that.
internal sealed partial class OperationJournal
{
    private void InitializeCustomerSessions(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS customer_devices(
                device_id TEXT PRIMARY KEY, enabled INTEGER NOT NULL DEFAULT 0,
                boot_id TEXT, challenge TEXT, challenge_expires INTEGER NOT NULL DEFAULT 0,
                generation INTEGER NOT NULL DEFAULT 0, catalog_version TEXT, operation_epoch TEXT);
            CREATE TABLE IF NOT EXISTS customer_start_tickets(
                token_hash TEXT PRIMARY KEY, protected_token TEXT NOT NULL,
                owner_id TEXT NOT NULL, operation_id TEXT NOT NULL, fingerprint TEXT NOT NULL,
                device_id TEXT NOT NULL, boot_id TEXT NOT NULL, challenge TEXT NOT NULL,
                profile_id TEXT NOT NULL, profile_revision INTEGER NOT NULL, snapshot TEXT NOT NULL,
                expires_at INTEGER NOT NULL, session_id TEXT, openai_consent INTEGER NOT NULL DEFAULT 0, UNIQUE(owner_id,operation_id));
            CREATE TABLE IF NOT EXISTS customer_sessions(
                id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                device_id TEXT NOT NULL, boot_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                profile_revision INTEGER NOT NULL, snapshot TEXT NOT NULL, state TEXT NOT NULL,
                conversation_id TEXT, end_requested INTEGER NOT NULL DEFAULT 0,
                created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL, UNIQUE(owner_id,operation_id));
            CREATE INDEX IF NOT EXISTS customer_sessions_owner ON customer_sessions(owner_id,created_at);
            CREATE TABLE IF NOT EXISTS customer_profile_revocations(
                owner_id TEXT NOT NULL, profile_id TEXT NOT NULL, PRIMARY KEY(owner_id,profile_id));
            CREATE TABLE IF NOT EXISTS device_conversation_leases(
                device_id TEXT PRIMARY KEY, operation_id TEXT NOT NULL,
                customer_session_id TEXT, conversation_id TEXT, boot_id TEXT);
            UPDATE customer_sessions SET state='recovering'
                WHERE state IN ('starting','context_ready','running');
            """;
        command.ExecuteNonQuery();
        using var column = connection.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('customer_start_tickets') WHERE name='openai_consent'";
        if ((long)column.ExecuteScalar()! == 0)
        {
            column.CommandText = "ALTER TABLE customer_start_tickets ADD COLUMN openai_consent INTEGER NOT NULL DEFAULT 0";
            column.ExecuteNonQuery();
        }
    }

    internal bool CustomerMode(string device)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT enabled FROM customer_devices WHERE device_id=$device", ("$device", device));
        return command.ExecuteScalar() is long value && value == 1;
    }

    internal void SetCustomerMode(string device, bool enabled)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        CustomerExecute(connection, transaction, """
            INSERT INTO customer_devices(device_id,enabled) VALUES($device,$enabled)
            ON CONFLICT(device_id) DO UPDATE SET enabled=$enabled;
            """, ("$device", device), ("$enabled", enabled ? 1 : 0));
        if (!enabled)
        {
            CustomerExecute(connection, transaction,
                "UPDATE customer_sessions SET end_requested=1 WHERE device_id=$device AND state NOT IN ('completed','failed')",
                ("$device", device));
        }
        transaction.Commit();
    }

    internal void RegisterCustomerChallenge(string device, CustomerDeviceChallenge request)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        CustomerExecute(connection, transaction,
            "INSERT OR IGNORE INTO customer_devices(device_id) VALUES($device)", ("$device", device));
        // Repeated polling must not extend a challenge's original lifetime.
        CustomerExecute(connection, transaction, """
            UPDATE customer_devices SET boot_id=$boot, challenge=$challenge, challenge_expires=$expires,
                generation=$generation,catalog_version=$catalog,operation_epoch=$epoch
            WHERE device_id=$device AND (boot_id IS NULL OR boot_id<>$boot OR challenge IS NULL OR challenge<>$challenge);
            """, ("$device", device), ("$boot", request.BootId), ("$challenge", request.Challenge),
            ("$expires", CustomerNow() + 120), ("$generation", request.Generation),
            ("$catalog", request.CatalogVersion), ("$epoch", request.OperationEpoch));
        transaction.Commit();
    }

    internal CustomerTicketRecord IssueCustomerTicket(string owner, CustomerTicketRequest request,
        string snapshot, string tokenHash, string protectedToken)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!request.OpenAiConsent) { throw new CustomerSessionException(409, "openai_consent_required"); }
        var fingerprint = CustomerHash(JsonSerializer.Serialize(request));
        RequireCustomerProfileNotRevoked(connection, transaction, owner, request.ProfileId);
        using (var previous = CustomerCommand(connection, transaction, """
            SELECT fingerprint,protected_token,expires_at,session_id FROM customer_start_tickets
            WHERE owner_id=$owner AND operation_id=$op
            """, ("$owner", owner), ("$op", request.OperationId)))
        using (var reader = previous.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != fingerprint) { throw new CustomerSessionException(409, "idempotency_conflict"); }
                return new(reader.GetString(1), reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3));
            }
        }
        RequireCustomerChallenge(connection, transaction, request.DeviceId, request.BootId, request.Challenge);
        using (var count = CustomerCommand(connection, transaction,
            "SELECT COUNT(*) FROM customer_start_tickets WHERE owner_id=$owner AND expires_at>$now AND session_id IS NULL",
            ("$owner", owner), ("$now", CustomerNow())))
        {
            if ((long)count.ExecuteScalar()! >= 8) { throw new CustomerSessionException(429, "start_ticket_limit"); }
        }
        var expires = CustomerNow() + 90;
        CustomerExecute(connection, transaction, """
            INSERT INTO customer_start_tickets(token_hash,protected_token,owner_id,operation_id,fingerprint,device_id,boot_id,
                challenge,profile_id,profile_revision,snapshot,expires_at,session_id,openai_consent) VALUES(
                $hash,$protected,$owner,$op,$fingerprint,$device,$boot,$challenge,$profile,$revision,$snapshot,$expires,NULL,1)
            """, ("$hash", tokenHash), ("$protected", protectedToken), ("$owner", owner),
            ("$op", request.OperationId), ("$fingerprint", fingerprint), ("$device", request.DeviceId),
            ("$boot", request.BootId), ("$challenge", request.Challenge), ("$profile", request.ProfileId),
            ("$revision", request.ProfileRevision), ("$snapshot", snapshot), ("$expires", expires));
        transaction.Commit();
        return new(protectedToken, expires, null);
    }

    internal CustomerTicketOwner? CustomerTicketOwner(string tokenHash)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT owner_id,profile_id,profile_revision,session_id FROM customer_start_tickets WHERE token_hash=$hash", ("$hash", tokenHash));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }

    internal CustomerSessionRecord RedeemCustomerTicket(string device, CustomerRedemption request)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        string owner, profile, snapshot;
        long revision;
        using (var lookup = CustomerCommand(connection, transaction, """
            SELECT owner_id,operation_id,device_id,boot_id,challenge,profile_id,profile_revision,snapshot,expires_at,session_id,openai_consent
            FROM customer_start_tickets WHERE token_hash=$hash
            """, ("$hash", CustomerHash(request.Ticket))))
        using (var reader = lookup.ExecuteReader())
        {
            if (!reader.Read()) { throw new CustomerSessionException(404, "start_ticket_unavailable"); }
            if (reader.GetString(1) != request.OperationId || reader.GetString(2) != device
                || reader.GetString(3) != request.BootId || reader.GetString(4) != request.Challenge)
            { throw new CustomerSessionException(409, "start_ticket_binding_mismatch"); }
            if (reader.GetInt64(10) != 1) { throw new CustomerSessionException(409, "openai_consent_required"); }
            if (!reader.IsDBNull(9))
            {
                var id = reader.GetString(9);
                reader.Close();
                return ReadCustomerSession(connection, transaction, id)!;
            }
            if (reader.GetInt64(8) <= CustomerNow()) { throw new CustomerSessionException(410, "start_ticket_expired"); }
            owner = reader.GetString(0); profile = reader.GetString(5); revision = reader.GetInt64(6); snapshot = reader.GetString(7);
        }
        RequireCustomerChallenge(connection, transaction, device, request.BootId, request.Challenge);
        RequireCustomerProfileNotRevoked(connection, transaction, owner, profile);
        using (var lease = CustomerCommand(connection, transaction,
            "SELECT 1 FROM device_conversation_leases WHERE device_id=$device", ("$device", device)))
        {
            if (lease.ExecuteScalar() is not null) { throw new CustomerSessionException(409, "device_busy"); }
        }
        var session = new CustomerSessionRecord(Guid.NewGuid().ToString("D"), owner, request.OperationId, device,
            request.BootId, profile, revision, snapshot, "starting", null, false, CustomerNow());
        CustomerExecute(connection, transaction, "INSERT INTO customer_sessions VALUES($id,$owner,$op,$device,$boot,$profile,$revision,$snapshot,'starting',NULL,0,$now,$now)",
            ("$id", session.Id), ("$owner", owner), ("$op", request.OperationId), ("$device", device),
            ("$boot", request.BootId), ("$profile", profile), ("$revision", revision), ("$snapshot", snapshot), ("$now", session.CreatedAt));
        CustomerExecute(connection, transaction, "INSERT INTO device_conversation_leases VALUES($device,$op,$id,NULL,$boot)",
            ("$device", device), ("$op", request.OperationId), ("$id", session.Id), ("$boot", request.BootId));
        CustomerExecute(connection, transaction, "UPDATE customer_start_tickets SET session_id=$id WHERE token_hash=$hash",
            ("$id", session.Id), ("$hash", CustomerHash(request.Ticket)));
        transaction.Commit();
        return session;
    }

    internal CustomerSessionRecord? CustomerSession(string id, string? owner = null)
    {
        using var connection = Open();
        var row = ReadCustomerSession(connection, null, id);
        return row is not null && (owner is null || row.OwnerId == owner) ? row : null;
    }

    internal CustomerSessionRecord[] CustomerSessions(string? owner = null, bool deletionInventory = false)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT id FROM customer_sessions WHERE ($owner IS NULL OR owner_id=$owner) ORDER BY created_at DESC" + (deletionInventory ? "" : " LIMIT 100"), ("$owner", owner));
        var ids = new List<string>();
        using (var reader = command.ExecuteReader()) { while (reader.Read()) { ids.Add(reader.GetString(0)); } }
        return ids.Select(id => ReadCustomerSession(connection, null, id)!).ToArray();
    }

    internal CustomerSessionRecord? ActiveCustomerSession(string device)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT customer_session_id FROM device_conversation_leases WHERE device_id=$device", ("$device", device));
        return command.ExecuteScalar() is string id ? ReadCustomerSession(connection, null, id) : null;
    }

    internal string[] ExpiredCustomerOperations(string owner)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        // Expiry and first redemption use the same journal clock. A ticket in
        // this result can never subsequently acquire a device lease. Take the
        // same immediate writer lock as redemption: a WAL reader alone could
        // miss an in-flight redemption that already passed the expiry check.
        using var command = CustomerCommand(connection, transaction, """
            SELECT operation_id FROM customer_start_tickets
            WHERE owner_id=$owner AND session_id IS NULL AND expires_at<=$now
            ORDER BY expires_at DESC LIMIT 100
            """, ("$owner", owner), ("$now", CustomerNow()));
        var operations = new List<string>();
        using (var reader = command.ExecuteReader())
        { while (reader.Read()) { operations.Add(reader.GetString(0)); } }
        transaction.Commit();
        return operations.ToArray();
    }

    internal bool RequestCustomerEnd(string id, string owner)
    {
        using var connection = Open();
        return CustomerExecute(connection, null, "UPDATE customer_sessions SET end_requested=1 WHERE id=$id AND owner_id=$owner",
            ("$id", id), ("$owner", owner)) == 1;
    }

    internal void RevokeCustomerProfile(string owner, string? profileId = null)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        CustomerExecute(connection, transaction,
            "INSERT OR IGNORE INTO customer_profile_revocations VALUES($owner,$profile)",
            ("$owner", owner), ("$profile", profileId ?? "*"));
        CustomerExecute(connection, transaction, """
            UPDATE customer_start_tickets SET expires_at=0,snapshot='',protected_token=''
                WHERE owner_id=$owner AND ($profile IS NULL OR profile_id=$profile);
            UPDATE customer_sessions SET snapshot='',end_requested=1
                WHERE owner_id=$owner AND ($profile IS NULL OR profile_id=$profile);
            """, ("$owner", owner), ("$profile", profileId));
        transaction.Commit();
    }

    internal void SuspendCustomerAccount(string owner)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        CustomerExecute(connection, transaction, "UPDATE customer_start_tickets SET expires_at=0 WHERE owner_id=$owner AND session_id IS NULL", ("$owner", owner));
        CustomerExecute(connection, transaction, "UPDATE customer_sessions SET end_requested=1 WHERE owner_id=$owner AND state NOT IN ('completed','failed')", ("$owner", owner));
        transaction.Commit();
    }

    internal void ReportCustomerIdle(string device, string boot)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var current = CustomerCommand(connection, transaction,
            "SELECT boot_id FROM customer_devices WHERE device_id=$device", ("$device", device));
        if (current.ExecuteScalar() as string != boot) { throw new CustomerSessionException(409, "device_epoch_mismatch"); }
        // This endpoint is a physical-runtime assertion at fresh boot. It cannot
        // turn an uncertain same-boot start into a free slot.
        CustomerExecute(connection, transaction, """
            UPDATE customer_sessions SET state='failed',updated_at=$now
                WHERE device_id=$device AND boot_id<>$boot AND state NOT IN ('completed','failed');
            DELETE FROM device_conversation_leases WHERE device_id=$device AND boot_id IS NOT NULL AND boot_id<>$boot;
            """, ("$device", device), ("$boot", boot), ("$now", CustomerNow()));
        transaction.Commit();
    }

    internal string? OldBootConversation(string device, string currentBoot)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null, """
            SELECT l.conversation_id FROM device_conversation_leases l
            JOIN customer_devices d ON d.device_id=l.device_id
            WHERE l.device_id=$device AND d.boot_id=$boot AND l.boot_id<>$boot
            """, ("$device", device), ("$boot", currentBoot));
        return command.ExecuteScalar() as string;
    }

    internal void ReleaseRemovedNoncustomerConversation(string device, string conversation)
    {
        using var connection = Open();
        CustomerExecute(connection, null, """
            DELETE FROM device_conversation_leases WHERE device_id=$device
                AND conversation_id=$conversation AND customer_session_id IS NULL
            """, ("$device", device), ("$conversation", conversation));
    }

    internal void ReportCustomerState(string device, string id, CustomerDeviceState report)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var row = ReadCustomerSession(connection, transaction, id);
        if (row is null || row.DeviceId != device) { throw new CustomerSessionException(404, "customer_session_unavailable"); }
        using var current = CustomerCommand(connection, transaction,
            "SELECT boot_id FROM customer_devices WHERE device_id=$device", ("$device", device));
        if (current.ExecuteScalar() as string != report.BootId) { throw new CustomerSessionException(409, "device_epoch_mismatch"); }
        var terminal = report.State is "completed" or "failed" or "idle";
        if (row.BootId != report.BootId && !terminal) { throw new CustomerSessionException(409, "device_epoch_mismatch"); }
        if (row.State is "completed" or "failed")
        {
            if (!terminal) { throw new CustomerSessionException(409, "customer_session_terminal"); }
            return;
        }
        if (report.ConversationId is not null && report.ConversationId != row.ConversationId)
        { throw new CustomerSessionException(409, "conversation_scope_mismatch"); }
        if (report.State == "running" && row.ConversationId is null)
        { throw new CustomerSessionException(409, "customer_context_not_ready"); }
        if (!terminal && (row.State == "recovering" || row.State == "running" && report.State == "context_ready"))
        { throw new CustomerSessionException(409, "customer_state_out_of_order"); }
        var state = report.State == "idle" ? "failed" : report.State;
        CustomerExecute(connection, transaction, "UPDATE customer_sessions SET state=$state,updated_at=$now WHERE id=$id",
            ("$id", id), ("$state", state), ("$now", CustomerNow()));
        if (terminal)
        {
            CustomerExecute(connection, transaction,
                "DELETE FROM device_conversation_leases WHERE device_id=$device AND customer_session_id=$id", ("$device", device), ("$id", id));
            // A subsequent guest needs a fresh device-minted challenge.
            CustomerExecute(connection, transaction, "UPDATE customer_devices SET challenge=NULL,challenge_expires=0 WHERE device_id=$device", ("$device", device));
        }
        transaction.Commit();
    }

    internal CustomerDeviceChallenge? CustomerChallenge(string device)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT boot_id,challenge,generation,catalog_version,operation_epoch FROM customer_devices WHERE device_id=$device AND boot_id IS NOT NULL", ("$device", device));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(1, reader.GetString(0), reader.IsDBNull(1) ? "" : reader.GetString(1), reader.GetInt32(2),
            reader.IsDBNull(3) ? "" : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)) : null;
    }

    internal CustomerSessionRecord? CustomerConversation(string device, string conversation)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT id FROM customer_sessions WHERE device_id=$device AND conversation_id=$conversation", ("$device", device), ("$conversation", conversation));
        return command.ExecuteScalar() is string id ? ReadCustomerSession(connection, null, id) : null;
    }

    private static CustomerSessionRecord? ReadCustomerSession(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT id,owner_id,operation_id,device_id,boot_id,profile_id,profile_revision,snapshot,state,conversation_id,end_requested,created_at
            FROM customer_sessions WHERE id=$id
            """, ("$id", id));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.GetString(7), reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10) != 0, reader.GetInt64(11)) : null;
    }

    private void RequireCustomerChallenge(SqliteConnection connection, SqliteTransaction transaction, string device, string boot, string challenge)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT 1 FROM customer_devices WHERE device_id=$device AND enabled=1 AND boot_id=$boot
                AND challenge=$challenge AND challenge_expires>$now
            """, ("$device", device), ("$boot", boot), ("$challenge", challenge), ("$now", CustomerNow()));
        if (command.ExecuteScalar() is null) { throw new CustomerSessionException(409, "device_challenge_unavailable"); }
    }

    private static bool CustomerEnabled(SqliteConnection connection, SqliteTransaction transaction, string device)
    {
        using var command = CustomerCommand(connection, transaction, "SELECT enabled FROM customer_devices WHERE device_id=$device", ("$device", device));
        return command.ExecuteScalar() is long value && value == 1;
    }
    private static void RequireCustomerProfileNotRevoked(SqliteConnection connection, SqliteTransaction transaction, string owner, string profile)
    {
        using var command = CustomerCommand(connection, transaction,
            "SELECT 1 FROM customer_profile_revocations WHERE owner_id=$owner AND profile_id IN ($profile,'*')",
            ("$owner", owner), ("$profile", profile));
        if (command.ExecuteScalar() is not null) { throw new CustomerSessionException(410, "customer_profile_revoked"); }
    }
    private long CustomerNow() => clock.GetUtcNow().ToUnixTimeSeconds();
    internal static string CustomerHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SqliteCommand CustomerCommand(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Key, object? Value)[] values)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in values) { command.Parameters.AddWithValue(key, value ?? DBNull.Value); }
        return command;
    }
    private static int CustomerExecute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Key, object? Value)[] values)
    {
        using var command = CustomerCommand(connection, transaction, sql, values); return command.ExecuteNonQuery();
    }
}

internal sealed record CustomerDeviceChallenge(int ProtocolVersion, string BootId, string Challenge, int Generation, string CatalogVersion, string? OperationEpoch);
internal sealed record CustomerTicketRequest(int ProtocolVersion, string OperationId, string DeviceId, string BootId, string Challenge, string ProfileId, long ProfileRevision,
    bool PersonalMemoryConsent = false, bool OpenAiConsent = false);
internal sealed record CustomerRedemption(int ProtocolVersion, string Ticket, string OperationId, string BootId, string Challenge);
internal sealed record CustomerDeviceState(string BootId, string State, string? ConversationId = null);
internal sealed record CustomerTicketRecord(string ProtectedToken, long ExpiresAt, string? SessionId);
internal sealed record CustomerTicketOwner(string OwnerId, string ProfileId, long Revision, string? SessionId);
internal sealed record CustomerSessionRecord(string Id, string OwnerId, string OperationId, string DeviceId, string BootId,
    string ProfileId, long ProfileRevision, string Snapshot, string State, string? ConversationId, bool EndRequested, long CreatedAt);
internal sealed class CustomerSessionException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

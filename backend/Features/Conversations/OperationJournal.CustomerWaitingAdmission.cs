using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class OperationJournal
{
    private static void InitializeCustomerWaitingAdmission(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS customer_waiting_ticket_bindings(
                token_hash TEXT PRIMARY KEY, event_id TEXT NOT NULL, entry_id TEXT NOT NULL,
                claim_id TEXT NOT NULL UNIQUE);
            """;
        command.ExecuteNonQuery();
    }

    // Unlike the public current-event projection, the latest marker includes a
    // closed event: activating waiting is durable and cannot restore legacy admission.
    private static CustomerWaitingEvent? LatestCustomerWaitingEvent(SqliteConnection connection,
        SqliteTransaction transaction, string device)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT id,device_id,evening_id,state,revision,activation_revision,typeof(activation_revision) FROM customer_waiting_events
                WHERE device_id=$device ORDER BY rowid DESC LIMIT 1
            """, ("$device", device));
        using var row = command.ExecuteReader();
        return row.Read() ? ReadWaitingEvent(row) : null;
    }

    private CustomerWaitingClaimBinding? RequireNewCustomerWaitingTicket(SqliteConnection connection,
        SqliteTransaction transaction, string owner, CustomerTicketRequest request)
    {
        var current = LatestCustomerWaitingEvent(connection, transaction, request.DeviceId);
        if (current is null) { return null; }
        RequireWaitingActivation(current);
        if (current.State != "open") { throw WaitingError("waiting_event_unavailable"); }
        CustomerWaitingClaimBinding binding;
        using (var command = CustomerCommand(connection, transaction, """
            SELECT id FROM customer_waiting_entries WHERE event_id=$event AND owner_id=$owner
                AND profile_id=$profile AND claim_id=$operation AND state='claimed' AND claim_expires>$now
            """, ("$event", current.Id), ("$owner", owner), ("$profile", request.ProfileId),
            ("$operation", request.OperationId), ("$now", CustomerNow())))
        {
            if (command.ExecuteScalar() is not string entryId) { throw WaitingError("waiting_claim_required"); }
            binding = new(current.Id, entryId, request.OperationId);
        }
        RequireCustomerWaitingClaim(connection, transaction, owner, request.DeviceId, request.ProfileId, binding);
        return binding;
    }

    private static void BindCustomerWaitingTicket(SqliteConnection connection, SqliteTransaction transaction,
        string tokenHash, CustomerWaitingClaimBinding? binding)
    {
        if (binding is null) { return; }
        CustomerExecute(connection, transaction, "INSERT INTO customer_waiting_ticket_bindings VALUES($hash,$event,$entry,$claim)",
            ("$hash", tokenHash), ("$event", binding.EventId), ("$entry", binding.EntryId), ("$claim", binding.ClaimId));
    }

    private void ConsumeCustomerWaitingTicket(SqliteConnection connection, SqliteTransaction transaction,
        string owner, string device, string profile, string tokenHash, string operationId)
    {
        var current = LatestCustomerWaitingEvent(connection, transaction, device);
        if (current is null) { return; }
        RequireWaitingActivation(current);
        if (current.State != "open") { throw WaitingError("waiting_event_unavailable"); }
        CustomerWaitingClaimBinding binding;
        using (var command = CustomerCommand(connection, transaction,
            "SELECT event_id,entry_id,claim_id FROM customer_waiting_ticket_bindings WHERE token_hash=$hash", ("$hash", tokenHash)))
        using (var row = command.ExecuteReader())
        {
            if (!row.Read()) { throw WaitingError("waiting_claim_required"); }
            binding = new(row.GetString(0), row.GetString(1), row.GetString(2));
        }
        if (binding.EventId != current.Id || binding.ClaimId != operationId)
        {
            throw WaitingError("waiting_claim_unavailable");
        }
        ConsumeCustomerWaitingClaim(connection, transaction, owner, device, profile, binding, operationId);
    }

    // This helper never infers terminality from time or queue state. It joins the
    // authoritative session ledger after its existing runtime retirement path.
    private static void SettleCustomerWaitingSessions(SqliteConnection connection, SqliteTransaction transaction,
        string device, string? sessionId = null)
        => CustomerExecute(connection, transaction, """
            UPDATE customer_waiting_events SET revision=revision+1 WHERE id IN(
                SELECT e.event_id FROM customer_waiting_entries e JOIN customer_waiting_events v ON v.id=e.event_id
                JOIN customer_sessions s ON s.owner_id=e.owner_id AND s.operation_id=e.operation_id AND s.device_id=v.device_id
                WHERE v.device_id=$device AND e.state='accepted' AND s.state IN ('completed','failed')
                    AND ($session IS NULL OR s.id=$session));
            UPDATE customer_waiting_entries SET state='completed',revision=revision+1,claim_id=NULL,claim_expires=NULL
                WHERE state='accepted' AND EXISTS(
                    SELECT 1 FROM customer_sessions s JOIN customer_waiting_events v ON v.id=customer_waiting_entries.event_id
                    WHERE s.owner_id=customer_waiting_entries.owner_id AND s.operation_id=customer_waiting_entries.operation_id
                        AND s.device_id=v.device_id AND v.device_id=$device AND s.state IN ('completed','failed')
                        AND ($session IS NULL OR s.id=$session));
            """, ("$device", device), ("$session", sessionId));

    private static void RevokeCustomerWaiting(SqliteConnection connection, SqliteTransaction transaction,
        string owner, string? profile)
    {
        if (profile is not null)
        {
            InvalidateRevokedCustomerWaiting(connection, transaction, owner);
            CustomerExecute(connection, transaction, """
                DELETE FROM customer_waiting_ticket_bindings WHERE token_hash IN(
                    SELECT token_hash FROM customer_start_tickets WHERE owner_id=$owner AND profile_id=$profile AND session_id IS NULL);
                """, ("$owner", owner), ("$profile", profile));
            return;
        }
        // The caller has already written the wildcard tombstone in this same
        // transaction. Removing accepted keys cannot resurrect this account.
        CustomerExecute(connection, transaction, """
            UPDATE customer_waiting_events SET revision=revision+1 WHERE id IN(
                SELECT event_id FROM customer_waiting_entries WHERE owner_id=$owner);
            DELETE FROM customer_waiting_ticket_bindings WHERE token_hash IN(
                SELECT token_hash FROM customer_start_tickets WHERE owner_id=$owner);
            DELETE FROM customer_waiting_entries WHERE owner_id=$owner;
            DELETE FROM customer_waiting_receipts WHERE actor_id='owner:'||$owner;
            """, ("$owner", owner));
    }

    private static void SuspendCustomerWaiting(SqliteConnection connection, SqliteTransaction transaction, string owner)
        => CustomerExecute(connection, transaction, """
            UPDATE customer_waiting_events SET revision=revision+1 WHERE id IN(
                SELECT event_id FROM customer_waiting_entries WHERE owner_id=$owner AND state IN ('waiting','claimed'));
            UPDATE customer_waiting_entries SET state='suspended',revision=revision+1,claim_id=NULL,claim_expires=NULL
                WHERE owner_id=$owner AND state IN ('waiting','claimed');
            DELETE FROM customer_waiting_ticket_bindings WHERE token_hash IN(
                SELECT token_hash FROM customer_start_tickets WHERE owner_id=$owner AND session_id IS NULL);
            """, ("$owner", owner));
}

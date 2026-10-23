using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class OperationJournal
{
    internal const int CustomerWaitingClaimSeconds = 90;
    private readonly CustomerWaitingLimits waitingLimits;
    internal CustomerWaitingLimits WaitingLimits => waitingLimits;

    private static void InitializeCustomerWaiting(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS customer_waiting_events(
                id TEXT PRIMARY KEY, device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN ('open','paused','closed')), revision INTEGER NOT NULL,
                next_sequence INTEGER NOT NULL DEFAULT 1,
                activation_revision INTEGER CHECK(activation_revision IS NULL OR (typeof(activation_revision)='integer' AND activation_revision>0)));
            CREATE UNIQUE INDEX IF NOT EXISTS customer_waiting_current
                ON customer_waiting_events(device_id) WHERE state!='closed';
            CREATE TABLE IF NOT EXISTS customer_waiting_entries(
                id TEXT PRIMARY KEY, event_id TEXT NOT NULL, owner_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                sequence INTEGER NOT NULL, revision INTEGER NOT NULL, state TEXT NOT NULL,
                claim_id TEXT, claim_expires INTEGER, operation_id TEXT,
                UNIQUE(event_id,sequence));
            CREATE UNIQUE INDEX IF NOT EXISTS customer_waiting_owner
                ON customer_waiting_entries(event_id,owner_id) WHERE state IN ('waiting','claimed','accepted');
            CREATE UNIQUE INDEX IF NOT EXISTS customer_waiting_claim
                ON customer_waiting_entries(claim_id) WHERE claim_id IS NOT NULL;
            CREATE TABLE IF NOT EXISTS customer_waiting_receipts(
                device_id TEXT NOT NULL, actor_id TEXT NOT NULL, request_id TEXT NOT NULL,
                event_id TEXT NOT NULL, fingerprint TEXT NOT NULL, result TEXT NOT NULL,
                PRIMARY KEY(device_id,actor_id,request_id));
            """;
        command.ExecuteNonQuery();
        command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('customer_waiting_events') WHERE name='activation_revision'";
        if ((long)command.ExecuteScalar()! == 0)
        {
            command.CommandText = """
                ALTER TABLE customer_waiting_events ADD COLUMN activation_revision INTEGER
                    CHECK(activation_revision IS NULL OR (typeof(activation_revision)='integer' AND activation_revision>0));
                """;
            command.ExecuteNonQuery();
        }
    }

    // The caller validates the evening under its memory lock before entering this journal.
    internal CustomerWaitingEvent OpenCustomerWaiting(string device, string eveningId, string requestId,
        string? previousEventId = null, long? expectedRevision = null, long? activationRevision = null, Action? beforeCommit = null)
    {
        WaitingGuid(eveningId); WaitingIdentity(device);
        if (activationRevision is null or <= 0) { throw WaitingError("waiting_activation_required"); }
        return WaitingCommand<CustomerWaitingEvent>(device, "operator", requestId,
            ["open", eveningId, previousEventId, expectedRevision, activationRevision], (connection, transaction) =>
        {
            var previous = WaitingEvent(connection, transaction, device, previousEventId);
            if (previousEventId is not null)
            {
                if (previous is null || previous.Id != previousEventId || previous.Revision != expectedRevision)
                {
                    throw WaitingError("waiting_revision_conflict");
                }
                var active = WaitingEvent(connection, transaction, device, null);
                if (active is not null && active.Id != previous.Id)
                {
                    throw WaitingError("waiting_event_exists");
                }
                CloseWaitingEvent(connection, transaction, previous.Id);
            }
            else if (previous is not null)
            {
                throw WaitingError("waiting_event_exists");
            }
            if (WaitingCount(connection, transaction, "SELECT COUNT(*) FROM customer_waiting_events WHERE device_id=$id", device)
                >= waitingLimits.EventsPerDevice)
            {
                throw WaitingError("waiting_event_capacity", 429);
            }
            var result = new CustomerWaitingEvent(Guid.NewGuid().ToString("D"), device, eveningId, "open", 1, activationRevision);
            CustomerExecute(connection, transaction, "INSERT INTO customer_waiting_events(id,device_id,evening_id,state,revision,activation_revision) VALUES($id,$device,$evening,'open',1,$activation)",
                ("$id", result.Id), ("$device", device), ("$evening", eveningId), ("$activation", activationRevision));
            return (result.Id, result);
        }, beforeCommit: beforeCommit);
    }

    internal CustomerWaitingEvent ChangeCustomerWaitingEvent(string device, string eventId, string requestId,
        long expectedRevision, string state, Action? beforeCommit = null)
    {
        if (state is not ("open" or "paused" or "closed"))
        {
            throw WaitingError("waiting_state_invalid", 400);
        }
        return WaitingCommand<CustomerWaitingEvent>(device, "operator", requestId,
            ["state", eventId, expectedRevision, state], (connection, transaction) =>
        {
            var current = RequireWaitingEvent(connection, transaction, device, eventId);
            if (current.Revision != expectedRevision)
            {
                throw WaitingError("waiting_revision_conflict");
            }
            if (current.State == "closed")
            {
                throw WaitingError("waiting_event_closed");
            }
            if (state == "closed")
            {
                CloseWaitingEvent(connection, transaction, eventId);
            }
            else
            {
                CustomerExecute(connection, transaction, "UPDATE customer_waiting_events SET state=$state,revision=revision+1 WHERE id=$id",
                    ("$state", state), ("$id", eventId));
            }
            return (eventId, RequireWaitingEvent(connection, transaction, device, eventId));
        }, beforeCommit: beforeCommit);
    }

    // HTTP adapters must supply proximity; null is reserved for trusted internal callers.
    internal CustomerWaitingView JoinCustomerWaiting(string owner, string device, CustomerWaitingJoin request,
        CustomerWaitingProximity? proximity = null, Action? beforeCommit = null)
    {
        WaitingIdentity(owner); WaitingIdentity(request.ProfileId);
        return WaitingCommand<CustomerWaitingView>(device, "owner:" + owner, request.RequestId,
            ["join", request.EventId, request.ProfileId], (connection, transaction) =>
        {
            var current = RequireWaitingEvent(connection, transaction, device, request.EventId);
            RequireWaitingActivation(current);
            if (current.State == "closed")
            {
                throw WaitingError("waiting_event_closed");
            }
            if (current.State != "open")
            {
                throw WaitingError("waiting_event_unavailable");
            }
            // A deleted profile must not retain the account's unique active-entry slot.
            InvalidateRevokedCustomerWaiting(connection, transaction, owner);
            var existing = WaitingOwnerEntry(connection, transaction, current.Id, owner);
            if (existing is null)
            {
                // Revocation hides the old profile from guest views, while its
                // accepted conversation still owns this account's active slot.
                using (var accepted = CustomerCommand(connection, transaction,
                    "SELECT 1 FROM customer_waiting_entries WHERE event_id=$event AND owner_id=$owner AND state='accepted'",
                    ("$event", current.Id), ("$owner", owner)))
                {
                    if (accepted.ExecuteScalar() is not null) { throw WaitingError("waiting_entry_unavailable"); }
                }
                if (WaitingCount(connection, transaction, "SELECT COUNT(*) FROM customer_waiting_entries WHERE event_id=$id", current.Id)
                    >= waitingLimits.EntriesPerEvent)
                {
                    throw WaitingError("waiting_entry_capacity", 429);
                }
                using var sequence = CustomerCommand(connection, transaction, "SELECT next_sequence FROM customer_waiting_events WHERE id=$id", ("$id", current.Id));
                var next = (long)sequence.ExecuteScalar()!;
                CustomerExecute(connection, transaction, """
                    INSERT INTO customer_waiting_entries(id,event_id,owner_id,profile_id,sequence,revision,state)
                        VALUES($id,$event,$owner,$profile,$sequence,1,'waiting');
                    UPDATE customer_waiting_events SET next_sequence=next_sequence+1,revision=revision+1 WHERE id=$event;
                    """, ("$id", Guid.NewGuid().ToString("D")), ("$event", current.Id), ("$owner", owner),
                    ("$profile", request.ProfileId), ("$sequence", next));
            }
            return (current.Id, WaitingView(connection, transaction, owner, device, current.Id));
        }, owner, request.ProfileId, proximity, beforeCommit);
    }

    internal CustomerWaitingView LeaveCustomerWaiting(string owner, string device, CustomerWaitingEntryCommand request, Action? beforeCommit = null)
        => MutateWaitingEntry(owner, device, request, false, beforeCommit);

    internal CustomerWaitingView CancelCustomerWaiting(string device, CustomerWaitingEntryCommand request, Action? beforeCommit = null)
        => MutateWaitingEntry(null, device, request, true, beforeCommit);

    private CustomerWaitingView MutateWaitingEntry(string? owner, string device, CustomerWaitingEntryCommand request, bool skipped, Action? beforeCommit)
        => WaitingCommand<CustomerWaitingView>(device, owner is null ? "operator" : "owner:" + owner,
            request.RequestId, [skipped ? "skip" : "leave", request.EventId, request.EntryId, request.ExpectedRevision], (connection, transaction) =>
        {
            RequireWaitingEvent(connection, transaction, device, request.EventId);
            var entry = RequireWaitingEntry(connection, transaction, request, owner);
            if (entry.State is not ("waiting" or "claimed"))
            {
                throw WaitingError("waiting_entry_unavailable");
            }
            CustomerExecute(connection, transaction, """
                UPDATE customer_waiting_entries SET state=$state,revision=revision+1,claim_id=NULL,claim_expires=NULL WHERE id=$id;
                UPDATE customer_waiting_events SET revision=revision+1 WHERE id=$event;
                """, ("$state", skipped ? "skipped" : "left"), ("$id", entry.Id), ("$event", request.EventId));
            // Operator responses do not expose another account's profile or entry.
            return (request.EventId, WaitingView(connection, transaction, owner ?? "", device, request.EventId));
        }, owner, beforeCommit: beforeCommit);

    internal CustomerWaitingView ClaimCustomerWaiting(string owner, string device, CustomerWaitingEntryCommand request,
        CustomerWaitingProximity? proximity = null, Action? beforeCommit = null)
        => WaitingCommand<CustomerWaitingView>(device, "owner:" + owner, request.RequestId,
            ["claim", request.EventId, request.EntryId, request.ExpectedRevision], (connection, transaction) =>
        {
            var current = RequireWaitingEvent(connection, transaction, device, request.EventId);
            RequireWaitingActivation(current);
            if (current.State != "open")
            {
                throw WaitingError("waiting_event_unavailable");
            }
            var entry = RequireWaitingEntry(connection, transaction, request, owner);
            RequireCustomerProfileNotRevoked(connection, transaction, owner, entry.ProfileId);
            if (entry.State is not ("waiting" or "claimed"))
            {
                throw WaitingError("waiting_entry_unavailable");
            }
            using (var head = CustomerCommand(connection, transaction, """
                SELECT e.id FROM customer_waiting_entries e WHERE e.event_id=$event AND e.state IN ('waiting','claimed')
                    AND NOT EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=e.owner_id AND r.profile_id IN (e.profile_id,'*'))
                    ORDER BY e.sequence LIMIT 1
                """, ("$event", current.Id)))
            {
                if (head.ExecuteScalar() as string != entry.Id)
                {
                    throw WaitingError("waiting_not_head");
                }
            }
            using (var lease = CustomerCommand(connection, transaction, "SELECT 1 FROM device_conversation_leases WHERE device_id=$device", ("$device", device)))
            {
                if (lease.ExecuteScalar() is not null)
                {
                    throw WaitingError("device_busy");
                }
            }
            if (entry.Claim is not null)
            {
                throw WaitingError("waiting_claim_exists");
            }
            CustomerExecute(connection, transaction, """
                UPDATE customer_waiting_entries SET state='claimed',revision=revision+1,claim_id=$claim,claim_expires=$expires WHERE id=$id;
                UPDATE customer_waiting_events SET revision=revision+1 WHERE id=$event;
                """, ("$claim", Guid.NewGuid().ToString("D")), ("$expires", CustomerNow() + CustomerWaitingClaimSeconds),
                ("$id", entry.Id), ("$event", current.Id));
            return (current.Id, WaitingView(connection, transaction, owner, device, current.Id));
        }, owner, proximity: proximity, beforeCommit: beforeCommit);

    internal CustomerWaitingView ReadCustomerWaiting(string owner, string device, string? eventId = null)
    {
        WaitingIdentity(owner); WaitingIdentity(device);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireCustomerProfileNotRevoked(connection, transaction, owner, "*");
        return WaitingView(connection, transaction, owner, device, eventId);
    }

    internal CustomerWaitingEvent? ReadCustomerWaitingEvent(string device)
    {
        using var connection = Open();
        return WaitingEvent(connection, null, device, null);
    }

    internal CustomerWaitingOperatorView ReadCustomerWaitingOperator(string device)
    {
        WaitingIdentity(device);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = LatestCustomerWaitingEvent(connection, transaction, device);
        if (current is null) { return new(null, null, 0); }
        const string eligible = """
            FROM customer_waiting_entries e WHERE e.event_id=$event AND e.state IN ('waiting','claimed')
                AND NOT EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=e.owner_id AND r.profile_id IN (e.profile_id,'*'))
            """;
        using var count = CustomerCommand(connection, transaction, "SELECT COUNT(*) " + eligible, ("$event", current.Id));
        var waitingCount = (int)(long)count.ExecuteScalar()!;
        using var head = CustomerCommand(connection, transaction,
            "SELECT e.id,e.revision,e.state,e.claim_expires,e.claim_id " + eligible + " ORDER BY e.sequence LIMIT 1", ("$event", current.Id));
        using var row = head.ExecuteReader();
        CustomerWaitingHead? visible = null;
        if (row.Read())
        {
            var state = row.GetString(2);
            if (state == "claimed" && (row.IsDBNull(3) || row.IsDBNull(4) || row.GetInt64(3) <= CustomerNow())) { state = "waiting"; }
            visible = new(row.GetString(0), row.GetInt64(1), state);
        }
        return new(current, visible, waitingCount);
    }

    // Admission must run these in the SAME transaction as ticket binding / lease insertion.
    // A claim has no authority outside its exact owner/profile/event/device binding.
    private void RequireCustomerWaitingClaim(SqliteConnection connection, SqliteTransaction transaction,
        string owner, string device, string profile, CustomerWaitingClaimBinding binding)
    {
        RequireCustomerProfileNotRevoked(connection, transaction, owner, profile);
        var current = RequireWaitingEvent(connection, transaction, device, binding.EventId);
        RequireWaitingActivation(current);
        if (current.State != "open")
        {
            throw WaitingError("waiting_event_unavailable");
        }
        using var command = CustomerCommand(connection, transaction, """
            SELECT 1 FROM customer_waiting_entries WHERE id=$id AND event_id=$event AND owner_id=$owner
                AND profile_id=$profile AND state='claimed' AND claim_id=$claim AND claim_expires>$now
            """, ("$id", binding.EntryId), ("$event", binding.EventId), ("$owner", owner),
            ("$profile", profile), ("$claim", binding.ClaimId), ("$now", CustomerNow()));
        if (command.ExecuteScalar() is null)
        {
            throw WaitingError("waiting_claim_unavailable");
        }
    }

    private void ConsumeCustomerWaitingClaim(SqliteConnection connection, SqliteTransaction transaction,
        string owner, string device, string profile, CustomerWaitingClaimBinding binding, string operationId)
    {
        RequireCustomerWaitingClaim(connection, transaction, owner, device, profile, binding);
        CustomerExecute(connection, transaction, """
            UPDATE customer_waiting_entries SET state='accepted',revision=revision+1,operation_id=$operation WHERE id=$id;
            UPDATE customer_waiting_events SET revision=revision+1 WHERE id=$event;
            """, ("$operation", operationId), ("$id", binding.EntryId), ("$event", binding.EventId));
    }

    // Direct Create admission may not race a live reservation of this device.
    private void RequireNoCustomerWaitingClaim(SqliteConnection connection, SqliteTransaction transaction, string device)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT 1 FROM customer_waiting_entries e JOIN customer_waiting_events v ON v.id=e.event_id
                WHERE v.device_id=$device AND v.state!='closed' AND e.state='claimed' AND e.claim_expires>$now
                AND NOT EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=e.owner_id AND r.profile_id IN (e.profile_id,'*')) LIMIT 1
            """, ("$device", device), ("$now", CustomerNow()));
        if (command.ExecuteScalar() is not null)
        {
            throw WaitingError("waiting_claim_reserved");
        }
    }

    private T WaitingCommand<T>(string device, string actor, string requestId, object?[] body,
        Func<SqliteConnection, SqliteTransaction, (string EventId, T Result)> apply, string? owner = null, string? profile = null,
        CustomerWaitingProximity? proximity = null, Action? beforeCommit = null)
    {
        WaitingIdentity(device); WaitingGuid(requestId);
        // JSON array encoding is unambiguous and culture independent; version and route are part of the hash.
        var fingerprint = CustomerHash(JsonSerializer.Serialize(new object?[] { "customer-waiting-v1", body }));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (owner is not null)
        {
            RequireCustomerProfileNotRevoked(connection, transaction, owner, profile ?? "*");
        }
        if (proximity is not null)
        {
            // Recheck the current device challenge in this same writer transaction,
            // even on accepted-key replay. Fresh BLE proof is not command identity.
            RequireCustomerChallenge(connection, transaction, device, proximity.BootId, proximity.Challenge);
        }
        var existing = false;
        T replay = default!;
        using (var receipt = CustomerCommand(connection, transaction,
            "SELECT fingerprint,result FROM customer_waiting_receipts WHERE device_id=$device AND actor_id=$actor AND request_id=$request",
            ("$device", device), ("$actor", actor), ("$request", requestId)))
        using (var row = receipt.ExecuteReader())
        {
            if (row.Read())
            {
                if (row.GetString(0) != fingerprint)
                {
                    throw WaitingError("waiting_request_conflict");
                }
                replay = JsonSerializer.Deserialize<T>(row.GetString(1))!;
                if (owner is not null && replay is CustomerWaitingView { Entry: { } entry })
                {
                    RequireCustomerProfileNotRevoked(connection, transaction, owner, entry.ProfileId);
                }
                existing = true;
            }
        }
        if (existing)
        {
            beforeCommit?.Invoke();
            transaction.Commit();
            return replay;
        }
        if (WaitingCount(connection, transaction, "SELECT COUNT(*) FROM customer_waiting_receipts WHERE device_id=$id", device)
            >= waitingLimits.ReceiptsPerDevice)
        {
            throw WaitingError("waiting_receipt_capacity", 429);
        }
        if (owner is not null)
        {
            using var ownerReceipts = CustomerCommand(connection, transaction,
                "SELECT COUNT(*) FROM customer_waiting_receipts WHERE device_id=$device AND actor_id=$actor",
                ("$device", device), ("$actor", actor));
            if ((long)ownerReceipts.ExecuteScalar()! >= waitingLimits.ReceiptsPerAccountDevice)
            {
                throw WaitingError("waiting_owner_receipt_capacity", 429);
            }
        }
        var (eventId, result) = apply(connection, transaction);
        CustomerExecute(connection, transaction, "INSERT INTO customer_waiting_receipts VALUES($device,$actor,$request,$event,$fingerprint,$result)",
            ("$device", device), ("$actor", actor), ("$request", requestId), ("$event", eventId),
            ("$fingerprint", fingerprint), ("$result", JsonSerializer.Serialize(result)));
        // Pure validation of guards already held by the caller only. Never
        // acquire locks, call another database, or run asynchronous work here.
        beforeCommit?.Invoke();
        transaction.Commit();
        return result;
    }

    private CustomerWaitingView WaitingView(SqliteConnection connection, SqliteTransaction transaction, string owner, string device, string? eventId)
    {
        var current = WaitingEvent(connection, transaction, device, eventId);
        if (current is null)
        {
            return new(null, null, null, 0);
        }
        var entry = WaitingOwnerEntry(connection, transaction, current.Id, owner);
        using var count = CustomerCommand(connection, transaction, """
            SELECT COUNT(*),SUM(CASE WHEN e.sequence<=$sequence THEN 1 ELSE 0 END) FROM customer_waiting_entries e
                WHERE e.event_id=$event AND e.state IN ('waiting','claimed')
                AND NOT EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=e.owner_id AND r.profile_id IN (e.profile_id,'*'))
            """, ("$sequence", entry?.Sequence ?? 0), ("$event", current.Id));
        using var row = count.ExecuteReader(); row.Read();
        return new(current, entry, entry is { State: "waiting" or "claimed" } ? (int)row.GetInt64(1) : null, (int)row.GetInt64(0));
    }

    private static CustomerWaitingEvent? WaitingEvent(SqliteConnection connection, SqliteTransaction? transaction, string device, string? eventId)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT id,device_id,evening_id,state,revision,activation_revision,typeof(activation_revision) FROM customer_waiting_events
                WHERE device_id=$device AND (($id IS NULL AND state!='closed') OR id=$id)
            """, ("$device", device), ("$id", eventId));
        using var row = command.ExecuteReader();
        return row.Read() ? ReadWaitingEvent(row) : null;
    }

    private static CustomerWaitingEvent ReadWaitingEvent(SqliteDataReader row)
        => new(row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(3), row.GetInt64(4),
            row.IsDBNull(5) || row.GetString(6) != "integer" ? null : row.GetInt64(5));

    private static void RequireWaitingActivation(CustomerWaitingEvent current)
    {
        if (current.ActivationRevision is null or <= 0) { throw WaitingError("waiting_activation_required"); }
    }

    private static CustomerWaitingEvent RequireWaitingEvent(SqliteConnection connection, SqliteTransaction transaction, string device, string eventId)
        => WaitingEvent(connection, transaction, device, eventId) ?? throw WaitingError("waiting_event_unavailable", 404);

    private CustomerWaitingEntry? WaitingOwnerEntry(SqliteConnection connection, SqliteTransaction transaction, string eventId, string owner)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT id,profile_id,sequence,revision,state,claim_id,claim_expires FROM customer_waiting_entries e
                WHERE event_id=$event AND owner_id=$owner AND state IN ('waiting','claimed','accepted')
                AND NOT EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=e.owner_id AND r.profile_id IN (e.profile_id,'*'))
            """, ("$event", eventId), ("$owner", owner));
        using var row = command.ExecuteReader();
        return row.Read() ? ReadWaitingEntry(row) : null;
    }

    private CustomerWaitingEntry RequireWaitingEntry(SqliteConnection connection, SqliteTransaction transaction, CustomerWaitingEntryCommand request, string? owner)
    {
        using var command = CustomerCommand(connection, transaction, """
            SELECT id,profile_id,sequence,revision,state,claim_id,claim_expires FROM customer_waiting_entries
                WHERE id=$id AND event_id=$event AND ($owner IS NULL OR owner_id=$owner)
            """, ("$id", request.EntryId), ("$event", request.EventId), ("$owner", owner));
        using var row = command.ExecuteReader();
        if (!row.Read())
        {
            throw WaitingError("waiting_entry_unavailable", 404);
        }
        var result = ReadWaitingEntry(row);
        if (result.Revision != request.ExpectedRevision)
        {
            throw WaitingError("waiting_revision_conflict");
        }
        return result;
    }

    private CustomerWaitingEntry ReadWaitingEntry(SqliteDataReader row)
    {
        var claim = !row.IsDBNull(5) && row.GetInt64(6) > CustomerNow() ? new CustomerWaitingClaim(row.GetString(5), row.GetInt64(6)) : null;
        var state = row.GetString(4);
        if (state == "claimed" && claim is null)
        {
            state = "waiting";
        }
        return new(row.GetString(0), row.GetString(1), row.GetInt64(2), row.GetInt64(3), state, claim);
    }

    private static void CloseWaitingEvent(SqliteConnection connection, SqliteTransaction transaction, string eventId)
        => CustomerExecute(connection, transaction, """
            UPDATE customer_waiting_entries SET state='closed',revision=revision+1,claim_id=NULL,claim_expires=NULL
                WHERE event_id=$event AND state IN ('waiting','claimed');
            UPDATE customer_waiting_events SET state='closed',revision=revision+1 WHERE id=$event AND state!='closed';
            """, ("$event", eventId));

    private static void InvalidateRevokedCustomerWaiting(SqliteConnection connection, SqliteTransaction transaction, string owner)
        => CustomerExecute(connection, transaction, """
            UPDATE customer_waiting_events SET revision=revision+1 WHERE id IN(
                SELECT e.event_id FROM customer_waiting_entries e WHERE e.owner_id=$owner AND e.state IN ('waiting','claimed')
                    AND EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=e.owner_id AND r.profile_id IN (e.profile_id,'*')));
            UPDATE customer_waiting_entries SET state='revoked',revision=revision+1,claim_id=NULL,claim_expires=NULL
                WHERE owner_id=$owner AND state IN ('waiting','claimed')
                    AND EXISTS(SELECT 1 FROM customer_profile_revocations r WHERE r.owner_id=customer_waiting_entries.owner_id
                        AND r.profile_id IN (customer_waiting_entries.profile_id,'*'));
            """, ("$owner", owner));

    private static long WaitingCount(SqliteConnection connection, SqliteTransaction transaction, string sql, string id)
    {
        using var command = CustomerCommand(connection, transaction, sql, ("$id", id));
        return (long)command.ExecuteScalar()!;
    }

    private static void WaitingGuid(string value)
    {
        if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty || value != id.ToString("D"))
        {
            throw WaitingError("waiting_id_invalid", 400);
        }
    }

    private static void WaitingIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl))
        {
            throw WaitingError("waiting_identity_invalid", 400);
        }
    }

    private static CustomerSessionException WaitingError(string code, int status = 409) => new(status, code);
}

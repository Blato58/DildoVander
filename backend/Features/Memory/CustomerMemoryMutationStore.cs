using System.Text;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Memory;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class EveningMemoryStore
{
    // Caller holds live ownership guards, real local-write admission, the actual
    // memory gate and this transaction. These methods never open another store.
    internal CustomerMemoryCorrectionResult CorrectCustomerMemory(SqliteConnection connection,
        SqliteTransaction transaction, CustomerOwnershipInventory inventory,
        string id, CustomerMemoryCorrectionRequest request)
    {
        if (!Guid.TryParseExact(id, "D", out _) || request.ExpectedRevision <= 0
            || string.IsNullOrWhiteSpace(request.Text)
            || Encoding.UTF8.GetByteCount(request.Text) > memoryOptions.MaxRecordBytes)
        {
            throw new CustomerDataAccessException(400, "invalid_memory_correction");
        }

        var record = RequireCustomerMemoryOrigin(connection, transaction, inventory, id);
        if (record.Revision != request.ExpectedRevision || record.Revision == long.MaxValue)
        {
            throw new CustomerDataAccessException(409, "memory_revision_mismatch");
        }
        var owned = FindCustomerMemorySession(inventory, record)!;
        var binding = ReadGuest(connection, transaction, record.DeviceId, record.ConversationId);
        if (!memoryOptions.Enabled || !memoryOptions.LongTermPersonalEnabled
            || record.Kind != MemoryKind.UserReportedFact
            || CustomerMemoryReadPolicy.TryProve(inventory.Access.AccountId, inventory.Access,
                owned, record, binding, memoryOptions.Namespace) is null)
        {
            throw new CustomerDataAccessException(404, "memory_unavailable");
        }

        RequireBoundedCustomerMemoryMutation(connection, transaction, record, addsRevision: true);
        var bytes = Encoding.UTF8.GetByteCount(request.Text);
        using (var capacity = Command(connection, transaction, """
            SELECT (SELECT COALESCE(SUM(content_bytes),0) FROM memory_records)
                +(SELECT COALESCE(SUM(length(CAST(text AS BLOB))),0) FROM live_transcripts);
            """))
        {
            if ((long)capacity.ExecuteScalar()! + bytes > memoryOptions.MaxStoredBytes)
            {
                RecordCapacityFailure(connection, transaction);
                return new(null, CapacityRejected: true);
            }
        }
        if (!Spend(connection, transaction, "write", 1, memoryOptions.MaxWritesPerDay))
        {
            throw new CustomerDataAccessException(429, "memory_write_limit");
        }

        var revision = checked(record.Revision + 1);
        // Save provenance/consent before Invalidate clears prior-revision flags.
        var exportAllowed = record.ExportAllowed;
        Invalidate(connection, transaction, record.MemoryId, eraseContent: false);
        using var insert = Command(connection, transaction, """
            INSERT INTO memory_records VALUES(
                $id,$rev,$scope,$device,$evening,$conversation,$guest,$personality,
                $kind,$text,$source,$created,$expires,1,$supersedes,1,$export,
                $permission,$bytes,$namespace);
            UPDATE memory_meta SET epoch=epoch+1 WHERE id=1;
            """, ("$id", record.MemoryId), ("$rev", revision), ("$scope", record.Scope.ToString()),
            ("$device", record.DeviceId), ("$evening", record.EveningId),
            ("$conversation", record.ConversationId), ("$guest", record.GuestId),
            ("$personality", record.PersonalityId), ("$kind", record.Kind.ToString()),
            ("$text", request.Text), ("$source", record.SourceReference),
            ("$created", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$expires", record.ExpiresAt.ToUnixTimeMilliseconds()),
            ("$supersedes", record.MemoryId + ":" + record.Revision),
            ("$export", exportAllowed), ("$permission", record.PermissionVersion),
            ("$bytes", bytes), ("$namespace", record.AppNamespace));
        insert.ExecuteNonQuery();
        if (exportAllowed && binding!.CloudExportAllowed && memoryOptions.CloudIndexingEnabled)
        {
            Queue(connection, transaction, record.MemoryId, revision,
                memoryOptions.StoreFor(MemoryScope.Personal), "index");
        }
        return new(new(record.MemoryId, revision, "corrected"), CapacityRejected: false);
    }

    internal CustomerMemoryMutationResult ForgetCustomerMemory(SqliteConnection connection,
        SqliteTransaction transaction, CustomerOwnershipInventory inventory,
        string id, CustomerMemoryForgetRequest request)
    {
        if (!request.Confirmed || !Guid.TryParseExact(id, "D", out _) || request.ExpectedRevision <= 0)
        {
            throw new CustomerDataAccessException(400, "memory_confirmation_required");
        }
        var record = RequireCustomerMemoryOrigin(connection, transaction, inventory, id);
        if (record.Revision != request.ExpectedRevision)
        {
            throw new CustomerDataAccessException(409, "memory_revision_mismatch");
        }

        RequireBoundedCustomerMemoryMutation(connection, transaction, record);
        // Origin, not recall consent, authorizes removal. Deleted profiles and
        // unavailable snapshots retain this blind, explicitly confirmed path.
        Invalidate(connection, transaction, record.MemoryId, eraseContent: true);
        return new(record.MemoryId, record.Revision, "forgotten");
    }

    private MemoryRecord RequireCustomerMemoryOrigin(SqliteConnection connection,
        SqliteTransaction transaction, CustomerOwnershipInventory inventory, string id)
    {
        var record = Latest(connection, transaction, id);
        if (!inventory.Complete || string.IsNullOrWhiteSpace(inventory.Access.AccountId)
            || record is null || record.Scope != MemoryScope.Personal
            || record.AppNamespace != memoryOptions.Namespace
            || record.Kind is not (MemoryKind.UserReportedFact or MemoryKind.ConfirmedSelection)
            || FindCustomerMemorySession(inventory, record) is null)
        {
            throw new CustomerDataAccessException(404, "memory_unavailable");
        }
        var binding = ReadGuest(connection, transaction, record.DeviceId, record.ConversationId);
        if (binding is not { IdentityVerified: true }
            || binding.GuestId != record.GuestId)
        {
            throw new CustomerDataAccessException(404, "memory_unavailable");
        }
        return record;
    }

    private static CustomerOwnedSession? FindCustomerMemorySession(
        CustomerOwnershipInventory inventory, MemoryRecord record) =>
        inventory.Sessions.FirstOrDefault(owned =>
            !string.IsNullOrWhiteSpace(owned.ProfileId)
            && !string.IsNullOrWhiteSpace(owned.ConversationId)
            && owned.DeviceId == record.DeviceId && owned.ConversationId == record.ConversationId
            && owned.ExpectedGuestId == record.GuestId
            && owned.ExpectedGuestId == CustomerMemoryOriginPolicy.ExpectedGuestId(
                inventory.Access.AccountId, owned.ProfileId));

    private void RequireBoundedCustomerMemoryMutation(SqliteConnection connection,
        SqliteTransaction transaction, MemoryRecord record, bool addsRevision = false)
    {
        var bound = CustomerDataLimits.MaxMemoryItems;
        using (var history = Command(connection, transaction, """
            SELECT device_id,conversation_id,guest_id,app_namespace,scope
            FROM memory_records WHERE memory_id=$id LIMIT $limit;
            """, ("$id", record.MemoryId), ("$limit", bound + 1)))
        using (var reader = history.ExecuteReader())
        {
            var count = 0;
            while (reader.Read())
            {
                if (++count > bound || addsRevision && count == bound)
                {
                    throw new CustomerDataAccessException(413, "memory_mutation_limit");
                }
                // The helper erases every revision of one ID. Prove that every
                // affected revision has this same original account-owned tuple.
                if (reader.GetString(0) != record.DeviceId || reader.GetString(1) != record.ConversationId
                    || reader.IsDBNull(2) || reader.GetString(2) != record.GuestId
                    || reader.GetString(3) != record.AppNamespace || reader.GetString(4) != "Personal")
                {
                    throw new CustomerDataAccessException(404, "memory_unavailable");
                }
            }
        }
        using var affected = Command(connection, transaction, """
            SELECT
                (SELECT COUNT(*) FROM (
                    SELECT 1 FROM memory_indexes
                    WHERE memory_id=$id AND state!='deleted' LIMIT $limit)),
                (SELECT COUNT(*) FROM (
                    SELECT 1 FROM memory_guests
                    WHERE app_namespace=$namespace AND device_id=$device AND guest_id=$guest LIMIT $limit));
            """, ("$id", record.MemoryId), ("$namespace", record.AppNamespace),
            ("$device", record.DeviceId), ("$guest", record.GuestId), ("$limit", bound + 1));
        using var affectedReader = affected.ExecuteReader();
        if (!affectedReader.Read() || affectedReader.GetInt64(0) > bound - (addsRevision ? 1 : 0)
            || affectedReader.GetInt64(1) > bound)
        {
            throw new CustomerDataAccessException(413, "memory_mutation_limit");
        }
    }
}

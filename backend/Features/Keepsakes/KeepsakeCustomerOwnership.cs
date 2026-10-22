using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeCustomerOwner(string AccountId, string SessionId);
internal sealed record KeepsakeAutomaticAccountLink(string Locator, int Epoch, long LinkedAt);

internal sealed partial class KeepsakeStore
{
    // Captured from the original conversation before generation. This private
    // association never enters story prompts, snapshots, or public exports.
    private void BindCustomerOwner(SqliteConnection connection, SqliteTransaction transaction,
        string id, KeepsakeCustomerOwner? owner)
    {
        if (owner is null) { return; }
        using var fence = Command(connection, transaction,
            "SELECT COUNT(*) FROM dv10_account_fences WHERE owner_id=$owner", ("$owner", owner.AccountId));
        if ((long)fence.ExecuteScalar()! != 0) { throw new KeepsakeException("account_deleted", 409); }
        using var existing = Command(connection, transaction,
            "SELECT owner_id,session_id FROM dv10_customer_owners WHERE keepsake_id=$id", ("$id", id));
        using (var reader = existing.ExecuteReader())
        {
            if (reader.Read())
            {
                if (reader.GetString(0) != owner.AccountId || reader.GetString(1) != owner.SessionId)
                { throw new KeepsakeException("outcome_owner_conflict", 409); }
                return;
            }
        }
        Execute(connection, transaction, """
            INSERT INTO dv10_customer_owners(keepsake_id,owner_id,session_id,epoch,linked_at)
            SELECT id,$owner,$session,account_access_epoch,$now FROM dv10_keepsakes WHERE id=$id AND deleted=0;
            """, ("$id", id), ("$owner", owner.AccountId), ("$session", owner.SessionId),
            ("$now", clock.GetUtcNow().ToUnixTimeSeconds()));
    }

    internal KeepsakeAutomaticAccountLink[] CustomerAccountLinks(string accountId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT o.keepsake_id,o.epoch,o.linked_at FROM dv10_customer_owners o
                JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND k.deleted=0 AND k.account_access_epoch=o.epoch
                ORDER BY o.linked_at DESC,o.keepsake_id LIMIT 100;
                """, ("$owner", accountId));
            using var reader = command.ExecuteReader();
            var links = new List<KeepsakeAutomaticAccountLink>();
            while (reader.Read()) { links.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2))); }
            return links.ToArray();
        }
    }

    internal KeepsakeAutomaticAccountLink? CustomerAccountLink(string accountId, string locator)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT o.epoch,o.linked_at FROM dv10_customer_owners o JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND o.keepsake_id=$id AND k.deleted=0 AND k.account_access_epoch=o.epoch
                """, ("$owner", accountId), ("$id", locator));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(locator, reader.GetInt32(0), reader.GetInt64(1)) : null;
        }
    }

    internal void RemoveCustomerAccountLink(string accountId, string locator)
    {
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, "UPDATE dv10_customer_owners SET epoch=-1 WHERE owner_id=$owner AND keepsake_id=$id",
                ("$owner", accountId), ("$id", locator));
        }
    }
}

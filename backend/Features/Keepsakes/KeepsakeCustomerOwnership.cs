using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeCustomerOwner(string AccountId, string SessionId);
internal sealed record KeepsakeAutomaticAccountLink(string Locator, int Epoch, long LinkedAt, long CollectionRevision = 0);
internal sealed record KeepsakeOwnedCollectionCandidate(string Locator, string SessionId, int Epoch,
    long LinkedAt, long CollectionRevision);

internal sealed partial class KeepsakeStore
{
    private static void InitializeCustomerCollectionRevisions(SqliteConnection connection)
    {
        EnsureColumn(connection, "dv10_customer_owners", "collection_revision", "INTEGER NOT NULL DEFAULT 0");
        Execute(connection, null, "CREATE TABLE IF NOT EXISTS dv10_customer_collection_state (id INTEGER PRIMARY KEY CHECK(id=1), revision INTEGER NOT NULL)");
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS ix_dv10_customer_owners_collection_page ON dv10_customer_owners(owner_id,linked_at DESC,keepsake_id ASC)");
        // Existing databases predate the collection watermark. Assign deterministic
        // positive revisions once, preserving all immutable owner rows.
        Execute(connection, null, """
            WITH ranked AS (
                SELECT keepsake_id, ROW_NUMBER() OVER(ORDER BY linked_at,owner_id,keepsake_id) AS revision
                FROM dv10_customer_owners WHERE collection_revision=0
            )
            UPDATE dv10_customer_owners
            SET collection_revision=(SELECT revision FROM ranked WHERE ranked.keepsake_id=dv10_customer_owners.keepsake_id)
            WHERE collection_revision=0;
            """);
        Execute(connection, null, """
            INSERT INTO dv10_customer_collection_state(id,revision)
            SELECT 1,COALESCE(MAX(collection_revision),0) FROM dv10_customer_owners WHERE 1=1
            ON CONFLICT(id) DO UPDATE SET revision=MAX(revision,excluded.revision);
            """);
    }

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
        using (var current = Command(connection, transaction,
            "SELECT 1 FROM dv10_keepsakes WHERE id=$id AND deleted=0", ("$id", id)))
        {
            if (current.ExecuteScalar() is null) { throw new KeepsakeException("access_unavailable", 404); }
        }
        long revision;
        using (var next = Command(connection, transaction, """
            INSERT INTO dv10_customer_collection_state(id,revision) VALUES(1,1)
            ON CONFLICT(id) DO UPDATE SET revision=revision+1
            RETURNING revision;
            """))
        {
            revision = (long)next.ExecuteScalar()!;
        }
        Execute(connection, transaction, """
            INSERT INTO dv10_customer_owners(keepsake_id,owner_id,session_id,epoch,linked_at)
            SELECT id,$owner,$session,account_access_epoch,$now FROM dv10_keepsakes WHERE id=$id AND deleted=0;
            """, ("$id", id), ("$owner", owner.AccountId), ("$session", owner.SessionId),
            ("$now", clock.GetUtcNow().ToUnixTimeSeconds()));
        Execute(connection, transaction, "UPDATE dv10_customer_owners SET collection_revision=$revision WHERE keepsake_id=$id",
            ("$revision", revision), ("$id", id));
    }

    internal long CustomerOwnedCollectionRevision(string accountId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT revision FROM dv10_customer_collection_state WHERE id=1");
            return command.ExecuteScalar() is long revision ? revision : 0;
        }
    }

    internal KeepsakeOwnedCollectionCandidate[] CustomerOwnedCollectionCandidates(string accountId,
        long throughRevision, long? beforeLinkedAt, string? beforeLocator, int limit)
    {
        if (limit is < 1 or > 201) { throw new ArgumentOutOfRangeException(nameof(limit)); }
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT o.keepsake_id,o.session_id,o.epoch,o.linked_at,o.collection_revision
                FROM dv10_customer_owners o
                JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND o.collection_revision<=$revision
                    AND k.deleted=0 AND k.account_access_epoch=o.epoch
                    AND ($before IS NULL OR o.linked_at<$before OR (o.linked_at=$before AND o.keepsake_id>$locator))
                ORDER BY o.linked_at DESC,o.keepsake_id COLLATE BINARY ASC
                LIMIT $limit;
                """, ("$owner", accountId), ("$revision", throughRevision),
                ("$before", beforeLinkedAt), ("$locator", beforeLocator ?? ""), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var rows = new List<KeepsakeOwnedCollectionCandidate>(limit);
            while (reader.Read())
            {
                rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt64(3), reader.GetInt64(4)));
            }
            return rows.ToArray();
        }
    }

    internal HashSet<string> CurrentCustomerOwnerLocators(string accountId, IReadOnlyCollection<string> locators)
    {
        if (locators.Count == 0) { return []; }
        if (locators.Count > 201) { throw new ArgumentOutOfRangeException(nameof(locators)); }
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            var parameters = new List<string>(locators.Count);
            var index = 0;
            foreach (var locator in locators)
            {
                var name = "$id" + index++;
                parameters.Add(name);
                command.Parameters.AddWithValue(name, locator);
            }
            command.Parameters.AddWithValue("$owner", accountId);
            command.CommandText = $"""
                SELECT o.keepsake_id FROM dv10_customer_owners o
                JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND k.deleted=0 AND k.account_access_epoch=o.epoch
                    AND o.keepsake_id IN ({string.Join(',', parameters)});
                """;
            using var reader = command.ExecuteReader();
            var result = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read()) { result.Add(reader.GetString(0)); }
            return result;
        }
    }

    internal KeepsakeAutomaticAccountLink[] CustomerAccountLinks(string accountId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT o.keepsake_id,o.epoch,o.linked_at,o.collection_revision FROM dv10_customer_owners o
                JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND k.deleted=0 AND k.account_access_epoch=o.epoch
                ORDER BY o.linked_at DESC,o.keepsake_id LIMIT 100;
                """, ("$owner", accountId));
            using var reader = command.ExecuteReader();
            var links = new List<KeepsakeAutomaticAccountLink>();
            while (reader.Read()) { links.Add(new(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3))); }
            return links.ToArray();
        }
    }

    internal KeepsakeAutomaticAccountLink? CustomerAccountLink(string accountId, string locator)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT o.epoch,o.linked_at,o.collection_revision FROM dv10_customer_owners o JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND o.keepsake_id=$id AND k.deleted=0 AND k.account_access_epoch=o.epoch
                """, ("$owner", accountId), ("$id", locator));
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(locator, reader.GetInt32(0), reader.GetInt64(1), reader.GetInt64(2)) : null;
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

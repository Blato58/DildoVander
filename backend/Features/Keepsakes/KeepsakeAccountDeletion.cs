namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    internal void FenceAccountDeletion(string accountId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Execute(connection, transaction, "INSERT OR IGNORE INTO dv10_account_fences(owner_id) VALUES($owner)", ("$owner", accountId));
            DeleteAccountCompletionUpdates(connection, transaction, accountId);
            transaction.Commit();
        }
    }

    internal bool DeleteAccountContent(string accountId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var command = Command(connection, transaction,
                "SELECT keepsake_id FROM dv10_customer_owners WHERE owner_id=$owner", ("$owner", accountId));
            var ids = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read()) { ids.Add(reader.GetString(0)); }
            }
            foreach (var id in ids)
            {
                using var deleted = Command(connection, transaction, "SELECT deleted FROM dv10_keepsakes WHERE id=$id", ("$id", id));
                if (deleted.ExecuteScalar() is long value && value == 0)
                {
                    PrepareDeletion(connection, transaction, id, Load(connection, transaction, id), KeepsakeDeleteScope.All, "account-deletion");
                }
            }
            DeleteAccountCompletionUpdates(connection, transaction, accountId);
            transaction.Commit();
            ResumeDeletionCleanup();
            using var pending = Command(connection, null, """
                SELECT COUNT(*) FROM dv10_deletions d JOIN dv10_customer_owners o ON o.keepsake_id=d.keepsake_id
                WHERE o.owner_id=$owner AND d.local_state='pending'
                """, ("$owner", accountId));
            if ((long)pending.ExecuteScalar()! != 0) { return false; }
            Execute(connection, null, "DELETE FROM dv10_customer_owners WHERE owner_id=$owner", ("$owner", accountId));
            return true;
        }
    }
}

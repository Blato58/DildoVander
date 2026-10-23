using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class OperationJournal
{
    internal string CustomerDataDatabasePath => new SqliteConnectionStringBuilder(connectionString).DataSource;

    internal CustomerDataSqliteGuard OpenCustomerDataGuard(CustomerDataGuardDeadline deadline)
    {
        return CustomerDataSqliteGuard.Acquire(connectionString, deadline);
    }

    internal CustomerOwnedSession[] ReadCustomerDataSessions(CustomerDataSqliteGuard guard,
        string accountId, CustomerDataGuardDeadline deadline)
    {
        deadline.Check();
        using (var revoked = guard.Command("SELECT 1 FROM customer_profile_revocations WHERE owner_id=$owner AND profile_id='*' LIMIT 1"))
        {
            revoked.Parameters.AddWithValue("$owner", accountId);
            if (revoked.ExecuteScalar() is not null)
            {
                throw new CustomerDataAccessException(401, "customer_data_unavailable");
            }
        }

        var items = new List<CustomerOwnedSession>(CustomerDataLimits.MaxOwnedSessions + 1);
        using (var command = guard.Command("""
            SELECT s.id,s.profile_id,s.profile_revision,s.device_id,s.conversation_id,
                CASE WHEN length(s.snapshot)>0 THEN 1 ELSE 0 END,
                CASE WHEN r.profile_id IS NULL THEN 0 ELSE 1 END
            FROM customer_sessions s
            LEFT JOIN customer_profile_revocations r
                ON r.owner_id=s.owner_id AND r.profile_id=s.profile_id
            WHERE s.owner_id=$owner
            ORDER BY s.id
            LIMIT $limit;
            """))
        {
            command.Parameters.AddWithValue("$owner", accountId);
            command.Parameters.AddWithValue("$limit", CustomerDataLimits.MaxOwnedSessions + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                deadline.Check();
                var sessionId = reader.GetString(0);
                var profileId = reader.GetString(1);
                items.Add(new CustomerOwnedSession(sessionId, profileId, reader.GetInt64(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    "account_" + CustomerHash(accountId + ":" + profileId).ToLowerInvariant(),
                    reader.GetInt32(6) != 0, reader.GetInt32(5) != 0));
            }
        }
        deadline.Check();
        if (items.Count > CustomerDataLimits.MaxOwnedSessions)
        {
            throw new CustomerDataAccessException(413, "owned_inventory_limit");
        }
        return items.ToArray();
    }
}

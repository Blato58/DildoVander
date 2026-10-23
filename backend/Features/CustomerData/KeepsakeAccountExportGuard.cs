using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record CustomerKeepsakeExportMetadata(string Id, int AccountEpoch, int ContentEpoch,
    int PhotoEpoch, int? DocumentRevision, long LinkedAt);

// Holds the existing archive monitor and the shared immediate SQLite transaction.
// It is intentionally synchronous because Monitor ownership is thread-affine.
internal sealed class KeepsakeAccountExportGuard : IDisposable
{
    private readonly object gate;
    private readonly int ownerThreadId;
    private CustomerDataSqliteGuard? database;

    internal CustomerDataSqliteGuard Database => database
        ?? throw new ObjectDisposedException(nameof(KeepsakeAccountExportGuard));

    private KeepsakeAccountExportGuard(object gate, CustomerDataSqliteGuard database)
    {
        this.gate = gate;
        this.database = database;
        ownerThreadId = Environment.CurrentManagedThreadId;
    }

    internal static KeepsakeAccountExportGuard Create(object gate, CustomerDataSqliteGuard database)
    {
        if (!Monitor.IsEntered(gate))
        {
            throw new InvalidOperationException("Keepsake export monitor must be held before creating its guard.");
        }
        return new KeepsakeAccountExportGuard(gate, database);
    }

    internal IReadOnlyList<CustomerKeepsakeExportMetadata> ReadMetadata(string accountId,
        CustomerDataGuardDeadline deadline)
    {
        deadline.Check();
        using (var fence = Database.Command("SELECT 1 FROM dv10_account_fences WHERE owner_id=$owner LIMIT 1"))
        {
            fence.Parameters.AddWithValue("$owner", accountId);
            if (fence.ExecuteScalar() is not null)
            {
                throw new CustomerDataAccessException(401, "customer_data_unavailable");
            }
        }

        var rows = new List<CustomerKeepsakeExportMetadata>(CustomerDataLimits.MaxOwnedSessions + 1);
        using (var command = Database.Command("""
            SELECT o.keepsake_id,o.epoch,k.epoch,k.photo_epoch,
                json_extract(k.document,'$.revision'),o.linked_at
            FROM dv10_customer_owners o
            JOIN dv10_keepsakes k ON k.id=o.keepsake_id
            WHERE o.owner_id=$owner AND o.epoch=k.account_access_epoch AND k.deleted=0
            ORDER BY o.keepsake_id
            LIMIT $limit;
            """))
        {
            command.Parameters.AddWithValue("$owner", accountId);
            command.Parameters.AddWithValue("$limit", CustomerDataLimits.MaxOwnedSessions + 1);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                deadline.Check();
                rows.Add(new CustomerKeepsakeExportMetadata(reader.GetString(0), reader.GetInt32(1),
                    reader.GetInt32(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.GetInt64(5)));
            }
        }
        deadline.Check();
        if (rows.Count > CustomerDataLimits.MaxOwnedSessions)
        {
            throw new CustomerDataAccessException(413, "keepsake_inventory_limit");
        }
        return rows.AsReadOnly();
    }

    public void Dispose()
    {
        if (database is null) { return; }
        if (Environment.CurrentManagedThreadId != ownerThreadId)
        {
            throw new InvalidOperationException("Keepsake export guard must be disposed on its acquiring thread.");
        }

        var heldDatabase = database;
        database = null;
        try { heldDatabase.Dispose(); }
        finally { Monitor.Exit(gate); }
    }
}

internal sealed partial class KeepsakeStore
{
    internal string CustomerDataDatabasePath => new SqliteConnectionStringBuilder(connectionString).DataSource;

    internal KeepsakeAccountExportGuard OpenCustomerDataExportGuard(CustomerDataGuardDeadline deadline)
    {
        var entered = false;
        while (!entered)
        {
            deadline.Check();
            entered = Monitor.TryEnter(gate, TimeSpan.FromMilliseconds(100));
            if (!entered) { deadline.Check(); }
        }

        try
        {
            deadline.Check();
            var database = CustomerDataSqliteGuard.Acquire(connectionString, deadline);
            try
            {
                deadline.Check();
                return KeepsakeAccountExportGuard.Create(gate, database);
            }
            catch
            {
                database.Dispose();
                throw;
            }
        }
        catch
        {
            Monitor.Exit(gate);
            throw;
        }
    }
}

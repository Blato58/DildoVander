using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    private void RecoverCompletionUpdates(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var affected = Command(connection, transaction,
            "SELECT DISTINCT keepsake_id FROM dv10_jobs WHERE state IN ('dispatched','leased')");
        var ids = new List<string>();
        using (var reader = affected.ExecuteReader())
        {
            while (reader.Read()) { ids.Add(reader.GetString(0)); }
        }
        // A crash cannot prove whether a dispatched provider request finished.
        // The recovery state and its completion update must commit together.
        Execute(connection, transaction,
            "UPDATE dv10_jobs SET state='awaiting_reconciliation', error_code='restart_after_dispatch' WHERE state='dispatched'");
        Execute(connection, transaction, "UPDATE dv10_jobs SET state='queued' WHERE state='leased'");
        foreach (var id in ids) { RecordCompletionUpdates(connection, transaction, id); }
        transaction.Commit();
    }
}

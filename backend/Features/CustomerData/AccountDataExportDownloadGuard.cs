namespace Kufr.Backend.Features.CustomerData;

// This technical job lease prevents replacement/deletion of a download proof
// between validation and byte acceptance. It never takes the job store Monitor.
internal sealed class AccountDataExportDownloadGuard : IDisposable
{
    private readonly CustomerDataSqliteGuard database;
    private bool disposed;
    internal AccountDataExportJob Job { get; }

    private AccountDataExportDownloadGuard(CustomerDataSqliteGuard database, AccountDataExportJob job)
    {
        this.database = database;
        Job = job;
    }

    internal static AccountDataExportDownloadGuard Acquire(string connectionString, string id,
        CustomerDataAccess access, CustomerDataGuardDeadline deadline)
    {
        var database = CustomerDataSqliteGuard.Acquire(connectionString, deadline);
        try
        {
            using var command = database.Command("""
                SELECT id,account_id,session_id,authority_revision,request_id,state,reason,requested_at,
                    ready_at,download_expires_at,inventory_revision,memory_revision,keepsake_revision,payload_sha256
                FROM account_data_export_jobs
                WHERE id=$id AND account_id=$account AND session_id=$session AND authority_revision=$authority
                """);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", access.AccountId);
            command.Parameters.AddWithValue("$session", access.SessionId);
            command.Parameters.AddWithValue("$authority", access.AuthorityRevision);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { throw new AccountDataExportException(404, "account_export_unavailable"); }
            var job = AccountDataExportStore.ReadDownloadJob(reader);
            deadline.Check();
            return new AccountDataExportDownloadGuard(database, job);
        }
        catch { database.Dispose(); throw; }
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        database.Dispose();
    }
}

internal sealed partial class AccountDataExportStore
{
    internal AccountDataExportDownloadGuard OpenDownloadGuard(string id, CustomerDataAccess access,
        CustomerDataGuardDeadline deadline) => AccountDataExportDownloadGuard.Acquire(connectionString, id, access, deadline);

    internal static AccountDataExportJob ReadDownloadJob(Microsoft.Data.Sqlite.SqliteDataReader reader) => ReadJob(reader);
}

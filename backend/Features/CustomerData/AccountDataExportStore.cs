using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kufr.Backend.Features.CustomerData;

internal sealed record AccountDataExportProof(string InventoryRevision, string MemoryRevision,
    string KeepsakeRevision, string PayloadSha256);

internal sealed record AccountDataExportJob(string Id, CustomerDataAccess Access, Guid RequestId,
    AccountDataExportState State, string? Reason, DateTimeOffset RequestedAt,
    DateTimeOffset? ReadyAt, DateTimeOffset? DownloadExpiresAt, AccountDataExportProof? Proof);

internal enum AccountDataExportFailure
{
    InventoryUnavailable,
    MemoryUnavailable,
    SourceChanged,
    PayloadTooLarge,
    PreparationInterrupted,
    DownloadExpired,
    InternalFailure
}

internal sealed record AccountDataExportReceiptLimits
{
    private const int MaximumPerAccount = 4_096;
    private const int MaximumGlobal = 65_536;

    internal int PerAccount { get; }
    internal int Global { get; }

    internal AccountDataExportReceiptLimits(int perAccount, int global)
    {
        if (perAccount is < 1 or > MaximumPerAccount
            || global is < 1 or > MaximumGlobal || global < perAccount)
        {
            throw new ArgumentOutOfRangeException(nameof(perAccount), "Export receipt limits are outside the supported bounds.");
        }
        PerAccount = perAccount;
        Global = global;
    }

    internal static AccountDataExportReceiptLimits Read(IConfiguration configuration) => new(
        configuration.GetValue("AccountDataExport:MaxReceiptsPerAccount", 128),
        configuration.GetValue("AccountDataExport:MaxReceipts", 4_096));
}

// This journal intentionally stores proofs and lifecycle metadata only. The
// export bytes and source content belong to a separate, short-lived pipeline.
internal sealed partial class AccountDataExportStore
{
    private const int MaximumJobs = 128;
    private const int MaximumProofTextLength = 256;
    private static readonly AccountDataExportReceiptLimits DefaultReceiptLimits = new(128, 4_096);
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly TimeProvider clock;
    private readonly AccountDataExportReceiptLimits receiptLimits;
    internal string DatabasePath => new SqliteConnectionStringBuilder(connectionString).DataSource;

    internal AccountDataExportStore(string dataRoot, TimeProvider clock,
        AccountDataExportReceiptLimits? receiptLimits = null)
    {
        Directory.CreateDirectory(dataRoot);
        var path = PathFor(dataRoot);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            DefaultTimeout = 5,
            Pooling = true
        }.ToString();
        this.clock = clock;
        this.receiptLimits = receiptLimits ?? DefaultReceiptLimits;

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS account_data_export_jobs(
                account_id TEXT PRIMARY KEY,
                id TEXT NOT NULL UNIQUE,
                session_id TEXT NOT NULL,
                authority_revision TEXT NOT NULL,
                request_id TEXT NOT NULL,
                state TEXT NOT NULL,
                reason TEXT,
                requested_at INTEGER NOT NULL,
                ready_at INTEGER,
                download_expires_at INTEGER,
                inventory_revision TEXT,
                memory_revision TEXT,
                keepsake_revision TEXT,
                payload_sha256 TEXT);
            CREATE INDEX IF NOT EXISTS account_data_export_queue
                ON account_data_export_jobs(state,requested_at,id);
            CREATE TABLE IF NOT EXISTS account_data_export_requests(
                account_id TEXT NOT NULL,
                request_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                authority_revision TEXT NOT NULL,
                job_id TEXT NOT NULL,
                requested_at INTEGER NOT NULL,
                PRIMARY KEY(account_id,request_id));
            CREATE INDEX IF NOT EXISTS account_data_export_requests_account
                ON account_data_export_requests(account_id,requested_at,request_id);
            CREATE TABLE IF NOT EXISTS account_data_export_account_fences(
                account_id TEXT PRIMARY KEY);
            DELETE FROM account_data_export_jobs WHERE account_id IN
                (SELECT account_id FROM account_data_export_account_fences);
            DELETE FROM account_data_export_requests WHERE account_id IN
                (SELECT account_id FROM account_data_export_account_fences);
            UPDATE account_data_export_jobs
                SET state='failed',reason='preparation_interrupted',ready_at=NULL,
                    download_expires_at=NULL,inventory_revision=NULL,memory_revision=NULL,
                    keepsake_revision=NULL,payload_sha256=NULL
                WHERE state='preparing';
            INSERT OR IGNORE INTO account_data_export_requests(
                account_id,request_id,session_id,authority_revision,job_id,requested_at)
            SELECT account_id,request_id,session_id,authority_revision,id,requested_at
                FROM account_data_export_jobs
                WHERE NOT EXISTS(SELECT 1 FROM account_data_export_account_fences f
                    WHERE f.account_id=account_data_export_jobs.account_id);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    internal static string PathFor(string dataRoot) => Path.Combine(Path.GetFullPath(dataRoot), "account-data-export.sqlite");

    internal AccountDataExportJob Create(CustomerDataAccess access, Guid requestId)
        => CreateCore(access, requestId, null);

    internal AccountDataExportJob Create(CustomerDataGuardPrelude authority, Guid requestId)
    {
        authority.ValidateCurrent();
        return CreateCore(authority.Access, requestId, authority);
    }

    private AccountDataExportJob CreateCore(CustomerDataAccess access, Guid requestId,
        CustomerDataGuardPrelude? authority)
    {
        authority?.ValidateCurrent();
        ValidateAccess(access);
        if (requestId == Guid.Empty)
        {
            throw new AccountDataExportException(400, "invalid_request_id");
        }

        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (IsAccountFenced(connection, transaction, access.AccountId))
            {
                throw new AccountDataExportException(410, "account_export_deleted");
            }
            var accepted = ReadRequest(connection, transaction, access.AccountId, requestId);
            if (accepted is not null)
            {
                if (accepted.Value.SessionId != access.SessionId
                    || accepted.Value.AuthorityRevision != access.AuthorityRevision)
                {
                    throw new AccountDataExportException(409, "request_conflict");
                }

                var current = ReadForAccount(connection, transaction, access.AccountId);
                if (current is null || current.Id != accepted.Value.JobId)
                {
                    throw new AccountDataExportException(409, "request_superseded");
                }
                authority?.ValidateCurrent();
                transaction.Commit();
                return current;
            }

            var existing = ReadForAccount(connection, transaction, access.AccountId);
            if (existing is null && Count(connection, transaction) >= MaximumJobs)
            {
                throw new AccountDataExportException(429, "export_queue_full");
            }
            if (CountRequests(connection, transaction, access.AccountId) >= receiptLimits.PerAccount
                || CountRequests(connection, transaction) >= receiptLimits.Global)
            {
                throw new AccountDataExportException(429, "export_request_limit");
            }

            var now = clock.GetUtcNow();
            var jobId = Guid.NewGuid().ToString("D");
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO account_data_export_jobs(account_id,id,session_id,authority_revision,request_id,
                    state,reason,requested_at,ready_at,download_expires_at,inventory_revision,memory_revision,
                    keepsake_revision,payload_sha256)
                VALUES($account,$id,$session,$authority,$request,'requested',NULL,$requested,NULL,NULL,NULL,NULL,NULL,NULL)
                ON CONFLICT(account_id) DO UPDATE SET
                    id=excluded.id,session_id=excluded.session_id,authority_revision=excluded.authority_revision,
                    request_id=excluded.request_id,state='requested',reason=NULL,requested_at=excluded.requested_at,
                    ready_at=NULL,download_expires_at=NULL,inventory_revision=NULL,memory_revision=NULL,
                    keepsake_revision=NULL,payload_sha256=NULL;
                """;
            command.Parameters.AddWithValue("$account", access.AccountId);
            command.Parameters.AddWithValue("$id", jobId);
            command.Parameters.AddWithValue("$session", access.SessionId);
            command.Parameters.AddWithValue("$authority", access.AuthorityRevision);
            command.Parameters.AddWithValue("$request", requestId.ToString("N"));
            command.Parameters.AddWithValue("$requested", now.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
            using (var receipt = connection.CreateCommand())
            {
                receipt.Transaction = transaction;
                receipt.CommandText = """
                    INSERT INTO account_data_export_requests(
                        account_id,request_id,session_id,authority_revision,job_id,requested_at)
                    VALUES($account,$request,$session,$authority,$job,$requested)
                    """;
                receipt.Parameters.AddWithValue("$account", access.AccountId);
                receipt.Parameters.AddWithValue("$request", requestId.ToString("N"));
                receipt.Parameters.AddWithValue("$session", access.SessionId);
                receipt.Parameters.AddWithValue("$authority", access.AuthorityRevision);
                receipt.Parameters.AddWithValue("$job", jobId);
                receipt.Parameters.AddWithValue("$requested", now.ToUnixTimeMilliseconds());
                receipt.ExecuteNonQuery();
            }
            var created = ReadForAccount(connection, transaction, access.AccountId)
                ?? throw new InvalidOperationException("Created export job disappeared.");
            authority?.ValidateCurrent();
            transaction.Commit();
            return created;
        }
    }

    internal AccountDataExportJob? TryClaim()
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            string? id;
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT id FROM account_data_export_jobs
                    WHERE state='requested' AND NOT EXISTS(
                        SELECT 1 FROM account_data_export_account_fences f
                        WHERE f.account_id=account_data_export_jobs.account_id)
                    ORDER BY requested_at,id LIMIT 1
                    """;
                id = select.ExecuteScalar() as string;
            }
            if (id is null)
            {
                transaction.Commit();
                return null;
            }

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE account_data_export_jobs SET state='preparing' WHERE id=$id AND state='requested'";
                update.Parameters.AddWithValue("$id", id);
                if (update.ExecuteNonQuery() != 1)
                {
                    transaction.Commit();
                    return null;
                }
            }
            var job = ReadById(connection, transaction, id);
            transaction.Commit();
            return job;
        }
    }

    internal bool Complete(string id, AccountDataExportProof proof)
    {
        ValidateProof(proof);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var now = clock.GetUtcNow();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE account_data_export_jobs SET state='ready',reason=NULL,ready_at=$ready,
                    download_expires_at=$expires,inventory_revision=$inventory,memory_revision=$memory,
                    keepsake_revision=$keepsake,payload_sha256=$hash
                WHERE id=$id AND state='preparing' AND NOT EXISTS(
                    SELECT 1 FROM account_data_export_account_fences f
                    WHERE f.account_id=account_data_export_jobs.account_id)
                """;
            command.Parameters.AddWithValue("$ready", now.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$expires", now.AddHours(1).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$inventory", proof.InventoryRevision);
            command.Parameters.AddWithValue("$memory", proof.MemoryRevision);
            command.Parameters.AddWithValue("$keepsake", proof.KeepsakeRevision);
            command.Parameters.AddWithValue("$hash", proof.PayloadSha256);
            command.Parameters.AddWithValue("$id", id);
            var changed = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return changed;
        }
    }

    internal bool Fail(string id, AccountDataExportFailure failure)
    {
        var reason = FailureReason(failure);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE account_data_export_jobs SET state='failed',reason=$reason,ready_at=NULL,
                    download_expires_at=NULL,inventory_revision=NULL,memory_revision=NULL,
                    keepsake_revision=NULL,payload_sha256=NULL
                WHERE id=$id AND (state IN ('requested','preparing')
                    OR (state='ready' AND $reason IN ('source_changed','download_expired')))
                    AND NOT EXISTS(SELECT 1 FROM account_data_export_account_fences f
                        WHERE f.account_id=account_data_export_jobs.account_id)
                """;
            command.Parameters.AddWithValue("$reason", reason);
            command.Parameters.AddWithValue("$id", id);
            var changed = command.ExecuteNonQuery() == 1;
            transaction.Commit();
            return changed;
        }
    }

    internal AccountDataExportJob? Find(string id, CustomerDataAccess access)
    {
        ValidateAccess(access);
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id,account_id,session_id,authority_revision,request_id,state,reason,requested_at,
                    ready_at,download_expires_at,inventory_revision,memory_revision,keepsake_revision,payload_sha256
                FROM account_data_export_jobs
                WHERE id=$id AND account_id=$account AND session_id=$session AND authority_revision=$authority
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", access.AccountId);
            command.Parameters.AddWithValue("$session", access.SessionId);
            command.Parameters.AddWithValue("$authority", access.AuthorityRevision);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadJob(reader) : null;
        }
    }

    internal bool DeleteForAccount(string id, string accountId)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(accountId)) { return false; }
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM account_data_export_jobs WHERE id=$id AND account_id=$account";
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$account", accountId);
            return command.ExecuteNonQuery() == 1;
        }
    }

    // The caller must invoke this only after the original account's permanent
    // deletion fences are durable. This fence prevents an in-flight captured
    // access proof from recreating job metadata after the saga cleanup.
    internal void DeleteAccountMetadata(string accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || accountId.Length > MaximumProofTextLength)
        {
            throw new ArgumentException("A bounded account id is required.", nameof(accountId));
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO account_data_export_account_fences(account_id) VALUES($account);
                DELETE FROM account_data_export_jobs WHERE account_id=$account;
                DELETE FROM account_data_export_requests WHERE account_id=$account;
                """;
            command.Parameters.AddWithValue("$account", accountId);
            command.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static int Count(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM account_data_export_jobs";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool IsAccountFenced(SqliteConnection connection, SqliteTransaction transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM account_data_export_account_fences WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", accountId);
        return command.ExecuteScalar() is not null;
    }

    private static int CountRequests(SqliteConnection connection, SqliteTransaction transaction,
        string? accountId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = accountId is null
            ? "SELECT COUNT(*) FROM account_data_export_requests"
            : "SELECT COUNT(*) FROM account_data_export_requests WHERE account_id=$account";
        if (accountId is not null) { command.Parameters.AddWithValue("$account", accountId); }
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static (string SessionId, string AuthorityRevision, string JobId)? ReadRequest(
        SqliteConnection connection, SqliteTransaction transaction, string accountId, Guid requestId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id,authority_revision,job_id
            FROM account_data_export_requests WHERE account_id=$account AND request_id=$request
            """;
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$request", requestId.ToString("N"));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static AccountDataExportJob? ReadForAccount(SqliteConnection connection,
        SqliteTransaction? transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,account_id,session_id,authority_revision,request_id,state,reason,requested_at,
                ready_at,download_expires_at,inventory_revision,memory_revision,keepsake_revision,payload_sha256
            FROM account_data_export_jobs WHERE account_id=$account
            """;
        command.Parameters.AddWithValue("$account", accountId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private static AccountDataExportJob? ReadById(SqliteConnection connection,
        SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,account_id,session_id,authority_revision,request_id,state,reason,requested_at,
                ready_at,download_expires_at,inventory_revision,memory_revision,keepsake_revision,payload_sha256
            FROM account_data_export_jobs WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private static AccountDataExportJob ReadJob(SqliteDataReader reader)
    {
        var account = new CustomerDataAccess(reader.GetString(1), reader.GetString(2), reader.GetString(3));
        var requestId = Guid.ParseExact(reader.GetString(4), "N");
        if (!Enum.TryParse<AccountDataExportState>(reader.GetString(5), ignoreCase: true, out var state)
            || !Enum.IsDefined(state))
        {
            throw new InvalidDataException("Export job contains an unknown state.");
        }
        AccountDataExportProof? proof = null;
        if (!reader.IsDBNull(10) && !reader.IsDBNull(11) && !reader.IsDBNull(12) && !reader.IsDBNull(13))
        {
            proof = new AccountDataExportProof(reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.GetString(13));
        }
        return new AccountDataExportJob(reader.GetString(0), account, requestId, state,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(7)),
            reader.IsDBNull(8) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)),
            reader.IsDBNull(9) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)), proof);
    }

    private static void ValidateAccess(CustomerDataAccess access)
    {
        if (string.IsNullOrWhiteSpace(access.AccountId) || string.IsNullOrWhiteSpace(access.SessionId)
            || string.IsNullOrWhiteSpace(access.AuthorityRevision)
            || access.AccountId.Length > MaximumProofTextLength || access.SessionId.Length > MaximumProofTextLength
            || access.AuthorityRevision.Length > MaximumProofTextLength)
        {
            throw new AccountDataExportException(400, "invalid_access_proof");
        }
    }

    private static void ValidateProof(AccountDataExportProof proof)
    {
        if (!ValidRevision(proof.InventoryRevision) || !ValidRevision(proof.MemoryRevision)
            || !ValidRevision(proof.KeepsakeRevision) || proof.PayloadSha256.Length != 64
            || proof.PayloadSha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new AccountDataExportException(400, "invalid_export_proof");
        }
    }

    private static bool ValidRevision(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= MaximumProofTextLength;

    private static string FailureReason(AccountDataExportFailure failure) => failure switch
    {
        AccountDataExportFailure.InventoryUnavailable => "inventory_unavailable",
        AccountDataExportFailure.MemoryUnavailable => "memory_unavailable",
        AccountDataExportFailure.SourceChanged => "source_changed",
        AccountDataExportFailure.PayloadTooLarge => "payload_too_large",
        AccountDataExportFailure.PreparationInterrupted => "preparation_interrupted",
        AccountDataExportFailure.DownloadExpired => "download_expired",
        AccountDataExportFailure.InternalFailure => "export_failed",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };
}

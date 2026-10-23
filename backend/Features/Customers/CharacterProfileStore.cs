using System.Text.Json;
using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Customers;

// A separate durable SQLite feature store follows the existing archive/job pattern.
// It does not mutate Identity's migration-managed schema during service startup.
internal sealed class CharacterProfileStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;

    internal CharacterProfileStore(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "customer-characters.sqlite"),
            ForeignKeys = true, DefaultTimeout = 15
        }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS character_profiles (
                account_id TEXT PRIMARY KEY, profile_id TEXT NOT NULL UNIQUE,
                revision INTEGER NOT NULL, input_json TEXT NOT NULL,
                status TEXT NOT NULL, enrichment_json TEXT NULL,
                error_code TEXT NULL, updated_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS character_jobs (
                id TEXT PRIMARY KEY, account_id TEXT NOT NULL, profile_id TEXT NOT NULL,
                revision INTEGER NOT NULL, input_json TEXT NOT NULL, rejected TEXT NULL,
                status TEXT NOT NULL, lease_token TEXT NULL, lease_until INTEGER NOT NULL DEFAULT 0,
                attempt INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL, openai_consent INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS character_jobs_queue ON character_jobs(status, created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS character_jobs_account ON character_jobs(account_id);
            CREATE TABLE IF NOT EXISTS character_account_fences (account_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS character_deletions (
                account_id TEXT NOT NULL, profile_id TEXT NOT NULL, completed INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(account_id,profile_id));
            CREATE TABLE IF NOT EXISTS character_provider_usage (
                job_id TEXT PRIMARY KEY, model TEXT NOT NULL,
                input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL, processed_at INTEGER NOT NULL);
            """;
        command.ExecuteNonQuery();
        using var column = connection.CreateCommand();
        column.CommandText = "SELECT COUNT(*) FROM pragma_table_info('character_jobs') WHERE name='openai_consent'";
        if ((long)column.ExecuteScalar()! == 0)
        {
            column.CommandText = "ALTER TABLE character_jobs ADD COLUMN openai_consent INTEGER NOT NULL DEFAULT 0";
            column.ExecuteNonQuery();
        }
        // Legacy jobs carry no affirmative consent. Keep their saved profile, but never dispatch them.
        column.CommandText = """
            UPDATE character_profiles SET status='saved',error_code=NULL
                WHERE status IN ('queued','running') AND account_id IN
                    (SELECT account_id FROM character_jobs WHERE openai_consent=0);
            DELETE FROM character_jobs WHERE openai_consent=0;
            """;
        column.ExecuteNonQuery();
    }

    internal string CustomerDataDatabasePath => new SqliteConnectionStringBuilder(connectionString).DataSource;

    internal CustomerDataSqliteGuard OpenCustomerDataGuard(CustomerDataGuardDeadline deadline)
    {
        return CustomerDataSqliteGuard.Acquire(connectionString, deadline);
    }

    internal void ValidateCustomerDataAccount(CustomerDataSqliteGuard guard, string accountId)
    {
        using var command = guard.Command("""
            SELECT 1 FROM character_account_fences WHERE account_id=$account
            UNION ALL
            SELECT 1 FROM character_deletions WHERE account_id=$account AND profile_id='*'
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$account", accountId);
        if (command.ExecuteScalar() is not null)
        {
            throw new CustomerDataAccessException(401, "customer_data_unavailable");
        }
    }

    internal bool CustomerDataProfileRevoked(CustomerDataSqliteGuard guard, string accountId, string profileId)
    {
        using var command = guard.Command("SELECT 1 FROM character_deletions WHERE account_id=$account AND profile_id=$profile LIMIT 1");
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        return command.ExecuteScalar() is not null;
    }

    internal Task<CharacterProfile?> ReadAsync(string accountId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        return Task.FromResult(Read(connection, null, accountId));
    }

    internal CharacterProfile? ReadHeldCustomerAdmissionProfile(CustomerDataSqliteGuard guard, string accountId,
        string profileId, long? expectedRevision = null)
    {
        ValidateCustomerDataAccount(guard, accountId);
        if (CustomerDataProfileRevoked(guard, accountId, profileId)) { return null; }
        var profile = Read(guard.Connection, guard.Transaction, accountId);
        return profile is not null && profile.ProfileId == profileId && (expectedRevision is null || profile.Revision == expectedRevision)
            ? profile : null;
    }

    internal CharacterConversationSnapshot? CaptureHeldCustomerAdmissionProfile(CustomerDataSqliteGuard guard,
        string accountId, string profileId, long revision)
    {
        var profile = ReadHeldCustomerAdmissionProfile(guard, accountId, profileId, revision);
        return profile is null ? null : new(profile.ProfileId, profile.Revision, profile.UserInput, profile.Enrichment, Now());
    }

    internal Task RecordProviderUsageAsync(string jobId, string model, int inputTokens, int outputTokens)
    {
        if (model.Length > 150 || inputTokens < 0 || outputTokens < 0)
        {
            throw new InvalidDataException("Invalid character provider usage.");
        }
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Provider audit survives revision/deletion fences, without user or character text.
        command.CommandText = """
            INSERT INTO character_provider_usage(job_id,model,input_tokens,output_tokens,processed_at)
            VALUES($job,$model,$input,$output,$now)
            ON CONFLICT(job_id) DO UPDATE SET input_tokens=input_tokens+$input,
                output_tokens=output_tokens+$output,processed_at=$now;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$input", inputTokens);
        command.Parameters.AddWithValue("$output", outputTokens);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    internal Task<CharacterProfile> SaveAsync(string accountId, CharacterWriteRequest request,
        CancellationToken token = default) => SaveRevisionAsync(accountId, request, false, token);

    internal async Task<CharacterProfile> CorrectAsync(string accountId, CharacterCorrectionRequest request,
        CancellationToken token = default)
    {
        if (!request.OpenAiConsent) { throw new CharacterException("openai_consent_required", 409); }
        var current = await ReadAsync(accountId, token) ?? throw new CharacterException("character_not_found", 404);
        return await SaveRevisionAsync(accountId,
            new(request.ExpectedRevision, current.UserInput, request.ExpectedProfileId, OpenAiConsent: true), true, token);
    }

    private Task<CharacterProfile> SaveRevisionAsync(string accountId, CharacterWriteRequest request,
        bool correction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Validate(request.UserInput);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (Scalar(connection, transaction, "SELECT COUNT(*) FROM character_account_fences WHERE account_id=$account", accountId) != 0)
        {
            throw new CharacterException("account_data_deleted", 409);
        }
        var current = Read(connection, transaction, accountId);
        if (request.ExpectedRevision != (current?.Revision ?? 0)
            || (current is not null && request.ExpectedProfileId != current.ProfileId)
            || (current is null && request.ExpectedProfileId is not null))
        {
            throw new CharacterException("character_revision_conflict", 409);
        }
        if (!correction && !request.NewCharacter && current is not null && current.UserInput == request.UserInput
            && (request.OpenAiConsent ? current.Status != "saved" : current.Status is not ("queued" or "running")))
        {
            return Task.FromResult(current);
        }
        if (request.OpenAiConsent && Scalar(connection, transaction, "SELECT COUNT(*) FROM character_jobs WHERE status IN ('queued','running')", accountId) >= 1000)
        {
            throw new CharacterException("character_queue_full", 429);
        }
        var profile = new CharacterProfile(request.NewCharacter ? Guid.NewGuid().ToString("N")
                : current?.ProfileId ?? Guid.NewGuid().ToString("N"),
            request.NewCharacter ? 1 : (current?.Revision ?? 0) + 1,
            request.UserInput, request.OpenAiConsent ? "queued" : "saved", null, null, Now());
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO character_profiles(account_id,profile_id,revision,input_json,status,enrichment_json,error_code,updated_at)
                VALUES($account,$profile,$revision,$input,$status,NULL,NULL,$now)
                ON CONFLICT(account_id) DO UPDATE SET profile_id=$profile,revision=$revision,input_json=$input,
                    status=$status,enrichment_json=NULL,error_code=NULL,updated_at=$now;
                DELETE FROM character_jobs WHERE account_id=$account;
                INSERT INTO character_jobs(id,account_id,profile_id,revision,input_json,rejected,status,created_at,openai_consent)
                SELECT $job,$account,$profile,$revision,$input,$rejected,'queued',$now,1 WHERE $consent=1;
                """;
            command.Parameters.AddWithValue("$account", accountId);
            command.Parameters.AddWithValue("$status", profile.Status);
            command.Parameters.AddWithValue("$consent", request.OpenAiConsent);
            command.Parameters.AddWithValue("$profile", profile.ProfileId);
            command.Parameters.AddWithValue("$revision", profile.Revision);
            command.Parameters.AddWithValue("$input", JsonSerializer.Serialize(request.UserInput, Json));
            command.Parameters.AddWithValue("$job", Guid.NewGuid().ToString("N"));
            command.Parameters.AddWithValue("$rejected", correction
                ? JsonSerializer.Serialize(current?.Enrichment is null ? new { recognizedName = "", world = "" }
                    : new { recognizedName = current.Enrichment.RecognizedName, world = current.Enrichment.World }, Json)
                : DBNull.Value);
            command.Parameters.AddWithValue("$now", profile.UpdatedAt);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Task.FromResult(profile);
    }

    internal Task DeleteAsync(string accountId, long revision, string profileId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var current = Read(connection, transaction, accountId);
        if (current is not null && (current.Revision != revision || current.ProfileId != profileId))
        {
            throw new CharacterException("character_revision_conflict", 409);
        }
        RecordDeletion(connection, transaction, accountId, profileId);
        DeleteData(connection, transaction, accountId);
        transaction.Commit();
        return Task.CompletedTask;
    }

    // Account deletion orchestration calls this before deleting/revoking identity.
    // The permanent fence rejects races that already authenticated before deletion.
    internal Task FenceAccountDeletionAsync(string accountId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR IGNORE INTO character_account_fences(account_id) VALUES($account)";
        command.Parameters.AddWithValue("$account", accountId);
        command.ExecuteNonQuery();
        RecordDeletion(connection, transaction, accountId, "*");
        DeleteData(connection, transaction, accountId);
        transaction.Commit();
        return Task.CompletedTask;
    }

    internal Task<bool> IsDeletedAsync(string accountId, string profileId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM character_deletions WHERE account_id=$account AND profile_id IN ($profile,'*')";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        return Task.FromResult((long)command.ExecuteScalar()! > 0);
    }

    internal Task<(string AccountId, string ProfileId)[]> PendingDeletionsAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT account_id,profile_id FROM character_deletions WHERE completed=0 LIMIT 100";
        using var reader = command.ExecuteReader();
        var items = new List<(string, string)>();
        while (reader.Read()) { items.Add((reader.GetString(0), reader.GetString(1))); }
        return Task.FromResult(items.ToArray());
    }

    internal Task CompleteDeletionAsync(string accountId, string profileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // Retain the tombstone to fence provider startup while the journal is being repaired.
        command.CommandText = "UPDATE character_deletions SET completed=1 WHERE account_id=$account AND profile_id=$profile";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.ExecuteNonQuery();
        return Task.CompletedTask;
    }

    internal async Task ReplayDeletionsAsync(Func<string, string?, Task> apply, CancellationToken token = default)
    {
        foreach (var item in await PendingDeletionsAsync(token))
        {
            await apply(item.AccountId, item.ProfileId == "*" ? null : item.ProfileId);
            await CompleteDeletionAsync(item.AccountId, item.ProfileId);
        }
    }

    private static void RecordDeletion(SqliteConnection connection, SqliteTransaction transaction, string accountId, string profileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO character_deletions(account_id,profile_id) VALUES($account,$profile) ON CONFLICT(account_id,profile_id) DO UPDATE SET completed=0";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$profile", profileId);
        command.ExecuteNonQuery();
    }

    internal async Task<CharacterConversationSnapshot?> CaptureAsync(string accountId, string profileId,
        long revision, CancellationToken token = default)
    {
        var current = await ReadAsync(accountId, token);
        return current is not null && current.ProfileId == profileId && current.Revision == revision
            ? new(current.ProfileId, current.Revision, current.UserInput, current.Enrichment, Now()) : null;
    }

    internal Task<CharacterJob?> ClaimAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,account_id,profile_id,revision,input_json,rejected,attempt FROM character_jobs
            WHERE openai_consent=1 AND ((status='queued' AND lease_until <= $now) OR (status='running' AND lease_until < $now))
            ORDER BY created_at,id LIMIT 1;
            """;
        command.Parameters.AddWithValue("$now", Now());
        CharacterJob? job;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) { return Task.FromResult<CharacterJob?>(null); }
            job = new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                JsonSerializer.Deserialize<CharacterUserInput>(reader.GetString(4), Json)!,
                reader.IsDBNull(5) ? null : reader.GetString(5), Guid.NewGuid().ToString("N"), reader.GetInt32(6) + 1, OpenAiConsent: true);
        }
        command.Parameters.Clear();
        command.CommandText = """
            UPDATE character_jobs SET status='running',lease_token=$lease,lease_until=$until,attempt=$attempt WHERE id=$id;
            UPDATE character_profiles SET status='running',updated_at=$now
                WHERE account_id=$account AND profile_id=$profile AND revision=$revision;
            """;
        BindJob(command, job);
        command.Parameters.AddWithValue("$until", Now() + 120);
        command.Parameters.AddWithValue("$attempt", job.Attempt);
        command.Parameters.AddWithValue("$now", Now());
        command.ExecuteNonQuery();
        transaction.Commit();
        return Task.FromResult<CharacterJob?>(job);
    }

    internal Task<bool> CanDispatchAsync(CharacterJob job, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM character_jobs j JOIN character_profiles p
                ON p.account_id=j.account_id AND p.profile_id=j.profile_id AND p.revision=j.revision
            WHERE j.id=$id AND j.account_id=$account AND j.lease_token=$lease AND j.status='running'
                AND j.openai_consent=1 AND NOT EXISTS
                    (SELECT 1 FROM character_account_fences WHERE account_id=$account)
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$account", job.AccountId);
        command.Parameters.AddWithValue("$lease", job.LeaseToken);
        return Task.FromResult(job.OpenAiConsent && (long)command.ExecuteScalar()! == 1);
    }

    internal Task<bool> CompleteAsync(CharacterJob job, CharacterEnrichment? enrichment, string? errorCode,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (enrichment is not null) { CharacterEnrichmentValidation.Validate(enrichment, job.Revision); }
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var status = enrichment is null ? "failed" : enrichment.NeedsClarification ? "needs_clarification" : "ready";
        command.CommandText = """
            UPDATE character_profiles SET status=$status,enrichment_json=$enrichment,error_code=$error,updated_at=$now
            WHERE account_id=$account AND profile_id=$profile AND revision=$revision
              AND EXISTS(SELECT 1 FROM character_jobs WHERE id=$id AND status='running' AND lease_token=$lease);
            """;
        BindJob(command, job);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$enrichment", enrichment is null ? DBNull.Value : JsonSerializer.Serialize(enrichment, Json));
        command.Parameters.AddWithValue("$error", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Now());
        var changed = command.ExecuteNonQuery() == 1;
        command.CommandText = "UPDATE character_jobs SET status=$status,lease_until=0 WHERE id=$id AND lease_token=$lease";
        command.ExecuteNonQuery();
        transaction.Commit();
        return Task.FromResult(changed);
    }

    internal Task<bool> RetryAsync(CharacterJob job, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE character_jobs SET status='queued',lease_until=$until
            WHERE id=$id AND lease_token=$lease AND status='running'
              AND EXISTS(SELECT 1 FROM character_profiles WHERE account_id=$account AND profile_id=$profile AND revision=$revision);
            """;
        BindJob(command, job);
        command.Parameters.AddWithValue("$until", Now() + Math.Min(60, 5 * (1 << Math.Min(3, job.Attempt))));
        var changed = command.ExecuteNonQuery() == 1;
        if (changed)
        {
            command.CommandText = "UPDATE character_profiles SET status='queued',error_code='character_retry_pending' WHERE account_id=$account AND profile_id=$profile AND revision=$revision";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        return Task.FromResult(changed);
    }

    internal Task<CharacterJobSummary[]> JobSummariesAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.profile_id,p.revision,p.status,COALESCE(j.attempt,0),p.error_code,p.updated_at
            FROM character_profiles p LEFT JOIN character_jobs j ON j.account_id=p.account_id AND j.profile_id=p.profile_id AND j.revision=p.revision
            ORDER BY p.updated_at DESC LIMIT 100
            """;
        using var reader = command.ExecuteReader();
        var items = new List<CharacterJobSummary>();
        while (reader.Read()) { items.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetInt64(5))); }
        return Task.FromResult(items.ToArray());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static CharacterProfile? Read(SqliteConnection connection, SqliteTransaction? transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT profile_id,revision,input_json,status,enrichment_json,error_code,updated_at FROM character_profiles WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", accountId);
        using var reader = command.ExecuteReader();
        return !reader.Read() ? null : new(reader.GetString(0), reader.GetInt64(1),
            JsonSerializer.Deserialize<CharacterUserInput>(reader.GetString(2), Json)!, reader.GetString(3),
            reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<CharacterEnrichment>(reader.GetString(4), Json),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6));
    }

    private static long Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$account", accountId);
        return (long)command.ExecuteScalar()!;
    }

    private static void DeleteData(SqliteConnection connection, SqliteTransaction transaction, string accountId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM character_jobs WHERE account_id=$account; DELETE FROM character_profiles WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", accountId);
        command.ExecuteNonQuery();
    }

    private static void BindJob(SqliteCommand command, CharacterJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$account", job.AccountId);
        command.Parameters.AddWithValue("$profile", job.ProfileId);
        command.Parameters.AddWithValue("$revision", job.Revision);
        command.Parameters.AddWithValue("$lease", job.LeaseToken);
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static void Validate(CharacterUserInput? input)
    {
        if (input is null || string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 100
            || string.IsNullOrWhiteSpace(input.World) || input.World.Length > 200
            || input.Version?.Length > 200 || input.Description?.Length > 2000 || input.Language?.Length > 40
            || new[] { input.Name, input.World, input.Version, input.Description, input.Language }
                .Any(value => value?.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) == true))
        {
            throw new CharacterException("invalid_character_input");
        }
    }
}

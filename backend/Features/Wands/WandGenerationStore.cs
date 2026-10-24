using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Customers;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Wands;

// A write transaction protects the operation, lease and immutable selection across
// instances. No transaction spans provider or identity-store calls.
internal sealed class WandGenerationStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string connectionString;
    private readonly TimeProvider clock;
    public WandGenerationStore(IConfiguration configuration, TimeProvider clock)
    {
        this.clock = clock;
        var path = Path.GetFullPath(configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Backend:DatabasePath is required."));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 15 }.ToString();
        using var connection = Open();
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS wand_generation_sessions(
                id TEXT PRIMARY KEY, account_id TEXT NOT NULL, operation_id TEXT NOT NULL,
                profile_id TEXT NOT NULL, profile_revision INTEGER NOT NULL, request_hash TEXT NOT NULL,
                request_json TEXT, state TEXT NOT NULL, error_code TEXT,
                lease_token TEXT, lease_until INTEGER NOT NULL DEFAULT 0, candidates_json TEXT, selected_json TEXT,
                UNIQUE(account_id,operation_id));
            CREATE TABLE IF NOT EXISTS wand_generation_revocations(
                account_id TEXT NOT NULL, profile_id TEXT NOT NULL, PRIMARY KEY(account_id,profile_id));
            CREATE TABLE IF NOT EXISTS wand_generation_usage(
                session_id TEXT NOT NULL, attempt_id TEXT NOT NULL, model TEXT NOT NULL,
                prompt_version TEXT NOT NULL, input_tokens INTEGER NOT NULL, output_tokens INTEGER NOT NULL,
                processed_at INTEGER NOT NULL, usage_reported INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(session_id,attempt_id));
            """);
        using var column = Command(connection, null,
            "SELECT COUNT(*) FROM pragma_table_info('wand_generation_usage') WHERE name='usage_reported'");
        if (Convert.ToInt64(column.ExecuteScalar()) == 0)
            Execute(connection, null, "ALTER TABLE wand_generation_usage ADD COLUMN usage_reported INTEGER NOT NULL DEFAULT 0");
    }

    internal WandGenerationSession Begin(string accountId, string operationId,
        CharacterConversationSnapshot profile, int readyMask, bool openAiConsent,
        IReadOnlyList<string>? evidence = null)
    {
        if (!WandGenerationValidation.Text(accountId, 200) || !WandGenerationValidation.Text(operationId, 120)
            || !WandGenerationValidation.Text(profile.ProfileId, 100) || profile.Revision < 1
            || readyMask is < 0 or > 63 || evidence?.Count > 32
            || evidence?.Any(value => !WandGenerationValidation.Text(value, 1000)) == true)
            throw new WandGenerationException("invalid_wand_generation", 400);
        if (!openAiConsent) throw new WandGenerationException("openai_consent_required");
        // Conversation facts remain in their revocable memory source. They are
        // supplied ephemerally to a claimed provider call, never retained here.
        var request = new WandGenerationRequest(accountId, operationId, profile, readyMask, openAiConsent, []);
        var hash = Hash(Serialize(request with { Profile = profile with { CapturedAt = 0 } }));
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNotRevoked(connection, transaction, accountId, profile.ProfileId);
        using (var previous = Command(connection, transaction,
            "SELECT request_hash FROM wand_generation_sessions WHERE account_id=$account AND operation_id=$op",
            ("$account", accountId), ("$op", operationId)))
        {
            if (previous.ExecuteScalar() is string oldHash)
            {
                var previousSession = Read(connection, transaction, accountId, operationId)
                    ?? throw new WandGenerationException("wand_identity_revoked", 410);
                if (oldHash != hash) throw new WandGenerationException("wand_operation_conflict");
                return previousSession;
            }
        }
        var id = Guid.NewGuid().ToString("N");
        Execute(connection, transaction, """
            INSERT INTO wand_generation_sessions(id,account_id,operation_id,profile_id,profile_revision,
                request_hash,request_json,state,error_code)
            VALUES($id,$account,$op,$profile,$revision,$hash,$request,$state,$error)
            """, ("$id", id), ("$account", accountId), ("$op", operationId), ("$profile", profile.ProfileId),
            ("$revision", profile.Revision), ("$hash", hash), ("$request", Serialize(request)),
            ("$state", readyMask == 0 ? "unavailable" : "queued"),
            ("$error", readyMask == 0 ? "no_available_compartment" : null));
        var result = Read(connection, transaction, accountId, operationId)!;
        transaction.Commit();
        return result;
    }

    internal WandGenerationSession? Read(string accountId, string operationId)
    {
        using var connection = Open();
        return Read(connection, null, accountId, operationId);
    }

    internal bool HasOperation(string accountId, string operationId)
    {
        using var connection = Open();
        using var command = Command(connection, null,
            "SELECT 1 FROM wand_generation_sessions WHERE account_id=$account AND operation_id=$op",
            ("$account", accountId), ("$op", operationId));
        return command.ExecuteScalar() is not null;
    }

    internal GeneratedWandSnapshot? LatestSelected(string accountId, string profileId, long revision)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT selected_json FROM wand_generation_sessions s
                WHERE account_id=$account AND profile_id=$profile AND profile_revision=$revision
                AND state='selected' AND selected_json IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM wand_generation_revocations r
                    WHERE r.account_id=s.account_id AND r.profile_id IN ('*',s.profile_id))
                ORDER BY rowid DESC LIMIT 1
            """, ("$account", accountId), ("$profile", profileId), ("$revision", revision));
        return command.ExecuteScalar() is string json ? Deserialize<GeneratedWandSnapshot>(json) : null;
    }

    internal GeneratedWandSnapshot? LatestBonded(string accountId, string profileId, long revision)
    {
        using var connection = Open();
        using (var tables = Command(connection, null,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('dv10_customer_owners','dv10_keepsakes')"))
            if (Convert.ToInt32(tables.ExecuteScalar()) != 2) return null;
        using var command = Command(connection, null, """
            SELECT s.selected_json FROM wand_generation_sessions s
                WHERE s.account_id=$account AND s.profile_id=$profile AND s.profile_revision<=$revision
                AND s.state='selected' AND s.selected_json IS NOT NULL
                AND NOT EXISTS(SELECT 1 FROM wand_generation_revocations r
                    WHERE r.account_id=s.account_id AND r.profile_id IN ('*',s.profile_id))
                AND EXISTS(SELECT 1 FROM dv10_customer_owners o JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                    WHERE o.owner_id=s.account_id AND k.deleted=0 AND k.account_access_epoch=o.epoch
                    AND json_extract(k.document,'$.snapshot.generatedWand.wandId')=json_extract(s.selected_json,'$.wandId'))
                ORDER BY s.rowid DESC LIMIT 1
            """, ("$account", accountId), ("$profile", profileId), ("$revision", revision));
        return command.ExecuteScalar() is string json ? Deserialize<GeneratedWandSnapshot>(json) : null;
    }

    private WandGenerationSession? Read(SqliteConnection connection, SqliteTransaction? transaction,
        string accountId, string operationId)
    {
        using var command = Command(connection, transaction, """
            SELECT id,profile_id,profile_revision,state,error_code,candidates_json,selected_json,
                request_json,lease_until FROM wand_generation_sessions WHERE account_id=$account AND operation_id=$op
            """, ("$account", accountId), ("$op", operationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var state = reader.GetString(3);
        if (state == "revoked") return null;
        var expired = state == "running" && reader.GetInt64(8) <= Now;
        var request = reader.IsDBNull(7) ? null : Deserialize<WandGenerationRequest>(reader.GetString(7));
        return new(reader.GetString(0), operationId, reader.GetString(1), reader.GetInt64(2), request?.ReadyMask ?? 0,
            expired ? "unavailable" : state, expired ? "generation_interrupted" : reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? [] : Deserialize<GeneratedWandSnapshot[]>(reader.GetString(5)),
            reader.IsDBNull(6) ? null : Deserialize<GeneratedWandSnapshot>(reader.GetString(6)));
    }

    internal WandGenerationJob? Claim(string accountId, string operationId, bool retry)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var session = Read(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        RequireNotRevoked(connection, transaction, accountId, session.ProfileId);
        if (session.Status != "queued" && !(retry && session.Status == "unavailable")) return null;
        if (session.ReadyMask == 0) return null;
        using var command = Command(connection, transaction,
            "SELECT request_json FROM wand_generation_sessions WHERE id=$id", ("$id", session.SessionId));
        var request = Deserialize<WandGenerationRequest>((string)command.ExecuteScalar()!);
        var lease = Guid.NewGuid().ToString("N");
        Execute(connection, transaction, """
            UPDATE wand_generation_sessions SET state='running',error_code=NULL,lease_token=$lease,
                lease_until=$until WHERE id=$id
            """, ("$lease", lease), ("$until", Now + 90), ("$id", session.SessionId));
        transaction.Commit();
        return new(session.SessionId, lease, request);
    }

    internal bool CanDispatch(WandGenerationJob job)
    {
        using var connection = Open();
        using var command = Command(connection, null, """
            SELECT 1 FROM wand_generation_sessions s WHERE id=$id AND state='running' AND lease_token=$lease
                AND lease_until>$now AND NOT EXISTS(SELECT 1 FROM wand_generation_revocations r
                    WHERE r.account_id=s.account_id AND r.profile_id IN ('*',s.profile_id))
            """, ("$id", job.SessionId), ("$lease", job.LeaseToken), ("$now", Now));
        return job.Request.OpenAiConsent && command.ExecuteScalar() is not null;
    }

    internal void Complete(WandGenerationJob job, WandCandidateContent[] contents)
    {
        WandGenerationValidation.Validate(contents, job.Request.ReadyMask);
        var slots = WandGenerationValidation.Slots(job.Request.ReadyMask);
        var candidates = contents.Select((content, index) => new GeneratedWandSnapshot(
            Guid.NewGuid().ToString("N"), job.SessionId, job.Request.Profile.ProfileId,
            job.Request.Profile.Revision, slots[index], content.Name, content.Material, content.Core,
            content.Temperament, content.Lore, content.DesignDescription, content.Affinity,
            content.Weakness, content.Spells, content.CombatArchetype)).ToArray();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNotRevoked(connection, transaction, job.Request.AccountId, job.Request.Profile.ProfileId);
        var changed = Execute(connection, transaction, """
            UPDATE wand_generation_sessions SET state='ready',error_code=NULL,candidates_json=$candidates,
                lease_token=NULL,lease_until=0 WHERE id=$id AND state='running' AND lease_token=$lease AND lease_until>$now
            """, ("$candidates", Serialize(candidates)), ("$id", job.SessionId), ("$lease", job.LeaseToken), ("$now", Now));
        if (changed != 1) throw new WandGenerationException("wand_generation_stale");
        transaction.Commit();
    }

    internal void Fail(WandGenerationJob job, string errorCode)
    {
        using var connection = Open();
        Execute(connection, null, """
            UPDATE wand_generation_sessions SET state='unavailable',error_code=$error,
                lease_token=NULL,lease_until=0 WHERE id=$id AND state='running' AND lease_token=$lease
            """, ("$error", errorCode), ("$id", job.SessionId), ("$lease", job.LeaseToken));
    }

    internal GeneratedWandSnapshot Select(string accountId, string operationId, string profileId,
        long profileRevision, int physicalSlot, int currentReadyMask = 63)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        RequireNotRevoked(connection, transaction, accountId, profileId);
        var session = Read(connection, transaction, accountId, operationId)
            ?? throw new WandGenerationException("wand_session_unavailable", 404);
        if (session.ProfileId != profileId || session.ProfileRevision != profileRevision)
            throw new WandGenerationException("wand_profile_revision_mismatch");
        if (session.SelectedWand is { } selected)
        {
            if (selected.PhysicalSlot != physicalSlot) throw new WandGenerationException("wand_selection_locked");
            return selected;
        }
        if (session.Status != "ready") throw new WandGenerationException("wand_candidates_unavailable");
        if (physicalSlot is < 1 or > 6 || currentReadyMask is < 0 or > 63
            || (currentReadyMask & (1 << (physicalSlot - 1))) == 0)
            throw new WandGenerationException("wand_compartment_unavailable");
        var candidate = session.Candidates.SingleOrDefault(wand => wand.PhysicalSlot == physicalSlot)
            ?? throw new WandGenerationException("wand_candidate_unavailable");
        Execute(connection, transaction,
            "UPDATE wand_generation_sessions SET state='selected',selected_json=$wand WHERE id=$id AND state='ready'",
            ("$wand", Serialize(candidate)), ("$id", session.SessionId));
        transaction.Commit();
        return candidate;
    }

    internal void RecordUsage(WandGenerationJob job, string model, string promptVersion, int input, int output,
        bool usageReported = true)
    {
        if (!WandGenerationValidation.Text(model, 150) || !WandGenerationValidation.Text(promptVersion, 100)
            || input < 0 || output < 0) throw new InvalidDataException("Invalid provider usage.");
        using var connection = Open();
        // Usage survives deletion without account, profile or generated text.
        Execute(connection, null, """
            INSERT INTO wand_generation_usage(session_id,attempt_id,model,prompt_version,input_tokens,
                output_tokens,processed_at,usage_reported) VALUES($id,$attempt,$model,$prompt,$input,$output,$now,$reported)
            ON CONFLICT(session_id,attempt_id) DO UPDATE SET model=$model,prompt_version=$prompt,
                input_tokens=MAX(input_tokens,$input),output_tokens=MAX(output_tokens,$output),
                usage_reported=MAX(usage_reported,$reported),processed_at=$now
            """, ("$id", job.SessionId), ("$attempt", job.LeaseToken), ("$model", model),
            ("$prompt", promptVersion), ("$input", input), ("$output", output), ("$now", Now), ("$reported", usageReported ? 1 : 0));
    }

    internal void DeleteAccount(string accountId) => Revoke(accountId, "*");
    internal void DeleteProfile(string accountId, string profileId) => Revoke(accountId, profileId);
    internal void DeleteOperation(string accountId, string operationId)
    {
        using var connection = Open();
        Execute(connection, null, """
            UPDATE wand_generation_sessions SET state='revoked',request_json=NULL,candidates_json=NULL,
                selected_json=NULL,lease_token=NULL,lease_until=0,error_code=NULL
                WHERE account_id=$account AND operation_id=$op
            """, ("$account", accountId), ("$op", operationId));
    }
    private void Revoke(string accountId, string profileId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction,
            "INSERT OR IGNORE INTO wand_generation_revocations VALUES($account,$profile)",
            ("$account", accountId), ("$profile", profileId));
        Execute(connection, transaction, """
            UPDATE wand_generation_sessions SET state='revoked',request_json=NULL,candidates_json=NULL,
                selected_json=NULL,lease_token=NULL,lease_until=0,error_code=NULL
                WHERE account_id=$account AND ($profile='*' OR profile_id=$profile)
            """, ("$account", accountId), ("$profile", profileId));
        transaction.Commit();
    }

    private static void RequireNotRevoked(SqliteConnection connection, SqliteTransaction transaction,
        string accountId, string profileId)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM wand_generation_revocations WHERE account_id=$account AND profile_id IN ('*',$profile) LIMIT 1",
            ("$account", accountId), ("$profile", profileId));
        if (command.ExecuteScalar() is not null) throw new WandGenerationException("wand_identity_revoked", 410);
    }
    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();
    private SqliteConnection Open() { var connection = new SqliteConnection(connectionString); connection.Open(); return connection; }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Key, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Key, object? Value)[] parameters)
    { using var command = Command(connection, transaction, sql, parameters); return command.ExecuteNonQuery(); }
    private static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("Invalid stored wand session.");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

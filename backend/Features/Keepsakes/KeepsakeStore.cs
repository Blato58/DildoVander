using Kufr.Backend.Features.Conversations;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;

namespace Kufr.Backend.Features.Keepsakes;

// A single process serializes metadata and file ingestion. SQLite transactions also fence
// access changes and deletion against publication; no transaction spans a provider call.
internal sealed partial class KeepsakeStore
{
    private readonly object gate = new();
    private readonly string connectionString;
    private readonly string dataRoot;
    private readonly TimeProvider clock;
    private readonly IConfiguration configuration;
    private readonly IDataProtector replayProtector;
    private readonly EveningMemoryStore? memoryStore;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public KeepsakeStore(IConfiguration configuration, TimeProvider clock, IDataProtectionProvider? protectionProvider = null, EveningMemoryStore? memoryStore = null)
    {
        this.configuration = configuration;
        this.memoryStore = memoryStore;
        this.clock = clock;
        var databasePath = configuration["Backend:DatabasePath"]
            ?? throw new InvalidOperationException("Backend:DatabasePath is required.");
        dataRoot = Path.GetFullPath(configuration["Keepsake:DataRoot"]
            ?? throw new InvalidOperationException("Keepsake:DataRoot is required outside releases and webroot."));
        if (dataRoot.Contains(Path.DirectorySeparatorChar + "wwwroot" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(dataRoot).Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Keepsake storage cannot be a webroot.");
        }
        Directory.CreateDirectory(dataRoot);
        protectionProvider ??= DataProtectionProvider.Create(new DirectoryInfo(
            configuration["Keepsake:DataProtectionRoot"] ?? dataRoot + ".keys"));
        replayProtector = protectionProvider.CreateProtector("DV10.SessionAndLinkReplay.v1");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath), DefaultTimeout = 5, Pooling = true
        }.ToString();
        using var connection = Open();
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS dv10_keepsakes (
                id TEXT PRIMARY KEY, outcome_key TEXT NOT NULL UNIQUE, device_id TEXT NOT NULL,
                epoch INTEGER NOT NULL DEFAULT 0, photo_epoch INTEGER NOT NULL DEFAULT 0,
                deleted INTEGER NOT NULL DEFAULT 0, claimed INTEGER NOT NULL DEFAULT 0, evening_id TEXT,
                published_at_ticks INTEGER, published_edition_id TEXT, document TEXT);
            CREATE TABLE IF NOT EXISTS dv10_sessions (
                secret_hash TEXT PRIMARY KEY, csrf_hash TEXT NOT NULL, csrf_protected TEXT NOT NULL, expires INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS dv10_links (
                secret_hash TEXT PRIMARY KEY, keepsake_id TEXT NOT NULL, kind TEXT NOT NULL,
                edition_id TEXT, expires INTEGER NOT NULL, consumed_session TEXT);
            CREATE TABLE IF NOT EXISTS dv10_grants (
                session_hash TEXT NOT NULL, keepsake_id TEXT NOT NULL, kind TEXT NOT NULL,
                edition_id TEXT NOT NULL DEFAULT '', link_hash TEXT,
                PRIMARY KEY(session_hash, keepsake_id, kind, edition_id));
            CREATE TABLE IF NOT EXISTS dv10_jobs (
                id TEXT PRIMARY KEY, keepsake_id TEXT NOT NULL, kind TEXT NOT NULL, state TEXT NOT NULL,
                epoch INTEGER NOT NULL, photo_epoch INTEGER NOT NULL, fence INTEGER NOT NULL DEFAULT 0,
                request_json TEXT NOT NULL, created INTEGER NOT NULL, error_code TEXT);
            CREATE TABLE IF NOT EXISTS dv10_idempotency (
                scope TEXT NOT NULL, operation_key TEXT NOT NULL, body_hash TEXT NOT NULL, result TEXT NOT NULL,
                PRIMARY KEY(scope, operation_key));
            CREATE TABLE IF NOT EXISTS dv10_account_fences (owner_id TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS dv10_customer_owners (
                keepsake_id TEXT PRIMARY KEY, owner_id TEXT NOT NULL, session_id TEXT NOT NULL,
                epoch INTEGER NOT NULL, linked_at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS dv10_deletions (
                id TEXT PRIMARY KEY, keepsake_id TEXT NOT NULL, scope TEXT NOT NULL,
                local_state TEXT NOT NULL, requested INTEGER NOT NULL, session_hash TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS dv10_job_queue ON dv10_jobs(state,created);
            CREATE TABLE IF NOT EXISTS dv10_attempts (
                id TEXT PRIMARY KEY, job_id TEXT NOT NULL, keepsake_id TEXT NOT NULL,
                stage TEXT NOT NULL, state TEXT NOT NULL, fence INTEGER NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS dv10_deletion_files (
                deletion_id TEXT NOT NULL, keepsake_id TEXT NOT NULL, asset_id TEXT NOT NULL,
                PRIMARY KEY(deletion_id,asset_id));
            CREATE TABLE IF NOT EXISTS dv10_deletion_directories (
                deletion_id TEXT NOT NULL, keepsake_id TEXT NOT NULL, upload_id TEXT NOT NULL,
                PRIMARY KEY(deletion_id,upload_id));
            CREATE TABLE IF NOT EXISTS dv10_recovery_state (store_id TEXT PRIMARY KEY, revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS dv10_revocations (
                id TEXT PRIMARY KEY, keepsake_id TEXT, session_hash TEXT, kind TEXT NOT NULL,
                revision INTEGER NOT NULL, created INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS dv10_admin_previews (
                id TEXT PRIMARY KEY, scope TEXT NOT NULL, targets_json TEXT NOT NULL,
                digest TEXT NOT NULL, total_bytes INTEGER NOT NULL, created INTEGER NOT NULL,
                confirmed_batch_id TEXT);
            CREATE TABLE IF NOT EXISTS dv10_admin_batches (
                id TEXT PRIMARY KEY, preview_id TEXT NOT NULL UNIQUE, scope TEXT NOT NULL,
                count INTEGER NOT NULL, total_bytes INTEGER NOT NULL,
                receipt_ids_json TEXT NOT NULL, created INTEGER NOT NULL, actor TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS dv10_derivatives (
                edition_id TEXT PRIMARY KEY, keepsake_id TEXT NOT NULL, state TEXT NOT NULL,
                retry_after INTEGER NOT NULL DEFAULT 0, attempts INTEGER NOT NULL DEFAULT 0);
            """);
        EnsureColumn(connection, "dv10_keepsakes", "claimed", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "dv10_keepsakes", "account_access_epoch", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "dv10_keepsakes", "evening_id", "TEXT");
        EnsureColumn(connection, "dv10_keepsakes", "published_at_ticks", "INTEGER");
        EnsureColumn(connection, "dv10_keepsakes", "published_edition_id", "TEXT");
        Execute(connection, null, "CREATE INDEX IF NOT EXISTS dv10_public_evening ON dv10_keepsakes(device_id,evening_id,deleted,published_at_ticks DESC,id DESC)");
        Execute(connection, null, "UPDATE dv10_keepsakes SET claimed=1 WHERE EXISTS "
            + "(SELECT 1 FROM dv10_grants g WHERE g.keepsake_id=dv10_keepsakes.id AND g.kind='owner') "
            + "OR EXISTS(SELECT 1 FROM dv10_links l WHERE l.keepsake_id=dv10_keepsakes.id AND l.kind='claim' AND l.consumed_session IS NOT NULL) "
            + "OR EXISTS(SELECT 1 FROM dv10_revocations r WHERE r.keepsake_id=dv10_keepsakes.id AND r.kind IN ('claim_attached','recovery_attached'))");
        using (var state = Command(connection, null, "SELECT count(*) FROM dv10_recovery_state"))
        {
            if ((long)state.ExecuteScalar()! == 0)
            {
                Execute(connection, null, "INSERT INTO dv10_recovery_state(store_id,revision) VALUES($id,0)", ("$id", NewId()));
            }
        }
        // A crash after dispatch cannot establish whether a paid attempt completed.
        Execute(connection, null, "UPDATE dv10_jobs SET state='awaiting_reconciliation', error_code='restart_after_dispatch' WHERE state='dispatched'");
        Execute(connection, null, "UPDATE dv10_jobs SET state='queued' WHERE state='leased'");
        ResumeDeletionCleanup();
    }

    private DateTimeOffset Now => clock.GetUtcNow();
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        // Identifiers are fixed feature-owned constants, never values from a request.
        using var command = Command(connection, null, "PRAGMA table_info(" + table + ")");
        using var reader = command.ExecuteReader();
        var found = false;
        while (reader.Read()) { found |= reader.GetString(1) == column; }
        reader.Close();
        if (!found) { Execute(connection, null, "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition); }
    }
    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql,
        params (string Key, object? Value)[] values)
    {
        using var command = Command(connection, transaction, sql, values);
        return command.ExecuteNonQuery();
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Key, object? Value)[] values)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (key, value) in values) { command.Parameters.AddWithValue(key, value ?? DBNull.Value); }
        return command;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string NewId() => Guid.NewGuid().ToString("D");
    private static bool ValidSecret(string? value) => value is { Length: >= 32 and <= 128 };
    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) { throw new KeepsakeException("invalid_idempotency_key"); }
    }
    private static string? Replay(SqliteConnection connection, SqliteTransaction? transaction,
        string scope, string key, string body)
    {
        ValidateKey(key);
        using var command = Command(connection, transaction,
            "SELECT body_hash,result FROM dv10_idempotency WHERE scope=$s AND operation_key=$k", ("$s", scope), ("$k", key));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { return null; }
        if (reader.GetString(0) != Hash(body)) { throw new KeepsakeException("idempotency_conflict", 409); }
        return reader.GetString(1);
    }
    private static void Remember(SqliteConnection connection, SqliteTransaction transaction,
        string scope, string key, string body, string result) => Execute(connection, transaction,
        "INSERT INTO dv10_idempotency(scope,operation_key,body_hash,result) VALUES($s,$k,$b,$r)",
        ("$s", scope), ("$k", key), ("$b", Hash(body)), ("$r", result));

    internal void RequireOutcomeNotDeleted(string deviceId, string outcomeId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT deleted FROM dv10_keepsakes WHERE outcome_key=$outcome",
                ("$outcome", Hash(deviceId + "\n" + outcomeId)));
            if (command.ExecuteScalar() is long deleted && deleted != 0)
            {
                throw new KeepsakeException("outcome_deleted", 410);
            }
        }
    }

    public IssuedKeepsake Issue(KeepsakeIssueSnapshot snapshot, KeepsakeCustomerOwner? customerOwner = null)
    {
        if (string.IsNullOrWhiteSpace(snapshot.OutcomeId) || snapshot.OutcomeId.Length > 128
            || string.IsNullOrWhiteSpace(snapshot.DeviceId) || snapshot.DeviceId.Length > 128
            || snapshot.Slot is < 1 or > 6 || string.IsNullOrWhiteSpace(snapshot.CatalogId)
            || snapshot.CatalogId.Length > 128 || snapshot.OfficialName.Length > 160
            || snapshot.CatalogFacts.Length > 4000 || snapshot.PhysicalEvidence.Length > 1000
            || (snapshot.SourceConversationId is not null && !Guid.TryParseExact(snapshot.SourceConversationId, "D", out _))
            || (snapshot.EveningId is not null && !Guid.TryParseExact(snapshot.EveningId, "D", out _)))
        {
            throw new KeepsakeException("invalid_outcome_snapshot");
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var outcomeKey = Hash(snapshot.DeviceId + "\n" + snapshot.OutcomeId);
            using var lookup = Command(connection, transaction, "SELECT id,document,deleted FROM dv10_keepsakes WHERE outcome_key=$o", ("$o", outcomeKey));
            using var reader = lookup.ExecuteReader();
            if (reader.Read())
            {
                var existingId = reader.GetString(0);
                if (reader.GetInt32(2) != 0) { throw new KeepsakeException("outcome_deleted", 410); }
                var document = JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!;
                if ((document.Snapshot with { SourceConversationId = snapshot.SourceConversationId }) != snapshot
                    || (document.Snapshot.SourceConversationId is not null && document.Snapshot.SourceConversationId != snapshot.SourceConversationId)) { throw new KeepsakeException("outcome_snapshot_conflict", 409); }
                reader.Close();
                BindCustomerOwner(connection, transaction, existingId, customerOwner);
                transaction.Commit();
                return new(existingId);
            }
            reader.Close();
            var id = NewId();
            Execute(connection, transaction,
                "INSERT INTO dv10_keepsakes(id,outcome_key,device_id,document,evening_id) VALUES($id,$o,$d,$j,$evening)",
                ("$id", id), ("$o", outcomeKey), ("$d", snapshot.DeviceId),
                ("$j", JsonSerializer.Serialize(new Document(snapshot), Json)), ("$evening", snapshot.EveningId));
            var issuedDocument = Load(connection, transaction, id);
            BindCustomerOwner(connection, transaction, id, customerOwner);
            TryAdmitAutomaticBase(connection, transaction, id, issuedDocument);
            transaction.Commit();
            return new(id);
        }
    }

    private sealed record Document(KeepsakeIssueSnapshot Snapshot)
    {
        public int Revision { get; set; }
        public string? ProphecyUnlockReason { get; set; }
        public int ConsentVersion { get; set; }
        public bool PortraitConsent { get; set; }
        public string? ConsentPolicy { get; set; }
        public string? CurrentBaseJob { get; set; }
        public string? CurrentPortraitJob { get; set; }
        public bool NeedsBaseGeneration { get; set; } = true;
        public HashSet<string> PublishedIdentityEvents { get; init; } = [];
        public List<WizardIdentity> WizardIdentities { get; init; } = [];
        public List<StoredProphecy> Prophecies { get; init; } = [];
        public List<KeepsakeEdition> Editions { get; init; } = [];
        public List<KeepsakePortrait> Portraits { get; init; } = [];
        public List<KeepsakeUpload> Uploads { get; init; } = [];
        public List<KeepsakeAsset> Assets { get; init; } = [];
    }
    private static Document Load(SqliteConnection connection, SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction,
            "SELECT document FROM dv10_keepsakes WHERE id=$id AND deleted=0", ("$id", id));
        var value = command.ExecuteScalar() as string;
        if (value is null) { throw new KeepsakeException("keepsake_not_found", 404); }
        return JsonSerializer.Deserialize<Document>(value, Json)!;
    }
    private static void Save(SqliteConnection connection, SqliteTransaction transaction, string id, Document document) =>
        Execute(connection, transaction, "UPDATE dv10_keepsakes SET document=$j WHERE id=$id AND deleted=0",
            ("$j", JsonSerializer.Serialize(document, Json)), ("$id", id));

    private string AssetPath(string id, string assetId, string? uploadId = null)
    {
        if (!Guid.TryParseExact(id, "D", out _) || !Guid.TryParseExact(assetId, "D", out _))
        {
            throw new KeepsakeException("invalid_asset_identifier");
        }
        if (uploadId is not null && !Guid.TryParseExact(uploadId, "D", out _))
        {
            throw new KeepsakeException("invalid_upload_identifier");
        }
        return uploadId is null ? Path.Combine(dataRoot, id, assetId + ".bin")
            : Path.Combine(dataRoot, id, "photos", uploadId, assetId + ".bin");
    }

    private void RecordRevocation(SqliteConnection connection, SqliteTransaction transaction,
        string? id, string? sessionHash, string kind)
    {
        Execute(connection, transaction, "UPDATE dv10_recovery_state SET revision=revision+1");
        Execute(connection, transaction,
            "INSERT INTO dv10_revocations(id,keepsake_id,session_hash,kind,revision,created) "
            + "SELECT $rid,$id,$session,$kind,revision,$time FROM dv10_recovery_state",
            ("$rid", NewId()), ("$id", id), ("$session", sessionHash), ("$kind", kind), ("$time", Now.ToUnixTimeSeconds()));
    }

    private KeepsakeAsset StoreAsset(string id, Document document, string kind, string mime, byte[] bytes,
        string? editionId = null, string? uploadId = null)
    {
        if (bytes.Length is 0 or > 32 * 1024 * 1024) { throw new KeepsakeException("asset_size_limit", 413); }
        var assetId = NewId();
        var path = AssetPath(id, assetId, uploadId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        AfterAssetFlush?.Invoke(kind);
        var asset = new KeepsakeAsset(assetId, kind, mime, Convert.ToHexString(SHA256.HashData(bytes)),
            bytes.LongLength, editionId, uploadId, Now);
        document.Assets.Add(asset);
        return asset;
    }
    internal Action<string>? AfterAssetFlush { get; set; }
}

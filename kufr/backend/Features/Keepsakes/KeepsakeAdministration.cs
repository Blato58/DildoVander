using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeAdminJob(string Id, string Kind, string State);
internal sealed record KeepsakeAdminItem(string Id, string OfficialName, string CatalogId, string CatalogVersion,
    string OutcomeId, int Epoch, int PhotoEpoch, int Revision, int AssetCount, long StoredBytes,
    IReadOnlyList<KeepsakeAdminJob> Jobs);
internal sealed record KeepsakeAdminTarget(string Id, int Revision, int Epoch, int PhotoEpoch,
    int AssetCount, long TotalBytes, string InventoryDigest);
internal sealed record KeepsakeAdminDeletionPreview(string Id, string Scope, int Count, long TotalBytes,
    IReadOnlyList<string> TargetIds, IReadOnlyList<KeepsakeAdminTarget> Targets, string Digest, DateTimeOffset CreatedAt);
internal sealed record KeepsakeAdminBatchReceipt(string Id, string PreviewId, string Scope, int Count,
    long TotalBytes, string State, IReadOnlyList<string> DeletionReceiptIds, DateTimeOffset CreatedAt,
    string Actor = "server_admin");

internal sealed partial class KeepsakeStore
{
    private const string AdminActor = "server_admin";

    // These primitives are not guest authority. Every HTTP caller must first pass
    // the existing server-admin boundary; no owner grant or bearer is minted here.
    public IReadOnlyList<KeepsakeAdminItem> AdminList(int limit = 100, string? afterId = null)
    {
        if (limit is < 1 or > 100 || (afterId is not null && !Guid.TryParseExact(afterId, "D", out _)))
        {
            throw new KeepsakeException("invalid_admin_list_request");
        }
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT id,epoch,photo_epoch FROM dv10_keepsakes WHERE deleted=0 AND ($after IS NULL OR id>$after) ORDER BY id LIMIT $limit",
                ("$after", afterId), ("$limit", limit));
            using var reader = command.ExecuteReader();
            var identities = new List<(string Id, int Epoch, int PhotoEpoch)>();
            while (reader.Read()) { identities.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2))); }
            reader.Close();
            return identities.Select(item =>
            {
                var document = Load(connection, null, item.Id);
                using var jobs = Command(connection, null,
                    "SELECT id,kind,state FROM dv10_jobs WHERE keepsake_id=$id ORDER BY created DESC,id DESC LIMIT 32", ("$id", item.Id));
                using var jobReader = jobs.ExecuteReader();
                var jobItems = new List<KeepsakeAdminJob>();
                while (jobReader.Read()) { jobItems.Add(new(jobReader.GetString(0), jobReader.GetString(1), jobReader.GetString(2))); }
                return new KeepsakeAdminItem(item.Id, document.Snapshot.OfficialName, document.Snapshot.CatalogId,
                    document.Snapshot.CatalogVersion, document.Snapshot.OutcomeId, item.Epoch, item.PhotoEpoch,
                    document.Revision, document.Assets.Count, Inventory(item.Id, KeepsakeDeleteScope.All).TotalBytes, jobItems);
            }).ToArray();
        }
    }

    public KeepsakeAdminDeletionPreview PreviewAdminDeletion(IReadOnlyList<string> ids, KeepsakeDeleteScope scope)
    {
        ValidateAdminScope(scope);
        if (ids is null || ids.Count is < 1 or > 100 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count
            || ids.Any(id => !Guid.TryParseExact(id, "D", out _)))
        {
            throw new KeepsakeException("invalid_admin_target_set");
        }
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var targets = ids.Order(StringComparer.Ordinal).Select(id => CaptureAdminTarget(connection, transaction, id, scope)).ToArray();
            var scopeName = AdminScopeName(scope);
            var preview = new KeepsakeAdminDeletionPreview(NewId(), scopeName, targets.Length,
                targets.Sum(target => target.TotalBytes), targets.Select(target => target.Id).ToArray(), targets,
                Hash(scopeName + ":" + JsonSerializer.Serialize(targets, Json)), Now);
            Execute(connection, transaction,
                "INSERT INTO dv10_admin_previews(id,scope,targets_json,digest,total_bytes,created) VALUES($id,$scope,$targets,$digest,$bytes,$created)",
                ("$id", preview.Id), ("$scope", preview.Scope), ("$targets", JsonSerializer.Serialize(targets, Json)),
                ("$digest", preview.Digest), ("$bytes", preview.TotalBytes), ("$created", preview.CreatedAt.ToUnixTimeSeconds()));
            transaction.Commit();
            return preview;
        }
    }

    public KeepsakeAdminBatchReceipt ConfirmAdminDeletion(string previewId, string explicitConfirm,
        string key, string? expectedDigest = null)
    {
        if (explicitConfirm != "delete" || string.IsNullOrWhiteSpace(expectedDigest))
        {
            throw new KeepsakeException("confirmation_required", 409);
        }
        string batchId;
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            const string operationScope = "admin-batch-delete";
            var body = previewId + ":" + explicitConfirm + ":" + expectedDigest;
            var replay = Replay(connection, transaction, operationScope, key, body);
            if (replay is not null) { batchId = replay; }
            else
            {
                using var command = Command(connection, transaction,
                    "SELECT scope,targets_json,digest,total_bytes,confirmed_batch_id FROM dv10_admin_previews WHERE id=$id", ("$id", previewId));
                using var reader = command.ExecuteReader();
                if (!reader.Read()) { throw new KeepsakeException("admin_preview_not_found", 404); }
                var scopeName = reader.GetString(0);
                var targets = JsonSerializer.Deserialize<KeepsakeAdminTarget[]>(reader.GetString(1), Json)!;
                var digest = reader.GetString(2);
                var bytes = reader.GetInt64(3);
                var confirmed = reader.IsDBNull(4) ? null : reader.GetString(4);
                reader.Close();
                if (digest != expectedDigest) { throw new KeepsakeException("admin_preview_changed", 409); }
                if (confirmed is not null) { batchId = confirmed; }
                else
                {
                    var scope = ParseAdminScope(scopeName);
                    // Validate the entire immutable set before the first revocation. A missing,
                    // changed, or newly ingested target rolls back the whole confirmation.
                    foreach (var target in targets)
                    {
                        KeepsakeAdminTarget current;
                        try { current = CaptureAdminTarget(connection, transaction, target.Id, scope); }
                        catch (KeepsakeException exception) when (exception.Code == "keepsake_not_found")
                        {
                            throw new KeepsakeException("admin_preview_changed", 409);
                        }
                        if (current != target) { throw new KeepsakeException("admin_preview_changed", 409); }
                    }
                    batchId = NewId();
                    var receipts = targets.Select(target => PrepareDeletion(connection, transaction, target.Id,
                        Load(connection, transaction, target.Id), scope, Hash(AdminActor))).ToArray();
                    Execute(connection, transaction,
                        "INSERT INTO dv10_admin_batches(id,preview_id,scope,count,total_bytes,receipt_ids_json,created,actor) "
                        + "VALUES($id,$preview,$scope,$count,$bytes,$receipts,$created,$actor)",
                        ("$id", batchId), ("$preview", previewId), ("$scope", scopeName), ("$count", targets.Length),
                        ("$bytes", bytes), ("$receipts", JsonSerializer.Serialize(receipts, Json)),
                        ("$created", Now.ToUnixTimeSeconds()), ("$actor", AdminActor));
                    Execute(connection, transaction, "UPDATE dv10_admin_previews SET confirmed_batch_id=$batch WHERE id=$id",
                        ("$batch", batchId), ("$id", previewId));
                }
                Remember(connection, transaction, operationScope, key, body, batchId);
            }
            transaction.Commit();
        }
        BeforeAdminCleanup?.Invoke();
        ResumeDeletionCleanup();
        return AdminGetBatchReceipt(batchId);
    }

    public KeepsakeAdminBatchReceipt AdminGetBatchReceipt(string batchId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT preview_id,scope,count,total_bytes,receipt_ids_json,created,actor FROM dv10_admin_batches WHERE id=$id", ("$id", batchId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { throw new KeepsakeException("admin_batch_not_found", 404); }
            var receiptIds = JsonSerializer.Deserialize<string[]>(reader.GetString(4), Json)!;
            var preview = reader.GetString(0);
            var scope = reader.GetString(1);
            var count = reader.GetInt32(2);
            var bytes = reader.GetInt64(3);
            var created = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5));
            var actor = reader.GetString(6);
            reader.Close();
            var complete = receiptIds.All(receiptId => AdminGetDeletionReceipt(receiptId).LocalState == "local_cleanup_complete");
            return new(batchId, preview, scope, count, bytes, complete ? "local_cleanup_complete" : "pending",
                receiptIds, created, actor);
        }
    }

    public KeepsakeDeletionReceipt AdminGetDeletionReceipt(string receiptId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null,
                "SELECT scope,local_state,requested FROM dv10_deletions WHERE id=$id", ("$id", receiptId));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { throw new KeepsakeException("deletion_receipt_not_found", 404); }
            return new(receiptId, reader.GetString(0), reader.GetString(1), "unknown_not_provider_confirmed",
                "pending_independent_backup_reconciliation", DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
        }
    }

    public void AdminCancel(string id, string jobId, string key)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Load(connection, transaction, id);
            var scope = "admin-cancel:" + id;
            if (Replay(connection, transaction, scope, key, jobId) is not null) { return; }
            using var job = Command(connection, transaction, "SELECT 1 FROM dv10_jobs WHERE id=$job AND keepsake_id=$id",
                ("$job", jobId), ("$id", id));
            if (job.ExecuteScalar() is null) { throw new KeepsakeException("job_not_found", 404); }
            Execute(connection, transaction,
                "UPDATE dv10_jobs SET state='cancelled' WHERE keepsake_id=$id AND id=$job AND state IN ('queued','leased','dispatched','awaiting_reconciliation')",
                ("$id", id), ("$job", jobId));
            RecordRevocation(connection, transaction, id, Hash(AdminActor), "admin_job_cancelled");
            Remember(connection, transaction, scope, key, jobId, jobId);
            transaction.Commit();
        }
    }

    public void AdminRevokeShares(string id, string key)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Load(connection, transaction, id);
            var scope = "admin-revoke-shares:" + id;
            if (Replay(connection, transaction, scope, key, id) is not null) { return; }
            Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id AND kind='share'", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_grants WHERE keepsake_id=$id AND kind='share'", ("$id", id));
            RecordRevocation(connection, transaction, id, Hash(AdminActor), "share_revoked");
            Remember(connection, transaction, scope, key, id, id);
            transaction.Commit();
        }
    }

    private KeepsakeAdminTarget CaptureAdminTarget(SqliteConnection connection, SqliteTransaction transaction,
        string id, KeepsakeDeleteScope scope)
    {
        var document = Load(connection, transaction, id);
        using var command = Command(connection, transaction, "SELECT epoch,photo_epoch FROM dv10_keepsakes WHERE id=$id", ("$id", id));
        using var reader = command.ExecuteReader();
        reader.Read();
        var inventory = Inventory(id, scope);
        var assetCount = document.Assets.Count(asset => scope == KeepsakeDeleteScope.All || asset.UploadId is not null);
        return new(id, document.Revision, reader.GetInt32(0), reader.GetInt32(1), assetCount, inventory.TotalBytes, inventory.Digest);
    }

    private (long TotalBytes, string Digest) Inventory(string id, KeepsakeDeleteScope scope)
    {
        if (!Guid.TryParseExact(id, "D", out _)) { throw new KeepsakeException("invalid_admin_target_set"); }
        var root = Path.Combine(dataRoot, id);
        if (scope == KeepsakeDeleteScope.PhotosAndPortraits) { root = Path.Combine(root, "photos"); }
        var entries = Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Select(path => new FileInfo(path)).OrderBy(file => file.FullName, StringComparer.Ordinal)
                .Select(file => new { Path = Path.GetRelativePath(root, file.FullName), file.Length, Modified = file.LastWriteTimeUtc.Ticks }).ToArray()
            : [];
        return (entries.Sum(entry => entry.Length), Hash(JsonSerializer.Serialize(entries, Json)));
    }

    private static void ValidateAdminScope(KeepsakeDeleteScope scope)
    {
        if (scope is not (KeepsakeDeleteScope.All or KeepsakeDeleteScope.PhotosAndPortraits))
        {
            throw new KeepsakeException("invalid_admin_delete_scope");
        }
    }
    private static string AdminScopeName(KeepsakeDeleteScope scope) => scope == KeepsakeDeleteScope.All ? "all" : "photos_and_portraits";
    private static KeepsakeDeleteScope ParseAdminScope(string scope) => scope switch
    {
        "all" => KeepsakeDeleteScope.All,
        "photos_and_portraits" => KeepsakeDeleteScope.PhotosAndPortraits,
        _ => throw new KeepsakeException("invalid_admin_delete_scope")
    };
    internal Action? BeforeAdminCleanup { get; set; }
}

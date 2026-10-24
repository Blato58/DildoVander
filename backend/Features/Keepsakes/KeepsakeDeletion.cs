using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    public KeepsakeDeletionReceipt Delete(string id, KeepsakeActor session, KeepsakeDeleteScope deleteScope,
        string key, int? expectedRevision = null)
    {
        string receiptId;
        lock (gate)
        {
            using var connection = Open();
            RequireSession(connection, null, session);
            var scope = "delete:" + id + ":" + Hash(session.StableIdentity);
            var body = deleteScope + ":" + expectedRevision;
            var replay = Replay(connection, null, scope, key, body);
            if (replay is not null) { return GetDeletionReceipt(replay, session); }
            RequireAccess(connection, null, id, session, KeepsakeScope.Delete);
            var document = Load(connection, null, id);
            if (expectedRevision is not null && expectedRevision != document.Revision)
            {
                throw new KeepsakeException("revision_changed", 409);
            }
            var validatedDocument = JsonSerializer.Serialize(document, Json);
            var validatedEpoch = ReadDeletionEpoch(connection, null, id);
            RevokeDeletionMemorySources(id, document, deleteScope);
            // No base-database transaction spans a memory-store call. Recheck
            // authority and the entire source after acquiring the write fence.
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Delete);
            document = Load(connection, transaction, id);
            if (JsonSerializer.Serialize(document, Json) != validatedDocument
                || ReadDeletionEpoch(connection, transaction, id) != validatedEpoch)
            {
                throw new KeepsakeException("revision_changed", 409);
            }
            receiptId = PrepareDeletion(connection, transaction, id, document, deleteScope, Hash(session.StableIdentity));
            Remember(connection, transaction, scope, key, body, receiptId);
            transaction.Commit();
        }
        ResumeDeletionCleanup();
        return GetDeletionReceipt(receiptId, session);
    }

    private static (int Epoch, int PhotoEpoch) ReadDeletionEpoch(SqliteConnection connection,
        SqliteTransaction? transaction, string id)
    {
        using var command = Command(connection, transaction,
            "SELECT epoch,photo_epoch FROM dv10_keepsakes WHERE id=$id AND deleted=0", ("$id", id));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new KeepsakeException("keepsake_not_found", 404); }
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    private void RevokeDeletionMemorySources(string id, Document document, KeepsakeDeleteScope scope)
    {
        if (scope != KeepsakeDeleteScope.All) { return; }
        BeforeDeletionMemoryRevocation?.Invoke();
        memoryStore?.RevokeShowSource(document.Snapshot.DeviceId, id);
        memoryStore?.RevokeShowSource(document.Snapshot.DeviceId, document.Snapshot.OutcomeId);
        if (document.Snapshot.SourceConversationId is { } conversationId)
        {
            memoryStore?.RevokeShowSource(document.Snapshot.DeviceId, conversationId);
        }
    }

    internal Action? BeforeDeletionMemoryRevocation { get; set; }

    private string PrepareDeletion(SqliteConnection connection, SqliteTransaction transaction, string id,
        Document document, KeepsakeDeleteScope deleteScope, string actorHash)
    {
        var receiptId = NewId();
        var all = deleteScope == KeepsakeDeleteScope.All;
        Execute(connection, transaction,
            "INSERT INTO dv10_deletions(id,keepsake_id,scope,local_state,requested,session_hash) VALUES($r,$id,$s,'pending',$t,$h)",
            ("$r", receiptId), ("$id", id), ("$s", all ? "all" : "photos_and_portraits"),
            ("$t", Now.ToUnixTimeSeconds()), ("$h", actorHash));
        var deletedAssets = document.Assets.Where(asset => all || asset.UploadId is not null
            || asset.Kind.StartsWith("portrait", StringComparison.Ordinal)).ToArray();
        foreach (var asset in deletedAssets)
        {
            Execute(connection, transaction,
                "INSERT INTO dv10_deletion_files(deletion_id,keepsake_id,asset_id) VALUES($r,$id,$a)",
                ("$r", receiptId), ("$id", id), ("$a", asset.Id));
        }
        if (!all)
        {
            var photosRoot = Path.Combine(dataRoot, id, "photos");
            var uploadDirectories = Directory.Exists(photosRoot) ? Directory.GetDirectories(photosRoot) : [];
            foreach (var directory in uploadDirectories)
            {
                var uploadId = Path.GetFileName(directory);
                if (!Guid.TryParseExact(uploadId, "D", out _)) { throw new KeepsakeException("photo_storage_integrity", 503); }
                Execute(connection, transaction,
                    "INSERT INTO dv10_deletion_directories(deletion_id,keepsake_id,upload_id) VALUES($r,$id,$upload)",
                    ("$r", receiptId), ("$id", id), ("$upload", uploadId));
            }
        }
        if (all)
        {
            // Scrub derived battle documents and candidate-session data in this same
            // transaction so deleting a source cannot leave a public replay or hidden copy.
            foreach (var identity in document.WizardIdentities)
                Kufr.Backend.Features.Show.DuelStore.PurgeIdentity(connection, transaction, identity.IdentityId);
            if (document.Snapshot.GeneratedWand is { } wand)
            {
                using var tables = Command(connection, transaction,
                    "SELECT 1 FROM sqlite_master WHERE type='table' AND name='wand_generation_sessions'");
                if (tables.ExecuteScalar() is not null)
                    Execute(connection, transaction, """
                        UPDATE wand_generation_sessions SET state='revoked',request_json=NULL,candidates_json=NULL,
                            selected_json=NULL,lease_token=NULL,lease_until=0,error_code=NULL
                        WHERE json_extract(selected_json,'$.wandId')=$wand
                        """, ("$wand", wand.WandId));
            }
            // Retain only technical tombstone identity; no catalog or visitor content.
            Execute(connection, transaction,
                "UPDATE dv10_keepsakes SET deleted=1,document=NULL,device_id='',epoch=epoch+1,photo_epoch=photo_epoch+1 WHERE id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_grants WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction,
                "UPDATE dv10_jobs SET state='deleted',request_json='{}',error_code=NULL WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_derivatives WHERE keepsake_id=$id", ("$id", id));
            Execute(connection, transaction, "DELETE FROM dv10_idempotency WHERE instr(scope,$id)>0 OR result=$id", ("$id", id));
        }
        else
        {
            var assetIds = deletedAssets.Select(asset => asset.Id).ToHashSet();
            document.Assets.RemoveAll(asset => assetIds.Contains(asset.Id));
            document.Portraits.Clear();
            document.Uploads.Clear();
            document.PortraitConsent = false;
            document.ConsentPolicy = null;
            document.ConsentVersion++;
            document.Revision++;
            document.CurrentPortraitJob = null;
            Save(connection, transaction, id, document);
            Execute(connection, transaction, "UPDATE dv10_keepsakes SET photo_epoch=photo_epoch+1 WHERE id=$id", ("$id", id));
            Execute(connection, transaction,
                "UPDATE dv10_jobs SET state='deleted',request_json='{}',error_code=NULL WHERE keepsake_id=$id AND kind='portrait'", ("$id", id));
            Execute(connection, transaction,
                "DELETE FROM dv10_idempotency WHERE instr(scope,$id)>0 AND (scope LIKE 'photo:%' OR scope LIKE 'enqueue:%:portrait' OR scope LIKE 'consent:%' OR scope LIKE 'export:%')", ("$id", id));
        }
        DeleteCompletionUpdates(connection, transaction, id, portraitsOnly: !all);
        if (!all) { RecordCompletionUpdates(connection, transaction, id); }
        RecordRevocation(connection, transaction, id, actorHash, all ? "deleted_all" : "deleted_photos_and_portraits");
        if (all)
        {
            Execute(connection, transaction, "UPDATE dv10_attempts SET state='deleted' WHERE keepsake_id=$id", ("$id", id));
        }
        return receiptId;
    }

    public KeepsakeDeletionReceipt GetDeletionReceipt(string receiptId, KeepsakeActor session)
    {
        lock (gate)
        {
            using var connection = Open();
            RequireSession(connection, null, session);
            using var command = Command(connection, null,
                "SELECT scope,local_state,requested FROM dv10_deletions WHERE id=$id AND session_hash=$s",
                ("$id", receiptId), ("$s", Hash(session.StableIdentity)));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { throw new KeepsakeException("deletion_receipt_not_found", 404); }
            return new(receiptId, reader.GetString(0), reader.GetString(1), "unknown_not_provider_confirmed",
                "pending_independent_backup_reconciliation", DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)));
        }
    }

    public void ResumeDeletionCleanup()
    {
        lock (gate)
        {
            using var connection = Open();
            using var pending = Command(connection, null,
                "SELECT id,keepsake_id,scope FROM dv10_deletions WHERE local_state='pending'");
            using var reader = pending.ExecuteReader();
            var deletions = new List<(string Id, string KeepsakeId, string Scope)>();
            while (reader.Read()) { deletions.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2))); }
            reader.Close();
            foreach (var deletion in deletions)
            {
                try
                {
                    if (deletion.Scope == "all")
                    {
                        // Generated IDs are validated before recursive cleanup, including orphaned staged files.
                        if (!Guid.TryParseExact(deletion.KeepsakeId, "D", out _)) { continue; }
                        var directory = Path.GetFullPath(Path.Combine(dataRoot, deletion.KeepsakeId));
                        if (!directory.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)) { continue; }
                        if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
                    }
                    else
                    {
                        using var directories = Command(connection, null,
                            "SELECT upload_id FROM dv10_deletion_directories WHERE deletion_id=$r", ("$r", deletion.Id));
                        using var directoryReader = directories.ExecuteReader();
                        while (directoryReader.Read())
                        {
                            var uploadId = directoryReader.GetString(0);
                            if (!Guid.TryParseExact(deletion.KeepsakeId, "D", out _) || !Guid.TryParseExact(uploadId, "D", out _))
                            {
                                throw new IOException("Invalid deletion namespace.");
                            }
                            var directory = Path.GetFullPath(Path.Combine(dataRoot, deletion.KeepsakeId, "photos", uploadId));
                            if (!directory.StartsWith(dataRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                            {
                                throw new IOException("Deletion namespace escapes storage.");
                            }
                            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
                        }
                    }
                    using var transaction = connection.BeginTransaction();
                    Execute(connection, transaction, "DELETE FROM dv10_deletion_files WHERE deletion_id=$r", ("$r", deletion.Id));
                    Execute(connection, transaction, "DELETE FROM dv10_deletion_directories WHERE deletion_id=$r", ("$r", deletion.Id));
                    Execute(connection, transaction, "UPDATE dv10_deletions SET local_state='local_cleanup_complete' WHERE id=$r", ("$r", deletion.Id));
                    transaction.Commit();
                }
                catch (IOException) { /* Durable pending receipt is resumed at startup or explicit operator retry. */ }
                catch (UnauthorizedAccessException) { /* Do not falsely report physical local cleanup. */ }
            }
        }
    }
}

using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Conversations;

// The authoritative records, permission versions, tombstones and durable jobs
// share EveningMemoryStore's database/lock. Cloud search never overrides them.
internal sealed partial class EveningMemoryStore
{
    private void MigrateHybridMemory(SqliteConnection c, SqliteTransaction tx)
    {
        using var version = Command(c, tx, "PRAGMA user_version");
        var originalVersion = Convert.ToInt32(version.ExecuteScalar());
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS memory_meta (id INTEGER PRIMARY KEY CHECK(id=1), epoch INTEGER NOT NULL);
            INSERT OR IGNORE INTO memory_meta(id,epoch) VALUES(1,0);
            CREATE TABLE IF NOT EXISTS memory_guests (
                device_id TEXT NOT NULL, conversation_id TEXT NOT NULL, guest_id TEXT NOT NULL,
                identity_verified INTEGER NOT NULL, identity_source TEXT NOT NULL,
                long_term_allowed INTEGER NOT NULL, cloud_export_allowed INTEGER NOT NULL, version INTEGER NOT NULL,
                app_namespace TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(device_id,conversation_id));
            CREATE TABLE IF NOT EXISTS memory_records (
                memory_id TEXT NOT NULL, revision INTEGER NOT NULL, scope TEXT NOT NULL,
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                guest_id TEXT, personality_id TEXT, kind TEXT NOT NULL, text TEXT NOT NULL,
                source_reference TEXT NOT NULL, created_at_ms INTEGER NOT NULL, expires_at_ms INTEGER NOT NULL,
                is_active INTEGER NOT NULL, supersedes TEXT, use_allowed INTEGER NOT NULL,
                export_allowed INTEGER NOT NULL, permission_version INTEGER NOT NULL, content_bytes INTEGER NOT NULL,
                app_namespace TEXT NOT NULL DEFAULT '',
                PRIMARY KEY(memory_id,revision));
            CREATE INDEX IF NOT EXISTS memory_scope ON memory_records(device_id,scope,conversation_id,guest_id,is_active);
            CREATE TABLE IF NOT EXISTS memory_indexes (
                memory_id TEXT NOT NULL, revision INTEGER NOT NULL, store_id TEXT NOT NULL,
                filename TEXT NOT NULL UNIQUE, file_id TEXT, state TEXT NOT NULL, owned INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY(memory_id,revision,store_id));
            CREATE INDEX IF NOT EXISTS memory_file ON memory_indexes(store_id,file_id);
            CREATE TABLE IF NOT EXISTS memory_outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT, memory_id TEXT NOT NULL, revision INTEGER NOT NULL,
                store_id TEXT NOT NULL, action TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'pending',
                attempts INTEGER NOT NULL DEFAULT 0, next_at_ms INTEGER NOT NULL, lease_until_ms INTEGER,
                error_code TEXT, deletion_requested_at_ms INTEGER, deletion_confirmed_at_ms INTEGER,
                UNIQUE(memory_id,revision,store_id,action));
            CREATE TABLE IF NOT EXISTS memory_usage (
                day TEXT NOT NULL, kind TEXT NOT NULL, amount INTEGER NOT NULL, PRIMARY KEY(day,kind));
            CREATE TABLE IF NOT EXISTS memory_deleted_conversations (
                app_namespace TEXT NOT NULL, device_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                PRIMARY KEY(app_namespace,device_id,conversation_id));
            CREATE TABLE IF NOT EXISTS memory_deleted_evenings (
                app_namespace TEXT NOT NULL, device_id TEXT NOT NULL, evening_id TEXT NOT NULL,
                PRIMARY KEY(app_namespace,device_id,evening_id));
            CREATE TABLE IF NOT EXISTS memory_storage_failures (
                code TEXT PRIMARY KEY, rejected_writes INTEGER NOT NULL, last_at_ms INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS memory_conversation_generations (
                app_namespace TEXT NOT NULL, device_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                deletion_epoch INTEGER NOT NULL DEFAULT 0, fact_epoch INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(app_namespace,device_id,conversation_id));
            CREATE TABLE IF NOT EXISTS memory_context_scopes (
                app_namespace TEXT NOT NULL, device_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                evening_id TEXT NOT NULL, guest_id TEXT,
                PRIMARY KEY(app_namespace,device_id,conversation_id));
            CREATE TABLE IF NOT EXISTS live_transcripts (
                conversation_id TEXT NOT NULL, session_generation TEXT NOT NULL, reference TEXT NOT NULL,
                device_id TEXT NOT NULL, evening_id TEXT NOT NULL, speaker TEXT NOT NULL,
                start_ms INTEGER NOT NULL, end_ms INTEGER NOT NULL, event_id TEXT,
                text TEXT NOT NULL, expires_at_ms INTEGER NOT NULL,
                PRIMARY KEY(conversation_id,session_generation,reference));
            PRAGMA user_version=3;
            """;
        cmd.Parameters.AddWithValue("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
        using var columns = Command(c, tx, "SELECT name FROM pragma_table_info('memory_records') WHERE name='app_namespace'");
        if (columns.ExecuteScalar() is null)
        {
            using var alter = Command(c, tx, "ALTER TABLE memory_records ADD COLUMN app_namespace TEXT NOT NULL DEFAULT ''");
            alter.ExecuteNonQuery();
        }
        if (originalVersion < 2)
        {
            using var legacy = Command(c, tx, """
                INSERT OR IGNORE INTO memory_records
                SELECT 'legacy:' || sequence,1,'Conversation',device_id,evening_id,conversation_id,
                    NULL,NULL,'LegacyUnverified',visitor_text || char(10) || reply,'legacy_exchange:' || sequence,
                    $now,$expires,1,NULL,0,0,0,content_bytes,'' FROM exchanges;
                """, ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()),
                ("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
            legacy.ExecuteNonQuery();
        }
        using var guestColumns = Command(c, tx, "SELECT name FROM pragma_table_info('memory_guests') WHERE name='app_namespace'");
        if (guestColumns.ExecuteScalar() is null)
        {
            using var alter = Command(c, tx, "ALTER TABLE memory_guests ADD COLUMN app_namespace TEXT NOT NULL DEFAULT ''"); alter.ExecuteNonQuery();
        }
        using var generationColumn = Command(c, tx, "SELECT name FROM pragma_table_info('memory_meta') WHERE name='deletion_epoch'");
        if (generationColumn.ExecuteScalar() is null)
        {
            using var alter = Command(c, tx, "ALTER TABLE memory_meta ADD COLUMN deletion_epoch INTEGER NOT NULL DEFAULT 0");
            alter.ExecuteNonQuery();
        }
        // Retention timestamps from older releases are archive metadata, never a
        // deletion schedule. Previously erased content cannot be reconstructed.
        using var retain = Command(c, tx, "UPDATE memory_records SET expires_at_ms=$forever WHERE is_active=1; UPDATE live_transcripts SET expires_at_ms=$forever;",
            ("$forever", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()));
        retain.ExecuteNonQuery();
        foreach (var column in new[] { "app_namespace", "guest_id", "personality_id" })
        {
            using var present = Command(c, tx, "SELECT name FROM pragma_table_info('live_transcripts') WHERE name=$column", ("$column", column));
            if (present.ExecuteScalar() is null)
            {
                using var add = Command(c, tx, "ALTER TABLE live_transcripts ADD COLUMN " + column + " TEXT");
                add.ExecuteNonQuery();
            }
        }
    }

    internal long MemoryEpoch
    {
        get { lock (gate) { using var c = Open(); return Epoch(c, null); } }
    }
    internal long MemoryDeletionEpoch
    {
        get
        {
            lock (gate)
            {
                using var c = Open();
                using var command = Command(c, null, "SELECT deletion_epoch FROM memory_meta WHERE id=1");
                return (long)command.ExecuteScalar()!;
            }
        }
    }
    internal long MemoryDeletionEpochFor(string device, string conversation)
    {
        lock (gate)
        {
            using var c = Open();
            return ConversationEpoch(c, null, device, conversation, facts: false);
        }
    }
    internal long MemoryFactEpochFor(MemoryAccess access)
    {
        lock (gate)
        {
            using var c = Open();
            return ConversationEpoch(c, null, access.DeviceId, access.ConversationId, facts: true);
        }
    }
    internal void RegisterMemoryContext(MemoryAccess access)
    {
        lock (gate)
        {
            using var c = Open();
            using var command = Command(c, null, """
                INSERT INTO memory_context_scopes VALUES($n,$d,$c,$e,$g)
                ON CONFLICT(app_namespace,device_id,conversation_id) DO UPDATE SET
                    evening_id=excluded.evening_id,guest_id=excluded.guest_id;
                """, ("$n", memoryOptions.Namespace), ("$d", access.DeviceId), ("$c", access.ConversationId),
                ("$e", access.EveningId), ("$g", access.Guest?.GuestId));
            command.ExecuteNonQuery();
        }
    }
    private long ConversationEpoch(SqliteConnection c, SqliteTransaction? tx, string device, string conversation, bool facts)
    {
        var column = facts ? "fact_epoch" : "deletion_epoch";
        using var command = Command(c, tx, "SELECT " + column + " FROM memory_conversation_generations WHERE app_namespace=$n AND device_id=$d AND conversation_id=$c",
            ("$n", memoryOptions.Namespace), ("$d", device), ("$c", conversation));
        return command.ExecuteScalar() is long value ? value : 0;
    }
    private void AdvanceConversationEpoch(SqliteConnection c, SqliteTransaction tx, string device, string conversation, bool deletion)
    {
        using var command = Command(c, tx, """
            INSERT INTO memory_conversation_generations VALUES($n,$d,$c,$deleted,1)
            ON CONFLICT(app_namespace,device_id,conversation_id) DO UPDATE SET
                deletion_epoch=deletion_epoch+$deleted,fact_epoch=fact_epoch+1;
            """, ("$n", memoryOptions.Namespace), ("$d", device), ("$c", conversation), ("$deleted", deletion ? 1 : 0));
        command.ExecuteNonQuery();
    }
    private static long Epoch(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = Command(c, tx, "SELECT epoch FROM memory_meta WHERE id=1");
        return (long)cmd.ExecuteScalar()!;
    }
    private static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql,
        params (string, object?)[] parameters)
    {
        var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    internal GuestMemoryPermission? GuestPermission(string device, string conversation)
    {
        lock (gate) { using var c = Open(); return ReadGuest(c, null, device, conversation); }
    }
    private GuestMemoryPermission? ReadGuest(SqliteConnection c, SqliteTransaction? tx, string device, string conversation)
    {
        using var cmd = Command(c, tx, "SELECT guest_id,identity_verified,long_term_allowed,cloud_export_allowed,version FROM memory_guests WHERE device_id=$d AND conversation_id=$c AND app_namespace=$n",
            ("$d", device), ("$c", conversation), ("$n", memoryOptions.Namespace));
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(device, conversation, r.GetString(0), r.GetBoolean(1), r.GetBoolean(2), r.GetBoolean(3), r.GetInt64(4)) : null;
    }

    // Called only through the admin identity/consent route, never transcript or
    // model output. A supplied name or voice is not an identity verification.
    internal GuestMemoryPermission? SetGuestPermission(string device, string conversation, string guest,
        string identitySource, bool verified, bool longTerm, bool export, long expectedVersion)
    {
        if (guest.Length is < 1 or > 80
            || guest.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            || string.IsNullOrWhiteSpace(identitySource) || identitySource.Length > 128 || identitySource.Any(char.IsControl)
            || (!verified && (longTerm || export)) || export && !longTerm) throw new ArgumentException("Invalid guest permission.");
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            var old = ReadGuest(c, tx, device, conversation);
            if ((old?.Version ?? 0) != expectedVersion) return null;
            if (old is not null && old.GuestId == guest && old.IdentityVerified == verified
                && old.LongTermAllowed == longTerm && old.CloudExportAllowed == export)
            {
                return old;
            }
            // Rebinding changes eligibility, not archived content. Revoke cloud
            // copies belonging to this origin without erasing their local facts.
            if (old is not null && (old.GuestId != guest || !verified || !longTerm || !export))
            {
                using var ids = Command(c, tx, "SELECT memory_id,revision FROM memory_records WHERE device_id=$d AND conversation_id=$c AND app_namespace=$n AND scope='Personal' AND is_active=1",
                    ("$d", device), ("$c", conversation), ("$n", memoryOptions.Namespace));
                var revoked = new List<(string Id, long Revision)>();
                using (var reader = ids.ExecuteReader())
                {
                    while (reader.Read()) { revoked.Add((reader.GetString(0), reader.GetInt64(1))); }
                }
                foreach (var record in revoked) { QueueIndexRemoval(c, tx, record.Id); }
                using var revoke = Command(c, tx, "UPDATE memory_records SET export_allowed=0 WHERE device_id=$d AND conversation_id=$c AND app_namespace=$n AND scope='Personal'",
                    ("$d", device), ("$c", conversation), ("$n", memoryOptions.Namespace));
                revoke.ExecuteNonQuery();
            }
            using var cmd = Command(c, tx, """
                INSERT INTO memory_guests VALUES($d,$c,$g,$v,$source,$long,$export,$version,$namespace)
                ON CONFLICT(device_id,conversation_id) DO UPDATE SET guest_id=excluded.guest_id,
                identity_verified=excluded.identity_verified,identity_source=excluded.identity_source,
                long_term_allowed=excluded.long_term_allowed,cloud_export_allowed=excluded.cloud_export_allowed,version=excluded.version,app_namespace=excluded.app_namespace;
                UPDATE memory_meta SET epoch=epoch+1,deletion_epoch=deletion_epoch+1 WHERE id=1;
                """, ("$d", device), ("$c", conversation), ("$g", guest), ("$v", verified), ("$source", identitySource),
                ("$long", longTerm), ("$export", export), ("$version", expectedVersion + 1), ("$namespace", memoryOptions.Namespace));
            cmd.ExecuteNonQuery();
            AdvanceConversationEpoch(c, tx, device, conversation, deletion: true);
            tx.Commit();
            return new(device, conversation, guest, verified, longTerm, export, expectedVersion + 1);
        }
    }

    internal MemoryRecord? WriteMemory(MemoryAccess access, MemoryWrite write)
    {
        if (!memoryOptions.Enabled) return null;
        if (!Guid.TryParseExact(write.MemoryId, "D", out _) || string.IsNullOrWhiteSpace(write.Text)
            || Encoding.UTF8.GetByteCount(write.Text) > memoryOptions.MaxRecordBytes
            || write.SourceReference.Length is < 1 or > 256
            || write.Scope == MemoryScope.EveningMotif && (write.Kind != MemoryKind.TopicUsed || !AnonymousMotifs.IsAllowed(write.Text))
            || write.Scope == MemoryScope.Personal && write.Kind is not (MemoryKind.UserReportedFact or MemoryKind.ConfirmedSelection))
            throw new ArgumentException("Invalid memory record.");
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            if (write.ExpectedEpoch is { } expectedEpoch && Epoch(c, tx) != expectedEpoch) return null;
            if (write.ExpectedFactEpoch is { } factEpoch
                && ConversationEpoch(c, tx, access.DeviceId, access.ConversationId, facts: true) != factEpoch) { return null; }
            if (!CanAppendConversation(c, tx, access, write.ExpectedDeletionEpoch)) return null;
            using var exists = Command(c, tx, "SELECT 1 FROM evening_catalog WHERE device_id=$d AND evening_id=$e", ("$d", access.DeviceId), ("$e", access.EveningId));
            if (exists.ExecuteScalar() is null) return null;
            var old = Latest(c, tx, write.MemoryId);
            if ((old?.Revision ?? 0) != write.ExpectedRevision || old is { IsActive: false }
                || old is not null && (old.DeviceId != access.DeviceId || old.ConversationId != access.ConversationId || old.Scope != write.Scope
                    || old.AppNamespace != memoryOptions.Namespace)) return null;
            var guest = ReadGuest(c, tx, access.DeviceId, access.ConversationId);
            if (write.Scope == MemoryScope.Personal && (!memoryOptions.LongTermPersonalEnabled
                || guest is not { IdentityVerified: true, LongTermAllowed: true }
                || access.Guest?.GuestId != guest.GuestId || access.Guest.Version != guest.Version)) return null;
            using var capacity = Command(c, tx, "SELECT (SELECT COALESCE(SUM(content_bytes),0) FROM memory_records)+(SELECT COALESCE(SUM(length(CAST(text AS BLOB))),0) FROM live_transcripts)");
            var bytes = Encoding.UTF8.GetByteCount(write.Text);
            if ((long)capacity.ExecuteScalar()! + bytes > memoryOptions.MaxStoredBytes)
            {
                RecordCapacityFailure(c, tx);
                tx.Commit();
                return null;
            }
            if (!Spend(c, tx, "write", 1, memoryOptions.MaxWritesPerDay)) { return null; }
            // Local persistence remains available even when derived indexing is
            // backlogged. Jobs are durable; workers bound network concurrency.
            if (old is not null) Invalidate(c, tx, old.MemoryId, eraseContent: false);
            var now = memoryClock.GetUtcNow();
            var expires = DateTimeOffset.MaxValue;
            var exportAllowed = write.Scope == MemoryScope.EveningMotif || write.Scope == MemoryScope.Personal && guest!.CloudExportAllowed;
            var record = new MemoryRecord(write.MemoryId, write.Scope, access.DeviceId, access.EveningId, access.ConversationId,
                write.Scope == MemoryScope.Personal ? guest!.GuestId : null, access.PersonalityId, write.Kind, write.Text,
                write.SourceReference, now, expires, write.ExpectedRevision + 1, true,
                old is null ? null : old.MemoryId + ":" + old.Revision, true, exportAllowed, guest?.Version ?? 0, memoryOptions.Namespace);
            using var insert = Command(c, tx, """
                INSERT INTO memory_records VALUES($id,$rev,$scope,$d,$e,$c,$g,$p,$kind,$text,$source,$now,$expires,1,$sup,1,$export,$permission,$bytes,$namespace);
                UPDATE memory_meta SET epoch=epoch+1 WHERE id=1;
                """, ("$id", record.MemoryId), ("$rev", record.Revision), ("$scope", record.Scope.ToString()),
                ("$d", record.DeviceId), ("$e", record.EveningId), ("$c", record.ConversationId), ("$g", record.GuestId),
                ("$p", record.PersonalityId), ("$kind", record.Kind.ToString()), ("$text", record.Text), ("$source", record.SourceReference),
                ("$now", now.ToUnixTimeMilliseconds()), ("$expires", expires.ToUnixTimeMilliseconds()), ("$sup", record.Supersedes),
                ("$export", record.ExportAllowed), ("$permission", record.PermissionVersion), ("$bytes", bytes), ("$namespace", memoryOptions.Namespace));
            insert.ExecuteNonQuery();
            if (record.ExportAllowed && memoryOptions.CloudIndexingEnabled && record.Scope != MemoryScope.Conversation)
                Queue(c, tx, record.MemoryId, record.Revision, memoryOptions.StoreFor(record.Scope), "index");
            tx.Commit(); return record;
        }
    }

    internal bool DeleteMemory(string device, string id, long expectedRevision)
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            var record = Latest(c, tx, id);
            if (record is null || record.DeviceId != device || record.Revision != expectedRevision || record.AppNamespace != memoryOptions.Namespace) return false;
            Invalidate(c, tx, id);
            tx.Commit(); return true;
        }
    }

    internal bool ForgetOwnPersonalMemory(MemoryAccess access, long? expectedFactEpoch = null, long? expectedDeletionEpoch = null)
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            if (expectedFactEpoch is { } factEpoch
                && ConversationEpoch(c, tx, access.DeviceId, access.ConversationId, facts: true) != factEpoch) { return false; }
            if (!CanAppendConversation(c, tx, access, expectedDeletionEpoch)) { return false; }
            var permission = ReadGuest(c, tx, access.DeviceId, access.ConversationId);
            if (permission is not { IdentityVerified: true } || access.Guest?.GuestId != permission.GuestId
                || access.Guest.Version != permission.Version) return false;
            using var cmd = Command(c, tx, "SELECT DISTINCT memory_id FROM memory_records WHERE device_id=$d AND guest_id=$g AND app_namespace=$n AND scope='Personal' AND is_active=1",
                ("$d", access.DeviceId), ("$g", permission.GuestId), ("$n", memoryOptions.Namespace));
            var ids = new List<string>(); using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
            foreach (var id in ids) Invalidate(c, tx, id);
            AdvanceConversationEpoch(c, tx, access.DeviceId, access.ConversationId, deletion: true);
            using var epoch = Command(c, tx, "UPDATE memory_meta SET epoch=epoch+1,deletion_epoch=deletion_epoch+1 WHERE id=1"); epoch.ExecuteNonQuery();
            tx.Commit(); return true;
        }
    }
    internal bool DeleteScopedMemory(string device, string scope, string subject, long expectedEpoch)
        => DeleteScopedMemory(device, scope, subject, expectedEpoch, out _);

    internal bool DeleteScopedMemory(string device, string scope, string subject, long expectedEpoch,
        out IReadOnlyList<string> affectedConversations)
    {
        affectedConversations = [];
        if (scope is not ("conversation" or "guest" or "evening") || string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("Invalid memory deletion scope.");
        }
        lock (gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            if (Epoch(c, tx) != expectedEpoch) { return false; }
            var predicate = scope switch
            {
                "conversation" => "conversation_id=$subject",
                "guest" => "guest_id=$subject",
                "evening" => "evening_id=$subject",
                _ => throw new ArgumentException("Invalid memory deletion scope.")
            };
            using var records = Command(c, tx,
                "SELECT DISTINCT memory_id,conversation_id FROM memory_records WHERE device_id=$d AND app_namespace=$n AND " + predicate,
                ("$d", device), ("$n", memoryOptions.Namespace), ("$subject", subject));
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var conversations = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = records.ExecuteReader())
            {
                while (reader.Read())
                {
                    ids.Add(reader.GetString(0));
                    conversations.Add(reader.GetString(1));
                }
            }
            using (var contexts = Command(c, tx,
                "SELECT conversation_id FROM memory_context_scopes WHERE device_id=$d AND app_namespace=$n AND " + predicate,
                ("$d", device), ("$n", memoryOptions.Namespace), ("$subject", subject)))
            using (var reader = contexts.ExecuteReader())
            {
                while (reader.Read()) { conversations.Add(reader.GetString(0)); }
            }
            if (scope == "conversation") { conversations.Add(subject); }
            if (scope == "guest")
            {
                using var bindings = Command(c, tx, "SELECT conversation_id FROM memory_guests WHERE device_id=$d AND app_namespace=$n AND guest_id=$subject",
                    ("$d", device), ("$n", memoryOptions.Namespace), ("$subject", subject));
                using var reader = bindings.ExecuteReader();
                while (reader.Read()) { conversations.Add(reader.GetString(0)); }
            }
            if (scope == "evening")
            {
                using var tombstone = Command(c, tx, "INSERT OR IGNORE INTO memory_deleted_evenings VALUES($n,$d,$subject)",
                    ("$d", device), ("$subject", subject), ("$n", memoryOptions.Namespace));
                tombstone.ExecuteNonQuery();
                using var sessions = Command(c, tx, "SELECT conversation_id FROM live_transcripts WHERE device_id=$d AND evening_id=$subject "
                    + "UNION SELECT conversation_id FROM exchanges WHERE device_id=$d AND evening_id=$subject "
                    + "UNION SELECT conversation_id FROM show_visits WHERE device_id=$d AND evening_id=$subject "
                    + "UNION SELECT conversation_id FROM show_memory_events WHERE device_id=$d AND evening_id=$subject AND conversation_id IS NOT NULL",
                    ("$d", device), ("$subject", subject));
                using var reader = sessions.ExecuteReader();
                while (reader.Read()) { conversations.Add(reader.GetString(0)); }
            }
            // A guest/session deletion also removes the associated raw archive;
            // retaining it would permit re-derivation of the deleted facts.
            foreach (var conversation in conversations)
            {
                using var related = Command(c, tx, "SELECT DISTINCT memory_id FROM memory_records WHERE device_id=$d AND app_namespace=$n AND conversation_id=$c",
                    ("$d", device), ("$n", memoryOptions.Namespace), ("$c", conversation));
                using (var reader = related.ExecuteReader())
                {
                    while (reader.Read()) { ids.Add(reader.GetString(0)); }
                }
                using var remove = Command(c, tx, """
                    INSERT OR IGNORE INTO memory_deleted_conversations VALUES($n,$d,$c);
                    DELETE FROM live_transcripts WHERE device_id=$d AND conversation_id=$c;
                    DELETE FROM exchanges WHERE device_id=$d AND conversation_id=$c;
                    """, ("$d", device), ("$n", memoryOptions.Namespace), ("$c", conversation));
                remove.ExecuteNonQuery();
                AdvanceConversationEpoch(c, tx, device, conversation, deletion: true);
            }
            var showSources = new HashSet<string>(conversations, StringComparer.Ordinal);
            if (scope == "evening")
            {
                showSources.Add(subject);
                using var showEvents = Command(c, tx,
                    "SELECT DISTINCT source_id FROM show_memory_events WHERE device_id=$d AND evening_id=$subject",
                    ("$d", device), ("$subject", subject));
                using var reader = showEvents.ExecuteReader();
                while (reader.Read()) { showSources.Add(reader.GetString(0)); }
            }
            RevokeShowSources(c, tx, device, showSources);
            foreach (var id in ids) { Invalidate(c, tx, id); }
            using var fence = Command(c, tx, "UPDATE memory_meta SET epoch=epoch+1,deletion_epoch=deletion_epoch+1 WHERE id=1");
            fence.ExecuteNonQuery();
            tx.Commit();
            affectedConversations = conversations.Order(StringComparer.Ordinal).ToArray();
            return true;
        }
    }
    private void QueueIndexRemoval(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var maps = Command(c, tx, "SELECT revision,store_id FROM memory_indexes WHERE memory_id=$id AND state!='deleted'", ("$id", id));
        var targets = new List<(long, string)>(); using (var r = maps.ExecuteReader()) while (r.Read()) targets.Add((r.GetInt64(0), r.GetString(1)));
        foreach (var (rev, store) in targets) Queue(c, tx, id, rev, store, "delete");
    }
    private void Invalidate(SqliteConnection c, SqliteTransaction tx, string id, bool eraseContent = true)
    {
        var record = Latest(c, tx, id);
        QueueIndexRemoval(c, tx, id);
        using var cmd = Command(c, tx, """
            UPDATE memory_records SET is_active=0,use_allowed=0,export_allowed=0,
                text=CASE WHEN $erase THEN '' ELSE text END,
                content_bytes=CASE WHEN $erase THEN 0 ELSE content_bytes END WHERE memory_id=$id;
            UPDATE memory_meta SET epoch=epoch+1,deletion_epoch=deletion_epoch+$erase WHERE id=1;
            """, ("$id", id), ("$erase", eraseContent));
        cmd.ExecuteNonQuery();
        if (record is null) { return; }
        AdvanceConversationEpoch(c, tx, record.DeviceId, record.ConversationId, eraseContent);
        if (record.Scope == MemoryScope.Personal && record.GuestId is not null)
        {
            using var bindings = Command(c, tx, "SELECT conversation_id FROM memory_guests WHERE app_namespace=$n AND device_id=$d AND guest_id=$guest",
                ("$n", memoryOptions.Namespace), ("$d", record.DeviceId), ("$guest", record.GuestId));
            var conversations = new List<string>();
            using (var reader = bindings.ExecuteReader())
            {
                while (reader.Read()) { conversations.Add(reader.GetString(0)); }
            }
            foreach (var conversation in conversations)
            {
                AdvanceConversationEpoch(c, tx, record.DeviceId, conversation, deletion: eraseContent);
            }
        }
        if (eraseContent && record.Scope == MemoryScope.EveningMotif)
        {
            using var contexts = Command(c, tx, "SELECT conversation_id FROM memory_context_scopes WHERE app_namespace=$n AND device_id=$d AND evening_id=$e",
                ("$n", memoryOptions.Namespace), ("$d", record.DeviceId), ("$e", record.EveningId));
            var conversations = new List<string>();
            using (var reader = contexts.ExecuteReader())
            {
                while (reader.Read()) { conversations.Add(reader.GetString(0)); }
            }
            foreach (var conversation in conversations)
            {
                AdvanceConversationEpoch(c, tx, record.DeviceId, conversation, deletion: true);
            }
        }
    }
    private void InvalidateEveningMemory(SqliteConnection c, SqliteTransaction tx, string device, string evening)
    {
        RevokeDeletedShowEvening(c, tx, device, evening);
        using var cmd = Command(c, tx, "SELECT DISTINCT memory_id FROM memory_records WHERE device_id=$d AND evening_id=$e",
            ("$d", device), ("$e", evening));
        var ids = new List<string>(); using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
        foreach (var id in ids) Invalidate(c, tx, id);
        using var delete = Command(c, tx, "DELETE FROM live_transcripts WHERE device_id=$d AND evening_id=$e", ("$d", device), ("$e", evening)); delete.ExecuteNonQuery();
        using var fence = Command(c, tx, "UPDATE memory_meta SET epoch=epoch+1,deletion_epoch=deletion_epoch+1 WHERE id=1");
        fence.ExecuteNonQuery();
    }
    private void Queue(SqliteConnection c, SqliteTransaction tx, string id, long rev, string store, string action)
    {
        using var cmd = Command(c, tx, """
            INSERT OR IGNORE INTO memory_indexes(memory_id,revision,store_id,filename,state)
                VALUES($id,$r,$s,$f,'queued');
            INSERT OR IGNORE INTO memory_outbox(memory_id,revision,store_id,action,next_at_ms)
                VALUES($id,$r,$s,$a,$now);
            """, ("$id", id), ("$r", rev), ("$s", store), ("$a", action),
            ("$f", "km-" + Guid.NewGuid().ToString("N") + ".txt"), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
        cmd.ExecuteNonQuery();
    }
    private static MemoryRecord ReadRecord(SqliteDataReader r) => new(r.GetString(0), Enum.Parse<MemoryScope>(r.GetString(2)),
        r.GetString(3), r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
        Enum.Parse<MemoryKind>(r.GetString(8)), r.GetString(9), r.GetString(10), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(11)),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(12)), r.GetInt64(1), r.GetBoolean(13), r.IsDBNull(14) ? null : r.GetString(14),
        r.GetBoolean(15), r.GetBoolean(16), r.GetInt64(17), r.GetString(19));
    private static MemoryRecord? Latest(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var cmd = Command(c, tx, "SELECT * FROM memory_records WHERE memory_id=$id ORDER BY revision DESC LIMIT 1", ("$id", id));
        using var r = cmd.ExecuteReader(); return r.Read() ? ReadRecord(r) : null;
    }
    internal MemoryRecord? CurrentMemory(string id) { lock (gate) { using var c = Open(); return Latest(c, null, id); } }
    internal bool IsCurrentMemoryPermitted(string id, MemoryAccess access, long expectedFactEpoch, long expectedDeletionEpoch)
    {
        lock (gate)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            return ConversationEpoch(c, tx, access.DeviceId, access.ConversationId, facts: true) == expectedFactEpoch
                && CanAppendConversation(c, tx, access, expectedDeletionEpoch)
                && Latest(c, tx, id) is { } record && IsPermitted(record, access);
        }
    }
    internal bool IsPermitted(MemoryRecord record, MemoryAccess access, bool cloud = false)
    {
        if (!record.IsActive || !record.UseAllowed || record.DeviceId != access.DeviceId
            || record.AppNamespace != memoryOptions.Namespace
            || record.Kind == MemoryKind.LegacyUnverified || cloud && !record.ExportAllowed) return false;
        if (record.Scope == MemoryScope.Conversation) return record.ConversationId == access.ConversationId && record.EveningId == access.EveningId;
        if (record.Scope == MemoryScope.EveningMotif) return record.EveningId == access.EveningId
            && (record.PersonalityId is null || record.PersonalityId == access.PersonalityId) && AnonymousMotifs.IsAllowed(record.Text);
        var permission = GuestPermission(access.DeviceId, access.ConversationId);
        return memoryOptions.LongTermPersonalEnabled && access.Guest is { IdentityVerified: true, LongTermAllowed: true }
            && permission is { IdentityVerified: true, LongTermAllowed: true }
            && access.Guest.GuestId == permission.GuestId && access.Guest.Version == permission.Version
            && record.GuestId == permission.GuestId && (!cloud || permission.CloudExportAllowed);
    }
    internal IReadOnlyList<MemoryRecord> LocalMemory(MemoryAccess access, MemoryScope? scope = null)
    {
        if (!memoryOptions.Enabled) return [];
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, """
                SELECT * FROM memory_records
                WHERE device_id=$d AND app_namespace=$namespace AND is_active=1 AND use_allowed=1
                    AND ($scope IS NULL OR scope=$scope)
                    AND ((scope='Conversation' AND conversation_id=$conversation AND evening_id=$evening)
                        OR (scope='EveningMotif' AND evening_id=$evening AND (personality_id IS NULL OR personality_id=$personality))
                        OR (scope='Personal' AND guest_id=$guest))
                ORDER BY created_at_ms DESC,revision DESC,memory_id;
                """, ("$d", access.DeviceId), ("$namespace", memoryOptions.Namespace), ("$scope", scope?.ToString()),
                ("$conversation", access.ConversationId), ("$evening", access.EveningId), ("$personality", access.PersonalityId),
                ("$guest", access.Guest?.GuestId));
            var rows = new List<MemoryRecord>(); using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(ReadRecord(r));
            return rows.Where(r => (scope is null || r.Scope == scope) && IsPermitted(r, access)).ToArray();
        }
    }
    internal MemoryRecord? ResolveFile(string store, string file, MemoryAccess access, MemoryScope scope)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, """
                SELECT r.* FROM memory_indexes i JOIN memory_records r ON r.memory_id=i.memory_id AND r.revision=i.revision
                WHERE i.store_id=$s AND i.file_id=$f AND i.state='completed' AND i.owned=1
                """, ("$s", store), ("$f", file));
            var rows = new List<MemoryRecord>(); using (var r = cmd.ExecuteReader()) while (r.Read()) rows.Add(ReadRecord(r));
            return rows.Count == 1 && rows[0].Scope == scope && IsPermitted(rows[0], access, cloud: true) ? rows[0] : null;
        }
    }

    internal MemoryIndexJob? ClaimMemoryJob()
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            var now = memoryClock.GetUtcNow().ToUnixTimeMilliseconds();
            using var cmd = Command(c, tx, """
                SELECT o.id,o.memory_id,o.revision,o.store_id,o.action,o.attempts,i.filename,i.file_id,i.state
                FROM memory_outbox o JOIN memory_indexes i USING(memory_id,revision,store_id)
                JOIN memory_records r ON r.memory_id=o.memory_id AND r.revision=o.revision
                WHERE r.app_namespace=$namespace AND (o.state='pending' OR o.state='leased' AND o.lease_until_ms <= $now) AND o.next_at_ms <= $now
                ORDER BY CASE o.action WHEN 'delete' THEN 0 ELSE 1 END,o.id LIMIT 1
                """, ("$now", now), ("$namespace", memoryOptions.Namespace));
            MemoryIndexJob? job; using (var r = cmd.ExecuteReader()) job = r.Read()
                ? new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8)) : null;
            if (job is null) return null;
            using var update = Command(c, tx, "UPDATE memory_outbox SET state='leased',attempts=attempts+1,lease_until_ms=$lease WHERE id=$id",
                ("$lease", now + (memoryOptions.IndexTimeoutSeconds + 2 * memoryOptions.TimeoutSeconds + 30) * 1000L), ("$id", job.Id)); update.ExecuteNonQuery();
            tx.Commit(); return job with { Attempts = job.Attempts + 1 };
        }
    }
    internal void SetMemoryIndex(MemoryIndexJob job, string state, string? file = null)
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            using var cmd = Command(c, tx, "UPDATE memory_indexes SET state=$state,file_id=COALESCE($file,file_id) WHERE memory_id=$id AND revision=$r AND store_id=$s",
                ("$state", state), ("$file", file), ("$id", job.MemoryId), ("$r", job.Revision), ("$s", job.StoreId)); cmd.ExecuteNonQuery();
            var current = Latest(c, tx, job.MemoryId);
            if (state is "completed" or "obsolete" && (current is not { IsActive: true, ExportAllowed: true } || current.Revision != job.Revision))
                Queue(c, tx, job.MemoryId, job.Revision, job.StoreId, "delete");
            tx.Commit();
        }
    }
    internal void QueueMemoryDeletion(MemoryIndexJob job)
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            Queue(c, tx, job.MemoryId, job.Revision, job.StoreId, "delete"); tx.Commit();
        }
    }
    internal void InvalidateConversationFacts(MemoryAccess access)
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            using var cmd = Command(c, tx, "SELECT DISTINCT memory_id FROM memory_records WHERE device_id=$d AND conversation_id=$c AND app_namespace=$n AND scope IN ('Conversation','Personal') AND kind IN ('UserReportedFact','ModelInterpretation','ProposedChoice','PendingQuestion') AND is_active=1",
                ("$d", access.DeviceId), ("$c", access.ConversationId), ("$n", memoryOptions.Namespace));
            var ids = new List<string>(); using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetString(0));
            foreach (var id in ids) Invalidate(c, tx, id, eraseContent: false);
            AdvanceConversationEpoch(c, tx, access.DeviceId, access.ConversationId, deletion: false);
            using var epoch = Command(c, tx, "UPDATE memory_meta SET epoch=epoch+1 WHERE id=1"); epoch.ExecuteNonQuery();
            tx.Commit();
        }
    }
    internal void FinishMemoryJob(MemoryIndexJob job, string? error = null, TimeSpan? retry = null)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, """
                UPDATE memory_outbox SET state=$state,error_code=$error,next_at_ms=$next,lease_until_ms=NULL,
                    deletion_confirmed_at_ms=CASE WHEN action='delete' AND $state='done' THEN $now ELSE deletion_confirmed_at_ms END
                WHERE id=$id
                """, ("$id", job.Id), ("$state", error is null ? "done" : retry is null ? "failed" : "pending"), ("$error", error),
                ("$next", memoryClock.GetUtcNow().Add(retry ?? TimeSpan.Zero).ToUnixTimeMilliseconds()), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds())); cmd.ExecuteNonQuery();
        }
    }
    internal bool IndexUploadInProgress(MemoryIndexJob job)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, "SELECT 1 FROM memory_outbox WHERE memory_id=$id AND revision=$r AND store_id=$s AND action='index' AND state='leased' AND lease_until_ms>$now",
                ("$id", job.MemoryId), ("$r", job.Revision), ("$s", job.StoreId), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
            return cmd.ExecuteScalar() is not null;
        }
    }
    internal void MarkDeletionRequested(MemoryIndexJob job)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, "UPDATE memory_outbox SET deletion_requested_at_ms=$now WHERE id=$id",
                ("$id", job.Id), ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds())); cmd.ExecuteNonQuery();
        }
    }
    internal bool OwnsUnsharedFile(MemoryIndexJob job, string file)
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, "SELECT COUNT(*) FROM memory_indexes WHERE file_id=$f AND owned=1 AND filename=$name",
                ("$f", file), ("$name", job.Filename));
            var own = (long)cmd.ExecuteScalar()! == 1;
            cmd.CommandText = "SELECT COUNT(*) FROM memory_indexes WHERE file_id=$f AND state!='deleted'";
            return own && (long)cmd.ExecuteScalar()! <= 1;
        }
    }
    internal (int Pending, int Failed, int Deleting) MemoryBacklog()
    {
        lock (gate)
        {
            using var c = Open(); using var cmd = Command(c, null, "SELECT SUM(state IN ('pending','leased')),SUM(state='failed'),SUM(action='delete' AND state!='done') FROM memory_outbox");
            using var r = cmd.ExecuteReader(); r.Read();
            return (r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1), r.IsDBNull(2) ? 0 : r.GetInt32(2));
        }
    }
    internal bool SpendMemoryQuota(string kind)
    {
        var max = kind == "search" ? memoryOptions.MaxSearchesPerDay : memoryOptions.MaxUploadsPerDay;
        lock (gate) { using var c = Open(); using var tx = c.BeginTransaction(); var allowed = Spend(c, tx, kind, 1, max); tx.Commit(); return allowed; }
    }
    private bool Spend(SqliteConnection c, SqliteTransaction tx, string kind, int amount, int maximum)
    {
        using var cmd = Command(c, tx, """
            INSERT INTO memory_usage(day,kind,amount) VALUES($day,$kind,$amount)
            ON CONFLICT(day,kind) DO UPDATE SET amount=amount+$amount WHERE amount+$amount <= $maximum
            """, ("$day", memoryClock.GetUtcNow().ToString("yyyy-MM-dd")), ("$kind", kind), ("$amount", amount), ("$maximum", maximum));
        return cmd.ExecuteNonQuery() == 1;
    }

    internal void PurgeHybridMemory()
    {
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction(); var now = memoryClock.GetUtcNow().ToUnixTimeMilliseconds();
            // Only technical bookkeeping expires. Application content, including
            // historical revisions and finalized transcripts, is manual-only.
            using var delete = Command(c, tx, """
                DELETE FROM memory_usage WHERE day<$cutoff;
                DELETE FROM memory_outbox WHERE state='done' AND next_at_ms<$retention;
                DELETE FROM memory_indexes WHERE state='deleted' AND NOT EXISTS
                    (SELECT 1 FROM memory_outbox o WHERE o.memory_id=memory_indexes.memory_id AND o.revision=memory_indexes.revision AND o.store_id=memory_indexes.store_id);
                """, ("$now", now), ("$cutoff", memoryClock.GetUtcNow().AddDays(-7).ToString("yyyy-MM-dd")), ("$retention", memoryClock.GetUtcNow().AddDays(-7).ToUnixTimeMilliseconds()));
            delete.ExecuteNonQuery(); tx.Commit();
            // Tombstone IDs/revisions intentionally survive content/index cleanup:
            // delayed summaries must never resurrect a removed record.
        }
    }

    private bool CanAppendConversation(SqliteConnection c, SqliteTransaction tx, MemoryAccess access, long? expectedDeletionEpoch)
    {
        using var check = Command(c, tx, """
            SELECT 1 FROM memory_meta WHERE id=1
                AND NOT EXISTS (SELECT 1 FROM memory_deleted_evenings WHERE app_namespace=$n AND device_id=$d AND evening_id=$e)
                AND NOT EXISTS (SELECT 1 FROM memory_deleted_conversations
                    WHERE app_namespace=$n AND device_id=$d AND conversation_id=$c);
            """, ("$epoch", expectedDeletionEpoch), ("$n", memoryOptions.Namespace),
            ("$d", access.DeviceId), ("$c", access.ConversationId), ("$e", access.EveningId));
        if (check.ExecuteScalar() is null || expectedDeletionEpoch is { } expected
            && ConversationEpoch(c, tx, access.DeviceId, access.ConversationId, facts: false) != expected) { return false; }
        // Runtime persistence supplies a captured generation. Direct operator
        // writes may omit it and still undergo scope-specific authorization.
        if (expectedDeletionEpoch is null) { return true; }
        var permission = ReadGuest(c, tx, access.DeviceId, access.ConversationId);
        return permission is null ? access.Guest is null
            : access.Guest is { } guest && permission.GuestId == guest.GuestId && permission.Version == guest.Version;
    }

    private void RecordCapacityFailure(SqliteConnection c, SqliteTransaction tx)
    {
        using var rejected = Command(c, tx, """
            INSERT INTO memory_storage_failures VALUES('archive_capacity',1,$now)
            ON CONFLICT(code) DO UPDATE SET rejected_writes=rejected_writes+1,last_at_ms=excluded.last_at_ms;
            """, ("$now", memoryClock.GetUtcNow().ToUnixTimeMilliseconds()));
        rejected.ExecuteNonQuery();
    }

    internal (long StoredBytes, long RejectedWrites, string? LastErrorCode, DateTimeOffset? LastErrorAt) MemoryStorageStatus()
    {
        lock (gate)
        {
            using var c = Open();
            using var command = Command(c, null, """
                SELECT (SELECT COALESCE(SUM(content_bytes),0) FROM memory_records)
                    +(SELECT COALESCE(SUM(length(CAST(text AS BLOB))),0) FROM live_transcripts),
                    (SELECT COALESCE(SUM(rejected_writes),0) FROM memory_storage_failures),
                    (SELECT code FROM memory_storage_failures ORDER BY last_at_ms DESC,code LIMIT 1),
                    (SELECT MAX(last_at_ms) FROM memory_storage_failures);
                """);
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)));
        }
    }

    internal bool AppendTimedTranscripts(MemoryAccess access, string generation, IReadOnlyList<LiveTranscriptSegment> segments,
        long? expectedDeletionEpoch = null, long? expectedFactEpoch = null)
    {
        if (!memoryOptions.Enabled) return false;
        lock (gate)
        {
            using var c = Open(); using var tx = c.BeginTransaction();
            if (!CanAppendConversation(c, tx, access, expectedDeletionEpoch)) { return false; }
            if (expectedFactEpoch is { } factEpoch
                && ConversationEpoch(c, tx, access.DeviceId, access.ConversationId, facts: true) != factEpoch) { return false; }
            using var exists = Command(c, tx, "SELECT 1 FROM evening_catalog WHERE device_id=$d AND evening_id=$e", ("$d", access.DeviceId), ("$e", access.EveningId));
            if (exists.ExecuteScalar() is null) return false;
            using var capacity = Command(c, tx, "SELECT (SELECT COALESCE(SUM(content_bytes),0) FROM memory_records)+(SELECT COALESCE(SUM(length(CAST(text AS BLOB))),0) FROM live_transcripts)");
            var used = (long)capacity.ExecuteScalar()!;
            var complete = true;
            foreach (var s in segments)
            {
                using var duplicate = Command(c, tx, "SELECT 1 FROM live_transcripts WHERE conversation_id=$c AND session_generation=$g AND reference=$r",
                    ("$c", access.ConversationId), ("$g", generation), ("$r", s.Reference));
                if (duplicate.ExecuteScalar() is not null) { continue; }
                var size = Encoding.UTF8.GetByteCount(s.Text);
                if (used + size > memoryOptions.MaxStoredBytes)
                {
                    RecordCapacityFailure(c, tx);
                    complete = false;
                    continue;
                }
                using var cmd = Command(c, tx, "INSERT OR IGNORE INTO live_transcripts(conversation_id,session_generation,reference,device_id,evening_id,speaker,start_ms,end_ms,event_id,text,expires_at_ms,app_namespace,guest_id,personality_id) VALUES($c,$g,$r,$d,$e,$speaker,$start,$end,$event,$text,$expires,$n,$guest,$personality)",
                    ("$c", access.ConversationId), ("$g", generation), ("$r", s.Reference), ("$d", access.DeviceId), ("$e", access.EveningId),
                    ("$speaker", s.Speaker), ("$start", s.StartMs), ("$end", s.EndMs), ("$event", s.EventId), ("$text", s.Text),
                    ("$expires", DateTimeOffset.MaxValue.ToUnixTimeMilliseconds()), ("$n", memoryOptions.Namespace),
                    ("$guest", access.Guest?.GuestId), ("$personality", access.PersonalityId));
                if (cmd.ExecuteNonQuery() == 1) used += size;
            }
            tx.Commit();
            return complete;
        }
    }
}

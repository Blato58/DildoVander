using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    private const int CompletionScanLimit = 200;
    private static readonly JsonSerializerOptions CompletionJson = new(JsonSerializerDefaults.Web);

    private sealed record CompletionCursor(string AccountId, long UpperSequence, long BeforeSequence, int Limit);
    private sealed record CompletionEventRow(string Id, long Sequence, string OwnerId, string SessionId,
        int AccessEpoch, string KeepsakeId, string Kind, string? WorkId, string State, string? Reason,
        bool HasStory, bool HasArtwork, long OccurredAtTicks, long? ReadAtTicks);

    private static void InitializeCompletionUpdates(SqliteConnection connection)
    {
        Execute(connection, null, """
            CREATE TABLE IF NOT EXISTS dv10_completion_updates (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                id TEXT NOT NULL UNIQUE,
                owner_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                access_epoch INTEGER NOT NULL,
                keepsake_id TEXT NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('keepsake','portrait')),
                work_id TEXT,
                state TEXT NOT NULL,
                reason TEXT,
                has_story INTEGER NOT NULL,
                has_artwork INTEGER NOT NULL,
                occurred_at_ticks INTEGER NOT NULL,
                read_at_ticks INTEGER
            );
            CREATE INDEX IF NOT EXISTS dv10_completion_owner_sequence
                ON dv10_completion_updates(owner_id,sequence DESC);
            CREATE INDEX IF NOT EXISTS dv10_completion_current
                ON dv10_completion_updates(keepsake_id,kind,sequence DESC);
            """);
    }

    // Called by mutation transactions after the document/job reaches its final
    // durable state and immediately before commit. It records only a changed
    // customer-visible projection, never provider or document content.
    private void RecordCompletionUpdates(SqliteConnection connection, SqliteTransaction transaction,
        string keepsakeId)
    {
        using var owner = Command(connection, transaction, """
            SELECT o.owner_id,o.session_id,o.epoch,k.account_access_epoch,k.document
            FROM dv10_customer_owners o
            JOIN dv10_keepsakes k ON k.id=o.keepsake_id
            WHERE o.keepsake_id=$id AND k.deleted=0
              AND o.epoch=k.account_access_epoch
              AND NOT EXISTS(SELECT 1 FROM dv10_account_fences f WHERE f.owner_id=o.owner_id)
            """, ("$id", keepsakeId));
        using var reader = owner.ExecuteReader();
        if (!reader.Read()) { return; }

        var ownerId = reader.GetString(0);
        var sessionId = reader.GetString(1);
        var accessEpoch = reader.GetInt32(2);
        var documentJson = reader.GetString(4);
        reader.Close();

        var document = JsonSerializer.Deserialize<Document>(documentJson, Json);
        if (document is null) { return; }

        RecordCompletionProjection(connection, transaction, ownerId, sessionId, accessEpoch,
            keepsakeId, ObserveCompletion(connection, transaction, keepsakeId, document,
                CompletionUpdateKind.Keepsake));

        if (document.CurrentPortraitJob is not null)
        {
            RecordCompletionProjection(connection, transaction, ownerId, sessionId, accessEpoch,
                keepsakeId, ObserveCompletion(connection, transaction, keepsakeId, document,
                    CompletionUpdateKind.Portrait));
        }
    }

    private void RecordCompletionProjection(SqliteConnection connection, SqliteTransaction transaction,
        string ownerId, string sessionId, int accessEpoch, string keepsakeId,
        CompletionObservation observation)
    {
        var projection = CustomerCompletionPolicy.Project(observation);
        var kind = KindName(projection.Kind);
        using (var latest = Command(connection, transaction, """
            SELECT owner_id,session_id,access_epoch,work_id,state,reason,has_story,has_artwork
            FROM dv10_completion_updates
            WHERE keepsake_id=$id AND kind=$kind
            ORDER BY sequence DESC LIMIT 1
            """, ("$id", keepsakeId), ("$kind", kind)))
        using (var reader = latest.ExecuteReader())
        {
            if (reader.Read()
                && reader.GetString(0) == ownerId
                && reader.GetString(1) == sessionId
                && reader.GetInt32(2) == accessEpoch
                && ReadNullableString(reader, 3) == projection.WorkId
                && reader.GetString(4) == StateName(projection.State)
                && ReadNullableString(reader, 5) == ReasonName(projection.Reason)
                && (reader.GetInt32(6) != 0) == projection.HasStory
                && (reader.GetInt32(7) != 0) == projection.HasArtwork)
            {
                return;
            }
        }

        Execute(connection, transaction, """
            INSERT INTO dv10_completion_updates(
                id,owner_id,session_id,access_epoch,keepsake_id,kind,work_id,state,reason,
                has_story,has_artwork,occurred_at_ticks)
            VALUES($event,$owner,$session,$epoch,$keepsake,$kind,$work,$state,$reason,$story,$artwork,$time)
            """, ("$event", NewId()), ("$owner", ownerId), ("$session", sessionId),
            ("$epoch", accessEpoch), ("$keepsake", keepsakeId), ("$kind", kind),
            ("$work", projection.WorkId), ("$state", StateName(projection.State)),
            ("$reason", ReasonName(projection.Reason)), ("$story", projection.HasStory ? 1 : 0),
            ("$artwork", projection.HasArtwork ? 1 : 0), ("$time", Now.UtcTicks));
    }

    private CompletionObservation ObserveCompletion(SqliteConnection connection,
        SqliteTransaction transaction, string keepsakeId, Document document, CompletionUpdateKind kind)
    {
        var workId = kind switch
        {
            CompletionUpdateKind.Keepsake => document.CurrentBaseJob,
            CompletionUpdateKind.Portrait => document.CurrentPortraitJob,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        string? jobState = null;
        if (workId is not null)
        {
            using var job = Command(connection, transaction,
                "SELECT state FROM dv10_jobs WHERE id=$job AND keepsake_id=$keepsake AND kind=$kind",
                ("$job", workId), ("$keepsake", keepsakeId),
                ("$kind", kind == CompletionUpdateKind.Keepsake ? "base" : "portrait"));
            jobState = job.ExecuteScalar() as string;
        }

        var edition = kind == CompletionUpdateKind.Keepsake && workId is not null
            ? document.Editions.SingleOrDefault(item => item.JobId == workId
                && item.PublicationState != "private_history")
            : null;
        var story = edition is not null && !string.IsNullOrWhiteSpace(edition.Story.Text);
        var artwork = edition is not null && document.Assets.Any(asset =>
            asset.Id == edition.ArtworkAssetId && asset.Kind == "base_image");

        var portraitPublished = kind == CompletionUpdateKind.Portrait
            && workId is not null
            && document.Portraits.Any(portrait => portrait.JobId == workId
                && document.Assets.Any(asset => asset.Id == portrait.AssetId && asset.Kind == "portrait_image"));

        return new(kind, workId, jobState,
            kind == CompletionUpdateKind.Keepsake && document.NeedsBaseGeneration,
            kind == CompletionUpdateKind.Keepsake && MayDispatch("base"),
            story, artwork, portraitPublished,
            Accessible: true);
    }

    private static void DeleteCompletionUpdates(SqliteConnection connection, SqliteTransaction transaction,
        string keepsakeId, bool portraitsOnly = false)
    {
        Execute(connection, transaction,
            portraitsOnly
                ? "DELETE FROM dv10_completion_updates WHERE keepsake_id=$id AND kind='portrait'"
                : "DELETE FROM dv10_completion_updates WHERE keepsake_id=$id",
            ("$id", keepsakeId));
    }

    private static void DeleteAccountCompletionUpdates(SqliteConnection connection,
        SqliteTransaction transaction, string accountId)
    {
        Execute(connection, transaction,
            "DELETE FROM dv10_completion_updates WHERE owner_id=$owner", ("$owner", accountId));
    }

    internal CustomerCompletionPage ReadCompletionUpdates(KeepsakeActor actor, int limit = 25,
        string? cursor = null)
    {
        if (!actor.IsAccount || actor.AccountId is not { } accountId)
        {
            throw new KeepsakeException("keepsake_not_found", 404);
        }
        if (limit is < 1 or > 50) { throw new KeepsakeException("invalid_completion_limit", 400); }

        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireSession(connection, transaction, actor);

            long upperSequence;
            long beforeSequence;
            if (cursor is null)
            {
                using var maximum = Command(connection, transaction,
                    "SELECT COALESCE(MAX(sequence),0) FROM dv10_completion_updates");
                upperSequence = (long)maximum.ExecuteScalar()!;
                beforeSequence = long.MaxValue;
            }
            else
            {
                var decoded = UnprotectCompletionCursor(cursor);
                var maximumBefore = decoded.UpperSequence == long.MaxValue
                    ? long.MaxValue : decoded.UpperSequence + 1;
                if (decoded.AccountId != accountId || decoded.Limit != limit
                    || decoded.UpperSequence < 0 || decoded.BeforeSequence < 1
                    || decoded.BeforeSequence > maximumBefore)
                {
                    throw new KeepsakeException("invalid_completion_cursor", 400);
                }
                upperSequence = decoded.UpperSequence;
                beforeSequence = decoded.BeforeSequence;
            }

            var events = ReadCompletionEventRows(connection, transaction, accountId,
                upperSequence, beforeSequence);
            var items = new List<CustomerCompletionUpdate>(limit);
            long? nextBefore = null;
            var rowsToScan = Math.Min(events.Count, CompletionScanLimit);
            for (var index = 0; index < rowsToScan; index++)
            {
                var row = events[index];
                if (!IsCurrentCompletionEvent(connection, transaction, actor, row)) { continue; }
                if (items.Count == limit)
                {
                    nextBefore = items[^1].Sequence;
                    break;
                }
                items.Add(ProjectCompletionEvent(row));
            }

            if (nextBefore is null && events.Count > CompletionScanLimit)
            {
                nextBefore = events[CompletionScanLimit - 1].Sequence;
            }

            var nextCursor = nextBefore is null ? null : ProtectCompletionCursor(
                new(accountId, upperSequence, nextBefore.Value, limit));
            transaction.Commit();
            return new(1, items, nextCursor);
        }
    }

    internal void MarkCompletionRead(KeepsakeActor actor, string eventId)
    {
        if (!actor.IsAccount || actor.AccountId is not { } accountId
            || !Guid.TryParseExact(eventId, "D", out _))
        {
            throw new KeepsakeException("completion_not_found", 404);
        }

        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireSession(connection, transaction, actor);
            var row = ReadCompletionEventById(connection, transaction, accountId, eventId);
            if (row is null || !IsCurrentCompletionEvent(connection, transaction, actor, row))
            {
                throw new KeepsakeException("completion_not_found", 404);
            }

            Execute(connection, transaction,
                "UPDATE dv10_completion_updates SET read_at_ticks=COALESCE(read_at_ticks,$time) WHERE id=$id",
                ("$time", Now.UtcTicks), ("$id", eventId));
            transaction.Commit();
        }
    }

    private List<CompletionEventRow> ReadCompletionEventRows(SqliteConnection connection,
        SqliteTransaction transaction, string accountId, long upperSequence, long beforeSequence)
    {
        using var command = Command(connection, transaction, """
            SELECT e.id,e.sequence,e.owner_id,e.session_id,e.access_epoch,e.keepsake_id,e.kind,
                   e.work_id,e.state,e.reason,e.has_story,e.has_artwork,e.occurred_at_ticks,e.read_at_ticks
            FROM dv10_completion_updates e
            JOIN dv10_customer_owners o ON o.keepsake_id=e.keepsake_id
                AND o.owner_id=e.owner_id AND o.session_id=e.session_id AND o.epoch=e.access_epoch
            JOIN dv10_keepsakes k ON k.id=e.keepsake_id
                AND k.account_access_epoch=e.access_epoch AND k.deleted=0
            WHERE e.owner_id=$owner AND e.sequence<=$upper AND e.sequence<$before
              AND NOT EXISTS(SELECT 1 FROM dv10_account_fences f WHERE f.owner_id=e.owner_id)
              AND NOT EXISTS(SELECT 1 FROM dv10_completion_updates newer
                  WHERE newer.keepsake_id=e.keepsake_id AND newer.kind=e.kind
                    AND newer.sequence>e.sequence)
            ORDER BY e.sequence DESC LIMIT $limit
            """, ("$owner", accountId), ("$upper", upperSequence),
            ("$before", beforeSequence), ("$limit", CompletionScanLimit + 1));
        using var reader = command.ExecuteReader();
        var rows = new List<CompletionEventRow>(CompletionScanLimit + 1);
        while (reader.Read()) { rows.Add(ReadCompletionEvent(reader)); }
        return rows;
    }

    private CompletionEventRow? ReadCompletionEventById(SqliteConnection connection,
        SqliteTransaction transaction, string accountId, string eventId)
    {
        using var command = Command(connection, transaction, """
            SELECT e.id,e.sequence,e.owner_id,e.session_id,e.access_epoch,e.keepsake_id,e.kind,
                   e.work_id,e.state,e.reason,e.has_story,e.has_artwork,e.occurred_at_ticks,e.read_at_ticks
            FROM dv10_completion_updates e
            JOIN dv10_customer_owners o ON o.keepsake_id=e.keepsake_id
                AND o.owner_id=e.owner_id AND o.session_id=e.session_id AND o.epoch=e.access_epoch
            JOIN dv10_keepsakes k ON k.id=e.keepsake_id
                AND k.account_access_epoch=e.access_epoch AND k.deleted=0
            WHERE e.id=$id AND e.owner_id=$owner
              AND NOT EXISTS(SELECT 1 FROM dv10_account_fences f WHERE f.owner_id=e.owner_id)
              AND NOT EXISTS(SELECT 1 FROM dv10_completion_updates newer
                  WHERE newer.keepsake_id=e.keepsake_id AND newer.kind=e.kind
                    AND newer.sequence>e.sequence)
            LIMIT 1
            """, ("$id", eventId), ("$owner", accountId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadCompletionEvent(reader) : null;
    }

    private bool IsCurrentCompletionEvent(SqliteConnection connection, SqliteTransaction transaction,
        KeepsakeActor actor, CompletionEventRow row)
    {
        try
        {
            RequireAccess(connection, transaction, row.KeepsakeId, actor, KeepsakeScope.Read);
        }
        catch (KeepsakeException exception) when (exception.Status == 404)
        {
            return false;
        }

        Document document;
        try { document = Load(connection, transaction, row.KeepsakeId); }
        catch (KeepsakeException exception) when (exception.Status == 404) { return false; }

        var kind = row.Kind switch
        {
            "keepsake" => CompletionUpdateKind.Keepsake,
            "portrait" => CompletionUpdateKind.Portrait,
            _ => (CompletionUpdateKind?)null
        };
        if (kind is null || (kind == CompletionUpdateKind.Portrait && document.CurrentPortraitJob is null))
        {
            return false;
        }

        var current = CustomerCompletionPolicy.Project(
            ObserveCompletion(connection, transaction, row.KeepsakeId, document, kind.Value));
        return current.WorkId == row.WorkId
            && StateName(current.State) == row.State
            && ReasonName(current.Reason) == row.Reason
            && current.HasStory == row.HasStory
            && current.HasArtwork == row.HasArtwork;
    }

    private static CustomerCompletionUpdate ProjectCompletionEvent(CompletionEventRow row) =>
        new(row.Id, row.Sequence, row.Kind, row.State, row.Reason, row.KeepsakeId,
            false, row.HasStory, row.HasArtwork,
            new DateTimeOffset(row.OccurredAtTicks, TimeSpan.Zero),
            row.ReadAtTicks is null ? null : new DateTimeOffset(row.ReadAtTicks.Value, TimeSpan.Zero));

    private static CompletionEventRow ReadCompletionEvent(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
            reader.GetInt32(4), reader.GetString(5), reader.GetString(6), ReadNullableString(reader, 7),
            reader.GetString(8), ReadNullableString(reader, 9), reader.GetInt32(10) != 0,
            reader.GetInt32(11) != 0, reader.GetInt64(12), reader.IsDBNull(13) ? null : reader.GetInt64(13));

    private string ProtectCompletionCursor(CompletionCursor cursor)
    {
        var protector = replayProtector.CreateProtector("DV10.CustomerCompletionCursor.v1");
        var protectedValue = protector.Protect(JsonSerializer.Serialize(cursor, CompletionJson));
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(protectedValue))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (token.Length > 2048) { throw new KeepsakeException("invalid_completion_cursor", 400); }
        return token;
    }

    private CompletionCursor UnprotectCompletionCursor(string token)
    {
        if (token.Length is 0 or > 2048) { throw new KeepsakeException("invalid_completion_cursor", 400); }
        try
        {
            var base64 = token.Replace('-', '+').Replace('_', '/');
            base64 += new string('=', (4 - base64.Length % 4) % 4);
            var protectedValue = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
            var protector = replayProtector.CreateProtector("DV10.CustomerCompletionCursor.v1");
            return JsonSerializer.Deserialize<CompletionCursor>(protector.Unprotect(protectedValue), CompletionJson)
                ?? throw new KeepsakeException("invalid_completion_cursor", 400);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException or JsonException)
        {
            throw new KeepsakeException("invalid_completion_cursor", 400);
        }
    }

    private static string KindName(CompletionUpdateKind kind) => kind switch
    {
        CompletionUpdateKind.Keepsake => "keepsake",
        CompletionUpdateKind.Portrait => "portrait",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string StateName(CompletionUpdateState state) => state switch
    {
        CompletionUpdateState.Pending => "pending",
        CompletionUpdateState.Processing => "processing",
        CompletionUpdateState.Partial => "partial",
        CompletionUpdateState.Ready => "ready",
        CompletionUpdateState.Failed => "failed",
        CompletionUpdateState.Unavailable => "unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };

    private static string? ReasonName(CompletionUpdateReason reason) => reason switch
    {
        CompletionUpdateReason.None => null,
        CompletionUpdateReason.AwaitingCapacity => "awaiting_capacity",
        CompletionUpdateReason.PausedPreparing => "paused_preparing",
        CompletionUpdateReason.ReconciliationRequired => "reconciliation_required",
        CompletionUpdateReason.GenerationFailed => "generation_failed",
        CompletionUpdateReason.ResultUnavailable => "result_unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeAccountCollectionMetadata(string? OfficialName, string? StoryTitle,
    bool HasPortrait, KeepsakeReadiness Readiness);

internal sealed partial class KeepsakeStore
{
    internal KeepsakeAccountCollectionMetadata ReadCustomerCollectionMetadata(string id, KeepsakeActor actor)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            _ = RequireAccess(connection, transaction, id, actor, KeepsakeScope.Read);
            var result = ReadCollectionMetadata(connection, transaction, id, null);
            transaction.Commit();
            return result;
        }
    }

    internal KeepsakeAccountCollectionMetadata ReadLinkedCollectionMetadata(string id, int epoch)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!HasAccountEpoch(connection, id, epoch, transaction))
            {
                throw new KeepsakeException("access_unavailable", 404);
            }
            var result = ReadCollectionMetadata(connection, transaction, id, epoch);
            transaction.Commit();
            return result;
        }
    }

    private KeepsakeAccountCollectionMetadata ReadCollectionMetadata(SqliteConnection connection,
        SqliteTransaction transaction, string id, int? epoch)
    {
        using var command = Command(connection, transaction, """
            SELECT json_extract(k.document,'$.snapshot.officialName'),
                (SELECT json_extract(e.value,'$.story.title')
                    FROM json_each(k.document,'$.editions') e
                    WHERE COALESCE(json_extract(e.value,'$.publicationState'),'ready')<>'private_history'
                    ORDER BY json_extract(e.value,'$.createdAt') DESC,
                        json_extract(e.value,'$.id') COLLATE BINARY ASC LIMIT 1),
                EXISTS(SELECT 1 FROM json_each(k.document,'$.portraits') p
                    JOIN json_each(k.document,'$.assets') a
                        ON json_extract(a.value,'$.id')=json_extract(p.value,'$.assetId')
                        AND json_extract(a.value,'$.kind')='portrait_image'),
                COALESCE(json_extract(k.document,'$.needsBaseGeneration'),1),
                EXISTS(SELECT 1 FROM json_each(k.document,'$.editions') e
                    WHERE COALESCE(json_extract(e.value,'$.publicationState'),'ready')<>'private_history'
                        AND length(trim(COALESCE(json_extract(e.value,'$.story.text'),'')))>0),
                EXISTS(SELECT 1 FROM json_each(k.document,'$.editions') e
                    WHERE COALESCE(json_extract(e.value,'$.publicationState'),'ready')<>'private_history'
                        AND EXISTS(SELECT 1 FROM json_each(k.document,'$.assets') a
                            WHERE json_extract(a.value,'$.id')=json_extract(e.value,'$.artworkAssetId')
                                AND json_extract(a.value,'$.kind')='base_image')),
                (SELECT state FROM dv10_jobs j
                    WHERE j.id=json_extract(k.document,'$.currentBaseJob')
                        AND j.keepsake_id=k.id AND j.kind='base' LIMIT 1)
            FROM dv10_keepsakes k
            WHERE k.id=$id AND k.deleted=0 AND ($epoch IS NULL OR k.account_access_epoch=$epoch);
            """, ("$id", id), ("$epoch", epoch));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new KeepsakeException("access_unavailable", 404); }
        var officialName = reader.IsDBNull(0) ? null : reader.GetString(0);
        var storyTitle = reader.IsDBNull(1) ? null : reader.GetString(1);
        var hasPortrait = reader.GetInt64(2) != 0;
        var needsBase = reader.GetInt64(3) != 0;
        var hasStory = reader.GetInt64(4) != 0;
        var hasArtwork = reader.GetInt64(5) != 0;
        var jobState = reader.IsDBNull(6) ? null : reader.GetString(6);
        return new(officialName, storyTitle, hasPortrait,
            KeepsakeReadiness.From(jobState, needsBase, hasStory, hasArtwork, MayDispatch("base")));
    }
}

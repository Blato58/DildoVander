using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record OwnedWandArtworkReference(string Locator, string AssetId);

internal sealed partial class KeepsakeStore
{
    // Project the canonical image for the active character only when the account
    // still owns its completed generated-wand keepsake and the asset still exists.
    // The resulting asset ID is not a capability: the asset route checks ownership
    // again on every read.
    internal OwnedWandArtworkReference? FindOwnedWandArtwork(string accountId, string wandId)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(wandId)) return null;
        lock (gate)
        {
            using var connection = Open();
            using var lookup = Command(connection, null, """
                SELECT o.keepsake_id FROM dv10_customer_owners o
                JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND k.deleted=0 AND k.account_access_epoch=o.epoch
                    AND json_extract(k.document,'$.snapshot.generatedWand.wandId')=$wand
                ORDER BY k.rowid DESC LIMIT 1
                """, ("$owner", accountId), ("$wand", wandId));
            if (lookup.ExecuteScalar() is not string locator) return null;
            var document = Load(connection, null, locator);
            var assetId = CanonicalGeneratedArtworkId(document);
            return assetId is null ? null : new(locator, assetId);
        }
    }

    private static string? CanonicalGeneratedArtworkId(Document document) =>
        document.Snapshot.GeneratedWand is null ? null : document.Editions
            .Where(edition => edition.PublicationState != "private_history")
            .LastOrDefault(edition => document.Assets.Any(asset => asset.Id == edition.ArtworkAssetId
                && asset.Kind == "base_image" && asset.UploadId is null))?.ArtworkAssetId;

    internal void ValidateSession(KeepsakeActor actor)
    {
        lock (gate)
        {
            using var connection = Open();
            RequireSession(connection, null, actor);
        }
    }

    internal CustomerSessionResult ReadCustomerSessionResult(string sessionId, KeepsakeActor actor)
    {
        if (!actor.IsAccount || actor.AccountId is null || actor.AccountSessionId is null)
        {
            throw new KeepsakeException("keepsake_not_found", 404);
        }

        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireSession(connection, transaction, actor);
            using var bindings = Command(connection, transaction, """
                SELECT o.keepsake_id,o.epoch,k.account_access_epoch,k.deleted
                FROM dv10_customer_owners o
                LEFT JOIN dv10_keepsakes k ON k.id=o.keepsake_id
                WHERE o.owner_id=$owner AND o.session_id=$session
                LIMIT 2;
                """, ("$owner", actor.AccountId), ("$session", sessionId));
            using var reader = bindings.ExecuteReader();
            if (!reader.Read())
            {
                reader.Close();
                transaction.Commit();
                return Pending(sessionId);
            }

            var locator = reader.GetString(0);
            var storedEpoch = reader.GetInt32(1);
            var currentEpoch = reader.IsDBNull(2) ? (int?)null : reader.GetInt32(2);
            var deleted = reader.IsDBNull(3) || reader.GetInt32(3) != 0;
            if (reader.Read())
            {
                reader.Close();
                transaction.Commit();
                return Unavailable(sessionId);
            }
            reader.Close();

            if (deleted || currentEpoch is null || storedEpoch < 0 || storedEpoch != currentEpoch)
            {
                transaction.Commit();
                return Unavailable(sessionId);
            }

            // Recheck through the common authorization predicate so the live
            // account-session callback, owner row, current epoch and deletion
            // fence are all required in the same SQLite transaction.
            RequireAccess(connection, transaction, locator, actor, KeepsakeScope.Read);
            var document = Load(connection, transaction, locator);
            var readiness = Readiness(connection, locator, document, transaction);
            var approved = WandPublicationApproved(connection, locator, document, transaction);
            // The original account owner may display the same canonical base illustration
            // as the keepsake. Return an asset identifier, never an unauthenticated URL;
            // every image byte request rechecks the existing owner authorization.
            var artwork = CanonicalGeneratedArtworkId(document);
            transaction.Commit();
            return new(1, sessionId, locator, readiness, "originalOwner", true, true, false,
                document.Snapshot.GeneratedWand, WandPublicationApproved: approved, ArtworkAssetId: artwork);
        }
    }

    internal CustomerSessionResult UnavailableCustomerSessionResult(string sessionId) => Unavailable(sessionId);

    private CustomerSessionResult Pending(string sessionId) => new(1, sessionId, null,
        KeepsakeReadiness.Pending(), null, false, false, false);

    private CustomerSessionResult Unavailable(string sessionId) => new(1, sessionId, null,
        KeepsakeReadiness.Unavailable(), null, false, false, false);

    private KeepsakeReadiness Readiness(SqliteConnection connection, string keepsakeId, Document document,
        SqliteTransaction? transaction = null)
    {
        string? jobState = null;
        if (document.CurrentBaseJob is { } currentJob)
        {
            using var job = Command(connection, transaction,
                "SELECT state FROM dv10_jobs WHERE id=$job AND keepsake_id=$keepsake AND kind='base'",
                ("$job", currentJob), ("$keepsake", keepsakeId));
            using var reader = job.ExecuteReader();
            if (reader.Read())
            {
                jobState = reader.GetString(0);
            }
        }

        var visible = document.Editions.Where(edition => edition.PublicationState != "private_history").ToArray();
        var hasArtwork = visible.Any(edition => document.Assets.Any(asset =>
            asset.Id == edition.ArtworkAssetId && asset.Kind == "base_image"));
        var hasStory = visible.Any(edition => !string.IsNullOrWhiteSpace(edition.Story.Text));
        return KeepsakeReadiness.From(jobState, document.NeedsBaseGeneration,
            hasStory, hasArtwork, MayDispatch("base"));
    }
}

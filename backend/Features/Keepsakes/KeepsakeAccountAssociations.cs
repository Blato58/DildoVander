using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed record KeepsakeAccountProof(string Locator, int Epoch);

internal sealed partial class KeepsakeStore
{
    public KeepsakeAccountProof AuthorizeAccountAssociation(string id, KeepsakeActor session)
    {
        lock (gate)
        {
            using var connection = Open();
            RequireAccess(connection, null, id, session, KeepsakeScope.Write);
            using var command = Command(connection, null,
                "SELECT account_access_epoch FROM dv10_keepsakes WHERE id=$id AND deleted=0", ("$id", id));
            var epoch = command.ExecuteScalar();
            if (epoch is null) { throw new KeepsakeException("access_unavailable", 404); }
            return new(id, Convert.ToInt32(epoch));
        }
    }

    public bool IsAccountAssociationCurrent(string id, int epoch)
    {
        if (!Guid.TryParseExact(id, "D", out _) || epoch < 0) { return false; }
        lock (gate)
        {
            using var connection = Open();
            return HasAccountEpoch(connection, id, epoch);
        }
    }

    private static bool HasAccountEpoch(SqliteConnection connection, string id, int epoch, SqliteTransaction? transaction = null)
    {
        using var command = Command(connection, transaction,
            "SELECT 1 FROM dv10_keepsakes WHERE id=$id AND account_access_epoch=$epoch AND deleted=0",
            ("$id", id), ("$epoch", epoch));
        return command.ExecuteScalar() is not null;
    }

    public string? RecallAccountAssociationTicket(KeepsakeAccountProof proof, KeepsakeActor session, string targetAccountId, string key)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, proof.Locator, session, KeepsakeScope.Write);
            if (!HasAccountEpoch(connection, proof.Locator, proof.Epoch, transaction)) { throw new KeepsakeException("access_unavailable", 404); }
            var replay = Replay(connection, transaction, "account-association:" + proof.Locator + ":" + Hash(session.StableIdentity), key, targetAccountId + ":" + proof.Epoch);
            transaction.Commit();
            return replay is null ? null : replayProtector.Unprotect(replay);
        }
    }

    public string RememberAccountAssociationTicket(KeepsakeAccountProof proof, KeepsakeActor session, string targetAccountId, string key, string ticket)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, proof.Locator, session, KeepsakeScope.Write);
            if (!HasAccountEpoch(connection, proof.Locator, proof.Epoch, transaction)) { throw new KeepsakeException("access_unavailable", 404); }
            var scope = "account-association:" + proof.Locator + ":" + Hash(session.StableIdentity);
            var body = targetAccountId + ":" + proof.Epoch;
            var replay = Replay(connection, transaction, scope, key, body);
            if (replay is not null) { return replayProtector.Unprotect(replay); }
            Remember(connection, transaction, scope, key, body, replayProtector.Protect(ticket));
            transaction.Commit();
            return ticket;
        }
    }

    // Account endpoints first check their own exact user/locator grant. The
    // independent epoch fence makes guest revocation and deletion authoritative.
    public KeepsakeView ReadForAccount(string id, int epoch)
    {
        lock (gate)
        {
            using var connection = Open();
            if (!HasAccountEpoch(connection, id, epoch)) { throw new KeepsakeException("access_unavailable", 404); }
            var document = Load(connection, null, id);
            var assets = document.Assets.Where(asset => asset.Kind is "base_image" or "portrait_image"
                || (asset.UploadId is null && asset.Kind is "export_story" or "export_card" or "export_zip")).ToArray();
            var assetIds = assets.Select(asset => asset.Id).ToHashSet();
            var portraits = document.Portraits.Where(portrait => assetIds.Contains(portrait.AssetId)).ToArray();
            var ready = document.Editions.Any(edition => assetIds.Contains(edition.ArtworkAssetId));
            return new(id, document.Snapshot, "manual_only", ready ? "ready" : "pending", document.Editions,
                portraits, [], [], assets, true, null, document.Revision,
                WizardIdentities: document.WizardIdentities.Where(identity => document.Editions.Any(edition =>
                    edition.Id == identity.EditionId && edition.PublicationState != "private_history")).Select(ToCard).ToArray(),
                Readiness: Readiness(connection, id, document), WandPublicationApproved: WandPublicationApproved(connection, id, document));
        }
    }

    public KeepsakeAssetContent ReadAssetForAccount(string id, int epoch, string assetId)
    {
        lock (gate)
        {
            var view = ReadForAccount(id, epoch);
            var asset = view.Assets.SingleOrDefault(asset => asset.Id == assetId);
            if (asset is null) { throw new KeepsakeException("access_unavailable", 404); }
            return new(asset, ReadBytes(id, asset));
        }
    }
}

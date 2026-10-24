using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    internal const string WandArtworkPromptVersion = "personal-wand-art-v1";

    // One-time deterministic adoption during startup, never during GET. Preserve
    // the currently published illustration; retain contradictory older artwork
    // privately and invalidate its old share grants rather than substituting it.
    private void AdoptLegacyWandArtwork(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        using var lookup = Command(connection, transaction,
            "SELECT id,document,published_edition_id FROM dv10_keepsakes WHERE deleted=0 "
            + "AND json_extract(document,'$.snapshot.generatedWand') IS NOT NULL "
            + "AND json_extract(document,'$.artwork') IS NULL");
        var rows = new List<(string Id, Document Document, string? Published)>();
        using (var reader = lookup.ExecuteReader())
            while (reader.Read()) rows.Add((reader.GetString(0), System.Text.Json.JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!, reader.IsDBNull(2) ? null : reader.GetString(2)));
        foreach (var (id, document, published) in rows)
        {
            var valid = document.Editions.Where(edition => edition.PublicationState != "private_history"
                && document.Assets.Any(asset => asset.Id == edition.ArtworkAssetId && asset.Kind == "base_image" && asset.UploadId is null)).ToArray();
            var chosen = valid.FirstOrDefault(edition => edition.Id == published) ?? valid.OrderBy(edition => edition.CreatedAt).FirstOrDefault();
            if (chosen is not null)
            {
                document.Artwork = new(document.Snapshot.GeneratedWand!.WandId, chosen.ArtworkAssetId, 1,
                    chosen.Synthetic, chosen.Provider, chosen.Model, "legacy-adopted-v1", chosen.CreatedAt);
                for (var index = 0; index < document.Editions.Count; index++)
                {
                    var old = document.Editions[index];
                    if (old.ArtworkAssetId.Length == 0 || old.ArtworkAssetId == chosen.ArtworkAssetId) continue;
                    document.Editions[index] = old with { PublicationState = "private_history" };
                    Execute(connection, transaction, "DELETE FROM dv10_grants WHERE link_hash IN (SELECT secret_hash FROM dv10_links WHERE keepsake_id=$id AND edition_id=$edition AND kind='share')", ("$id", id), ("$edition", old.Id));
                    Execute(connection, transaction, "DELETE FROM dv10_links WHERE keepsake_id=$id AND edition_id=$edition AND kind='share'", ("$id", id), ("$edition", old.Id));
                    RecordRevocation(connection, transaction, id, null, "legacy_artwork_reconciled");
                }
                document.Revision++;
                Save(connection, transaction, id, document);
            }
            else
            {
                // A queued old story must not occupy the only queue slot while
                // waiting for its newly independent artwork dependency.
                Execute(connection, transaction, "UPDATE dv10_jobs SET state='awaiting_artwork' WHERE keepsake_id=$id AND kind='base' AND state IN ('queued','leased')", ("$id", id));
            }
        }
        transaction.Commit();
    }

    private bool TryAdmitWandArtwork(SqliteConnection connection, SqliteTransaction transaction, string id, Document document)
    {
        if (document.Snapshot.GeneratedWand is null || CanonicalGeneratedArtworkId(document) is not null
            || document.CurrentArtworkJob is not null || !MayDispatch("wand_artwork")
            || !HasQueueCapacity(connection, transaction) || !CanReserveGenerationStorage()) return false;
        var job = EnqueueInternal(connection, transaction, id, "wand_artwork", new(new()));
        document.CurrentArtworkJob = job.Id;
        Save(connection, transaction, id, document);
        RecordCompletionUpdates(connection, transaction, id);
        return true;
    }

    private OwnedWandArtworkReference? ArtworkReference(SqliteConnection connection, SqliteTransaction? transaction,
        string id, Document document)
    {
        if (document.Snapshot.GeneratedWand is null) return null;
        if (CanonicalGeneratedArtworkId(document) is { } assetId) return new(id, assetId,
            Synthetic: document.Artwork?.Synthetic ?? document.Editions.FirstOrDefault(edition => edition.ArtworkAssetId == assetId)?.Synthetic ?? false);
        if (document.CurrentArtworkJob is not { } jobId) return new(id, null, "missing", MayDispatch("wand_artwork"));
        var job = ReadJob(connection, transaction, jobId);
        var status = job.State switch { "queued" => "queued", "leased" or "dispatched" => "running", _ => "failed" };
        return new(id, null, status, status == "failed" && MayDispatch("wand_artwork"), job.ErrorCode);
    }

    internal KeepsakeJob RetryWandArtwork(string id, KeepsakeActor actor, string key)
    {
        RequireFeature("GenerationEnabled");
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, actor, KeepsakeScope.Write);
            var document = Load(connection, transaction, id);
            if (document.Snapshot.GeneratedWand is null) throw new KeepsakeException("personal_wand_unavailable", 409);
            var scope = "retry-wand-artwork:" + id;
            if (Replay(connection, transaction, scope, key, "v1") is { } replay) return ReadJob(connection, transaction, replay);
            if (document.CurrentArtworkJob is { } current)
            {
                var old = ReadJob(connection, transaction, current);
                if (CanonicalGeneratedArtworkId(document) is not null || old.State is "queued" or "leased" or "dispatched")
                {
                    Remember(connection, transaction, scope, key, "v1", current);
                    transaction.Commit();
                    return old;
                }
                // Explicit recovery abandons an uncertain attempt; incrementing its fence
                // rejects late results. Restart alone never repeats a paid dispatch.
                Execute(connection, transaction, "UPDATE dv10_jobs SET state='cancelled',fence=fence+1 WHERE id=$id", ("$id", current));
            }
            else if (CanonicalGeneratedArtworkId(document) is not null)
                throw new KeepsakeException("wand_artwork_already_published", 409);
            CheckAdmissionCapacity();
            if (!HasQueueCapacity(connection, transaction)) throw new KeepsakeException("generation_backpressure", 429);
            var job = EnqueueInternal(connection, transaction, id, "wand_artwork", new(new()));
            document.CurrentArtworkJob = job.Id;
            document.Revision++;
            Save(connection, transaction, id, document);
            Remember(connection, transaction, scope, key, "v1", job.Id);
            RecordCompletionUpdates(connection, transaction, id);
            transaction.Commit();
            return job;
        }
    }

    private bool CompleteWandArtwork(SqliteConnection connection, SqliteTransaction transaction, LeasedJob job,
        Document document, KeepsakeProviderOutput output, bool publish)
    {
        if (!publish || CanonicalGeneratedArtworkId(document) is not null) return false;
        var wand = document.Snapshot.GeneratedWand ?? throw new KeepsakeException("personal_wand_unavailable", 409);
        var asset = StoreAsset(job.KeepsakeId, document, "base_image", output.MediaType, output.ImageBytes);
        StoreAsset(job.KeepsakeId, document, "provider_response", "application/json",
            System.Text.Encoding.UTF8.GetBytes(output.ContentResponse));
        StoreAsset(job.KeepsakeId, document, "prompt", "text/plain; charset=utf-8", System.Text.Encoding.UTF8.GetBytes(output.Prompt));
        document.Artwork = new(wand.WandId, asset.Id, 1, output.Synthetic, output.Provider, output.Model, WandArtworkPromptVersion, Now);
        document.Revision++;
        Save(connection, transaction, job.KeepsakeId, document);
        Execute(connection, transaction, "UPDATE dv10_jobs SET state='completed' WHERE id=$id AND fence=$f", ("$id", job.Id), ("$f", job.Fence));
        Execute(connection, transaction, "UPDATE dv10_jobs SET state='queued' WHERE keepsake_id=$id AND kind='base' AND state='awaiting_artwork'", ("$id", job.KeepsakeId));
        TryAdmitAutomaticBase(connection, transaction, job.KeepsakeId, document);
        RecordCompletionUpdates(connection, transaction, job.KeepsakeId);
        transaction.Commit();
        return true;
    }

    internal Kufr.Backend.Features.Show.DuelView ProjectBattleArtwork(Kufr.Backend.Features.Show.DuelView duel)
    {
        // Preserve identity/event snapshots in storage, but do not leave withdrawn
        // opponent artwork in an open account-history presentation after polling.
        return duel with
        {
            Left = duel.Left with { ArtworkUrl = Visible(duel.Left) },
            Right = duel.Right with { ArtworkUrl = Visible(duel.Right) }
        };
        string? Visible(Kufr.Backend.Features.Show.DuelIdentitySnapshot side) =>
            side.ArtworkUrl is { } original && PublicWandArtworkUrl(side.IdentityId, duel.EveningId, duel.DeviceId) == original ? original : null;
    }

    // Only approved public editions expose art to battle spectators. The URL is
    // an identifier, not a capability; every byte request repeats publication checks.
    internal string? PublicWandArtworkUrl(string identityId, string eveningId, string deviceId)
        => PublicWandArtworkUrl(identityId, eveningId, deviceId, out _);

    internal string? PublicWandArtworkUrl(string identityId, string eveningId, string deviceId, out bool synthetic)
    {
        synthetic = false;
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, null, """
                SELECT k.id,k.document,k.published_edition_id FROM dv10_keepsakes k,
                    json_each(k.document,'$.wizardIdentities') i
                WHERE k.device_id=$device AND k.evening_id=$evening AND k.deleted=0
                    AND json_extract(i.value,'$.identityId')=$identity
                """, ("$device", deviceId), ("$evening", eveningId), ("$identity", identityId));
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(2)) return null;
            var id = reader.GetString(0);
            var document = System.Text.Json.JsonSerializer.Deserialize<Document>(reader.GetString(1), Json)!;
            var editionId = reader.GetString(2);
            if (!WandPublicationApproved(document, editionId)) return null;
            var edition = document.Editions.SingleOrDefault(item => item.Id == editionId && item.PublicationState == "ready");
            if (edition is null || !document.Assets.Any(asset => asset.Id == edition.ArtworkAssetId && IsPublicBaseAsset(asset, edition))) return null;
            synthetic = document.Artwork?.Synthetic ?? edition.Synthetic;
            return $"/guest/v1/evenings/{eveningId}/keepsakes/{id}/editions/{editionId}/assets/{edition.ArtworkAssetId}";
        }
    }
}

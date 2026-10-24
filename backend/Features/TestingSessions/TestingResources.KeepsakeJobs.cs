namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    // Exact archive lease for an authorized explicit integration request. This
    // preserves normal storage/admission/job fences without draining another
    // account's queue or launching a separate generation implementation.
    internal LeasedJob? LeaseNextForKeepsake(string id)
    {
        lock (gate)
        {
            if (!MayDispatch("base") || !CanReserveGenerationStorage()) return null;
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            var document = Load(connection, transaction, id);
            TryAdmitAutomaticBase(connection, transaction, id, document);
            using var command = Command(connection, transaction,
                "SELECT id,keepsake_id,kind,fence,epoch,photo_epoch,request_json FROM dv10_jobs "
                + "WHERE keepsake_id=$id AND state='queued' AND (kind='wand_artwork' OR "
                + "(kind='base' AND EXISTS(SELECT 1 FROM dv10_keepsakes k WHERE k.id=keepsake_id AND "
                + "(json_extract(k.document,'$.snapshot.generatedWand') IS NULL OR json_extract(k.document,'$.artwork.assetId') IS NOT NULL))) "
                + "OR (kind='portrait' AND $portrait=1)) ORDER BY CASE kind WHEN 'wand_artwork' THEN 0 ELSE 1 END,created,id LIMIT 1",
                ("$id", id), ("$portrait", MayDispatch("portrait") ? 1 : 0));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { reader.Close(); transaction.Commit(); return null; }
            var job = new LeasedJob(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) + 1,
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6));
            reader.Close();
            Execute(connection, transaction, "UPDATE dv10_jobs SET state='leased',fence=$f WHERE id=$id AND state='queued'",
                ("$f", job.Fence), ("$id", job.Id));
            RecordCompletionUpdates(connection, transaction, job.KeepsakeId);
            transaction.Commit();
            return job;
        }
    }
}

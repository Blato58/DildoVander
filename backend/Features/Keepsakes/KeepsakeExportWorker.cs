namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    // Local derivatives have their own durable retry state, entirely separate
    // from provider attempts. Retrying a PNG/ZIP never calls the AI provider.
    internal bool ProcessOneBaseExport()
    {
        lock (gate)
        {
            if (!configuration.GetValue<bool>("Keepsake:Enabled")) { return false; }
            using var connection = Open();
            using var candidate = Command(connection, null,
                "SELECT keepsake_id,edition_id FROM dv10_derivatives WHERE state='queued' AND retry_after<=$time ORDER BY rowid LIMIT 1",
                ("$time", Now.ToUnixTimeSeconds()));
            using var reader = candidate.ExecuteReader();
            if (!reader.Read()) { return false; }
            var id = reader.GetString(0);
            var editionId = reader.GetString(1);
            reader.Close();
            try
            {
                foreach (var format in new[] { "story", "card", "zip" })
                {
                    using var transaction = connection.BeginTransaction();
                    var document = Load(connection, transaction, id);
                    var edition = document.Editions.SingleOrDefault(item => item.Id == editionId && item.PublicationState == "ready");
                    if (edition is null) { throw new KeepsakeException("edition_not_published", 409); }
                    var before = document.Assets.Count;
                    CreateExportInternal(connection, transaction, id, document, editionId, format, null);
                    if (before != document.Assets.Count)
                    {
                        document.Revision++;
                        Save(connection, transaction, id, document);
                    }
                    transaction.Commit();
                }
                Execute(connection, null, "UPDATE dv10_derivatives SET state='completed',attempts=attempts+1 WHERE edition_id=$edition",
                    ("$edition", editionId));
            }
            catch (KeepsakeException exception) when (exception.Status is 404 or 409 or 413)
            {
                Execute(connection, null, "UPDATE dv10_derivatives SET state='unavailable',attempts=attempts+1 WHERE edition_id=$edition",
                    ("$edition", editionId));
            }
            catch (Exception)
            {
                Execute(connection, null, "UPDATE dv10_derivatives SET attempts=attempts+1,retry_after=$retry WHERE edition_id=$edition",
                    ("$retry", Now.AddSeconds(30).ToUnixTimeSeconds()), ("$edition", editionId));
            }
            return true;
        }
    }
}

internal sealed class KeepsakeExportWorker(KeepsakeStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!store.ProcessOneBaseExport()) { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
        }
    }
}

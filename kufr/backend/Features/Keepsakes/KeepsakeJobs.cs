using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Kufr.Backend.Features.Keepsakes;

internal sealed partial class KeepsakeStore
{
    private sealed record JobRequest(KeepsakeBrief Brief, string? EditionId = null,
        string? UploadId = null, string? Variant = null, int ConsentVersion = 0);
    internal sealed record LeasedJob(string Id, string KeepsakeId, string Kind, int Fence,
        int Epoch, int PhotoEpoch, string RequestJson);

    public KeepsakeJob EnqueueBase(string id, string session, KeepsakeBrief brief, string key)
    {
        RequireFeature("GenerationEnabled");
        if ((brief.DisplayName?.Length ?? 0) > 80 || (brief.PersonalMotif?.Length ?? 0) > 400
            || (!brief.PersonalizationConsent && (brief.DisplayName is not null || brief.PersonalMotif is not null)))
        {
            throw new KeepsakeException("personalization_consent_required");
        }
        return Enqueue(id, session, "base", new(brief), key);
    }

    public KeepsakeJob EnqueuePortrait(string id, string session, string uploadId, string editionId,
        string variant, bool consent, string key)
    {
        RequireFeature("PortraitEnabled");
        if (!consent) { throw new KeepsakeException("portrait_consent_required"); }
        // The pinned SDK supports one visitor photo. Do not silently substitute variants.
        if (variant != "portrait_only") { throw new KeepsakeException("portrait_variant_not_supported", 409); }
        lock (gate)
        {
            using var connection = Open();
            RequireAccess(connection, null, id, session, KeepsakeScope.Photo);
            var document = Load(connection, null, id);
            if (!document.PortraitConsent) { throw new KeepsakeException("portrait_consent_required"); }
            if (!document.Uploads.Any(item => item.Id == uploadId)
                || !document.Editions.Any(item => item.Id == editionId))
            {
                throw new KeepsakeException("portrait_source_not_found", 404);
            }
            return Enqueue(id, session, "portrait", new(new(), editionId, uploadId, variant, document.ConsentVersion), key);
        }
    }

    private KeepsakeJob Enqueue(string id, string session, string kind, JobRequest request, string key)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            var body = JsonSerializer.Serialize(request, Json);
            var scope = "enqueue:" + id + ":" + kind;
            var replay = Replay(connection, transaction, scope, key, body);
            if (replay is not null) { return ReadJob(connection, transaction, replay); }
            var document = Load(connection, transaction, id);
            CheckAdmissionCapacity();
            using var pending = Command(connection, transaction,
                "SELECT count(*) FROM dv10_jobs WHERE keepsake_id=$id AND state IN ('queued','leased','dispatched','awaiting_reconciliation')", ("$id", id));
            if ((long)pending.ExecuteScalar()! >= 2) { throw new KeepsakeException("generation_backpressure", 429); }
            if (!HasQueueCapacity(connection, transaction)) { throw new KeepsakeException("generation_backpressure", 429); }
            var job = EnqueueInternal(connection, transaction, id, kind, request);
            if (kind == "base") { document.CurrentBaseJob = job.Id; document.NeedsBaseGeneration = false; }
            else { document.CurrentPortraitJob = job.Id; }
            document.Revision++;
            Save(connection, transaction, id, document);
            Remember(connection, transaction, scope, key, body, job.Id);
            transaction.Commit();
            return job;
        }
    }

    private KeepsakeJob EnqueueInternal(SqliteConnection connection, SqliteTransaction transaction,
        string id, string kind, JobRequest request)
    {
        if (kind == "base") { request = request with { EditionId = NewId() }; }
        var jobId = NewId();
        Execute(connection, transaction, """
            INSERT INTO dv10_jobs(id,keepsake_id,kind,state,epoch,photo_epoch,request_json,created)
            SELECT $job,id,$kind,'queued',epoch,photo_epoch,$request,$time FROM dv10_keepsakes WHERE id=$id AND deleted=0
            """, ("$job", jobId), ("$kind", kind), ("$request", JsonSerializer.Serialize(request, Json)),
            ("$time", Now.ToUnixTimeSeconds()), ("$id", id));
        return new(jobId, kind, "queued", request.EditionId, request.UploadId, request.Variant, 0, Now);
    }

    private static KeepsakeJob ReadJob(SqliteConnection connection, SqliteTransaction? transaction, string jobId)
    {
        using var command = Command(connection, transaction,
            "SELECT kind,state,request_json,fence,created,error_code FROM dv10_jobs WHERE id=$id", ("$id", jobId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) { throw new KeepsakeException("job_not_found", 404); }
        var request = JsonSerializer.Deserialize<JobRequest>(reader.GetString(2), Json)!;
        return new(jobId, reader.GetString(0), reader.GetString(1), request.EditionId, request.UploadId,
            request.Variant, reader.GetInt32(3), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(4)),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private bool HasQueueCapacity(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var total = Command(connection, transaction, "SELECT count(*) FROM dv10_jobs WHERE state IN ('queued','leased','dispatched')");
        var capacity = Math.Clamp(configuration.GetValue("Keepsake:MaxQueuedJobs", 32), 1, 32);
        return (long)total.ExecuteScalar()! < capacity;
    }

    private bool TryAdmitAutomaticBase(SqliteConnection connection, SqliteTransaction transaction, string id, Document document)
    {
        if (!document.NeedsBaseGeneration || document.CurrentBaseJob is not null) { return false; }
        if (!MayDispatch("base") || !HasQueueCapacity(connection, transaction)) { return false; }
        if (!CanReserveGenerationStorage()) { return false; }
        var job = EnqueueInternal(connection, transaction, id, "base", new(new()));
        document.CurrentBaseJob = job.Id;
        document.NeedsBaseGeneration = false;
        Save(connection, transaction, id, document);
        return true;
    }

    private void AdmitOneDeferredBase(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!HasQueueCapacity(connection, transaction)) { return; }
        using var candidate = Command(connection, transaction,
            "SELECT id FROM dv10_keepsakes WHERE deleted=0 AND json_extract(document,'$.currentBaseJob') IS NULL "
            + "AND coalesce(json_extract(document,'$.needsBaseGeneration'),1)=1 ORDER BY id LIMIT 1");
        var id = candidate.ExecuteScalar() as string;
        if (id is null) { return; }
        TryAdmitAutomaticBase(connection, transaction, id, Load(connection, transaction, id));
    }

    internal LeasedJob? LeaseNext()
    {
        lock (gate)
        {
            if (!MayDispatch("base") || !CanReserveGenerationStorage()) { return null; }
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            AdmitOneDeferredBase(connection, transaction);
            using var command = Command(connection, transaction,
                "SELECT id,keepsake_id,kind,fence,epoch,photo_epoch,request_json FROM dv10_jobs "
                + "WHERE state='queued' AND (kind='base' OR (kind='portrait' AND $portrait=1)) ORDER BY created,id LIMIT 1",
                ("$portrait", MayDispatch("portrait") ? 1 : 0));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) { reader.Close(); transaction.Commit(); return null; }
            var job = new LeasedJob(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3) + 1,
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6));
            reader.Close();
            Execute(connection, transaction, "UPDATE dv10_jobs SET state='leased',fence=$f WHERE id=$id AND state='queued'",
                ("$f", job.Fence), ("$id", job.Id));
            transaction.Commit();
            return job;
        }
    }

    internal KeepsakeProviderRequest? Dispatch(LeasedJob job)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (!MayDispatch(job.Kind))
            {
                Execute(connection, transaction, "UPDATE dv10_jobs SET state='queued' WHERE id=$id AND state='leased' AND fence=$f",
                    ("$id", job.Id), ("$f", job.Fence));
                transaction.Commit();
                return null;
            }
            var document = Load(connection, transaction, job.KeepsakeId);
            var request = JsonSerializer.Deserialize<JobRequest>(job.RequestJson, Json)!;
            if (!Eligible(connection, transaction, job, document, request)) { return null; }
            if (Execute(connection, transaction,
                "UPDATE dv10_jobs SET state='dispatched' WHERE id=$id AND fence=$f AND state='leased'",
                ("$id", job.Id), ("$f", job.Fence)) != 1) { return null; }
            byte[]? photo = null;
            KeepsakeStory? story = null;
            if (job.Kind == "portrait")
            {
                var upload = document.Uploads.Single(item => item.Id == request.UploadId);
                var asset = document.Assets.Single(item => item.Id == upload.NormalizedAssetId);
                photo = ReadBytes(job.KeepsakeId, asset);
                story = document.Editions.Single(item => item.Id == request.EditionId).Story;
            }
            transaction.Commit();
            return new KeepsakeProviderRequest(job.KeepsakeId, document.Snapshot, request.Brief, job.Id, request.EditionId,
                request.UploadId, request.Variant, photo, story)
            {
                RecordStage = (stage, phase, content) => RecordProviderStage(job, stage, phase, content)
            };
        }
    }

    private bool RecordProviderStage(LeasedJob job, string stage, string phase, string content)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            if (phase == "dispatch" && !MayDispatch(job.Kind))
            {
                Execute(connection, transaction,
                    "UPDATE dv10_jobs SET state=CASE WHEN EXISTS(SELECT 1 FROM dv10_attempts WHERE job_id=$id) "
                    + "THEN 'awaiting_reconciliation' ELSE 'queued' END,error_code='provider_paused' WHERE id=$id AND fence=$f AND state='dispatched'",
                    ("$id", job.Id), ("$f", job.Fence));
                transaction.Commit();
                return false;
            }
            Document document;
            try { document = Load(connection, transaction, job.KeepsakeId); }
            catch (KeepsakeException) { return false; }
            var request = JsonSerializer.Deserialize<JobRequest>(job.RequestJson, Json)!;
            if (!(phase == "dispatch" ? Eligible(connection, transaction, job, document, request)
                : CanIngest(connection, transaction, job, document, request))) { return false; }
            using var current = Command(connection, transaction,
                "SELECT state FROM dv10_jobs WHERE id=$id AND fence=$f", ("$id", job.Id), ("$f", job.Fence));
            var state = current.ExecuteScalar() as string;
            if (state is not ("dispatched" or "cancelled")) { return false; }
            if (phase == "dispatch")
            {
                if (state != "dispatched") { return false; }
                Execute(connection, transaction,
                    "INSERT INTO dv10_attempts(id,job_id,keepsake_id,stage,state,fence,created) VALUES($a,$job,$id,$stage,'dispatched',$f,$t)",
                    ("$a", NewId()), ("$job", job.Id), ("$id", job.KeepsakeId), ("$stage", stage),
                    ("$f", job.Fence), ("$t", Now.ToUnixTimeSeconds()));
            }
            else
            {
                Execute(connection, transaction,
                    "UPDATE dv10_attempts SET state='completed' WHERE job_id=$job AND stage=$stage AND fence=$f AND state='dispatched'",
                    ("$job", job.Id), ("$stage", stage), ("$f", job.Fence));
            }
            StoreAsset(job.KeepsakeId, document, "provider_" + stage + "_" + phase,
                "application/json", System.Text.Encoding.UTF8.GetBytes(content), request.EditionId, request.UploadId);
            if (stage == "story" && phase == "completed")
            {
                try
                {
                    using var response = JsonDocument.Parse(content);
                    var story = OpenAiKeepsakeProvider.ParseStory(response.RootElement);
                    var publish = state == "dispatched" && document.CurrentBaseJob == job.Id
                        && Eligible(connection, transaction, job, document, request);
                    var model = response.RootElement.TryGetProperty("model", out var modelValue) ? modelValue.GetString() ?? "unknown" : "unknown";
                    document.Editions.Add(new(request.EditionId!, job.Id, story, "", false, "openai", model, "", Now,
                        publish ? "story_ready" : "private_history"));
                    document.Revision++;
                }
                catch (Exception exception) when (exception is JsonException or KeepsakeException or KeyNotFoundException or InvalidOperationException)
                {
                    // Validation rejection does not erase the provider's archived content.
                    Execute(connection, transaction, "UPDATE dv10_jobs SET error_code='provider_story_invalid' WHERE id=$id", ("$id", job.Id));
                }
            }
            Save(connection, transaction, job.KeepsakeId, document);
            transaction.Commit();
            return true;
        }
    }

    private static bool Eligible(SqliteConnection connection, SqliteTransaction transaction,
        LeasedJob job, Document document, JobRequest request)
    {
        using var command = Command(connection, transaction,
            "SELECT epoch,photo_epoch,deleted FROM dv10_keepsakes WHERE id=$id", ("$id", job.KeepsakeId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(2) != 0 || reader.GetInt32(0) != job.Epoch) { return false; }
        return job.Kind != "portrait" || (reader.GetInt32(1) == job.PhotoEpoch
            && document.PortraitConsent && document.ConsentVersion == request.ConsentVersion
            && document.Uploads.Any(item => item.Id == request.UploadId)
            && document.Editions.Any(item => item.Id == request.EditionId));
    }

    private static bool CanIngest(SqliteConnection connection, SqliteTransaction transaction,
        LeasedJob job, Document document, JobRequest request)
    {
        using var command = Command(connection, transaction,
            "SELECT epoch,deleted FROM dv10_keepsakes WHERE id=$id", ("$id", job.KeepsakeId));
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(1) != 0 || reader.GetInt32(0) != job.Epoch) { return false; }
        // A newer consent or edition can hide an already dispatched result, but only
        // explicit deletion removes its accepted source and prevents private archival.
        return job.Kind != "portrait" || (document.Uploads.Any(item => item.Id == request.UploadId)
            && document.Editions.Any(item => item.Id == request.EditionId));
    }

    internal bool Complete(LeasedJob job, KeepsakeProviderOutput output)
    {
        ValidateGeneratedImage(output);
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            Document document;
            try { document = Load(connection, transaction, job.KeepsakeId); }
            catch (KeepsakeException) { return false; }
            var request = JsonSerializer.Deserialize<JobRequest>(job.RequestJson, Json)!;
            if (!CanIngest(connection, transaction, job, document, request)) { return false; }
            using var status = Command(connection, transaction, "SELECT state,fence FROM dv10_jobs WHERE id=$id", ("$id", job.Id));
            using var reader = status.ExecuteReader();
            if (!reader.Read() || reader.GetInt32(1) != job.Fence) { return false; }
            var state = reader.GetString(0);
            reader.Close();
            if (state is not ("dispatched" or "cancelled" or "awaiting_reconciliation")) { return false; }
            // Cancel preserves a legitimately returned result in private history only.
            var publish = state == "dispatched" && Eligible(connection, transaction, job, document, request) && (job.Kind == "base"
                ? document.CurrentBaseJob == job.Id : document.CurrentPortraitJob == job.Id);
            var editionId = request.EditionId!;
            var asset = StoreAsset(job.KeepsakeId, document, publish ? job.Kind + "_image" : job.Kind + "_history_image",
                output.MediaType, output.ImageBytes, editionId, request.UploadId);
            StoreAsset(job.KeepsakeId, document, "provider_response", "application/json",
                System.Text.Encoding.UTF8.GetBytes(output.ContentResponse), editionId, request.UploadId);
            StoreAsset(job.KeepsakeId, document, "prompt", "text/plain; charset=utf-8",
                System.Text.Encoding.UTF8.GetBytes(output.Prompt), editionId, request.UploadId);
            if (job.Kind == "base")
            {
                if (output.Story is null) { throw new KeepsakeException("provider_story_missing"); }
                document.Editions.RemoveAll(edition => edition.Id == editionId);
                var completedAt = Now;
                document.Editions.Add(new(editionId, job.Id, output.Story, asset.Id, output.Synthetic,
                    output.Provider, output.Model, output.Prompt, completedAt, publish ? "ready" : "private_history"));
                if (publish)
                {
                    Execute(connection, transaction,
                        "UPDATE dv10_keepsakes SET published_at_ticks=$time,published_edition_id=$edition WHERE id=$id AND deleted=0",
                        ("$time", completedAt.UtcTicks), ("$edition", editionId), ("$id", job.KeepsakeId));
                    Execute(connection, transaction,
                        "INSERT OR IGNORE INTO dv10_derivatives(edition_id,keepsake_id,state) VALUES($edition,$id,'queued')",
                        ("$edition", editionId), ("$id", job.KeepsakeId));
                }
            }
            else
            {
                document.Portraits.Add(new(NewId(), job.Id, editionId, request.UploadId!, request.Variant!, asset.Id,
                    output.Synthetic, output.Provider, output.Model, output.Prompt, Now));
            }
            document.Revision++;
            Save(connection, transaction, job.KeepsakeId, document);
            Execute(connection, transaction, "UPDATE dv10_jobs SET state=$s WHERE id=$id AND fence=$f",
                ("$s", publish ? "completed" : "archived_private"), ("$id", job.Id), ("$f", job.Fence));
            transaction.Commit();
            return publish;
        }
    }

    internal void MarkAmbiguous(LeasedJob job, string errorCode)
    {
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null,
                "UPDATE dv10_jobs SET state='awaiting_reconciliation',error_code=$e WHERE id=$id AND fence=$f AND state='dispatched'",
                ("$e", errorCode), ("$id", job.Id), ("$f", job.Fence));
        }
    }

    public void Cancel(string id, string session, string jobId, string key)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            RequireAccess(connection, transaction, id, session, KeepsakeScope.Write);
            var scope = "cancel:" + id;
            if (Replay(connection, transaction, scope, key, jobId) is not null) { return; }
            Execute(connection, transaction,
                "UPDATE dv10_jobs SET state='cancelled' WHERE keepsake_id=$id AND id=$job AND state IN ('queued','leased','dispatched','awaiting_reconciliation')",
                ("$id", id), ("$job", jobId));
            Remember(connection, transaction, scope, key, jobId, jobId);
            transaction.Commit();
        }
    }

    private void RequireFeature(string name)
    {
        if (!configuration.GetValue<bool>("Keepsake:" + name)) { throw new KeepsakeException("feature_disabled", 404); }
    }

    private bool MayDispatch(string kind) => configuration.GetValue<bool>("Keepsake:Enabled")
        && configuration.GetValue<bool>("Keepsake:GenerationEnabled")
        && (kind == "base" || configuration.GetValue<bool>("Keepsake:PortraitEnabled"));

    private void CheckAdmissionCapacity()
    {
        var minimum = Math.Max(64L * 1024 * 1024, configuration.GetValue("Keepsake:MinimumFreeDiskBytes", 256L * 1024 * 1024));
        var drive = new DriveInfo(Path.GetPathRoot(dataRoot)!);
        if (drive.AvailableFreeSpace - 64L * 1024 * 1024 < minimum)
        {
            throw new KeepsakeException("storage_backpressure", 503);
        }
    }

    private bool CanReserveGenerationStorage()
    {
        try { CheckAdmissionCapacity(); return true; }
        catch (KeepsakeException exception) when (exception.Code == "storage_backpressure") { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

internal sealed class KeepsakeWorker(KeepsakeStore store, IKeepsakeProvider provider,
    ILogger<KeepsakeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = store.LeaseNext();
            if (job is null) { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); continue; }
            try
            {
                var request = store.Dispatch(job);
                if (request is null) { continue; }
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromMinutes(4));
                var output = await provider.GenerateAsync(request, deadline.Token);
                store.Complete(job, output);
            }
            catch (Exception exception)
            {
                // Technical diagnostics never contain prompts, names, photos or provider bodies.
                store.MarkAmbiguous(job, exception is OperationCanceledException ? "provider_timeout" : "provider_attempt_unknown");
                logger.LogWarning("Keepsake job {JobId} requires reconciliation ({ExceptionType})", job.Id, exception.GetType().Name);
            }
        }
    }
}

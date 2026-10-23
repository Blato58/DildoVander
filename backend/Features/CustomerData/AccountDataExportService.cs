using System.Security.Cryptography;

namespace Kufr.Backend.Features.CustomerData;

internal sealed class AccountDataExportService(ICustomerDataAuthority authority,
    ICustomerDataGuardAuthority guards, ICustomerMemoryGuardedDataSource memory,
    AccountDataExportStore store, TimeProvider clock)
{
    private readonly SemaphoreSlim downloadSlots = new(2, 2);

    internal async Task<AccountDataExportView> RequestAsync(HttpContext http, Guid requestId,
        CancellationToken token = default)
    {
        var access = await authority.CaptureAsync(http, token);
        AccountDataExportJob job;
        {
            using var prelude = await guards.AcquireAsync(access, token);
            job = store.Create(prelude, requestId);
        }
        return job.State == AccountDataExportState.Ready
            ? await StatusAsync(http, job.Id, token) : View(job);
    }

    internal async Task<AccountDataExportView> StatusAsync(HttpContext http, string id,
        CancellationToken token = default)
    {
        var access = await authority.CaptureAsync(http, token);
        var job = Find(id, access);
        if (job.State != AccountDataExportState.Ready) { return View(job); }
        if (job.DownloadExpiresAt is not { } expiry || clock.GetUtcNow() >= expiry)
        {
            store.Fail(id, AccountDataExportFailure.DownloadExpired);
            return View(Find(id, access));
        }
        var current = false;
        try
        {
            using var prelude = await guards.AcquireAsync(access, token);
            using var admission = await memory.AcquireAsync(prelude.Deadline, token);
            using var download = store.OpenDownloadGuard(id, access, prelude.Deadline);
            job = download.Job;
            if (job.State != AccountDataExportState.Ready || job.Proof is null)
            {
                throw new AccountDataExportException(409, "account_export_not_ready");
            }
            expiry = job.DownloadExpiresAt
                ?? throw new AccountDataExportException(410, "export_download_expired");
            using var scope = prelude.AcquireShared(admission);
            scope.ConstrainDownloadExpiry(expiry);
            var snapshot = memory.Capture(scope);
            current = Matches(job, scope, snapshot);
            scope.ValidateCurrent();
        }
        catch (CustomerDataAccessException error) when (error.Status is 401 or 409)
        {
            current = false;
        }
        catch (AccountDataExportException error) when (error.Status == 410)
        {
            store.Fail(id, AccountDataExportFailure.DownloadExpired);
            return View(Find(id, access));
        }
        if (!current) { store.Fail(id, AccountDataExportFailure.SourceChanged); }
        return View(Find(id, access));
    }

    internal async Task DeleteAsync(HttpContext http, string id, CancellationToken token = default)
    {
        var access = await authority.CaptureAsync(http, token);
        if (!store.DeleteForAccount(id, access.AccountId))
        {
            throw new AccountDataExportException(404, "account_export_unavailable");
        }
    }

    // The job journal never holds a transaction while acquiring source guards.
    // Prepared bytes are hashed and discarded; downloads rebuild the same bytes.
    internal async Task<bool> PrepareNextAsync(CancellationToken token = default)
    {
        var job = store.TryClaim();
        if (job is null) { return false; }
        try
        {
            AccountDataExportProof proof;
            {
                using var prelude = await guards.AcquireAsync(job.Access, token);
                using var admission = await memory.AcquireAsync(prelude.Deadline, token);
                using var scope = prelude.AcquireShared(admission);
                var snapshot = memory.Capture(scope);
                var payload = AccountDataExportDocument.Build(job, scope, snapshot);
                try
                {
                    if (!memory.IsCurrent(scope, snapshot))
                    {
                        throw new CustomerDataAccessException(409, "memory_revision_mismatch");
                    }
                    scope.ValidateCurrent();
                    proof = new(scope.Inventory.Revision, snapshot.Revision, scope.KeepsakeRevision, payload.Sha256);
                }
                finally { CryptographicOperations.ZeroMemory(payload.Bytes); }
            }
            store.Complete(job.Id, proof);
        }
        catch (OperationCanceledException)
        {
            store.Fail(job.Id, AccountDataExportFailure.PreparationInterrupted);
            throw;
        }
        catch (CustomerDataAccessException error)
        {
            store.Fail(job.Id, error.Status switch
            {
                413 => AccountDataExportFailure.PayloadTooLarge,
                401 or 409 => AccountDataExportFailure.SourceChanged,
                _ => AccountDataExportFailure.InventoryUnavailable
            });
        }
        catch (AccountDataExportException error)
        {
            store.Fail(job.Id, error.Status == 413
                ? AccountDataExportFailure.PayloadTooLarge : AccountDataExportFailure.InternalFailure);
        }
        catch
        {
            store.Fail(job.Id, AccountDataExportFailure.InternalFailure);
        }
        return true;
    }

    internal async Task DownloadAsync(HttpContext http, string id, CancellationToken token = default)
    {
        var access = await authority.CaptureAsync(http, token);
        if (!await downloadSlots.WaitAsync(TimeSpan.FromMilliseconds(100), token))
        {
            throw new AccountDataExportException(429, "export_download_busy");
        }
        try { await DownloadCoreAsync(http, id, access, token); }
        finally { downloadSlots.Release(); }
    }

    private async Task DownloadCoreAsync(HttpContext http, string id, CustomerDataAccess access,
        CancellationToken token)
    {
        var job = Find(id, access);
        if (job.State != AccountDataExportState.Ready || job.Proof is null)
        {
            throw new AccountDataExportException(409, "account_export_not_ready");
        }
        if (job.DownloadExpiresAt is not { } expiry || clock.GetUtcNow() >= expiry)
        {
            store.Fail(id, AccountDataExportFailure.DownloadExpired);
            throw new AccountDataExportException(410, "export_download_expired");
        }

        var released = false;
        var uncertain = false;
        try
        {
            using var prelude = await guards.AcquireAsync(access, token);
            using var admission = await memory.AcquireAsync(prelude.Deadline, token);
            using var download = store.OpenDownloadGuard(id, access, prelude.Deadline);
            job = download.Job;
            if (job.State != AccountDataExportState.Ready || job.Proof is null)
            {
                throw new AccountDataExportException(409, "account_export_not_ready");
            }
            expiry = job.DownloadExpiresAt
                ?? throw new AccountDataExportException(410, "export_download_expired");
            using var scope = prelude.AcquireShared(admission);
            scope.ConstrainDownloadExpiry(expiry);
            var snapshot = memory.Capture(scope);
            if (!Matches(job, scope, snapshot))
            {
                throw new CustomerDataAccessException(409, "export_source_changed");
            }
            var payload = AccountDataExportDocument.Build(job, scope, snapshot);
            try
            {
                if (payload.Sha256 != job.Proof.PayloadSha256)
                {
                    throw new CustomerDataAccessException(409, "export_source_changed");
                }
                http.Response.ContentType = "application/json; charset=utf-8";
                http.Response.ContentLength = payload.Bytes.Length;
                http.Response.Headers.ContentDisposition = $"attachment; filename=account-data-{job.Id}.json";
                http.Response.Headers.CacheControl = "no-store";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                try
                {
                    memory.Release(scope, snapshot, payload.Bytes, job.Proof.PayloadSha256, http.Response.BodyWriter);
                    released = true;
                }
                catch (CustomerDataReleaseUncertainException)
                {
                    uncertain = true;
                    throw;
                }
            }
            finally { CryptographicOperations.ZeroMemory(payload.Bytes); }
        }
        catch when (released || uncertain || http.Response.HasStarted)
        {
            http.Abort();
            return;
        }
        catch (CustomerDataAccessException error) when (error.Status is 401 or 409)
        {
            store.Fail(id, AccountDataExportFailure.SourceChanged);
            throw;
        }
        catch (AccountDataExportException error) when (error.Status == 410)
        {
            store.Fail(id, AccountDataExportFailure.DownloadExpired);
            throw;
        }

        // Every thread-affine/shared/source guard has been released before I/O.
        try
        {
            using var flushDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            flushDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var flush = await http.Response.BodyWriter.FlushAsync(flushDeadline.Token);
            if (flush.IsCanceled) { http.Abort(); }
        }
        catch { http.Abort(); }
    }

    private AccountDataExportJob Find(string id, CustomerDataAccess access) => store.Find(id, access)
        ?? throw new AccountDataExportException(404, "account_export_unavailable");

    private static bool Matches(AccountDataExportJob job, CustomerDataGuardScope scope,
        CustomerMemoryDataSnapshot memory) => job.Proof is { } proof && memory.Complete
        && memory.InventoryRevision == scope.Inventory.Revision
        && proof.InventoryRevision == scope.Inventory.Revision && proof.MemoryRevision == memory.Revision
        && proof.KeepsakeRevision == scope.KeepsakeRevision;

    private static AccountDataExportView View(AccountDataExportJob job) => new(1, job.Id, job.State switch
    {
        AccountDataExportState.Requested => "requested",
        AccountDataExportState.Preparing => "preparing",
        AccountDataExportState.Ready => "ready",
        AccountDataExportState.Failed => "failed",
        _ => throw new InvalidOperationException("Invalid export state.")
    }, job.Reason, job.RequestedAt, job.ReadyAt, job.DownloadExpiresAt,
        job.State == AccountDataExportState.Ready ? $"/app/v1/customer/data/exports/{job.Id}/download" : null);
}

internal sealed class AccountDataExportWorker(AccountDataExportService exports,
    ILogger<AccountDataExportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try { worked = await exports.PrepareNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                logger.LogWarning("Account data export worker unavailable ({ErrorType}).", error.GetType().Name);
            }
            if (!worked)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            }
        }
    }
}

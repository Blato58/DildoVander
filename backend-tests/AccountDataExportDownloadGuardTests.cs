using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

internal static class AccountDataExportDownloadGuardTests
{
    public static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv10-export-download-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
            var store = new AccountDataExportStore(root, clock);
            var access = new CustomerDataAccess("owner", "session", "authority");
            var first = MakeReady(store, access);

            ExpectUnavailable(() => store.OpenDownloadGuard(first.Id,
                access with { AccountId = "another-owner" }, Deadline()), "another account cannot lease the download");
            ExpectUnavailable(() => store.OpenDownloadGuard(first.Id,
                access with { SessionId = "another-session" }, Deadline()), "another session cannot lease the download");
            ExpectUnavailable(() => store.OpenDownloadGuard(first.Id,
                access with { AuthorityRevision = "stale" }, Deadline()), "stale authority cannot lease the download");

            using var guard = store.OpenDownloadGuard(first.Id, access, Deadline());
            Check(guard.Job.Id == first.Id && guard.Job.State == AccountDataExportState.Ready
                && guard.Job.Proof?.PayloadSha256 == first.Proof?.PayloadSha256,
                "download guard returns the exact current ready proof while holding its SQLite write lease");

            using (var started = new ManualResetEventSlim())
            {
                var replacing = Task.Run(() =>
                {
                    started.Set();
                    return store.Create(access, Guid.NewGuid());
                });
                Check(started.Wait(TimeSpan.FromSeconds(2)), "replacement writer started");
                await Task.Delay(150);
                Check(!replacing.IsCompleted, "replacement waits while download proof is leased");
                guard.Dispose();
                var replacement = await replacing.WaitAsync(TimeSpan.FromSeconds(3));
                Check(replacement.Id != first.Id && replacement.State == AccountDataExportState.Requested,
                    "replacement proceeds after the download proof lease is released");
                ExpectUnavailable(() => store.OpenDownloadGuard(first.Id, access, Deadline()),
                    "a replaced job id cannot acquire a fresh download lease");

                var replacementReady = MakeReadyFromRequested(store, replacement);
                using var deleteGuard = store.OpenDownloadGuard(replacementReady.Id, access, Deadline());
                using var deleteStarted = new ManualResetEventSlim();
                var deleting = Task.Run(() =>
                {
                    deleteStarted.Set();
                    return store.DeleteForAccount(replacementReady.Id, access.AccountId);
                });
                Check(deleteStarted.Wait(TimeSpan.FromSeconds(2)), "delete writer started");
                await Task.Delay(150);
                Check(!deleting.IsCompleted, "explicit deletion waits while download proof is leased");
                deleteGuard.Dispose();
                Check(await deleting.WaitAsync(TimeSpan.FromSeconds(3)),
                    "explicit deletion proceeds after the download proof lease is released");
            }

            Check(store.Find(first.Id, access) is null,
                "a replaced download job remains unavailable after later deletion of its successor");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AccountDataExportJob MakeReady(AccountDataExportStore store, CustomerDataAccess access)
    {
        var job = store.Create(access, Guid.NewGuid());
        return MakeReadyFromRequested(store, job);
    }

    private static AccountDataExportJob MakeReadyFromRequested(AccountDataExportStore store,
        AccountDataExportJob job)
    {
        var claimed = store.TryClaim();
        Check(claimed?.Id == job.Id, "fixture claims the expected export job");
        Check(store.Complete(job.Id, new AccountDataExportProof("inventory", "memory", "keepsakes",
            new string('B', 64))), "fixture completes the expected export job");
        return store.Find(job.Id, job.Access)
            ?? throw new InvalidOperationException("Download guard fixture lost its ready job.");
    }

    private static CustomerDataGuardDeadline Deadline() => new(CancellationToken.None);

    private static void ExpectUnavailable(Action action, string message)
    {
        try { action(); }
        catch (AccountDataExportException error) when (error.Status == 404)
        {
            Check(true, message);
            return;
        }
        throw new InvalidOperationException("Account data export download guard test failed: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Account data export download guard test failed: " + message); }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

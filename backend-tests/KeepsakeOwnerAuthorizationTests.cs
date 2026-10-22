using Kufr.Backend.Features.Keepsakes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using SkiaSharp;

internal static class KeepsakeOwnerAuthorizationTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dv10-owner-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await RunInDirectoryAsync(directory);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task RunInDirectoryAsync(string directory)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "keepsake-owner.db"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "keepsake-owner-content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:PortraitEnabled"] = "true", ["Keepsake:Origin"] = "https://localhost"
        }).Build();
        var store = new KeepsakeStore(config, TimeProvider.System);
        var accountId = "account-owner-1";
        var accountSession = "account-session-1";
        var live = true;
        var owner = KeepsakeActor.ForAccount(accountId, accountSession, () => live);
        var issue = store.Issue(Snapshot(), new KeepsakeCustomerOwner(accountId, "creation-session"));

        Check(store.Authorize(issue.Id, owner, KeepsakeScope.Read)
            && store.Authorize(issue.Id, owner, KeepsakeScope.Write)
            && !store.Authorize(issue.Id, KeepsakeActor.ForAccount("other-account", "other-session", () => true), KeepsakeScope.Read),
            "only the immutable account owner receives owner authority");
        Check(store.Read(issue.Id, owner).ReadOnly == false, "the original account owner receives the private owner view");

        var recoveryKey = Guid.NewGuid().ToString("D");
        var recovery = store.CreateRecovery(issue.Id, owner, recoveryKey);
        Check(store.CreateRecovery(issue.Id, owner, recoveryKey).Token == recovery.Token,
            "owner link creation retains idempotent replay for the same live account session");

        var consentVersion = store.SetPortraitConsent(issue.Id, owner, true, "dv10-portrait-v1", Guid.NewGuid().ToString("D"));
        using (var photo = new RevokingReadStream(Png(), () => live = false))
        {
            try
            {
                await store.AcceptPhotoAsync(issue.Id, owner, photo, "image/png", Guid.NewGuid().ToString("D"), consentVersion);
                throw new InvalidOperationException("An account session revoked during photo upload must be rejected.");
            }
            catch (KeepsakeException exception) when (exception.Code == "invalid_session") { }
        }
        live = true;
        Check(store.Read(issue.Id, owner).Uploads.Count == 0,
            "photo upload rechecks the live account session after its asynchronous body read");

        var fenceAccount = "upload-fence-owner";
        var fenceActor = KeepsakeActor.ForAccount(fenceAccount, "upload-fence-session", () => true);
        var fenceIssue = store.Issue(Snapshot("upload-fence-device"), new KeepsakeCustomerOwner(fenceAccount, "created-here"));
        var fenceConsent = store.SetPortraitConsent(fenceIssue.Id, fenceActor, true, "dv10-portrait-v1", Guid.NewGuid().ToString("D"));
        using (var photo = new RevokingReadStream(Png(), () => store.FenceAccountDeletion(fenceAccount)))
        {
            try
            {
                await store.AcceptPhotoAsync(fenceIssue.Id, fenceActor, photo, "image/png", Guid.NewGuid().ToString("D"), fenceConsent);
                throw new InvalidOperationException("An account deletion fence during photo upload must reject the upload.");
            }
            catch (KeepsakeException exception) when (exception.Code == "account_deleted") { }
        }

        live = false;
        ExpectDenied(() => store.Read(issue.Id, owner));
        live = true;
        store.FenceAccountDeletion(accountId);
        ExpectDenied(() => store.Read(issue.Id, owner));

        var guestStore = new KeepsakeStore(config, TimeProvider.System);
        var guest = guestStore.Bootstrap();
        var guestIssue = guestStore.Issue(Snapshot("guest-device"));
        guestStore.Claim(guestStore.IssueClaim(guestIssue.Id, "guest-device").Token, guest.Token, Guid.NewGuid().ToString("D"));
        Check(guestStore.Read(guestIssue.Id, guest.Token).ReadOnly == false,
            "legacy guest owner sessions retain their existing grant behavior");

        var epochAccount = "epoch-owner";
        var epochActor = KeepsakeActor.ForAccount(epochAccount, "epoch-session", () => true);
        var epochIssue = store.Issue(Snapshot("epoch-device"), new KeepsakeCustomerOwner(epochAccount, "created-here"));
        store.RevokeAllAccess(epochIssue.Id, epochActor);
        Check(!store.Authorize(epochIssue.Id, epochActor, KeepsakeScope.Read),
            "revoke-all advances the customer-owner epoch and removes subsequent owner authority");

        var jobAccount = "job-owner";
        var jobActor = KeepsakeActor.ForAccount(jobAccount, "job-session", () => true);
        var jobConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "owner-job.db"),
            ["Keepsake:DataRoot"] = Path.Combine(directory, "owner-job-content"),
            ["Keepsake:Enabled"] = "true", ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:Origin"] = "https://localhost"
        }).Build();
        var jobStore = new KeepsakeStore(jobConfiguration, TimeProvider.System);
        var jobIssue = jobStore.Issue(Snapshot("job-device"), new KeepsakeCustomerOwner(jobAccount, "created-here"));
        _ = jobStore.EnqueueBase(jobIssue.Id, jobActor, new KeepsakeBrief(), Guid.NewGuid().ToString("D"));
        var leasedJob = jobStore.LeaseNext() ?? throw new InvalidOperationException("Expected a queued owner generation job.");
        var provider = new FakeKeepsakeProvider();
        Check(leasedJob.KeepsakeId == jobIssue.Id, "the isolated provider job belongs to the intended original owner");
        var providerRequest = jobStore.Dispatch(leasedJob) ?? throw new InvalidOperationException("Expected a dispatched owner generation job.");
        var revision = jobStore.Read(jobIssue.Id, jobActor).Revision;
        jobStore.Delete(jobIssue.Id, jobActor, KeepsakeDeleteScope.All, Guid.NewGuid().ToString("D"), revision);
        var output = await provider.GenerateAsync(providerRequest, CancellationToken.None);
        Check(!jobStore.Complete(leasedJob, output),
            "a provider result cannot be ingested after account-owner content deletion");
        ExpectDenied(() => jobStore.Read(jobIssue.Id, jobActor));

        Console.WriteLine("Keepsake owner policy tests passed: immutable owner-only access, live-session checks, account deletion fence, and legacy guest compatibility.");
    }

    private static KeepsakeIssueSnapshot Snapshot(string deviceId = "owner-policy-device") => new(Guid.NewGuid().ToString("D"), deviceId, 1,
        "fixture-wand", "Synthetic owner fixture", "Synthetic catalog facts", "No physical evidence is evaluated.");

    private static byte[] Png()
    {
        using var bitmap = new SKBitmap(8, 8);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static void ExpectDenied(Action action)
    {
        try { action(); }
        catch (KeepsakeException exception) when (exception.Status is 401 or 403 or 404) { return; }
        throw new InvalidOperationException("Expected account keepsake access to be denied.");
    }

    private sealed class RevokingReadStream(byte[] content, Action revoke) : MemoryStream(content)
    {
        private bool revoked;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.ReadAsync(buffer, cancellationToken);
            if (!revoked)
            {
                revoked = true;
                revoke();
            }
            return read;
        }
    }
}

using Kufr.Backend.Features.CustomerData;
using Microsoft.Data.Sqlite;

internal static class AccountDataExportStoreTests
{
    public static Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv10-export-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
            var store = new AccountDataExportStore(root, clock);
            VerifySchema(Path.Combine(root, "account-data-export.sqlite"));
            VerifyRequestLedgerSchema(Path.Combine(root, "account-data-export.sqlite"));

            var access = Access("owner-one", "session-one", "authority-one");
            var requestId = Guid.NewGuid();
            var created = store.Create(access, requestId);
            Check(created.State == AccountDataExportState.Requested && created.Proof is null,
                "new export starts requested without payload content");
            var replay = store.Create(access, requestId);
            Check(replay.Id == created.Id && replay.RequestedAt == created.RequestedAt,
                "same request key and exact authority replay the same job");
            ExpectExportFailure(() => store.Create(access with { SessionId = "other-session" }, requestId), 409,
                "request key cannot be replayed with different authority");

            var claimed = store.TryClaim();
            Check(claimed?.Id == created.Id && claimed.State == AccountDataExportState.Preparing,
                "oldest requested job is atomically claimed as preparing");
            Check(store.TryClaim() is null, "preparing job is not claimed twice");
            var proof = Proof("inv-proof-one", "memory-proof-one", "keepsake-proof-one", new string('A', 64));
            Check(store.Complete(created.Id, proof), "current preparing job accepts its bounded proof");
            var ready = store.Find(created.Id, access)
                ?? throw new InvalidOperationException("Account data export store test failed: ready job disappeared.");
            Check(ready is { State: AccountDataExportState.Ready } && ready.Proof == proof,
                "ready job returns the exact revision and payload hash proof");
            Check(ready.ReadyAt == clock.GetUtcNow() && ready.DownloadExpiresAt == clock.GetUtcNow().AddHours(1),
                "ready job timestamps and one-hour expiry use the injected clock");
            var readyReplay = store.Create(access, requestId);
            Check(readyReplay.Id == ready.Id && readyReplay.State == AccountDataExportState.Ready
                && readyReplay.Proof == proof,
                "exact current request replay preserves its ready proof");

            Check(store.Find(created.Id, access with { AccountId = "other-account" }) is null,
                "another account cannot find an export job");
            Check(store.Find(created.Id, access with { SessionId = "other-session" }) is null,
                "another account session cannot find an export job");
            Check(store.Find(created.Id, access with { AuthorityRevision = "other-revision" }) is null,
                "changed authority proof cannot find an export job");

            clock.Advance(TimeSpan.FromHours(2));
            var expired = store.Find(created.Id, access);
            Check(expired is { State: AccountDataExportState.Ready, Proof: not null }
                && expired.DownloadExpiresAt < clock.GetUtcNow(),
                "expired proof metadata remains available until explicit deletion");
            Check(!store.DeleteForAccount(created.Id, "other-account"), "another account cannot delete the job");
            Check(store.Find(created.Id, access) is not null && store.DeleteForAccount(created.Id, access.AccountId)
                && store.Find(created.Id, access) is null, "owner can explicitly delete its expired job metadata");
            ExpectExportFailure(() => store.Create(access, requestId), 409,
                "technical job deletion retains the accepted request tombstone");

            var oldPreparing = store.Create(access, Guid.NewGuid());
            Check(store.TryClaim()?.Id == oldPreparing.Id, "replacement scenario job can be claimed");
            var replacement = store.Create(access, Guid.NewGuid());
            Check(replacement.Id != oldPreparing.Id && replacement.State == AccountDataExportState.Requested,
                "new request atomically replaces a preparing account job with a new generation id");
            Check(store.Create(access, replacement.RequestId).Id == replacement.Id,
                "current request remains exactly replayable after replacement");
            ExpectExportFailure(() => store.Create(access, oldPreparing.RequestId), 409,
                "an accepted request cannot recreate a superseded job");
            ExpectExportFailure(() => store.Create(access with { AuthorityRevision = "new-authority" }, replacement.RequestId), 409,
                "the current accepted request cannot replay under changed authority");
            Check(!store.Complete(oldPreparing.Id, proof)
                && !store.Fail(oldPreparing.Id, AccountDataExportFailure.SourceChanged)
                && store.Find(oldPreparing.Id, access) is null,
                "old worker completion or failure cannot affect or find a replacement job");
            var replacementClaim = store.TryClaim();
            Check(replacementClaim?.Id == replacement.Id, "replacement remains claimable");
            Check(store.Complete(replacement.Id, proof), "replacement generation completes independently");
            Check(store.Fail(replacement.Id, AccountDataExportFailure.SourceChanged),
                "source invalidation can revoke an already-ready download proof");
            Check(store.Find(replacement.Id, replacement.Access) is
                    { State: AccountDataExportState.Failed, Reason: "source_changed", Proof: null },
                "ready source invalidation clears the downloadable proof");

            var readyToExpire = store.Create(Access("owner-expiry", "session-expiry", "authority-expiry"), Guid.NewGuid());
            Check(store.TryClaim()?.Id == readyToExpire.Id && store.Complete(readyToExpire.Id, proof),
                "expiry scenario job becomes ready with proof");
            Check(store.Fail(readyToExpire.Id, AccountDataExportFailure.DownloadExpired),
                "download expiry can revoke an already-ready proof");
            Check(store.Find(readyToExpire.Id, readyToExpire.Access) is
                    { State: AccountDataExportState.Failed, Reason: "download_expired", Proof: null },
                "expired ready proof is cleared without pruning job metadata");

            var interrupted = store.Create(Access("owner-restart", "session-restart", "authority-restart"), Guid.NewGuid());
            Check(store.TryClaim()?.Id == interrupted.Id, "restart scenario job becomes preparing");
            DeleteRequest(Path.Combine(root, "account-data-export.sqlite"), interrupted.Access.AccountId,
                interrupted.RequestId);
            var restarted = new AccountDataExportStore(root, clock);
            var failedAfterRestart = restarted.Find(interrupted.Id,
                Access("owner-restart", "session-restart", "authority-restart"));
            Check(failedAfterRestart is { State: AccountDataExportState.Failed, Reason: "preparation_interrupted", Proof: null },
                "startup fails interrupted preparations without retrying or retaining a proof");
            Check(restarted.Create(interrupted.Access, interrupted.RequestId).Id == interrupted.Id,
                "accepted request ledger survives store restart and keeps the current key replayable");

            var invalid = restarted.Create(Access("owner-invalid", "session-invalid", "authority-invalid"), Guid.NewGuid());
            Check(restarted.TryClaim()?.Id == invalid.Id, "invalid proof test job is claimed");
            ExpectExportFailure(() => restarted.Complete(invalid.Id,
                    proof with { PayloadSha256 = "not-a-sha256" }), 400,
                "invalid payload digest is rejected before job completion");
            ExpectExportFailure(() => restarted.Complete(invalid.Id,
                    proof with { InventoryRevision = new string('x', 257) }), 400,
                "oversized revision proof is rejected");
            Check(restarted.Find(invalid.Id, invalid.Access)?.State == AccountDataExportState.Preparing,
                "invalid proof does not change the job state");
            Check(restarted.Fail(invalid.Id, AccountDataExportFailure.SourceChanged),
                "typed failure can fail a current preparing job");
            var failed = restarted.Find(invalid.Id, invalid.Access);
            Check(failed is { State: AccountDataExportState.Failed, Reason: "source_changed", Proof: null },
                "failure reason is whitelisted and clears proof fields");

            var requestedFailure = restarted.Create(Access("owner-requested-failure", "session", "authority"), Guid.NewGuid());
            Check(restarted.Fail(requestedFailure.Id, AccountDataExportFailure.MemoryUnavailable),
                "typed failure can fail a current requested job before claim");
            Check(restarted.Find(requestedFailure.Id, requestedFailure.Access) is
                    { State: AccountDataExportState.Failed, Reason: "memory_unavailable", Proof: null },
                "requested-job failure uses the reason allowlist and retains no proof");

            VerifyGlobalLimit(root, clock);
            VerifyReceiptLimits(root, clock);
            VerifyAccountMetadataPurge(root, clock);
            return Task.CompletedTask;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void VerifyReceiptLimits(string parentRoot, MutableClock clock)
    {
        var root = Path.Combine(parentRoot, "receipt-limits");
        var limits = new AccountDataExportReceiptLimits(3, 4);
        var store = new AccountDataExportStore(root, clock, limits);
        var accountA = Access("receipt-account-a", "session-a", "authority-a");
        var accountB = Access("receipt-account-b", "session-b", "authority-b");
        var a1 = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();
        store.Create(accountA, a1);
        store.Create(accountA, a2);
        var currentA = store.Create(accountA, a3);
        ExpectExportFailure(() => store.Create(accountA, Guid.NewGuid()), 429,
            "per-account accepted-request limit rejects new keys without eviction");
        ExpectExportFailure(() => store.Create(accountA, a1), 409,
            "per-account cap does not evict an earlier accepted-key tombstone");
        Check(store.Create(accountA, a3).Id == currentA.Id,
            "the current accepted key still replays when the per-account cap is full");

        var b1 = Guid.NewGuid();
        var currentB = store.Create(accountB, b1);
        ExpectExportFailure(() => store.Create(accountB, Guid.NewGuid()), 429,
            "global accepted-request limit rejects a new key while account receipt capacity remains");
        Check(store.Create(accountB, b1).Id == currentB.Id,
            "exact accepted-key replay remains available at the global cap");
        Check(CountRequests(Path.Combine(root, "account-data-export.sqlite")) == 4,
            "receipt caps are enforced without inserting or deleting rows on rejection");
    }

    private static void VerifyAccountMetadataPurge(string parentRoot, MutableClock clock)
    {
        var root = Path.Combine(parentRoot, "account-purge");
        var store = new AccountDataExportStore(root, clock);
        var removedAccount = Access("purged-account", "old-session", "old-authority");
        var retainedAccount = Access("retained-account", "retained-session", "retained-authority");
        var purgeKey = Guid.NewGuid();
        var retainedKey = Guid.NewGuid();
        var retained = store.Create(retainedAccount, retainedKey);
        store.Create(removedAccount, purgeKey);

        // Production callers must make the original account's permanent
        // deletion fences durable before invoking this privileged cleanup seam.
        store.DeleteAccountMetadata(removedAccount.AccountId);
        store.DeleteAccountMetadata(removedAccount.AccountId);

        Check(store.Create(retainedAccount, retainedKey).Id == retained.Id
            && store.Find(retained.Id, retainedAccount) is not null,
            "account metadata purge is isolated from other account jobs and accepted keys");
        Check(!HasRequest(Path.Combine(root, "account-data-export.sqlite"), removedAccount.AccountId, purgeKey),
            "trusted account deletion purge removes that account's request tombstones");
        ExpectExportFailure(() => store.Create(removedAccount, purgeKey), 410,
            "permanent account fence rejects old accepted keys after metadata purge");
        ExpectExportFailure(() => store.Create(removedAccount, Guid.NewGuid()), 410,
            "permanent account fence rejects new requests after metadata purge");
        Check(HasFence(Path.Combine(root, "account-data-export.sqlite"), removedAccount.AccountId),
            "trusted purge retains a minimal permanent account tombstone");

        var leakedId = Guid.NewGuid().ToString("D");
        SeedFencedJob(Path.Combine(root, "account-data-export.sqlite"), removedAccount, leakedId);
        Check(store.TryClaim()?.Id != leakedId,
            "worker claim excludes any row for a permanently fenced account");
        var leakedProof = Proof("inventory", "memory", "keepsake", new string('A', 64));
        Check(!store.Complete(leakedId, leakedProof)
            && !store.Fail(leakedId, AccountDataExportFailure.InternalFailure),
            "late worker writes cannot update a fenced account even if a stray row exists");

        var restarted = new AccountDataExportStore(root, clock);
        Check(!ExportJobExists(Path.Combine(root, "account-data-export.sqlite"), leakedId)
            && !HasRequest(Path.Combine(root, "account-data-export.sqlite"), removedAccount.AccountId, purgeKey),
            "restart removes inconsistent fenced jobs before legacy request-ledger backfill");
        ExpectExportFailure(() => restarted.Create(removedAccount, Guid.NewGuid()), 410,
            "permanent account fence survives store restart");
        Check(restarted.Find(retained.Id, retainedAccount) is not null
            && restarted.Create(retainedAccount, retainedKey).Id == retained.Id,
            "restart preserves another account's job and accepted request");
    }

    private static void VerifyGlobalLimit(string parentRoot, MutableClock clock)
    {
        var root = Path.Combine(parentRoot, "global-limit");
        var store = new AccountDataExportStore(root, clock);
        AccountDataExportJob? firstJob = null;
        CustomerDataAccess? firstAccess = null;
        for (var index = 0; index < 128; index++)
        {
            var access = Access($"account-{index:D3}", "session", "authority");
            var job = store.Create(access, Guid.NewGuid());
            if (index == 0)
            {
                firstJob = job;
                firstAccess = access;
            }
        }

        ExpectExportFailure(() => store.Create(Access("account-overflow", "session", "authority"), Guid.NewGuid()),
            429, "global export queue rejects a new account after 128 rows");

        var originalFirstJob = firstJob ?? throw new InvalidOperationException("Global queue fixture did not create its first job.");
        var originalFirstAccess = firstAccess ?? throw new InvalidOperationException("Global queue fixture did not create its first access.");
        var replacement = store.Create(originalFirstAccess, Guid.NewGuid());
        Check(replacement.Id != originalFirstJob.Id && replacement.State == AccountDataExportState.Requested,
            "existing account may replace its job when the global queue is full");
        Check(store.Find(originalFirstJob.Id, originalFirstAccess) is null, "replacement invalidates the previous job id");
        Check(CountJobs(Path.Combine(root, "account-data-export.sqlite")) == 128,
            "per-account replacement preserves the global row bound");
    }

    private static void VerifySchema(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('account_data_export_jobs')";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, string Type)>();
        while (reader.Read()) { columns.Add((reader.GetString(1), reader.GetString(2))); }
        var names = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        Check(names.SetEquals([
            "account_id", "id", "session_id", "authority_revision", "request_id", "state", "reason",
            "requested_at", "ready_at", "download_expires_at", "inventory_revision", "memory_revision",
            "keepsake_revision", "payload_sha256"
        ]), "job schema stores only ownership, lifecycle, revision proofs, and a payload hash");
        Check(columns.All(column => column.Type != "BLOB")
            && columns.All(column => !column.Name.Contains("payload", StringComparison.Ordinal)
                || column.Name == "payload_sha256"),
            "job schema has no payload bytes or content columns");
    }

    private static void VerifyRequestLedgerSchema(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('account_data_export_requests')";
        using var reader = command.ExecuteReader();
        var columns = new List<(string Name, string Type)>();
        while (reader.Read()) { columns.Add((reader.GetString(1), reader.GetString(2))); }
        var names = columns.Select(column => column.Name).ToHashSet(StringComparer.Ordinal);
        Check(names.SetEquals(["account_id", "request_id", "session_id", "authority_revision", "job_id", "requested_at"]),
            "accepted-request ledger stores only bounded authority and lifecycle metadata");
        Check(columns.All(column => column.Type != "BLOB"),
            "accepted-request ledger never persists export bytes or source content");
    }

    private static int CountJobs(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM account_data_export_jobs";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static int CountRequests(string databasePath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM account_data_export_requests";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static bool HasRequest(string databasePath, string accountId, Guid requestId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM account_data_export_requests WHERE account_id=$account AND request_id=$request";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$request", requestId.ToString("N"));
        return command.ExecuteScalar() is not null;
    }

    private static bool HasFence(string databasePath, string accountId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM account_data_export_account_fences WHERE account_id=$account";
        command.Parameters.AddWithValue("$account", accountId);
        return command.ExecuteScalar() is not null;
    }

    private static bool ExportJobExists(string databasePath, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM account_data_export_jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private static void SeedFencedJob(string databasePath, CustomerDataAccess access, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO account_data_export_jobs(account_id,id,session_id,authority_revision,request_id,
                state,reason,requested_at,ready_at,download_expires_at,inventory_revision,memory_revision,
                keepsake_revision,payload_sha256)
            VALUES($account,$id,$session,$authority,$request,'preparing',NULL,$requested,NULL,NULL,NULL,NULL,NULL,NULL)
            """;
        command.Parameters.AddWithValue("$account", access.AccountId);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$session", access.SessionId);
        command.Parameters.AddWithValue("$authority", access.AuthorityRevision);
        command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("$requested", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.ExecuteNonQuery();
    }

    private static void DeleteRequest(string databasePath, string accountId, Guid requestId)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM account_data_export_requests WHERE account_id=$account AND request_id=$request";
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$request", requestId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static CustomerDataAccess Access(string account, string session, string authority) =>
        new(account, session, authority);

    private static AccountDataExportProof Proof(string inventory, string memory, string keepsake, string hash) =>
        new(inventory, memory, keepsake, hash);

    private static void ExpectExportFailure(Action action, int expectedStatus, string message)
    {
        try
        {
            action();
        }
        catch (AccountDataExportException error) when (error.Status == expectedStatus)
        {
            Check(true, message);
            return;
        }
        throw new InvalidOperationException("Account data export store test failed: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Account data export store test failed: " + message); }
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan duration) => current = current.Add(duration);
    }
}

using System.Security.Claims;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static class AccountDataExportAuthorityTests
{
    public static async Task RunAsync()
    {
        await using var fixture = await Fixture.CreateAsync();

        var owner = await fixture.AddAccountAsync();
        var access = await fixture.CaptureAsync(owner);
        Check(access.AccountId == owner.AccountId && access.SessionId == owner.SessionId,
            "capture binds the authenticated account and live session");
        Check(await fixture.Authority.IsCurrentAsync(access), "fresh captured authority is current");
        Check(await fixture.AcquireAndDisposeSharedAsync(access),
            "fake asynchronous admission resumes before final same-thread shared-scope acquisition and disposal");

        await ExpectAccessFailureAsync(() => fixture.CaptureAsync(owner with { AccountId = "another-account" }), 401,
            "a claim for the wrong account is rejected");
        await ExpectAccessFailureAsync(() => fixture.CaptureAsync(owner with { SessionId = "another-session" }), 401,
            "a claim for the wrong session is rejected");
        await ExpectAccessFailureAsync(() => fixture.CaptureAsync(owner with { Stamp = "wrong-stamp" }), 401,
            "a stale security stamp claim is rejected");

        var expiringJob = fixture.CreateReadyExport(access);
        var expiryPause = fixture.Admission.PauseNext();
        var pendingExpiryCheck = fixture.AcquireWithDownloadExpiryAsync(access, expiringJob.Id);
        await expiryPause.Started;
        fixture.Clock.Advance(TimeSpan.FromHours(2));
        expiryPause.Release();
        await ExpectAccessFailureAsync(() => pendingExpiryCheck, 410,
            "a download that expires while memory admission is pending is rejected before release");
        Check(fixture.Admission.LiveLeases == 0,
            "expired-download rejection releases memory admission and the shared database scope");

        var wrongAccount = access with { AccountId = "another-account" };
        var wrongSession = access with { SessionId = "another-session" };
        var wrongRevision = access with { AuthorityRevision = "tampered" };
        Check(!await fixture.Authority.IsCurrentAsync(wrongAccount), "captured authority cannot be transferred to another account");
        Check(!await fixture.Authority.IsCurrentAsync(wrongSession), "captured authority cannot be transferred to another session");
        Check(!await fixture.Authority.IsCurrentAsync(wrongRevision), "captured authority revision is checked");

        var firstSession = new SessionRow("historical-session", "profile-historical", 4, "device-history", "", "completed");
        await fixture.AddJournalSessionsAsync(owner.AccountId, [firstSession]);
        var historicalRows = Enumerable.Range(0, 150).Select(index => new SessionRow(
            $"session-{index:D3}", $"profile-{index:D3}", index + 1, "device-inventory", "snapshot", "completed")).ToArray();
        await fixture.AddJournalSessionsAsync(owner.AccountId, historicalRows);
        await fixture.AddCharacterTombstoneAsync(owner.AccountId, "profile-000");
        var inventory = await fixture.Authority.SnapshotAsync(access);
        Check(inventory.Complete && inventory.Sessions.Count == 151,
            "complete inventory includes more than one hundred historical sessions");
        Check(inventory.Sessions.Single(item => item.SessionId == "session-000").ProfileRevoked,
            "profile tombstone is merged into the account inventory");
        Check(!inventory.Sessions.Single(item => item.SessionId == "historical-session").SnapshotAvailable,
            "cleared session snapshot is represented without its content");
        Check(await fixture.Authority.IsCurrentAsync(inventory), "untampered inventory is current");
        Check(!await fixture.Authority.IsCurrentAsync(inventory with { Revision = "tampered" }),
            "inventory revision tampering is rejected");
        Check(!await fixture.Authority.IsCurrentAsync(inventory with { Sessions = Array.Empty<CustomerOwnedSession>() }),
            "inventory row tampering is rejected");

        var keptRows = await fixture.AddKeepsakeMetadataAsync(owner.AccountId, 150);
        var staleKeepsake = await fixture.AddKeepsakeMetadataAsync(owner.AccountId, 1, staleEpoch: true);
        var deletedKeepsake = await fixture.AddKeepsakeMetadataAsync(owner.AccountId, 1, deleted: true);
        var keepProof = await fixture.ReadSharedScopeAsync(access);
        Check(keepProof.Metadata.Count == 150 && keepProof.Metadata.All(item => keptRows.Contains(item.Id)),
            "keepsake metadata inventory is complete beyond one hundred rows and excludes stale or deleted owners");
        Check(keepProof.Metadata.All(item => item.Id != staleKeepsake[0] && item.Id != deletedKeepsake[0]),
            "stale owner epochs and deleted keepsakes are excluded");
        Check(keepProof.Metadata[0].DocumentRevision == 1, "metadata contains only the serialized document revision");

        var linkedViewer = await fixture.AddAccountAsync();
        await fixture.AddArchiveAssociationAsync(linkedViewer.AccountId, keptRows[0]);
        var viewerProof = await fixture.ReadSharedScopeAsync(await fixture.CaptureAsync(linkedViewer));
        Check(viewerProof.Metadata.Count == 0, "linked viewer receives no original-owner keepsake metadata");

        await fixture.UpdateKeepsakeDocumentRevisionAsync(keptRows[0], 2);
        var changedKeepsakeProof = await fixture.ReadSharedScopeAsync(access);
        Check(changedKeepsakeProof.Revision != keepProof.Revision,
            "keepsake metadata revision proof changes when an owned document revision changes");

        await fixture.ClearKeepsakeOwnersAsync(owner.AccountId);
        await fixture.AddKeepsakeMetadataAsync(owner.AccountId, CustomerDataLimits.MaxOwnedSessions + 1);
        await ExpectAccessFailureAsync(() => fixture.ReadSharedScopeAsync(access), 413,
            "keepsake metadata overflow fails closed instead of truncating");
        await fixture.ClearKeepsakeOwnersAsync(owner.AccountId);

        fixture.VerifySharedDatabaseAliases();
        await fixture.VerifyExportDatabaseAliasRejectedBeforeOpenAsync(owner);

        await fixture.ClearJournalSessionsAsync(owner.AccountId);
        var overflowRows = Enumerable.Range(0, CustomerDataLimits.MaxOwnedSessions + 1).Select(index => new SessionRow(
            $"overflow-{index:D4}", "overflow-profile", index, "device-overflow", "snapshot", "completed")).ToArray();
        await fixture.AddJournalSessionsAsync(owner.AccountId, overflowRows);
        await ExpectAccessFailureAsync(() => fixture.Authority.SnapshotAsync(access), 413,
            "inventory overflow fails closed instead of returning a truncated archive");

        await fixture.ClearJournalSessionsAsync(owner.AccountId);
        var changedStampAccess = await fixture.CaptureAsync(owner);
        await fixture.UpdateAccountAsync(owner.AccountId, user => user.SecurityStamp = "rotated-stamp");
        Check(!await fixture.Authority.IsCurrentAsync(changedStampAccess), "stamp rotation invalidates prior authority");

        var roleOwner = await fixture.AddAccountAsync();
        var roleAccess = await fixture.CaptureAsync(roleOwner);
        await fixture.AddRoleAsync(roleOwner.AccountId, "Administrator");
        Check(!await fixture.Authority.IsCurrentAsync(roleAccess), "role changes invalidate prior authority");

        var disabledOwner = await fixture.AddAccountAsync();
        await fixture.UpdateAccountAsync(disabledOwner.AccountId, user => user.Disabled = true);
        await ExpectAccessFailureAsync(() => fixture.CaptureAsync(disabledOwner), 401, "disabled account is rejected");

        var deletionOwner = await fixture.AddAccountAsync();
        var deletionAccess = await fixture.CaptureAsync(deletionOwner);
        await fixture.AddDeletionRequestAsync(deletionOwner.AccountId);
        Check(!await fixture.Authority.IsCurrentAsync(deletionAccess), "deletion request invalidates captured authority");
        await ExpectAccessFailureAsync(() => fixture.CaptureAsync(deletionOwner), 401,
            "account with a deletion request is rejected");

        var journalFenceOwner = await fixture.AddAccountAsync();
        var journalFenceAccess = await fixture.CaptureAsync(journalFenceOwner);
        await fixture.AddJournalRevocationAsync(journalFenceOwner.AccountId, "*");
        await ExpectAccessFailureAsync(() => fixture.Authority.SnapshotAsync(journalFenceAccess), 401,
            "journal account-wide revocation fence blocks inventory");

        var characterFenceOwner = await fixture.AddAccountAsync();
        var characterFenceAccess = await fixture.CaptureAsync(characterFenceOwner);
        await fixture.AddCharacterAccountFenceAsync(characterFenceOwner.AccountId);
        await ExpectAccessFailureAsync(() => fixture.Authority.SnapshotAsync(characterFenceAccess), 401,
            "character account-wide fence blocks inventory");

        var characterTombstoneOwner = await fixture.AddAccountAsync();
        var characterTombstoneAccess = await fixture.CaptureAsync(characterTombstoneOwner);
        await fixture.AddCharacterTombstoneAsync(characterTombstoneOwner.AccountId, "*");
        await ExpectAccessFailureAsync(() => fixture.Authority.SnapshotAsync(characterTombstoneAccess), 401,
            "character wildcard deletion tombstone blocks inventory");

        var expiredOwner = await fixture.AddAccountAsync();
        var expiringAccess = await fixture.CaptureAsync(expiredOwner);
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        Check(!await fixture.Authority.IsCurrentAsync(expiringAccess), "expired session invalidates captured authority");
        await ExpectAccessFailureAsync(() => fixture.Authority.CaptureAsync(fixture.Context(expiredOwner),
            new CancellationToken(canceled: true)), 0, "cancelled capture observes cancellation");

        var canceledWaitOwner = await fixture.AddAccountAsync();
        var canceledWaitAccess = await fixture.CaptureAsync(canceledWaitOwner);
        var canceledPause = fixture.Admission.PauseNext();
        using (var cancellation = new CancellationTokenSource())
        {
            var waiting = fixture.Authority.SnapshotAsync(canceledWaitAccess, cancellation.Token);
            await canceledPause.Started.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            await ExpectAccessFailureAsync(() => waiting, 0, "cancellation while awaiting memory admission is observed");
        }
        Check(fixture.Admission.LiveLeases == 0, "cancelled memory admission leaves no lease");

        var expiredWaitOwner = await fixture.AddAccountAsync();
        var expiredWaitAccess = await fixture.CaptureAsync(expiredWaitOwner);
        var expiredPause = fixture.Admission.PauseNext();
        var expiredWaiting = fixture.Authority.SnapshotAsync(expiredWaitAccess);
        await expiredPause.Started.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        expiredPause.Release();
        await ExpectAccessFailureAsync(() => expiredWaiting, 401,
            "session expiry while waiting for memory admission prevents shared-scope acquisition");
        Check(fixture.Admission.LiveLeases == 0, "expired admission lease is released after shared-scope rejection");

        var heldOwner = await fixture.AddAccountAsync();
        var heldAccess = await fixture.CaptureAsync(heldOwner);
        var prelude = await fixture.Authority.AcquireAsync(heldAccess);
        var lease = await fixture.Admission.AcquireAsync(prelude.Deadline);
        var held = prelude.AcquireShared(lease);
        Task writer = Task.CompletedTask;
        var writerStarted = false;
        var writerWasBlocked = false;
        try
        {
            using (var started = new ManualResetEventSlim())
            {
                writer = Task.Run(() =>
                {
                    started.Set();
                    fixture.Keepsakes.FenceAccountDeletion(heldOwner.AccountId);
                });
                writerStarted = started.Wait(TimeSpan.FromSeconds(2));
                writerWasBlocked = writerStarted && !writer.Wait(TimeSpan.FromMilliseconds(150));
            }
        }
        finally
        {
            held.Dispose();
            lease.Dispose();
            prelude.Dispose();
        }
        Check(writerStarted, "keepsake deletion writer started");
        Check(writerWasBlocked, "held final scope blocks the real Keepsake account-deletion writer");
        await writer.WaitAsync(TimeSpan.FromSeconds(3));
        Check(!await fixture.Authority.IsCurrentAsync(heldAccess),
            "keepsake deletion writer commits after final-scope release and fences authority");

        var fenceFirstOwner = await fixture.AddAccountAsync();
        var fenceFirstAccess = await fixture.CaptureAsync(fenceFirstOwner);
        fixture.Keepsakes.FenceAccountDeletion(fenceFirstOwner.AccountId);
        await ExpectAccessFailureAsync(() => fixture.Authority.SnapshotAsync(fenceFirstAccess), 401,
            "keepsake deletion completed before acquisition rejects the shared scope");

        await fixture.BusyAccountDatabaseIsUnavailableAsync(owner);
    }

    private static async Task ExpectAccessFailureAsync(Func<Task> action, int expectedStatus, string message)
    {
        try
        {
            await action();
        }
        catch (CustomerDataAccessException error) when (error.Status == expectedStatus)
        {
            Check(true, message);
            return;
        }
        catch (AccountDataExportException error) when (error.Status == expectedStatus)
        {
            Check(true, message);
            return;
        }
        catch (OperationCanceledException) when (expectedStatus == 0)
        {
            Check(true, message);
            return;
        }
        throw new InvalidOperationException("Account data authority test failed: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Account data authority test failed: " + message); }
    }

    private sealed record PrincipalClaims(string AccountId, string SessionId, string Stamp);
    private sealed record SessionRow(string Id, string ProfileId, long ProfileRevision, string DeviceId,
        string Snapshot, string State);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly string accountPath;
        private readonly string sharedPath;
        private readonly string characterPath;
        private readonly ServiceProvider services;
        private readonly OperationJournal journal;
        private readonly CharacterProfileStore profiles;
        private readonly KeepsakeStore keepsakes;
        private readonly FakeMemoryAdmission admission;
        private readonly AccountDataExportStore exportJobs;

        internal MutableClock Clock { get; }
        internal AccountDataExportAuthority Authority { get; }
        internal FakeMemoryAdmission Admission => admission;
        internal KeepsakeStore Keepsakes => keepsakes;

        private Fixture(string root, string accountPath, string sharedPath, string characterPath,
            ServiceProvider services, OperationJournal journal, KeepsakeStore keepsakes,
            CharacterProfileStore profiles, FakeMemoryAdmission admission, AccountDataExportStore exportJobs,
            MutableClock clock)
        {
            this.root = root;
            this.accountPath = accountPath;
            this.sharedPath = sharedPath;
            this.characterPath = characterPath;
            this.services = services;
            this.journal = journal;
            this.profiles = profiles;
            this.keepsakes = keepsakes;
            this.admission = admission;
            this.exportJobs = exportJobs;
            Clock = clock;
            Authority = new AccountDataExportAuthority(services.GetRequiredService<IServiceScopeFactory>(),
                journal, profiles, keepsakes, admission, clock, services.GetRequiredService<IOptions<IdentityOptions>>(),
                exportJobs);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "dv10-account-export-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var accountPath = Path.Combine(root, "identity.sqlite");
            var sharedPath = Path.Combine(root, "journal-and-keepsakes.sqlite");
            var accountConnection = new SqliteConnectionStringBuilder { DataSource = accountPath }.ToString();
            var services = new ServiceCollection();
            services.Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
            services.AddDbContext<AccountDatabase>(options => options.UseSqlite(accountConnection));
            var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
                await database.Database.EnsureCreatedAsync();
            }
            var clock = new MutableClock(DateTimeOffset.FromUnixTimeSeconds(2_000_000_000));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = sharedPath,
                ["Keepsake:DataRoot"] = Path.Combine(root, "keepsake-files"),
                ["Keepsake:DataProtectionRoot"] = Path.Combine(root, "protection-keys")
            }).Build();
            var journal = new OperationJournal(configuration, clock);
            var profiles = new CharacterProfileStore(Path.Combine(root, "profiles"));
            var keepsakes = new KeepsakeStore(configuration, clock);
            var memoryPath = Path.Combine(root, "evening-memory.sqlite");
            await using (var memoryFile = File.Create(memoryPath)) { }
            var admission = new FakeMemoryAdmission(memoryPath);
            var exportJobs = new AccountDataExportStore(Path.Combine(root, "export-jobs"), clock);
            var characterPath = Path.Combine(root, "profiles", "customer-characters.sqlite");
            return new Fixture(root, accountPath, sharedPath, characterPath, provider, journal,
                keepsakes, profiles, admission, exportJobs, clock);
        }

        internal DefaultHttpContext Context(PrincipalClaims claims)
        {
            var stampType = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, claims.AccountId),
                new Claim(AccountIdentity.SessionClaim, claims.SessionId),
                new Claim(stampType, claims.Stamp)
            ], "test");
            return new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        }

        internal async Task<PrincipalClaims> AddAccountAsync()
        {
            var userId = "user-" + Guid.NewGuid().ToString("N");
            var sessionId = "session-" + Guid.NewGuid().ToString("N");
            var stamp = "stamp-" + Guid.NewGuid().ToString("N");
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var role = await database.Roles.SingleOrDefaultAsync(item => item.Name == AccountRoles.Owner);
            if (role is null)
            {
                role = new IdentityRole { Id = Guid.NewGuid().ToString("N"), Name = AccountRoles.Owner, NormalizedName = AccountRoles.Owner.ToUpperInvariant() };
                database.Roles.Add(role);
                await database.SaveChangesAsync();
            }
            database.Users.Add(new AccountUser
            {
                Id = userId,
                UserName = userId,
                NormalizedUserName = userId.ToUpperInvariant(),
                SecurityStamp = stamp,
                DisplayName = "Authority test user",
                CreatedAt = Clock.GetUtcNow().ToUnixTimeSeconds()
            });
            database.Sessions.Add(new AccountSession
            {
                Id = sessionId,
                UserId = userId,
                ExpiresAt = Clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds(),
                AuthenticatedAt = Clock.GetUtcNow().ToUnixTimeSeconds(),
                LastSeenAt = Clock.GetUtcNow().ToUnixTimeSeconds()
            });
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
            await database.SaveChangesAsync();
            return new PrincipalClaims(userId, sessionId, stamp);
        }

        internal async Task<CustomerDataAccess> CaptureAsync(PrincipalClaims claims)
        {
            return await Authority.CaptureAsync(Context(claims));
        }

        internal async Task<(string Revision, IReadOnlyList<CustomerKeepsakeExportMetadata> Metadata)> ReadSharedScopeAsync(
            CustomerDataAccess access)
        {
            using var prelude = await Authority.AcquireAsync(access);
            using var lease = await admission.AcquireAsync(prelude.Deadline);
            await Task.Yield();
            using var held = prelude.AcquireShared(lease);
            held.ValidateCurrent();
            return (held.KeepsakeRevision, held.Keepsakes.ToArray());
        }

        internal async Task<bool> AcquireAndDisposeSharedAsync(CustomerDataAccess access)
        {
            _ = await ReadSharedScopeAsync(access);
            return admission.LiveLeases == 0;
        }

        internal AccountDataExportJob CreateReadyExport(CustomerDataAccess access)
        {
            var job = exportJobs.Create(access, Guid.NewGuid());
            var claimed = exportJobs.TryClaim()
                ?? throw new InvalidOperationException("Export fixture failed to claim its job.");
            Check(claimed.Id == job.Id, "download expiry fixture claims the correct job");
            Check(exportJobs.Complete(job.Id, new AccountDataExportProof("inventory", "memory", "keepsakes",
                new string('A', 64))), "download expiry fixture makes its job ready");
            return exportJobs.Find(job.Id, access)
                ?? throw new InvalidOperationException("Ready export fixture disappeared.");
        }

        internal async Task AcquireWithDownloadExpiryAsync(CustomerDataAccess access, string jobId)
        {
            using var prelude = await Authority.AcquireAsync(access);
            using var lease = await admission.AcquireAsync(prelude.Deadline);
            await Task.Yield();
            using var download = exportJobs.OpenDownloadGuard(jobId, access, prelude.Deadline);
            var expiry = download.Job.DownloadExpiresAt
                ?? throw new InvalidOperationException("Download expiry fixture job has no expiry.");
            using var scope = prelude.AcquireShared(lease);
            scope.ConstrainDownloadExpiry(expiry);
        }

        internal async Task<string[]> AddKeepsakeMetadataAsync(string owner, int count,
            bool staleEpoch = false, bool deleted = false)
        {
            var identifiers = Enumerable.Range(0, count)
                .Select(_ => Guid.NewGuid().ToString("D")).ToArray();
            await using var connection = new SqliteConnection(JournalConnectionString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var kept = connection.CreateCommand();
            kept.Transaction = (SqliteTransaction)transaction;
            kept.CommandText = """
                INSERT INTO dv10_keepsakes(id,outcome_key,device_id,epoch,photo_epoch,deleted,account_access_epoch,document)
                VALUES($id,$outcome,$device,11,4,$deleted,3,'{"revision":1}')
                """;
            var id = kept.Parameters.Add("$id", SqliteType.Text);
            var outcome = kept.Parameters.Add("$outcome", SqliteType.Text);
            var device = kept.Parameters.Add("$device", SqliteType.Text);
            var isDeleted = kept.Parameters.Add("$deleted", SqliteType.Integer);
            await using var association = connection.CreateCommand();
            association.Transaction = (SqliteTransaction)transaction;
            association.CommandText = """
                INSERT INTO dv10_customer_owners(keepsake_id,owner_id,session_id,epoch,linked_at)
                VALUES($id,$owner,$session,$epoch,$linked)
                """;
            var ownerKeep = association.Parameters.Add("$id", SqliteType.Text);
            var account = association.Parameters.Add("$owner", SqliteType.Text);
            var session = association.Parameters.Add("$session", SqliteType.Text);
            var ownerEpoch = association.Parameters.Add("$epoch", SqliteType.Integer);
            var linkedAt = association.Parameters.Add("$linked", SqliteType.Integer);
            foreach (var identifier in identifiers)
            {
                id.Value = identifier;
                outcome.Value = "outcome-" + Guid.NewGuid().ToString("N");
                device.Value = "device-keepsake-test";
                isDeleted.Value = deleted ? 1 : 0;
                await kept.ExecuteNonQueryAsync();
                ownerKeep.Value = identifier;
                account.Value = owner;
                session.Value = "test-session";
                ownerEpoch.Value = staleEpoch ? 2 : 3;
                linkedAt.Value = Clock.GetUtcNow().ToUnixTimeSeconds();
                await association.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return identifiers;
        }

        internal Task UpdateKeepsakeDocumentRevisionAsync(string id, int revision)
        {
            return ExecuteSqlAsync(JournalConnectionString(),
                "UPDATE dv10_keepsakes SET document=json_set(document,'$.revision',$revision) WHERE id=$id",
                ("$revision", revision), ("$id", id));
        }

        internal async Task ClearKeepsakeOwnersAsync(string owner)
        {
            await using var connection = new SqliteConnection(JournalConnectionString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM dv10_keepsakes WHERE id IN
                    (SELECT keepsake_id FROM dv10_customer_owners WHERE owner_id=$owner);
                DELETE FROM dv10_customer_owners WHERE owner_id=$owner;
                """;
            command.Parameters.AddWithValue("$owner", owner);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        internal async Task AddArchiveAssociationAsync(string viewerId, string locator)
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            database.ArchiveAssociations.Add(new AccountArchiveAssociation
            {
                UserId = viewerId,
                Locator = locator,
                Epoch = 3,
                LinkedAt = Clock.GetUtcNow().ToUnixTimeSeconds()
            });
            await database.SaveChangesAsync();
        }

        internal void VerifySharedDatabaseAliases()
        {
            var accountConnection = new SqliteConnectionStringBuilder { DataSource = accountPath }.ToString();
            CustomerDataDatabasePaths.Validate(accountConnection, characterPath, journal.CustomerDataDatabasePath,
                keepsakes.CustomerDataDatabasePath, admission.DatabasePath, exportJobs.DatabasePath);
            var originalLength = new FileInfo(sharedPath).Length;
            var alternatePath = Path.Combine(root, "alternate.sqlite");
            using (File.Create(alternatePath)) { }
            var invalidPaths = new[]
            {
                (new SqliteConnectionStringBuilder { DataSource = sharedPath }.ToString(), characterPath,
                    keepsakes.CustomerDataDatabasePath, admission.DatabasePath, exportJobs.DatabasePath),
                (accountConnection, sharedPath, keepsakes.CustomerDataDatabasePath, admission.DatabasePath, exportJobs.DatabasePath),
                (accountConnection, characterPath, keepsakes.CustomerDataDatabasePath, sharedPath, exportJobs.DatabasePath),
                (accountConnection, characterPath, alternatePath, admission.DatabasePath, exportJobs.DatabasePath),
                (new SqliteConnectionStringBuilder { DataSource = characterPath }.ToString(), characterPath,
                    keepsakes.CustomerDataDatabasePath, admission.DatabasePath, exportJobs.DatabasePath),
                (accountConnection, characterPath, keepsakes.CustomerDataDatabasePath, accountPath, exportJobs.DatabasePath),
                (accountConnection, characterPath, keepsakes.CustomerDataDatabasePath, characterPath, exportJobs.DatabasePath),
                (accountConnection, characterPath, keepsakes.CustomerDataDatabasePath, admission.DatabasePath, accountPath),
                (accountConnection, characterPath, keepsakes.CustomerDataDatabasePath, admission.DatabasePath, characterPath),
                (accountConnection, characterPath, keepsakes.CustomerDataDatabasePath, admission.DatabasePath, sharedPath)
            };
            foreach (var (account, character, keepsakePath, memory, exportPath) in invalidPaths)
            {
                try
                {
                    CustomerDataDatabasePaths.Validate(account, character, journal.CustomerDataDatabasePath,
                        keepsakePath, memory, exportPath);
                }
                catch (CustomerDataAccessException error) when (error.Status == 503)
                {
                    continue;
                }
                throw new InvalidOperationException("Account data authority test failed: aliased stores must be rejected.");
            }
            Check(new FileInfo(sharedPath).Length == originalLength,
                "store alias validation rejects before writing to the shared database");
        }

        internal async Task VerifyExportDatabaseAliasRejectedBeforeOpenAsync(PrincipalClaims claims)
        {
            var accountConnection = new SqliteConnectionStringBuilder { DataSource = accountPath }.ToString();
            var originalLength = new FileInfo(accountPath).Length;
            await ExpectAccessFailureAsync(() => Task.Run(() =>
            {
                using var prelude = CustomerDataGuardPrelude.Capture(accountConnection, journal,
                    profiles, keepsakes, admission.DatabasePath,
                    sharedPath, Clock, claims.AccountId, claims.SessionId, claims.Stamp, CancellationToken.None);
            }), 503, "export job database alias is rejected before acquiring any customer-data guard");
            Check(new FileInfo(accountPath).Length == originalLength,
                "export database alias rejection occurs before opening the identity database");
        }

        internal async Task UpdateAccountAsync(string userId, Action<AccountUser> update)
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            update(await database.Users.SingleAsync(user => user.Id == userId));
            await database.SaveChangesAsync();
        }

        internal async Task AddRoleAsync(string userId, string roleName)
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var role = await database.Roles.SingleOrDefaultAsync(item => item.Name == roleName);
            if (role is null)
            {
                role = new IdentityRole { Id = Guid.NewGuid().ToString("N"), Name = roleName, NormalizedName = roleName.ToUpperInvariant() };
                database.Roles.Add(role);
            }
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = role.Id });
            await database.SaveChangesAsync();
        }

        internal async Task AddDeletionRequestAsync(string userId)
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            database.DeletionRequests.Add(new AccountDeletionRequest
            {
                UserId = userId,
                ReceiptId = Guid.NewGuid().ToString("N"),
                RequestedAt = Clock.GetUtcNow().ToUnixTimeSeconds()
            });
            await database.SaveChangesAsync();
        }

        internal async Task AddJournalSessionsAsync(string owner, IReadOnlyCollection<SessionRow> sessions)
        {
            await using var connection = new SqliteConnection(JournalConnectionString());
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO customer_sessions(id,owner_id,operation_id,device_id,boot_id,profile_id,
                    profile_revision,snapshot,state,conversation_id,end_requested,created_at,updated_at)
                VALUES($id,$owner,$operation,$device,'boot',$profile,$revision,$snapshot,$state,NULL,0,$created,$created)
                """;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var sessionOwner = command.Parameters.Add("$owner", SqliteType.Text);
            var operation = command.Parameters.Add("$operation", SqliteType.Text);
            var device = command.Parameters.Add("$device", SqliteType.Text);
            var profile = command.Parameters.Add("$profile", SqliteType.Text);
            var revision = command.Parameters.Add("$revision", SqliteType.Integer);
            var snapshot = command.Parameters.Add("$snapshot", SqliteType.Text);
            var state = command.Parameters.Add("$state", SqliteType.Text);
            var created = command.Parameters.Add("$created", SqliteType.Integer);
            foreach (var session in sessions)
            {
                id.Value = session.Id;
                sessionOwner.Value = owner;
                operation.Value = session.Id;
                device.Value = session.DeviceId;
                profile.Value = session.ProfileId;
                revision.Value = session.ProfileRevision;
                snapshot.Value = session.Snapshot;
                state.Value = session.State;
                created.Value = Clock.GetUtcNow().ToUnixTimeSeconds();
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        internal async Task ClearJournalSessionsAsync(string owner)
        {
            await using var connection = new SqliteConnection(JournalConnectionString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM customer_sessions WHERE owner_id=$owner";
            command.Parameters.AddWithValue("$owner", owner);
            await command.ExecuteNonQueryAsync();
        }

        internal Task AddJournalRevocationAsync(string owner, string profile)
        {
            return ExecuteSqlAsync(JournalConnectionString(),
                "INSERT OR IGNORE INTO customer_profile_revocations(owner_id,profile_id) VALUES($owner,$profile)",
                ("$owner", owner), ("$profile", profile));
        }

        internal Task AddCharacterTombstoneAsync(string owner, string profile)
        {
            return ExecuteSqlAsync(CharacterConnectionString(),
                "INSERT OR IGNORE INTO character_deletions(account_id,profile_id,completed) VALUES($owner,$profile,1)",
                ("$owner", owner), ("$profile", profile));
        }

        internal Task AddCharacterAccountFenceAsync(string owner)
        {
            return ExecuteSqlAsync(CharacterConnectionString(),
                "INSERT OR IGNORE INTO character_account_fences(account_id) VALUES($owner)", ("$owner", owner));
        }

        internal async Task BusyAccountDatabaseIsUnavailableAsync(PrincipalClaims claims)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = accountPath }.ToString());
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction(deferred: false);
            try
            {
                await ExpectAccessFailureAsync(() => Authority.CaptureAsync(Context(claims)), 503,
                    "busy account database returns unavailable rather than bypassing the guard");
            }
            finally
            {
                await transaction.RollbackAsync();
            }
        }

        private string JournalConnectionString()
        {
            return new SqliteConnectionStringBuilder { DataSource = sharedPath }.ToString();
        }

        private string CharacterConnectionString()
        {
            return new SqliteConnectionStringBuilder { DataSource = characterPath }.ToString();
        }

        private static async Task ExecuteSqlAsync(string connectionString, string sql,
            params (string Name, object Value)[] parameters)
        {
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) { command.Parameters.AddWithValue(name, value); }
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            journal.Dispose();
            await services.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FakeMemoryAdmission(string databasePath) : ICustomerDataMemoryAdmission
    {
        private AdmissionPause? nextPause;
        private int liveLeases;

        public string DatabasePath { get; } = databasePath;
        internal int LiveLeases => Volatile.Read(ref liveLeases);

        internal AdmissionPause PauseNext()
        {
            var pause = new AdmissionPause();
            if (Interlocked.CompareExchange(ref nextPause, pause, null) is not null)
            {
                throw new InvalidOperationException("Only one fake admission wait may be installed at a time.");
            }
            return pause;
        }

        public async Task<ICustomerDataMemoryLease> AcquireAsync(CustomerDataGuardDeadline deadline,
            CancellationToken token = default)
        {
            deadline.Check();
            var pause = Interlocked.Exchange(ref nextPause, null);
            if (pause is not null)
            {
                pause.SignalStarted();
                await pause.WaitAsync(token);
            }
            deadline.Check();
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref liveLeases);
            return new Lease(this);
        }

        private sealed class Lease(FakeMemoryAdmission admission) : ICustomerDataMemoryLease
        {
            private int disposed;

            public void ValidateCurrent()
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    throw new CustomerDataAccessException(401, "memory_admission_unavailable");
                }
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref admission.liveLeases);
                }
            }
        }
    }

    private sealed class AdmissionPause
    {
        private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Started => started.Task;
        internal void SignalStarted() => started.TrySetResult();
        internal void Release() => release.TrySetResult();
        internal Task WaitAsync(CancellationToken token) => release.Task.WaitAsync(token);
    }

    private sealed class MutableClock(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset now = current;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan duration) => now = now.Add(duration);
        internal void Set(DateTimeOffset value) => now = value;
    }
}

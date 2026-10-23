using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static class CustomerAdmissionTests
{
    internal static async Task RunAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await CookieAndProfileAuthorityAsync(fixture);
        await DelegatedAuthorityAsync(fixture);
        await DevicePolicyAsync(fixture);
        await SerializationAndUnwindAsync(fixture);
        await CommitExpiryAsync(fixture);
        await AccountIdentityTests.RunCustomerAdmissionHttpsAsync();
        Console.WriteLine("PASS customer admission: live SQLite account/session/stamp/deletion authority, held owned profiles, delegated ticket identity, device policy, serializable deletion/profile writes, cancellation/deadline/unwind, transactional expiry rollback, real HTTPS cookie authority");
    }

    private static async Task CookieAndProfileAuthorityAsync(Fixture fixture)
    {
        var owner = await fixture.AddAsync(AccountRoles.Customer);
        var profile = await fixture.Profiles.SaveAsync(owner.Account, new(0, new("Guest", "Story")));
        using (var held = fixture.Authority.AcquireCookie(fixture.Context(owner), profile.ProfileId, profile.Revision))
        {
            Check(held.Proof.AccountId == owner.Account && held.Proof.SessionId == owner.Session, "proof binds account and live session");
            var expected = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            {
                accountId = owner.Account, sessionId = owner.Session, stamp = owner.Stamp,
                expires = owner.Expires, authenticated = owner.Authenticated, roles = new[] { AccountRoles.Customer }
            })));
            Check(held.Proof.AuthorityRevision == expected, "export authority digest retains the original field names, order and numeric bytes");
            Check(held.CaptureOwnedProfile(profile.ProfileId, profile.Revision).UserInput == profile.UserInput, "capture uses held exact profile");
            Expect(409, () => held.ReadOwnedProfile(profile.ProfileId, profile.Revision + 1));
            Expect(409, () => held.ReadOwnedProfile(Guid.NewGuid().ToString("N")));
        }
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(owner with { Stamp = "stale" })));
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(owner with { Session = "missing" })));
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(owner with { Account = "missing" })));
        var expired = await fixture.AddAsync(AccountRoles.Customer, seconds: -1);
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(expired)));
        var stranger = await fixture.AddAsync(AccountRoles.Customer);
        Expect(409, () => fixture.Authority.AcquireCookie(fixture.Context(stranger), profile.ProfileId));
        await fixture.Profiles.DeleteAsync(owner.Account, profile.Revision, profile.ProfileId);
        using (var held = fixture.Authority.AcquireCookie(fixture.Context(owner)))
        {
            Check(held.IsProfileRevoked(profile.ProfileId), "deleted profile tombstone is visible under held character reservation");
            Expect(409, () => held.ReadOwnedProfile(profile.ProfileId));
        }
        await fixture.Profiles.FenceAccountDeletionAsync(stranger.Account);
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(stranger)));
        await fixture.ExecuteAsync("UPDATE AspNetUsers SET Disabled=1 WHERE Id=$id", owner.Account);
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(owner)));
        var deleting = await fixture.AddAsync(AccountRoles.Customer);
        await fixture.ExecuteAsync("INSERT INTO DeletionRequests(UserId,ReceiptId,RequestedAt,Attempts) VALUES($id,'receipt-' || $id,1,0)", deleting.Account);
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(deleting)));
        Check(fixture.Journal.ReadCustomerWaiting(deleting.Account, "device").Entry is null, "durable account deletion blocks admission before journal revocation runs");
    }

    private static async Task DelegatedAuthorityAsync(Fixture fixture)
    {
        var owner = await fixture.AddAsync(AccountRoles.Customer);
        var profile = await fixture.Profiles.SaveAsync(owner.Account, new(0, new("Guest", "Story")));
        var ticket = new CustomerTicketOwner(owner.Account, profile.ProfileId, profile.Revision, null);
        await fixture.ExecuteAsync("DELETE FROM Sessions WHERE UserId=$id", owner.Account);
        await fixture.ExecuteAsync("UPDATE AspNetUsers SET SecurityStamp='rotated' WHERE Id=$id", owner.Account);
        using (var held = fixture.Authority.AcquireDelegated(ticket))
        {
            Check(held.Proof.SessionId is null && held.Proof.DelegatedProfileId == profile.ProfileId, "delegated physical identity has no cookie requirement");
            _ = held.CaptureOwnedProfile(ticket.ProfileId, ticket.Revision);
            Expect(401, () => held.RequireDevicePermission("device"));
        }
        var edited = await fixture.Profiles.SaveAsync(owner.Account, new(profile.Revision, new("Edited", "Story"), profile.ProfileId));
        using (var held = fixture.Authority.AcquireDelegated(ticket with { SessionId = "accepted-session" }))
        {
            Expect(409, () => held.ReadOwnedProfile(ticket.ProfileId, ticket.Revision));
            // The adapter re-reads accepted state under the guard and skips the old revision.
            Check(held.ReadOwnedProfile(edited.ProfileId).Revision == edited.Revision, "accepted replay guard can retain original durable session after profile edit");
        }
        await fixture.ExecuteAsync("INSERT INTO DeletionRequests(UserId,ReceiptId,RequestedAt,Attempts) VALUES($id,'receipt-' || $id,1,0)", owner.Account);
        Expect(401, () => fixture.Authority.AcquireDelegated(ticket));
    }

    private static async Task DevicePolicyAsync(Fixture fixture)
    {
        foreach (var role in new[] { AccountRoles.Owner, AccountRoles.Administrator, AccountRoles.Operator, AccountRoles.Viewer, AccountRoles.Customer })
        {
            var owner = await fixture.AddAsync(role);
            using (var held = fixture.Authority.AcquireCookie(fixture.Context(owner)))
            {
                if (role is AccountRoles.Owner or AccountRoles.Administrator) { held.RequireDevicePermission("device"); }
                else { Expect(403, () => held.RequireDevicePermission("device")); }
            }
            await fixture.GrantAsync(owner.Account, "read");
            using (var held = fixture.Authority.AcquireCookie(fixture.Context(owner)))
            {
                if (role is AccountRoles.Operator or AccountRoles.Viewer or AccountRoles.Customer) { Expect(403, () => held.RequireDevicePermission("device")); }
            }
            await fixture.GrantAsync(owner.Account, "operate");
            using (var held = fixture.Authority.AcquireCookie(fixture.Context(owner)))
            {
                if (role is AccountRoles.Owner or AccountRoles.Administrator or AccountRoles.Operator) { held.RequireDevicePermission("device"); }
                else { Expect(403, () => held.RequireDevicePermission("device")); }
                if (role == AccountRoles.Operator) { Expect(403, () => held.RequireDevicePermission("other-device")); }
            }
        }
        var managing = await fixture.AddAsync(AccountRoles.Operator);
        await fixture.GrantAsync(managing.Account, "manage");
        using var manageHeld = fixture.Authority.AcquireCookie(fixture.Context(managing));
        manageHeld.RequireDevicePermission("device");
    }

    private static async Task SerializationAndUnwindAsync(Fixture fixture)
    {
        var owner = await fixture.AddAsync(AccountRoles.Customer);
        var profile = await fixture.Profiles.SaveAsync(owner.Account, new(0, new("Guest", "Story")));
        var held = fixture.Authority.AcquireCookie(fixture.Context(owner), profile.ProfileId, profile.Revision);
        var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletion = Task.Run(async () =>
        {
            attempted.SetResult();
            await fixture.ExecuteAsync("INSERT INTO DeletionRequests(UserId,ReceiptId,RequestedAt,Attempts) VALUES($id,'receipt-' || $id,1,0)", owner.Account);
        });
        await attempted.Task;
        await Task.Delay(100);
        Check(!deletion.IsCompleted, "account deletion waits while admission owns the account reservation");
        held.ValidateCurrent();
        held.Dispose();
        await deletion;
        Expect(401, () => fixture.Authority.AcquireCookie(fixture.Context(owner)));

        var other = await fixture.AddAsync(AccountRoles.Customer);
        var otherProfile = await fixture.Profiles.SaveAsync(other.Account, new(0, new("Other", "Story")));
        using (var profileHeld = fixture.Authority.AcquireCookie(fixture.Context(other), otherProfile.ProfileId))
        {
            var profileAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var profileDelete = Task.Run(async () => { profileAttempted.SetResult(); await fixture.Profiles.DeleteAsync(other.Account, otherProfile.Revision, otherProfile.ProfileId); });
            await profileAttempted.Task;
            await Task.Delay(100);
            Check(!profileDelete.IsCompleted, "profile deletion waits behind exact held profile reservation");
            _ = profileHeld.CaptureOwnedProfile(otherProfile.ProfileId, otherProfile.Revision);
            profileHeld.Dispose();
            await profileDelete;
        }
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { fixture.Authority.AcquireCookie(fixture.Context(other), token: canceled.Token); throw new Exception("canceled admission was accepted"); }
        catch (OperationCanceledException) { }
        using (var characterBlock = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Profiles.CustomerDataDatabasePath }.ToString()))
        {
            characterBlock.Open();
            using var reservation = characterBlock.BeginTransaction(deferred: false);
            var timer = Stopwatch.StartNew();
            Expect(503, () => fixture.Authority.AcquireCookie(fixture.Context(other)));
            Check(timer.Elapsed < TimeSpan.FromSeconds(3), "SQLite contention is bounded");
            await fixture.ExecuteAsync("UPDATE Sessions SET LastSeenAt=LastSeenAt+1 WHERE UserId=$id", other.Account);
        }
        fixture.Authority.ValidateDatabasePaths(Path.Combine(fixture.Root, "extra.sqlite"));
        Expect(503, () => fixture.Authority.ValidateDatabasePaths(fixture.Profiles.CustomerDataDatabasePath));
        Expect(503, () => CustomerAdmissionDatabasePaths.Validate(fixture.ConnectionString, fixture.AccountPath));
        using (var deadlineHeld = fixture.Authority.AcquireCookie(fixture.Context(other)))
        {
            fixture.Clock.Advance(TimeSpan.FromDays(-1));
            await Task.Delay(TimeSpan.FromMilliseconds(5100));
            Expect(503, deadlineHeld.ValidateCurrent);
            fixture.Clock.Advance(TimeSpan.FromDays(1));
        }
    }

    private static async Task CommitExpiryAsync(Fixture fixture)
    {
        var owner = await fixture.AddAsync(AccountRoles.Customer, seconds: 2);
        var profile = await fixture.Profiles.SaveAsync(owner.Account, new(0, new("Guest", "Story")));
        var waiting = fixture.Journal.OpenCustomerWaiting("device", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), activationRevision: 1);
        var request = new CustomerWaitingJoin(Guid.NewGuid().ToString("D"), waiting.Id, profile.ProfileId);
        using (var held = fixture.Authority.AcquireCookie(fixture.Context(owner), profile.ProfileId, profile.Revision))
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(3));
            Expect(401, () => fixture.Journal.JoinCustomerWaiting(owner.Account, "device", request, beforeCommit: held.ValidateCurrent));
        }
        Check(fixture.Journal.ReadCustomerWaiting(owner.Account, "device").Entry is null, "final live-session expiry rolls back entry and receipt");
        fixture.Clock.Advance(TimeSpan.FromSeconds(-3));
        using var fresh = fixture.Authority.AcquireCookie(fixture.Context(owner));
        Check(fixture.Journal.JoinCustomerWaiting(owner.Account, "device", request, beforeCommit: fresh.ValidateCurrent).Entry is not null,
            "rolled-back request key can be admitted once authority is valid");
    }

    private static void Expect(int status, Action action)
    {
        try { action(); throw new Exception("Expected admission denial " + status); }
        catch (CustomerDataAccessException error) when (error.Status == status) { }
    }

    private static void Check(bool value, string message) { if (!value) { throw new Exception(message); } }
    private sealed record Identity(string Account, string Session, string Stamp, long Expires, long Authenticated);
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan interval) => now += interval;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "customer-admission-" + Guid.NewGuid().ToString("N"));
        internal string AccountPath => Path.Combine(Root, "account.sqlite");
        internal string ConnectionString => new SqliteConnectionStringBuilder { DataSource = AccountPath, DefaultTimeout = 5 }.ToString();
        internal CharacterProfileStore Profiles { get; }
        internal CustomerAdmissionAuthority Authority { get; }
        internal OperationJournal Journal { get; }
        internal Clock Clock { get; } = new();
        private readonly ServiceProvider services;

        private Fixture()
        {
            Directory.CreateDirectory(Root);
            var collection = new ServiceCollection();
            collection.Configure<IdentityOptions>(options => options.Stores.SchemaVersion = IdentitySchemaVersions.Version3);
            collection.AddDbContext<AccountDatabase>(options => options.UseSqlite(ConnectionString));
            services = collection.BuildServiceProvider();
            Profiles = new CharacterProfileStore(Path.Combine(Root, "characters"));
            Authority = new(services.GetRequiredService<IServiceScopeFactory>(), Profiles, Clock, services.GetRequiredService<IOptions<IdentityOptions>>());
            Journal = new OperationJournal(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Backend:DatabasePath"] = Path.Combine(Root, "journal.sqlite") }).Build(), Clock);
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await using var scope = fixture.services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().Database.EnsureCreatedAsync();
            return fixture;
        }

        internal DefaultHttpContext Context(Identity identity)
        {
            var stamp = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
            return new() { User = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, identity.Account), new(AccountIdentity.SessionClaim, identity.Session), new(stamp, identity.Stamp)
            ], "test")) };
        }

        internal async Task<Identity> AddAsync(string roleName, int seconds = 3600)
        {
            var id = Guid.NewGuid().ToString("N"); var session = Guid.NewGuid().ToString("N"); var stamp = Guid.NewGuid().ToString("N");
            var now = Clock.GetUtcNow().ToUnixTimeSeconds();
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            var role = await database.Roles.SingleOrDefaultAsync(row => row.Name == roleName);
            if (role is null) { role = new IdentityRole { Id = Guid.NewGuid().ToString("N"), Name = roleName, NormalizedName = roleName.ToUpperInvariant() }; database.Roles.Add(role); }
            database.Users.Add(new AccountUser { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), SecurityStamp = stamp, DisplayName = "Test guest", CreatedAt = now });
            database.Sessions.Add(new AccountSession { Id = session, UserId = id, AuthenticatedAt = now, ExpiresAt = now + seconds, LastSeenAt = now });
            database.UserRoles.Add(new IdentityUserRole<string> { UserId = id, RoleId = role.Id });
            await database.SaveChangesAsync();
            return new(id, session, stamp, now + seconds, now);
        }

        internal async Task GrantAsync(string owner, string permission)
        {
            await using var scope = services.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            database.ResourceGrants.Add(new AccountResourceGrant { Id = Guid.NewGuid().ToString("N"), UserId = owner, ResourceType = "device", ResourceId = "device", Permission = permission });
            await database.SaveChangesAsync();
        }

        internal async Task ExecuteAsync(string sql, string id)
        {
            await using var connection = new SqliteConnection(ConnectionString); await connection.OpenAsync();
            await using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Journal.Dispose(); await services.DisposeAsync(); SqliteConnection.ClearAllPools(); Directory.Delete(Root, true);
        }
    }
}

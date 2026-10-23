using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kufr.Backend.Features.CustomerData;

internal interface ICustomerDataGuardAuthority
{
    Task<CustomerDataGuardPrelude> AcquireAsync(CustomerDataAccess access, CancellationToken token = default);
}

internal sealed class AccountDataExportAuthority(IServiceScopeFactory scopes, OperationJournal journal,
    CharacterProfileStore profiles, KeepsakeStore keepsakes, ICustomerDataMemoryAdmission memoryAdmission,
    TimeProvider clock, IOptions<IdentityOptions> identityOptions, AccountDataExportStore exportJobs)
    : ICustomerDataAuthority, ICustomerDataGuardAuthority
{
    public Task<CustomerDataAccess> CaptureAsync(HttpContext context, CancellationToken token = default)
    {
        var account = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = context.User.FindFirstValue(AccountIdentity.SessionClaim);
        var stamp = context.User.FindFirstValue(identityOptions.Value.ClaimsIdentity.SecurityStampClaimType);
        if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(account)
            || string.IsNullOrEmpty(session) || string.IsNullOrEmpty(stamp))
        {
            throw new CustomerDataAccessException(401, "account_access_unavailable");
        }
        using var prelude = CustomerDataGuardPrelude.Capture(ConnectionString(), journal, profiles, keepsakes,
            memoryAdmission.DatabasePath, exportJobs.DatabasePath, clock, account, session, stamp, token);
        // Capturing authentication is not enumeration or content authority.
        // Even an over-limit account must be able to remove its job metadata.
        prelude.ValidateCurrent();
        return Task.FromResult(prelude.Access);
    }

    public Task<CustomerDataGuardPrelude> AcquireAsync(CustomerDataAccess access, CancellationToken token = default)
    {
        return Task.FromResult(CustomerDataGuardPrelude.Acquire(ConnectionString(), journal, profiles,
            keepsakes, memoryAdmission.DatabasePath, exportJobs.DatabasePath, clock, access, token));
    }

    public async Task<CustomerOwnershipInventory> SnapshotAsync(CustomerDataAccess access, CancellationToken token = default)
    {
        using var prelude = await AcquireAsync(access, token);
        using var admission = await memoryAdmission.AcquireAsync(prelude.Deadline, token);
        using var held = prelude.AcquireShared(admission);
        held.ValidateCurrent();
        return held.Inventory;
    }

    public async Task<bool> IsCurrentAsync(CustomerDataAccess access, CancellationToken token = default)
    {
        try { await SnapshotAsync(access, token); return true; }
        catch (CustomerDataAccessException error) when (error.Status is 401 or 409) { return false; }
    }

    public async Task<bool> IsCurrentAsync(CustomerOwnershipInventory inventory, CancellationToken token = default)
    {
        if (!inventory.Complete) { return false; }
        try
        {
            var current = await SnapshotAsync(inventory.Access, token);
            return current.Revision == inventory.Revision && current.Sessions.SequenceEqual(inventory.Sessions);
        }
        catch (CustomerDataAccessException error) when (error.Status is 401 or 409) { return false; }
    }

    private string ConnectionString()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AccountDatabase>().Database.GetConnectionString()
            ?? throw new CustomerDataAccessException(503, "customer_data_unavailable");
    }
}

// This prelude contains no thread-affine lock. Await memory admission first,
// then acquire and dispose the shared archive/journal scope synchronously.
internal sealed class CustomerDataGuardPrelude : IDisposable, IAsyncDisposable
{
    private readonly CustomerDataSqliteGuard account;
    private readonly CustomerDataSqliteGuard character;
    private readonly OperationJournal journal;
    private readonly CharacterProfileStore profiles;
    private readonly KeepsakeStore keepsakes;
    private readonly TimeProvider clock;
    private readonly long expiresAt;
    private readonly CustomerDataAccess access;
    private bool disposed;
    private bool sharedActive;

    internal CustomerDataGuardDeadline Deadline { get; }
    internal CustomerDataAccess Access => access;

    private CustomerDataGuardPrelude(CustomerDataSqliteGuard account, CustomerDataSqliteGuard character,
        OperationJournal journal, CharacterProfileStore profiles, KeepsakeStore keepsakes,
        CustomerDataGuardDeadline deadline, TimeProvider clock, long expiresAt, CustomerDataAccess access)
    {
        this.account = account;
        this.character = character;
        this.journal = journal;
        this.profiles = profiles;
        this.keepsakes = keepsakes;
        Deadline = deadline;
        this.clock = clock;
        this.expiresAt = expiresAt;
        this.access = access;
    }

    internal static CustomerDataGuardPrelude Capture(string connectionString, OperationJournal journal,
        CharacterProfileStore profiles, KeepsakeStore keepsakes, string memoryPath, string exportJobPath, TimeProvider clock,
        string accountId, string sessionId, string claimedStamp, CancellationToken token)
    {
        return Create(connectionString, journal, profiles, keepsakes, memoryPath, exportJobPath, clock,
            new CustomerDataAccess(accountId, sessionId, ""), claimedStamp, token);
    }

    internal static CustomerDataGuardPrelude Acquire(string connectionString, OperationJournal journal,
        CharacterProfileStore profiles, KeepsakeStore keepsakes, string memoryPath, string exportJobPath, TimeProvider clock,
        CustomerDataAccess access, CancellationToken token)
    {
        return Create(connectionString, journal, profiles, keepsakes, memoryPath, exportJobPath, clock, access, null, token);
    }

    private static CustomerDataGuardPrelude Create(string connectionString, OperationJournal journal,
        CharacterProfileStore profiles, KeepsakeStore keepsakes, string memoryPath, string exportJobPath, TimeProvider clock,
        CustomerDataAccess access, string? claimedStamp, CancellationToken token)
    {
        CustomerDataDatabasePaths.Validate(connectionString, profiles.CustomerDataDatabasePath,
            journal.CustomerDataDatabasePath, keepsakes.CustomerDataDatabasePath, memoryPath, exportJobPath);
        var deadline = new CustomerDataGuardDeadline(token);
        CustomerDataSqliteGuard? account = null;
        CustomerDataSqliteGuard? character = null;
        try
        {
            account = CustomerDataSqliteGuard.Acquire(connectionString, deadline);
            var proof = ReadAccount(account, access.AccountId, access.SessionId, clock);
            if (claimedStamp is not null ? proof.Stamp != claimedStamp : proof.Revision != access.AuthorityRevision)
            {
                throw new CustomerDataAccessException(401, "account_access_unavailable");
            }
            character = profiles.OpenCustomerDataGuard(deadline);
            profiles.ValidateCustomerDataAccount(character, access.AccountId);
            var currentAccess = new CustomerDataAccess(access.AccountId, access.SessionId, proof.Revision);
            var prelude = new CustomerDataGuardPrelude(account, character, journal, profiles, keepsakes,
                deadline, clock, proof.ExpiresAt, currentAccess);
            prelude.ValidateCurrent();
            return prelude;
        }
        catch
        {
            try { character?.Dispose(); }
            finally { account?.Dispose(); }
            throw;
        }
    }

    internal CustomerDataGuardScope AcquireShared(ICustomerDataMemoryLease memoryAdmission)
    {
        ValidateCurrent();
        memoryAdmission.ValidateCurrent();
        if (sharedActive) { throw new InvalidOperationException("A shared customer-data scope is already active."); }
        var shared = keepsakes.OpenCustomerDataExportGuard(Deadline);
        try
        {
            var sessions = journal.ReadCustomerDataSessions(shared.Database, access.AccountId, Deadline);
            var deletedProfiles = new Dictionary<string, bool>(StringComparer.Ordinal);
            for (var index = 0; index < sessions.Length; index++)
            {
                Deadline.Check();
                var session = sessions[index];
                if (!deletedProfiles.TryGetValue(session.ProfileId, out var deleted))
                {
                    deleted = profiles.CustomerDataProfileRevoked(character, access.AccountId, session.ProfileId);
                    deletedProfiles.Add(session.ProfileId, deleted);
                }
                sessions[index] = session with { ProfileRevoked = session.ProfileRevoked || deleted };
            }
            var revision = Digest(new { access, sessions });
            var inventory = new CustomerOwnershipInventory(access, Array.AsReadOnly(sessions), revision, true);
            var metadata = shared.ReadMetadata(access.AccountId, Deadline);
            ValidateCurrent();
            memoryAdmission.ValidateCurrent();
            sharedActive = true;
            return CustomerDataGuardScope.Create(this, memoryAdmission, shared, inventory, metadata, Digest(metadata));
        }
        catch { shared.Dispose(); throw; }
    }

    internal void ValidateCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Deadline.Check();
        if (expiresAt <= clock.GetUtcNow().ToUnixTimeSeconds())
        {
            throw new CustomerDataAccessException(401, "account_access_unavailable");
        }
    }

    internal void ReleaseShared() => sharedActive = false;

    internal void ValidateDownloadExpiry(DateTimeOffset expiresAt)
    {
        ValidateCurrent();
        if (clock.GetUtcNow() >= expiresAt)
        {
            throw new AccountDataExportException(410, "export_download_expired");
        }
    }

    private static (string Stamp, string Revision, long ExpiresAt) ReadAccount(CustomerDataSqliteGuard guard,
        string accountId, string sessionId, TimeProvider clock)
    {
        using var command = guard.Command("""
            SELECT u.SecurityStamp, s.ExpiresAt, s.AuthenticatedAt
            FROM AspNetUsers u JOIN Sessions s ON s.UserId=u.Id
            WHERE u.Id=$account AND s.Id=$session AND u.Disabled=0
                AND NOT EXISTS(SELECT 1 FROM DeletionRequests d WHERE d.UserId=u.Id)
            """);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId);
        string stamp;
        long expires;
        long authenticated;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(0))
            {
                throw new CustomerDataAccessException(401, "account_access_unavailable");
            }
            stamp = reader.GetString(0);
            expires = reader.GetInt64(1);
            authenticated = reader.GetInt64(2);
        }
        if (string.IsNullOrEmpty(stamp) || expires <= clock.GetUtcNow().ToUnixTimeSeconds())
        {
            throw new CustomerDataAccessException(401, "account_access_unavailable");
        }
        using var roles = guard.Command("""
            SELECT r.Name FROM AspNetUserRoles m JOIN AspNetRoles r ON r.Id=m.RoleId
            WHERE m.UserId=$account ORDER BY r.Name
            """);
        roles.Parameters.AddWithValue("$account", accountId);
        using var roleReader = roles.ExecuteReader();
        var names = new List<string>();
        while (roleReader.Read()) { names.Add(roleReader.GetString(0)); }
        return (stamp, Digest(new { accountId, sessionId, stamp, expires, authenticated, roles = names }), expires);
    }

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    public void Dispose()
    {
        if (disposed) { return; }
        if (sharedActive) { throw new InvalidOperationException("Dispose the shared customer-data scope first."); }
        disposed = true;
        try { character.Dispose(); }
        finally { account.Dispose(); }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

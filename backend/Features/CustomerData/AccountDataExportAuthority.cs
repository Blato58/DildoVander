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
    private readonly CustomerAdmissionGuard admission;
    private readonly OperationJournal journal;
    private readonly KeepsakeStore keepsakes;
    private readonly TimeProvider clock;
    private readonly CustomerDataAccess access;
    private bool disposed;
    private bool sharedActive;

    internal CustomerDataGuardDeadline Deadline { get; }
    internal CustomerDataAccess Access => access;

    private CustomerDataGuardPrelude(CustomerAdmissionGuard admission, OperationJournal journal,
        KeepsakeStore keepsakes, TimeProvider clock)
    {
        this.admission = admission;
        this.journal = journal;
        this.keepsakes = keepsakes;
        this.clock = clock;
        Deadline = admission.Deadline;
        access = new CustomerDataAccess(admission.Proof.AccountId, admission.Proof.SessionId!, admission.Proof.AuthorityRevision);
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
        // Keep the export's complete preflight before the neutral prefix opens
        // either connection. Waiting callers independently validate their extras.
        CustomerAdmissionDatabasePaths.Validate(connectionString, profiles.CustomerDataDatabasePath,
            journal.CustomerDataDatabasePath, memoryPath, exportJobPath);
        var admission = claimedStamp is not null
            ? CustomerAdmissionGuard.AcquireCookie(connectionString, profiles, clock, access.AccountId, access.SessionId, claimedStamp, token)
            : CustomerAdmissionGuard.AcquireCookieRevision(connectionString, profiles, clock, access.AccountId, access.SessionId, access.AuthorityRevision, token);
        try
        {
            var prelude = new CustomerDataGuardPrelude(admission, journal, keepsakes, clock);
            prelude.ValidateCurrent();
            return prelude;
        }
        catch { admission.Dispose(); throw; }
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
                    deleted = admission.IsProfileRevoked(session.ProfileId);
                    deletedProfiles.Add(session.ProfileId, deleted);
                }
                sessions[index] = session with { ProfileRevoked = session.ProfileRevoked || deleted };
            }
            var preferences = admission.ReadExportConversationPreferences();
            // Keep legacy proofs stable until an account explicitly saves preferences.
            var revision = preferences is null ? Digest(new { access, sessions })
                : Digest(new { access, sessions, preferences });
            var inventory = new CustomerOwnershipInventory(access, Array.AsReadOnly(sessions), revision, true, preferences);
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
        admission.ValidateCurrent();
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

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    public void Dispose()
    {
        if (disposed) { return; }
        if (sharedActive) { throw new InvalidOperationException("Dispose the shared customer-data scope first."); }
        disposed = true;
        admission.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}

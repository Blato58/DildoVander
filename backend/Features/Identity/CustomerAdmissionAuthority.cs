using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kufr.Backend.Features.Identity;

internal sealed record CustomerAdmissionProof(string AccountId, string? SessionId, string AuthorityRevision,
    IReadOnlyList<string> Roles, string? DelegatedProfileId = null, long? DelegatedProfileRevision = null);

// Identity/profile admission has no memory, archive, export-job or journal DI dependency.
internal sealed class CustomerAdmissionAuthority(IServiceScopeFactory scopes, CharacterProfileStore profiles,
    TimeProvider clock, IOptions<IdentityOptions> options)
{
    internal void ValidateDatabasePaths(params string[] additionalPaths)
        => CustomerAdmissionDatabasePaths.Validate(ConnectionString(), profiles.CustomerDataDatabasePath, additionalPaths);

    internal CustomerAdmissionGuard AcquireCookie(HttpContext context, string? profileId = null,
        long? expectedRevision = null, CancellationToken token = default)
    {
        var account = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = context.User.FindFirstValue(AccountIdentity.SessionClaim);
        var stamp = context.User.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        if (context.User.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(account)
            || string.IsNullOrEmpty(session) || string.IsNullOrEmpty(stamp))
        {
            throw new CustomerDataAccessException(401, "account_access_unavailable");
        }
        var guard = CustomerAdmissionGuard.AcquireCookie(ConnectionString(), profiles, clock, account, session, stamp, token);
        try
        {
            if (profileId is not null) { guard.ReadOwnedProfile(profileId, expectedRevision); }
            return guard;
        }
        catch { guard.Dispose(); throw; }
    }

    // The adapter obtains this identity from the durable ticket, never from phone input.
    // Acceptance must be re-read under this guard before requiring the old profile revision.
    internal CustomerAdmissionGuard AcquireDelegated(CustomerTicketOwner durableOwner, CancellationToken token = default)
        => CustomerAdmissionGuard.AcquireDelegated(ConnectionString(), profiles, clock, durableOwner, token);

    private string ConnectionString()
    {
        using var scope = scopes.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AccountDatabase>().Database.GetConnectionString()
            ?? throw new CustomerDataAccessException(503, "customer_data_unavailable");
    }
}

// Account -> character BEGIN IMMEDIATE reservations are not thread-affine.
// Acquire these before awaiting memory admission; dispose later scopes first.
internal sealed partial class CustomerAdmissionGuard : IDisposable, IAsyncDisposable
{
    private readonly CustomerDataSqliteGuard account;
    private readonly CustomerDataSqliteGuard character;
    private readonly CharacterProfileStore profiles;
    private readonly TimeProvider clock;
    private readonly long? expiresAt;
    private bool disposed;

    internal CustomerDataGuardDeadline Deadline { get; }
    internal CustomerAdmissionProof Proof { get; }

    private CustomerAdmissionGuard(CustomerDataSqliteGuard account, CustomerDataSqliteGuard character,
        CharacterProfileStore profiles, TimeProvider clock, CustomerDataGuardDeadline deadline,
        CustomerAdmissionProof proof, long? expiresAt)
    {
        this.account = account; this.character = character; this.profiles = profiles;
        this.clock = clock; this.expiresAt = expiresAt; Deadline = deadline; Proof = proof;
    }

    internal static CustomerAdmissionGuard AcquireCookie(string connectionString, CharacterProfileStore profiles,
        TimeProvider clock, string accountId, string sessionId, string stamp, CancellationToken token = default)
        => Acquire(connectionString, profiles, clock, accountId, sessionId, stamp, null, null, token);

    internal static CustomerAdmissionGuard AcquireCookieRevision(string connectionString, CharacterProfileStore profiles,
        TimeProvider clock, string accountId, string sessionId, string revision, CancellationToken token = default)
        => Acquire(connectionString, profiles, clock, accountId, sessionId, null, revision, null, token);

    internal static CustomerAdmissionGuard AcquireDelegated(string connectionString, CharacterProfileStore profiles,
        TimeProvider clock, CustomerTicketOwner owner, CancellationToken token = default)
        => Acquire(connectionString, profiles, clock, owner.OwnerId, null, null, null, owner, token);

    private static CustomerAdmissionGuard Acquire(string connectionString, CharacterProfileStore profiles, TimeProvider clock,
        string accountId, string? sessionId, string? stamp, string? revision, CustomerTicketOwner? delegated,
        CancellationToken token)
    {
        CustomerAdmissionDatabasePaths.Validate(connectionString, profiles.CustomerDataDatabasePath);
        var deadline = new CustomerDataGuardDeadline(token);
        CustomerDataSqliteGuard? account = null; CustomerDataSqliteGuard? character = null;
        try
        {
            account = CustomerDataSqliteGuard.Acquire(connectionString, deadline);
            var current = ReadAccount(account, accountId, sessionId, clock);
            if (delegated is null && (sessionId is null || (stamp is not null ? current.Stamp != stamp : current.Proof.AuthorityRevision != revision)))
            {
                throw new CustomerDataAccessException(401, "account_access_unavailable");
            }
            character = profiles.OpenCustomerDataGuard(deadline);
            profiles.ValidateCustomerDataAccount(character, accountId);
            var proof = current.Proof with { DelegatedProfileId = delegated?.ProfileId, DelegatedProfileRevision = delegated?.Revision };
            var guard = new CustomerAdmissionGuard(account, character, profiles, clock, deadline, proof, current.ExpiresAt);
            guard.ValidateCurrent();
            return guard;
        }
        catch
        {
            try { character?.Dispose(); }
            finally { account?.Dispose(); }
            throw;
        }
    }

    // This is the single live-account/session/stamp/deletion SQL implementation.
    // Delegated physical admission intentionally has no cookie/session/stamp requirement.
    private static (CustomerAdmissionProof Proof, string? Stamp, long? ExpiresAt) ReadAccount(
        CustomerDataSqliteGuard guard, string accountId, string? sessionId, TimeProvider clock)
    {
        using var command = guard.Command("""
            SELECT u.SecurityStamp,s.ExpiresAt,s.AuthenticatedAt FROM AspNetUsers u
            LEFT JOIN Sessions s ON s.UserId=u.Id AND s.Id=$session
            WHERE u.Id=$account AND u.Disabled=0
                AND NOT EXISTS(SELECT 1 FROM DeletionRequests d WHERE d.UserId=u.Id)
            """);
        command.Parameters.AddWithValue("$account", accountId);
        command.Parameters.AddWithValue("$session", sessionId is null ? DBNull.Value : sessionId);
        string? stamp; long? expires; long? authenticated;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) { throw new CustomerDataAccessException(401, "account_access_unavailable"); }
            stamp = reader.IsDBNull(0) ? null : reader.GetString(0);
            expires = reader.IsDBNull(1) ? null : reader.GetInt64(1);
            authenticated = reader.IsDBNull(2) ? null : reader.GetInt64(2);
        }
        if (sessionId is not null && (string.IsNullOrEmpty(stamp) || expires is null || authenticated is null
            || expires <= clock.GetUtcNow().ToUnixTimeSeconds()))
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
        var revision = Digest(new { accountId, sessionId, stamp, expires, authenticated, roles = names });
        return (new(accountId, sessionId, revision, Array.AsReadOnly(names.ToArray())), stamp, expires);
    }

    internal CharacterProfile ReadOwnedProfile(string profileId, long? expectedRevision = null)
    {
        ValidateCurrent();
        var profile = profiles.ReadHeldCustomerAdmissionProfile(character, Proof.AccountId, profileId, expectedRevision);
        ValidateCurrent();
        return profile ?? throw new CustomerDataAccessException(409, "character_revision_mismatch");
    }

    internal CharacterConversationSnapshot CaptureOwnedProfile(string profileId, long revision)
    {
        ValidateCurrent();
        var snapshot = profiles.CaptureHeldCustomerAdmissionProfile(character, Proof.AccountId, profileId, revision);
        ValidateCurrent();
        return snapshot ?? throw new CustomerDataAccessException(409, "character_revision_mismatch");
    }

    internal bool IsProfileRevoked(string profileId)
    {
        ValidateCurrent();
        return profiles.CustomerDataProfileRevoked(character, Proof.AccountId, profileId);
    }

    internal void RequireDevicePermission(string device)
    {
        ValidateCurrent();
        if (Proof.SessionId is null) { throw new CustomerDataAccessException(401, "account_access_unavailable"); }
        if (AccountDevicePermissionPolicy.AllowsOperation(Proof.Roles, false)) { return; }
        using var command = account.Command("""
            SELECT 1 FROM ResourceGrants WHERE UserId=$account AND ResourceType=$type AND ResourceId=$device
                AND Permission IN ($operate,$manage) LIMIT 1
            """);
        command.Parameters.AddWithValue("$account", Proof.AccountId); command.Parameters.AddWithValue("$device", device);
        command.Parameters.AddWithValue("$type", AccountDevicePermissionPolicy.ResourceType);
        command.Parameters.AddWithValue("$operate", AccountDevicePermissionPolicy.Operate);
        command.Parameters.AddWithValue("$manage", AccountDevicePermissionPolicy.Manage);
        if (!AccountDevicePermissionPolicy.AllowsOperation(Proof.Roles, command.ExecuteScalar() is not null))
        {
            throw new CustomerDataAccessException(403, "device_permission_required");
        }
        ValidateCurrent();
    }

    // Pure validation only: safe as the journal's final beforeCommit callback.
    // SQL reservations already prevent stamp/deletion/profile/permission changes.
    internal void ValidateCurrent()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Deadline.Check();
        if (expiresAt is not null && expiresAt <= clock.GetUtcNow().ToUnixTimeSeconds())
        {
            throw new CustomerDataAccessException(401, "account_access_unavailable");
        }
    }

    private static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        try { character.Dispose(); }
        finally { account.Dispose(); }
    }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

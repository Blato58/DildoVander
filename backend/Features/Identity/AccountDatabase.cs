using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal sealed class AccountUser : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public bool Disabled { get; set; }
    public long CreatedAt { get; set; }
}

internal sealed class AccountSession
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public long ExpiresAt { get; set; }
    public string InstallationName { get; set; } = "Browser";
    public bool Trusted { get; set; }
    public long AuthenticatedAt { get; set; }
    public long LastSeenAt { get; set; }
}

internal sealed class AccountInvitation
{
    public string TokenHash { get; set; } = "";
    public string Purpose { get; set; } = "invite";
    public string Role { get; set; } = AccountRoles.Viewer;
    public string? UserId { get; set; }
    public long ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

internal sealed class AccountCeremony
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? InvitationHash { get; set; }
    public long ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

internal sealed class AccountResourceGrant
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string Permission { get; set; } = "read";
}

internal sealed class AccountDeviceAssociation
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string Alias { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public long CreatedAt { get; set; }
}

internal sealed class AccountArchiveTicket
{
    public string TokenHash { get; set; } = "";
    public string TargetUserId { get; set; } = "";
    public string Locator { get; set; } = "";
    public int Epoch { get; set; }
    public long ExpiresAt { get; set; }
    public bool Consumed { get; set; }
}

internal sealed class AccountArchiveAssociation
{
    public string UserId { get; set; } = "";
    public string Locator { get; set; } = "";
    public int Epoch { get; set; }
    public long LinkedAt { get; set; }
}

internal sealed class AccountDeletionRequest
{
    public string UserId { get; set; } = "";
    public string ReceiptId { get; set; } = "";
    public long RequestedAt { get; set; }
    public long? CompletedAt { get; set; }
    public int Attempts { get; set; }
}

internal sealed class AccountDatabase(DbContextOptions<AccountDatabase> options)
    : IdentityDbContext<AccountUser>(options)
{
    public DbSet<AccountSession> Sessions => Set<AccountSession>();
    public DbSet<AccountInvitation> Invitations => Set<AccountInvitation>();
    public DbSet<AccountCeremony> Ceremonies => Set<AccountCeremony>();
    public DbSet<AccountResourceGrant> ResourceGrants => Set<AccountResourceGrant>();
    public DbSet<AccountDeviceAssociation> DeviceAssociations => Set<AccountDeviceAssociation>();
    public DbSet<AccountArchiveTicket> ArchiveTickets => Set<AccountArchiveTicket>();
    public DbSet<AccountArchiveAssociation> ArchiveAssociations => Set<AccountArchiveAssociation>();

    public DbSet<AccountDeletionRequest> DeletionRequests => Set<AccountDeletionRequest>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.Entity<AccountDeletionRequest>().HasKey(x => x.UserId);
        builder.Entity<AccountDeletionRequest>().HasIndex(x => x.ReceiptId).IsUnique();
        builder.Entity<AccountInvitation>().HasKey(x => x.TokenHash);
        builder.Entity<AccountArchiveTicket>().HasKey(x => x.TokenHash);
        builder.Entity<AccountArchiveAssociation>().HasKey(x => new { x.UserId, x.Locator });
        builder.Entity<AccountResourceGrant>()
            .HasIndex(x => new { x.UserId, x.ResourceType, x.ResourceId, x.Permission }).IsUnique();
        builder.Entity<AccountSession>().HasIndex(x => x.UserId);
        builder.Entity<AccountDeviceAssociation>().HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
        builder.Entity<AccountCeremony>().HasIndex(x => x.ExpiresAt);
    }
}

internal static class AccountRoles
{
    public const string Owner = "Owner";
    public const string Administrator = "Administrator";
    public const string Operator = "Operator";
    public const string Viewer = "Viewer";
    public const string Customer = "Customer";
    public static readonly string[] All = [Owner, Administrator, Operator, Viewer, Customer];
    public static bool IsStaff(System.Security.Claims.ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true &&
        (principal.IsInRole(Owner) || principal.IsInRole(Administrator)
            || principal.IsInRole(Operator) || principal.IsInRole(Viewer));
    public static bool IsAdmin(System.Security.Claims.ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true &&
        (principal.IsInRole(Owner) || principal.IsInRole(Administrator));
}

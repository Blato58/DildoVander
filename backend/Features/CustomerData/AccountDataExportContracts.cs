namespace Kufr.Backend.Features.CustomerData;

// Internal authority proofs are never part of the downloadable account document.
// Revision is an opaque server-derived digest, not a security stamp or credential.
internal sealed record CustomerDataAccess(string AccountId, string SessionId, string AuthorityRevision);

internal sealed record CustomerOwnedSession(string SessionId, string ProfileId, long ProfileRevision,
    string DeviceId, string? ConversationId, string ExpectedGuestId, bool ProfileRevoked,
    bool SnapshotAvailable);

internal sealed record CustomerOwnershipInventory(CustomerDataAccess Access,
    IReadOnlyList<CustomerOwnedSession> Sessions, string Revision, bool Complete);

internal interface ICustomerDataAuthority
{
    Task<CustomerDataAccess> CaptureAsync(HttpContext context, CancellationToken token = default);
    Task<CustomerOwnershipInventory> SnapshotAsync(CustomerDataAccess access, CancellationToken token = default);
    Task<bool> IsCurrentAsync(CustomerDataAccess access, CancellationToken token = default);
    Task<bool> IsCurrentAsync(CustomerOwnershipInventory inventory, CancellationToken token = default);
}

internal static class CustomerDataLimits
{
    internal const int MaxOwnedSessions = 4096;
    internal const int MaxMemoryItems = 4096;
    internal const int MaxExportBytes = 32 * 1024 * 1024;
}

internal sealed record CustomerMemoryItem(string Id, long Revision, string Text, string Kind,
    string SourceReference, DateTimeOffset CreatedAt, string ProfileId, string CustomerSessionId);

internal sealed record CustomerMemoryDataSnapshot(string InventoryRevision, string Revision,
    IReadOnlyList<CustomerMemoryItem> Items, bool Complete);

internal interface ICustomerMemoryDataSource
{
    Task<CustomerMemoryDataSnapshot> CaptureAsync(CustomerOwnershipInventory inventory,
        CancellationToken token = default);
    Task<bool> IsCurrentAsync(CustomerOwnershipInventory inventory, CustomerMemoryDataSnapshot snapshot,
        CancellationToken token = default);
}

internal sealed class CustomerDataAccessException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

internal enum AccountDataExportState { Requested, Preparing, Ready, Failed }

internal sealed record AccountDataExportRequest(int SchemaVersion);

internal sealed record AccountDataExportView(int SchemaVersion, string Id, string State,
    string? Reason, DateTimeOffset RequestedAt, DateTimeOffset? ReadyAt,
    DateTimeOffset? DownloadExpiresAt, string? DownloadPath);

internal sealed class AccountDataExportException(int status, string code) : Exception(code)
{
    internal int Status { get; } = status;
    internal string Code { get; } = code;
}

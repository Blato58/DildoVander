using Kufr.Backend.Features.CustomerData;

namespace Kufr.Backend.Features.Memory;

internal sealed record CustomerMemoryReadProof(
    string MemoryId,
    string AccountId,
    string CustomerSessionId,
    string ProfileId,
    string DeviceId,
    string ConversationId,
    long RecordRevision,
    long RecordPermissionVersion,
    long GuestPermissionVersion);

/// <summary>
/// Decides whether a candidate is eligible for an owner's local Personal-memory
/// read. This pure decision does not establish that account access or ownership
/// inventory is live, nor does it replace current-session/stamp checks, deletion
/// fences, permission revalidation, or atomic epoch checks by the authority layer.
/// </summary>
internal static class CustomerMemoryReadPolicy
{
    internal static CustomerMemoryReadProof? TryProve(
        string authenticatedAccountId,
        CustomerDataAccess access,
        CustomerOwnedSession ownedSession,
        MemoryRecord record,
        GuestMemoryPermission? currentBinding,
        string currentNamespace)
    {
        if (string.IsNullOrWhiteSpace(authenticatedAccountId)
            || !string.Equals(authenticatedAccountId, access.AccountId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(access.SessionId)
            || string.IsNullOrWhiteSpace(access.AuthorityRevision)
            || string.IsNullOrWhiteSpace(ownedSession.SessionId)
            || string.IsNullOrWhiteSpace(ownedSession.ProfileId)
            || string.IsNullOrWhiteSpace(ownedSession.DeviceId)
            || string.IsNullOrWhiteSpace(ownedSession.ConversationId)
            || ownedSession.ProfileRevoked
            || !ownedSession.SnapshotAvailable
            || string.IsNullOrWhiteSpace(currentNamespace)
            || string.IsNullOrWhiteSpace(record.MemoryId)
            || record.Scope != MemoryScope.Personal
            || !string.Equals(record.AppNamespace, currentNamespace, StringComparison.Ordinal)
            || !record.IsActive
            || !record.UseAllowed
            || record.Revision <= 0
            || record.Kind is not (MemoryKind.UserReportedFact or MemoryKind.ConfirmedSelection)
            || !string.Equals(record.DeviceId, ownedSession.DeviceId, StringComparison.Ordinal)
            || !string.Equals(record.ConversationId, ownedSession.ConversationId, StringComparison.Ordinal)
            || currentBinding is not { IdentityVerified: true, LongTermAllowed: true })
        {
            return null;
        }

        var expectedGuestId = CustomerMemoryOriginPolicy.ExpectedGuestId(
            authenticatedAccountId, ownedSession.ProfileId);
        if (!string.Equals(ownedSession.ExpectedGuestId, expectedGuestId, StringComparison.Ordinal)
            || !string.Equals(record.GuestId, expectedGuestId, StringComparison.Ordinal)
            || !string.Equals(currentBinding.GuestId, expectedGuestId, StringComparison.Ordinal)
            || !string.Equals(currentBinding.DeviceId, ownedSession.DeviceId, StringComparison.Ordinal)
            || !string.Equals(currentBinding.ConversationId, ownedSession.ConversationId, StringComparison.Ordinal))
        {
            return null;
        }

        return new CustomerMemoryReadProof(
            record.MemoryId,
            access.AccountId,
            ownedSession.SessionId,
            ownedSession.ProfileId,
            ownedSession.DeviceId,
            ownedSession.ConversationId,
            record.Revision,
            record.PermissionVersion,
            currentBinding.Version);
    }
}

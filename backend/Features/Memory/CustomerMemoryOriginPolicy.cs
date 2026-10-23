using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Memory;

/// <summary>
/// Evidence that a Personal memory record originated from a retained customer
/// session for the authenticated account and its account-bound guest identity.
/// This proves origin only. Callers must separately revalidate live account
/// session/stamp, profile deletion state, current permissions, and transactional
/// epochs before reading or mutating memory.
/// </summary>
internal sealed record CustomerMemoryOriginProof(
    string MemoryId,
    string CustomerSessionId,
    string AccountId,
    string ProfileId,
    string DeviceId,
    string ConversationId,
    long RecordRevision,
    long RecordPermissionVersion,
    long GuestPermissionVersion);

internal static class CustomerMemoryOriginPolicy
{
    internal static string ExpectedGuestId(string accountId, string profileId) =>
        "account_" + OperationJournal.CustomerHash(accountId + ":" + profileId).ToLowerInvariant();

    internal static CustomerMemoryOriginProof? TryProve(
        string authenticatedAccountId,
        IEnumerable<CustomerSessionRecord> retainedSessions,
        MemoryRecord record,
        string currentNamespace,
        GuestMemoryPermission? currentBinding)
    {
        if (string.IsNullOrWhiteSpace(authenticatedAccountId)
            || string.IsNullOrWhiteSpace(currentNamespace)
            || record.Scope != MemoryScope.Personal
            || !string.Equals(record.AppNamespace, currentNamespace, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(record.MemoryId)
            || string.IsNullOrWhiteSpace(record.DeviceId)
            || string.IsNullOrWhiteSpace(record.ConversationId)
            || string.IsNullOrWhiteSpace(record.GuestId)
            || currentBinding is not { IdentityVerified: true })
        {
            return null;
        }

        foreach (var session in retainedSessions)
        {
            if (!string.Equals(session.OwnerId, authenticatedAccountId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(session.ProfileId)
                || string.IsNullOrWhiteSpace(session.ConversationId)
                || !string.Equals(session.DeviceId, record.DeviceId, StringComparison.Ordinal)
                || !string.Equals(session.ConversationId, record.ConversationId, StringComparison.Ordinal))
            {
                continue;
            }

            var expectedGuestId = ExpectedGuestId(session.OwnerId, session.ProfileId);
            if (!string.Equals(record.GuestId, expectedGuestId, StringComparison.Ordinal)
                || !string.Equals(currentBinding.GuestId, expectedGuestId, StringComparison.Ordinal)
                || !string.Equals(currentBinding.DeviceId, session.DeviceId, StringComparison.Ordinal)
                || !string.Equals(currentBinding.ConversationId, session.ConversationId, StringComparison.Ordinal))
            {
                continue;
            }

            return new CustomerMemoryOriginProof(
                record.MemoryId,
                session.Id,
                session.OwnerId,
                session.ProfileId,
                session.DeviceId,
                session.ConversationId,
                record.Revision,
                record.PermissionVersion,
                currentBinding.Version);
        }

        return null;
    }
}

using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Memory;

internal static class CustomerMemoryReadPolicyTests
{
    internal static Task RunAsync()
    {
        ProvesEligibleLocalPersonalMemory();
        RejectsOtherAccountsAndGuestBindings();
        RejectsRevokedOrUnavailableProfiles();
        RejectsForeignOriginScopeAndNamespace();
        RejectsInactiveOrUnusableRecords();
        Console.WriteLine("PASS customer memory read policy: account/profile guest binding, revocation and snapshot gates, Personal origin, current local-read permissions, eligible kinds, and independent version capture");
        return Task.CompletedTask;
    }

    private static void ProvesEligibleLocalPersonalMemory()
    {
        var access = Access("account-a");
        var owned = Owned(access.AccountId, "profile-a", "session-a", "device-a", "conversation-a");
        var binding = Binding(owned, verified: true, longTermAllowed: true,
            cloudExportAllowed: false, version: 31);
        var record = Record(owned, MemoryKind.UserReportedFact) with
        {
            PermissionVersion = 7,
            ExportAllowed = false
        };

        var proof = CustomerMemoryReadPolicy.TryProve(
            access.AccountId, access, owned, record, binding, "kufr:prod");

        Check(proof is not null, "owner can prove current local Personal memory even without cloud/export permission");
        Check(proof!.MemoryId == record.MemoryId
            && proof.AccountId == access.AccountId
            && proof.CustomerSessionId == owned.SessionId
            && proof.ProfileId == owned.ProfileId
            && proof.DeviceId == owned.DeviceId
            && proof.ConversationId == owned.ConversationId,
            "read proof binds the candidate to the exact account, owned session, profile and origin tuple");
        Check(proof.RecordRevision == record.Revision
            && proof.RecordPermissionVersion == record.PermissionVersion
            && proof.GuestPermissionVersion == binding.Version
            && proof.RecordPermissionVersion != proof.GuestPermissionVersion,
            "read proof captures historical and current permission versions without requiring equality");

        var confirmed = CustomerMemoryReadPolicy.TryProve(
            access.AccountId, access, owned, record with { Kind = MemoryKind.ConfirmedSelection }, binding, "kufr:prod");
        Check(confirmed is not null, "confirmed selections are eligible Personal records");
    }

    private static void RejectsOtherAccountsAndGuestBindings()
    {
        var access = Access("account-a");
        var owned = Owned(access.AccountId, "profile-a", "session-a", "device-a", "conversation-a");
        var record = Record(owned, MemoryKind.UserReportedFact);
        var binding = Binding(owned, verified: true, longTermAllowed: true);

        Check(CustomerMemoryReadPolicy.TryProve("account-b", access, owned, record, binding, "kufr:prod") is null,
            "authenticated account must match captured account access");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, Access("account-b"), owned, record, binding, "kufr:prod") is null,
            "captured access from another account is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access,
                owned with { ExpectedGuestId = GuestId("account-b", owned.ProfileId) }, record, binding, "kufr:prod") is null,
            "owned session expected guest must equal the recomputed account/profile guest identity");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { GuestId = GuestId("account-b", owned.ProfileId) }, binding, "kufr:prod") is null,
            "record for a foreign account/profile guest is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned, record,
                Binding(owned with { DeviceId = "device-other" }, verified: true, longTermAllowed: true), "kufr:prod") is null,
            "current binding device must match the owned session");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned, record,
                Binding(owned with { ConversationId = "conversation-other" }, verified: true, longTermAllowed: true), "kufr:prod") is null,
            "current binding conversation must match the owned session");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned, record,
                Binding(owned, verified: false, longTermAllowed: true), "kufr:prod") is null,
            "unverified binding is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned, record,
                Binding(owned, verified: true, longTermAllowed: false), "kufr:prod") is null,
            "current binding without long-term permission is rejected for a read");
    }

    private static void RejectsRevokedOrUnavailableProfiles()
    {
        var access = Access("account-a");
        var owned = Owned(access.AccountId, "profile-a", "session-a", "device-a", "conversation-a");
        var record = Record(owned, MemoryKind.UserReportedFact);
        var binding = Binding(owned, verified: true, longTermAllowed: true);

        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access,
                owned with { ProfileRevoked = true }, record, binding, "kufr:prod") is null,
            "profile revocation blocks an otherwise valid origin and binding");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access,
                owned with { SnapshotAvailable = false }, record, binding, "kufr:prod") is null,
            "missing captured profile snapshot blocks an otherwise valid origin and binding");
    }

    private static void RejectsForeignOriginScopeAndNamespace()
    {
        var access = Access("account-a");
        var owned = Owned(access.AccountId, "profile-a", "session-a", "device-a", "conversation-a");
        var record = Record(owned, MemoryKind.UserReportedFact);
        var binding = Binding(owned, verified: true, longTermAllowed: true);

        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { Scope = MemoryScope.Conversation }, binding, "kufr:prod") is null,
            "conversation scope is not an eligible Personal memory read");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { AppNamespace = "other:prod" }, binding, "kufr:prod") is null,
            "foreign namespace is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access,
                owned with { DeviceId = "device-other" }, record, binding, "kufr:prod") is null,
            "record from another device is rejected even when its account guest matches");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access,
                owned with { ConversationId = "conversation-other" }, record, binding, "kufr:prod") is null,
            "record from another conversation is rejected even when its account guest matches");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record, binding, " ") is null,
            "empty current namespace is rejected");
    }

    private static void RejectsInactiveOrUnusableRecords()
    {
        var access = Access("account-a");
        var owned = Owned(access.AccountId, "profile-a", "session-a", "device-a", "conversation-a");
        var record = Record(owned, MemoryKind.UserReportedFact);
        var binding = Binding(owned, verified: true, longTermAllowed: true);

        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { IsActive = false }, binding, "kufr:prod") is null,
            "inactive record is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { UseAllowed = false }, binding, "kufr:prod") is null,
            "record disallowed for use is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { Revision = 0 }, binding, "kufr:prod") is null,
            "record without a positive revision is rejected");
        Check(CustomerMemoryReadPolicy.TryProve(access.AccountId, access, owned,
                record with { Kind = MemoryKind.TopicUsed }, binding, "kufr:prod") is null,
            "non-factual Personal kinds are rejected");
    }

    private static CustomerDataAccess Access(string accountId) =>
        new(accountId, "session-cookie", "authority-revision");

    private static CustomerOwnedSession Owned(string accountId, string profileId, string sessionId,
        string deviceId, string conversationId) =>
        new(sessionId, profileId, 3, deviceId, conversationId,
            GuestId(accountId, profileId), ProfileRevoked: false, SnapshotAvailable: true);

    private static MemoryRecord Record(CustomerOwnedSession owned, MemoryKind kind) =>
        new("memory-a", MemoryScope.Personal, owned.DeviceId, "evening-a", owned.ConversationId!,
            owned.ExpectedGuestId, null, kind, "private text", "source-ref", DateTimeOffset.UnixEpoch,
            DateTimeOffset.MaxValue, 5, true, null, true, false, 7, "kufr:prod");

    private static GuestMemoryPermission Binding(CustomerOwnedSession owned, bool verified,
        bool longTermAllowed, bool cloudExportAllowed = false, long version = 31) =>
        new(owned.DeviceId, owned.ConversationId!, owned.ExpectedGuestId,
            verified, longTermAllowed, cloudExportAllowed, version);

    private static string GuestId(string accountId, string profileId) =>
        CustomerMemoryOriginPolicy.ExpectedGuestId(accountId, profileId);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Customer memory read policy test failed: {message}");
        }
    }
}

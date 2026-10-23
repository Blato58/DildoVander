using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;

internal static class CustomerMemoryOriginTests
{
    internal static Task RunAsync()
    {
        ProvesOnlyExactAccountSessionOrigin();
        RejectsOtherScopesAndNamespaces();
        RejectsMissingOrMismatchedBinding();
        SearchesTheFullRetainedSessionInventory();
        IgnoresDisplayAndCapturedSnapshotChanges();
        Console.WriteLine("PASS customer memory origin: exact account/session/profile guest binding, scope and namespace filtering, binding mismatch, full retained-session inventory, and proof version capture");
        return Task.CompletedTask;
    }

    private static void ProvesOnlyExactAccountSessionOrigin()
    {
        var session = Session("account-a", "profile-a", "conversation-a", "device-a");
        var guest = GuestId(session.OwnerId, session.ProfileId);
        var record = Record(session, guest);
        var binding = Binding(session, guest, version: 19);

        var proof = CustomerMemoryOriginPolicy.TryProve(
            session.OwnerId, [session], record, "kufr:prod", binding);

        Check(proof is not null, "matching Personal record has an origin proof");
        Check(proof!.MemoryId == record.MemoryId
            && proof.CustomerSessionId == session.Id
            && proof.AccountId == session.OwnerId
            && proof.ProfileId == session.ProfileId
            && proof.DeviceId == session.DeviceId
            && proof.ConversationId == session.ConversationId,
            "proof carries the original account, session, profile, device and conversation");
        Check(proof.RecordRevision == record.Revision
            && proof.RecordPermissionVersion == record.PermissionVersion
            && proof.GuestPermissionVersion == binding.Version,
            "proof snapshots record and current binding versions without requiring equality");

        var otherAccountSession = Session("account-b", "profile-a", "conversation-a", "device-a");
        var sameDisplayNameGuest = GuestId(otherAccountSession.OwnerId, otherAccountSession.ProfileId);
        Check(sameDisplayNameGuest != guest, "same profile label in another account maps to a different account guest");
        Check(CustomerMemoryOriginPolicy.TryProve(
                otherAccountSession.OwnerId, [session, otherAccountSession], record, "kufr:prod", binding) is null,
            "matching names and a caller-supplied account cannot claim another account's record");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [otherAccountSession], record, "kufr:prod", binding) is null,
            "record guest identity does not substitute for a retained origin session");
    }

    private static void RejectsOtherScopesAndNamespaces()
    {
        var session = Session("account-a", "profile-a", "conversation-a", "device-a");
        var guest = GuestId(session.OwnerId, session.ProfileId);
        var binding = Binding(session, guest);

        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], Record(session, guest) with { Scope = MemoryScope.Conversation }, "kufr:prod", binding) is null,
            "conversation memory never receives a Personal origin proof");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], Record(session, guest) with { Scope = MemoryScope.EveningMotif }, "kufr:prod", binding) is null,
            "evening motifs never receive a Personal origin proof");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], Record(session, guest) with { AppNamespace = "other:prod" }, "kufr:prod", binding) is null,
            "records from another namespace are rejected");
        Check(CustomerMemoryOriginPolicy.TryProve(
                " ", [session], Record(session, guest), "kufr:prod", binding) is null,
            "empty authenticated account is rejected");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], Record(session, guest), " ", binding) is null,
            "empty current namespace is rejected");
    }

    private static void RejectsMissingOrMismatchedBinding()
    {
        var session = Session("account-a", "profile-a", "conversation-a", "device-a");
        var guest = GuestId(session.OwnerId, session.ProfileId);
        var record = Record(session, guest);

        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], record, "kufr:prod", null) is null,
            "missing live binding is rejected");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], record, "kufr:prod", Binding(session, guest, verified: false)) is null,
            "unverified guest binding is rejected");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], record with { GuestId = "account_wrong" }, "kufr:prod", Binding(session, guest)) is null,
            "record guest ID must match the account/profile-derived guest ID");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], record, "kufr:prod", Binding(session, "account_wrong")) is null,
            "current binding guest ID must match the account/profile-derived guest ID");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], record, "kufr:prod", Binding(session with { DeviceId = "device-other" }, guest)) is null,
            "current binding device must match the retained session");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session], record, "kufr:prod", Binding(session with { ConversationId = "conversation-other" }, guest)) is null,
            "current binding conversation must match the retained session");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session with { ProfileId = "" }], record, "kufr:prod", Binding(session, guest)) is null,
            "empty retained profile ID is rejected");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session with { ConversationId = "conversation-other" }], record, "kufr:prod", Binding(session, guest)) is null,
            "retained session must match the record conversation even when guest IDs match");
        Check(CustomerMemoryOriginPolicy.TryProve(
                session.OwnerId, [session with { DeviceId = "device-other" }], record, "kufr:prod", Binding(session, guest)) is null,
            "retained session must match the record device even when guest IDs match");
    }

    private static void SearchesTheFullRetainedSessionInventory()
    {
        var origin = Session("account-a", "profile-a", "conversation-a", "device-a");
        var decoys = Enumerable.Range(0, 100)
            .Select(index => Session("account-a", $"profile-{index}", $"conversation-{index}", $"device-{index}"))
            .ToArray();
        var retainedSessions = decoys.Append(origin).ToArray();
        var guest = GuestId(origin.OwnerId, origin.ProfileId);

        var proof = CustomerMemoryOriginPolicy.TryProve(
            origin.OwnerId, retainedSessions, Record(origin, guest), "kufr:prod", Binding(origin, guest));

        Check(proof?.CustomerSessionId == origin.Id,
            "origin lookup includes a valid retained session beyond the first 100 entries");
    }

    private static void IgnoresDisplayAndCapturedSnapshotChanges()
    {
        var session = Session("account-a", "stable-profile-id", "conversation-a", "device-a");
        var guest = GuestId(session.OwnerId, session.ProfileId);
        var changedCapture = session with
        {
            ProfileRevision = session.ProfileRevision + 1,
            Snapshot = "{\"displayName\":\"A changed display name\"}"
        };

        var proof = CustomerMemoryOriginPolicy.TryProve(
            session.OwnerId, [changedCapture], Record(session, guest), "kufr:prod", Binding(session, guest));

        Check(proof is not null && proof.ProfileId == session.ProfileId,
            "display and captured profile changes do not alter the stable profile-origin proof");
    }

    private static CustomerSessionRecord Session(string owner, string profile, string conversation, string device) =>
        new(Guid.NewGuid().ToString("N"), owner, "operation", device, "boot", profile, 1,
            "{\"displayName\":\"same name\"}", "completed", conversation, false, 1);

    private static MemoryRecord Record(CustomerSessionRecord session, string guest) =>
        new("memory-a", MemoryScope.Personal, session.DeviceId, "evening-a", session.ConversationId!,
            guest, null, MemoryKind.UserReportedFact, "private fact", "source", DateTimeOffset.UnixEpoch,
            DateTimeOffset.MaxValue, 7, true, null, true, false, 12, "kufr:prod");

    private static GuestMemoryPermission Binding(CustomerSessionRecord session, string guest,
        long version = 19, bool verified = true) =>
        new(session.DeviceId, session.ConversationId!, guest, verified, false, false, version);

    private static string GuestId(string owner, string profile) =>
        "account_" + OperationJournal.CustomerHash(owner + ":" + profile).ToLowerInvariant();

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Customer memory origin test failed: {message}");
        }
    }
}

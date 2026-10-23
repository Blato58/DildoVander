using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Keepsakes;

internal static class AccountDataExportDocumentTests
{
    public static Task RunAsync()
    {
        VerifyDeterministicWhitelistProjection();
        VerifyIncompleteSourcesRejected();
        VerifyInventoryLimits();
        VerifyUtf8ByteLimit();
        return Task.CompletedTask;
    }

    private static void VerifyDeterministicWhitelistProjection()
    {
        var access = new CustomerDataAccess("account-public-id", "auth-session-secret", "authority-secret");
        var sessions = new[]
        {
            new CustomerOwnedSession("session-z", "profile-z", 3, "device-z", "conversation-z",
                "expected-guest-secret", false, true),
            new CustomerOwnedSession("session-a", "profile-a", 2, "device-a", null,
                "expected-guest-secret-2", true, false)
        };
        var inventory = new CustomerOwnershipInventory(access, sessions, "inventory-revision", true);
        var keepsakes = new[]
        {
            new CustomerKeepsakeExportMetadata("keepsake-z", 19, 7, 4, 12, 200),
            new CustomerKeepsakeExportMetadata("keepsake-a", 15, 6, 2, null, 100)
        };
        var created = DateTimeOffset.Parse("2026-10-23T12:34:56Z");
        var memories = new[]
        {
            new CustomerMemoryItem("memory-z", 9, "Personal fact Z", "preference", "session-z",
                created, "profile-z", "session-z"),
            new CustomerMemoryItem("memory-a", 4, "Personal fact A", "detail", "session-a",
                created.AddDays(-1), "profile-a", "session-a")
        };
        var memory = new CustomerMemoryDataSnapshot("inventory-revision", "memory-revision", memories, true);
        var job = Job(access, created);

        var first = AccountDataExportDocument.Build(job, inventory, keepsakes, memory);
        var reversed = AccountDataExportDocument.Build(job,
            inventory with { Sessions = sessions.Reverse().ToArray() },
            keepsakes.Reverse().ToArray(),
            memory with { Items = memories.Reverse().ToArray() });
        Check(first.Bytes.SequenceEqual(reversed.Bytes), "projection bytes are deterministic across input enumeration order");
        Check(first.Sha256 == reversed.Sha256
            && first.Sha256 == Convert.ToHexString(SHA256.HashData(first.Bytes)),
            "payload hash is computed over the exact returned UTF-8 bytes");

        using var document = JsonDocument.Parse(first.Bytes);
        var root = document.RootElement;
        Check(root.GetProperty("schemaVersion").GetInt32() == 1, "document uses schema version one");
        Check(root.GetProperty("requestedAt").GetDateTimeOffset() == created, "requested time comes from the job record");
        Check(root.GetProperty("account").GetProperty("id").GetString() == access.AccountId,
            "projection includes the account identifier");
        Check(root.GetProperty("history").GetProperty("sessions")[0].GetProperty("id").GetString() == "session-a",
            "session metadata is ordinally sorted");
        Check(root.GetProperty("keepsakes")[0].GetProperty("id").GetString() == "keepsake-a",
            "owned keepsake metadata is ordinally sorted");
        Check(root.GetProperty("memoryFacts")[0].GetProperty("id").GetString() == "memory-a",
            "personal memory facts are ordinally sorted");
        Check(root.GetProperty("included").GetArrayLength() == 5 && root.GetProperty("excluded").GetArrayLength() == 5,
            "the document explicitly identifies included and excluded data categories");
        Check(root.GetProperty("history").GetProperty("sessions")[0].TryGetProperty("snapshotAvailable", out _)
            && !root.GetProperty("history").GetProperty("sessions")[0].TryGetProperty("expectedGuestId", out _),
            "history exposes approved availability metadata without guest identifiers");
        Check(root.GetProperty("keepsakes")[0].EnumerateObject().Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal).SetEquals(["id", "documentRevision", "linkedAt"]),
            "keepsake export contains only the approved public metadata fields");

        Check(root.GetProperty("conversationPreferences").ValueKind == JsonValueKind.Null,
            "unsaved defaults are not represented as persisted account preferences");
        var preferences = new Kufr.Backend.Features.Customers.CustomerConversationPreferenceView(
            1, "profile-a", 2, 7, "en-US", "gentle", true);
        var withPreferences = AccountDataExportDocument.Build(job,
            inventory with { ConversationPreferences = preferences }, keepsakes, memory);
        using var preferenceDocument = JsonDocument.Parse(withPreferences.Bytes);
        var saved = preferenceDocument.RootElement.GetProperty("conversationPreferences");
        Check(saved.GetProperty("conversationLanguage").GetString() == "en-US"
            && saved.GetProperty("tone").GetString() == "gentle"
            && saved.GetProperty("preferenceRevision").GetInt64() == 7
            && saved.GetProperty("profileId").GetString() == "profile-a",
            "export includes the guarded saved conversation preference and profile binding");

        var text = Encoding.UTF8.GetString(first.Bytes);
        foreach (var forbidden in new[]
        {
            "auth-session-secret", "authority-secret", "expected-guest-secret", job.Id,
            job.RequestId.ToString("N"), "inventory-revision", "memory-revision", "keepsake-revision",
            "payload-sha256", "\"provider\":", "\"prompt\":", "\"photo\":", "\"story\":", "full-story"
        })
        {
            Check(!text.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                $"projection does not serialize forbidden marker '{forbidden}'");
        }

        var changedMemory = memory with
        {
            Items = [memories[0] with { Text = "Changed personal fact" }, memories[1]]
        };
        var changed = AccountDataExportDocument.Build(job, inventory, keepsakes, changedMemory);
        Check(changed.Sha256 != first.Sha256 && !changed.Bytes.SequenceEqual(first.Bytes),
            "a source-content change changes the document and its hash");
    }

    private static void VerifyIncompleteSourcesRejected()
    {
        var access = Access();
        var inventory = new CustomerOwnershipInventory(access, Array.Empty<CustomerOwnedSession>(), "inventory", true);
        var keepsakes = Array.Empty<CustomerKeepsakeExportMetadata>();
        var memory = new CustomerMemoryDataSnapshot("inventory", "memory", Array.Empty<CustomerMemoryItem>(), true);
        var job = Job(access, DateTimeOffset.UnixEpoch);

        ExpectExportFailure(() => AccountDataExportDocument.Build(job,
            inventory with { Complete = false }, keepsakes, memory), 409,
            "incomplete ownership inventories fail closed");
        ExpectExportFailure(() => AccountDataExportDocument.Build(job, inventory, keepsakes,
            memory with { Complete = false }), 409, "incomplete memory snapshots fail closed");
        ExpectExportFailure(() => AccountDataExportDocument.Build(job, inventory, keepsakes,
            memory with { InventoryRevision = "different-inventory" }), 409,
            "memory captured against another inventory cannot be combined");
        Check(AccountDataExportDocument.Build(job with { State = AccountDataExportState.Ready },
            inventory, keepsakes, memory).Sha256 == AccountDataExportDocument.Build(job, inventory, keepsakes, memory).Sha256,
            "a ready job rebuilds the exact prepared bytes for download");
        ExpectExportFailure(() => AccountDataExportDocument.Build(job with { State = AccountDataExportState.Requested },
            inventory, keepsakes, memory), 409, "an unprepared job cannot be projected");
        ExpectExportFailure(() => AccountDataExportDocument.Build(job, inventory with
        {
            Access = access with { AccountId = "another-account" }
        }, keepsakes, memory), 409, "job authority cannot be combined with another account inventory");
    }

    private static void VerifyInventoryLimits()
    {
        var access = Access();
        var job = Job(access, DateTimeOffset.UnixEpoch);
        var sessions = Enumerable.Range(0, CustomerDataLimits.MaxOwnedSessions + 1)
            .Select(index => new CustomerOwnedSession($"session-{index:D4}", "profile", 1, "device", null,
                "guest", false, true)).ToArray();
        var inventory = new CustomerOwnershipInventory(access, sessions, "inventory", true);
        var emptyMemory = new CustomerMemoryDataSnapshot("inventory", "memory", Array.Empty<CustomerMemoryItem>(), true);
        ExpectExportFailure(() => AccountDataExportDocument.Build(job, inventory,
            Array.Empty<CustomerKeepsakeExportMetadata>(), emptyMemory), 413,
            "4097 historical sessions are rejected instead of truncated");

        var completeInventory = inventory with { Sessions = Array.Empty<CustomerOwnedSession>() };
        var excessiveMemory = Enumerable.Range(0, CustomerDataLimits.MaxMemoryItems + 1)
            .Select(index => new CustomerMemoryItem($"memory-{index:D4}", 1, "x", "fact", "source",
                DateTimeOffset.UnixEpoch, "profile", "session")).ToArray();
        ExpectExportFailure(() => AccountDataExportDocument.Build(job, completeInventory,
            Array.Empty<CustomerKeepsakeExportMetadata>(),
            new CustomerMemoryDataSnapshot("inventory", "memory", excessiveMemory, true)), 413,
            "4097 personal memory facts are rejected instead of truncated");

        var excessiveKeepsakes = Enumerable.Range(0, CustomerDataLimits.MaxOwnedSessions + 1)
            .Select(index => new CustomerKeepsakeExportMetadata($"keep-{index:D4}", 1, 1, 1, 1, index)).ToArray();
        ExpectExportFailure(() => AccountDataExportDocument.Build(job, completeInventory,
            excessiveKeepsakes, emptyMemory), 413,
            "4097 originally owned keepsakes are rejected instead of truncated");
    }

    private static void VerifyUtf8ByteLimit()
    {
        var access = Access();
        var inventory = new CustomerOwnershipInventory(access, Array.Empty<CustomerOwnedSession>(), "inventory", true);
        var tooLargeText = new string('é', CustomerDataLimits.MaxExportBytes / 2 + 1024);
        var memory = new CustomerMemoryDataSnapshot("inventory", "memory",
            [new CustomerMemoryItem("large", 1, tooLargeText, "fact", "source", DateTimeOffset.UnixEpoch,
                "profile", "session")], true);
        ExpectExportFailure(() => AccountDataExportDocument.Build(Job(access, DateTimeOffset.UnixEpoch),
            inventory, Array.Empty<CustomerKeepsakeExportMetadata>(), memory), 413,
            "UTF-8 output exceeding 32 MiB is rejected during bounded serialization");
    }

    private static AccountDataExportJob Job(CustomerDataAccess access, DateTimeOffset requestedAt) =>
        new("export-job-secret", access, Guid.Parse("c0f3d2c7-d35d-44b3-8435-1659c92bdc7f"),
            AccountDataExportState.Preparing, null, requestedAt, null, null, null);

    private static CustomerDataAccess Access() => new("account", "session-secret", "authority-secret");

    private static void ExpectExportFailure(Action action, int status, string message)
    {
        try { action(); }
        catch (CustomerDataAccessException error) when (error.Status == status)
        {
            Check(true, message);
            return;
        }
        catch (AccountDataExportException error) when (error.Status == status)
        {
            Check(true, message);
            return;
        }
        throw new InvalidOperationException("Account data export document test failed: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Account data export document test failed: " + message); }
    }
}

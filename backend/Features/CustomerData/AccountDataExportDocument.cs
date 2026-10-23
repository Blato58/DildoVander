using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.CustomerData;

internal sealed record AccountDataExportPayload(byte[] Bytes, string Sha256);

// This is an intentionally scoped v1 projection, not a dump of the backing
// stores. Additions to the export contract must be explicit and reviewed.
internal static class AccountDataExportDocument
{
    private static readonly string[] IncludedSections =
    [
        "account_identifier",
        "historical_character_session_metadata",
        "originally_owned_keepsake_metadata",
        "permitted_personal_memory_facts",
        "saved_conversation_preferences"
    ];

    private static readonly string[] ExcludedSections =
    [
        "credentials_and_authentication_sessions",
        "security_stamps_and_authority_proofs",
        "other_guests_and_linked_viewer_private_data",
        "full_stories_photos_and_media",
        "raw_transcripts_provider_logs_prompts_and_character_inputs"
    ];

    internal static AccountDataExportPayload Build(AccountDataExportJob job,
        CustomerDataGuardScope scope, CustomerMemoryDataSnapshot memory)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.ValidateCurrent();
        try
        {
            var payload = Build(job, scope.Inventory, scope.Keepsakes, memory);
            if (!string.Equals(scope.KeepsakeRevision, ComputeKeepsakeRevision(scope.Keepsakes),
                    StringComparison.Ordinal))
            {
                throw new CustomerDataAccessException(409, "export_source_changed");
            }
            scope.ValidateCurrent();
            return payload;
        }
        catch
        {
            // A final scope validation is required even when projection fails,
            // so cancellation or a lost authority proof is not hidden by error.
            scope.ValidateCurrent();
            throw;
        }
    }

    // The argument-only form is useful for deterministic unit tests. Production
    // callers must use the held-scope overload above.
    internal static AccountDataExportPayload Build(AccountDataExportJob job,
        CustomerOwnershipInventory inventory, IReadOnlyList<CustomerKeepsakeExportMetadata> keepsakes,
        CustomerMemoryDataSnapshot memory)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(keepsakes);
        ArgumentNullException.ThrowIfNull(memory);

        if (job.State is not (AccountDataExportState.Preparing or AccountDataExportState.Ready)
            || job.Access != inventory.Access
            || !inventory.Complete
            || !memory.Complete
            || !string.Equals(memory.InventoryRevision, inventory.Revision, StringComparison.Ordinal))
        {
            throw new CustomerDataAccessException(409, "export_source_changed");
        }

        if (inventory.Sessions.Count > CustomerDataLimits.MaxOwnedSessions
            || memory.Items.Count > CustomerDataLimits.MaxMemoryItems
            || keepsakes.Count > CustomerDataLimits.MaxOwnedSessions)
        {
            throw new CustomerDataAccessException(413, "export_inventory_limit");
        }

        var writer = new BoundedBufferWriter(CustomerDataLimits.MaxExportBytes);
        using (var json = new Utf8JsonWriter(writer, new JsonWriterOptions { Indented = false }))
        {
            WriteDocument(json, job, inventory, keepsakes, memory);
        }

        var bytes = writer.ToArray();
        return new AccountDataExportPayload(bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal static string ComputeKeepsakeRevision(IReadOnlyList<CustomerKeepsakeExportMetadata> keepsakes) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(keepsakes)));

    private static void WriteDocument(Utf8JsonWriter json, AccountDataExportJob job,
        CustomerOwnershipInventory inventory, IReadOnlyList<CustomerKeepsakeExportMetadata> keepsakes,
        CustomerMemoryDataSnapshot memory)
    {
        json.WriteStartObject();
        json.WriteNumber("schemaVersion", 1);
        json.WriteString("requestedAt", job.RequestedAt);

        json.WriteStartObject("account");
        json.WriteString("id", inventory.Access.AccountId);
        json.WriteEndObject();

        json.WriteStartArray("included");
        foreach (var section in IncludedSections) { json.WriteStringValue(section); }
        json.WriteEndArray();
        json.WriteStartArray("excluded");
        foreach (var section in ExcludedSections) { json.WriteStringValue(section); }
        json.WriteEndArray();

        json.WritePropertyName("conversationPreferences");
        JsonSerializer.Serialize(json, inventory.ConversationPreferences, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.WriteStartObject("history");
        json.WriteStartArray("sessions");
        foreach (var session in inventory.Sessions.OrderBy(item => item.SessionId, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("id", session.SessionId);
            json.WriteString("profileId", session.ProfileId);
            json.WriteNumber("profileRevision", session.ProfileRevision);
            json.WriteString("deviceId", session.DeviceId);
            json.WriteString("conversationId", session.ConversationId);
            json.WriteBoolean("profileRevoked", session.ProfileRevoked);
            json.WriteBoolean("snapshotAvailable", session.SnapshotAvailable);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();

        json.WriteStartArray("keepsakes");
        foreach (var keepsake in keepsakes.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("id", keepsake.Id);
            if (keepsake.DocumentRevision is { } revision)
            {
                json.WriteNumber("documentRevision", revision);
            }
            else
            {
                json.WriteNull("documentRevision");
            }
            json.WriteNumber("linkedAt", keepsake.LinkedAt);
            json.WriteEndObject();
        }
        json.WriteEndArray();

        json.WriteStartArray("memoryFacts");
        foreach (var item in memory.Items.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            json.WriteStartObject();
            json.WriteString("id", item.Id);
            json.WriteNumber("revision", item.Revision);
            json.WriteString("text", item.Text);
            json.WriteString("kind", item.Kind);
            json.WriteString("sourceReference", item.SourceReference);
            json.WriteString("createdAt", item.CreatedAt);
            json.WriteString("profileId", item.ProfileId);
            json.WriteString("customerSessionId", item.CustomerSessionId);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }

    private sealed class BoundedBufferWriter(int maximumBytes) : IBufferWriter<byte>
    {
        private byte[] buffer = new byte[Math.Min(16 * 1024, maximumBytes)];
        private int written;

        public void Advance(int count)
        {
            if (count < 0 || count > buffer.Length - written)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            written += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return buffer.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            EnsureCapacity(sizeHint);
            return buffer.AsSpan(written);
        }

        internal byte[] ToArray() => buffer.AsSpan(0, written).ToArray();

        private void EnsureCapacity(int sizeHint)
        {
            if (sizeHint < 0) { throw new ArgumentOutOfRangeException(nameof(sizeHint)); }
            sizeHint = Math.Max(sizeHint, 1);
            var required = (long)written + sizeHint;
            if (required > maximumBytes)
            {
                throw new AccountDataExportException(413, "export_too_large");
            }
            if (required <= buffer.Length) { return; }

            var nextSize = (int)Math.Min(maximumBytes, Math.Max(required, (long)buffer.Length * 2));
            Array.Resize(ref buffer, nextSize);
        }
    }
}

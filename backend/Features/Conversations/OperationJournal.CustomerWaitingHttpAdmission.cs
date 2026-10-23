using System.Text.Json;
using Kufr.Backend.Features.Customers;

namespace Kufr.Backend.Features.Conversations;

internal sealed record CustomerTicketAdmission(CustomerTicketOwner Owner, long ExpiresAt,
    string DeviceId, string OperationId, string BootId, string Challenge);
internal sealed record CustomerWaitingOpenReplayContext(string EveningId, long ActivationRevision);

internal sealed partial class OperationJournal
{
    // These lookups select replay versus new admission; they grant no authority.
    // The subsequent writer transaction still validates the complete fingerprint,
    // ownership, proximity, revocation and actual ticket/entry binding.
    internal bool HasCustomerTicketForOperation(string owner, string operation)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null,
            "SELECT 1 FROM customer_start_tickets WHERE owner_id=$owner AND operation_id=$operation",
            ("$owner", owner), ("$operation", operation));
        return command.ExecuteScalar() is not null;
    }

    internal CustomerTicketAdmission? ReadCustomerTicketAdmission(string hash)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null, """
            SELECT owner_id,profile_id,profile_revision,session_id,expires_at,device_id,operation_id,boot_id,challenge
            FROM customer_start_tickets WHERE token_hash=$hash
            """, ("$hash", hash));
        using var row = command.ExecuteReader();
        return row.Read() ? new(new(row.GetString(0), row.GetString(1), row.GetInt64(2),
            row.IsDBNull(3) ? null : row.GetString(3)), row.GetInt64(4), row.GetString(5),
            row.GetString(6), row.GetString(7), row.GetString(8)) : null;
    }

    internal bool HasCustomerWaitingReceipt(string device, string? owner, string requestId)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null, """
            SELECT 1 FROM customer_waiting_receipts
            WHERE device_id=$device AND actor_id=$actor AND request_id=$request
            """, ("$device", device), ("$actor", owner is null ? "operator" : "owner:" + owner),
            ("$request", requestId));
        return command.ExecuteScalar() is not null;
    }

    internal string? CustomerWaitingOwnedEntryProfile(string owner, string device, string eventId, string entryId)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null, """
            SELECT e.profile_id FROM customer_waiting_entries e
            JOIN customer_waiting_events v ON v.id=e.event_id
            WHERE e.owner_id=$owner AND v.device_id=$device AND v.id=$event AND e.id=$entry
            """, ("$owner", owner), ("$device", device), ("$event", eventId), ("$entry", entryId));
        return command.ExecuteScalar() as string;
    }

    internal CustomerWaitingOpenReplayContext? ReadCustomerWaitingOpenReplayContext(string device, string requestId)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null, """
            SELECT result FROM customer_waiting_receipts
            WHERE device_id=$device AND actor_id='operator' AND request_id=$request
            """, ("$device", device), ("$request", requestId));
        if (command.ExecuteScalar() is not string result) return null;
        try
        {
            using var document = JsonDocument.Parse(result);
            var value = document.RootElement;
            // Only reconstruct immutable server-selected inputs. The writer
            // still verifies the full command fingerprint before replaying;
            // this context never grants a new activation or reaches the wire.
            if (value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("DeviceId", out var storedDevice)
                || storedDevice.ValueKind != JsonValueKind.String || storedDevice.GetString() != device
                || !value.TryGetProperty("EveningId", out var evening) || evening.ValueKind != JsonValueKind.String
                || !Guid.TryParseExact(evening.GetString(), "D", out var id) || id == Guid.Empty
                || !value.TryGetProperty("ActivationRevision", out var revision)
                || revision.ValueKind != JsonValueKind.Number
                || !revision.TryGetInt64(out var epoch) || epoch <= 0) return null;
            return new(evening.GetString()!, epoch);
        }
        catch (JsonException) { throw new CustomerSessionException(503, "waiting_receipt_unavailable"); }
    }
}

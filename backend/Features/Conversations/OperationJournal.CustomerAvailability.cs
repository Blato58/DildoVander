using Kufr.Backend.Features.Customers;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class OperationJournal
{
    private sealed record CustomerRuntimePresence(string BootId, bool Ready, long Timestamp);
    private readonly object customerRuntimePresenceGate = new();
    private readonly Dictionary<string, CustomerRuntimePresence> customerRuntimePresence = new(StringComparer.Ordinal);

    // Called only after device authentication and strict boot/ready validation.
    // Presence is intentionally not persisted: restart must require a new
    // matching runtime observation rather than resurrect a stored ready flag.
    internal bool ObserveCustomerRuntime(string device, string boot, bool ready)
    {
        using var connection = Open();
        using var command = CustomerCommand(connection, null, """
            SELECT boot_id,challenge_expires FROM customer_devices WHERE device_id=$device
            """, ("$device", device));
        using var row = command.ExecuteReader();
        if (!row.Read() || row.IsDBNull(0) || row.GetString(0) != boot || row.GetInt64(1) <= CustomerNow()) return false;
        lock (customerRuntimePresenceGate)
        {
            if (!customerRuntimePresence.ContainsKey(device) && customerRuntimePresence.Count >= 4096) return false;
            customerRuntimePresence[device] = new(boot, ready, clock.GetTimestamp());
        }
        return true;
    }

    internal CustomerAvailabilityProjection CustomerAvailabilityFor(string device, bool eventOpen, bool paused)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        string? boot = null;
        var enabled = false;
        var challengeCurrent = false;
        using (var command = CustomerCommand(connection, transaction,
            "SELECT enabled,boot_id,challenge_expires FROM customer_devices WHERE device_id=$device", ("$device", device)))
        using (var row = command.ExecuteReader())
        {
            if (row.Read())
            {
                enabled = row.GetInt64(0) == 1;
                boot = row.IsDBNull(1) ? null : row.GetString(1);
                challengeCurrent = row.GetInt64(2) > CustomerNow();
            }
        }
        var lease = CustomerLeaseObservation.None;
        using (var command = CustomerCommand(connection, transaction, """
            SELECT l.customer_session_id,l.boot_id,s.state FROM device_conversation_leases l
            LEFT JOIN customer_sessions s ON s.id=l.customer_session_id WHERE l.device_id=$device
            """, ("$device", device)))
        using (var row = command.ExecuteReader())
        {
            if (row.Read())
            {
                var differentBoot = row.IsDBNull(1) || boot is null || row.GetString(1) != boot;
                var unresolvedCustomer = !row.IsDBNull(0) && (row.IsDBNull(2) || row.GetString(2) == "recovering");
                lease = differentBoot || unresolvedCustomer ? CustomerLeaseObservation.Unresolved : CustomerLeaseObservation.Occupied;
            }
        }
        CustomerRuntimePresence? presence;
        lock (customerRuntimePresenceGate) { customerRuntimePresence.TryGetValue(device, out presence); }
        var matches = presence is not null && presence.BootId == boot && challengeCurrent;
        var evidence = new CustomerAvailabilityEvidence(lease, paused, eventOpen, enabled,
            presence is { Ready: true } ? CustomerRuntimeObservation.Ready : CustomerRuntimeObservation.Unknown,
            matches, presence is null ? null : clock.GetElapsedTime(presence.Timestamp));
        return CustomerAvailability.Project(evidence);
    }
}

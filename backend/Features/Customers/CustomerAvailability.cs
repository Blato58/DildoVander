namespace Kufr.Backend.Features.Customers;

internal enum CustomerAvailabilityState { Available, Busy, Recovering, Paused, Offline, Unknown }
internal enum CustomerLeaseObservation { None, Occupied, Unresolved }
internal enum CustomerRuntimeObservation { Unknown, Ready, Offline }

// These observations must be supplied by the authoritative journal/runtime
// adapter. A phone's connectivity, queue position or challenge expiry is not
// runtime evidence, and this read-only projection grants no admission rights.
internal sealed record CustomerAvailabilityEvidence(
    CustomerLeaseObservation Lease,
    bool AdmissionPaused,
    bool EventOpen,
    bool CustomerModeEnabled,
    CustomerRuntimeObservation Runtime,
    bool RuntimeIdentityMatches,
    TimeSpan? ObservationAge);

internal sealed record CustomerAvailabilityProjection(
    CustomerAvailabilityState State, string Reason, bool Fresh);

internal static class CustomerAvailability
{
    // In-memory presence is deliberately short-lived and starts unknown after
    // restart. Durable leases remain authoritative when presence expires.
    internal static readonly TimeSpan MaximumObservationAge = TimeSpan.FromSeconds(10);

    internal static CustomerAvailabilityProjection Project(CustomerAvailabilityEvidence evidence)
    {
        if (evidence.Lease == CustomerLeaseObservation.Unresolved)
        {
            return new(CustomerAvailabilityState.Recovering, "lease_unresolved", true);
        }
        if (evidence.Lease == CustomerLeaseObservation.Occupied)
        {
            return new(CustomerAvailabilityState.Busy, "lease_occupied", true);
        }
        // An unrecognized enum value must never collapse to an empty lease.
        if (evidence.Lease != CustomerLeaseObservation.None)
        {
            return new(CustomerAvailabilityState.Unknown, "lease_unknown", false);
        }
        if (evidence.AdmissionPaused || !evidence.EventOpen || !evidence.CustomerModeEnabled)
        {
            return new(CustomerAvailabilityState.Paused,
                !evidence.EventOpen ? "event_closed" : !evidence.CustomerModeEnabled ? "customer_mode_disabled" : "admission_paused", true);
        }
        if (!evidence.RuntimeIdentityMatches)
        {
            return new(CustomerAvailabilityState.Unknown, "runtime_identity_unverified", false);
        }
        if (evidence.ObservationAge is not { } age || age < TimeSpan.Zero || age >= MaximumObservationAge)
        {
            return new(CustomerAvailabilityState.Unknown, "runtime_observation_stale", false);
        }
        return evidence.Runtime switch
        {
            CustomerRuntimeObservation.Ready => new(CustomerAvailabilityState.Available, "runtime_ready", true),
            CustomerRuntimeObservation.Offline => new(CustomerAvailabilityState.Offline, "runtime_offline", true),
            _ => new(CustomerAvailabilityState.Unknown, "runtime_unknown", false)
        };
    }
}

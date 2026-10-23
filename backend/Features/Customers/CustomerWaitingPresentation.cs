namespace Kufr.Backend.Features.Customers;

// The activation revision is an internal admission fence. Customer responses
// expose only the event identity and display revision, never that authority.
internal sealed record CustomerWaitingHttpEvent(
    string Id, string DeviceId, string EveningId, string State, long Revision);

internal sealed record CustomerWaitingHttpAvailability(string State, string Reason, bool Fresh);

internal sealed record CustomerWaitingHttpView(int SchemaVersion,
    CustomerWaitingHttpAvailability Availability, CustomerWaitingHttpEvent? Event,
    CustomerWaitingEntry? Entry, int? Position, int Count);

internal static class CustomerWaitingPresentation
{
    // Callers supply an already authorized, account-scoped journal view. This
    // projection grants no ownership, proximity or permission to start.
    internal static CustomerWaitingHttpView Project(CustomerWaitingView view,
        CustomerAvailabilityProjection availability)
    {
        var state = availability.State switch
        {
            CustomerAvailabilityState.Available => "available",
            CustomerAvailabilityState.Busy => "busy",
            CustomerAvailabilityState.Recovering => "recovering",
            CustomerAvailabilityState.Paused => "paused",
            CustomerAvailabilityState.Offline => "offline",
            _ => "unknown"
        };
        var current = view.Event;
        return new(1, new(state, availability.Reason, availability.Fresh),
            current is null ? null : new(current.Id, current.DeviceId,
                current.EveningId, current.State, current.Revision),
            view.Entry, view.Position, view.Count);
    }
}

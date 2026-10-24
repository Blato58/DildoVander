using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private bool PinCustomerWand(RealtimeCall call, int physicalSlot)
    {
        if (call.LabHardware is not null) return true;
        var customer = journal.CustomerConversation(call.Correlation.DeviceId, call.Correlation.ConversationId);
        if (customer is null) return true;
        if (wands is null || call.Context.GeneratedWands is null || customer.Snapshot.Length == 0 || customer.EndRequested
            || !call.Context.GeneratedWands.Any(w => w.PhysicalSlot == physicalSlot
                && w.ProfileId == customer.ProfileId && w.ProfileRevision == customer.ProfileRevision)) return false;
        var available = LiveAvailability(call);
        if (!available.Current || available.Revision != call.Correlation.DeviceRevision) return false;
        var selected = wands.Select(customer.OwnerId, customer.OperationId, customer.ProfileId,
            customer.ProfileRevision, physicalSlot, available.ReadyMask);
        return call.Context.GeneratedWands.Any(w => w.WandId == selected.WandId && w.PhysicalSlot == physicalSlot);
    }
}

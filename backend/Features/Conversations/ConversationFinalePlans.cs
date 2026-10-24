using Kufr.Backend.Features.Character;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    private void BindFinaleSnapshot(string deviceId, string operationId, object? value, string? generation,
        long selectionRevision, string personaVersion, FinaleProfile? profile, string? eveningId)
    {
        if (show is null || value is not SelectionProposal proposal) { return; }
        try
        {
            var customer = journal.CustomerConversation(deviceId, proposal.ConversationId);
            var wandId = customer is null ? null : wands?.Read(customer.OwnerId, customer.OperationId)?.SelectedWand?.WandId;
            var result = show.BindFinaleProposal(deviceId, operationId, proposal, generation,
                selectionRevision, personaVersion, profile, eveningId, wandId);
            if (result.Status is not ("bound" or "replayed"))
            { throw new InvalidOperationException("Finale snapshot was not bound: " + result.Code); }
        }
        catch
        {
            // The legacy paid path stages its cached result before completing
            // the journal. Never expose that proposal if the snapshot failed.
            operations.Remove(operationId);
            throw;
        }
    }
}

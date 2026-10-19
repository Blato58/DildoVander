using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    internal MemoryAccess? LiveMemoryAccess(RealtimeCall call)
    {
        lock (gate)
        {
            return call.EveningId is null ? null : new(call.Correlation.DeviceId, call.EveningId,
                call.Correlation.ConversationId, call.Context.PersonalityId,
                eveningMemory?.GuestPermission(call.Correlation.DeviceId, call.Correlation.ConversationId));
        }
    }
    internal bool CanBindGuest(string device, string id)
    {
        lock (gate) return conversations.TryGetValue(id, out var c) && c.DeviceId == device && c.TurnCount == 0 && !HasActiveCall(id);
    }
    internal void InvalidateMemoryContext(string device, string? conversation = null)
    {
        lock (gate)
        {
            // Replacement sessions are started only by the explicit new-
            // interaction event, using valid records. A deleted fact is never
            // left in a running model's context for possible reuse.
            foreach (var pair in conversations.Where(p => p.Value.DeviceId == device && (conversation is null || p.Key == conversation)).ToArray())
                RemoveConversation(pair.Key);
        }
    }
    internal (int ReadyMask, int Revision, bool Current) LiveAvailability(RealtimeCall call)
    {
        lock (gate)
        {
            if (!IsRealtimeTurnCurrent(call) || !conversations.TryGetValue(call.Correlation.ConversationId, out var c))
                return (0, call.Context.Request.DeviceRevision, false);
            return (c.LiveReadyMask ?? call.Context.Request.ReadyMask, c.LiveAvailabilityRevision ?? call.Context.Request.DeviceRevision, true);
        }
    }
    internal bool UpdateLiveAvailability(string device, string conversation, Guid boot, int generation, int mask, int revision)
    {
        lock (gate)
        {
            if (mask is < 0 or > 63 || !conversations.TryGetValue(conversation, out var c) || c.DeviceId != device
                || c.BootId != boot || c.Generation != generation || c.LiveAvailabilityRevision is not { } old || revision < old
                || revision == old && mask != c.LiveReadyMask) return false;
            c.LiveReadyMask = mask; c.LiveAvailabilityRevision = revision; return true;
        }
    }
    private TurnResult? CompleteAutonomousLive(RealtimeCall call, LiveOutcome outcome)
    {
        var availability = LiveAvailability(call);
        var decision = outcome.Decision;
        if (call.Context.LivePrompt is { Version: "dv01-adult-v1" } && show is not null
            && !show.CanPublishCharacterFinale(call.Correlation.DeviceId, call.SessionGeneration)) { return null; }
        if (call.Context.LivePrompt is { Version: "dv01-adult-v1" } characterSnapshot
            && (characterSnapshot.CatalogVersion != call.Context.Request.CatalogVersion
                || characterSnapshot.CatalogVersion != PartyWandCatalog.Version)) { return null; }
        if (!outcome.PlaybackVerified || decision is null || !availability.Current
            || decision.DecisionRevision != outcome.DecisionRevision || decision.AvailabilityRevision != availability.Revision
            || decision.Status == WandDecisionStatus.ValidProposal && (availability.ReadyMask & (1 << (decision.Compartment!.Value - 1))) == 0
            || !journal.SettleCost(call.OperationId, outcome.Reply.CostMicroUsd)) return null;
        // A changed physical snapshot needs a new correlated interaction. Never
        // forge a proposal against the old turn's device revision.
        if (decision.Status == WandDecisionStatus.ValidProposal && availability.Revision != call.Correlation.DeviceRevision) return null;
        var reply = outcome.Reply;
        var correlation = call.Correlation;
        var characterBlocked = call.Context.LivePrompt is { Version: "dv01-adult-v1", Options.Character.FinaleProfile: Character.FinaleProfile.Blocked };
        var proposal = !characterBlocked && decision.Status == WandDecisionStatus.ValidProposal && decision.Compartment is { } compartment
            ? new SelectionProposal(Guid.NewGuid().ToString("D"), correlation.ConversationId, correlation.ClientTurnId,
                call.Context.Request.CatalogVersion, correlation.DeviceRevision, compartment, reply.Reason,
                correlation.BootId, correlation.Generation, clock.GetUtcNow().AddSeconds(60)) : null;
        var result = new TurnResult(reply.Reply, proposal is null ? "no_selection" : "propose", reply.Reason, proposal);
        if (call.EveningId is not null && call.Context.LivePrompt?.Version != "dv01-adult-v1")
        {
            var access = LiveMemoryAccess(call)!;
            eveningMemory!.AppendTimedTranscripts(access, call.SessionGeneration, outcome.Segments);
            var epoch = eveningMemory.MemoryEpoch;
            var motif = AnonymousMotifs.FromTranscript(string.Concat(outcome.Segments.Select(s => s.Text)));
            if (motif.Length > 0) eveningMemory.WriteMemory(access, new(Guid.NewGuid().ToString("D"), MemoryScope.EveningMotif,
                MemoryKind.TopicUsed, motif, call.OperationId, ExpectedEpoch: epoch));
        }
        var capturedEvening = eveningMemory?.ReadCapturedShowEvening(correlation.DeviceId, correlation.ConversationId) ?? call.EveningId;
        bool CommitProposal() => journal.MarkSelectionCompleted(call.OperationId, proposal, capturedEvening);
        var published = call.Context.LivePrompt is { Version: "dv01-adult-v1" } && show is not null
            ? show.TryCommitCharacterFinale(correlation.DeviceId, call.SessionGeneration, CommitProposal)
            : CommitProposal();
        if (!published) { return null; }
        BindFinaleSnapshot(correlation.DeviceId, call.OperationId, proposal, call.SessionGeneration,
            decision.DecisionRevision, call.Context.LivePrompt?.Version ?? "live-existing",
            call.Context.LivePrompt is { Version: "dv01-adult-v1" } frozen ? frozen.Options.Character.FinaleProfile : null,
            eveningMemory?.ReadCapturedShowEvening(correlation.DeviceId, correlation.ConversationId) ?? call.EveningId);
        operations.Add(call.OperationId, new(call.OperationId, "turn", "completed", correlation, result, null));
        conversations[correlation.ConversationId].TerminalReplyPublished = true;
        return result;
    }
}

using System.Security.Cryptography;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    internal RealtimeAdmission BeginRealtimeTurn(
        string deviceId,
        string conversationId,
        Guid key,
        byte[] body,
        RealtimeTurnRequest request,
        bool providerAvailable,
        CancellationToken disconnected,
        CancellationToken stopping)
    {
        if (!RealtimeProtocol.IsValid(request))
        {
            return new(400, "invalid_request", null);
        }
        var route = $"/v1/conversations/{conversationId}/realtime-turns";
        var bodyHash = Convert.ToHexString(SHA256.HashData(body));
        lock (gate)
        {
            var now = clock.GetUtcNow();
            CleanupExpired(now);
            var previous = journal.FindExisting(deviceId, route, key, bodyHash);
            if (previous is not null)
            {
                // The journal prevents regeneration; progressive sound cannot
                // be replayed after the stream has gone away.
                return new(409, previous.Conflict ? "idempotency_conflict" : "realtime_stream_not_replayable", null);
            }
            if (!conversations.TryGetValue(conversationId, out var conversation))
            {
                return new(404, "conversation_not_found", null);
            }
            if (conversation.DeviceId != deviceId)
            {
                return new(403, "device_scope_mismatch", null);
            }
            if (!conversation.AudioConsented)
            {
                return new(403, "audio_consent_required", null);
            }
            if (conversation.BootId != request.BootId
                || conversation.Generation != request.Generation
                || conversation.CatalogVersion != request.CatalogVersion)
            {
                return new(409, "conversation_version_mismatch", null);
            }
            if (conversation.TerminalReplyPublished)
            {
                return new(409, "conversation_finished", null);
            }
            if (request.Opening != (conversation.TurnCount == 0))
            {
                return new(409, "opening_required_or_already_started", null);
            }
            if (conversation.TurnCount >= 10)
            {
                return new(429, "turn_limit", null);
            }
            if (conversation.TurnIds.Contains(request.ClientTurnId))
            {
                return new(409, "duplicate_turn_id", null);
            }
            if (!providerAvailable || dialogRunner?.Enabled != true || voiceRunner?.Enabled != true)
            {
                return new(409, "realtime_unavailable", null);
            }
            if (activePaidCalls.Count != 0)
            {
                return new(503, "dialogue_busy", null);
            }
            var memory = eveningMemory?.ReadContext(deviceId, conversationId, request.Text);
            var turn = new RealtimeTurnContext(
                request,
                conversation.CompletedQuestions < ResponsesTextDialogBudget.MinimumQuestions,
                memory?.Transcript ?? "",
                conversation.History.ToArray());
            var correlation = new TurnCorrelation(deviceId, request.BootId, conversationId,
                request.ClientTurnId, request.Generation, request.DeviceRevision);
            var reservation = journal.Reserve(deviceId, route, key, bodyHash, correlation,
                estimatedCostMicroUsd: RealtimeProtocol.ReservationMicroUsd(turn));
            if (reservation.Conflict || reservation.Existing)
            {
                return new(409, "realtime_stream_not_replayable", null);
            }
            if (reservation.CostRejected || reservation.LimitExceeded)
            {
                return new(429, reservation.CostRejected ? "cost_limit" : "turn_limit", null);
            }
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(disconnected, stopping);
            cancellation.CancelAfter(TimeSpan.FromSeconds(45));
            var call = new RealtimeCall(reservation.OperationId, correlation, turn, memory?.EveningId, cancellation);
            activePaidCalls.Add(call.OperationId, new PaidCall(deviceId, conversationId, cancellation));
            RecordAcceptedTurn(conversation, request.ClientTurnId, now);
            return new(200, null, call);
        }
    }

    internal bool StartRealtimeTurn(RealtimeCall call)
    {
        lock (gate)
        {
            return !call.Cancellation.IsCancellationRequested && journal.MarkRunning(call.OperationId);
        }
    }

    internal bool IsRealtimeTurnCurrent(RealtimeCall call)
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            return !call.Cancellation.IsCancellationRequested
                && conversations.ContainsKey(call.Correlation.ConversationId)
                && (call.EveningId is null
                    || eveningMemory!.GetStatus(call.Correlation.DeviceId).EveningId == call.EveningId);
        }
    }

    internal TurnResult? CompleteRealtimeTurn(RealtimeCall call, RealtimeReply response)
    {
        lock (gate)
        {
            if (!journal.SettleCost(call.OperationId, response.CostMicroUsd)
                || !IsRealtimeTurnCurrent(call))
            {
                journal.MarkFailed(call.OperationId);
                return null;
            }
            var conversation = conversations[call.Correlation.ConversationId];
            if (string.IsNullOrWhiteSpace(response.Reply)
                || response.Reply.Length > 500
                || response.VisitorText.Length > 1000
                || response.Reason.Length > 200
                || response.Decision is not ("ask" or "propose" or "no_selection")
                || response.Decision == "propose" && (
                    call.Context.RequireQuestion
                    || response.Compartment is null or < 1 or > 6
                    || (call.Context.Request.ReadyMask & (1 << (response.Compartment.Value - 1))) == 0)
                || response.Decision != "propose" && response.Compartment is not null
                || call.Context.Request.ReadyMask == 0 && response.Decision != "no_selection")
            {
                journal.MarkFailed(call.OperationId);
                return null;
            }
            var correlation = call.Correlation;
            var now = clock.GetUtcNow();
            var proposal = response.Compartment is { } selected
                ? new SelectionProposal(Guid.NewGuid().ToString("D"), correlation.ConversationId,
                    correlation.ClientTurnId, call.Context.Request.CatalogVersion, correlation.DeviceRevision,
                    selected, response.Reason, correlation.BootId, correlation.Generation, now.AddSeconds(60))
                : null;
            var result = new TurnResult(response.Reply, response.Decision, response.Reason, proposal);
            if (call.EveningId is not null)
            {
                eveningMemory!.Append(correlation.DeviceId, call.EveningId, call.OperationId,
                    correlation.ConversationId, response.VisitorText, response.Reply);
            }
            // A memory write failure must leave the operation fail-able rather
            // than publish a completed journal entry without its result.
            if (!journal.MarkCompleted(call.OperationId))
            {
                return null;
            }
            operations.Add(call.OperationId,
                new TurnOperation(call.OperationId, "turn", "completed", correlation, result, null));
            if (response.Decision == "ask")
            {
                conversation.CompletedQuestions++;
            }
            conversation.TerminalReplyPublished = response.Decision is "propose" or "no_selection";
            conversation.History.Add(new DialogExchange(
                response.VisitorText[..Math.Min(response.VisitorText.Length, 500)],
                response.Reply[..Math.Min(response.Reply.Length, 300)]));
            if (conversation.History.Count > 2)
            {
                conversation.History.RemoveAt(0);
            }
            return result;
        }
    }

    internal void ReleaseRealtimeTurn(RealtimeCall call, bool dispatched, bool completed)
    {
        lock (gate)
        {
            if (!completed)
            {
                if (!dispatched)
                {
                    journal.SettleCost(call.OperationId, 0);
                }
                journal.MarkFailed(call.OperationId);
            }
            activePaidCalls.Remove(call.OperationId);
        }
        call.Cancellation.Dispose();
    }
}

internal sealed record RealtimeAdmission(int Status, string? Code, RealtimeCall? Call);
internal sealed record RealtimeCall(
    string OperationId,
    TurnCorrelation Correlation,
    RealtimeTurnContext Context,
    string? EveningId,
    CancellationTokenSource Cancellation);

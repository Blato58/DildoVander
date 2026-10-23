using System.Security.Cryptography;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.PersonaLab;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    internal bool RegisterRealtimeLifetime(RealtimeCall call, CancellationTokenSource lifetime)
    {
        lock (gate)
        {
            return IsRealtimeTurnCurrent(call)
                && realtimeLifetimes.TryAdd(call.Correlation.ConversationId, lifetime);
        }
    }

    internal void ReleaseRealtimeLifetime(string conversation)
    {
        lock (gate)
        {
            realtimeLifetimes.Remove(conversation);
        }
    }

    internal RealtimeAdmission BeginRealtimeTurn(
        string deviceId,
        string conversationId,
        Guid key,
        byte[] body,
        RealtimeTurnRequest request,
        bool providerAvailable,
        CancellationToken disconnected,
        CancellationToken stopping,
        bool handsFree = false,
        bool gptLive = false)
    {
        if (!RealtimeProtocol.IsValid(request, handsFree) || gptLive && request.Speed != 1)
        {
            return new(400, "invalid_request", null);
        }
        var route = $"/v1/conversations/{conversationId}/{(gptLive ? "gpt-live" : "realtime-turns")}";
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
            var hardware = conversation.LabHardware;
            if (hardware is not null)
            {
                if (hardware.Token.IsCancellationRequested || hardware.ExpiresAt <= now)
                { return new(409, "lab_hardware_ended", null); }
                if ((hardware.Session.Engine == GptLiveProtocol.Model) != gptLive
                    || conversation.TurnCount == 0 && request.DeviceRevision != hardware.Target.ExpectedRevision)
                { return new(409, "lab_hardware_configuration_mismatch", null); }
                request = request with { Voice = hardware.Session.Config.Voice };
            }
            if (!conversation.AudioConsented)
            {
                return new(403, "audio_consent_required", null);
            }
            if (conversation.BootId != request.BootId
                || conversation.Generation != request.Generation
                || !OperationEpoch.Same(conversation.OperationEpoch, request.OperationEpoch)
                || conversation.CatalogVersion != request.CatalogVersion)
            {
                return new(409, "conversation_version_mismatch", null);
            }
            if (conversation.TerminalReplyPublished)
            {
                return new(409, "conversation_finished", null);
            }
            if (conversation.LivePrompt.Version == "dv01-adult-v1" && conversation.LivePrompt.CatalogVersion != request.CatalogVersion)
            {
                return new(409, "conversation_version_mismatch", null);
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
            if (!providerAvailable)
            {
                return new(409, "realtime_unavailable", null);
            }
            if (realtimeLifetimes.TryGetValue(conversationId, out var existingLifetime)
                && existingLifetime.Token != disconnected)
            {
                return new(409, "conversation_stream_active", null);
            }
            if (activePaidCalls.Values.Any(call => call.ConversationId == conversationId && !call.IsImageAnalysis))
            {
                return new(503, "dialogue_busy", null);
            }
            var memory = hardware is null ? eveningMemory?.ReadContext(deviceId, conversationId, request.Text) : null;
            var turn = new RealtimeTurnContext(
                request,
                conversation.CompletedQuestions < PersonalityCatalog.MinimumQuestions(conversation.PersonalityId),
                memory?.Transcript ?? "",
                conversation.History.ToArray(),
                LiveAudio: handsFree && !request.Opening,
                OutputTokenLimit: handsFree ? 768 : RealtimeProtocol.MaxOutputTokens)
            { PersonalityId = conversation.PersonalityId, LivePrompt = conversation.LivePrompt,
                ConversationLanguage = conversation.ConversationLanguage,
                CostumeContext = CostumePrompt(conversation) };
            if (hardware is not null) { turn = LabTurnContext(turn, hardware); }
            if (gptLive)
            {
                conversation.LiveReadyMask = request.ReadyMask;
                conversation.LiveAvailabilityRevision = request.DeviceRevision;
                conversation.LiveOperationEpoch = request.OperationEpoch;
            }
            var correlation = new TurnCorrelation(deviceId, request.BootId, conversationId,
                request.ClientTurnId, request.Generation, request.DeviceRevision, request.OperationEpoch);
            var reservation = journal.Reserve(deviceId, route, key, bodyHash, correlation,
                estimatedCostMicroUsd: gptLive ? GptLiveProtocol.SessionReservationMicroUsd : RealtimeProtocol.ReservationMicroUsd(turn));
            if (reservation.Conflict || reservation.Existing)
            {
                return new(409, "realtime_stream_not_replayable", null);
            }
            if (reservation.LimitExceeded)
            {
                return new(429, "turn_limit", null);
            }
            var cancellation = hardware is null
                ? CancellationTokenSource.CreateLinkedTokenSource(disconnected, stopping)
                : CancellationTokenSource.CreateLinkedTokenSource(disconnected, stopping, hardware.Token);
            cancellation.CancelAfter(TimeSpan.FromSeconds(gptLive ? 180 : 45));
            var call = new RealtimeCall(reservation.OperationId, correlation, turn, memory?.EveningId, cancellation)
            {
                SessionGeneration = conversation.LiveGeneration,
                MemoryDeletionEpoch = hardware is null ? eveningMemory?.MemoryDeletionEpochFor(deviceId, conversationId) ?? 0 : 0,
                LabHardware = hardware
            };
            if (hardware is not null)
            {
                personaLab?.Change(hardware.Session.OwnerId, hardware.Session.Id, value => value.Hardware is { } metadata
                    ? value with { Hardware = metadata with { OperationId = call.OperationId } } : value);
            }
            activePaidCalls.Add(call.OperationId, new PaidCall(deviceId, conversationId, cancellation));
            if (conversation.GuestTone is { } guestTone)
            {
                call = call with { Context = call.Context with { GuestTone = BindGuestTone(call, guestTone) } };
            }
            RecordAcceptedTurn(conversation, request.ClientTurnId, now);
            return new(200, null, call);
        }
    }

    internal bool StartRealtimeTurn(RealtimeCall call)
    {
        lock (gate)
        {
            return IsRealtimeTurnCurrent(call) && journal.MarkRunning(call.OperationId);
        }
    }

    internal bool IsRealtimeTurnCurrent(RealtimeCall call)
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            return !call.Cancellation.IsCancellationRequested
                && activePaidCalls.ContainsKey(call.OperationId)
                && conversations.TryGetValue(call.Correlation.ConversationId, out var conversation)
                && conversation.DeviceId == call.Correlation.DeviceId
                && OperationEpoch.Same(conversation.OperationEpoch, call.Correlation.OperationEpoch)
                && OperationEpoch.Same(call.Context.Request.OperationEpoch, call.Correlation.OperationEpoch)
                && conversation.LiveGeneration == call.SessionGeneration
                && (call.LabHardware is not null || eveningMemory is null || eveningMemory.MemoryDeletionEpochFor(
                    call.Correlation.DeviceId, call.Correlation.ConversationId) == call.MemoryDeletionEpoch)
                && (call.EveningId is null
                    || eveningMemory!.GetStatus(call.Correlation.DeviceId).EveningId == call.EveningId);
        }
    }

    internal TurnResult? CompleteRealtimeTurn(RealtimeCall call, RealtimeReply response, bool archive = true)
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
                    selected, response.Reason, correlation.BootId, correlation.Generation, now.AddSeconds(60),
                    correlation.OperationEpoch)
                : null;
            var result = new TurnResult(response.Reply, response.Decision, response.Reason, proposal);
            if (archive && call.TranscriptEvents.Count == 0 && call.EveningId is not null)
            {
                if (!eveningMemory!.Append(correlation.DeviceId, call.EveningId, call.OperationId,
                    correlation.ConversationId, response.VisitorText, response.Reply, expectedDeletionEpoch: call.MemoryDeletionEpoch))
                {
                    return null;
                }
            }
            // A memory write failure must leave the operation fail-able rather
            // than publish a completed journal entry without its result.
            if (!journal.MarkSelectionCompleted(call.OperationId, proposal,
                call.LabHardware is null ? eveningMemory?.ReadCapturedShowEvening(call.Correlation.DeviceId, call.Correlation.ConversationId) ?? call.EveningId : null,
                call.MemoryDeletionEpoch, call.Context.PersonalityId))
            {
                return null;
            }
            if (call.LabHardware is null) { BindFinaleSnapshot(correlation.DeviceId, call.OperationId, proposal, call.SessionGeneration,
                call.Context.Request.DeviceRevision, call.Context.LivePrompt?.Version ?? "realtime-existing", null,
                eveningMemory?.ReadCapturedShowEvening(correlation.DeviceId, correlation.ConversationId) ?? call.EveningId); }
            ObserveLabCompletion(call, response.Reply, response.CostMicroUsd, response.Decision, response.Compartment, proposal);
            operations.Add(call.OperationId,
                new TurnOperation(call.OperationId, "turn", "completed", correlation, result, null));
            if (response.Decision == "ask")
            {
                conversation.CompletedQuestions++;
            }
            conversation.TerminalReplyPublished = response.Decision is "propose" or "no_selection";
            if (call.TranscriptEvents.Count == 0)
            {
                conversation.History.Add(new DialogExchange(response.VisitorText, response.Reply));
            }
            return result;
        }
    }

    internal TurnResult? CompleteGptLive(RealtimeCall call, LiveOutcome outcome)
    {
        lock (gate)
        {
            if (!IsRealtimeTurnCurrent(call))
                return null;
            if (outcome.Autonomous)
                return CompleteAutonomousLive(call, outcome);
            var completed = call with { Context = call.Context with
            {
                RequireQuestion = outcome.Questions < PersonalityCatalog.MinimumQuestions(call.Context.PersonalityId)
            } };
            // Archive actual frontend transcripts, never an unspoken Sol draft.
            if (call.EveningId is not null)
                for (var i = 0; i < outcome.Transcript.Count; i++)
                {
                    var fragment = outcome.Transcript[i];
                    eveningMemory!.Append(call.Correlation.DeviceId, call.EveningId,
                        call.OperationId + ":live:" + i, call.Correlation.ConversationId, fragment.Visitor, fragment.Reply);
                }
            return CompleteRealtimeTurn(completed, outcome.Reply, archive: false);
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
    CancellationTokenSource Cancellation)
{
    internal LabHardwareContext? LabHardware { get; init; }
    internal string SessionGeneration { get; init; } = Guid.NewGuid().ToString("D");
    internal long MemoryDeletionEpoch { get; init; }
    // Accessed only under ConversationStore.gate. Provider event identities
    // make final transcript persistence idempotent across duplicate callbacks.
    internal HashSet<string> TranscriptEvents { get; } = new(StringComparer.Ordinal);
}

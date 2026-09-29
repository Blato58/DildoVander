using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Conversations;

internal sealed class ConversationStore(TimeProvider clock, OperationJournal journal)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, ConversationEntry> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnOperation> operations = new(StringComparer.Ordinal);

    internal int CachedOperationCount
    {
        get
        {
            lock (gate)
            {
                return operations.Count;
            }
        }
    }

    public CreateResult Create(string deviceId, Guid key, ReadOnlySpan<byte> requestBody, CreateConversationRequest request)
    {
        var now = clock.GetUtcNow();
        var bodyHash = Convert.ToHexString(SHA256.HashData(requestBody));

        lock (gate)
        {
            CleanupExpired(now);

            var previous = journal.FindExistingCreate(deviceId, key, bodyHash);
            if (previous is not null)
            {
                return previous.Conflict
                    ? new CreateResult(409, null)
                    : new CreateResult(201, previous.Response);
            }

            if (conversations.Values.Any(entry => entry.DeviceId == deviceId))
            {
                return new CreateResult(409, null);
            }

            var reservation = journal.ReserveCreate(deviceId, key, bodyHash);
            if (reservation.Conflict)
            {
                return new CreateResult(409, null);
            }

            if (reservation.Existing)
            {
                return new CreateResult(201, reservation.Response);
            }

            var response = reservation.Response!;

            conversations.Add(
                response.ConversationId,
                new ConversationEntry(
                    deviceId,
                    request.BootId,
                    request.Generation,
                    request.CatalogVersion,
                    request.Consent.Audio,
                    response.ExpiresAt,
                    now.AddMinutes(30)));
            return new CreateResult(201, response);
        }
    }

    public SubmitTurnResult SubmitTurn(
        string deviceId,
        string conversationId,
        Guid key,
        ReadOnlySpan<byte> requestBody,
        TextTurnRequest request)
    {
        var now = clock.GetUtcNow();
        var route = $"/v1/conversations/{conversationId}/turns";
        var bodyHash = Convert.ToHexString(SHA256.HashData(requestBody));

        lock (gate)
        {
            CleanupExpired(now);

            var previous = journal.FindExisting(deviceId, route, key, bodyHash);
            if (previous is not null)
            {
                return previous.Conflict
                    ? new SubmitTurnResult(409, null, "idempotency_conflict")
                    : new SubmitTurnResult(202, previous.OperationId, null);
            }

            if (!conversations.TryGetValue(conversationId, out var conversation))
            {
                return new SubmitTurnResult(404, null, "conversation_not_found");
            }

            if (conversation.DeviceId != deviceId)
            {
                return new SubmitTurnResult(403, null, "device_scope_mismatch");
            }

            if (request.BootId != conversation.BootId
                || request.Generation != conversation.Generation
                || request.CatalogVersion != conversation.CatalogVersion)
            {
                return new SubmitTurnResult(409, null, "conversation_version_mismatch");
            }

            if (conversation.TurnCount >= 10)
            {
                return new SubmitTurnResult(429, null, "turn_limit");
            }

            if (conversation.TurnIds.Contains(request.ClientTurnId))
            {
                return new SubmitTurnResult(409, null, "duplicate_turn_id");
            }

            // The fake adapter does not inspect or retain the visitor's text.
            var correlation = new TurnCorrelation(
                deviceId,
                request.BootId,
                conversationId,
                request.ClientTurnId,
                request.Generation,
                request.DeviceRevision);
            var reservation = journal.Reserve(deviceId, route, key, bodyHash, correlation);
            if (reservation.Conflict)
            {
                return new SubmitTurnResult(409, null, "idempotency_conflict");
            }

            if (reservation.LimitExceeded)
            {
                return new SubmitTurnResult(429, null, "turn_limit");
            }

            if (reservation.Existing)
            {
                return new SubmitTurnResult(202, reservation.OperationId, null);
            }

            var operationId = reservation.OperationId;
            var operation = new TurnOperation(
                operationId,
                "turn",
                "completed",
                correlation,
                new TurnResult("Toto je testovací odpověď bez AI.", "ask", "", null),
                null);

            if (!journal.MarkCompleted(operationId))
            {
                return new SubmitTurnResult(409, null, "operation_cancelled");
            }

            operations.Add(operationId, operation);
            conversation.TurnIds.Add(request.ClientTurnId);
            conversation.TurnCount++;
            var idleExpiresAt = now.AddMinutes(15);
            conversation.ExpiresAt = idleExpiresAt < conversation.AbsoluteExpiresAt
                ? idleExpiresAt
                : conversation.AbsoluteExpiresAt;
            return new SubmitTurnResult(202, operationId, null);
        }
    }

    public SubmitTurnResult SubmitTranscription(
        string deviceId,
        string conversationId,
        Guid key,
        TranscriptionUpload upload)
    {
        var now = clock.GetUtcNow();
        var route = $"/v1/conversations/{conversationId}/transcriptions";
        var audioHash = Convert.ToHexString(SHA256.HashData(upload.Wav));
        var canonicalBody = $"{upload.ClientTurnId:D}|{upload.BootId:D}|{upload.Generation}|{upload.DeviceRevision}|{audioHash}";
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalBody)));

        lock (gate)
        {
            CleanupExpired(now);
            var previous = journal.FindExisting(deviceId, route, key, bodyHash);
            if (previous is not null)
            {
                return previous.Conflict
                    ? new SubmitTurnResult(409, null, "idempotency_conflict")
                    : new SubmitTurnResult(202, previous.OperationId, null);
            }

            if (!conversations.TryGetValue(conversationId, out var conversation))
            {
                return new SubmitTurnResult(404, null, "conversation_not_found");
            }

            if (conversation.DeviceId != deviceId)
            {
                return new SubmitTurnResult(403, null, "device_scope_mismatch");
            }

            if (!conversation.AudioConsented)
            {
                return new SubmitTurnResult(403, null, "audio_consent_required");
            }

            if (upload.BootId != conversation.BootId || upload.Generation != conversation.Generation)
            {
                return new SubmitTurnResult(409, null, "conversation_version_mismatch");
            }

            if (conversation.TranscriptionIds.Count >= 10)
            {
                return new SubmitTurnResult(429, null, "transcription_limit");
            }

            if (conversation.TranscriptionIds.Contains(upload.ClientTurnId))
            {
                return new SubmitTurnResult(409, null, "duplicate_turn_id");
            }

            var correlation = new TurnCorrelation(
                deviceId,
                upload.BootId,
                conversationId,
                upload.ClientTurnId,
                upload.Generation,
                upload.DeviceRevision);
            var reservation = journal.Reserve(
                deviceId,
                route,
                key,
                bodyHash,
                correlation,
                kind: "transcription",
                consumesTurn: false);
            if (reservation.Conflict)
            {
                return new SubmitTurnResult(409, null, "idempotency_conflict");
            }

            if (reservation.Existing)
            {
                return new SubmitTurnResult(202, reservation.OperationId, null);
            }

            if (!journal.MarkCompleted(reservation.OperationId))
            {
                return new SubmitTurnResult(409, null, "operation_cancelled");
            }

            operations.Add(
                reservation.OperationId,
                new TurnOperation(
                    reservation.OperationId,
                    "transcription",
                    "completed",
                    correlation,
                    new TranscriptionResult("Testovací přepis bez STT.", "deviceMicrophone"),
                    null));
            conversation.TranscriptionIds.Add(upload.ClientTurnId);
            var idleExpiresAt = now.AddMinutes(15);
            conversation.ExpiresAt = idleExpiresAt < conversation.AbsoluteExpiresAt
                ? idleExpiresAt
                : conversation.AbsoluteExpiresAt;
            return new SubmitTurnResult(202, reservation.OperationId, null);
        }
    }

    public GetOperationResult GetOperation(string deviceId, string operationId)
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            var recorded = journal.GetOperation(deviceId, operationId);
            if (recorded.Status != 200)
            {
                if (recorded.Status == 404)
                {
                    operations.Remove(operationId);
                }

                return recorded;
            }

            return recorded.Operation?.Status == "failed"
                && operations.TryGetValue(operationId, out var operation)
                ? new GetOperationResult(200, operation)
                : recorded;
        }
    }

    public MutationResult CancelOperation(string deviceId, string operationId, Guid key)
    {
        lock (gate)
        {
            var result = journal.CancelOperation(deviceId, operationId, key);
            if (result.Status == 204)
            {
                operations.Remove(operationId);
            }

            return result;
        }
    }

    public MutationResult EndConversation(string deviceId, string conversationId, Guid key)
    {
        lock (gate)
        {
            var route = $"DELETE:/v1/conversations/{conversationId}";
            var previous = journal.FindMutation(deviceId, route, key);
            if (previous is not null)
            {
                return previous;
            }

            CleanupExpired(clock.GetUtcNow());
            if (!conversations.TryGetValue(conversationId, out var conversation))
            {
                return new MutationResult(404, false);
            }

            if (conversation.DeviceId != deviceId)
            {
                return new MutationResult(403, false);
            }

            var result = journal.EndConversation(deviceId, conversationId, key);
            if (result.Status == 204)
            {
                RemoveConversation(conversationId);
            }

            return result;
        }
    }

    public void SweepExpired()
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
        }
    }

    private void CleanupExpired(DateTimeOffset now)
    {
        foreach (var key in conversations.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            RemoveConversation(key);
        }
    }

    private void RemoveConversation(string conversationId)
    {
        conversations.Remove(conversationId);
        foreach (var operationId in operations
            .Where(pair => pair.Value.Correlation.ConversationId == conversationId)
            .Select(pair => pair.Key)
            .ToArray())
        {
            operations.Remove(operationId);
        }
    }

    private sealed class ConversationEntry(
        string deviceId,
        Guid bootId,
        int generation,
        string catalogVersion,
        bool audioConsented,
        DateTimeOffset expiresAt,
        DateTimeOffset absoluteExpiresAt)
    {
        public string DeviceId { get; } = deviceId;
        public Guid BootId { get; } = bootId;
        public int Generation { get; } = generation;
        public string CatalogVersion { get; } = catalogVersion;
        public bool AudioConsented { get; } = audioConsented;
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public DateTimeOffset AbsoluteExpiresAt { get; } = absoluteExpiresAt;
        public int TurnCount { get; set; }
        public HashSet<Guid> TurnIds { get; } = [];
        public HashSet<Guid> TranscriptionIds { get; } = [];
    }

}

internal sealed record CreateResult(int Status, ConversationResponse? Response);
internal sealed record ConversationResponse(string ConversationId, DateTimeOffset ExpiresAt, ConversationLimits Limits);
internal sealed record ConversationLimits(int MaxTurns, int MaxTextCharacters, int IdleSeconds, int MaxLifetimeSeconds);
internal sealed record ConversationConsent(bool Audio, bool Image);
internal sealed record CreateConversationRequest(
    string DeviceId,
    Guid BootId,
    int Generation,
    string CatalogVersion,
    string Locale,
    ConversationConsent Consent);
internal sealed record TextTurnRequest(
    Guid ClientTurnId,
    Guid BootId,
    int Generation,
    string Text,
    int ReadyMask,
    int DeviceRevision,
    string CatalogVersion);
internal sealed record SubmitTurnResult(int Status, string? OperationId, string? ErrorCode);
internal sealed record GetOperationResult(int Status, TurnOperation? Operation);
internal sealed record TurnCorrelation(
    string DeviceId,
    Guid BootId,
    string ConversationId,
    Guid ClientTurnId,
    int Generation,
    int DeviceRevision);
internal sealed record TurnResult(string Reply, string Decision, string Reason, object? Proposal);
internal sealed record TranscriptionResult(string Transcript, string Source);
internal sealed record TurnOperation(
    string OperationId,
    string Kind,
    string Status,
    TurnCorrelation Correlation,
    object? Result,
    object? Error);

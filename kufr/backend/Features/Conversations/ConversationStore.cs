using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kufr.Backend.Features.Conversations;

internal sealed class ConversationStore(
    TimeProvider clock,
    OperationJournal journal,
    TextDialogRunner? dialogRunner = null,
    ILogger<ConversationStore>? logger = null)
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, ConversationEntry> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnOperation> operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaidCall> activePaidCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioEntry> audio = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AudioTombstone> expiredAudio = new(StringComparer.Ordinal);

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

    internal int CachedAudioCount
    {
        get
        {
            lock (gate)
            {
                return audio.Count;
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

            var paid = dialogRunner?.Enabled == true;
            if (paid && !dialogRunner!.TryAcquire())
            {
                return new SubmitTurnResult(503, null, "dialogue_busy");
            }

            var queued = false;
            try
            {
                // The journal records only a body hash, never the visitor's text.
                var correlation = new TurnCorrelation(
                    deviceId,
                    request.BootId,
                    conversationId,
                    request.ClientTurnId,
                    request.Generation,
                    request.DeviceRevision);
                var reservation = journal.Reserve(
                    deviceId,
                    route,
                    key,
                    bodyHash,
                    correlation,
                    estimatedCostMicroUsd: paid ? ResponsesTextDialogBudget.ReservedMicroUsd : 0);
                if (reservation.Conflict)
                {
                    return new SubmitTurnResult(409, null, "idempotency_conflict");
                }

                if (reservation.LimitExceeded)
                {
                    return new SubmitTurnResult(429, null, "turn_limit");
                }

                if (reservation.CostRejected)
                {
                    return new SubmitTurnResult(429, null, "cost_limit");
                }

                if (reservation.Existing)
                {
                    return new SubmitTurnResult(202, reservation.OperationId, null);
                }

                var operationId = reservation.OperationId;
                if (paid)
                {
                    RecordAcceptedTurn(conversation, request.ClientTurnId, now);
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        dialogRunner!.Stopping);
                    activePaidCalls.Add(
                        operationId,
                        new PaidCall(conversationId, cancellation));
                    var history = conversation.History.ToArray();
                    var completedQuestions = conversation.CompletedQuestions;
                    _ = Task.Run(() => ProcessPaidTurnAsync(
                        operationId,
                        correlation,
                        request.Text,
                        request.ReadyMask,
                        request.CatalogVersion,
                        history,
                        completedQuestions,
                        cancellation));
                    queued = true;
                    return new SubmitTurnResult(202, operationId, null);
                }

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
                RecordAcceptedTurn(conversation, request.ClientTurnId, now);
                return new SubmitTurnResult(202, operationId, null);
            }
            finally
            {
                if (paid && !queued)
                {
                    dialogRunner!.Release();
                }
            }
        }
    }

    private async Task ProcessPaidTurnAsync(
        string operationId,
        TurnCorrelation correlation,
        string visitorText,
        int readyMask,
        string catalogVersion,
        IReadOnlyList<DialogExchange> history,
        int completedQuestions,
        CancellationTokenSource cancellation)
    {
        try
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested)
                {
                    journal.SettleCost(operationId, 0);
                    journal.MarkFailed(operationId);
                    return;
                }

                if (!journal.MarkRunning(operationId))
                {
                    // A cancellation before execution has no provider charge.
                    journal.SettleCost(operationId, 0);
                    return;
                }
            }

            var response = await dialogRunner!.GenerateAsync(
                visitorText,
                readyMask,
                history,
                completedQuestions < 2 && readyMask != 0,
                cancellation.Token);
            var actualCost = ResponsesTextDialogBudget.CalculateActualMicroUsd(response);
            lock (gate)
            {
                if (!journal.SettleCost(operationId, actualCost))
                {
                    journal.MarkFailed(operationId);
                    return;
                }

                var now = clock.GetUtcNow();
                CleanupExpired(now);
                if (!conversations.TryGetValue(correlation.ConversationId, out var conversation))
                {
                    journal.MarkFailed(operationId);
                    return;
                }

                // A provider proposal before two completed questions cannot reach the device.
                if (response.Decision == "propose" && conversation.CompletedQuestions < 2)
                {
                    journal.MarkFailed(operationId);
                    return;
                }

                object? proposal = response.Compartment is { } compartment
                    ? new SelectionProposal(
                        Guid.NewGuid().ToString("D"),
                        correlation.ConversationId,
                        correlation.ClientTurnId,
                        catalogVersion,
                        correlation.DeviceRevision,
                        compartment,
                        response.Reason,
                        correlation.BootId,
                        correlation.Generation,
                        now.AddSeconds(60))
                    : null;
                operations.Add(
                    operationId,
                    new TurnOperation(
                        operationId,
                        "turn",
                        "completed",
                        correlation,
                        new TurnResult(response.Reply, response.Decision, response.Reason, proposal),
                        null));
                if (!journal.MarkCompleted(operationId))
                {
                    operations.Remove(operationId);
                    return;
                }

                if (response.Decision == "ask")
                {
                    conversation.CompletedQuestions++;
                }

                conversation.History.Add(new DialogExchange(
                    visitorText[..Math.Min(visitorText.Length, 500)],
                    response.Reply[..Math.Min(response.Reply.Length, 300)]));
                if (conversation.History.Count > 2)
                {
                    conversation.History.RemoveAt(0);
                }
            }
        }
        catch (TextDialogRequestTooLargeException)
        {
            lock (gate)
            {
                // Payload validation failed before the provider request was sent.
                journal.SettleCost(operationId, 0);
                journal.MarkFailed(operationId);
            }
        }
        catch (Exception)
        {
            logger?.LogWarning("Text dialog failed for operation {OperationId}.", operationId);
            try
            {
                lock (gate)
                {
                    // An interrupted provider call may still be billed. Keep its
                    // reservation until the normal audit retention window.
                    journal.MarkFailed(operationId);
                }
            }
            catch (Exception)
            {
                logger?.LogError("Could not record failure for operation {OperationId}.", operationId);
            }
        }
        finally
        {
            lock (gate)
            {
                activePaidCalls.Remove(operationId);
            }

            cancellation.Dispose();
            dialogRunner!.Release();
        }
    }

    private static void RecordAcceptedTurn(
        ConversationEntry conversation,
        Guid turnId,
        DateTimeOffset now)
    {
        conversation.TurnIds.Add(turnId);
        conversation.TurnCount++;
        var idleExpiresAt = now.AddMinutes(15);
        conversation.ExpiresAt = idleExpiresAt < conversation.AbsoluteExpiresAt
            ? idleExpiresAt
            : conversation.AbsoluteExpiresAt;
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

    public SubmitTurnResult SubmitSpeech(
        string deviceId,
        string conversationId,
        Guid key,
        ReadOnlySpan<byte> requestBody,
        SpeechRequest request)
    {
        var now = clock.GetUtcNow();
        var route = $"/v1/conversations/{conversationId}/speech";
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

            if (conversation.DeviceId != deviceId || !conversation.AudioConsented)
            {
                return new SubmitTurnResult(403, null, conversation.DeviceId != deviceId
                    ? "device_scope_mismatch"
                    : "audio_consent_required");
            }

            if (conversation.SpeechCount >= 10)
            {
                return new SubmitTurnResult(429, null, "speech_limit");
            }

            if (conversation.ActiveAudioId is not null)
            {
                return new SubmitTurnResult(409, null, "speech_output_active");
            }

            var source = operations.Values.FirstOrDefault(operation =>
                operation.Kind == "turn"
                && operation.Status == "completed"
                && operation.Correlation.ConversationId == conversationId
                && operation.Correlation.ClientTurnId == request.TurnId);
            if (source is null)
            {
                return new SubmitTurnResult(404, null, "turn_not_found");
            }

            var reservation = journal.Reserve(
                deviceId,
                route,
                key,
                bodyHash,
                source.Correlation,
                kind: "speech",
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

            var audioId = Guid.NewGuid().ToString("D");
            var expiresAt = now.AddSeconds(120);
            audio.Add(audioId, new AudioEntry(deviceId, conversationId, reservation.OperationId, expiresAt, BuildFakeWav()));
            operations.Add(
                reservation.OperationId,
                new TurnOperation(
                    reservation.OperationId,
                    "speech",
                    "completed",
                    source.Correlation,
                    new SpeechResult(audioId, expiresAt, request.OutputTarget),
                    null));
            conversation.ActiveAudioId = audioId;
            conversation.SpeechCount++;
            var idleExpiresAt = now.AddMinutes(15);
            conversation.ExpiresAt = idleExpiresAt < conversation.AbsoluteExpiresAt
                ? idleExpiresAt
                : conversation.AbsoluteExpiresAt;
            return new SubmitTurnResult(202, reservation.OperationId, null);
        }
    }

    public AudioReadResult GetAudio(string deviceId, string audioId)
    {
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            if (audio.TryGetValue(audioId, out var entry))
            {
                return entry.DeviceId == deviceId
                    ? new AudioReadResult(200, entry.Wav.ToArray())
                    : new AudioReadResult(403, null);
            }

            if (expiredAudio.TryGetValue(audioId, out var tombstone))
            {
                return new AudioReadResult(tombstone.DeviceId == deviceId ? 410 : 403, null);
            }

            return new AudioReadResult(404, null);
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
                if (activePaidCalls.TryGetValue(operationId, out var paidCall))
                {
                    paidCall.Cancellation.Cancel();
                }

                operations.Remove(operationId);
                foreach (var audioId in audio.Where(pair => pair.Value.OperationId == operationId)
                    .Select(pair => pair.Key).ToArray())
                {
                    RemoveAudio(audioId, expired: false, clock.GetUtcNow());
                }
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
        foreach (var audioId in audio.Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key).ToArray())
        {
            RemoveAudio(audioId, expired: true, now);
        }

        foreach (var audioId in expiredAudio.Where(pair => pair.Value.RemoveAt <= now)
            .Select(pair => pair.Key).ToArray())
        {
            expiredAudio.Remove(audioId);
        }

        foreach (var key in conversations.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            RemoveConversation(key);
        }
    }

    private void RemoveConversation(string conversationId)
    {
        if (conversations.Remove(conversationId, out var conversation))
        {
            conversation.History.Clear();
        }
        foreach (var paidCall in activePaidCalls.Values
            .Where(call => call.ConversationId == conversationId))
        {
            paidCall.Cancellation.Cancel();
        }

        foreach (var audioId in audio.Where(pair => pair.Value.ConversationId == conversationId)
            .Select(pair => pair.Key).ToArray())
        {
            RemoveAudio(audioId, expired: false, clock.GetUtcNow());
        }

        foreach (var operationId in operations
            .Where(pair => pair.Value.Correlation.ConversationId == conversationId)
            .Select(pair => pair.Key)
            .ToArray())
        {
            operations.Remove(operationId);
        }
    }

    private void RemoveAudio(string audioId, bool expired, DateTimeOffset now)
    {
        if (!audio.Remove(audioId, out var entry))
        {
            return;
        }

        CryptographicOperations.ZeroMemory(entry.Wav);
        if (conversations.TryGetValue(entry.ConversationId, out var conversation)
            && conversation.ActiveAudioId == audioId)
        {
            conversation.ActiveAudioId = null;
        }

        if (expired)
        {
            expiredAudio[audioId] = new AudioTombstone(entry.DeviceId, now.AddHours(24));
        }
    }

    private static byte[] BuildFakeWav()
    {
        const int sampleBytes = 6400;
        var wav = new byte[44 + sampleBytes];
        "RIFF"u8.CopyTo(wav.AsSpan(0));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(4), (uint)(wav.Length - 8));
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(24), 32000);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(28), 64000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40), sampleBytes);
        return wav;
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
        public int CompletedQuestions { get; set; }
        public List<DialogExchange> History { get; } = [];
        public HashSet<Guid> TurnIds { get; } = [];
        public HashSet<Guid> TranscriptionIds { get; } = [];
        public int SpeechCount { get; set; }
        public string? ActiveAudioId { get; set; }
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
internal sealed record SelectionProposal(
    string ProposalId,
    string ConversationId,
    Guid TurnId,
    string CatalogVersion,
    int DeviceRevision,
    int Compartment,
    string Reason,
    Guid BootId,
    int Generation,
    DateTimeOffset ExpiresAt);
internal sealed record TranscriptionResult(string Transcript, string Source);
internal sealed record SpeechRequest(Guid TurnId, string OutputTarget);
internal sealed record SpeechResult(string AudioId, DateTimeOffset ExpiresAt, string OutputTarget);
internal sealed record AudioReadResult(int Status, byte[]? Wav);
internal sealed record AudioEntry(string DeviceId, string ConversationId, string OperationId, DateTimeOffset ExpiresAt, byte[] Wav);
internal sealed record AudioTombstone(string DeviceId, DateTimeOffset RemoveAt);
internal sealed record PaidCall(string ConversationId, CancellationTokenSource Cancellation);
internal sealed record TurnOperation(
    string OperationId,
    string Kind,
    string Status,
    TurnCorrelation Correlation,
    object? Result,
    object? Error);

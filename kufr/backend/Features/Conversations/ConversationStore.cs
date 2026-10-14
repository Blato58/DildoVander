using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.DigitalTwin;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore(
    TimeProvider clock,
    OperationJournal journal,
    TextDialogRunner? dialogRunner = null,
    ILogger<ConversationStore>? logger = null,
    EveningMemoryStore? eveningMemory = null,
    VoiceProviderRunner? voiceRunner = null,
    GptLiveOptions? liveOptions = null,
    TwinFakeInputPolicy? twinFakeInputs = null)
{
    private readonly Lock gate = new();
    private CharacterPolicy characterPolicy = liveOptions?.Character ?? new();
    private readonly Dictionary<string, ConversationEntry> conversations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TurnOperation> operations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PaidCall> activePaidCalls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> realtimeLifetimes = new(StringComparer.Ordinal);
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
                    personalityId,
                    response.ExpiresAt,
                    now.AddMinutes(30)) { ImageConsented = request.Consent.Image, LivePrompt = LivePromptSnapshot.Create(personalityId, (liveOptions ?? new()) with { Character = characterPolicy }) });
            return new CreateResult(201, response);
        }
    }

    public SubmitTurnResult SubmitTurn(
        string deviceId,
        string conversationId,
        Guid key,
        ReadOnlySpan<byte> requestBody,
        TextTurnRequest request,
        bool opening = false)
    {
        var now = clock.GetUtcNow();
        var route = $"/v1/conversations/{conversationId}/{(opening ? "opening" : "turns")}";
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

            if (conversation.TerminalReplyPublished)
            {
                return new SubmitTurnResult(409, null, "conversation_finished");
            }

            if (opening && (conversation.TurnCount != 0 || request.Text.Length != 0))
            {
                return new SubmitTurnResult(409, null, "opening_already_started");
            }

            if (conversation.TurnCount >= 10)
            {
                return new SubmitTurnResult(429, null, "turn_limit");
            }

            if (conversation.TurnIds.Contains(request.ClientTurnId))
            {
                return new SubmitTurnResult(409, null, "duplicate_turn_id");
            }

            var paid = dialogRunner?.Enabled == true && twinFakeInputs?.Allows(deviceId) != true;
            if ((paid && activePaidCalls.Count != 0) || HasActiveCall(conversationId))
            {
                return paid
                    ? new SubmitTurnResult(503, null, "dialogue_busy")
                    : new SubmitTurnResult(409, null, "operation_active");
            }

            if (paid && !dialogRunner!.TryAcquire())
            {
                return new SubmitTurnResult(503, null, "dialogue_busy");
            }

            var queued = false;
            EveningContext? memory = null;
            try
            {
                memory = eveningMemory?.ReadContext(deviceId, conversationId, request.Text);
                // The technical journal remains free of visitor content.
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
                    estimatedCostMicroUsd: paid
                        ? ResponsesTextDialogBudget.ReserveForContext((memory?.Transcript ?? "") + CostumePrompt(conversation), dialogRunner!.Model, conversation.PersonalityId)
                        : 0);
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
                if (paid)
                {
                    RecordAcceptedTurn(conversation, request.ClientTurnId, now);
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        dialogRunner!.Stopping);
                    activePaidCalls.Add(
                        operationId,
                        new PaidCall(deviceId, conversationId, cancellation));
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
                        conversation.PersonalityId,
                        memory,
                        CostumePrompt(conversation),
                        cancellation));
                    queued = true;
                    return new SubmitTurnResult(202, operationId, null);
                }

                var syntheticDialogue = deviceId == "debug-server-admin" || twinFakeInputs?.Allows(deviceId) == true;
                var turnResult = syntheticDialogue
                    ? BuildAdminFakeTurn(conversation, request, conversationId, opening, now)
                    : new TurnResult(
                        opening ? "Jak ti říkají?" : "Toto je testovací odpověď bez AI.",
                        "ask", "", null);
                var operation = new TurnOperation(
                    operationId,
                    "turn",
                    "completed",
                    correlation,
                    turnResult,
                    null);

                eveningMemory?.Append(deviceId, memory!.EveningId, operationId,
                    conversationId, request.Text, turnResult.Reply);
                if (!journal.MarkCompleted(operationId))
                {
                    return new SubmitTurnResult(409, null, "operation_cancelled");
                }

                operations.Add(operationId, operation);
                RecordAcceptedTurn(conversation, request.ClientTurnId, now);
                if (syntheticDialogue)
                {
                    if (turnResult.Decision == "ask")
                    {
                        conversation.CompletedQuestions++;
                    }

                    conversation.TerminalReplyPublished = turnResult.Decision is "propose" or "no_selection";
                }

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

    private static TurnResult BuildAdminFakeTurn(
        ConversationEntry conversation,
        TextTurnRequest request,
        string conversationId,
        bool opening,
        DateTimeOffset now)
    {
        if (request.ReadyMask == 0)
        {
            return new TurnResult(
                "Dnes nemám připravenou žádnou hůlku. Zkusíme to, až budou přihrádky připravené.",
                "no_selection", "Žádná přihrádka není dostupná.", null);
        }

        string[] questions =
        [
            "Jak ti říkají?",
            "Co tě v poslední době opravdu bavilo?",
            "Kdy ses naposledy odvážil zkusit něco nového?",
            "Co na tobě tvoji blízcí nejvíc oceňují?"
        ];
        if (conversation.CompletedQuestions < PersonalityCatalog.FakeQuestions(conversation.PersonalityId))
        {
            var question = questions[conversation.CompletedQuestions];
            var reply = opening || string.IsNullOrWhiteSpace(request.Text)
                ? question
                : $"To si zapamatuju. {question}";
            return new TurnResult(reply, "ask", "", null);
        }

        var available = Enumerable.Range(1, 6)
            .Where(compartment => (request.ReadyMask & (1 << (compartment - 1))) != 0)
            .ToArray();
        var answerHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Text));
        var compartment = available[answerHash[0] % available.Length];
        const string reason = "Tahle hůlka ladí s odpověďmi našeho zkušebního rozhovoru.";
        var proposal = new SelectionProposal(
            Guid.NewGuid().ToString("D"), conversationId, request.ClientTurnId,
            request.CatalogVersion, request.DeviceRevision, compartment, reason,
            request.BootId, request.Generation, now.AddSeconds(60));
        return new TurnResult(
            $"Rozhodnuto. Navrhuji hůlku z přihrádky {compartment}.",
            "propose", reason, proposal);
    }

    private async Task ProcessPaidTurnAsync(
        string operationId,
        TurnCorrelation correlation,
        string visitorText,
        int readyMask,
        string catalogVersion,
        IReadOnlyList<DialogExchange> history,
        int completedQuestions,
        string selectedPersonalityId,
        EveningContext? memory,
        string costumeContext,
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
                completedQuestions < PersonalityCatalog.MinimumQuestions(selectedPersonalityId) && readyMask != 0,
                cancellation.Token,
                memory?.Transcript ?? "",
                selectedPersonalityId,
                costumeContext);
            var actualCost = ResponsesTextDialogBudget.CalculateActualMicroUsd(response, dialogRunner.Model);
            lock (gate)
            {
                if (!journal.SettleCost(operationId, actualCost))
                {
                    journal.MarkFailed(operationId);
                    return;
                }

                var now = clock.GetUtcNow();
                CleanupExpired(now);
                if (cancellation.IsCancellationRequested
                    || !conversations.TryGetValue(correlation.ConversationId, out var conversation)
                    || memory is not null && eveningMemory!.GetStatus(correlation.DeviceId).EveningId != memory.EveningId)
                {
                    journal.MarkFailed(operationId);
                    return;
                }

                // A provider proposal before the minimum completed questions cannot reach the device.
                if (response.Decision == "propose" && conversation.CompletedQuestions < PersonalityCatalog.MinimumQuestions(conversation.PersonalityId))
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
                eveningMemory?.Append(correlation.DeviceId, memory!.EveningId, operationId,
                    correlation.ConversationId, visitorText, response.Reply);

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

                // Preserve the final poll result until the device acknowledges closure with DELETE.
                conversation.TerminalReplyPublished = response.Decision is "propose" or "no_selection";
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

            if (conversation.TerminalReplyPublished)
            {
                return new SubmitTurnResult(409, null, "conversation_finished");
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
            if (voiceRunner?.Enabled == true)
            {
                return QueueVoiceCall(
                    deviceId, route, key, bodyHash, correlation,
                    VoiceOperationKind.Transcription, upload.Wav, null, null);
            }

            if (HasActiveCall(conversationId))
            {
                return new SubmitTurnResult(409, null, "operation_active");
            }

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

            if (voiceRunner?.Enabled == true)
            {
                return QueueVoiceCall(
                    deviceId, route, key, bodyHash, source.Correlation,
                    VoiceOperationKind.Speech, null, ((TurnResult)source.Result!).Reply,
                    request.OutputTarget, SpeechOptions.Resolve(request.Voice, request.Speed));
            }

            if (HasActiveCall(conversationId))
            {
                return new SubmitTurnResult(409, null, "operation_active");
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
                foreach (var conversation in conversations.Values.Where(entry => entry.ImageOperationId == operationId && !entry.ManualCostume))
                {
                    conversation.Costume = null;
                }
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

    public EveningMemoryStatus? GetEveningMemory(string deviceId) => eveningMemory?.GetStatus(deviceId);

    public EveningsList? ListEvenings(string deviceId) => eveningMemory?.List(deviceId);

    public EveningMutationResult CreateEvening(
        string deviceId,
        Guid key,
        ReadOnlySpan<byte> requestBody,
        string expectedEveningId,
        string name)
    {
        const string route = "/v1/evenings";
        var bodyHash = Convert.ToHexString(SHA256.HashData(requestBody));
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            var replay = eveningMemory!.FindMutation(deviceId, key, route, bodyHash);
            if (replay is not null)
            {
                return replay;
            }

            if (HasLiveEveningWork(deviceId))
            {
                return new(409, null, "evening_busy");
            }

            return eveningMemory.Create(deviceId, key, route, bodyHash, expectedEveningId, name);
        }
    }

    public EveningMutationResult ActivateEvening(
        string deviceId,
        Guid key,
        ReadOnlySpan<byte> requestBody,
        string targetEveningId,
        string expectedEveningId)
    {
        var route = $"/v1/evenings/{targetEveningId}/active";
        var bodyHash = Convert.ToHexString(SHA256.HashData(requestBody));
        lock (gate)
        {
            CleanupExpired(clock.GetUtcNow());
            var replay = eveningMemory!.FindMutation(deviceId, key, route, bodyHash);
            if (replay is not null)
            {
                return replay;
            }

            if (!eveningMemory.Contains(deviceId, targetEveningId))
            {
                return new(404, null, "evening_not_found");
            }

            if (HasLiveEveningWork(deviceId))
            {
                return new(409, null, "evening_busy");
            }

            return eveningMemory.Activate(
                deviceId, key, route, bodyHash, targetEveningId, expectedEveningId);
        }
    }

    private bool HasLiveEveningWork(string deviceId)
    {
        return conversations.Values.Any(conversation => conversation.DeviceId == deviceId)
            || activePaidCalls.Values.Any(call => call.DeviceId == deviceId);
    }

    public void ClearEveningMemory(string deviceId, string expectedEveningId)
    {
        lock (gate)
        {
            // A retry for an old evening must not cancel or erase the next one.
            if (eveningMemory?.GetStatus(deviceId).EveningId != expectedEveningId)
            {
                return;
            }

            foreach (var conversationId in conversations
                .Where(pair => pair.Value.DeviceId == deviceId)
                .Select(pair => pair.Key).ToArray())
            {
                journal.EndConversation(deviceId, conversationId, Guid.NewGuid());
                RemoveConversation(conversationId);
            }

            eveningMemory.Clear(deviceId, expectedEveningId);
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
        foreach (var conversation in conversations.Values)
        {
            if (conversation.Costume?.ExpiresAt <= now) { conversation.Costume = null; }
        }
        foreach (var id in operations.Where(pair => pair.Value.Result is CostumeContext context && context.ExpiresAt <= now).Select(pair => pair.Key).ToArray())
        {
            operations.Remove(id);
        }
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
        if (realtimeLifetimes.Remove(conversationId, out var realtime))
        {
            realtime.Cancel();
        }
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
        string selectedPersonalityId,
        DateTimeOffset expiresAt,
        DateTimeOffset absoluteExpiresAt)
    {
        public string DeviceId { get; } = deviceId;
        public Guid BootId { get; } = bootId;
        public int Generation { get; } = generation;
        public string CatalogVersion { get; } = catalogVersion;
        public bool AudioConsented { get; } = audioConsented;
        public bool ImageConsented { get; init; }
        public string? ImageOperationId { get; set; }
        public CostumeContext? Costume { get; set; }
        public bool ManualCostume { get; set; }
        public bool CostumeCleared { get; set; }
        public int CostumeCorrectionCount { get; set; }
        public string PersonalityId { get; } = selectedPersonalityId;
        public LivePromptSnapshot LivePrompt { get; init; } = LivePromptSnapshot.Create(selectedPersonalityId, new());
        public string LiveGeneration { get; } = Guid.NewGuid().ToString("D");
        public int? LiveReadyMask { get; set; }
        public int? LiveAvailabilityRevision { get; set; }
        public DateTimeOffset ExpiresAt { get; set; } = expiresAt;
        public DateTimeOffset AbsoluteExpiresAt { get; } = absoluteExpiresAt;
        public int TurnCount { get; set; }
        public int CompletedQuestions { get; set; }
        public bool TerminalReplyPublished { get; set; }
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
internal sealed record SpeechRequest(
    Guid TurnId,
    string OutputTarget,
    string? Voice = null,
    double? Speed = null);
internal sealed record SpeechResult(string AudioId, DateTimeOffset ExpiresAt, string OutputTarget);
internal sealed record AudioReadResult(int Status, byte[]? Wav);
internal sealed record AudioEntry(string DeviceId, string ConversationId, string OperationId, DateTimeOffset ExpiresAt, byte[] Wav);
internal sealed record AudioTombstone(string DeviceId, DateTimeOffset RemoveAt);
internal sealed record PaidCall(string DeviceId, string ConversationId, CancellationTokenSource Cancellation);
internal sealed record TurnOperation(
    string OperationId,
    string Kind,
    string Status,
    TurnCorrelation Correlation,
    object? Result,
    object? Error);

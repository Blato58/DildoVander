using System.Security.Cryptography;
using System.Text;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;

namespace Kufr.Backend.Features.Conversations;

internal sealed partial class ConversationStore
{
    internal int ActiveProviderCallCount
    {
        get
        {
            lock (gate)
            {
                return activePaidCalls.Count;
            }
        }
    }

    private bool HasActiveCall(string conversationId) => activePaidCalls.Values
        .Any(call => call.ConversationId == conversationId);

    // Caller holds gate after scope, consent, correlation and idempotence checks.
    private SubmitTurnResult QueueVoiceCall(
        string deviceId,
        string route,
        Guid key,
        string bodyHash,
        TurnCorrelation correlation,
        VoiceOperationKind kind,
        byte[]? inputWav,
        string? reply,
        string? outputTarget)
    {
        if (kind == VoiceOperationKind.Transcription
            && (inputWav is null || inputWav.Length > 1_920_512
                || TranscriptionUploadReader.ValidateWav(inputWav) != 200))
        {
            return new SubmitTurnResult(400, null, "invalid_request");
        }

        if (kind == VoiceOperationKind.Speech
            && (string.IsNullOrWhiteSpace(reply) || reply.EnumerateRunes().Count() > 500
                || outputTarget is not ("device" or "phone")))
        {
            return new SubmitTurnResult(400, null, "invalid_request");
        }

        if (activePaidCalls.Count != 0 || !voiceRunner!.TryAcquire())
        {
            return new SubmitTurnResult(503, null, "voice_busy");
        }

        var queued = false;
        byte[]? ownedInput = null;
        try
        {
            var reservation = journal.Reserve(
                deviceId,
                route,
                key,
                bodyHash,
                correlation,
                kind: kind == VoiceOperationKind.Speech ? "speech" : "transcription",
                consumesTurn: false,
                estimatedCostMicroUsd: kind == VoiceOperationKind.Speech
                    ? VoiceProviderRunner.SpeechReservationMicroUsd
                    : VoiceProviderRunner.TranscriptionReservationMicroUsd);
            if (reservation.Conflict)
            {
                return new SubmitTurnResult(409, null, "idempotency_conflict");
            }

            if (reservation.CostRejected)
            {
                return new SubmitTurnResult(429, null, "cost_limit");
            }

            if (reservation.Existing)
            {
                return new SubmitTurnResult(202, reservation.OperationId, null);
            }

            // The endpoint wipes its upload after returning 202. The worker owns
            // a separate bounded copy and wipes it after the provider is done.
            ownedInput = inputWav?.ToArray();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(voiceRunner.Stopping);
            activePaidCalls.Add(reservation.OperationId, new PaidCall(correlation.ConversationId, cancellation));
            var conversation = conversations[correlation.ConversationId];
            if (kind == VoiceOperationKind.Transcription)
            {
                conversation.TranscriptionIds.Add(correlation.ClientTurnId);
            }
            else
            {
                conversation.SpeechCount++;
            }

            var now = clock.GetUtcNow();
            conversation.ExpiresAt = now.AddMinutes(15) < conversation.AbsoluteExpiresAt
                ? now.AddMinutes(15)
                : conversation.AbsoluteExpiresAt;
            var workerInput = ownedInput;
            _ = Task.Run(() => ProcessVoiceCallAsync(
                reservation.OperationId, correlation, kind, workerInput, reply, outputTarget, cancellation));
            queued = true;
            return new SubmitTurnResult(202, reservation.OperationId, null);
        }
        finally
        {
            if (!queued)
            {
                if (ownedInput is not null)
                {
                    CryptographicOperations.ZeroMemory(ownedInput);
                }

                voiceRunner.Release();
            }
        }
    }

    private async Task ProcessVoiceCallAsync(
        string operationId,
        TurnCorrelation correlation,
        VoiceOperationKind kind,
        byte[]? inputWav,
        string? reply,
        string? outputTarget,
        CancellationTokenSource cancellation)
    {
        byte[]? generatedWav = null;
        try
        {
            lock (gate)
            {
                if (cancellation.IsCancellationRequested || !journal.MarkRunning(operationId))
                {
                    journal.SettleCost(operationId, 0);
                    journal.MarkFailed(operationId);
                    return;
                }
            }

            string? transcript = null;
            if (kind == VoiceOperationKind.Transcription)
            {
                transcript = await voiceRunner!.TranscribeAsync(inputWav!, cancellation.Token);
                if (string.IsNullOrWhiteSpace(transcript) || transcript.EnumerateRunes().Count() > 1000)
                {
                    throw new InvalidDataException("Invalid transcription result.");
                }
            }
            else
            {
                generatedWav = await voiceRunner!.SpeakAsync(reply!, cancellation.Token);
                if (generatedWav.Length > PcmSpeechAudio.MaxWavBytes
                    || TranscriptionUploadReader.ValidateWav(generatedWav) != 200)
                {
                    throw new InvalidDataException("Invalid speech result.");
                }
            }

            lock (gate)
            {
                var now = clock.GetUtcNow();
                CleanupExpired(now);
                if (cancellation.IsCancellationRequested
                    || !conversations.TryGetValue(correlation.ConversationId, out var conversation)
                    || !conversation.AudioConsented
                    || !journal.MarkCompleted(operationId))
                {
                    journal.MarkFailed(operationId);
                    return;
                }

                object result;
                if (kind == VoiceOperationKind.Speech)
                {
                    var audioId = Guid.NewGuid().ToString("D");
                    var expiresAt = now.AddSeconds(120);
                    audio.Add(audioId, new AudioEntry(
                        correlation.DeviceId, correlation.ConversationId, operationId, expiresAt, generatedWav!));
                    generatedWav = null; // Ownership moves to the existing short-lived audio store.
                    conversation.ActiveAudioId = audioId;
                    result = new SpeechResult(audioId, expiresAt, outputTarget!);
                }
                else
                {
                    result = new TranscriptionResult(transcript!, "deviceMicrophone");
                }

                operations.Add(operationId, new TurnOperation(
                    operationId,
                    kind == VoiceOperationKind.Speech ? "speech" : "transcription",
                    "completed", correlation, result, null));
                // No reliable billed usage is available in binary TTS. Both
                // audio estimates stay reserved until audit retention expires.
            }
        }
        catch (Exception)
        {
            logger?.LogWarning("Voice provider failed for operation {OperationId}.", operationId);
            try
            {
                lock (gate)
                {
                    journal.MarkFailed(operationId);
                }
            }
            catch (Exception)
            {
                logger?.LogError("Could not record voice failure for operation {OperationId}.", operationId);
            }
        }
        finally
        {
            if (inputWav is not null)
            {
                CryptographicOperations.ZeroMemory(inputWav);
            }

            if (generatedWav is not null)
            {
                CryptographicOperations.ZeroMemory(generatedWav);
            }

            lock (gate)
            {
                activePaidCalls.Remove(operationId);
            }

            cancellation.Dispose();
            voiceRunner!.Release();
        }
    }

    private enum VoiceOperationKind
    {
        Transcription,
        Speech
    }
}

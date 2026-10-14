using System.Buffers.Binary;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Speech;

namespace Kufr.Backend.Features.DigitalTwin;

// Only adapts the existing fake REST provider to the production live envelope.
// ConversationStore remains the dialogue/proposal owner; no slot choice lives here.
internal sealed class TwinFakeVoiceStream
{
    private readonly Lock gate = new();
    private TaskCompletionSource<byte[]>? input;
    private TaskCompletionSource<bool>? played;
    private string? inputId, operationId;
    private ulong sequence;
    internal Task<byte[]> Listen(string id)
    {
        lock (gate)
        {
            inputId = id; sequence = 0;
            input = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return input.Task;
        }
    }
    internal bool AcceptInput(Dictionary<string, string> headers, byte[] bytes)
    {
        lock (gate)
        {
            if (input is null || !headers.TryGetValue("Input-Id", out var id) || id != inputId
                || !headers.TryGetValue("Input-Sequence", out var value) || !TwinSnapshot.Decimal(value, out var ordinal)
                || ordinal != sequence || bytes.Length is < 2 or > 9600 || bytes.Length % 2 != 0) { return false; }
            sequence++;
            input.TrySetResult(bytes.ToArray());
            return true;
        }
    }
    internal Task<bool> Drain(string id)
    {
        lock (gate) { operationId = id; played = new(TaskCreationOptions.RunContinuationsAsynchronously); return played.Task; }
    }
    internal bool AcceptPlayed(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        lock (gate)
        {
            return played is not null && document.RootElement.GetProperty("operationId").GetString() == operationId
                && played.TrySetResult(true);
        }
    }
    internal static byte[] FixturePcm(byte[] wav)
    {
        if (wav.Length < 46 || !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)) != 32000)
        { throw new InvalidDataException("Invalid fake speech WAV."); }
        // Existing fake speech is a100ms silent32kHz fixture, not synthesized speech.
        // Linear resampling here is a platform media conversion, never scene logic.
        var sourceSamples = (wav.Length - 44) / 2;
        var count = sourceSamples * 3 / 4;
        var pcm = new byte[count * 2];
        for (var index = 0; index < count; index++)
        {
            var position = index * 4.0 / 3;
            var left = Math.Min((int)position, sourceSamples - 1);
            var right = Math.Min(left + 1, sourceSamples - 1);
            var a = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + left * 2));
            var b = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44 + right * 2));
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 2), (short)Math.Round(a + (b - a) * (position - left)));
        }
        return pcm;
    }
}

internal sealed partial class TwinMediaSessions
{
    private async Task FakeServiceAsync(Actor actor, TwinMediaServiceRequest request, byte[] bytes)
    {
        if (request.Kind == "realtime-start")
        {
            var opening = RealtimeProtocol.ReadRequest(bytes);
            if (opening is null || !opening.Opening || !RealtimeProtocol.IsValid(opening))
            { await ResultAsync(actor, request.RequestId, 400, "application/json", []); return; }
            actor.Fake = new TwinFakeVoiceStream();
            actor.StreamId = Guid.NewGuid().ToString("D");
            await ResultAsync(actor, request.RequestId, 200, "application/x-ndjson", []);
            _ = RunFakeAsync(actor, opening);
            return;
        }
        var accepted = actor.Fake is not null && (request.Kind == "realtime-input"
            ? actor.Fake.AcceptInput(request.Headers, bytes)
            : request.Kind == "realtime-played" && actor.Fake.AcceptPlayed(bytes));
        await ResultAsync(actor, request.RequestId, accepted ? 204 : 409, "application/json", []);
    }
    private async Task RunFakeAsync(Actor actor, RealtimeTurnRequest opening)
    {
        try
        {
            var cancellation = actor.Lifetime.Token;
            var live = actor.Fake!;
            await EmitAsync(new { type = "session", streamId = actor.StreamId, audioFormat = "pcm16-mono-24000" });
            var current = opening;
            for (var turn = 0; turn < 10; turn++)
            {
                var text = "";
                if (turn > 0)
                {
                    current = opening with { ClientTurnId = Guid.NewGuid(), Opening = false };
                    var capture = live.Listen(current.ClientTurnId.ToString("D"));
                    await EmitAsync(new { type = "listening", inputId = current.ClientTurnId });
                    var pcm = await capture.WaitAsync(TimeSpan.FromSeconds(30), cancellation);
                    await EmitAsync(new { type = "speech_stopped", inputId = current.ClientTurnId });
                    var wav = PcmSpeechAudio.ToDeviceWav(pcm);
                    Array.Clear(pcm);
                    var transcribed = conversations.SubmitTranscription(actor.InstanceId, actor.ConversationId!, Guid.NewGuid(),
                        new TranscriptionUpload(current.ClientTurnId, current.BootId, current.Generation, current.DeviceRevision, wav));
                    Array.Clear(wav);
                    if (transcribed.OperationId is null) { throw new InvalidDataException("Fake transcription rejected."); }
                    var transcription = conversations.GetOperation(actor.InstanceId, transcribed.OperationId).Operation?.Result as TranscriptionResult
                        ?? throw new InvalidDataException("Fake transcription missing.");
                    text = transcription.Transcript;
                }
                var request = new TextTurnRequest(current.ClientTurnId, current.BootId, current.Generation, text,
                    current.ReadyMask, current.DeviceRevision, current.CatalogVersion);
                var body = JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions);
                var submitted = conversations.SubmitTurn(actor.InstanceId, actor.ConversationId!, Guid.NewGuid(), body, request, opening: turn == 0);
                Array.Clear(body);
                var operation = submitted.OperationId is null ? null : conversations.GetOperation(actor.InstanceId, submitted.OperationId).Operation;
                if (operation is not { Status: "completed", Result: TurnResult result }) { throw new InvalidDataException("Fake dialogue rejected."); }
                await EmitAsync(new { type = "started", operationId = operation.OperationId, correlation = operation.Correlation });
                var speechRequest = new SpeechRequest(current.ClientTurnId, "device", current.Voice, current.Speed);
                var speechBody = JsonSerializer.SerializeToUtf8Bytes(speechRequest, RealtimeProtocol.JsonOptions);
                var speech = conversations.SubmitSpeech(actor.InstanceId, actor.ConversationId!, Guid.NewGuid(), speechBody, speechRequest);
                Array.Clear(speechBody);
                var speechResult = speech.OperationId is null ? null : conversations.GetOperation(actor.InstanceId, speech.OperationId).Operation?.Result as SpeechResult;
                if (speechResult is null) { throw new InvalidDataException("Fake speech rejected."); }
                var outputWav = conversations.GetAudio(actor.InstanceId, speechResult.AudioId).Wav
                    ?? throw new InvalidDataException("Fake audio missing.");
                var output = TwinFakeVoiceStream.FixturePcm(outputWav);
                Array.Clear(outputWav);
                await EmitAsync(new { type = "audio", delta = Convert.ToBase64String(output) });
                Array.Clear(output);
                var drained = live.Drain(operation.OperationId);
                await EmitAsync(new { type = "completed", operationId = operation.OperationId, correlation = operation.Correlation, result, costMicroUsd = 0 });
                await drained.WaitAsync(TimeSpan.FromSeconds(35), cancellation);
                conversations.CancelOperation(actor.InstanceId, speech.OperationId!, Guid.NewGuid());
                if (result.Decision != "ask")
                {
                    await EmitAsync(new { type = "closed", reason = "conversation_terminal" });
                    return;
                }
            }
            throw new InvalidDataException("Fake turn limit.");
        }
        catch (Exception error) when (error is IOException or JsonException or OperationCanceledException or TimeoutException or InvalidOperationException)
        {
            if (!actor.Cancelled)
            {
                try { await EmitAsync(new { type = "failed", code = "fake_media_failed" }); }
                catch (Exception nested) when (nested is IOException or OperationCanceledException or TimeoutException) { }
            }
        }
        Task EmitAsync(object value) => EventAsync(actor, JsonSerializer.SerializeToUtf8Bytes(value, RealtimeProtocol.JsonOptions));
    }
}

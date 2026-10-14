using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kufr.Backend.Features.DigitalTwin;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaSettingsChange(string ExpectedRevision, string Engine,
    string Voice, double Speed, bool PhotoConsent, string PresentationVariant);
internal sealed record TwinMediaAvailability(bool Fake, bool Realtime, bool GptLive,
    bool Mic, bool Speaker, bool Camera);
internal sealed record TwinMediaSettings(string Revision, string Engine, string Voice,
    double Speed, bool PhotoConsent, string PresentationVariant, TwinMediaAvailability Availability);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaStart(string OperationId, string ExpectedBootId,
    bool AudioConsent, bool PhotoConsent, bool MicAvailable = false, bool SpeakerAvailable = false, bool CameraAvailable = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaCancel(string? SessionId = null, string? BootId = null, string? Epoch = null,
    string? OperationId = null, string? ExpectedBootId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaPlayed(string SessionId, string BootId, string Epoch,
    string OutputEpoch, string Sequence, string PcmBytes, bool Drained);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaState(string? SessionId, string BootId, string Epoch,
    string InputEpoch, string OutputEpoch, string Phase, bool Active, string? Error = null,
    string? Text = null, string? NextInputSequence = null, string? PhotoWarning = null, bool InputEnabled = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaFrame(string SessionId, string BootId, string Epoch,
    string InputEpoch, string OutputEpoch, string Sequence, string Kind, int ByteLength,
    string Phase, bool DrainedRequested = false, string? PcmBytes = null,
    string? Text = null, string? Error = null, bool InputEnabled = false)
{
    internal bool Valid(int actualBytes) => ByteLength == actualBytes && actualBytes is >= 0 and <= 32768
        && SessionId.Length <= 96 && TwinSnapshot.Id(SessionId) && TwinSnapshot.Id(BootId)
        && TwinSnapshot.Decimal(Epoch, out _) && TwinSnapshot.Decimal(InputEpoch, out _)
        && TwinSnapshot.Decimal(OutputEpoch, out _) && TwinSnapshot.Decimal(Sequence, out _)
        && (PcmBytes is null || TwinSnapshot.Decimal(PcmBytes, out _))
        && Kind is "pcm" or "listening" or "thinking" or "speaking" or "drain" or "audio_cancel"
            or "photo_request" or "dialogue" or "completed" or "failed" or "closed" or "idle"
        && (Kind != "pcm" || actualBytes > 0 && actualBytes % 2 == 0)
        && Phase is { Length: > 0 and <= 48 } && (Text is null || Text.Length <= 4096)
        && (Error is null || Error.Length <= 96);
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinMediaServiceRequest(string RequestId, string Kind, string Method,
    string Path, string IdempotencyKey, Dictionary<string, string> Headers,
    string ContentType, int ByteLength);
internal sealed record TwinPrivateReply(TwinMediaState? State = null, TwinMediaFrame? Media = null,
    TwinMediaServiceRequest? Service = null, byte[]? Attachment = null);

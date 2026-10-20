using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kufr.Backend.Features.DigitalTwin;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinSource(string Mode, string InstanceId, string DeviceId, string BootId,
    string SessionId, string FirmwareVersion, string BuildId, string HardwareProfile,
    string SceneBundleHash, string? CadManifestRevision, string? CalibrationRevision);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinServo(int Slot, int? PulseUs, string Estimate);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinReported(string State, int Slot, int ReadyMask, string Generation,
    bool NeedsRecovery, string PhysicalVerification, TwinServo[] Servo, bool OutputsEnabled,
    int[]? LedGrb, int VibrationMask, string Audio);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinModelSlot(int Slot, double Position, bool Jammed);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinModel(TwinModelSlot[] Slots, string Evidence);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinTransport(string DroppedSnapshots);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record TwinSnapshot(int SchemaVersion, TwinSource Source, string Sequence,
    string TimeMs, string Revision, TwinReported Reported, TwinModel? Model, JsonElement Trace,
    TwinTransport Transport, string[] Capabilities)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };
    private static readonly HashSet<string> TraceFields = new((
        "schemaVersion source coreVersion bundleHash sourceHash compiledHash sceneId sceneVersion sceneHash variant " +
        "runtimeProfile calibrationId bootId operationId generation deviceRevision slot quality state evidence " +
        "firstSequence lastSequence droppedEvents track audioGeneration audioPositionMs beatIndex events " +
        "observerPublishedAtMs observerSkippedSnapshots observerStale").Split(' '));
    private static readonly HashSet<string> EventFields = new((
        "sequence kind phaseId phaseIndex clockDomain timeMs dueAt latenessMs evidence reason slot mask " +
        "red green blue level vibration toneHz").Split(' '));

    public static bool Decimal(string? value, out ulong result)
    {
        result = 0;
        return value is { Length: > 0 and <= 20 } && (value == "0" || value[0] != '0')
            && value.All(char.IsAsciiDigit)
            && ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    public bool Valid()
    {
        if (SchemaVersion != 1 || Source is null || Reported is null || Transport is null
            || Source.Mode is not ("simulation" or "live") || !Id(Source.InstanceId) || !Id(Source.DeviceId)
            || !Id(Source.BootId) || !Id(Source.SessionId) || !Text(Source.FirmwareVersion, 96)
            || !Text(Source.BuildId, 96) || Source.HardwareProfile != "B-effects"
            || !Hash(Source.SceneBundleHash) || Source.CadManifestRevision is not null && !Hash(Source.CadManifestRevision)
            || Source.CalibrationRevision is not null && !Decimal(Source.CalibrationRevision, out _)
            || !Decimal(Sequence, out _)
            || !Decimal(TimeMs, out _) || !Decimal(Revision, out _) || !Decimal(Reported.Generation, out _)
            || !Decimal(Transport.DroppedSnapshots, out _) || Reported.PhysicalVerification != "unavailable"
            || Reported.Slot is < 0 or > 6 || Reported.ReadyMask is < 0 or > 63
            || !Text(Reported.State, 48) || !Text(Reported.Audio, 48)
            || Reported.VibrationMask is < 0 or > 63
            || Reported.Servo is not { Length: 6 }
            || Reported.LedGrb is not null && (Reported.LedGrb.Length != 108 || Reported.LedGrb.Any(value => value is < 0 or > 255))
            || Capabilities is not { Length: > 0 and <= 16 } || Capabilities.Any(value => !Text(value, 48)))
        {
            return false;
        }
        if (Reported.Servo.Any(value => value is null)
            || Reported.Servo.Select(value => value.Slot).Distinct().Count() != 6
            || Reported.Servo.Any(value => value.Slot is < 1 or > 6
                || value.PulseUs is < 500 or > 2500
                || value.Estimate is not ("unknown" or "moving" or "raised" or "rest")))
        {
            return false;
        }
        if (Source.Mode == "live" && Model is not null)
        {
            return false;
        }
        if (Model is not null && (Model.Evidence != "simulated" || Model.Slots is not { Length: 6 }
            || Model.Slots.Any(value => value is null)
            || Model.Slots.Select(value => value.Slot).Distinct().Count() != 6
            || Model.Slots.Any(value => value.Slot is < 1 or > 6 || !double.IsFinite(value.Position)
                || value.Position is < 0 or > 1)))
        {
            return false;
        }
        return ValidTrace(Trace);
    }

    private static bool ValidTrace(JsonElement trace)
    {
        if (trace.ValueKind != JsonValueKind.Object || trace.EnumerateObject().Any(field => !TraceFields.Contains(field.Name))
            || !trace.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array
            || events.GetArrayLength() > 64)
        {
            return false;
        }
        foreach (var field in trace.EnumerateObject().Where(field => field.Name != "events"))
        {
            if (!Primitive(field.Value)) { return false; }
        }
        foreach (var item in events.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(field =>
                !EventFields.Contains(field.Name) || !Primitive(field.Value)))
            {
                return false;
            }
        }
        return true;
    }
    private static bool Primitive(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() is { Length: <= 128 } text
            && text.All(character => character is >= ' ' and <= '~'),
        JsonValueKind.Number => value.TryGetUInt32(out _),
        JsonValueKind.True or JsonValueKind.False => true,
        _ => false
    };
    internal static bool Id(string? value) => value is { Length: > 0 and <= 96 }
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
    private static bool Text(string? value, int length) => value is { Length: > 0 } && value.Length <= length
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');
    private static bool Hash(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

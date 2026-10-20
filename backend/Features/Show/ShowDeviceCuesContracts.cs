namespace Kufr.Backend.Features.Show;

internal sealed record DeviceCueAppliedSettings(uint Revision, bool Quiet, bool AmbientEnabled,
    DateTimeOffset ObservedAt, Guid BootId, int Generation, int DeviceRevision, uint StopRevision = 0);
internal sealed record ShowDeviceCuePoll(Guid BootId, int Generation, int DeviceRevision, long DeviceTimeMs,
    uint? AppliedSettingsRevision = null, bool? AppliedQuiet = null, bool? AppliedAmbient = null, string? Voice = null,
    uint? AppliedStopRevision = null);
internal sealed record ShowDeviceCueEnvelope(bool Quiet, bool AmbientEnabled, uint SettingsRevision,
    ShowDeviceCue? Cue, Guid? CancelledCueId = null, uint StopRevision = 0);
internal sealed record ShowDeviceCue(Guid CueId, Guid? DuelId, int Round, Guid BootId, int Generation,
    int DeviceRevision, long IssuedAtDeviceMs, long ValidUntilDeviceMs, string SceneId, int Compartment,
    string SpeechText, string? AudioPath, string? ClipId = null, string CueType = "NarratedBeat",
    string? Preset = null, string Voice = "marin");
internal sealed record ShowDeviceCueAck(Guid BootId, int Generation, int DeviceRevision,
    string Outcome, string Evidence, string? Reason = null);
internal sealed record ShowDeviceCueAckResult(int Status, string Code, string? Outcome = null);
internal sealed record ShowDeviceCueAudioResult(int Status, string Code, byte[]? Wav = null);

internal sealed partial class ShowCoordinator
{
    internal void ConfirmDeviceAmbientCue(string deviceId, string sourceCueId, bool speechFinished)
    {
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (runtime.State.SessionId is not null || runtime.State.OccupiedBy is not null
                || runtime.State.PendingCue is not { } cue || cue.CueId != sourceCueId
                || runtime.State.Quiet || runtime.State.Phase != "idle") { return; }
            if (speechFinished && cue.Kind == "ActorDirection")
            {
                runtime.PlayedLines.Enqueue(cue.Value);
                while (runtime.PlayedLines.Count > 12) { runtime.PlayedLines.Dequeue(); }
            }
            Update(runtime, runtime.State with { PendingCue = null });
        }
    }

    internal void ReportDeviceCueSettings(string deviceId, ShowDeviceCuePoll poll)
    {
        if (poll.AppliedSettingsRevision is not { } revision || poll.AppliedQuiet is not { } quiet
            || poll.AppliedAmbient is not { } ambient) { return; }
        lock (gate)
        {
            var runtime = Get(deviceId);
            if (revision > runtime.State.DeviceSettingsRevision) { return; }
            var previous = runtime.State.DeviceSettings;
            if (previous is not null && previous.Revision == revision && previous.Quiet == quiet
                && previous.AmbientEnabled == ambient && previous.BootId == poll.BootId
                && previous.Generation == poll.Generation && previous.DeviceRevision == poll.DeviceRevision
                && previous.StopRevision == (poll.AppliedStopRevision ?? previous.StopRevision)) { return; }
            Update(runtime, runtime.State with { DeviceSettings = new(revision, quiet, ambient, clock.GetUtcNow(),
                poll.BootId, poll.Generation, poll.DeviceRevision,
                poll.AppliedStopRevision is { } stop && stop <= runtime.State.DeviceStopRevision ? stop : 0) });
        }
    }
}

using System.Text.Json;

namespace Kufr.Backend.Features.DigitalTwin;

internal sealed partial class TwinHost
{
    // Subscribers may only enqueue bounded work; network I/O never runs here.
    internal event Action<string, TwinPrivateReply>? MediaReply;

    internal string? ClaimMedia(string id, TwinSession session)
    {
        if (session.Principal.Role != "control" || session.Principal.Mode != "simulation"
            || session.Principal.InstanceId != id || !hosts.TryGetValue(id, out var host)) { return "control_forbidden"; }
        lock (host.Gate)
        {
            if (host.Profile != "integration") { return "media_requires_wall_clock_profile"; }
            if (!host.Ready) { return "host_unavailable"; }
            if (host.Owner is not null && host.Owner != session.Id && host.OwnerExpires > clock.GetUtcNow())
            { return "control_owned"; }
            if (host.Owner != session.Id || session.Expires > host.OwnerExpires) { host.OwnerExpires = session.Expires; }
            host.Owner = session.Id;
            return null;
        }
    }

    internal Task<TwinCommandResult> MediaCommandAsync(string id, string line, byte[]? attachment = null)
    {
        if (!hosts.TryGetValue(id, out var host) || !host.Ready)
        { return Task.FromResult(new TwinCommandResult(false, "host_unavailable")); }
        var completion = new TaskCompletionSource<TwinCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!host.Commands.Writer.TryWrite(new Pending(line, completion, Attachment: attachment)))
        { return Task.FromResult(new TwinCommandResult(false, "command_queue_full")); }
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static TwinPrivateReply? ParsePrivate(JsonElement root, byte[] attachment)
    {
        var state = root.TryGetProperty("mediaState", out var stateJson)
            ? stateJson.Deserialize<TwinMediaState>(TwinSnapshot.JsonOptions) : null;
        var media = root.TryGetProperty("media", out var mediaJson)
            ? mediaJson.Deserialize<TwinMediaFrame>(TwinSnapshot.JsonOptions) : null;
        var service = root.TryGetProperty("mediaServiceRequest", out var serviceJson)
            ? serviceJson.Deserialize<TwinMediaServiceRequest>(TwinSnapshot.JsonOptions) : null;
        if (media is not null && !media.Valid(attachment.Length))
        { throw new InvalidDataException("Invalid native media descriptor."); }
        if (service is not null && service.ByteLength != attachment.Length)
        { throw new InvalidDataException("Invalid native service descriptor."); }
        if (state is not null && (!TwinSnapshot.Id(state.BootId) || !TwinSnapshot.Decimal(state.Epoch, out _)
            || !TwinSnapshot.Decimal(state.InputEpoch, out _) || !TwinSnapshot.Decimal(state.OutputEpoch, out _)
            || state.Phase.Length > 48 || state.Error?.Length > 96 || state.PhotoWarning?.Length > 96))
        { throw new InvalidDataException("Invalid native media state."); }
        return state is null && media is null && service is null ? null : new(state, media, service, attachment);
    }
}

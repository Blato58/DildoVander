namespace Kufr.Backend.Features.DigitalTwin;

internal sealed partial class TwinMediaSessions
{
    internal async Task<string?> InputAsync(string id, TwinSession owner, string session, string boot, string epoch,
        string inputEpoch, string sequence, byte[] bytes, bool photo)
    {
        var actor = Owned(id, owner);
        if (actor is null) { return "media_owner_required"; }
        if (!TwinSnapshot.Decimal(sequence, out var firstSequence) || !TwinSnapshot.Decimal(inputEpoch, out _))
        { return "media_epoch_or_sequence_mismatch"; }
        if (photo ? bytes.Length > 262144 : bytes.Length is < 2 or > 9600 || bytes.Length % 2 != 0)
        { return "media_input_size"; }
        var identityError = InputEpochError(actor, session, boot, epoch, inputEpoch, photo);
        if (identityError is not null) { return identityError; }
        if (!await actor.Inputs.WaitAsync(0)) { return "media_input_busy"; }
        try
        {
            for (var offset = 0; offset < bytes.Length || offset == 0; offset += 32768)
            {
                identityError = InputEpochError(actor, session, boot, epoch, inputEpoch, photo);
                if (identityError is not null) { return identityError; }
                var length = Math.Min(32768, bytes.Length - offset);
                if (firstSequence > ulong.MaxValue - (ulong)(offset / 32768)) { return "media_sequence_limit"; }
                var sequencePart = firstSequence + (ulong)(offset / 32768);
                var payload = bytes.AsSpan(offset, length).ToArray();
                var result = await hosts.MediaCommandAsync(id,
                    $"MEDIA_FRAME {session} {epoch} {inputEpoch} {sequencePart} {(photo ? "jpeg" : "pcm")} {length}", payload);
                Array.Clear(payload);
                if (!result.Accepted) { return result.Code; }
                if (bytes.Length == 0) { break; }
            }
            return null;
        }
        finally { actor.Inputs.Release(); Array.Clear(bytes); }
    }
    private static string? InputEpochError(Actor actor, string session, string boot, string epoch, string inputEpoch, bool photo)
    {
        lock (actor.Gate)
        {
            if (!Bound(actor, session, boot, epoch) || actor.State is not { } state)
            { return "media_epoch_or_sequence_mismatch"; }
            if (!photo && TwinSnapshot.Decimal(inputEpoch, out var captured)
                && TwinSnapshot.Decimal(state.InputEpoch, out var current)
                && (captured < current || captured == current && !state.InputEnabled))
            { return "media-input-retired"; }
            return inputEpoch == state.InputEpoch ? null : "media_epoch_or_sequence_mismatch";
        }
    }
    internal async Task<string?> PlayedAsync(string id, TwinSession owner, TwinMediaPlayed played)
    {
        var actor = Owned(id, owner);
        if (actor is null) { return "media_owner_required"; }
        if (!Bound(actor, played.SessionId, played.BootId, played.Epoch)
            || played.OutputEpoch != actor.State?.OutputEpoch || !TwinSnapshot.Decimal(played.OutputEpoch, out _)
            || !TwinSnapshot.Decimal(played.Sequence, out _) || !TwinSnapshot.Decimal(played.PcmBytes, out _))
        { return "media_epoch_or_playback_mismatch"; }
        var result = await hosts.MediaCommandAsync(id,
            $"MEDIA_PLAYED {played.SessionId} {played.Epoch} {played.OutputEpoch} {played.Sequence} {played.PcmBytes} {played.Drained.ToString().ToLowerInvariant()}");
        return result.Accepted ? null : result.Code;
    }
}

using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.PersonaLab;

// This gate tracks one response, not a whole conversation turn: an audio-bearing
// tool response must drain before its follow-up response can start too.
internal sealed class PersonaLabPlayback(
    Func<object, CancellationToken, Task> send,
    Func<string, int, CancellationToken, Task<bool>> interrupt,
    Action prepareNextListening,
    Action<bool> waiting) : IRealtimeResponsePlayback
{
    private readonly Lock gate = new();
    private Playback? current;

    public async Task StartedAsync(string playbackId, CancellationToken token)
    {
        lock (gate)
        {
            if (current is { Disposition: Disposition.Pending })
            {
                throw new InvalidDataException("Previous playback is unresolved.");
            }
            current = new(playbackId);
        }
        await send(new { type = "playback_started", playbackId }, token);
    }

    public async Task CompletedAsync(string playbackId, int totalSamples, bool final, CancellationToken token)
    {
        Playback response;
        lock (gate)
        {
            response = current is { } active && active.Id == playbackId ? active
                : throw new InvalidDataException("Playback identity missing.");
            if (totalSamples is <= 0 or > RealtimeProtocol.MaxAudioBytes / 2)
            {
                throw new InvalidDataException("Invalid playback size.");
            }
            response.TotalSamples = totalSamples;
            response.Final = final;
            response.Completed = true;
        }
        waiting(true);
        try
        {
            await send(new { type = "playback_end", playbackId, totalSamples, final }, token);
            // The voice session's existing deadline and cancellation own this
            // wait, including disconnect, account revocation and explicit end.
            await response.Drained.Task.WaitAsync(token);
        }
        finally { waiting(false); }
    }

    internal bool Acknowledge(string playbackId, int playedSamples)
    {
        lock (gate)
        {
            if (current is not { Disposition: Disposition.Pending } response || response.Id != playbackId) { return false; }
            if (!response.Completed || playedSamples != response.TotalSamples)
            {
                throw new LabException(400, "invalid_lab_playback");
            }
            response.Disposition = Disposition.Full;
            if (response.Final) { prepareNextListening(); }
            response.Drained.TrySetResult();
            return true;
        }
    }

    internal async Task<bool> CancelAsync(string playbackId, int playedAudioMs, CancellationToken token)
    {
        Playback response;
        Task<bool> cancellation;
        lock (gate)
        {
            if (current is not { Disposition: Disposition.Pending } active || active.Id != playbackId) { return false; }
            response = active;
            response.Disposition = Disposition.Cancel;
            // Claim before microphone admission/suppression changes. Starting
            // the callback under this lock attaches to this exact response.
            cancellation = interrupt(playbackId, playedAudioMs, token);
        }
        if (!await cancellation)
        {
            // Never turn a failed attachment into full playback or persistence.
            throw new LabException(502, "lab_playback_cancel_failed");
        }
        response.Drained.TrySetResult();
        return true;
    }

    internal bool IsCurrent(string playbackId)
    {
        lock (gate) { return current is { Disposition: Disposition.Pending } response && response.Id == playbackId; }
    }

    private enum Disposition { Pending, Full, Cancel }
    private sealed class Playback(string id)
    {
        internal string Id { get; } = id;
        internal int TotalSamples { get; set; }
        internal bool Final { get; set; }
        internal bool Completed { get; set; }
        internal Disposition Disposition { get; set; }
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

using System.Collections.Concurrent;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;

namespace Kufr.Backend.Features.Realtime;

// Each session has one event reader/provider connection. Uploads are serialized
// separately so small HTTPS microphone frames cannot block output playback.
internal sealed class RealtimeLiveRegistry
{
    private readonly ConcurrentDictionary<Guid, RealtimeLiveSession> sessions = new();

    internal bool Add(RealtimeLiveSession session) => sessions.TryAdd(session.Id, session);
    internal void Remove(Guid id) => sessions.TryRemove(id, out _);
    internal RealtimeLiveSession? Find(Guid id, string device, string conversation) =>
        sessions.TryGetValue(id, out var session)
        && session.DeviceId == device && session.ConversationId == conversation
            ? session
            : null;
}

internal sealed class RealtimeLiveSession : IDisposable
{
    private readonly Lock gate = new();
    private readonly SemaphoreSlim uploads = new(1);
    private readonly IStreamingAudioInput provider;
    private readonly bool continuous;
    private readonly CancellationTokenSource lifetime;
    private readonly CancellationToken lifetimeToken;
    private RealtimeCall? input;
    private Guid? endedInput;
    private bool accepting;
    private bool audioStreamAttached;
    private int sequence;
    private int bytes;
    private string? playbackOperation;
    private TaskCompletionSource? playback;
    private long playbackGeneration;
    private long outputGeneration;
    private long appliedVisualGeneration;
    private TaskCompletionSource visualBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<(string Operation, long Generation)> retiredPlayback = [];
    private LivePlaybackRequest? checkpoint;
    private TaskCompletionSource<LivePlaybackConfirmation>? checkpointDone;
    private readonly Dictionary<Guid, int> acknowledgedCheckpoints = [];

    internal RealtimeLiveSession(string device, string conversation,
        IStreamingAudioInput provider, CancellationTokenSource lifetime, bool continuous = false)
    {
        DeviceId = device;
        ConversationId = conversation;
        this.provider = provider;
        this.continuous = continuous;
        this.lifetime = lifetime;
        lifetimeToken = lifetime.Token;
    }

    internal Guid Id { get; } = Guid.NewGuid();
    internal string DeviceId { get; }
    internal string ConversationId { get; }

    internal bool AttachAudioStream()
    {
        lock (gate)
        {
            if (audioStreamAttached || lifetime.IsCancellationRequested) { return false; }
            audioStreamAttached = true;
            return true;
        }
    }

    // A broken stream has an unknown last accepted frame. Close the entire
    // provider session rather than reconnect and replay potentially spoken input.
    internal void EndAudioStream()
    {
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { } // The owning output request already ended.
    }
    internal CancellationToken Lifetime => lifetimeToken;

    internal void Listen(RealtimeCall call)
    {
        lock (gate)
        {
            if (accepting && input?.Context.Request.ClientTurnId == call.Context.Request.ClientTurnId) { return; }
            input = call;
            accepting = true;
            sequence = 0;
            bytes = 0;
        }
    }

    internal void StopListening()
    {
        lock (gate)
        {
            if (input is not null)
            {
                endedInput = input.Context.Request.ClientTurnId;
            }
            accepting = false;
            input = null;
        }
    }

    internal async Task StopListeningAsync(CancellationToken token)
    {
        StopListening();
        await uploads.WaitAsync(token);
        uploads.Release();
    }

    internal async Task<bool> UploadAsync(Guid inputId, int inputSequence, byte[] pcm,
        CancellationToken disconnected, bool streaming = false)
    {
        if (pcm.Length == 0 || pcm.Length > 9600 || pcm.Length % 2 != 0)
        {
            return false;
        }
        lock (gate)
        {
            if (audioStreamAttached && !streaming) { return false; }
            if (inputId == endedInput)
            {
                return true;
            }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(disconnected, lifetimeToken);
        if (!await uploads.WaitAsync(0, cancellation.Token))
        {
            lock (gate)
            {
                return inputId == endedInput;
            }
        }
        try
        {
            CancellationToken token;
            lock (gate)
            {
                // The last frame can arrive after VAD stops speech. Discard it
                // rather than feed it into the next visitor's input buffer.
                if (inputId == endedInput)
                {
                    return true;
                }
                if (!accepting || input is null || input.Context.Request.ClientTurnId != inputId
                    || inputSequence != sequence || pcm.Length == 0 || pcm.Length > 9600 || pcm.Length % 2 != 0)
                {
                    return false;
                }
                bytes = checked(bytes + pcm.Length);
                if (bytes > (continuous ? 24_000 * 2 * 180 : RealtimeProtocol.MaxAudioBytes))
                {
                    lifetime.Cancel();
                    return false;
                }
                sequence++;
                token = input.Cancellation.Token;
            }
            try
            {
                await provider.SendAudioAsync(pcm, token);
                return true;
            }
            catch
            {
                // Upload outcome is ambiguous: never replay audio.
                lifetime.Cancel();
                throw;
            }
        }
        finally
        {
            uploads.Release();
        }
    }

    internal long OutputGeneration { get { lock (gate) { return outputGeneration; } } }
    internal Guid? ListeningInput { get { lock (gate) { return accepting ? input?.Context.Request.ClientTurnId : null; } } }

    internal long CancelOutput()
    {
        lock (gate)
        {
            outputGeneration++;
            if (playbackOperation is not null && playback is not null)
            {
                retiredPlayback.Add((playbackOperation, playbackGeneration));
                playback.TrySetResult();
            }
            CancelCheckpoint();
            return outputGeneration;
        }
    }
    internal void ApplyVisualBarrier(long generation)
    {
        lock (gate)
        {
            appliedVisualGeneration = generation;
            var previous = visualBarrier;
            visualBarrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }
    internal async Task WaitForVisualBarrierAsync(long generation, CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (gate)
            {
                if (appliedVisualGeneration >= generation) { return; }
                changed = visualBarrier.Task;
            }
            await changed.WaitAsync(token);
        }
    }

    internal Task PreparePlayback(string operation)
    {
        lock (gate)
        {
            playbackOperation = operation;
            playbackGeneration = outputGeneration;
            playback = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return playback.Task;
        }
    }

    internal bool Played(string operation, long? generation = null)
    {
        lock (gate)
        {
            if (generation is { } retired && retiredPlayback.Contains((operation, retired))) { return true; }
            if (generation is null && outputGeneration != 0 || generation is { } value && value != playbackGeneration) { return false; }
            return playbackOperation == operation && playback is not null
                && (playback.TrySetResult() || playback.Task.IsCompletedSuccessfully);
        }
    }

    internal Task<LivePlaybackConfirmation> PrepareCheckpoint(LivePlaybackRequest request)
    {
        lock (gate)
        {
            if (checkpoint is not null) checkpointDone?.TrySetResult(new(checkpoint.PlaybackId, checkpoint.PcmBytes, false));
            checkpoint = request;
            checkpointDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return checkpointDone.Task;
        }
    }
    internal bool ConfirmCheckpoint(Guid id, int pcmBytes, bool drained)
    {
        lock (gate)
        {
            if (acknowledgedCheckpoints.TryGetValue(id, out var acknowledgedBytes)) return acknowledgedBytes == pcmBytes && drained;
            if (checkpoint is null || checkpoint.PlaybackId != id || checkpoint.PcmBytes != pcmBytes || !drained || checkpointDone is null) return false;
            acknowledgedCheckpoints.Add(id, pcmBytes);
            return checkpointDone.TrySetResult(new(id, pcmBytes, true)) || checkpointDone.Task.IsCompletedSuccessfully;
        }
    }
    internal void CancelCheckpoint()
    {
        lock (gate)
        {
            if (checkpoint is not null) { checkpointDone?.TrySetResult(new(checkpoint.PlaybackId, checkpoint.PcmBytes, false)); }
            checkpoint = null; checkpointDone = null;
        }
    }

    public void Dispose()
    {
        StopListening();
        lifetime.Cancel();
        // Upload requests own their awaited semaphore lifetime; do not dispose
        // it while a disconnected request is releasing its final frame.
    }
}

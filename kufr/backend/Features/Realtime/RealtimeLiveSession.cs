using System.Collections.Concurrent;
using Kufr.Backend.Features.Conversations;

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
    private RealtimeCall? input;
    private Guid? endedInput;
    private bool accepting;
    private int sequence;
    private int bytes;
    private string? playbackOperation;
    private TaskCompletionSource? playback;

    internal RealtimeLiveSession(string device, string conversation,
        IStreamingAudioInput provider, CancellationTokenSource lifetime, bool continuous = false)
    {
        DeviceId = device;
        ConversationId = conversation;
        this.provider = provider;
        this.continuous = continuous;
        this.lifetime = lifetime;
    }

    internal Guid Id { get; } = Guid.NewGuid();
    internal string DeviceId { get; }
    internal string ConversationId { get; }

    internal void Listen(RealtimeCall call)
    {
        lock (gate)
        {
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
        CancellationToken disconnected)
    {
        if (pcm.Length == 0 || pcm.Length > 9600 || pcm.Length % 2 != 0)
        {
            return false;
        }
        lock (gate)
        {
            if (inputId == endedInput)
            {
                return true;
            }
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(disconnected, lifetime.Token);
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

    internal Task PreparePlayback(string operation)
    {
        lock (gate)
        {
            playbackOperation = operation;
            playback = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return playback.Task;
        }
    }

    internal bool Played(string operation)
    {
        lock (gate)
        {
            return playbackOperation == operation && playback is not null
                && (playback.TrySetResult() || playback.Task.IsCompletedSuccessfully);
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

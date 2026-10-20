using System.Security.Cryptography;

namespace Kufr.Backend.Features.PersonaLab;

// Owns only microphone admission. The existing Realtime connection still owns
// VAD, response cancellation, correlated draining and conversation history.
internal sealed class PersonaLabRealtimeInput(Func<byte[], CancellationToken, Task> send) : IDisposable
{
    internal const int MaximumPrefixBytes = 96_000;
    private readonly Lock stateGate = new();
    private readonly SemaphoreSlim uploads = new(1);
    private readonly Queue<byte[]> prefix = new();
    private InputPhase phase;
    private long generation;
    private int prefixBytes;

    internal async Task SendAsync(byte[] pcm, CancellationToken token)
    {
        long admittedGeneration;
        lock (stateGate)
        {
            if (phase == InputPhase.Interrupting)
            {
                if (pcm.Length > MaximumPrefixBytes - prefixBytes)
                {
                    ClearPrefix();
                    throw new LabException(413, "lab_realtime_input_buffer_limit");
                }
                prefix.Enqueue(pcm.ToArray());
                prefixBytes += pcm.Length;
                return;
            }
            if (phase != InputPhase.Listening) { return; }
            admittedGeneration = generation;
        }
        await uploads.WaitAsync(token);
        try
        {
            lock (stateGate)
            {
                if (phase != InputPhase.Listening || generation != admittedGeneration) { return; }
            }
            await send(pcm, token);
        }
        finally { uploads.Release(); }
    }

    internal async Task StopAsync(CancellationToken token)
    {
        lock (stateGate)
        {
            if (phase == InputPhase.Listening) { phase = InputPhase.Stopped; }
        }
        // VAD's caller awaits this before clearing the provider input buffer.
        await uploads.WaitAsync(token);
        uploads.Release();
    }

    internal async Task ListenAsync(CancellationToken token)
    {
        await uploads.WaitAsync(token);
        byte[][] pending = [];
        long listeningGeneration = 0;
        try
        {
            lock (stateGate)
            {
                if (phase == InputPhase.Disposed) { throw new OperationCanceledException(token); }
                generation++;
                listeningGeneration = generation;
                phase = InputPhase.Listening;
                pending = prefix.ToArray();
                prefix.Clear();
                prefixBytes = 0;
            }
            // Holding uploads preserves the prefix ahead of newly arriving PCM.
            foreach (var pcm in pending)
            {
                lock (stateGate)
                {
                    if (phase != InputPhase.Listening || generation != listeningGeneration) { break; }
                }
                await send(pcm, token);
            }
        }
        finally
        {
            foreach (var pcm in pending) { CryptographicOperations.ZeroMemory(pcm); }
            uploads.Release();
        }
    }

    internal async Task<bool> InterruptAsync(Func<Task<bool>> interrupt)
    {
        Task<bool> request;
        long interruptedGeneration;
        lock (stateGate)
        {
            // A late cancel belongs to the preceding response. It must neither
            // cancel a new input nor leave an idle listening turn buffered.
            if (phase is InputPhase.Listening or InputPhase.Disposed) { return false; }
            if (phase == InputPhase.Interrupting) { return true; }
            interruptedGeneration = generation;
            phase = InputPhase.Interrupting;
            try
            {
                // Start the provider request before a concurrent Listen can
                // advance generation and attach the cancel to a newer response.
                request = interrupt();
            }
            catch
            {
                phase = InputPhase.Stopped;
                ClearPrefix();
                throw;
            }
        }
        var accepted = await request;
        if (!accepted)
        {
            lock (stateGate)
            {
                if (phase == InputPhase.Interrupting && generation == interruptedGeneration)
                {
                    phase = InputPhase.Stopped;
                    ClearPrefix();
                }
            }
        }
        return accepted;
    }

    public void Dispose()
    {
        lock (stateGate)
        {
            phase = InputPhase.Disposed;
            ClearPrefix();
        }
        // The voice lifetime cancels and joins uploads before disposal. Retain
        // the managed semaphore so a concurrently unwinding send can release it.
    }

    private void ClearPrefix()
    {
        while (prefix.TryDequeue(out var pcm)) { CryptographicOperations.ZeroMemory(pcm); }
        prefixBytes = 0;
    }

    private enum InputPhase { Stopped, Listening, Interrupting, Disposed }
}

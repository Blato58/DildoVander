using System.Security.Cryptography;
using System.Threading.Channels;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.DeviceMedia;

// One provider-upload worker and one credited queued frame. Credit is granted
// before ACK visibility, independently of the sender continuation returning.
internal sealed class DeviceMediaInputOwner
{
    private sealed record Frame(RealtimeLiveSession Session, Guid Input, int Sequence, uint Stream, byte[] Pcm);
    private readonly Lock gate = new();
    private readonly Channel<Frame> queued = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private bool credit = true;
    private bool closed;

    internal DeviceMediaInputOwner(DeviceMediaSocket socket, CancellationToken lifetime)
    {
        Completion = RunAsync(socket, lifetime);
    }

    internal Task Completion { get; }
    internal bool TrySubmit(RealtimeLiveSession session, Guid input, int sequence, uint stream, byte[] pcm)
    {
        lock (gate)
        {
            if (closed || !credit) { return false; }
            credit = false;
            if (queued.Writer.TryWrite(new(session, input, sequence, stream, pcm))) { return true; }
            closed = true;
            return false;
        }
    }

    internal void Complete()
    {
        lock (gate) { closed = true; queued.Writer.TryComplete(); }
    }

    private async Task RunAsync(DeviceMediaSocket socket, CancellationToken lifetime)
    {
        try
        {
            await foreach (var frame in queued.Reader.ReadAllAsync(lifetime))
            {
                try
                {
                    lifetime.ThrowIfCancellationRequested();
                    if (!await frame.Session.UploadAsync(frame.Input, frame.Sequence, frame.Pcm, lifetime, streaming: true))
                    {
                        throw new InvalidDataException("media_input_mismatch");
                    }
                }
                finally { CryptographicOperations.ZeroMemory(frame.Pcm); }
                lifetime.ThrowIfCancellationRequested();
                lock (gate) { if (!closed) { credit = true; } }
                await socket.SendJsonAsync(new { type = "media.ack", stream = frame.Stream, sequence = frame.Sequence }, lifetime);
            }
        }
        catch { socket.Abort(); throw; }
        finally
        {
            Complete();
            while (queued.Reader.TryRead(out var frame)) { CryptographicOperations.ZeroMemory(frame.Pcm); }
        }
    }
}

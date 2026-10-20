using System.Threading.Channels;

namespace Kufr.Backend.Features.DeviceMedia;

// One sender task, one in-flight receipt and at most one queued receipt. A
// refused urgent cancel must not create another task behind a blocked sender.
// Its ledger receipt remains recoverable by an exact retry after ACK credit.
internal sealed class DeviceMediaControlReplies
{
    private sealed record Reply(Guid Request, int Status, string? Code);
    private readonly Channel<Reply> queued = Channel.CreateBounded<Reply>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private int pending;

    internal DeviceMediaControlReplies(DeviceMediaSocket socket, CancellationToken lifetime)
    {
        Completion = RunAsync(socket, lifetime);
    }

    internal Task Completion { get; }
    internal bool HasPending => Volatile.Read(ref pending) != 0;

    // Only the receive owner calls this method. The sender can only free slots.
    internal bool TryEnqueue(Guid request, int status, string? code)
    {
        if (Volatile.Read(ref pending) >= 2) { return false; }
        Interlocked.Increment(ref pending);
        if (queued.Writer.TryWrite(new(request, status, code))) { return true; }
        Interlocked.Decrement(ref pending);
        return false;
    }

    internal void Complete() => queued.Writer.TryComplete();

    private async Task RunAsync(DeviceMediaSocket socket, CancellationToken lifetime)
    {
        try
        {
            await foreach (var reply in queued.Reader.ReadAllAsync(lifetime))
            {
                try
                {
                    if (reply.Code is null)
                    {
                        await socket.SendJsonAsync(new { type = "control.ack", requestId = reply.Request,
                            status = reply.Status }, lifetime);
                    }
                    else
                    {
                        await socket.SendJsonAsync(new { type = "control.ack", requestId = reply.Request,
                            status = reply.Status, code = reply.Code }, lifetime);
                    }
                }
                finally { Interlocked.Decrement(ref pending); }
            }
        }
        catch
        {
            socket.Abort();
            throw;
        }
        finally
        {
            while (queued.Reader.TryRead(out _)) { Interlocked.Decrement(ref pending); }
        }
    }
}

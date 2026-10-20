namespace Kufr.Backend.Features.DeviceMedia;

// One sender, one in-flight receipt and separate single normal/urgent slots.
// Urgent refusals cannot consume a legal normal control's reserved ACK slot.
internal sealed class DeviceMediaControlReplies
{
    private sealed record Reply(Guid Request, int Status, string? Code, bool ReturnsCredit, Action<bool>? Published);
    private readonly Lock gate = new();
    private readonly SemaphoreSlim ready = new(0, 1);
    private Reply? normal;
    private Reply? urgent;
    private bool inFlight;
    private bool credit = true;
    private bool closed;

    internal DeviceMediaControlReplies(DeviceMediaSocket socket, CancellationToken lifetime)
    {
        Completion = RunAsync(socket, lifetime);
    }

    internal Task Completion { get; }
    internal bool HasPending { get { lock (gate) { return inFlight || normal is not null || urgent is not null; } } }

    internal bool TryTakeCredit()
    {
        lock (gate)
        {
            if (closed || !credit) { return false; }
            credit = false;
            return true;
        }
    }

    // Only the receive owner enqueues. A normal control already consumed its
    // protocol credit; urgent refusals cannot fill that normal reserved slot.
    internal bool TryEnqueue(Guid request, int status, string? code, bool returnsCredit = false,
        Action<bool>? published = null)
    {
        lock (gate)
        {
            if (closed || (returnsCredit ? normal is not null : urgent is not null)) { return false; }
            var reply = new Reply(request, status, code, returnsCredit, published);
            if (returnsCredit) { normal = reply; }
            else { urgent = reply; }
            SignalLocked();
            return true;
        }
    }

    internal void Complete()
    {
        lock (gate) { closed = true; SignalLocked(); }
    }
    private void SignalLocked()
    {
        if (ready.CurrentCount == 0) { ready.Release(); }
    }

    private async Task RunAsync(DeviceMediaSocket socket, CancellationToken lifetime)
    {
        try
        {
            while (true)
            {
                await ready.WaitAsync(lifetime);
                while (true)
                {
                    Reply? reply;
                    lock (gate)
                    {
                        reply = normal ?? urgent;
                        if (reply is null) { if (closed) { return; } break; }
                        if (normal == reply) { normal = null; }
                        else { urgent = null; }
                        inFlight = true;
                        // ACK bytes may reach a peer before SendAsync returns.
                        // Resource ownership and protocol credit are distinct.
                        if (reply.ReturnsCredit) { credit = true; }
                    }
                    var delivered = false;
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
                        delivered = true;
                    }
                    finally
                    {
                        lock (gate) { inFlight = false; }
                        reply.Published?.Invoke(delivered);
                    }
                }
            }
        }
        catch { socket.Abort(); throw; }
        finally
        {
            Reply? pendingNormal;
            Reply? pendingUrgent;
            lock (gate)
            {
                closed = true;
                pendingNormal = normal; pendingUrgent = urgent;
                normal = urgent = null;
            }
            pendingNormal?.Published?.Invoke(false);
            pendingUrgent?.Published?.Invoke(false);
        }
    }
}

namespace Kufr.Backend.Features.PersonaLab;

// Admission shares the existing Lab runtime capacity. ConversationStore remains
// the owner of the device conversation and its physical operation correlation.
internal sealed class LabHardwareLease : IDisposable
{
    private readonly CancellationTokenSource lifetime;
    private readonly Action release;
    private int disposed;
    internal LabSession Session { get; }
    internal CancellationToken Token { get; }
    internal DateTimeOffset ExpiresAt { get; }

    internal LabHardwareLease(LabSession session, CancellationTokenSource lifetime, Action release,
        DateTimeOffset expiresAt)
    {
        Session = session;
        this.lifetime = lifetime;
        this.release = release;
        Token = lifetime.Token;
        ExpiresAt = expiresAt;
    }

    internal void Cancel()
    {
        try { lifetime.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        try { Cancel(); }
        finally
        {
            release();
            lifetime.Dispose();
        }
    }
}

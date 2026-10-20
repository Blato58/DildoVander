using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.DeviceMedia;

// Exactly one receive owner and one serialized sender. There is no output
// queue: provider writes retain their existing generation gate through send.
internal sealed class DeviceMediaSocket(WebSocket socket, CancellationTokenSource lifetime)
{
    private readonly SemaphoreSlim sends = new(1);

    internal async Task SendJsonAsync(object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RealtimeProtocol.JsonOptions);
        try { await SendAsync(bytes, WebSocketMessageType.Text, token); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task SendAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType kind, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await sends.WaitAsync(deadline.Token);
            try { await socket.SendAsync(bytes, kind, true, deadline.Token); }
            finally { sends.Release(); }
        }
        catch
        {
            Abort();
            throw;
        }
    }

    internal async Task<(WebSocketMessageType Kind, int Count)> ReceiveAsync(byte[] buffer, CancellationToken token)
    {
        var used = 0;
        WebSocketMessageType? kind = null;
        while (true)
        {
            if (used == buffer.Length) { throw new InvalidDataException("media_message_too_large"); }
            var received = await socket.ReceiveAsync(buffer.AsMemory(used), token);
            if (received.MessageType == WebSocketMessageType.Close) { return (WebSocketMessageType.Close, 0); }
            kind ??= received.MessageType;
            if (kind != received.MessageType) { throw new InvalidDataException("invalid_media_frame"); }
            used += received.Count;
            if (used > DeviceMediaProtocol.MaxMessageBytes) { throw new InvalidDataException("media_message_too_large"); }
            if (received.EndOfMessage) { return (kind.Value, used); }
        }
    }

    internal async Task CompleteAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        await sends.WaitAsync(deadline.Token);
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "session_complete", deadline.Token);
        }
        finally { sends.Release(); }
    }

    // The endpoint calls this only after cancelling/aborting the transport.
    // Abort is a cancellation request, not the end of a borrowed Memory lease.
    internal static async Task ReleaseReceiveAsync(Task? receive, byte[] buffer)
    {
        try
        {
            if (receive is not null) { await receive; }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or InvalidDataException) { }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    internal void Abort()
    {
        lifetime.Cancel();
        socket.Abort();
    }
}

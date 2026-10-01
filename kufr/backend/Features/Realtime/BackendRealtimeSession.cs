using System.Net.WebSockets;
using OpenAI.Realtime;

namespace Kufr.Backend.Features.Realtime;

// Realtime is still marked experimental by the pinned official SDK.
#pragma warning disable OPENAI002
internal sealed class BackendRealtimeSession : RealtimeSessionClient
{
    internal BackendRealtimeSession(WebSocket socket)
        : base(null!, null!, null!, null!, null!)
    {
        // Keep our no-redirect connection and byte cap while the SDK owns
        // command serialization, event decoding and send synchronization.
        WebSocket = new BoundedRealtimeSocket(socket);
    }
}
#pragma warning restore OPENAI002

internal sealed class BoundedRealtimeSocket(WebSocket socket) : WebSocket
{
    private const int MaxEventBytes = 262_144;
    private int messageBytes;

    public override WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
    public override string? CloseStatusDescription => socket.CloseStatusDescription;
    public override WebSocketState State => socket.State;
    public override string? SubProtocol => socket.SubProtocol;

    public override void Abort() => socket.Abort();
    public override void Dispose() => socket.Dispose();

    public override Task CloseAsync(
        WebSocketCloseStatus status,
        string? description,
        CancellationToken token) => socket.CloseAsync(status, description, token);

    public override Task CloseOutputAsync(
        WebSocketCloseStatus status,
        string? description,
        CancellationToken token) => socket.CloseOutputAsync(status, description, token);

    public override Task SendAsync(
        ArraySegment<byte> buffer,
        WebSocketMessageType type,
        bool complete,
        CancellationToken token) => socket.SendAsync(buffer, type, complete, token);

    public override async Task<WebSocketReceiveResult> ReceiveAsync(
        ArraySegment<byte> buffer,
        CancellationToken token)
    {
        var received = await socket.ReceiveAsync(buffer, token);
        messageBytes = checked(messageBytes + received.Count);
        if (received.MessageType != WebSocketMessageType.Text || messageBytes > MaxEventBytes)
        {
            throw new InvalidDataException("Invalid Realtime frame.");
        }
        if (received.EndOfMessage)
        {
            messageBytes = 0;
        }
        return received;
    }
}

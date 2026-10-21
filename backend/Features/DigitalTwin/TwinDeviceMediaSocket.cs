using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Kufr.Backend.Features.DeviceMedia;

namespace Kufr.Backend.Features.DigitalTwin;

// One actor's bounded I/O bridge. Protocol decisions remain in DeviceMediaSession
// and the real device-media endpoint. No reconnect, replay, or provider calls.
internal sealed class TwinDeviceMediaSocket : IDisposable
{
    private readonly WebSocket socket;
    private readonly HttpMessageInvoker? http;
    private readonly CancellationTokenRegistration cancellation;
    private readonly CancellationTokenSource lifetime;
    private readonly SemaphoreSlim operations = new(1, 1);
    private Task<(byte[] Bytes, bool Binary)>? incoming;
    private int disposed;
    internal TwinDeviceMediaSocket(WebSocket socket, CancellationToken actorLifetime, HttpMessageInvoker? http = null)
    {
        this.socket = socket; this.http = http;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(actorLifetime);
        lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        cancellation = lifetime.Token.Register(Dispose);
    }
    internal static async Task<TwinDeviceMediaSocket> ConnectAsync(Uri uri, string authorization,
        string key, CancellationToken cancellation)
    {
        var socket = new ClientWebSocket();
        var http = new HttpMessageInvoker(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
        socket.Options.AddSubProtocol(DeviceMediaProtocol.Subprotocol);
        socket.Options.SetRequestHeader("Authorization", authorization);
        socket.Options.SetRequestHeader("Idempotency-Key", key);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await socket.ConnectAsync(uri, http, deadline.Token);
            return new TwinDeviceMediaSocket(socket, cancellation, http);
        }
        catch { socket.Dispose(); http.Dispose(); throw; }
    }
    internal async Task SendAsync(byte[] bytes, bool binary, CancellationToken cancellation)
    {
        if (bytes.Length is < 1 or > 8184) { throw new InvalidDataException("media_frame_limit"); }
        await operations.WaitAsync(cancellation);
        try
        {
            if (Volatile.Read(ref disposed) != 0) { throw new InvalidDataException("media_socket_closed"); }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromMilliseconds(250));
            await socket.SendAsync(bytes.AsMemory(), binary ? WebSocketMessageType.Binary : WebSocketMessageType.Text,
                true, deadline.Token);
        }
        catch { Dispose(); throw; }
        finally { operations.Release(); }
    }
    internal async Task<(byte[] Bytes, bool Binary)?> ReceiveAsync(CancellationToken cancellation)
    {
        await operations.WaitAsync(cancellation);
        try
        {
            if (Volatile.Read(ref disposed) != 0) { throw new InvalidDataException("media_socket_closed"); }
            incoming ??= ReceiveMessageAsync();
            // Keep the same receive and its private bounded buffer across idle
            // polls. Cancelling a WebSocket read would destroy its connection.
            if (await Task.WhenAny(incoming, Task.Delay(20, cancellation)) != incoming)
            {
                cancellation.ThrowIfCancellationRequested();
                return null;
            }
            var result = await incoming;
            incoming = null;
            return result;
        }
        catch { Dispose(); throw; }
        finally { operations.Release(); }
    }
    private async Task<(byte[] Bytes, bool Binary)> ReceiveMessageAsync()
    {
        var buffer = new byte[65537];
        var count = 0;
        WebSocketMessageType? kind = null;
        try
        {
            while (true)
            {
                var part = await socket.ReceiveAsync(buffer.AsMemory(count), lifetime.Token);
                if (part.MessageType == WebSocketMessageType.Close || kind is not null && kind != part.MessageType)
                { throw new InvalidDataException("media_socket_closed"); }
                kind = part.MessageType;
                count += part.Count;
                if (count > 65536) { throw new InvalidDataException("media_frame_limit"); }
                if (part.EndOfMessage)
                {
                    if (count == 0) { throw new InvalidDataException("media_empty_message"); }
                    return (buffer.AsSpan(0, count).ToArray(), kind == WebSocketMessageType.Binary);
                }
            }
        }
        finally { Array.Clear(buffer); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        socket.Abort(); socket.Dispose(); http?.Dispose();
        cancellation.Dispose(); lifetime.Dispose();
        if (incoming is { } pending)
        {
            _ = pending.ContinueWith(task =>
            {
                if (task.Status == TaskStatus.RanToCompletion) { Array.Clear(task.Result.Bytes); }
                else { _ = task.Exception; }
            }, TaskScheduler.Default);
        }
    }
}

internal sealed partial class TwinMediaSessions
{
    private sealed class SocketSlot
    {
        internal bool Attempted;
        internal TwinDeviceMediaSocket? Socket;
    }
    private readonly ConditionalWeakTable<Actor, SocketSlot> mediaSockets = new();

    private async Task SocketServiceAsync(Actor actor, TwinMediaServiceRequest request, byte[] bytes)
    {
        var slot = mediaSockets.GetOrCreateValue(actor);
        if (request.Kind == "media-socket-open")
        {
            lock (actor.Gate)
            {
                if (slot.Attempted || actor.Cancelled) { throw new InvalidDataException("media_socket_reused"); }
                slot.Attempted = true;
            }
            // Fake remains the explicit offline legacy fixture. It never opens a
            // real socket or silently substitutes for a failed supported engine.
            if (actor.Settings.Engine == "fake")
            {
                await ResultAsync(actor, request.RequestId, 299, "application/json", []);
                return;
            }
            var baseUri = actor.Http.BaseAddress ?? throw new InvalidDataException("media_origin_missing");
            var uri = new UriBuilder(new Uri(baseUri, request.Path.TrimStart('/')))
            { Scheme = baseUri.Scheme == "https" ? "wss" : "ws" }.Uri;
            var authorization = actor.Http.DefaultRequestHeaders.Authorization?.ToString()
                ?? throw new InvalidDataException("media_identity_missing");
            var socket = await TwinDeviceMediaSocket.ConnectAsync(uri, authorization, request.IdempotencyKey, actor.Lifetime.Token);
            lock (actor.Gate)
            {
                if (actor.Cancelled || actor.Lifetime.IsCancellationRequested) { socket.Dispose(); throw new OperationCanceledException(); }
                slot.Socket = socket;
            }
            await ResultAsync(actor, request.RequestId, 204, "application/json", []);
            return;
        }
        var active = slot.Socket ?? throw new InvalidDataException("media_socket_missing");
        if (request.Kind == "media-socket-send")
        {
            await active.SendAsync(bytes, request.ContentType == "application/octet-stream", actor.Lifetime.Token);
            await ResultAsync(actor, request.RequestId, 204, "application/json", []);
        }
        else if (request.Kind == "media-socket-receive")
        {
            var result = await active.ReceiveAsync(actor.Lifetime.Token);
            if (result is null) { await ResultAsync(actor, request.RequestId, 204, "application/json", []); }
            else
            {
                try { await ResultAsync(actor, request.RequestId, 200,
                    result.Value.Binary ? "application/octet-stream" : "application/json", result.Value.Bytes); }
                finally { Array.Clear(result.Value.Bytes); }
            }
        }
        else
        {
            active.Dispose();
            await ResultAsync(actor, request.RequestId, 204, "application/json", []);
        }
    }
}

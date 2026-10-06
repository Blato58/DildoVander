using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Usage;

namespace Kufr.Backend.Features.GptLive;

// The pinned .NET SDK has no Live surface. This isolated transport follows the
// official Live wire schema; delegated Responses still use the official SDK.
internal sealed class GptLiveClient(IConfiguration configuration) : IDisposable
{
    private readonly HttpMessageInvoker transport = new(new HttpClientHandler { AllowAutoRedirect = false });
    internal bool Available => !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"])
        && CostLimits.Read(configuration).Enabled;
    internal async Task<GptLiveConnection> ConnectAsync(CancellationToken token)
    {
        if (!Available) throw new InvalidOperationException("GPT-Live is unavailable.");
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + configuration["OPENAI_API_KEY"]);
        try
        {
            await socket.ConnectAsync(new Uri("wss://api.openai.com/v1/live/sessions"), transport, token);
            return new GptLiveConnection(socket);
        }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose() => transport.Dispose();
}

internal sealed class GptLiveConnection(WebSocket socket) : IStreamingAudioInput, IAsyncDisposable
{
    private readonly SemaphoreSlim writes = new(1, 1);
    private bool closing;
    private long inputPcmBytes;
    internal long InputPcmBytes => Interlocked.Read(ref inputPcmBytes);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal void AudioReady() => ready.TrySetResult();
    public async Task SendAudioAsync(byte[] pcm, CancellationToken token)
    {
        await ready.Task.WaitAsync(token);
        await SendAsync(new { type = "session.input_audio.append", audio = Convert.ToBase64String(pcm) }, token, audio: true, audioBytes: pcm.Length);
    }

    internal Task CloseAsync(CancellationToken token, string? eventId = null) => eventId is null
        ? SendAsync(new { type = "session.close" }, token, close: true)
        : SendAsync(new { type = "session.close", event_id = eventId }, token, close: true);

    internal async Task SendAsync(object value, CancellationToken token, bool audio = false, bool close = false, int audioBytes = 0)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, RealtimeProtocol.JsonOptions);
        if (bytes.Length > 65_536) throw new InvalidDataException("Live command too large.");
        await writes.WaitAsync(token);
        try
        {
            // Serialize close with uploads; acknowledge late device chunks without
            // sending illegal input after the provider close request.
            if (audio && closing) return;
            if (close) closing = true;
            await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            if (audio) Interlocked.Add(ref inputPcmBytes, audioBytes);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); writes.Release(); }
    }

    internal async Task<JsonDocument> ReceiveAsync(CancellationToken token)
    {
        var bytes = System.Buffers.ArrayPool<byte>.Shared.Rent(262_144);
        var used = 0;
        try
        {
            while (used < bytes.Length)
            {
                var part = await socket.ReceiveAsync(bytes.AsMemory(used), token);
                if (part.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("Live closed without final usage.");
                used += part.Count;
                if (part.EndOfMessage)
                {
                    using var stream = new MemoryStream(bytes, 0, used, writable: false);
                    return JsonDocument.Parse(stream);
                }
            }
            throw new InvalidDataException("Live event too large.");
        }
        finally { CryptographicOperations.ZeroMemory(bytes); System.Buffers.ArrayPool<byte>.Shared.Return(bytes); }
    }

    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); return ValueTask.CompletedTask; }
}

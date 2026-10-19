using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Realtime;

internal static partial class RealtimeLiveEndpoints
{
    // One authenticated ordered connection replaces a request/response round
    // trip for every 200 ms microphone frame. No credential is placed in a URL.
    private static async Task AudioStreamAsync(string id, Guid streamId, HttpContext context,
        DeviceAccessStore access, RealtimeLiveRegistry registry)
    {
        var device = await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access);
        if (device is null)
        {
            await ApiProblem.Create(context, 401, "access_token_required").ExecuteAsync(context);
            return;
        }
        var session = registry.Find(streamId, device, id);
        if (session is null)
        {
            await ApiProblem.Create(context, 404, "realtime_session_not_found").ExecuteAsync(context);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await ApiProblem.Create(context, 400, "websocket_required").ExecuteAsync(context);
            return;
        }
        if (!session.AttachAudioStream())
        {
            await ApiProblem.Create(context, 409, "audio_stream_already_attached").ExecuteAsync(context);
            return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, session.Lifetime);
        WebSocket? socket = null;
        var buffer = new byte[14_001];
        try
        {
            socket = await context.WebSockets.AcceptWebSocketAsync();
            while (!lifetime.IsCancellationRequested)
            {
                if (await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) != device)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "access_token_expired", lifetime.Token);
                    return;
                }
                var used = 0;
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, used, buffer.Length - used), lifetime.Token);
                    used += received.Count;
                    if (received.MessageType == WebSocketMessageType.Close) { return; }
                    if (received.MessageType != WebSocketMessageType.Text || used > 14_000)
                    {
                        throw new InvalidDataException("invalid_audio_frame");
                    }
                } while (!received.EndOfMessage);

                using var document = JsonDocument.Parse(buffer.AsMemory(0, used));
                var root = document.RootElement;
                if (!TryReadAudioFrame(root, out var input, out var sequence, out var pcm))
                {
                    throw new InvalidDataException("invalid_audio_frame");
                }
                try
                {
                    if (await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) != device)
                    {
                        await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "access_token_expired", lifetime.Token);
                        return;
                    }
                    if (!await session.UploadAsync(input, sequence, pcm, lifetime.Token, streaming: true))
                    {
                        throw new InvalidDataException("realtime_input_mismatch");
                    }
                }
                finally { CryptographicOperations.ZeroMemory(pcm); }
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, used));
                var acknowledgement = JsonSerializer.SerializeToUtf8Bytes(new { type = "ack", inputId = input, sequence });
                await socket.SendAsync(acknowledgement, WebSocketMessageType.Text, true, lifetime.Token);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            if (socket?.State == WebSocketState.Open)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation,
                    error is InvalidDataException ? error.Message : "invalid_audio_frame", CancellationToken.None);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            socket?.Abort();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            socket?.Dispose();
            session.EndAudioStream();
        }
    }

    internal static bool TryReadAudioFrame(JsonElement root, out Guid input, out int sequence, out byte[] pcm)
    {
        input = Guid.Empty;
        sequence = -1;
        pcm = [];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 3
            || !root.TryGetProperty("inputId", out var inputField) || inputField.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(inputField.GetString(), "D", out input) || input == Guid.Empty
            || !root.TryGetProperty("sequence", out var sequenceField) || sequenceField.ValueKind != JsonValueKind.Number || !sequenceField.TryGetInt32(out sequence) || sequence < 0
            || !root.TryGetProperty("audio", out var audioField) || audioField.ValueKind != JsonValueKind.String
            || audioField.GetString() is not { Length: > 0 and <= 12_800 } encoded)
        {
            return false;
        }
        try { pcm = Convert.FromBase64String(encoded); }
        catch (FormatException) { return false; }
        if (pcm.Length is > 0 and <= 9_600 && pcm.Length % 2 == 0) { return true; }
        CryptographicOperations.ZeroMemory(pcm);
        pcm = [];
        return false;
    }
}

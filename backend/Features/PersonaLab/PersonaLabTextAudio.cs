using System.Net.WebSockets;
using System.Text.Json;

namespace Kufr.Backend.Features.PersonaLab;

internal sealed partial class PersonaLabRuntime
{
    // Live accepts typed context at session startup. Each explicit text submission
    // therefore opens the selected real engine using canonical retained history.
    // The account socket carries its actual PCM output, without microphone input,
    // an invented incremental Live command, a TTS service, or a second engine.
    private async Task TextAudioAsync(string owner, string id, WebSocket socket,
        Func<CancellationToken, Task<bool>> authorized, CancellationToken requestToken, string? platform)
    {
        var initial = Detail(owner, id);
        if (initial.Status != "open") { throw new LabException(409, "lab_session_closed"); }
        if (initial.Hardware is not null) { throw new LabException(409, "lab_hardware_bound"); }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        lifetime.CancelAfter(TimeSpan.FromSeconds(options?.MaxSessionSeconds ?? 180));
        var token = lifetime.Token;
        var sender = new LabSocketSender(socket, authorized);
        Task<LabSession>? turn = null;
        CancellationTokenSource? operation = null;
        Task<JsonDocument>? incoming = null;
        var guard = GuardAsync();
        try
        {
            // This acknowledges the account transport only. Provider execution is
            // acknowledged separately after that provider's actual handshake.
            await sender.JsonAsync(new { type = "ready", playbackMode = "stream", inputMode = "text" }, token);
            while (!token.IsCancellationRequested)
            {
                incoming ??= ReadAsync();
                var completed = turn is null
                    ? await Task.WhenAny(incoming, guard)
                    : await Task.WhenAny(incoming, guard, turn);
                if (completed == guard) { await guard; }
                if (completed == turn)
                {
                    try
                    {
                        var result = await turn!;
                        await sender.JsonAsync(new { type = "session", session = result }, token);
                        await sender.JsonAsync(new { type = "text_ready" }, token);
                    }
                    catch (LabException error) when (operation?.IsCancellationRequested == true && error.Code == "lab_request_cancelled")
                    { await sender.JsonAsync(new { type = "interrupted" }, token); }
                    catch (LabException error)
                    {
                        await sender.JsonAsync(new { type = "error", code = error.Code }, token);
                        return;
                    }
                    finally { operation?.Dispose(); operation = null; turn = null; }
                    continue;
                }
                using var message = await incoming;
                incoming = null;
                var type = GptLive.GptLiveProtocol.RequiredString(message.RootElement, "type", 20);
                if (type == "end") { return; }
                if (type == "cancel")
                {
                    if (operation is not null) { operation.Cancel(); }
                    else { await sender.JsonAsync(new { type = "interrupted" }, token); }
                    continue;
                }
                if (type != "text") { throw new LabException(400, "invalid_lab_command"); }
                if (turn is not null)
                {
                    await sender.JsonAsync(new { type = "busy", code = "lab_session_busy" }, token);
                    continue;
                }
                var text = GptLive.GptLiveProtocol.RequiredString(message.RootElement, "text", 4000);
                LabContract.Text(text, 4000);
                if (!await authorized(token)) { throw new LabException(401, "account_session_revoked"); }
                operation = CancellationTokenSource.CreateLinkedTokenSource(token);
                turn = TurnAsync(owner, id, text, operation.Token, platform: platform,
                    audioOutput: sender.AudioAsync,
                    providerReady: (providerSessionId, cancellation) => sender.JsonAsync(new
                    {
                        type = "provider_ready", providerSessionId,
                        engine = Detail(owner, id).Engine,
                        continuity = "canonical_history_new_provider_session"
                    }, cancellation));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (InvalidDataException) { throw new LabException(400, "invalid_lab_command"); }
        finally
        {
            lifetime.Cancel(); operation?.Cancel();
            var pending = new List<Task> { guard };
            if (incoming is not null) { pending.Add(incoming); }
            if (turn is not null) { pending.Add(turn); }
            try { await Task.WhenAll(pending); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            operation?.Dispose();
        }

        async Task GuardAsync()
        {
            while (!token.IsCancellationRequested)
            {
                if (!await authorized(token)) { throw new LabException(401, "account_session_revoked"); }
                if (Detail(owner, id).Status != "open") { throw new LabException(409, "lab_session_closed"); }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
        async Task<JsonDocument> ReadAsync()
        {
            var buffer = new byte[32768];
            var received = await socket.ReceiveAsync(buffer.AsMemory(), token);
            if (received.MessageType == WebSocketMessageType.Close) { return JsonDocument.Parse("{\"type\":\"end\"}"); }
            if (!received.EndOfMessage || received.MessageType != WebSocketMessageType.Text)
            { throw new LabException(400, "lab_text_socket_requires_text"); }
            try { return JsonDocument.Parse(buffer.AsMemory(0, received.Count)); }
            catch (JsonException) { throw new LabException(400, "invalid_lab_command"); }
        }
    }
}

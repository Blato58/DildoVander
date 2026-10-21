using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.DeviceMedia;

internal static class DeviceMediaEndpoints
{
    internal static IEndpointRouteBuilder MapDeviceMedia(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/conversations/{id}/device-media", RunAsync);
        return routes;
    }

    private static async Task RunAsync(string id, HttpContext context, DeviceAccessStore access,
        ConversationStore conversations, OpenAiRealtimeClient realtime, GptLiveClient live,
        RealtimeLiveRegistry registry, IHostApplicationLifetime host, WandDecisionService decisions,
        HybridMemoryService memory, TimeProvider clock)
    {
        // Physical devices must present their current device bearer. An account
        // browser cookie never authorizes this autonomous media connection.
        var device = context.Request.Headers.ContainsKey("Authorization")
            ? await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) : null;
        if (device is null)
        {
            await ApiProblem.Create(context, 401, "access_token_required").ExecuteAsync(context);
            return;
        }
        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key) || key == Guid.Empty)
        {
            await ApiProblem.Create(context, 400, "invalid_idempotency_key").ExecuteAsync(context);
            return;
        }
        if (!context.WebSockets.IsWebSocketRequest
            || !context.WebSockets.WebSocketRequestedProtocols.Contains(DeviceMediaProtocol.Subprotocol, StringComparer.Ordinal))
        {
            await ApiProblem.Create(context, 400, "device_media_protocol_required").ExecuteAsync(context);
            return;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(DeviceMediaProtocol.Subprotocol);
        var socket = new DeviceMediaSocket(webSocket, lifetime);
        var buffer = new byte[DeviceMediaProtocol.MaxMessageBytes + 1];
        RealtimeLiveSession? session = null;
        Task? run = null;
        Task<(WebSocketMessageType Kind, int Count)>? receive = null;
        var replies = new DeviceMediaControlReplies(socket, lifetime.Token);
        var inputs = new DeviceMediaInputOwner(socket, lifetime.Token);
        var controls = new DeviceMediaControlLedger();
        long inputBytes = 0;
        var authorization = WatchAuthorizationAsync();
        var sink = new DeviceMediaEventSink(socket, lifetime, streamId =>
        {
            var candidate = registry.Find(streamId, device, id);
            if (candidate is null || !candidate.AttachAudioStream())
            {
                throw new InvalidDataException("media_session_mismatch");
            }
            Volatile.Write(ref session, candidate);
        });
        try
        {
            using var openingDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            openingDeadline.CancelAfter(TimeSpan.FromSeconds(5));
            var opening = await socket.ReceiveAsync(buffer, openingDeadline.Token);
            if (opening.Kind != WebSocketMessageType.Text) { throw new InvalidDataException("invalid_media_open"); }
            if (await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) != device)
            {
                throw new InvalidDataException("access_token_expired");
            }
            var parsed = DeviceMediaProtocol.ReadOpening(buffer.AsMemory(0, opening.Count));
            CryptographicOperations.ZeroMemory(buffer);
            sink.BindOpening(parsed.Engine, RealtimeProtocol.ReadRequest(parsed.Request)!);
            // Both transports enter the same sole admission/provider lifecycle;
            // the core takes ownership of and zeroes the exact opening bytes.
            run = parsed.Engine == "gpt-live"
                ? GptLiveEndpoints.RunWithTransportAsync(id, context, device, key, parsed.Request, sink,
                    conversations, live, registry, host, decisions, memory, clock)
                : RealtimeLiveEndpoints.RunWithTransportAsync(id, context, device, key, parsed.Request, sink,
                    conversations, realtime, registry, host, memory);
            while (!lifetime.IsCancellationRequested)
            {
                receive = socket.ReceiveAsync(buffer, lifetime.Token);
                if (await Task.WhenAny(receive, run) == run)
                {
                    await run;
                    // Keep the last failure/completion event ordered before a
                    // normal close. Aborting here can discard already sent
                    // terminal bytes with a TCP reset.
                    await socket.CompleteAsync(lifetime.Token);
                    try
                    {
                        await receive.WaitAsync(TimeSpan.FromSeconds(2), lifetime.Token);
                        receive = null;
                    }
                    catch (TimeoutException) { }
                    break;
                }
                var message = await receive;
                receive = null;
                if (message.Kind == WebSocketMessageType.Close) { break; }
                if (await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) != device)
                {
                    throw new InvalidDataException("access_token_expired");
                }
                var current = Volatile.Read(ref session);
                if (current is null || registry.Find(current.Id, device, id) != current)
                {
                    throw new InvalidDataException("media_session_mismatch");
                }
                current.Lifetime.ThrowIfCancellationRequested();
                await current.EnsureHardwareAuthorityAsync(lifetime.Token);
                current.Lifetime.ThrowIfCancellationRequested();
                if (message.Kind == WebSocketMessageType.Binary)
                {
                    // One credit per input frame. The receiver never waits on a
                    // provider send or socket ACK, so cancel/close remain live.
                    if (inputs.Completion.IsCompleted) { await inputs.Completion; }
                    var header = DeviceMediaProtocol.ReadPcmHeader(buffer.AsSpan(0, message.Count));
                    inputBytes = checked(inputBytes + message.Count - DeviceMediaProtocol.HeaderBytes);
                    if (inputBytes > 24_000 * 2 * 180) { throw new InvalidDataException("media_input_capacity"); }
                    var input = sink.AdmitInput(header.Stream, header.Sequence);
                    var pcm = buffer.AsSpan(DeviceMediaProtocol.HeaderBytes, message.Count - DeviceMediaProtocol.HeaderBytes).ToArray();
                    if (!inputs.TrySubmit(current, input.Input, input.Sequence, header.Stream, pcm))
                    {
                        CryptographicOperations.ZeroMemory(pcm);
                        throw new InvalidDataException("media_input_credit_exceeded");
                    }
                }
                else if (message.Kind == WebSocketMessageType.Text && message.Count <= 1024)
                {
                    using var document = JsonDocument.Parse(buffer.AsMemory(0, message.Count));
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object
                        || !root.TryGetProperty("requestId", out var requestField) || requestField.ValueKind != JsonValueKind.String
                        || !Guid.TryParseExact(requestField.GetString(), "D", out var request) || request == Guid.Empty)
                    {
                        throw new InvalidDataException("invalid_media_control");
                    }
                    var cancel = root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        && type.GetString() == "cancel";
                    if (!cancel && !replies.TryTakeCredit())
                    {
                        throw new InvalidDataException("media_control_credit_exceeded");
                    }
                    if (replies.Completion.IsCompleted) { await replies.Completion; }
                    var replay = controls.IsReplay(request, buffer.AsSpan(0, message.Count));
                    Action<bool>? published = null;
                    if (!replay)
                    {
                        try
                        {
                            if (ApplyControl(root, current, sink, conversations, device, id, out published)) { break; }
                            controls.Complete(request, 204);
                        }
                        catch (ControlRefusal refusal) { controls.Complete(request, 409, refusal.Code); }
                    }
                    var receipt = controls.Result(request);
                    if (!replies.TryEnqueue(request, receipt.Status, receipt.Code, returnsCredit: !cancel, published))
                    {
                        published?.Invoke(false);
                        if (!cancel) { throw new InvalidDataException("media_control_credit_exceeded"); }
                    }
                }
                else { throw new InvalidDataException("invalid_media_frame"); }
                CryptographicOperations.ZeroMemory(buffer.AsSpan(0, message.Count));
            }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException
            or FormatException or OverflowException or OperationCanceledException or WebSocketException)
        {
            // Do not reflect visitor content, bearer material or exception text.
            // No HTTP ProblemDetails may be written after the upgrade.
        }
        finally
        {
            if (webSocket.State == WebSocketState.Closed) { lifetime.Cancel(); }
            else { socket.Abort(); }
            replies.Complete();
            inputs.Complete();
            session?.EndAudioStream();
            // Cancellation/Abort does not release a borrowed Memory buffer.
            // Observe the sole receive owner before scrubbing or disposing it.
            await DeviceMediaSocket.ReleaseReceiveAsync(receive, buffer);
            await ObserveAsync(inputs.Completion);
            await ObserveAsync(replies.Completion);
            await ObserveAsync(run);
            await ObserveAsync(authorization);
        }

        async Task WatchAuthorizationAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime.Token))
                {
                    if (await DeviceAccessEndpoints.AuthenticateCurrentAsync(context, access) != device)
                    {
                        socket.Abort();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }
    }

    // True closes only this authenticated operation/session. Every field is
    // explicit; a stale response or mismatched operation never falls back to a
    // global Stop or an unscoped registry cancellation.
    internal static bool ApplyControl(JsonElement root, RealtimeLiveSession session, DeviceMediaEventSink sink,
        ConversationStore conversations, string device, string conversation, out Action<bool>? published)
    {
        published = null;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var kind)
            || kind.ValueKind != JsonValueKind.String) { throw new InvalidDataException("invalid_media_control"); }
        switch (kind.GetString())
        {
            case "cancel":
                if (!DeviceMediaProtocol.Fields(root, "type", "operationId", "responseGeneration", "requestId")
                    || !Guid.TryParseExact(root.GetProperty("requestId").GetString(), "D", out var key) || key == Guid.Empty
                    || root.GetProperty("operationId").GetString() is not { Length: > 0 and <= 128 } operation)
                {
                    throw new InvalidDataException("media_cancel_mismatch");
                }
                if (!sink.HasPublishedOperation) { throw new ControlRefusal("current_operation_not_ready"); }
                var cancelGeneration = root.GetProperty("responseGeneration").GetInt64();
                var claim = sink.TryClaimCancellation(operation, cancelGeneration, () => session.OutputGeneration);
                if (claim is null)
                {
                    throw new ControlRefusal("media_cancel_mismatch");
                }
                bool cancelled;
                try
                {
                    // The store owns its own transaction gate; no RAM transport
                    // lock is held while this synchronous durable attempt runs.
                    cancelled = conversations.CancelOperation(device, operation, key).Status == 204;
                }
                catch
                {
                    sink.CompleteCancellation(claim, close: true);
                    throw;
                }
                sink.CompleteCancellation(claim, close: cancelled);
                if (!cancelled) { throw new ControlRefusal("media_cancel_mismatch"); }
                return true;
            case "playback":
                if (!DeviceMediaProtocol.Fields(root, "type", "requestId", "playbackId", "pcmBytes", "drained")
                    || !Guid.TryParseExact(root.GetProperty("playbackId").GetString(), "D", out var playback)
                    || !root.GetProperty("pcmBytes").TryGetInt32(out var bytes) || bytes < 0
                    || root.GetProperty("drained").ValueKind != JsonValueKind.True || !session.ConfirmCheckpoint(playback, bytes, true))
                {
                    throw new InvalidDataException("media_playback_mismatch");
                }
                return false;
            case "played":
                if (!DeviceMediaProtocol.Fields(root, "type", "requestId", "operationId", "responseGeneration"))
                {
                    throw new InvalidDataException("media_playback_mismatch");
                }
                var playedOperation = root.GetProperty("operationId").GetString()!;
                var playedGeneration = root.GetProperty("responseGeneration").GetInt64();
                var replyClaim = sink.ClaimPlayedReply(playedOperation, playedGeneration,
                    root.GetProperty("requestId").GetGuid(), () => session.OutputGeneration);
                if (replyClaim is null) { throw new ControlRefusal("media_playback_mismatch"); }
                if (!session.Played(playedOperation, playedGeneration))
                {
                    sink.CompletePlayedReply(replyClaim, delivered: false, refused: true);
                    throw new ControlRefusal("media_playback_mismatch");
                }
                published = delivered => sink.CompletePlayedReply(replyClaim, delivered);
                return false;
            case "availability":
                var epochPresent = root.TryGetProperty("operationEpoch", out var epoch);
                var fields = epochPresent
                    ? new[] { "type", "requestId", "bootId", "generation", "readyMask", "deviceRevision", "operationEpoch" }
                    : new[] { "type", "requestId", "bootId", "generation", "readyMask", "deviceRevision" };
                if (!DeviceMediaProtocol.Fields(root, fields)
                    || epochPresent && (epoch.ValueKind != JsonValueKind.String || !OperationEpoch.IsValid(epoch.GetString()))
                    || !Guid.TryParseExact(root.GetProperty("bootId").GetString(), "D", out var boot))
                {
                    throw new InvalidDataException("media_availability_mismatch");
                }
                var generation = root.GetProperty("generation").GetInt32();
                var mask = root.GetProperty("readyMask").GetInt32();
                var revision = root.GetProperty("deviceRevision").GetInt32();
                var originalEpoch = epochPresent ? epoch.GetString() : null;
                if (sink.RealtimeOpening is { } original)
                {
                    if (original.BootId != boot || original.Generation != generation || original.ReadyMask != mask
                        || original.DeviceRevision != revision || !OperationEpoch.Same(original.OperationEpoch, originalEpoch))
                    {
                        throw new ControlRefusal("conversation_restart_required");
                    }
                    return false;
                }
                if (!conversations.UpdateLiveAvailability(device, conversation, boot, generation, mask, revision, originalEpoch))
                {
                    throw new ControlRefusal("conversation_version_mismatch");
                }
                return false;
            default: throw new InvalidDataException("invalid_media_control");
        }
    }

    internal sealed class ControlRefusal(string code) : Exception(code)
    {
        internal string Code => Message;
    }

    private static async Task ObserveAsync(Task? task)
    {
        if (task is null) { return; }
        try { await task; }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException or InvalidDataException) { }
    }
}

using Kufr.Backend.Features.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Memory;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.Realtime;

internal static partial class RealtimeLiveEndpoints
{
    internal static IEndpointRouteBuilder MapRealtimeLive(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/conversations/{id}/realtime-live", RunAsync);
        routes.MapPost("/v1/conversations/{id}/realtime-live/{streamId:guid}/audio", UploadAsync);
        routes.MapPost("/v1/conversations/{id}/realtime-live/{streamId:guid}/played", PlayedAsync);
        routes.MapPost("/v1/conversations/{id}/gpt-live/{streamId:guid}/audio", UploadAsync);
        routes.MapPost("/v1/conversations/{id}/gpt-live/{streamId:guid}/played", PlayedAsync);
        routes.MapGet("/v1/conversations/{id}/realtime-live/{streamId:guid}/audio-stream", AudioStreamAsync);
        routes.MapGet("/v1/conversations/{id}/gpt-live/{streamId:guid}/audio-stream", AudioStreamAsync);
        return routes;
    }

    private static async Task RunAsync(string id, HttpContext context, DeviceAccessStore access,
        ConversationStore conversations, OpenAiRealtimeClient provider, RealtimeLiveRegistry registry,
        IHostApplicationLifetime host, HybridMemoryService memory)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
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
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        lifetime.CancelAfter(TimeSpan.FromMinutes(8));
        using var writes = new SemaphoreSlim(1);
        RealtimeCall? call = null;
        RealtimeLiveSession? live = null;
        var dispatched = false;
        var completed = false;
        Task? heartbeat = null;
        Task? visualWatcher = null;
        var registered = false;
        async Task<long> CancelOutputAsync(int revision, CancellationToken token)
        {
            await writes.WaitAsync(token);
            try
            {
                var generation = live!.CancelOutput();
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { type = "audio_cancel", responseGeneration = generation, visualRevision = revision }, RealtimeProtocol.JsonOptions) + "\n", token);
                await context.Response.Body.FlushAsync(token);
                return generation;
            }
            finally { writes.Release(); }
        }
        try
        {
            var body = await ReadBytesAsync(context, 4096);
            var request = RealtimeProtocol.ReadRequest(body);
            if (request is null || !request.Opening || !RealtimeProtocol.IsValid(request))
            {
                throw new InvalidDataException();
            }
            var admission = conversations.BeginRealtimeTurn(device, id, key, body, request,
                provider.Available, lifetime.Token, host.ApplicationStopping, handsFree: true);
            CryptographicOperations.ZeroMemory(body);
            if (admission.Call is null)
            {
                await ApiProblem.Create(context, admission.Status, admission.Code!).ExecuteAsync(context);
                return;
            }
            call = admission.Call;
            if (!conversations.RegisterRealtimeLifetime(call, lifetime))
            {
                throw new OperationCanceledException();
            }
            registered = true;
            call = await ConversationStartupMemory.PrepareAsync(call, conversations, memory, call.Cancellation.Token);
            context.Response.ContentType = "application/x-ndjson";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await using var connection = await provider.ConnectAsync(call.Cancellation.Token);
            using var session = new RealtimeLiveSession(device, id, connection, lifetime);
            live = session;
            if (!registry.Add(session))
            {
                throw new InvalidOperationException();
            }
            Task WriteAsync(object item, CancellationToken token) => WriteEventAsync(context, writes, item, token);
            await WriteAsync(new { type = "session", streamId = session.Id, audioFormat = "pcm16-mono-24000" }, lifetime.Token);
            heartbeat = HeartbeatAsync(WriteAsync, lifetime);
            visualWatcher = VisualResponseWatcher.RunAsync(conversations, session, WriteAsync, lifetime.Token, CancelOutputAsync);
            for (var turn = 0; turn < 10; turn++)
            {
                var current = call!;
                completed = false;
                dispatched = false;
                async Task SourceEventAsync(Func<long, object> item, CancellationToken token)
                {
                    var visual = connection.VisualGeneration;
                    await session.WaitForVisualBarrierAsync(visual, token);
                    var generation = session.OutputGeneration;
                    await WriteEventAsync(context, writes, item(generation), token,
                        () => session.OutputGeneration == generation && conversations.ReadVisualResponseRevision(device, id).Generation == visual);
                }
                Task ResponseReadyAsync(CancellationToken token) => SourceEventAsync(generation => new
                { type = "started", operationId = current.OperationId, correlation = current.Correlation, responseGeneration = generation }, token);
                if (!conversations.StartRealtimeTurn(current))
                {
                    throw new OperationCanceledException(current.Cancellation.Token);
                }
                dispatched = true;
                TurnResult? result = null;
                Task? played = null;
                long finalizedGeneration = 0, finalizedVisual = 0;
                async Task FinalizeAsync(RealtimeReply response, CancellationToken token)
                {
                    var visual = connection.VisualGeneration;
                    await session.WaitForVisualBarrierAsync(visual, token);
                    var generation = session.OutputGeneration;
                    await VisualResponsePublisher.PublishAsync(conversations, session, writes, visual, generation, () =>
                    {
                        result = conversations.CompleteRealtimeTurn(current, response) ?? throw new InvalidDataException();
                        completed = true; finalizedGeneration = generation; finalizedVisual = visual;
                        played = session.PreparePlayback(current.OperationId);
                        return new { type = "completed", operationId = current.OperationId, correlation = current.Correlation,
                            result, costMicroUsd = response.CostMicroUsd, responseGeneration = generation };
                    }, async (item, writeToken) =>
                    {
                        ServerLogEvents.Stream(context, item);
                        await context.Response.WriteAsync(JsonSerializer.Serialize(item, RealtimeProtocol.JsonOptions) + "\n", writeToken);
                        await context.Response.Body.FlushAsync(writeToken);
                    }, token);
                }
                var response = await connection.GenerateTurnAsync(current.Context, null,
                    async (pcm, token) =>
                    {
                        if (!conversations.IsRealtimeTurnCurrent(current)) { throw new OperationCanceledException(token); }
                        var visual = connection.VisualGeneration;
                        await session.WaitForVisualBarrierAsync(visual, token);
                        var generation = session.OutputGeneration;
                        await WriteEventAsync(context, writes, new { type = "audio", delta = Convert.ToBase64String(pcm), responseGeneration = generation }, token,
                            () => session.OutputGeneration == generation && conversations.ReadVisualResponseRevision(device, id).Generation == visual);
                    },
                    (text, token) => SourceEventAsync(generation => new { type = "visitor", text, responseGeneration = generation }, token),
                    current.Cancellation.Token,
                    async token =>
                    {
                        session.Listen(current);
                        await SourceEventAsync(generation => new { type = "listening", inputId = current.Context.Request.ClientTurnId, responseGeneration = generation }, token);
                    },
                    async (type, token) =>
                    {
                        if (type == "speech_stopped")
                        {
                            await session.StopListeningAsync(token);
                        }
                        // VAD can finish accepted input after a visual revoke.
                        // Drain its literal transcription before resetting the
                        // provider; never publish old controls into the new generation.
                        try { await SourceEventAsync(generation => new { type, inputId = current.Context.Request.ClientTurnId, responseGeneration = generation }, token); }
                        catch (VisualResponseRevokedException) { }
                    },
                    ResponseReadyAsync,
                    transcript: (eventId, visitorText, replyText) => conversations.AppendRealtimeTranscript(current, eventId, visitorText, replyText),
                    visualImage: () => conversations.ReadVisualImage(device, id),
                    inputContext: (text, token) => ConversationStartupMemory.ProcessInputAsync(current, conversations, memory, text, token),
                    visualContext: () => conversations.ReadVisualContext(device, id),
                    visualRevision: () => conversations.ReadVisualResponseRevision(device, id),
                    recoveryContext: () => conversations.LiveMemoryAccess(current) is { } permitted ? memory.RecoveryContext(permitted).Text : "",
                    finalize: FinalizeAsync);
                session.StopListening();
                conversations.ReleaseRealtimeTurn(current, dispatched, completed);
                call = null;
                if (result!.Decision != "ask" || turn == 9)
                {
                    await played!.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
                    await WriteEventAsync(context, writes, new { type = "closed", reason = "conversation_terminal", responseGeneration = finalizedGeneration }, lifetime.Token,
                        () => session.OutputGeneration == finalizedGeneration && conversations.ReadVisualResponseRevision(device, id).Generation == finalizedVisual);
                    return;
                }
                await played!.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
                request = request with { ClientTurnId = Guid.NewGuid(), Opening = false };
                var nextBody = JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions);
                admission = conversations.BeginRealtimeTurn(device, id, Guid.NewGuid(), nextBody, request,
                    provider.Available, lifetime.Token, host.ApplicationStopping, handsFree: true);
                CryptographicOperations.ZeroMemory(nextBody);
                if (admission.Call is null)
                {
                    await WriteAsync(new { type = "failed", code = admission.Code }, lifetime.Token);
                    return;
                }
                call = admission.Call;
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!context.Response.HasStarted)
            {
                var invalidInput = call is null && exception is (JsonException or InvalidDataException);
                var status = exception is BadHttpRequestException badRequest
                    ? badRequest.StatusCode
                    : invalidInput ? 400 : 503;
                var code = exception is BadHttpRequestException ? "request_too_large"
                    : invalidInput ? "invalid_request"
                    : exception is OperationCanceledException ? "operation_cancelled" : "ai_unavailable";
                await ApiProblem.Create(context, status, code).ExecuteAsync(context);
            }
            else if (!context.RequestAborted.IsCancellationRequested)
            {
                using var final = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    var code = exception is OperationCanceledException ? "operation_cancelled" : "ai_unavailable";
                    await WriteEventAsync(context, writes, new { type = "failed", code }, final.Token);
                }
                catch (Exception)
                {
                    context.Abort();
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            if (registered)
            {
                conversations.ReleaseRealtimeLifetime(id);
            }
            if (call is not null)
            {
                conversations.ReleaseRealtimeTurn(call, dispatched, completed);
            }
            if (live is not null)
            {
                registry.Remove(live.Id);
            }
            if (visualWatcher is not null) { try { await visualWatcher; } catch (Exception) { } }
            if (heartbeat is not null)
            {
                try
                {
                    await heartbeat;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // A broken output stream must still release the paid slot
                    // and unregister the session before cleanup returns.
                }
            }
        }
    }

    private static async Task<IResult> UploadAsync(string id, Guid streamId, HttpContext context,
        DeviceAccessStore access, RealtimeLiveRegistry registry)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }
        var session = registry.Find(streamId, device, id);
        if (session is null)
        {
            return ApiProblem.Create(context, 404, "realtime_session_not_found");
        }
        byte[]? bytes = null;
        try
        {
            if (context.Request.ContentType != "application/octet-stream"
                || !Guid.TryParseExact(context.Request.Headers["Input-Id"], "D", out var input)
                || !int.TryParse(context.Request.Headers["Input-Sequence"], out var sequence) || sequence < 0)
            {
                return ApiProblem.Create(context, 400, "invalid_audio_frame");
            }
            bytes = await ReadBytesAsync(context, 9600);
            return await session.UploadAsync(input, sequence, bytes, context.RequestAborted)
                ? Results.NoContent()
                : ApiProblem.Create(context, 409, "realtime_input_mismatch");
        }
        catch (BadHttpRequestException exception)
        {
            return ApiProblem.Create(context, exception.StatusCode, "request_too_large");
        }
        catch (OperationCanceledException)
        {
            return ApiProblem.Create(context, 409, "operation_cancelled");
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private static async Task<IResult> PlayedAsync(string id, Guid streamId, HttpContext context,
        DeviceAccessStore access, RealtimeLiveRegistry registry)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null)
        {
            return ApiProblem.Create(context, 401, "access_token_required");
        }
        var session = registry.Find(streamId, device, id);
        if (session is null)
        {
            return ApiProblem.Create(context, 404, "realtime_session_not_found");
        }
        try
        {
            var body = await ReadBytesAsync(context, 512);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return ApiProblem.Create(context, 400, "invalid_request"); }
            var count = root.EnumerateObject().Count();
            long? generation = null;
            var validGeneration = !root.TryGetProperty("responseGeneration", out var value)
                || value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) && parsed is >= 0 and <= 9_007_199_254_740_991
                    && (generation = parsed) is not null;
            return root.ValueKind == JsonValueKind.Object && count == (generation is null ? 1 : 2) && validGeneration
                && root.TryGetProperty("operationId", out var operation)
                && operation.ValueKind == JsonValueKind.String && session.Played(operation.GetString()!, generation)
                    ? Results.NoContent()
                    : ApiProblem.Create(context, 409, "realtime_playback_mismatch");
        }
        catch (JsonException)
        {
            return ApiProblem.Create(context, 400, "invalid_request");
        }
        catch (BadHttpRequestException exception)
        {
            return ApiProblem.Create(context, exception.StatusCode, "request_too_large");
        }
    }

    private static async Task<byte[]> ReadBytesAsync(HttpContext context, int maximum)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = maximum;
        }
        var buffer = new byte[maximum + 1];
        try
        {
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(used), context.RequestAborted);
                if (read == 0)
                {
                    return buffer.AsSpan(0, used).ToArray();
                }
                used += read;
            }
            throw new BadHttpRequestException("Request too large.", 413);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static async Task WriteEventAsync(HttpContext context, SemaphoreSlim writes, object item,
        CancellationToken token, Func<bool>? current = null)
    {
        await writes.WaitAsync(token);
        try
        {
            if (current?.Invoke() == false) { throw new VisualResponseRevokedException(); }
            ServerLogEvents.Stream(context, item);
            await context.Response.WriteAsync(JsonSerializer.Serialize(item, RealtimeProtocol.JsonOptions) + "\n", Encoding.UTF8, token);
            await context.Response.Body.FlushAsync(token);
        }
        finally
        {
            writes.Release();
        }
    }

    private static async Task HeartbeatAsync(Func<object, CancellationToken, Task> write,
        CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token))
            {
                await write(new { type = "heartbeat" }, lifetime.Token);
            }
        }
        catch
        {
            lifetime.Cancel();
            throw;
        }
    }
}

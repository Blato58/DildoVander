using Kufr.Backend.Features.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.Realtime;

internal static class RealtimeLiveEndpoints
{
    internal static IEndpointRouteBuilder MapRealtimeLive(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/conversations/{id}/realtime-live", RunAsync);
        routes.MapPost("/v1/conversations/{id}/realtime-live/{streamId:guid}/audio", UploadAsync);
        routes.MapPost("/v1/conversations/{id}/realtime-live/{streamId:guid}/played", PlayedAsync);
        return routes;
    }

    private static async Task RunAsync(string id, HttpContext context, DeviceAccessStore access,
        ConversationStore conversations, OpenAiRealtimeClient provider, RealtimeLiveRegistry registry,
        IHostApplicationLifetime host)
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
        var registered = false;
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
            for (var turn = 0; turn < 10; turn++)
            {
                var current = call!;
                completed = false;
                dispatched = false;
                Task ResponseReadyAsync(CancellationToken token) => WriteAsync(new
                {
                    type = "started",
                    operationId = current.OperationId,
                    correlation = current.Correlation
                }, token);
                if (!current.Context.LiveAudio)
                {
                    await ResponseReadyAsync(current.Cancellation.Token);
                }
                if (!conversations.StartRealtimeTurn(current))
                {
                    throw new OperationCanceledException(current.Cancellation.Token);
                }
                dispatched = true;
                var response = await connection.GenerateTurnAsync(current.Context, null,
                    (pcm, token) => WriteAsync(new { type = "audio", delta = Convert.ToBase64String(pcm) }, token),
                    (text, token) => WriteAsync(new { type = "visitor", text }, token),
                    current.Cancellation.Token,
                    async token =>
                    {
                        session.Listen(current);
                        await WriteAsync(new { type = "listening", inputId = current.Context.Request.ClientTurnId }, token);
                    },
                    async (type, token) =>
                    {
                        if (type == "speech_stopped")
                        {
                            await session.StopListeningAsync(token);
                        }
                        await WriteAsync(new { type, inputId = current.Context.Request.ClientTurnId }, token);
                    },
                    current.Context.LiveAudio ? ResponseReadyAsync : null);
                session.StopListening();
                var result = conversations.CompleteRealtimeTurn(current, response)
                    ?? throw new InvalidDataException();
                completed = true;
                var played = session.PreparePlayback(current.OperationId);
                await WriteAsync(new { type = "completed", operationId = current.OperationId,
                    correlation = current.Correlation, result, costMicroUsd = response.CostMicroUsd }, lifetime.Token);
                conversations.ReleaseRealtimeTurn(current, dispatched, completed);
                call = null;
                if (result.Decision != "ask" || turn == 9)
                {
                    await played.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
                    await WriteAsync(new { type = "closed", reason = "conversation_terminal" }, lifetime.Token);
                    return;
                }
                await played.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
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
            return root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Count() == 1
                && root.TryGetProperty("operationId", out var operation)
                && operation.ValueKind == JsonValueKind.String && session.Played(operation.GetString()!)
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
        CancellationToken token)
    {
        await writes.WaitAsync(token);
        try
        {
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

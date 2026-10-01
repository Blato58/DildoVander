using Kufr.Backend.Features.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.Realtime;

internal static class RealtimeEndpoints
{
    private const int MaxRequestBytes = 2_600_000;
    internal static IEndpointRouteBuilder MapRealtimeTurns(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/conversations/{id}/realtime-turns", StreamTurnAsync);
        return routes;
    }

    private static async Task StreamTurnAsync(
        string id,
        HttpContext context,
        DeviceAccessStore access,
        ConversationStore conversations,
        OpenAiRealtimeClient provider,
        IHostApplicationLifetime lifetime)
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
        byte[]? body = null;
        byte[]? wav = null;
        byte[]? pcm = null;
        RealtimeCall? call = null;
        var dispatched = false;
        var completed = false;
        try
        {
            var limit = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (limit is { IsReadOnly: false })
            {
                limit.MaxRequestBodySize = MaxRequestBytes;
            }
            if (context.Request.ContentType?.Split(';')[0] != "application/json")
            {
                throw new InvalidDataException();
            }
            body = await ReadBodyAsync(context.Request, context.RequestAborted);
            var request = RealtimeProtocol.ReadRequest(body);
            if (request is null || !RealtimeProtocol.IsValid(request))
            {
                throw new InvalidDataException();
            }
            if (request.Wav is not null)
            {
                wav = Convert.FromBase64String(request.Wav);
                pcm = RealtimeAudio.FromDeviceWav(wav);
            }
            var admitted = conversations.BeginRealtimeTurn(device, id, key, body, request,
                provider.Available, context.RequestAborted, lifetime.ApplicationStopping);
            if (admitted.Call is null)
            {
                await ApiProblem.Create(context, admitted.Status, admitted.Code!).ExecuteAsync(context);
                return;
            }
            call = admitted.Call;
            var token = call.Cancellation.Token;
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-ndjson";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await WriteAsync(context, new
            {
                type = "started",
                operationId = call.OperationId,
                correlation = call.Correlation,
                audioFormat = "pcm16-mono-24000"
            }, token);
            if (!conversations.StartRealtimeTurn(call))
            {
                throw new OperationCanceledException(token);
            }
            dispatched = true;
            var response = await provider.GenerateAsync(call.Context, pcm,
                async (chunk, cancellation) =>
                {
                    if (!conversations.IsRealtimeTurnCurrent(call))
                    {
                        throw new OperationCanceledException(cancellation);
                    }
                    await WriteAsync(context, new { type = "audio", delta = Convert.ToBase64String(chunk) }, cancellation);
                },
                (text, cancellation) => WriteAsync(context, new { type = "visitor", text }, cancellation),
                token);
            var result = conversations.CompleteRealtimeTurn(call, response);
            if (result is null)
            {
                throw new InvalidDataException();
            }
            completed = true;
            await WriteAsync(context, new
            {
                type = "completed",
                operationId = call.OperationId,
                correlation = call.Correlation,
                result
            }, token);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (!context.Response.HasStarted)
            {
                var oversized = exception is BadHttpRequestException { StatusCode: 413 };
                await ApiProblem.Create(context, oversized ? 413 : 400,
                    oversized ? "request_too_large" : "invalid_request").ExecuteAsync(context);
            }
            else if (!context.RequestAborted.IsCancellationRequested)
            {
                // Bounded technical codes only, never provider messages or text.
                using var finalDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await WriteAsync(context, new
                    {
                        type = "failed",
                        code = exception is OperationCanceledException ? "operation_cancelled" : "ai_unavailable"
                    }, finalDeadline.Token);
                }
                catch (Exception)
                {
                    context.Abort();
                }
            }
        }
        finally
        {
            if (call is not null)
            {
                conversations.ReleaseRealtimeTurn(call, dispatched, completed);
            }
            foreach (var sensitive in new[] { body, wav, pcm })
            {
                if (sensitive is not null)
                {
                    CryptographicOperations.ZeroMemory(sensitive);
                }
            }
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken token)
    {
        if (request.ContentLength > MaxRequestBytes)
        {
            throw new BadHttpRequestException("Request too large.", 413);
        }
        var buffer = new byte[MaxRequestBytes + 1];
        try
        {
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await request.Body.ReadAsync(buffer.AsMemory(used), token);
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

    private static async Task WriteAsync(HttpContext context, object item, CancellationToken token)
    {
        ServerLogEvents.Stream(context, item);
        var line = JsonSerializer.Serialize(item, RealtimeProtocol.JsonOptions) + "\n";
        await context.Response.WriteAsync(line, Encoding.UTF8, token);
        await context.Response.Body.FlushAsync(token);
    }
}

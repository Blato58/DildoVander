using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;

namespace Kufr.Backend.Features.GptLive;

internal static class GptLiveEndpoints
{
    internal static IEndpointRouteBuilder MapGptLive(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/conversations/{id}/gpt-live", RunAsync);
        return routes;
    }

    private static async Task RunAsync(string id, HttpContext context, DeviceAccessStore access,
        ConversationStore conversations, GptLiveClient provider, TextDialogRunner dialog,
        RealtimeLiveRegistry registry, IHostApplicationLifetime host)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { await ApiProblem.Create(context, 401, "access_token_required").ExecuteAsync(context); return; }
        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key) || key == Guid.Empty)
        { await ApiProblem.Create(context, 400, "invalid_idempotency_key").ExecuteAsync(context); return; }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping);
        lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        using var writes = new SemaphoreSlim(1);
        RealtimeCall? call = null;
        RealtimeLiveSession? session = null;
        Task? heartbeat = null;
        var dispatched = false;
        var completed = false;
        var registered = false;
        async Task Write(object item, CancellationToken token)
        {
            await writes.WaitAsync(token);
            try
            {
                await context.Response.WriteAsync(JsonSerializer.Serialize(item, RealtimeProtocol.JsonOptions) + "\n", token);
                await context.Response.Body.FlushAsync(token);
            }
            finally { writes.Release(); }
        }
        try
        {
            var body = new byte[4097];
            var used = 0;
            while (used < body.Length)
            {
                var count = await context.Request.Body.ReadAsync(body.AsMemory(used), lifetime.Token);
                if (count == 0) break;
                used += count;
            }
            if (used > 4096) throw new InvalidDataException();
            var payload = body.AsSpan(0, used).ToArray();
            CryptographicOperations.ZeroMemory(body);
            var request = RealtimeProtocol.ReadRequest(payload);
            // GPT-Live has no speed setting: reject rather than silently ignore it.
            if (request is null || !request.Opening || request.Speed != 1 || !RealtimeProtocol.IsValid(request))
                throw new InvalidDataException();
            var admission = conversations.BeginRealtimeTurn(device, id, key, payload, request,
                provider.Available, lifetime.Token, host.ApplicationStopping, handsFree: true, gptLive: true);
            CryptographicOperations.ZeroMemory(payload);
            if (admission.Call is null)
            { await ApiProblem.Create(context, admission.Status, admission.Code!).ExecuteAsync(context); return; }
            call = admission.Call;
            if (!conversations.RegisterRealtimeLifetime(call, lifetime)) throw new OperationCanceledException();
            registered = true;
            if (!conversations.StartRealtimeTurn(call)) throw new OperationCanceledException();
            // Session.start may incur cost even if the socket fails subsequently.
            dispatched = true;
            await using var connection = await provider.ConnectAsync(call.Cancellation.Token);
            using var live = new RealtimeLiveSession(device, id, connection, lifetime, continuous: true);
            session = live;
            if (!registry.Add(live)) throw new InvalidOperationException();
            context.Response.ContentType = "application/x-ndjson";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            await Write(new { type = "session", streamId = live.Id, audioFormat = "pcm16-mono-24000" }, lifetime.Token);
            await Write(new { type = "started", operationId = call.OperationId, correlation = call.Correlation }, lifetime.Token);
            live.Listen(call);
            await Write(new { type = "listening", inputId = request.ClientTurnId }, lifetime.Token);
            heartbeat = Heartbeat(Write, lifetime);
            var outcome = await new GptLiveDialogue(connection, dialog).RunAsync(call.Context,
                (pcm, token) => Write(new { type = "audio", delta = Convert.ToBase64String(pcm) }, token),
                (text, token) => Write(new { type = "visitor", text }, token),
                token => Write(new { type = "pause" }, token), call.Cancellation.Token);
            await live.StopListeningAsync(lifetime.Token);
            await Write(new { type = "speech_stopped", inputId = request.ClientTurnId }, lifetime.Token);
            var result = conversations.CompleteGptLive(call, outcome) ?? throw new InvalidDataException();
            completed = true;
            var played = live.PreparePlayback(call.OperationId);
            await Write(new { type = "completed", operationId = call.OperationId, correlation = call.Correlation,
                result, costMicroUsd = outcome.Reply.CostMicroUsd }, lifetime.Token);
            await played.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
            await Write(new { type = "closed", reason = "conversation_terminal" }, lifetime.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            var code = error is OperationCanceledException ? "operation_cancelled"
                : call is null && error is (JsonException or InvalidDataException) ? "invalid_request" : "ai_unavailable";
            if (!context.Response.HasStarted)
                await ApiProblem.Create(context, code == "invalid_request" ? 400 : 503, code).ExecuteAsync(context);
            else if (!context.RequestAborted.IsCancellationRequested)
            {
                using var final = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await Write(new { type = "failed", code }, final.Token); }
                catch { context.Abort(); }
            }
        }
        finally
        {
            lifetime.Cancel();
            if (registered) conversations.ReleaseRealtimeLifetime(id);
            if (session is not null) registry.Remove(session.Id);
            if (call is not null) conversations.ReleaseRealtimeTurn(call, dispatched, completed);
            if (heartbeat is not null) { try { await heartbeat; } catch (Exception) { } }
        }
    }

    private static async Task Heartbeat(Func<object, CancellationToken, Task> write, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await write(new { type = "heartbeat" }, lifetime.Token); }
        catch { lifetime.Cancel(); throw; }
    }
}

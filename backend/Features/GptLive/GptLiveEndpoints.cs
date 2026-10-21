using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Diagnostics;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Show;

namespace Kufr.Backend.Features.GptLive;

internal static class GptLiveEndpoints
{
    internal static IEndpointRouteBuilder MapGptLive(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/conversations/{id}/gpt-live", RunAsync);
        routes.MapPost("/v1/conversations/{id}/gpt-live/{streamId:guid}/playback", ConfirmPlaybackAsync);
        routes.MapPost("/v1/conversations/{id}/gpt-live/{streamId:guid}/availability", UpdateAvailabilityAsync);
        return routes;
    }

    private static async Task RunAsync(string id, HttpContext context, DeviceAccessStore access,
        ConversationStore conversations, GptLiveClient provider,
        RealtimeLiveRegistry registry, IHostApplicationLifetime host, WandDecisionService decisions,
        HybridMemoryService memory, TimeProvider clock)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { await ApiProblem.Create(context, 401, "access_token_required").ExecuteAsync(context); return; }
        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out var key) || key == Guid.Empty)
        { await ApiProblem.Create(context, 400, "invalid_idempotency_key").ExecuteAsync(context); return; }
        await RunCoreAsync(id, context, device, key, null, new HttpConversationEventSink(context),
            conversations, provider, registry, host, decisions, memory, clock);
    }

    internal static Task RunWithTransportAsync(string id, HttpContext context, string authenticatedDevice,
        Guid key, byte[] openingPayload, ConversationEventSink output, ConversationStore conversations,
        GptLiveClient provider, RealtimeLiveRegistry registry, IHostApplicationLifetime host,
        WandDecisionService decisions, HybridMemoryService memory, TimeProvider clock) =>
        RunCoreAsync(id, context, authenticatedDevice, key, openingPayload, output,
            conversations, provider, registry, host, decisions, memory, clock);

    private static async Task RunCoreAsync(string id, HttpContext context, string device, Guid key,
        byte[]? openingPayload, ConversationEventSink output, ConversationStore conversations,
        GptLiveClient provider, RealtimeLiveRegistry registry, IHostApplicationLifetime host,
        WandDecisionService decisions, HybridMemoryService memory, TimeProvider clock)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, host.ApplicationStopping, output.Disconnected);
        lifetime.CancelAfter(TimeSpan.FromSeconds(180));
        using var writes = new SemaphoreSlim(1);
        RealtimeCall? call = null;
        RealtimeLiveSession? session = null;
        Task? heartbeat = null;
        Task? visualWatcher = null;
        var dispatched = false;
        var completed = false;
        var registered = false;
        async Task Write(object item, CancellationToken token, long? generation = null, long? visualGeneration = null)
        {
            await writes.WaitAsync(token);
            try
            {
                if (generation is { } expected && session?.OutputGeneration != expected
                    || visualGeneration is { } visual && conversations.ReadVisualResponseRevision(device, id).Generation != visual)
                { throw new VisualResponseRevokedException(); }
                ServerLogEvents.Stream(context, item);
                await output.WriteAsync(item, token);
            }
            finally { writes.Release(); }
        }
        async Task<long> CancelOutputAsync(int? revision, CancellationToken token)
        {
            await writes.WaitAsync(token);
            try
            {
                var generation = await output.AdvanceOutputAsync(() => session!.CancelOutput(), token);
                object item = revision is { } visual
                    ? new { type = "audio_cancel", responseGeneration = generation, visualRevision = visual }
                    : new { type = "audio_cancel", responseGeneration = generation };
                await output.WriteAsync(item, token);
                return generation;
            }
            finally { writes.Release(); }
        }
        try
        {
            openingPayload ??= await ReadOpeningAsync(context, lifetime.Token);
            if (openingPayload.Length > 4096 || key == Guid.Empty) throw new InvalidDataException();
            var payload = openingPayload;
            var request = RealtimeProtocol.ReadRequest(payload);
            // GPT-Live has no speed setting: reject rather than silently ignore it.
            if (request is null || !request.Opening || request.Speed != 1 || !RealtimeProtocol.IsValid(request))
                throw new InvalidDataException();
            var admission = conversations.BeginRealtimeTurn(device, id, key, payload, request,
                provider.Available, lifetime.Token, host.ApplicationStopping, handsFree: true, gptLive: true);
            CryptographicOperations.ZeroMemory(payload);
            if (admission.Call is null)
            { await output.ProblemAsync(admission.Status, admission.Code!); return; }
            call = admission.Call;
            if (!conversations.RegisterRealtimeLifetime(call, lifetime)) throw new OperationCanceledException();
            registered = true;
            if (!conversations.StartRealtimeTurn(call)) throw new OperationCanceledException();
            output.BindOperation(call.OperationId);
            if (call.LabHardware is { } hardwareAdmission) { await hardwareAdmission.EnsureAuthorizedAsync(call.Cancellation.Token); }
            var memoryAccess = conversations.LiveMemoryAccess(call);
            call = await ConversationStartupMemory.PrepareAsync(call, conversations, memory, call.Cancellation.Token, context.RequestServices);
            // Session.start may incur cost even if the socket fails subsequently.
            dispatched = true;
            await using var connection = await provider.ConnectAsync(call.Cancellation.Token);
            using var live = new RealtimeLiveSession(device, id, connection, lifetime, continuous: true, labHardware: call.LabHardware);
            session = live;
            if (!registry.Add(live)) throw new InvalidOperationException();
            output.Start(live.Id);
            await Write(new { type = "session", streamId = live.Id, audioFormat = "pcm16-mono-24000" }, lifetime.Token);
            await live.EnsureHardwareAuthorityAsync(call.Cancellation.Token);
            await Write(new ConversationOperationBinding(call.OperationId, live.OutputGeneration), call.Cancellation.Token,
                live.OutputGeneration, connection.VisualGeneration);
            await Write(new { type = "started", operationId = call.OperationId, correlation = call.Correlation, responseGeneration = live.OutputGeneration }, lifetime.Token);
            heartbeat = Heartbeat((item, token) => Write(item, token), lifetime);
            visualWatcher = VisualResponseWatcher.RunAsync(conversations, live, (item, token) => Write(item, token), lifetime.Token, (revision, token) => CancelOutputAsync(revision, token));
            var inputStopped = false;
            async Task StopInput(CancellationToken token)
            {
                live.StopListening();
                await Write(new { type = "speech_stopped", inputId = request.ClientTurnId, responseGeneration = live.OutputGeneration }, token);
                inputStopped = true;
            }
            async Task SourceEventAsync(Func<long, object> item, CancellationToken token)
            {
                var visual = connection.VisualGeneration;
                await live.WaitForVisualBarrierAsync(visual, token);
                var generation = live.OutputGeneration;
                await Write(item(generation), token, generation, visual);
            }
            Task? played = null;
            long finalizedGeneration = 0, finalizedVisual = 0;
            async Task FinalizeAsync(LiveOutcome outcome, CancellationToken token)
            {
                var visual = connection.VisualGeneration;
                await live.WaitForVisualBarrierAsync(visual, token);
                var generation = live.OutputGeneration;
                await VisualResponsePublisher.PublishAsync(conversations, live, writes, visual, generation, () =>
                {
                    var result = conversations.CompleteGptLive(call, outcome) ?? throw new InvalidDataException();
                    completed = true; finalizedGeneration = generation; finalizedVisual = visual;
                    played = live.PreparePlayback(call.OperationId);
                    return new { type = "completed", operationId = call.OperationId, correlation = call.Correlation,
                        result, costMicroUsd = outcome.Reply.CostMicroUsd, responseGeneration = generation };
                }, async (item, writeToken) =>
                {
                    ServerLogEvents.Stream(context, item);
                    await output.WriteAsync(item, writeToken);
                }, token);
            }
            var outcome = await new AutonomousLiveDialogue(connection, decisions, clock, memory,
                call.LabHardware is null ? context.RequestServices.GetService<ShowCoordinator>() : null).RunAsync(call,
                    async (pcm, token) =>
                    {
                        if (!conversations.IsRealtimeTurnCurrent(call)) { throw new OperationCanceledException(token); }
                        var visual = connection.VisualGeneration;
                        await live.WaitForVisualBarrierAsync(visual, token);
                        var generation = live.OutputGeneration;
                        await Write(new { type = "audio", delta = Convert.ToBase64String(pcm), responseGeneration = generation }, token, generation, visual);
                    },
                    (text, token) => SourceEventAsync(generation => new { type = "visitor", text, responseGeneration = generation }, token), token => SourceEventAsync(generation => new { type = "pause", responseGeneration = generation }, token),
                    async (checkpoint, token) =>
                    {
                        var confirmation = live.PrepareCheckpoint(checkpoint);
                        await SourceEventAsync(generation => new { type = "playback_checkpoint", playbackId = checkpoint.PlaybackId,
                            pcmBytes = checkpoint.PcmBytes, decisionRevision = checkpoint.DecisionRevision, responseGeneration = generation }, token);
                        return await confirmation.WaitAsync(token);
                    }, () => conversations.LiveAvailability(call), StopInput, call.Cancellation.Token, memoryAccess,
                    (ms, status) => context.RequestServices.GetService<ServerLogStore>()?.Write("info", "live_decision_completed",
                        context.TraceIdentifier, "/v1/conversations/{id}/gpt-live", "POST", durationMs: Math.Round(ms, 1),
                        operationId: call.OperationId, conversationId: id, code: status.ToString().ToLowerInvariant()),
                    state => context.RequestServices.GetService<ServerLogStore>()?.Write("info", "live_terminal_state",
                        context.TraceIdentifier, "/v1/conversations/{id}/gpt-live", "POST",
                        operationId: call.OperationId, conversationId: id, code: state),
                    cancelAudio: async (generation, token) => { await CancelOutputAsync(null, token); },
                    recentStyles: conversations.CharacterStyles(call),
                    stylePlayed: (style, opening) => conversations.RecordCharacterStyle(call, style, opening),
                    directorTiming: ms => context.RequestServices.GetService<ServerLogStore>()?.Write("info", "character_director",
                        context.TraceIdentifier, "/v1/conversations/{id}/gpt-live", "POST", durationMs: Math.Round(ms, 3),
                        operationId: call.OperationId, conversationId: id, code: "dv01_v1"),
                    transcript: segment => conversations.AppendRealtimeTranscript(call, segment),
                    visualContext: () => conversations.ReadVisualContext(device, id),
                    visualRevision: () => conversations.ReadVisualResponseRevision(device, id),
                    finalize: FinalizeAsync,
                    resumeInput: async token =>
                    {
                        live.Listen(call); inputStopped = false;
                        var visual = connection.VisualGeneration;
                        await live.WaitForVisualBarrierAsync(visual, token);
                        var generation = live.OutputGeneration;
                        await Write(new { type = "listening", inputId = request.ClientTurnId, responseGeneration = generation }, token, generation, visual);
                    });
            await live.StopListeningAsync(lifetime.Token);
            if (!inputStopped)
                await Write(new { type = "speech_stopped", inputId = request.ClientTurnId, responseGeneration = finalizedGeneration }, lifetime.Token, finalizedGeneration, finalizedVisual);
            await played!.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
            await Write(new { type = "closed", reason = "conversation_terminal", responseGeneration = finalizedGeneration }, lifetime.Token,
                finalizedGeneration, finalizedVisual);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (call is not null)
                context.RequestServices.GetService<ServerLogStore>()?.Write(error is OperationCanceledException ? "info" : "warning",
                    "live_failure_detail", context.TraceIdentifier, "/v1/conversations/{id}/gpt-live", "POST",
                    operationId: call.OperationId, conversationId: id, code: LiveFailureDiagnostics.Code(error));
            var code = error is OperationCanceledException ? "operation_cancelled"
                : call is null && error is (JsonException or InvalidDataException) ? "invalid_request" : "ai_unavailable";
            if (!output.HasStarted)
                await output.ProblemAsync(code == "invalid_request" ? 400 : 503, code);
            else if (!context.RequestAborted.IsCancellationRequested)
            {
                using var final = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await Write(new { type = "failed", code }, final.Token); }
                catch { output.Abort(); }
            }
        }
        finally
        {
            if (openingPayload is not null) CryptographicOperations.ZeroMemory(openingPayload);
            lifetime.Cancel();
            if (registered) conversations.ReleaseRealtimeLifetime(id);
            if (session is not null) registry.Remove(session.Id);
            if (call is not null) conversations.ReleaseRealtimeTurn(call, dispatched, completed);
            if (visualWatcher is not null) { try { await visualWatcher; } catch (Exception) { } }
            if (heartbeat is not null) { try { await heartbeat; } catch (Exception) { } }
        }
    }

    private static async Task<byte[]> ReadOpeningAsync(HttpContext context, CancellationToken token)
    {
        var buffer = new byte[4097];
        try
        {
            var used = 0;
            while (used < buffer.Length)
            {
                var count = await context.Request.Body.ReadAsync(buffer.AsMemory(used), token);
                if (count == 0) break;
                used += count;
            }
            if (used > 4096) throw new InvalidDataException();
            return buffer.AsSpan(0, used).ToArray();
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static async Task<IResult> ConfirmPlaybackAsync(string id, Guid streamId, HttpContext context,
        DeviceAccessStore access, RealtimeLiveRegistry registry)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) return ApiProblem.Create(context, 401, "access_token_required");
        var session = registry.Find(streamId, device, id);
        if (session is null) return ApiProblem.Create(context, 404, "realtime_session_not_found");
        try { await session.EnsureHardwareAuthorityAsync(context.RequestAborted); }
        catch (OperationCanceledException) { return ApiProblem.Create(context, 403, "lab_hardware_authority_revoked"); }
        try
        {
            using var body = await ReadControl(context);
            var root = body.RootElement;
            if (root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "drained", "pcmBytes", "playbackId" })
                && Guid.TryParseExact(root.GetProperty("playbackId").GetString(), "D", out var playback)
                && root.GetProperty("pcmBytes").TryGetInt32(out var bytes) && bytes >= 0
                && root.GetProperty("drained").ValueKind == JsonValueKind.True && session.ConfirmCheckpoint(playback, bytes, true))
                return Results.NoContent();
            return ApiProblem.Create(context, 409, "realtime_playback_mismatch");
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException)
        { return ApiProblem.Create(context, 400, "invalid_request"); }
    }
    private static async Task<IResult> UpdateAvailabilityAsync(string id, Guid streamId, HttpContext context,
        DeviceAccessStore access, RealtimeLiveRegistry registry, ConversationStore conversations)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) return ApiProblem.Create(context, 401, "access_token_required");
        var session = registry.Find(streamId, device, id);
        if (session is null) return ApiProblem.Create(context, 404, "realtime_session_not_found");
        try { await session.EnsureHardwareAuthorityAsync(context.RequestAborted); }
        catch (OperationCanceledException) { return ApiProblem.Create(context, 403, "lab_hardware_authority_revoked"); }
        try
        {
            using var body = await ReadControl(context); var r = body.RootElement;
            var hasEpoch = r.TryGetProperty("operationEpoch", out var epoch);
            if (hasEpoch && (epoch.ValueKind != JsonValueKind.String || !OperationEpoch.IsValid(epoch.GetString())))
                return ApiProblem.Create(context, 400, "invalid_request");
            var fields = hasEpoch
                ? new[] { "bootId", "deviceRevision", "generation", "operationEpoch", "readyMask" }
                : new[] { "bootId", "deviceRevision", "generation", "readyMask" };
            return r.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields)
                && Guid.TryParseExact(r.GetProperty("bootId").GetString(), "D", out var boot)
                && conversations.UpdateLiveAvailability(device, id, boot, r.GetProperty("generation").GetInt32(),
                    r.GetProperty("readyMask").GetInt32(), r.GetProperty("deviceRevision").GetInt32(), hasEpoch ? epoch.GetString() : null)
                ? Results.NoContent() : ApiProblem.Create(context, 409, "conversation_version_mismatch");
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or InvalidOperationException)
        { return ApiProblem.Create(context, 400, "invalid_request"); }
    }
    private static async Task<JsonDocument> ReadControl(HttpContext context)
    {
        var buffer = new byte[513]; var used = 0;
        while (used < buffer.Length)
        { var n = await context.Request.Body.ReadAsync(buffer.AsMemory(used), context.RequestAborted); if (n == 0) break; used += n; }
        if (used > 512) throw new InvalidDataException("Control too large.");
        return JsonDocument.Parse(buffer.AsMemory(0, used));
    }

    private static async Task Heartbeat(Func<object, CancellationToken, Task> write, CancellationTokenSource lifetime)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try { while (await timer.WaitForNextTickAsync(lifetime.Token)) await write(new { type = "heartbeat" }, lifetime.Token); }
        catch { lifetime.Cancel(); throw; }
    }
}

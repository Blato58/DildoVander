using System.ClientModel.Primitives;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;

#pragma warning disable OPENAI002
internal static class PersonaReliabilityTests
{
    internal static async Task RunAsync()
    {
        VerifyInstructionOverride();
        await VerifyProviderSessionIdsAsync();
        for (var cycle = 0; cycle < 100; cycle++)
        {
            await VerifyLiveStartupAndCancellationAsync();
            await VerifyRealtimeCompletionAsync();
            await VerifyRetiredInputAsync();
            await VerifyRealtimeInterruptionAsync(cycle % 2 == 0 ? 50 : 500);
        }
        await VerifyLiveStartupFailureAsync();
        await VerifyPendingUploadTeardownAsync();
        await VerifyRealtimeCancellationAsync();
        await VerifyRealtimeTransportLossAsync();
        await VerifyAmbiguousInputFailureAsync();
        Console.WriteLine("Persona reliability: 100 Live startup/cancel, 100 Realtime completion, 100 input-retirement, 100 Realtime interruption/reuse cycles and fault tests passed (fake providers only).");
    }

    private static void VerifyInstructionOverride()
    {
        var turn = Context() with { InstructionOverride = "Exact editable Lab persona instructions." };
        using var wire = JsonDocument.Parse(ModelReaderWriter.Write(RealtimeProtocol.SessionOptions(turn)).ToMemory());
        Require(wire.RootElement.GetProperty("instructions").GetString()!.StartsWith(turn.InstructionOverride, StringComparison.Ordinal),
            "Realtime uses the actual draft without impersonating a production persona version");
        foreach (var invalid in new[] { "", "  ", new string('x', 16_001) })
        {
            try
            {
                RealtimeProtocol.SessionOptions(turn with { InstructionOverride = invalid });
                throw new Exception("Invalid instruction override accepted.");
            }
            catch (InvalidDataException) { }
        }
    }

    private static async Task VerifyLiveStartupAndCancellationAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ProviderSocket();
        await using var connection = new GptLiveConnection(socket);
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Startup fixture must not select a wand."));
        var listening = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var call = Call(lifetime);
        var run = new AutonomousLiveDialogue(connection, decisions, TimeProvider.System).RunAsync(call,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            (_, _) => throw new Exception("Startup fixture must not finalize."), () => (63, 0, true),
            _ => Task.CompletedTask, lifetime.Token,
            resumeInput: async token =>
            {
                await connection.SendAudioAsync(new byte[2], token);
                listening.TrySetResult();
            });
        var start = await socket.LiveStart.Task.WaitAsync(lifetime.Token);
        Require(!listening.Task.IsCompleted && connection.InputPcmBytes == 0,
            "capture remains gated while the provider has not acknowledged session.start");
        socket.Push(new { type = "session.started", client_event_id = start, session = new { model = GptLiveProtocol.Model, id = "live_fixture-1" } });
        await listening.Task.WaitAsync(lifetime.Token);
        Require(connection.ProviderSessionId == "live_fixture-1", "verified Live startup captures the provider session id");
        Require(connection.InputPcmBytes == 2, "listening starts with a ready provider, allowing immediate PCM acknowledgement");
        lifetime.Cancel();
        await ExpectCancellationAsync(run);
    }

    private static async Task VerifyLiveStartupFailureAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ProviderSocket();
        await using var connection = new GptLiveConnection(socket);
        using var decisions = new WandDecisionService((_, _) => throw new Exception("Rejected startup must not select."));
        var listening = false;
        var run = new AutonomousLiveDialogue(connection, decisions, TimeProvider.System).RunAsync(Call(lifetime),
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
            (_, _) => throw new Exception("Rejected startup must not finalize."), () => (63, 0, true),
            _ => Task.CompletedTask, lifetime.Token,
            resumeInput: _ => { listening = true; return Task.CompletedTask; });
        await socket.LiveStart.Task.WaitAsync(lifetime.Token);
        socket.Push(new { type = "error", error = new { code = "rate_limit_exceeded", message = "private provider detail" } });
        try
        {
            await run.WaitAsync(lifetime.Token);
            throw new Exception("Provider startup rejection was accepted.");
        }
        catch (LiveProviderFailure error)
        {
            Require(error.Code == "provider_capacity" && !error.Message.Contains("private provider detail") && !listening,
                "startup capacity failures retain safe classification and never enable capture");
        }
    }

    private static async Task VerifyRetiredInputAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var old = new ProviderSocket();
        using var fresh = new ProviderSocket();
        await using var connection = new GptLiveConnection(old, _ => Task.FromResult<WebSocket>(fresh));
        var observed = 0;
        connection.ObserveInput(_ => observed++);
        using var started = JsonDocument.Parse("{\"session\":{\"id\":\"live_before-reset\"}}");
        connection.CaptureSessionId(started.RootElement);
        var waiting = connection.SendAudioAsync(new byte[2], lifetime.Token);
        await connection.RestartAsync(1, lifetime.Token);
        Require(connection.ProviderSessionId is null, "Live reconnect retires the prior provider session id");
        await waiting.WaitAsync(lifetime.Token);
        Require(observed == 0 && connection.InputPcmBytes == 0,
            "audio waiting on a revoked provider cannot contaminate the new input activity state");
        connection.AudioReady();
        await connection.SendAudioAsync(new byte[2], lifetime.Token);
        await connection.CloseAsync(lifetime.Token);
        await connection.SendAudioAsync(new byte[2], lifetime.Token);
        Require(observed == 1 && connection.InputPcmBytes == 2 && fresh.AudioFrames == 1,
            "accepted input is observed once; audio after session.close reaches neither observer nor provider");
    }

    private static async Task VerifyPendingUploadTeardownAsync()
    {
        using var socket = new ProviderSocket();
        var connection = new GptLiveConnection(socket);
        var waiting = connection.SendAudioAsync(new byte[2], CancellationToken.None);
        await connection.DisposeAsync();
        await ExpectCancellationAsync(waiting);
        Require(socket.Disposed && socket.AudioFrames == 0, "startup teardown releases uploads without needing caller cancellation");

        using var closingSocket = new ProviderSocket();
        await using var closing = new GptLiveConnection(closingSocket);
        var late = closing.SendAudioAsync(new byte[2], CancellationToken.None);
        await closing.CloseAsync(CancellationToken.None);
        await late.WaitAsync(TimeSpan.FromSeconds(5));
        Require(closingSocket.AudioFrames == 0, "close before provider readiness retires waiting audio promptly");
    }

    private static async Task VerifyRealtimeCompletionAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ProviderSocket(realtime: true);
        await using var connection = new OpenAiRealtimeConnection(socket, lifetime.Token);
        var bytes = 0;
        var reply = await connection.GenerateTurnAsync(Context(), null,
            (pcm, _) => { bytes += pcm.Length; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, lifetime.Token);
        Require(bytes == 2 && reply.Reply == "Fixture reply." && reply.Decision == "ask" && socket.Responses == 1,
            "the official Realtime SDK completes one correlated response without reconnect or replay");
        Require(connection.ProviderSessionId == "sess_fixture-1", "actual SDK session metadata exposes only the captured provider id");
        Require(connection.LastInputTokens == 1 && connection.LastOutputTokens == 1,
            "actual response usage supplies token counts without estimating from audio");
        await connection.GenerateTurnAsync(Context(), null, (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, lifetime.Token);
        Require(connection.LastInputTokens == 1 && connection.LastOutputTokens == 1,
            "per-turn token counts reset rather than double-counting connection totals");
    }

    private static async Task VerifyProviderSessionIdsAsync()
    {
        foreach (var id in new string?[] { null, "", "private provider detail", "sess_bad\nvalue", "<script>", "sess_č", new string('a', 129) })
        {
            using var socket = new ProviderSocket();
            await using var live = new GptLiveConnection(socket);
            using var started = JsonDocument.Parse(JsonSerializer.Serialize(new { session = new { id } }));
            live.CaptureSessionId(started.RootElement);
            Require(live.ProviderSessionId is null, "missing or unsafe Live session metadata cannot enter technical diagnostics");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var realtimeSocket = new ProviderSocket(realtime: true, providerSessionId: id, updatedProviderSessionId: id);
            await using var realtime = new OpenAiRealtimeConnection(realtimeSocket, lifetime.Token);
            await realtime.GenerateTurnAsync(Context(), null, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, lifetime.Token);
            Require(realtime.ProviderSessionId is null, "missing or unsafe SDK session metadata remains absent without changing socket behavior");
        }
        using var validLifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var updatedOnly = new ProviderSocket(realtime: true, providerSessionId: null, updatedProviderSessionId: "sess_updated-2");
        await using var connection = new OpenAiRealtimeConnection(updatedOnly, validLifetime.Token);
        await connection.GenerateTurnAsync(Context(), null, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, validLifetime.Token);
        Require(connection.ProviderSessionId == "sess_updated-2", "session.updated can supply the first verified safe provider id");

        using var old = new ProviderSocket(realtime: true, interruptible: true, providerSessionId: "sess_old", updatedProviderSessionId: "sess_old");
        using var fresh = new ProviderSocket(realtime: true, providerSessionId: "sess_new", updatedProviderSessionId: "sess_new");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unchanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long generation = 0;
        var resetObserved = false;
        OpenAiRealtimeConnection? reset = null;
        reset = new(old, validLifetime.Token, _ =>
        {
            resetObserved = reset!.ProviderSessionId is null;
            return Task.FromResult<WebSocket>(fresh);
        });
        await using var resetOwner = reset;
        var run = reset.GenerateTurnAsync(Context(), null, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, validLifetime.Token,
            visualRevision: () => new(generation, generation == 0 ? changed.Task : unchanged.Task));
        await old.ResponseWaiting.Task.WaitAsync(validLifetime.Token);
        Require(reset.ProviderSessionId == "sess_old", "old provider id is available before visual reconnect");
        generation = 1;
        changed.TrySetResult();
        await run.WaitAsync(validLifetime.Token);
        Require(resetObserved && reset.ProviderSessionId == "sess_new", "reconnect clears the old id before connecting and captures the fresh SDK session id");
    }

    private static async Task VerifyRealtimeCancellationAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var turn = new CancellationTokenSource();
        using var socket = new ProviderSocket();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.ReceiveCleanup = release.Task;
        await using var connection = new OpenAiRealtimeConnection(socket, lifetime.Token);
        var run = connection.GenerateTurnAsync(Context(), null,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, turn.Token);
        await socket.Receiving.Task.WaitAsync(lifetime.Token);
        turn.Cancel();
        await socket.CancelObserved.Task.WaitAsync(lifetime.Token);
        Require(!run.IsCompleted, "turn cancellation drains the in-flight SDK reader before enumerator disposal");
        release.TrySetResult();
        await ExpectCancellationAsync(run);
        Require(socket.Aborted, "cancelling a turn aborts the pending real SDK receive");
    }

    private static async Task VerifyRealtimeInterruptionAsync(int playedAudioMs)
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ProviderSocket(realtime: true, interruptible: true);
        await using var connection = new OpenAiRealtimeConnection(socket, lifetime.Token);
        Require(!await connection.InterruptResponseAsync(0, lifetime.Token), "idle cancellation sends no provider command");
        var bytes = 0;
        var run = connection.GenerateTurnAsync(Context(), null,
            (pcm, _) => { bytes += pcm.Length; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, lifetime.Token);
        await socket.ResponseWaiting.Task.WaitAsync(lifetime.Token);
        Require(await connection.InterruptResponseAsync(playedAudioMs, lifetime.Token), "active response accepts barge-in");
        try
        {
            await run.WaitAsync(lifetime.Token);
            throw new Exception("Interrupted response was published as complete.");
        }
        catch (RealtimeResponseInterruptedException error)
        {
            Require(error.CostMicroUsd > 0 && bytes == 4800, "cancelled usage remains audited and late provider PCM is suppressed");
        }
        Require(socket.CancelCommands == 1 && socket.TruncatedAtMs == Math.Min(100, playedAudioMs) && !socket.Aborted,
            "one correlated cancel truncates to confirmed heard audio and keeps the provider socket");
        Require(!await connection.InterruptResponseAsync(0, lifetime.Token), "completed interruption leaves no active response");
        var reply = await connection.GenerateTurnAsync(Context() with { Request = Context().Request with { Opening = false, Text = "Synthetic continuation." } },
            null, (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, lifetime.Token);
        Require(reply.Reply == "Fixture reply." && socket.Responses == 2 && !socket.Aborted,
            "next turn succeeds on the same SDK provider session after cancellation and truncation");
    }

    private static async Task VerifyRealtimeTransportLossAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = new ProviderSocket();
        socket.CloseInput();
        await using var connection = new OpenAiRealtimeConnection(socket, lifetime.Token);
        try
        {
            await connection.GenerateTurnAsync(Context(), null,
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, lifetime.Token);
            throw new Exception("Provider close without a response was accepted.");
        }
        catch (InvalidDataException) { }
        Require(socket.Responses == 0, "provider loss cannot fabricate completion or replay a response");
    }

    private static async Task VerifyAmbiguousInputFailureAsync()
    {
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var callLifetime = new CancellationTokenSource();
        var input = new FailingInput();
        using var session = new RealtimeLiveSession("fixture", "fixture", input, lifetime, continuous: true);
        var call = Call(callLifetime);
        session.Listen(call);
        Require(session.AttachAudioStream(), "one microphone stream attaches");
        try
        {
            await session.UploadAsync(call.Context.Request.ClientTurnId, 0, new byte[2], CancellationToken.None, streaming: true);
            throw new Exception("Failed upload was accepted.");
        }
        catch (WebSocketException) { }
        Require(lifetime.IsCancellationRequested && input.Attempts == 1,
            "ambiguous provider input ends the session and is never automatically replayed");
    }

    private static RealtimeTurnContext Context()
    {
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        return new(request, true, "", [])
        {
            LivePrompt = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions())
        };
    }

    private static RealtimeCall Call(CancellationTokenSource lifetime)
    {
        var context = Context();
        return new("fixture-operation", new("fixture", context.Request.BootId, "fixture-conversation",
            context.Request.ClientTurnId, 0, 0), context, null, lifetime);
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5));
            throw new Exception("Expected cancellation.");
        }
        catch (OperationCanceledException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }

    private sealed class FailingInput : IStreamingAudioInput
    {
        internal int Attempts { get; private set; }
        public Task SendAudioAsync(byte[] pcm, CancellationToken token)
        {
            Attempts++;
            throw new WebSocketException("Fixture transport loss after possible send.");
        }
    }

    private sealed class ProviderSocket : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource aborted = new();
        private byte[]? fragment;
        private int offset;
        private bool closeInput;
        internal TaskCompletionSource<string> LiveStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Receiving { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancelObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task? ReceiveCleanup { get; set; }
        internal TaskCompletionSource ResponseWaiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int AudioFrames { get; private set; }
        internal int Responses { get; private set; }
        internal int CancelCommands { get; private set; }
        internal int? TruncatedAtMs { get; private set; }
        internal bool Aborted { get; private set; }
        internal bool Disposed { get; private set; }
        private readonly bool realtime;
        private readonly bool interruptible;
        private readonly string? updatedProviderSessionId;

        internal ProviderSocket(bool realtime = false, bool interruptible = false,
            string? providerSessionId = "sess_fixture-1", string? updatedProviderSessionId = "sess_fixture-1")
        {
            this.realtime = realtime;
            this.interruptible = interruptible;
            this.updatedProviderSessionId = updatedProviderSessionId;
            if (realtime) { PushSession("session.created", providerSessionId); }
        }

        private void PushSession(string type, string? id)
        {
            var session = new Dictionary<string, object> { ["type"] = "realtime", ["model"] = RealtimeProtocol.Model };
            if (id is not null) { session["id"] = id; }
            Push(new { type, session });
        }

        internal void Push(object item) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(item));
        internal void CloseInput() => closeInput = true;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Disposed ? WebSocketState.Closed : Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { Aborted = true; aborted.Cancel(); }
        public override void Dispose() { Disposed = true; Abort(); }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            Receiving.TrySetResult();
            if (closeInput) { return new(0, WebSocketMessageType.Close, true); }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, aborted.Token);
            try
            {
                if (fragment is null && !incoming.Reader.TryPeek(out _) && Responses > 0) { ResponseWaiting.TrySetResult(); }
                fragment ??= await incoming.Reader.ReadAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                CancelObserved.TrySetResult();
                if (ReceiveCleanup is not null) { await ReceiveCleanup; }
                throw;
            }
            var size = Math.Min(buffer.Count, fragment.Length - offset);
            fragment.AsSpan(offset, size).CopyTo(buffer.AsSpan());
            offset += size;
            var complete = offset == fragment.Length;
            if (complete) { fragment = null; offset = 0; }
            return new(size, WebSocketMessageType.Text, complete);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer);
            var root = document.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "session.start":
                    LiveStart.TrySetResult(root.GetProperty("event_id").GetString()!);
                    break;
                case "session.input_audio.append":
                case "input_audio_buffer.append":
                    AudioFrames++;
                    break;
                case "session.update" when realtime:
                    PushSession("session.updated", updatedProviderSessionId);
                    break;
                case "response.create" when realtime:
                    Responses++;
                    Push(new { type = "response.created", response = new { id = "fixture-response" } });
                    Push(new { type = "response.output_audio.delta", response_id = "fixture-response", item_id = "fixture-assistant", content_index = 0,
                        delta = interruptible && Responses == 1 ? Convert.ToBase64String(new byte[4800]) : "AAA=" });
                    if (!interruptible || Responses != 1) { PushDone("completed"); }
                    break;
                case "response.cancel" when realtime:
                    Require(root.GetProperty("response_id").GetString() == "fixture-response", "cancel binds the actual active response id");
                    CancelCommands++;
                    Push(new { type = "response.output_audio.delta", response_id = "fixture-response", item_id = "fixture-assistant", content_index = 0, delta = "AAA=" });
                    PushDone("cancelled");
                    break;
                case "conversation.item.truncate" when realtime:
                    Require(root.GetProperty("item_id").GetString() == "fixture-assistant" && root.GetProperty("content_index").GetInt32() == 0,
                        "truncate binds the exact assistant audio item and content part");
                    TruncatedAtMs = root.GetProperty("audio_end_ms").GetInt32();
                    break;
            }
            return Task.CompletedTask;
        }

        private void PushDone(string status) => Push(new
        {
            type = "response.done", response = new
            {
                id = "fixture-response", status, output = new[]
                {
                    new { type = "message", role = "assistant", status = "completed",
                        content = new[] { new { type = "output_audio", transcript = "Fixture reply." } } }
                },
                usage = new { input_tokens = 1, output_tokens = 1, input_token_details = new { text_tokens = 1, audio_tokens = 0 } }
            }
        });
    }
}
#pragma warning restore OPENAI002

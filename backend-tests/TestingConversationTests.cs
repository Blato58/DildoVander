using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.PersonaLab;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class TestingConversationTests
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "testing-conversations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var baseline = LivePromptSnapshot.Create(PersonalityCatalog.OllivanderId, new GptLiveOptions());
            var store = new PersonaLabStore(Path.Combine(directory, "lab.sqlite"), baseline);
            var original = store.CreateSession("tester", new(store.ActiveRevisionId, GptLiveProtocol.Model,
                "first-visit", "empty", "real application", RecordContent: true));
            Check(original.Testing is null, "ordinary lab preparation cannot grant integration authority");
            var bound = store.Change("tester", original.Id, session => session with
            {
                Testing = new("test-scope", "CHARACTER-CONTEXT", "AUTHORIZED-MEMORY", [])
            });
            var context = PersonaLabRuntime.Context(bound, "What do you remember?", out _);
            Check(context.EveningContext.Contains("CHARACTER-CONTEXT") && context.EveningContext.Contains("AUTHORIZED-MEMORY"),
                "both engines receive actual authorized character and memory snapshots");
            Check(!context.InstructionOverride!.Contains("All tool outcomes are simulations") && context.InstructionOverride.Contains("explicitly virtual hardware"),
                "application tools are real while physical observations remain explicitly virtual");
            Check(context.GeneratedWands is { Count: 0 }, "empty generated candidates cannot become six permanent wand identities");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["OPENAI_API_KEY"] = "test-transport-only-not-a-credential" }).Build();
            var sockets = new List<ProviderSocket>();
            using var live = new GptLiveClient(configuration, _ =>
            {
                var socket = new ProviderSocket(); sockets.Add(socket);
                return Task.FromResult<WebSocket>(socket);
            });
            using var realtime = new OpenAiRealtimeClient(_ => throw new Exception("Unexpected engine substitution"));
            var bridge = new Bridge();
            using var runtime = new PersonaLabRuntime(store, live, realtime, testing: bridge);
            try { runtime.ReserveHardware("tester", original.Id); throw new Exception("Bound virtual session admitted physical transport"); }
            catch (LabException error) when (error.Code == "testing_hardware_is_virtual") { }
            try { runtime.ReserveScript("tester", original.Id, out _); throw new Exception("Bound test session admitted scripted suite"); }
            catch (LabException error) when (error.Code == "testing_session_script_unsupported") { }
            var result = await runtime.TurnAsync("tester", original.Id, "I repair clocks.", CancellationToken.None);
            Check(bridge.Delegations == 1 && bridge.Completed == 1 && bridge.SawUserEvidence,
                "live provider delegation uses server bridge and actual persisted guest input");
            Check(result.Turns.Any(turn => turn.Role == "assistant" && turn.Text == "ACTUAL-TRANSPORT-REPLY"),
                "only provider transcript becomes the assistant response");
            Check(sockets[0].Commentary == "REAL-APPLICATION-DECISION; physical observation simulated",
                "the real backend result is correlated back into the existing Live provider session");
            Check(result.Events.Any(entry => entry.Type == "provider_session" && entry.ProviderSessionId == "live-integration-fixture")
                && result.Events.Any(entry => entry.Type == "provider_usage_final")
                && result.Events.Any(entry => entry.Type == "tool_result" && entry.Code == "real_backend_virtual_hardware"),
                "provider handshake, final usage and actual application tool completion have separate evidence");
            var restored = new PersonaLabStore(Path.Combine(directory, "lab.sqlite"), baseline).Session("tester", original.Id);
            var voiceContext = PersonaLabRuntime.Context(restored, "", out _, liveAudio: true);
            Check(voiceContext.History.Any(turn => turn.VisitorText == "I repair clocks." && turn.Reply == "ACTUAL-TRANSPORT-REPLY")
                && voiceContext.EveningContext.Contains("AUTHORIZED-MEMORY"),
                "voice after refresh reconstructs actual persisted conversation and authorized memory");
            bridge.Reject = true;
            var before = sockets.Count;
            try { await runtime.TurnAsync("tester", original.Id, "Blocked turn", CancellationToken.None); throw new Exception("Closed test scope admitted provider work"); }
            catch (LabException error) when (error.Code == "testing_session_closed") { }
            Check(sockets.Count == before, "authoritative test rejection happens before any provider connection");
            using var unavailableBridge = new PersonaLabRuntime(store, live, realtime);
            try { await unavailableBridge.TurnAsync("tester", original.Id, "No fallback", CancellationToken.None); throw new Exception("Missing integration bridge fell back to fake lab tools"); }
            catch (LabException error) when (error.Code == "testing_runtime_unavailable") { }
            Check(sockets.Count == before, "missing bridge fails explicitly without fake successful dialogue");
            bridge.Reject = false;
            using var held = new ProviderSocket(holdStartup: true);
            using var heldLive = new GptLiveClient(configuration, _ => Task.FromResult<WebSocket>(held));
            using var cancellable = new PersonaLabRuntime(store, heldLive, realtime, testing: bridge);
            var pending = cancellable.TurnAsync("tester", original.Id, "Cancel this operation", CancellationToken.None);
            await held.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellable.Cancel("tester", original.Id);
            try { await pending; throw new Exception("Explicit cancellation reported success"); }
            catch (LabException error) when (error.Code == "lab_request_cancelled") { }
            var cancelled = store.Session("tester", original.Id);
            Check(cancelled.Status == "open" && cancellable.ActiveCount == 0
                && cancelled.Turns.Count(turn => turn.Role == "assistant") == 1,
                "cancelling an in-flight provider handshake retains the resumable session and never adds an assistant reply");
            Check(cancelled.Events.Last().Type == "request_cancelled" && cancelled.Events.Last().Code == "user_cancelled",
                "deliberate cancellation is distinct from provider timeout/failure");
            using var client = new TextClientSocket();
            var connectionsBeforeTextSocket = sockets.Count;
            var spokenText = runtime.VoiceAsync("tester", original.Id, client, _ => Task.FromResult(true),
                CancellationToken.None, textInputOnly: true);
            await client.EventAsync("ready");
            Check(sockets.Count == connectionsBeforeTextSocket, "opening text audio requests no provider work before explicit user text");
            client.Command(new { type = "text", text = "Typed and spoken." });
            var handshake = await client.EventAsync("provider_ready");
            Check(handshake.GetProperty("providerSessionId").GetString() == "live-integration-fixture",
                "typed audio distinguishes account socket readiness from actual provider handshake");
            await client.EventAsync("text_ready");
            Check(sockets.Count == connectionsBeforeTextSocket + 1 && client.Pcm.Any(bytes => bytes.SequenceEqual(new byte[] { 1, 2 })),
                "typed text returns the selected real engine's exact PCM over the maintained account socket");
            Check(sockets.Last().StartupContext!.Contains("I repair clocks.") && sockets.Last().StartupContext!.Contains("ACTUAL-TRANSPORT-REPLY"),
                "spoken typed turn reconstructs only actual durable history in the same canonical conversation");
            client.Command(new { type = "end" });
            await spokenText.WaitAsync(TimeSpan.FromSeconds(5));
            Check(runtime.ActiveCount == 0, "closing typed output disposes provider/socket execution without ending saved session");
            Console.WriteLine("Testing conversation runtime: provider transport, real tool bridge, ownership admission, durable modality continuity and evidence passed (controlled doubles; no paid calls).");
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }

    private static void Check(bool condition, string message)
    { if (!condition) { throw new Exception(message); } }

    private sealed class Bridge : ITestingConversationBridge
    {
        internal int Delegations;
        internal int Completed;
        internal bool SawUserEvidence;
        internal bool Reject;
        public Task ValidateAsync(LabSession session, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Reject) { throw new LabException(409, "testing_session_closed"); }
            Check(session.OwnerId == "tester" && session.Testing?.ScopeId == "test-scope", "bridge receives the immutable server scope");
            return Task.CompletedTask;
        }
        public async Task<string> DelegateAsync(LabSession session, CancellationToken token)
        {
            await ValidateAsync(session, token);
            Delegations++;
            SawUserEvidence = session.Turns.Any(turn => turn.Role == "user" && turn.Text == "I repair clocks.");
            return "REAL-APPLICATION-DECISION; physical observation simulated";
        }
        public Task AcceptDecisionAsync(LabSession session, string decision, int? compartment, CancellationToken token) => ValidateAsync(session, token);
        public Task CompleteTurnAsync(LabSession session, CancellationToken token)
        { Completed++; return ValidateAsync(session, token); }
    }

    private sealed class ProviderSocket(bool holdStartup = false) : WebSocket
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private byte[]? fragment;
        private int offset;
        private bool disposed;
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string? Commentary;
        internal string? StartupContext;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => disposed ? WebSocketState.Closed : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => disposed = true;
        public override void Dispose() => disposed = true;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        private void Push(object data) => incoming.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(data));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            using var document = JsonDocument.Parse(buffer);
            var value = document.RootElement;
            switch (value.GetProperty("type").GetString())
            {
                case "session.start":
                    StartupContext = value.GetProperty("session").GetProperty("input").GetRawText();
                    Started.TrySetResult();
                    if (holdStartup) { break; }
                    Push(new { type = "session.started", client_event_id = value.GetProperty("event_id").GetString(), session = new { model = GptLiveProtocol.Model, id = "live-integration-fixture" } });
                    Push(new { type = "session.delegation.created", delegation = new { id = "real-delegation", target = "client" } });
                    break;
                case "session.commentary.append":
                    Check(value.GetProperty("delegation_id").GetString() == "real-delegation", "delegation id correlation");
                    Commentary = value.GetProperty("content").GetString();
                    Push(new { type = "session.output_audio.delta", delta = "AQI=" });
                    Push(new { type = "session.output_transcript.delta", delta = "ACTUAL-TRANSPORT-REPLY" });
                    break;
                case "session.close":
                    Push(new { type = "session.closed", client_event_id = value.GetProperty("event_id").GetString(), reason = "close_requested", usage = new { seconds = 1.2 } });
                    break;
            }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            fragment ??= await incoming.Reader.ReadAsync(token);
            var count = Math.Min(buffer.Count, fragment.Length - offset);
            fragment.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var complete = offset == fragment.Length;
            if (complete) { fragment = null; offset = 0; }
            return new(count, WebSocketMessageType.Text, complete);
        }
    }

    private sealed class TextClientSocket : WebSocket
    {
        private readonly Channel<byte[]> commands = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<JsonElement> events = Channel.CreateUnbounded<JsonElement>();
        internal List<byte[]> Pcm { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        internal void Command(object command) => commands.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(command));
        internal async Task<JsonElement> EventAsync(string type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var value = await events.Reader.ReadAsync(timeout.Token);
                if (value.GetProperty("type").GetString() == "error") { throw new Exception(value.GetRawText()); }
                if (value.GetProperty("type").GetString() == type) { return value; }
            }
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            if (type == WebSocketMessageType.Binary) { Pcm.Add(buffer.ToArray()); }
            else { using var json = JsonDocument.Parse(buffer); events.Writer.TryWrite(json.RootElement.Clone()); }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var command = await commands.Reader.ReadAsync(token);
            command.CopyTo(buffer.AsSpan());
            return new(command.Length, WebSocketMessageType.Text, true);
        }
    }
}

using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.Usage;
using Microsoft.Extensions.Configuration;

internal static class GptLiveTests
{
    internal static async Task RunAsync()
    {
        var defaults = CostLimits.Read(new ConfigurationBuilder().Build());
        Check(defaults.Enabled && defaults.SessionMicroUsd == 3_000_000
            && defaults.DailyMicroUsd == 0 && defaults.MonthlyMicroUsd == 0, "3 USD session-only default");
        Check(GptLiveProtocol.VoiceCost(60) == 50_000 && GptLiveProtocol.VoiceCost(0.1) == 84,
            "duration billing uses fractional seconds without rounding to minutes");
        foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, -1, 191 })
        {
            try { GptLiveProtocol.VoiceCost(seconds); throw new Exception("Invalid duration accepted"); }
            catch (InvalidDataException) { }
        }
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var turn = new RealtimeTurnContext(request, true, "private old archive", []);
        using var socket = new LiveSocket();
        await using var connection = new GptLiveConnection(socket);
        var calls = 0;
        using var dialog = new TextDialogRunner((text, mask, history, question, _) =>
        {
            calls++;
            Check(text == "Odpověď " + calls && mask == 63 && question == (calls < 4),
                "delegations use current transcript and retain the four-question gate");
            Check(history.Count > 0 && history[^1].Reply == (calls == 1 ? "Ahoj, co odkládáš?" : "Skutečně vyslovená replika"),
                "Sol follows the actual spoken question, including the opening");
            return Task.FromResult(new TextDialogResponse("Replika " + calls,
                calls == 4 ? "propose" : "ask", "", calls == 4 ? 2 : null,
                ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        var audio = 0;
        var pauses = 0;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcome = await new GptLiveDialogue(connection, dialog).RunAsync(turn,
            (pcm, _) => { audio += pcm.Length; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, _ => { pauses++; return Task.CompletedTask; }, deadline.Token);
        Check(calls == 4 && outcome.Questions == 4 && outcome.Reply.Compartment == 2,
            "terminal delegated selection survives finalized voice session");
        Check(outcome.Reply.CostMicroUsd == 51_400,
            "cumulative duration is charged once, plus all four Sol calls");
        Check(audio > 0 && pauses > 0 && outcome.Transcript.Any(t => t.Reply == "Skutečně vyslovená replika"),
            "audio and actual frontend transcript are retained independently of backend draft");
        using var start = JsonDocument.Parse(socket.Sent[0]);
        Check(start.RootElement.GetProperty("session").GetProperty("model").GetString() == "gpt-live-1"
            && start.RootElement.GetProperty("session").GetProperty("input").GetArrayLength() == 0
            && !socket.Sent[0].Contains("private old archive"), "fresh Live opening has no fabricated visitor or archive");
        Check(socket.Sent.Any(s => s.Contains("session.close")), "finalization requests explicit provider close");
        var sentBeforeLateInput = socket.Sent.Count;
        await connection.SendAudioAsync(new byte[960], deadline.Token);
        Check(socket.Sent.Count == sentBeforeLateInput, "late microphone input never follows provider close");

        using var interruptedSocket = new LiveSocket(interruptFinal: true);
        await using var interruptedConnection = new GptLiveConnection(interruptedSocket);
        var interruptedCalls = 0;
        using var interruptedDialog = new TextDialogRunner((text, mask, history, question, _) =>
        {
            interruptedCalls++;
            return Task.FromResult(new TextDialogResponse("Závěr", interruptedCalls < 4 ? "ask" : "propose",
                "", interruptedCalls < 4 ? null : 2, ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        using var interruptedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await new GptLiveDialogue(interruptedConnection, interruptedDialog).RunAsync(turn,
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask,
                interruptedDeadline.Token);
            throw new Exception("Interrupted final selection was published");
        }
        catch (OperationCanceledException) { }
        Check(!interruptedSocket.Sent.Any(s => s.Contains("session.close")),
            "visitor correction invalidates terminal selection before close");

        using var stalled = new LiveSocket(stall: true);
        await using var stalledConnection = new GptLiveConnection(stalled);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(80));
        try
        {
            await new GptLiveDialogue(stalledConnection, dialog).RunAsync(turn,
                (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, stop.Token);
            throw new Exception("Stop did not cancel Live startup");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("GPT-Live wire, delegation, duration, opening and cancellation tests passed.");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private sealed class LiveSocket(bool stall = false, bool interruptFinal = false) : WebSocket
    {
        private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
        private int answer;
        internal List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => events.Writer.TryComplete();
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        private void Emit(object value) => events.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        private void NextAnswer()
        {
            answer++;
            Emit(new { type = "session.input_transcript.delta", delta = "Odpověď " + answer, start_ms = answer * 1000, end_ms = answer * 1000 + 500 });
            Emit(new { type = "session.delegation.created", delegation = new { id = "task-" + answer, target = "client" } });
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            var text = System.Text.Encoding.UTF8.GetString(buffer);
            Sent.Add(text);
            if (stall) return Task.CompletedTask;
            using var parsed = JsonDocument.Parse(text);
            var root = parsed.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "session.start": Emit(new { type = "session.started", session = new { model = "gpt-live-1" } }); break;
                case "session.instructions.append":
                    if (root.GetProperty("content").GetString()!.StartsWith("Začni teď"))
                    {
                        Emit(new { type = "session.output_transcript.delta", delta = "Ahoj, co odkládáš?" });
                        Emit(new { type = "session.usage.updated", usage = new { seconds = 12 } });
                        NextAnswer();
                    }
                    break;
                case "session.commentary.append":
                    Emit(new { type = "session.output_transcript.delta", delta = "Skutečně vyslo" });
                    Emit(new { type = "session.output_transcript.delta", delta = "vená replika" });
                    Emit(new { type = "session.output_audio.delta", delta = Convert.ToBase64String(new byte[] { 0, 32, 0, 32 }) });
                    if (answer < 4) NextAnswer();
                    else if (interruptFinal) Emit(new { type = "session.input_transcript.delta", delta = "Oprava, jinak." });
                    break;
                case "session.close": Emit(new { type = "session.closed", usage = new { seconds = 60 }, reason = "close_requested" }); break;
            }
            return Task.CompletedTask;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            var next = await events.Reader.ReadAsync(token);
            next.AsSpan().CopyTo(buffer.AsSpan());
            return new(next.Length, WebSocketMessageType.Text, true);
        }
    }
}

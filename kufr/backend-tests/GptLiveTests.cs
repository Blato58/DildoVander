using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class GptLiveTests
{
    internal static async Task RunAsync()
    {
        var keyOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "fixture-key",
            ["Backend:Usage:SessionCapMicroUsd"] = "invalid-ignored",
            ["Backend:Usage:DailyCapMicroUsd"] = "1",
            ["Backend:Usage:MonthlyCapMicroUsd"] = "1"
        }).Build();
        using var available = new GptLiveClient(keyOnly);
        Check(available.Available, "provider key enables Live; legacy monetary caps are ignored");
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
        using (var session = JsonDocument.Parse(JsonSerializer.Serialize(GptLiveProtocol.Start(turn))))
        {
            var instructions = session.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
            Check(instructions.StartsWith(PartyDialoguePrompt.VoicePersona + "\n\n", StringComparison.Ordinal)
                && instructions.Contains("Backchannel policy:") && instructions.Contains("Interruption policy:"),
                "Live shares the Czech voice persona and silence/interruption rules");
            Check(instructions.Contains("Delegation policy:") && instructions.Contains("Hůlku nikdy nevybírej sám")
                && !instructions.Contains("propose_wand"),
                "Live delegates selection without inheriting the decision model's tool instructions");
        }
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
        var inputStopped = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcome = await new GptLiveDialogue(connection, dialog).RunAsync(turn,
            (pcm, _) => { audio += pcm.Length; return Task.CompletedTask; },
            (_, _) => Task.CompletedTask, _ => { pauses++; return Task.CompletedTask; }, deadline.Token,
            _ =>
            {
                Check(!socket.Sent.Any(s => s.Contains("session.close")), "input gate closes before provider usage wait");
                inputStopped = true;
                return Task.CompletedTask;
            });
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
        Check(inputStopped, "terminal microphone is stopped before provider close");
        var finalCommentary = socket.Sent.FindLastIndex(s => s.Contains("session.commentary.append"));
        Check(!socket.Sent.Skip(finalCommentary + 1).Any(s => s.Contains("session.instructions.append")),
            "a terminal instruction cannot interrupt commentary that just started speaking");
        var sentBeforeLateInput = socket.Sent.Count;
        await connection.SendAudioAsync(new byte[960], deadline.Token);
        Check(socket.Sent.Count == sentBeforeLateInput, "late microphone input never follows provider close");

        using var shortSocket = new LiveSocket(answers: 2);
        await using var shortConnection = new GptLiveConnection(shortSocket);
        var shortCalls = 0;
        using var shortDialog = new TextDialogRunner((text, mask, history, question, token, memory, personality) =>
        {
            shortCalls++;
            Check(personality == PersonalityCatalog.OllivanderId && !question,
                "Live delegates the selected personality and permits early selection");
            return Task.FromResult(new TextDialogResponse("Krátká replika", shortCalls == 2 ? "propose" : "ask",
                "", shortCalls == 2 ? 2 : null, ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        using var shortDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var shortOutcome = await new GptLiveDialogue(shortConnection, shortDialog).RunAsync(
            turn with { PersonalityId = PersonalityCatalog.OllivanderId }, (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, _ => Task.CompletedTask, shortDeadline.Token);
        Check(shortCalls == 2 && shortOutcome.Questions == 2 && shortOutcome.Reply.Compartment == 2,
            "new Live personality completes selection after two answers instead of the original four");

        using var interruptedSocket = new LiveSocket(interruptFinal: true);
        await using var interruptedConnection = new GptLiveConnection(interruptedSocket);
        var interruptedCalls = 0;
        using var interruptedDialog = new TextDialogRunner((text, mask, history, question, _) =>
        {
            interruptedCalls++;
            if (interruptedCalls == 5) Check(text == "Oprava, jinak.", "terminal correction reaches Sol without another delegation");
            return Task.FromResult(new TextDialogResponse("Závěr", interruptedCalls == 4 ? "propose" : "ask",
                "", interruptedCalls == 4 ? 2 : null, ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        using var interruptedDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
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
        Check(interruptedCalls == 5, "terminal correction is processed automatically");

        await VerifyDelegationOrderingAsync(turn);
        await VerifyChangedInputAsync(turn);
        await VerifyFastReplyDuringSpeechAsync(turn);

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

    private static async Task VerifyDelegationOrderingAsync(RealtimeTurnContext turn)
    {
        using var socket = new LiveSocket(delegationFirst: true);
        await using var connection = new GptLiveConnection(socket);
        var calls = 0;
        using var dialog = new TextDialogRunner((text, mask, history, question, _) =>
        {
            calls++;
            Check(text == "Odpověď " + calls, "early delegation is retained until its transcript arrives");
            return Task.FromResult(new TextDialogResponse("Replika", calls == 4 ? "propose" : "ask",
                "", calls == 4 ? 2 : null, ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcome = await new GptLiveDialogue(connection, dialog).RunAsync(turn,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, deadline.Token);
        Check(calls == 4 && outcome.Reply.Compartment == 2, "delegation-before-transcript finishes without model retry");
        Check(!socket.Sent.Any(s => s.Contains("session.thinking.append")), "ordering recovery sends no waiting notices");
    }

    private static async Task VerifyChangedInputAsync(RealtimeTurnContext turn)
    {
        using var socket = new LiveSocket();
        await using var connection = new GptLiveConnection(socket);
        var oldResult = new TaskCompletionSource<TextDialogResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new List<string>();
        var sawCorrection = false;
        using var dialog = new TextDialogRunner((text, mask, history, question, _) =>
        {
            inputs.Add(text);
            if (inputs.Count == 1)
            {
                socket.EmitDelegation("duplicate-same-revision");
                socket.EmitInput(" a oprava"); // no new delegation for this corrected revision
                return oldResult.Task;
            }
            Check(inputs.Count != 2 || text == "Odpověď 1 a oprava", "latest accumulated transcript replaces stale input");
            Check(question == (text != "Odpověď 4"), "stale work does not advance the question gate");
            return Task.FromResult(new TextDialogResponse("Aktuální replika", text == "Odpověď 4" ? "propose" : "ask",
                "", text == "Odpověď 4" ? 2 : null, ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var outcome = await new GptLiveDialogue(connection, dialog).RunAsync(turn,
            (_, _) => Task.CompletedTask,
            (fragment, _) =>
            {
                if (fragment == " a oprava")
                {
                    sawCorrection = true;
                    oldResult.SetResult(new("ZASTARALÝ VÝBĚR", "propose", "", 6,
                        ResponsesTextDialogBudget.Model, 100, 10, "default"));
                }
                return Task.CompletedTask;
            }, _ => Task.CompletedTask, deadline.Token);
        Check(sawCorrection && inputs.Count == 5 && outcome.Questions == 4 && outcome.Reply.Compartment == 2,
            "corrected work resumes automatically and duplicate work is suppressed");
        Check(outcome.Reply.CostMicroUsd == 51_750, "discarded result remains charged exactly once");
        Check(!socket.Sent.Any(s => s.Contains("ZASTARALÝ") || s.Contains("session.thinking.append")),
            "stale choice and waiting notices never reach the voice model");
    }

    private static async Task VerifyFastReplyDuringSpeechAsync(RealtimeTurnContext turn)
    {
        using var socket = new LiveSocket(splitAnswer: true);
        await using var connection = new GptLiveConnection(socket);
        var calls = 0;
        using var dialog = new TextDialogRunner((text, mask, history, question, _) =>
        {
            calls++;
            var partial = text == "Odpověď ";
            return Task.FromResult(new TextDialogResponse(partial ? "PŘEDČASNÁ REPLIKA" : "Celá odpověď",
                !partial && text == "Odpověď 4" ? "propose" : "ask", "",
                !partial && text == "Odpověď 4" ? 2 : null,
                ResponsesTextDialogBudget.Model, 100, 10, "default"));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        var outcome = await new GptLiveDialogue(connection, dialog).RunAsync(turn,
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, _ => Task.CompletedTask, deadline.Token);
        Check(calls == 8 && outcome.Reply.Compartment == 2 && outcome.Questions == 4,
            "fast replies wait for delayed speech fragments without advancing the question gate");
        Check(!socket.Sent.Any(s => s.Contains("PŘEDČASNÁ")), "partial-answer reply never interrupts continuing speech");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }

    private sealed class LiveSocket(bool stall = false, bool interruptFinal = false, bool delegationFirst = false, bool splitAnswer = false, int answers = 4) : WebSocket
    {
        private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
        private int answer;
        private bool interrupted;
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
        internal void EmitInput(string text) => Emit(new { type = "session.input_transcript.delta", delta = text });
        internal void EmitDelegation(string id) => Emit(new { type = "session.delegation.created", delegation = new { id, target = "client" } });
        private void NextAnswer()
        {
            answer++;
            if (delegationFirst) EmitDelegation("task-" + answer);
            EmitInput("Odpověď " + (splitAnswer ? "" : answer));
            if (!delegationFirst) EmitDelegation("task-" + answer);
            if (splitAnswer)
            {
                var suffix = answer.ToString();
                _ = Task.Run(async () => { await Task.Delay(350); EmitInput(suffix); });
            }
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
                    if (answer < answers) NextAnswer();
                    else if (interruptFinal && !interrupted)
                    {
                        interrupted = true;
                        EmitInput("Oprava, jinak.");
                    }
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

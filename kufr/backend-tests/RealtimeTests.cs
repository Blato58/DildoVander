using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.ClientModel.Primitives;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Realtime;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using OpenAI.Realtime;

#pragma warning disable OPENAI002
internal static class RealtimeTests
{
    internal static async Task RunAsync()
    {
        VerifyResampling();
        await VerifyProgressiveAudioAsync();
        await VerifyToolValidationAsync();
        VerifyAdmissionAndMemory();
        Console.WriteLine("Realtime stream, selection, resampling and journal tests passed.");
    }

    private static void VerifyResampling()
    {
        var low = RealtimeAudio.FromDeviceWav(Wav(1000));
        var high = RealtimeAudio.FromDeviceWav(Wav(14000));
        Require(low.Length == 48000 && high.Length == low.Length, "32 -> 24 kHz duration is preserved");
        Require(Rms(low) > 7000 && Rms(high) < 500, "downsampling keeps speech-band tone and rejects aliasing");
        Require(Throws(() => RealtimeAudio.FromDeviceWav(new byte[44])), "invalid WAV rejected before dispatch");
        var encoded = JsonSerializer.Serialize(new
        {
            type = "audio",
            delta = Convert.ToBase64String(new byte[RealtimeProtocol.MaxChunkBytes])
        });
        Require(Encoding.UTF8.GetByteCount(encoded) < 65536, "largest audio envelope fits native line cap");
    }

    private static async Task VerifyProgressiveAudioAsync()
    {
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heard = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var socket = new ScriptSocket(
            [Created(), Updated(), ResponseCreated(), Audio(new byte[4800]), Done("Jak ti říkají?")],
            pauseAt: 4, released.Task);
        using var provider = new OpenAiRealtimeClient(_ => Task.FromResult<WebSocket>(socket));
        var request = Request();
        var pending = provider.GenerateAsync(new(request, true, "", []), null,
            (pcm, _) =>
            {
                Require(pcm.Length == 4800, "PCM arrives with expected samples");
                heard.TrySetResult();
                return Task.CompletedTask;
            }, (_, _) => throw new Exception("Opening must not fabricate a visitor."), CancellationToken.None);
        await heard.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(!pending.IsCompleted, "first audio is relayed before response completion");
        released.TrySetResult();
        var result = await pending;
        Require(result.Reply == "Jak ti říkají?" && result.Decision == "ask" && result.VisitorText == "",
            "opening stores actual answer and no invented visitor");
        Require(socket.Sent.Any(value => value.Contains("\"turn_detection\":null", StringComparison.Ordinal)),
            "PTT does not wait for a VAD timeout");
        using var update = JsonDocument.Parse(socket.Sent[0]);
        Require(!update.RootElement.GetProperty("session").TryGetProperty("model", out _),
            "the immutable provider model is selected by connection URL, not session.update");
        Require(result.CostMicroUsd == 13292, "usage is conservatively settled by modality");

        using var bad = new ScriptSocket([Created(), Updated(), ResponseCreated(), Audio(new byte[1])]);
        using var badProvider = new OpenAiRealtimeClient(_ => Task.FromResult<WebSocket>(bad));
        await ExpectFailureAsync(() => badProvider.GenerateAsync(new(request, true, "", []), null,
            (_, _) => throw new Exception("Odd PCM reached the phone."), (_, _) => Task.CompletedTask,
            CancellationToken.None));

        using var lateTranscript = new ScriptSocket([
            Created(), Updated(), ResponseCreated(), Audio(new byte[4800]), Done("Kdo tě prásknul?"),
            JsonSerializer.Serialize(new { type = "conversation.item.input_audio_transcription.completed", transcript = "Byl to Petr." })
        ]);
        using var voiceProvider = new OpenAiRealtimeClient(_ => Task.FromResult<WebSocket>(lateTranscript));
        var spoken = await voiceProvider.GenerateAsync(
            new(request with { Opening = false, Wav = "opaque-input-marker" }, true, "", []), new byte[4800],
            (_, _) => Task.CompletedTask, (_, _) => Task.CompletedTask, CancellationToken.None);
        Require(spoken.VisitorText == "Byl to Petr.", "late parallel transcript is retained without delaying first audio");
    }

    private static async Task VerifyToolValidationAsync()
    {
        var request = Request() with { Opening = false, Text = "Už vyber.", ReadyMask = 2 };
        var tool = new
        {
            type = "function_call",
            name = "propose_wand",
            call_id = "call-test",
            arguments = "{\"compartment\":2,\"reason\":\"Jeho výmluva\"}"
        };
        var toolItem = ModelReaderWriter.Read<RealtimeFunctionCallItem>(BinaryData.FromString(JsonSerializer.Serialize(tool)))!;
        var context = new RealtimeTurnContext(request, false, "", []);
        Require(OpenAiRealtimeClient.ValidateTool(toolItem, context).Compartment == 2,
            "explicit available compartment is accepted");
        Require(Throws(() => OpenAiRealtimeClient.ValidateTool(toolItem, context with { RequireQuestion = true })),
            "early proposal is rejected independently of the tool schema");
        Require(Throws(() => OpenAiRealtimeClient.ValidateTool(toolItem,
            context with { Request = request with { ReadyMask = 1 } })), "unavailable proposal is rejected");
        using var socket = new ScriptSocket([
            Created(), Updated(), ResponseCreated(), Done("", tool), ResponseCreated("response-2"),
            Audio(new byte[4800], "response-2"), Done("Dvojka ti sedí. Ta výmluva už má vlastní život.", id: "response-2")
        ]);
        using var provider = new OpenAiRealtimeClient(_ => Task.FromResult<WebSocket>(socket));
        var result = await provider.GenerateAsync(context, null, (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Require(result.Decision == "propose" && result.Compartment == 2, "one validated tool then spoken conclusion");
        Require(socket.Sent.Any(value => value.Contains("\\\"accepted\\\":true", StringComparison.Ordinal))
            && socket.Sent.Any(value => value.Contains("\"tool_choice\":\"none\"", StringComparison.Ordinal)),
            "model gets validated result and cannot recursively call tools");
    }

    private static void VerifyAdmissionAndMemory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-realtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Backend:Usage:SessionCapMicroUsd"] = "2000000",
                ["Backend:Usage:DailyCapMicroUsd"] = "20000000",
                ["Backend:Usage:MonthlyCapMicroUsd"] = "20000000"
            }).Build();
            using var journal = new OperationJournal(configuration, TimeProvider.System);
            using var dialog = new TextDialogRunner((_, _, _, _, _) => throw new Exception("Legacy Sol must not run."));
            using var voice = new VoiceProviderRunner((_, _) => throw new Exception("Legacy STT must not run."),
                (_, _) => throw new Exception("Legacy TTS must not run."));
            var memory = new EveningMemoryStore(configuration, journal);
            var store = new ConversationStore(TimeProvider.System, journal, dialog, eveningMemory: memory, voiceRunner: voice);
            var request = Request();
            var created = store.Create("device-a", Guid.NewGuid(), "create"u8,
                new("device-a", request.BootId, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false)));
            var conversation = created.Response!.ConversationId;
            var body = JsonSerializer.SerializeToUtf8Bytes(request);
            var key = Guid.NewGuid();
            var limitedConfiguration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Backend:DatabasePath"] = Path.Combine(directory, "limited.sqlite"),
                    ["Backend:Usage:SessionCapMicroUsd"] = "200000",
                    ["Backend:Usage:DailyCapMicroUsd"] = "20000000",
                    ["Backend:Usage:MonthlyCapMicroUsd"] = "20000000"
                })
                .Build();
            using (var limitedJournal = new OperationJournal(limitedConfiguration, TimeProvider.System))
            {
                var limitedStore = new ConversationStore(
                    TimeProvider.System, limitedJournal, dialog, voiceRunner: voice);
                var limitedConversation = limitedStore.Create(
                    "device-a", Guid.NewGuid(), "limited-create"u8,
                    new("device-a", request.BootId, 0, PartyWandCatalog.Version, "cs-CZ", new(true, false)));
                var rejected = limitedStore.BeginRealtimeTurn(
                    "device-a", limitedConversation.Response!.ConversationId, key,
                    body, request, true, default, default);
                Require(rejected.Status == 429 && rejected.Code == "cost_limit"
                    && rejected.Call is null && limitedStore.ActiveProviderCallCount == 0,
                    "a low session cap rejects Realtime before provider dispatch");
            }
            Require(store.BeginRealtimeTurn("device-b", conversation, key, body, request, true, default, default).Status == 403,
                "device scope enforced");
            Require(store.BeginRealtimeTurn("device-a", conversation, key, body, request, false, default, default).Status == 409,
                "provider admission fails before reservation");
            var admission = store.BeginRealtimeTurn("device-a", conversation, key, body, request, true, default, default);
            var call = admission.Call!;
            Require(call is not null && store.StartRealtimeTurn(call), "turn journal reserved before provider starts");
            Require(store.BeginRealtimeTurn("device-a", conversation, key, body, request, true, default, default).Code
                == "realtime_stream_not_replayable", "retry cannot regenerate paid sound");
            Require(store.ActiveProviderCallCount == 1, "Realtime shares the global paid slot");
            var result = store.CompleteRealtimeTurn(call!, new("", "Jak ti říkají?", "ask", "", null, 1000));
            Require(result is { Decision: "ask" } && memory.GetStatus("device-a").ExchangeCount == 1,
                "completed response persists in evening memory");
            Require(store.GetOperation("device-a", call!.OperationId).Operation?.Status == "completed",
                "completed stream remains pollable without a provider replay");
            store.ReleaseRealtimeTurn(call, true, true);
            var nextRequest = request with { Opening = false, Text = "Petr.", ClientTurnId = Guid.NewGuid() };
            using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = configuration["Backend:DatabasePath"] + ".evening.sqlite",
                Pooling = false
            }.ToString()))
            {
                database.Open();
                using var fault = database.CreateCommand();
                fault.CommandText = "CREATE TRIGGER reject_exchange BEFORE INSERT ON exchanges BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END";
                fault.ExecuteNonQuery();
                var failed = store.BeginRealtimeTurn(
                    "device-a", conversation, Guid.NewGuid(), "fail-memory"u8.ToArray(),
                    nextRequest, true, default, default).Call!;
                Require(store.StartRealtimeTurn(failed), "fault-injected turn starts normally");
                try
                {
                    store.CompleteRealtimeTurn(failed, new("Petr.", "Kdo tě práskne?", "ask", "", null, 1000));
                    throw new Exception("A failed memory write was published.");
                }
                catch (SqliteException)
                {
                    store.ReleaseRealtimeTurn(failed, true, false);
                }
                Require(store.GetOperation("device-a", failed.OperationId).Operation?.Status == "failed"
                    && memory.GetStatus("device-a").ExchangeCount == 1,
                    "memory failure cannot leave a completed journal entry without a result");
                fault.CommandText = "DROP TRIGGER reject_exchange";
                fault.ExecuteNonQuery();
            }
            nextRequest = nextRequest with { ClientTurnId = Guid.NewGuid() };
            var next = store.BeginRealtimeTurn("device-a", conversation, Guid.NewGuid(), "next"u8.ToArray(),
                nextRequest, true, default, default).Call!;
            Require(next.Context.EveningContext.Contains("Jak ti říkají?", StringComparison.Ordinal),
                "next turn restores context from backend memory");
            store.EndConversation("device-a", conversation, Guid.NewGuid());
            Require(next.Cancellation.IsCancellationRequested
                && store.CompleteRealtimeTurn(next, new("Petr.", "Kdo tě práskne?", "ask", "", null, 1000)) is null,
                "Stop prevents late publication and append");
            store.ReleaseRealtimeTurn(next, true, false);
            Require(memory.GetStatus("device-a").ExchangeCount == 1 && store.ActiveProviderCallCount == 0,
                "cancelled turn adds no memory and releases the slot");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static RealtimeTurnRequest Request() =>
        new(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0, PartyWandCatalog.Version, true, "", null, "marin", 1);
    private static string Created() => JsonSerializer.Serialize(new
    {
        type = "session.created",
        session = new { type = "realtime", model = RealtimeProtocol.Model }
    });
    private static string Updated() => JsonSerializer.Serialize(new
    {
        type = "session.updated",
        session = new { type = "realtime", model = RealtimeProtocol.Model }
    });
    private static string ResponseCreated(string id = "response-1") => JsonSerializer.Serialize(new
    {
        type = "response.created",
        response = new { id }
    });
    private static string Audio(byte[] pcm, string id = "response-1") => JsonSerializer.Serialize(new
    {
        type = "response.output_audio.delta",
        response_id = id,
        delta = Convert.ToBase64String(pcm)
    });
    private static string Done(string text, object? tool = null, string id = "response-1") => JsonSerializer.Serialize(new
    {
        type = "response.done",
        response = new
        {
            id,
            status = "completed",
            output = tool is null
                ? new object[]
                {
                    new
                    {
                        type = "message",
                        role = "assistant",
                        status = "completed",
                        content = new[] { new { type = "output_audio", transcript = text } }
                    }
                }
                : [tool],
            usage = new
            {
                input_tokens = 123,
                output_tokens = 200,
                input_token_details = new { text_tokens = 123, audio_tokens = 0 }
            }
        }
    });
    private static byte[] Wav(int frequency)
    {
        var wav = new byte[64044];
        "RIFF"u8.CopyTo(wav);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 32000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 64000);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), 64000);
        for (var index = 0; index < 32000; index++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + index * 2),
                (short)(12000 * Math.Sin(2 * Math.PI * frequency * index / 32000)));
        }
        return wav;
    }
    private static double Rms(byte[] pcm) => Math.Sqrt(
        Enumerable.Range(100, pcm.Length / 2 - 200)
            .Select(index => Math.Pow(BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index * 2)), 2))
            .Average());
    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
    private static async Task ExpectFailureAsync(Func<Task<RealtimeReply>> action)
    {
        try
        {
            await action();
            throw new Exception("Invalid stream was accepted.");
        }
        catch (InvalidDataException)
        {
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }
    private sealed class ScriptSocket(string[] events, int pauseAt = -1, Task? released = null) : WebSocket
    {
        private int index;
        private int fragmentOffset;
        public List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token)
        {
            if (index == pauseAt)
            {
                await released!.WaitAsync(token);
            }
            if (index >= events.Length)
            {
                throw new InvalidDataException("Script ended.");
            }
            var data = Encoding.UTF8.GetBytes(events[index]);
            var count = Math.Min(buffer.Count, data.Length - fragmentOffset);
            data.AsSpan(fragmentOffset, count).CopyTo(buffer.AsSpan());
            fragmentOffset += count;
            var complete = fragmentOffset == data.Length;
            if (complete)
            {
                index++;
                fragmentOffset = 0;
            }
            return new(count, WebSocketMessageType.Text, complete);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }
}
#pragma warning restore OPENAI002

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Character;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Realtime;
using Microsoft.Extensions.Configuration;

internal static class GptLiveTests
{
    internal static async Task RunAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "fixture-key",
            ["Backend:GptLive:ActorMode"] = "LegacyBackendScripted"
        }).Build();
        using var provider = new GptLiveClient(configuration);
        Check(provider.Available, "provider credential enables Live readiness");
        var options = GptLiveOptions.Read(configuration);
        Check(options.Mode == LiveActorMode.AutonomousActor, "retired actor setting migrates to retained actor");
        Check(Enum.GetValues<LiveActorMode>().Length == 1, "only one production Live implementation exists");
        Check(GptLiveProtocol.VoiceCost(60) == 50_000 && GptLiveProtocol.VoiceCost(0.1) == 84,
            "duration billing retains fractional seconds");
        foreach (var seconds in new[] { double.NaN, double.PositiveInfinity, -1, 191 })
        {
            try { GptLiveProtocol.VoiceCost(seconds); throw new Exception("Invalid duration accepted"); }
            catch (InvalidDataException) { }
        }
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var turn = new RealtimeTurnContext(request, true, "", []) { CostumeContext = "green cloak" };
        using (var start = JsonDocument.Parse(JsonSerializer.Serialize(GptLiveProtocol.Start(turn))))
        {
            Check(start.RootElement.GetProperty("session").GetProperty("model").GetString() == "gpt-live-1",
                "startup identifies the exact requested engine");
            var instructions = start.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
            Check(instructions.Contains("green cloak") && instructions.Contains("Běžné repliky tvoříš sám"),
                "retained Live actor receives visual context and authors ordinary conversation");
        }
        VerifyCustomerPreferences(options, request);
        AutonomousLiveTests.VerifyLiveSpeedAdmission();
        VerifyAudioFrames();
        await VerifyOrderedAudioAsync();
        await VerifyResetBeforeReadyAsync();
        await VerifyAttachedStreamAsync();
        Console.WriteLine("Live production selection, startup and persistent audio frame tests passed.");
    }

    private static void VerifyCustomerPreferences(GptLiveOptions options, RealtimeTurnRequest request)
    {
        foreach (var (tone, expectedTone) in new[]
        {
            ("gentle", CharacterTone.Reserved),
            ("cheeky", CharacterTone.Dry),
            ("roast", CharacterTone.Adult)
        })
        {
            var preferences = new CustomerConversationPreferenceSnapshot(1, 1, "en-US", tone);
            var prompt = LivePromptSnapshot.Create(PersonalityCatalog.DefaultId, options)
                .WithConversationPreferences(preferences, "en-US");
            Check(prompt.ConversationLanguage == "en-US" && prompt.Options.Character.HostTone == expectedTone
                && prompt.Instructions.Contains("Conduct this conversation in English.", StringComparison.Ordinal),
                $"Live freezes {tone} ceiling and English instructions into its prompt snapshot");

            var toneState = new ConversationGuestToneState(options.Character, preferences);
            var binding = new GuestToneBinding(toneState.Read, toneState.Apply, toneState.SetOperatorTone);
            var turn = new RealtimeTurnContext(request, true, "", [])
            {
                LivePrompt = prompt,
                ConversationLanguage = "en-US",
                GuestTone = binding
            };
            using var start = JsonDocument.Parse(JsonSerializer.Serialize(GptLiveProtocol.Start(turn)));
            var instructions = start.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;
            Check(instructions.Contains("Effective frozen host/guest ceiling", StringComparison.Ordinal)
                && instructions.Contains("Use a gentle", StringComparison.Ordinal) == (tone == "gentle"),
                $"Live sends the trusted {tone} guest policy with its startup request");
        }
    }

    private static async Task VerifyResetBeforeReadyAsync()
    {
        using var first = new AudioSocket(); using var second = new AudioSocket(); using var third = new AudioSocket();
        var sockets = new Queue<WebSocket>([second, third]);
        await using var connection = new GptLiveConnection(first, _ => Task.FromResult(sockets.Dequeue()));
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var old = connection.SendAudioAsync(new byte[2], lifetime.Token);
        await connection.RestartAsync(1, lifetime.Token);
        await old.WaitAsync(lifetime.Token);
        var middle = connection.SendAudioAsync(new byte[2], lifetime.Token);
        await connection.RestartAsync(2, lifetime.Token);
        await middle.WaitAsync(lifetime.Token);
        connection.AudioReady();
        await connection.SendAudioAsync(new byte[2], lifetime.Token);
        Check(first.Sent.Count == 0 && second.Sent.Count == 0 && third.Sent.Count == 1,
            "rapid revoke before readiness retires queued PCM and releases old readiness waiters");
    }

    private static void VerifyAudioFrames()
    {
        var input = Guid.NewGuid();
        using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new { inputId = input, sequence = 2, audio = "AAA=" }));
        Check(RealtimeLiveEndpoints.TryReadAudioFrame(valid.RootElement, out var actual, out var sequence, out var pcm)
            && actual == input && sequence == 2 && pcm.Length == 2, "PCM envelope parses without per-frame request headers");
        foreach (var invalid in new object[]
        {
            new { inputId = input, sequence = -1, audio = "AAA=" },
            new { inputId = Guid.Empty, sequence = 0, audio = "AAA=" },
            new { inputId = input, sequence = 0, audio = "AA==" },
            new { inputId = input, sequence = 0, audio = "garbage" },
            new { inputId = input, sequence = 0, audio = "AAA=", extra = true }
        })
        {
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(invalid));
            Check(!RealtimeLiveEndpoints.TryReadAudioFrame(document.RootElement, out _, out _, out _),
                "malformed stream envelopes are rejected");
        }
    }

    private static async Task VerifyOrderedAudioAsync()
    {
        using var socket = new AudioSocket();
        await using var connection = new GptLiveConnection(socket);
        using var lifetime = new CancellationTokenSource();
        using var session = new RealtimeLiveSession("device", "conversation", connection, lifetime, continuous: true);
        Check(session.AttachAudioStream() && !session.AttachAudioStream(), "one stream owns microphone ingress");
        connection.AudioReady();
        await Task.WhenAll(connection.SendAudioAsync(new byte[9600], lifetime.Token),
            connection.SendAsync(new { type = "session.instructions.append", content = "fixture" }, lifetime.Token));
        Check(socket.Sent.Count == 2 && connection.InputPcmBytes == 9600, "audio and control share ordered provider writer");
        await connection.CloseAsync(lifetime.Token);
        await connection.SendAudioAsync(new byte[9600], lifetime.Token);
        Check(socket.Sent.Count == 3 && connection.InputPcmBytes == 9600, "late audio cannot resume after provider close");
        session.EndAudioStream();
        Check(lifetime.IsCancellationRequested, "socket loss cancels provider session rather than replaying input");
    }

    private static async Task VerifyAttachedStreamAsync()
    {
        var input = new CountingInput();
        using var lifetime = new CancellationTokenSource();
        using var turnLifetime = new CancellationTokenSource();
        using var session = new RealtimeLiveSession("device", "conversation", input, lifetime, continuous: true);
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 0, 63, 0,
            PartyWandCatalog.Version, true, "", null, "marin", 1);
        var call = new RealtimeCall("operation", new("device", request.BootId, "conversation", request.ClientTurnId, 0, 0),
            new(request, true, "", []), null, turnLifetime);
        session.Listen(call);
        Check(session.AttachAudioStream(), "authenticated microphone stream attaches");
        Check(!await session.UploadAsync(request.ClientTurnId, 0, new byte[100], default),
            "HTTP ingress cannot interleave with an attached stream");
        Check(await session.UploadAsync(request.ClientTurnId, 0, new byte[100], default, streaming: true),
            "attached WebSocket sends its first frame through actual session admission");
        Check(await session.UploadAsync(request.ClientTurnId, 1, new byte[100], default, streaming: true)
            && input.Frames == 2, "ordered stream frames both reach provider without HTTP round trips");
        Check(!await session.UploadAsync(request.ClientTurnId, 1, new byte[100], default, streaming: true)
            && input.Frames == 2, "duplicate stream sequence cannot replay provider audio");
    }

    private sealed class CountingInput : IStreamingAudioInput
    {
        internal int Frames { get; private set; }
        public Task SendAudioAsync(byte[] pcm, CancellationToken token)
        {
            Frames++;
            return Task.CompletedTask;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new Exception(message); }
    }

    private sealed class AudioSocket : WebSocket
    {
        internal List<string> Sent { get; } = [];
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken token) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken token) =>
            throw new NotSupportedException();
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool complete, CancellationToken token)
        {
            Sent.Add(Encoding.UTF8.GetString(buffer));
            return Task.CompletedTask;
        }
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using Kufr.Backend.Features.DeviceMedia;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.DigitalTwin;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// A loopback WebSocket fixture exercises the production device-media/native bridge. It
// supplies protocol data only; no provider, dialogue or physical engine runs here.
internal static class TwinMediaStreamingTests
{
    private enum StreamFault
    {
        None, Truncated, MissingType, MissingStreamId, StaleGeneration,
        SeededJitter, AbruptLoss, DuplicateFrame, OutOfOrderFrame, FullQueue, StallStopRestart
    }
    internal static async Task RunAsync(string native)
    {
        await CleanupRetryAsync();
        await RunCaseAsync(native, StreamFault.None);
        await RunCaseAsync(native, StreamFault.None, gptLive: false);
        await RunCaseAsync(native, StreamFault.Truncated);
        await RunCaseAsync(native, StreamFault.MissingType);
        await RunCaseAsync(native, StreamFault.MissingStreamId);
        await RunCaseAsync(native, StreamFault.StaleGeneration);
        await RunCaseAsync(native, StreamFault.StaleGeneration, gptLive: false);
        foreach (var engine in new[] { true, false })
        {
            foreach (var fault in new[] { StreamFault.SeededJitter, StreamFault.AbruptLoss,
                StreamFault.DuplicateFrame, StreamFault.OutOfOrderFrame, StreamFault.FullQueue, StreamFault.StallStopRestart })
            { await RunCaseAsync(native, fault, engine); }
        }
        Console.WriteLine("PASS Twin WS streaming/native: paced bounded PCM/drain and malformed-stream cleanup");
    }

    private static async Task CleanupRetryAsync()
    {
        foreach (var scenario in new[] { "transient", "timeout", "unavailable" })
        {
            using var handler = new CleanupHandler(scenario);
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1/") };
            await TwinMediaSessions.DeleteConversationAsync(http, Guid.NewGuid().ToString("D"));
            Check(handler.Attempts == (scenario == "unavailable" ? 3 : 2), "bounded cleanup retry count");
            Check(handler.Keys.Distinct().Count() == 1 && Guid.TryParse(handler.Keys[0], out _), "stable cleanup idempotency key");
        }
        Console.WriteLine("PASS Twin cleanup retries transient failure/timeout, stops on204/404, caps attempts and retains one key");
    }

    private sealed class CleanupHandler(string scenario) : HttpMessageHandler
    {
        internal int Attempts;
        internal readonly List<string> Keys = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            Keys.Add(request.Headers.GetValues("Idempotency-Key").Single());
            if (scenario == "timeout" && Attempts == 1) { throw new TaskCanceledException("Synthetic timeout"); }
            var code = scenario == "unavailable" || Attempts == 1 ? HttpStatusCode.ServiceUnavailable
                : scenario == "timeout" ? HttpStatusCode.NotFound : HttpStatusCode.NoContent;
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }

    private static async Task RunCaseAsync(string native, StreamFault fault, bool gptLive = true)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellation = deadline.Token;
        var root = Path.Combine(Path.GetTempPath(), "twin-stream-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new Upstream(fault, gptLive);
        var engine = gptLive ? "gpt-live" : "realtime";
        var upstreamBuilder = WebApplication.CreateBuilder();
        upstreamBuilder.Logging.ClearProviders();
        upstreamBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var upstream = upstreamBuilder.Build();
        fixture.Map(upstream);
        await upstream.StartAsync(cancellation);

        const string credential = "offline-stream-control-credential-000000000000000000000000001";
        const string instance = "sim-stream";
        const string prefix = "/v1/twin/instances/" + instance;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DigitalTwin:DataRoot"] = root, ["DigitalTwin:Origin"] = "https://twin.test",
            ["Backend:DatabasePath"] = Path.Combine(root, "isolated.sqlite"), ["Backend:Memory:Enabled"] = "false",
            ["DigitalTwin:IntegrationBackendUrl"] = upstream.Urls.Single() + "/",
            ["DigitalTwin:ExternalServicesAuthorized"] = "true", ["OPENAI_API_KEY"] = "offline-unused-stream-fixture",
            [$"DigitalTwin:Instances:{instance}:Mode"] = "simulation", [$"DigitalTwin:Instances:{instance}:DeviceId"] = instance,
            [$"DigitalTwin:Instances:{instance}:Profile"] = "integration", [$"DigitalTwin:Instances:{instance}:Executable"] = native,
            [$"DigitalTwin:Instances:{instance}:BuildId"] = "offline-http-stream-regression",
            ["Devices:0:DeviceId"] = instance, ["Devices:0:CredentialSha256"] = Hash("offline-stream-device-credential-placeholder"),
            ["DigitalTwin:Credentials:0:Role"] = "control", ["DigitalTwin:Credentials:0:Mode"] = "simulation",
            ["DigitalTwin:Credentials:0:InstanceId"] = instance, ["DigitalTwin:Credentials:0:Sha256"] = Hash(credential)
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DeviceAccessStore>(); builder.Services.AddSingleton<TwinAccess>();
        builder.Services.AddSingleton<TwinStore>(); builder.Services.AddSingleton<TwinHost>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TwinHost>());
        builder.Services.AddSingleton<TwinMediaSettingsStore>(); builder.Services.AddSingleton<TwinFakeInputPolicy>();
        builder.Services.AddSingleton<OperationJournal>(); builder.Services.AddSingleton<ConversationStore>();
        builder.Services.AddSingleton<TwinMediaSessions>();
        await using var app = builder.Build();
        app.MapDigitalTwin(); app.MapTwinMedia();
        try
        {
            await app.StartAsync(cancellation);
            var host = app.Services.GetRequiredService<TwinHost>();
            while (!host.IsReady()) { await Task.Delay(20, cancellation); }
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(8) };
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/session") { Content = JsonContent.Create(new { credential }) };
            loginRequest.Headers.Add("Origin", "https://twin.test");
            using var login = await http.SendAsync(loginRequest, cancellation);
            Check(login.IsSuccessStatusCode, "scoped stream login");
            var cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
            using (var inspection = await SendAsync(HttpMethod.Post, "/commands", new { action = "inspect" }))
            { Check(inspection.IsSuccessStatusCode, "stream inspection"); }
            using (var settings = await SendAsync(HttpMethod.Put, "/settings", new
                { expectedRevision = "0", engine, voice = "cedar", speed = 1, photoConsent = false, presentationVariant = "main" }))
            { Check(settings.IsSuccessStatusCode, "dummy-key availability selects real HTTP bridge"); }
            var store = app.Services.GetRequiredService<TwinStore>();
            using var start = await SendAsync(HttpMethod.Post, "/media/start", new
            {
                operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read(instance)!.Snapshot.Source.BootId,
                audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false
            });
            Check(start.IsSuccessStatusCode, "HTTP streaming media admission");
            try { await fixture.StreamStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellation); }
            catch (TimeoutException) { throw new InvalidOperationException("WSS fixture not reached: " + (await StateAsync()).ToString()); }
            if (fault == StreamFault.StallStopRestart)
            {
                await VerifyStallRestartAsync();
                return;
            }
            if (fault is not (StreamFault.None or StreamFault.StaleGeneration or StreamFault.SeededJitter))
            {
                JsonElement state;
                var invalidationDeadline = DateTimeOffset.UtcNow.AddSeconds(fault == StreamFault.FullQueue ? 20 : 4);
                do
                {
                    await Task.Delay(10, cancellation); state = await StateAsync();
                    Check(!state.GetProperty("active").GetBoolean() || DateTimeOffset.UtcNow < invalidationDeadline,
                        $"{fault} stream stayed active: phase={state.GetProperty("phase")}, deletes={fixture.DeleteCount}");
                }
                while (state.GetProperty("active").GetBoolean());
                Check(state.TryGetProperty("error", out var error) && !string.IsNullOrEmpty(error.GetString()),
                    $"post-admission {fault} stream invalidates actor");
                var until = DateTimeOffset.UtcNow.AddSeconds(1);
                while (DateTimeOffset.UtcNow < until)
                {
                    // Native TICKs plus repeated observer/state reads must not
                    // schedule DELETE again for each inactive snapshot.
                    Check(!(await StateAsync()).GetProperty("active").GetBoolean(), "failed stream never revives");
                    using var snapshot = await SendAsync(HttpMethod.Get, "");
                    Check(snapshot.IsSuccessStatusCode, "failed stream observer remains available");
                    await Task.Delay(20, cancellation);
                }
                Check(fixture.Connections == 1, "terminal socket failure never reconnects automatically");
                if (fault == StreamFault.FullQueue)
                {
                    Check(fixture.SentFrames > 16 && !string.IsNullOrEmpty(state.GetProperty("error").GetString()),
                        "full bounded playback pipeline fails explicitly: " + state.ToString());
                    var stopAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    using var stopped = await SendAsync(HttpMethod.Post, "/commands", new { action = "stop" });
                    Check(stopped.IsSuccessStatusCode && System.Diagnostics.Stopwatch.GetElapsedTime(stopAt) < TimeSpan.FromSeconds(1),
                        "Stop stays independent after full playback queue");
                }
                Check(fixture.DeleteCount is >= 1 and <= 2, "cleanup bounded to native and HTTP actor DELETE");
                Check(store.Read(instance)!.Snapshot.Reported.Slot == 0, $"{fault} stream has no physical proposal");
                Console.WriteLine($"PASS Twin WS {fault} stream: inactive actor and bounded DELETE cleanup");
                return;
            }

            // Briefly hold the browser HAL, then consume through the real
            // bounded playback owner while the fixture streams binary frames.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellation);
            using var received = new MemoryStream();
            ulong lastSequence = 0;
            var completed = false;
            var dialogue = false;
            var drained = false;
            var pcmFrames = 0;
            var lastOutputAt = DateTimeOffset.UtcNow;
            while (!completed || (await StateAsync()).GetProperty("active").GetBoolean())
            {
                using var response = await SendAsync(HttpMethod.Get, "/media/output");
                Check(response.IsSuccessStatusCode, "stream output remains readable after backpressure");
                if (response.StatusCode == HttpStatusCode.NoContent)
                {
                    var emptyState = await StateAsync();
                    var phase = emptyState.GetProperty("phase").GetString();
                    var error = emptyState.TryGetProperty("error", out var failure) && failure.ValueKind == JsonValueKind.String
                        ? failure.GetString() : null;
                    Check(phase != "failed" && string.IsNullOrEmpty(error) && DateTimeOffset.UtcNow - lastOutputAt < TimeSpan.FromSeconds(4),
                        $"empty HAL output: phase={phase}, active={emptyState.GetProperty("active")}, error={error ?? "none"}, "
                        + $"bytes={received.Length}, frames={pcmFrames}, drained={drained}, played={fixture.PlayedCount}, deletes={fixture.DeleteCount}");
                    await Task.Delay(5, cancellation); continue;
                }
                lastOutputAt = DateTimeOffset.UtcNow;
                var output = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
                var kind = output.GetProperty("kind").GetString();
                var sequence = ulong.Parse(output.GetProperty("sequence").GetString()!);
                Check(sequence > lastSequence, "HAL sequences remain monotonic after coalescing");
                lastSequence = sequence;
                if (kind == "pcm")
                {
                    var bytes = Convert.FromBase64String(output.GetProperty("pcmBase64").GetString()!);
                    Check(bytes.Length is > 0 and <= 32768 && bytes.Length == output.GetProperty("byteLength").GetInt32(), "coalesced PCM bound");
                    received.Write(bytes); pcmFrames++;
                    Check(ulong.Parse(output.GetProperty("pcmBytes").GetString()!) == (ulong)received.Length, "exact cumulative PCM count");
                    await PlayedAsync(output, false);
                }
                else if (kind == "drain")
                {
                    Check(received.ToArray().SequenceEqual(fixture.Expected), "all burst bytes preserved in order without retry duplication");
                    Check(ulong.Parse(output.GetProperty("pcmBytes").GetString()!) == (ulong)received.Length, "drain matches bytes actually consumed");
                    await PlayedAsync(output, true); drained = true;
                }
                else if (kind == "dialogue")
                {
                    Check(output.GetProperty("text").GetString() == Upstream.Reply, "dialogue comes only from completed result");
                    dialogue = true;
                }
                else if (kind == "failed") { throw new InvalidOperationException("Burst unexpectedly failed native workflow."); }
                else if (kind == "completed") { completed = true; }
            }
            Check(drained && dialogue && pcmFrames is > 0 and <= Upstream.FrameCount * 2 && fixture.PlayedCount == 1, $"completed stream preserved bounded binary frames and genuine software playback ACK: drained={drained}, dialogue={dialogue}, frames={pcmFrames}, played={fixture.PlayedCount}");
            var finalState = await StateAsync();
            Check(finalState.GetProperty("phase").GetString() == "completed"
                && (!finalState.TryGetProperty("error", out var finalError) || finalError.ValueKind == JsonValueKind.Null),
                "burst finishes without a hidden actor or stream failure");
            Check(store.Read(instance)!.Snapshot.Reported.Slot == 0 && !store.Read(instance)!.Snapshot.Reported.NeedsRecovery,
                "no-selection fixture does not admit a physical proposal or claim hardware feedback");
            Console.WriteLine($"PASS Twin WS {engine} {fault} stream: {fixture.Count} PCM frames, exact bytes/drain and generation-bound completion");

            async Task VerifyStallRestartAsync()
            {
                await fixture.Stalled.Task.WaitAsync(cancellation);
                var original = await StateAsync();
                var stopAt = System.Diagnostics.Stopwatch.GetTimestamp();
                using (var stop = await SendAsync(HttpMethod.Post, "/commands", new { action = "stop" }))
                { Check(stop.IsSuccessStatusCode, "Stop accepted during multi-second real socket stall"); }
                var stopLatency = System.Diagnostics.Stopwatch.GetElapsedTime(stopAt);
                Check(stopLatency < TimeSpan.FromSeconds(1) && !(await StateAsync()).GetProperty("active").GetBoolean(),
                    "native Stop stays independent of stalled receive/playback");
                Check(fixture.Connections == 1, "stopped original socket never reconnects");
                using (var inspection = await SendAsync(HttpMethod.Post, "/commands", new { action = "inspect" }))
                { Check(inspection.IsSuccessStatusCode, "explicit recovery before replacement"); }
                fixture.NextSession();
                var replacement = await StartReplacementAsync();
                Check(replacement != original.GetProperty("sessionId").GetString(), "replacement has fresh native authority");
                await fixture.DelayedCallback.Task.WaitAsync(cancellation);
                var afterLate = await StateAsync();
                Check(afterLate.GetProperty("sessionId").GetString() == replacement && afterLate.GetProperty("active").GetBoolean(),
                    "delayed old callback cannot close or revive replacement");
                await ConsumeReplacementAsync();

                // Actually stop Kestrel and bind a fresh backend fixture at the
                // same address. Recovery requires a fresh explicit actor/start.
                var backendAddress = upstream.Urls.Single();
                await upstream.StopAsync(cancellation);
                var restartedBuilder = WebApplication.CreateBuilder();
                restartedBuilder.Logging.ClearProviders(); restartedBuilder.WebHost.UseUrls(backendAddress);
                await using var restarted = restartedBuilder.Build();
                fixture.NextSession(); fixture.Map(restarted);
                await restarted.StartAsync(cancellation);
                try
                {
                    var restartedSession = await StartReplacementAsync();
                    Check(restartedSession != replacement, "backend restart uses fresh native session");
                    await ConsumeReplacementAsync();
                    Check(fixture.Connections == 3 && fixture.PlayedCount == 2,
                        "only explicit replacements connect and acknowledge; cancelled audio never replays");
                }
                finally { await restarted.StopAsync(CancellationToken.None); }
                Check(store.Read(instance)!.Snapshot.Reported.Slot == 0, "network recovery never admits a physical action");
                Console.WriteLine($"PASS Twin real WS {engine}: 2500ms stall, Stop {stopLatency.TotalMilliseconds:F0}ms, delayed old callback, explicit replacement/backend restart without replay");
            }
            async Task<string> StartReplacementAsync()
            {
                using var response = await SendAsync(HttpMethod.Post, "/media/start", new
                {
                    operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read(instance)!.Snapshot.Source.BootId,
                    audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false
                });
                Check(response.IsSuccessStatusCode, "explicit replacement media admission");
                return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellation)).GetProperty("sessionId").GetString()!;
            }
            async Task ConsumeReplacementAsync()
            {
                using var bytes = new MemoryStream();
                while (true)
                {
                    using var response = await SendAsync(HttpMethod.Get, "/media/output");
                    Check(response.IsSuccessStatusCode, "replacement output access");
                    if (response.StatusCode == HttpStatusCode.NoContent) { await Task.Delay(5, cancellation); continue; }
                    var frame = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
                    var kind = frame.GetProperty("kind").GetString();
                    if (kind == "failed") { throw new InvalidOperationException("Replacement actor failed: " + (await StateAsync()).ToString()); }
                    if (kind == "pcm") { bytes.Write(Convert.FromBase64String(frame.GetProperty("pcmBase64").GetString()!)); }
                    if (kind == "drain")
                    {
                        Check(bytes.ToArray().SequenceEqual(fixture.Expected), "replacement output excludes cancelled audio and preserves exact bytes");
                        using var played = await SendAsync(HttpMethod.Post, "/media/played", new
                        {
                            sessionId = frame.GetProperty("sessionId").GetString(), bootId = frame.GetProperty("bootId").GetString(),
                            epoch = frame.GetProperty("epoch").GetString(), outputEpoch = frame.GetProperty("outputEpoch").GetString(),
                            sequence = frame.GetProperty("sequence").GetString(), pcmBytes = bytes.Length.ToString(), drained = true
                        });
                        Check(played.IsSuccessStatusCode, "replacement drain receipt admitted");
                    }
                    if (kind == "completed") { return; }
                }
            }

            async Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, object? body = null)
            {
                using var request = new HttpRequestMessage(method, prefix + route);
                request.Headers.Add("Cookie", cookie); request.Headers.Add("Origin", "https://twin.test");
                if (body is not null) { request.Content = JsonContent.Create(body); }
                return await http.SendAsync(request, cancellation);
            }
            async Task<JsonElement> StateAsync()
            {
                using var response = await SendAsync(HttpMethod.Get, "/media/state");
                Check(response.IsSuccessStatusCode, "stream state access");
                return await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
            }
            async Task PlayedAsync(JsonElement output, bool drainedOutput)
            {
                using var response = await SendAsync(HttpMethod.Post, "/media/played", new
                {
                    sessionId = output.GetProperty("sessionId").GetString(), bootId = output.GetProperty("bootId").GetString(),
                    epoch = output.GetProperty("epoch").GetString(), outputEpoch = output.GetProperty("outputEpoch").GetString(),
                    sequence = output.GetProperty("sequence").GetString(), pcmBytes = received.Length.ToString(), drained = drainedOutput
                });
                Check(response.IsSuccessStatusCode, "actual HTTP playback ACK accepted");
            }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await upstream.StopAsync(CancellationToken.None);
        }
    }

    private sealed class Upstream(StreamFault fault, bool gptLive)
    {
        internal const int FrameCount = 64;
        internal const int JitterSeed = 82421;
        internal int Count => Mode == StreamFault.SeededJitter ? 16 : Mode == StreamFault.FullQueue ? 300 : FrameCount;
        internal StreamFault Mode { get; set; } = fault;
        private int connections, sentFrames;
        internal int SentFrames => Volatile.Read(ref sentFrames);
        internal int Connections => Volatile.Read(ref connections);
        internal readonly TaskCompletionSource Stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource DelayedCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal const string Reply = "Offline stream completed.";
        internal readonly TaskCompletionSource StreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string conversation = Guid.NewGuid().ToString("D"), stream = Guid.NewGuid().ToString("D"), operation = Guid.NewGuid().ToString("D");
        internal void NextSession()
        {
            Mode = StreamFault.None;
            conversation = Guid.NewGuid().ToString("D"); stream = Guid.NewGuid().ToString("D"); operation = Guid.NewGuid().ToString("D");
        }
        private int deletes, playedCount;
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> createdConversations = new();
        private string? device, boot;
        internal int DeleteCount => Volatile.Read(ref deletes);
        internal int PlayedCount => Volatile.Read(ref playedCount);
        internal byte[] Expected => Enumerable.Range(0, Count).SelectMany(Frame).ToArray();
        private static byte[] Frame(int index)
        {
            var bytes = Enumerable.Repeat((byte)(index % 251), 4800).ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(), index);
            return bytes;
        }
        internal void Map(WebApplication app)
        {
            app.MapPost("/v1/conversations", async (HttpContext context) =>
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                device = body.RootElement.GetProperty("deviceId").GetString(); boot = body.RootElement.GetProperty("bootId").GetString();
                createdConversations.TryAdd(conversation, 0);
                context.Response.StatusCode = 201;
                await context.Response.WriteAsJsonAsync(new { conversationId = conversation, expiresAt = "2099-01-01T00:00:00Z",
                    limits = new { maxTurns = 10, maxTextCharacters = 1000, idleSeconds = 900, maxLifetimeSeconds = 1800 } }, context.RequestAborted);
            });
            app.UseWebSockets();
            app.MapGet("/v1/conversations/{conversationId}/device-media", async (HttpContext context) =>
            {
                var conversation = this.conversation;
                var stream = this.stream;
                var operation = this.operation;
                var fault = Mode;
                Interlocked.Increment(ref connections);
                Check(context.Request.RouteValues["conversationId"]?.ToString() == conversation, "fixture conversation route");
                Check(context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
                    && Guid.TryParse(context.Request.Headers["Idempotency-Key"], out _), "device bearer and original key stay server-side");
                using var websocket = await context.WebSockets.AcceptWebSocketAsync(DeviceMediaProtocol.Subprotocol);
                using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                var transport = new DeviceMediaSocket(websocket, lifetime);
                var sink = new DeviceMediaEventSink(transport, lifetime, _ => { });
                var received = new byte[8193];
                var opening = await transport.ReceiveAsync(received, context.RequestAborted);
                using var body = JsonDocument.Parse(received.AsMemory(0, opening.Count));
                Check(body.RootElement.GetProperty("engine").GetString() == (gptLive ? "gpt-live" : "realtime"), "selected engine through shared WSS opening");
                var actual = body.RootElement.GetProperty("request");
                Check(actual.GetProperty("bootId").GetString() == boot, "actual native start boot correlation");
                var correlation = new Dictionary<string, object?>
                {
                    ["deviceId"] = device, ["bootId"] = boot, ["conversationId"] = conversation,
                    ["clientTurnId"] = actual.GetProperty("clientTurnId").GetString(),
                    ["generation"] = actual.GetProperty("generation").Clone(),
                    ["deviceRevision"] = actual.GetProperty("deviceRevision").Clone()
                };
                if (actual.TryGetProperty("operationEpoch", out var epoch)) { correlation["operationEpoch"] = epoch.Clone(); }
                var responseGeneration = 0;
                var played = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var controls = ReadControlsAsync();
                _ = controls.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                await EmitAsync(new { type = "session", streamId = stream, audioFormat = "pcm16-mono-24000" });
                await EmitAsync(new { type = "started", operationId = operation, correlation, responseGeneration = 0 });
                StreamStarted.TrySetResult();
                if (fault == StreamFault.Truncated)
                {
                    await transport.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"audio\""), WebSocketMessageType.Text, context.RequestAborted);
                    return;
                }
                if (fault == StreamFault.MissingType)
                {
                    await EmitAsync(new { delta = "AAAA" });
                    return;
                }
                if (fault == StreamFault.MissingStreamId)
                {
                    await EmitAsync(new { type = "session", audioFormat = "pcm16-mono-24000" });
                    return;
                }
                if (fault is StreamFault.DuplicateFrame or StreamFault.OutOfOrderFrame)
                {
                    await transport.SendJsonAsync(new { type = "media.output", stream = 1,
                        responseGeneration = 0, format = "pcm16-mono-24000" }, context.RequestAborted);
                    var invalid = new byte[18];
                    DeviceMediaProtocol.WritePcmHeader(invalid, 1, fault == StreamFault.OutOfOrderFrame ? 1U : 0U, 2);
                    await transport.SendAsync(invalid, WebSocketMessageType.Binary, context.RequestAborted);
                    if (fault == StreamFault.DuplicateFrame)
                    { await transport.SendAsync(invalid, WebSocketMessageType.Binary, context.RequestAborted); }
                    await Task.Delay(100, context.RequestAborted);
                    return;
                }
                if (fault == StreamFault.StallStopRestart)
                {
                    await EmitAsync(new { type = "audio", delta = Convert.ToBase64String(Frame(999)), responseGeneration = 0 });
                    Stalled.TrySetResult();
                    // Intentionally late, even after peer cancellation: this is
                    // a delayed old transport callback, never current authority.
                    await Task.Delay(2500);
                    try { await transport.SendJsonAsync(new { type = "closed", reason = "conversation_terminal", responseGeneration = 0 }, CancellationToken.None); }
                    catch (Exception error) when (error is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
                    finally { DelayedCallback.TrySetResult(); }
                    return;
                }
                responseGeneration = fault == StreamFault.StaleGeneration ? 1 : 0;
                if (fault == StreamFault.StaleGeneration)
                {
                    await EmitAsync(new { type = "audio_cancel", responseGeneration });
                    // Late work from the same operation must not restart audio,
                    // publish a result or satisfy the replacement playback.
                    await EmitAsync(new { type = "visitor", text = "Obsolete visitor.", responseGeneration = 0 });
                    await EmitAsync(new { type = "pause", responseGeneration = 0 });
                    await EmitAsync(new { type = "playback_checkpoint", playbackId = Guid.NewGuid().ToString("D"),
                        pcmBytes = 6, decisionRevision = 0, responseGeneration = 0 });
                    await EmitAsync(new { type = "speech_started", inputId = Guid.NewGuid().ToString("D"), responseGeneration = 0 });
                    await EmitAsync(new { type = "speech_stopped", inputId = Guid.NewGuid().ToString("D"), responseGeneration = 0 });
                    await EmitAsync(new { type = "closed", reason = "conversation_terminal", responseGeneration = 0 });
                    if (!gptLive)
                    {
                        await EmitAsync(new { type = "started", operationId = operation, correlation, responseGeneration });
                    }
                }
                var random = new Random(JitterSeed);
                for (var index = 0; index < Count; index++)
                {
                    await EmitAsync(new { type = "audio", delta = Convert.ToBase64String(Frame(index)), responseGeneration });
                    Interlocked.Increment(ref sentFrames);
                    // Pace a software fixture burst without claiming that the
                    // fixed playback RAM can absorb an unbounded provider burst.
                    if (fault == StreamFault.AbruptLoss && index == 4) { websocket.Abort(); return; }
                    var pause = fault == StreamFault.FullQueue ? 0
                        : fault == StreamFault.SeededJitter ? index == 4 ? 500 : index == 10 ? 2500 : random.Next(50, 301) : 100;
                    if (pause > 0) { await Task.Delay(pause, context.RequestAborted); }
                }
                await EmitAsync(new { type = "completed", operationId = operation, correlation,
                    result = new { reply = Reply, decision = "no_selection", reason = "", proposal = (object?)null }, costMicroUsd = 0, responseGeneration });
                await played.Task.WaitAsync(TimeSpan.FromSeconds(25), context.RequestAborted);
                await EmitAsync(new { type = "closed", reason = "conversation_terminal", responseGeneration });
                await controls;

                async Task EmitAsync(object value)
                {
                    using var item = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, RealtimeProtocol.JsonOptions));
                    if (item.RootElement.TryGetProperty("type", out var type) && type.GetString() == "started")
                    {
                        sink.BindOperation(operation);
                        await sink.WriteAsync(new ConversationOperationBinding(operation, item.RootElement.GetProperty("responseGeneration").GetInt64()), context.RequestAborted);
                    }
                    await sink.WriteAsync(value, context.RequestAborted);
                }
                async Task ReadControlsAsync()
                {
                    var buffer = new byte[8193];
                    while (!played.Task.IsCompleted)
                    {
                        var frame = await transport.ReceiveAsync(buffer, context.RequestAborted);
                        Check(frame.Kind == WebSocketMessageType.Text, "fixture receives typed controls");
                        using var control = JsonDocument.Parse(buffer.AsMemory(0, frame.Count));
                        var root = control.RootElement;
                        Check(root.GetProperty("type").GetString() == "played" && root.GetProperty("operationId").GetString() == operation
                            && root.GetProperty("responseGeneration").GetInt64() == (fault == StreamFault.StaleGeneration ? 1 : 0), "WSS played identity");
                        await transport.SendJsonAsync(new { type = "control.ack", requestId = root.GetProperty("requestId").GetString(), status = 204 }, context.RequestAborted);
                        Interlocked.Increment(ref playedCount); played.TrySetResult();
                    }
                }
            });
            app.MapDelete("/v1/conversations/{conversationId}", (HttpContext context) =>
            {
                Check(createdConversations.ContainsKey(context.Request.RouteValues["conversationId"]?.ToString() ?? ""), "fixture delete route");
                Interlocked.Increment(ref deletes); return Results.NoContent();
            });
        }
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Check(bool value, string reason)
    { if (!value) { throw new InvalidOperationException("Twin streaming regression failed: " + reason); } }
}

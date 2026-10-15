using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
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

// A loopback NDJSON fixture exercises the production HTTP/native bridge. It
// supplies protocol data only; no provider, dialogue or physical engine runs here.
internal static class TwinMediaStreamingTests
{
    private enum StreamFault { None, Truncated, MissingType, MissingStreamId }
    internal static async Task RunAsync(string native)
    {
        await RunCaseAsync(native, StreamFault.None);
        await RunCaseAsync(native, StreamFault.Truncated);
        await RunCaseAsync(native, StreamFault.MissingType);
        await RunCaseAsync(native, StreamFault.MissingStreamId);
        Console.WriteLine("PASS Twin HTTP streaming/native: burst backpressure, exact PCM/drain and malformed-stream bounded cleanup");
    }

    private static async Task RunCaseAsync(string native, StreamFault fault)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cancellation = deadline.Token;
        var root = Path.Combine(Path.GetTempPath(), "twin-stream-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fixture = new Upstream(fault);
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
                { expectedRevision = "0", engine = "gpt-live", voice = "cedar", speed = 1, photoConsent = false, presentationVariant = "main" }))
            { Check(settings.IsSuccessStatusCode, "dummy-key availability selects real HTTP bridge"); }
            var store = app.Services.GetRequiredService<TwinStore>();
            using var start = await SendAsync(HttpMethod.Post, "/media/start", new
            {
                operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read(instance)!.Snapshot.Source.BootId,
                audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false
            });
            Check(start.IsSuccessStatusCode, "HTTP streaming media admission");
            await fixture.StreamStarted.Task.WaitAsync(cancellation);
            if (fault != StreamFault.None)
            {
                JsonElement state;
                var invalidationDeadline = DateTimeOffset.UtcNow.AddSeconds(4);
                do
                {
                    await Task.Delay(10, cancellation); state = await StateAsync();
                    Check(!state.GetProperty("active").GetBoolean() || DateTimeOffset.UtcNow < invalidationDeadline,
                        $"{fault} stream stayed active: phase={state.GetProperty("phase")}, deletes={fixture.DeleteCount}");
                }
                while (state.GetProperty("active").GetBoolean());
                Check(state.TryGetProperty("error", out var error) && error.GetString() == "media_stream_failed",
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
                Check(fixture.DeleteCount is >= 1 and <= 2, "cleanup bounded to native and HTTP actor DELETE");
                Check(store.Read(instance)!.Snapshot.Reported.Slot == 0, $"{fault} stream has no physical proposal");
                Console.WriteLine($"PASS Twin HTTP {fault} stream: inactive actor and bounded DELETE cleanup");
                return;
            }

            // Deliberately hold the browser HAL while the real upstream stream
            // fills 32 coalesced PCM slots and eight pending native events.
            await Task.Delay(TimeSpan.FromSeconds(5), cancellation);
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
            Check(drained && dialogue && pcmFrames < 260 && fixture.PlayedCount == 1, "completed stream used coalescing and genuine software playback ACK");
            var finalState = await StateAsync();
            Check(finalState.GetProperty("phase").GetString() == "completed"
                && (!finalState.TryGetProperty("error", out var finalError) || finalError.ValueKind == JsonValueKind.Null),
                "burst finishes without a hidden actor or stream failure");
            Check(store.Read(instance)!.Snapshot.Reported.Slot == 0 && !store.Read(instance)!.Snapshot.Reported.NeedsRecovery,
                "no-selection fixture does not admit a physical proposal or claim hardware feedback");
            Console.WriteLine("PASS Twin HTTP native stream: 260 PCM frames, slow consumer, exact bytes/drain and completed result");

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

    private sealed class Upstream(StreamFault fault)
    {
        internal const string Reply = "Offline stream completed.";
        internal readonly TaskCompletionSource StreamStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource played = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string conversation = Guid.NewGuid().ToString("D"), stream = Guid.NewGuid().ToString("D"), operation = Guid.NewGuid().ToString("D");
        private int deletes, playedCount;
        private string? device, boot;
        internal int DeleteCount => Volatile.Read(ref deletes);
        internal int PlayedCount => Volatile.Read(ref playedCount);
        internal byte[] Expected { get; } = Enumerable.Range(0, 260).SelectMany(Frame).ToArray();
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
                context.Response.StatusCode = 201;
                await context.Response.WriteAsJsonAsync(new { conversationId = conversation, expiresAt = "2099-01-01T00:00:00Z",
                    limits = new { maxTurns = 10, maxTextCharacters = 1000, idleSeconds = 900, maxLifetimeSeconds = 1800 } }, context.RequestAborted);
            });
            app.MapPost("/v1/conversations/{conversationId}/gpt-live", async (HttpContext context) =>
            {
                Check(context.Request.RouteValues["conversationId"]?.ToString() == conversation, "fixture conversation route");
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var actual = body.RootElement;
                Check(actual.GetProperty("bootId").GetString() == boot, "actual native start boot correlation");
                var correlation = new { deviceId = device, bootId = boot, conversationId = conversation,
                    clientTurnId = actual.GetProperty("clientTurnId").GetString(), generation = actual.GetProperty("generation").Clone(),
                    deviceRevision = actual.GetProperty("deviceRevision").Clone() };
                context.Response.ContentType = "application/x-ndjson";
                await EmitAsync(new { type = "session", streamId = stream, audioFormat = "pcm16-mono-24000" });
                await EmitAsync(new { type = "started", operationId = operation, correlation });
                StreamStarted.TrySetResult();
                if (fault == StreamFault.Truncated)
                {
                    await context.Response.WriteAsync("{\"type\":\"audio\"", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
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
                for (var index = 0; index < 260; index++)
                {
                    await EmitAsync(new { type = "audio", delta = Convert.ToBase64String(Frame(index)) });
                    // Twenty times realtime, matching the reported provider
                    // burst while leaving room before the five-second stall cap.
                    await Task.Delay(5, context.RequestAborted);
                }
                await EmitAsync(new { type = "completed", operationId = operation, correlation,
                    result = new { reply = Reply, decision = "no_selection", reason = "", proposal = (object?)null }, costMicroUsd = 0 });
                await played.Task.WaitAsync(TimeSpan.FromSeconds(25), context.RequestAborted);
                await EmitAsync(new { type = "closed", reason = "conversation_terminal" });

                async Task EmitAsync(object value)
                {
                    await context.Response.WriteAsync(JsonSerializer.Serialize(value, RealtimeProtocol.JsonOptions) + "\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            });
            app.MapPost("/v1/conversations/{conversationId}/gpt-live/{streamId}/played", (HttpContext context) =>
            {
                Check(context.Request.RouteValues["conversationId"]?.ToString() == conversation
                    && context.Request.RouteValues["streamId"]?.ToString() == stream, "fixture played route");
                Interlocked.Increment(ref playedCount); played.TrySetResult(); return Results.NoContent();
            });
            app.MapDelete("/v1/conversations/{conversationId}", (HttpContext context) =>
            {
                Check(context.Request.RouteValues["conversationId"]?.ToString() == conversation, "fixture delete route");
                Interlocked.Increment(ref deletes); return Results.NoContent();
            });
        }
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Check(bool value, string reason)
    { if (!value) { throw new InvalidOperationException("Twin streaming regression failed: " + reason); } }
}

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.DeviceMedia;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Memory;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class DeviceMediaTransportTests
{
    private const string MaximumEpoch = "18446744073709551615";

    internal static async Task RunAsync()
    {
        VerifyOpeningAndFrames();
        VerifyControlReceipts();
        await VerifyHttpSinkAsync();
        await VerifyStalledWriterAsync();
        await VerifyOperationPublicationAsync();
        await VerifyReceiveLeaseReleaseAsync();
        await VerifyCancellationSchedulesAsync();
        await VerifyRealSocketsAsync();
        Console.WriteLine("PASS device media strict framing/HTTP sink, real mapped available-provider lifecycle/control/input/replay, stalled-upload cancellation, bearer admission/expiry and cleanup.");
    }

    private static string Opening(string engine = "gpt-live", string? epoch = MaximumEpoch)
    {
        var request = new RealtimeTurnRequest(Guid.NewGuid(), Guid.NewGuid(), 1, 63, 7,
            PartyWandCatalog.Version, true, "", null, "marin", 1, epoch);
        return JsonSerializer.Serialize(new { type = "open", engine, request }, RealtimeProtocol.JsonOptions);
    }

    private static void VerifyOpeningAndFrames()
    {
        foreach (var engine in new[] { "gpt-live", "realtime" })
        {
            foreach (var epoch in new string?[] { null, "0", "9007199254740993", MaximumEpoch })
            {
                var parsed = DeviceMediaProtocol.ReadOpening(Encoding.UTF8.GetBytes(Opening(engine, epoch)));
                Check(parsed.Engine == engine && RealtimeProtocol.ReadRequest(parsed.Request)?.OperationEpoch == epoch,
                    "opening retains engine and exact canonical epoch");
            }
        }
        var valid = Opening();
        var invalid = new[]
        {
            "[]", "{}", "{", valid.Replace("\"open\"", "\"other\""),
            valid.Replace("\"gpt-live\"", "\"unknown\""), valid[..^1] + ",\"extra\":1}",
            valid[..^1] + ",\"type\":\"open\"}", valid.Replace("\"opening\":true", "\"opening\":false"),
            valid.Replace("\"generation\":1", "\"generation\":1,\"generation\":1"),
            valid.Replace("\"operationEpoch\":\"" + MaximumEpoch + "\"", "\"operationEpoch\":1"),
            Opening(epoch: "01"), Opening(epoch: "18446744073709551616"),
            valid.Replace("\"text\":\"\"", "\"text\":\"" + new string('x', 4200) + "\"")
        };
        foreach (var value in invalid)
        {
            Reject(() => DeviceMediaProtocol.ReadOpening(Encoding.UTF8.GetBytes(value)), "invalid opening rejected");
        }
        var frame = InputFrame(7, 3, 4);
        Check(DeviceMediaProtocol.ReadPcmHeader(frame) == (7U, 3U), "little endian input frame");
        foreach (var length in new[] { 0, 16, 17, 19, 4114 })
        {
            Reject(() => DeviceMediaProtocol.ReadPcmHeader(new byte[length]), "invalid input frame length");
        }
        foreach (var index in new[] { 0, 1, 2, 3, 4, 12 })
        {
            var corrupt = frame.ToArray();
            corrupt[index] = 0;
            Reject(() => DeviceMediaProtocol.ReadPcmHeader(corrupt), "invalid KF header rejected");
        }
        DeviceMediaProtocol.WritePcmHeader(frame, 9, 8, 4);
        Check(frame[3] == 2 && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4)) == 9
            && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8)) == 8
            && BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(12)) == 4, "output header direction and lengths");
    }

    private static async Task VerifyHttpSinkAsync()
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new FlushedStream();
        context.Response.Body = body;
        var sink = new HttpConversationEventSink(context);
        Check(!sink.HasStarted, "HTTP problem may precede stream");
        sink.Start(Guid.NewGuid());
        Check(context.Response.ContentType == "application/x-ndjson"
            && context.Response.Headers["X-Accel-Buffering"] == "no", "HTTP stream disables proxy buffering");
        sink.BindOperation("http-fixture-operation");
        await sink.WriteAsync(new ConversationOperationBinding("http-fixture-operation", 0), CancellationToken.None);
        Check(body.Length == 0 && body.Flushes == 0, "internal operation binding adds no HTTP bytes or flush");
        await sink.WriteAsync(new { type = "text", text = "one\ntwo" }, CancellationToken.None);
        var firstFlushes = body.Flushes;
        await sink.WriteAsync(new { type = "closed" }, CancellationToken.None);
        var expected = "{\"type\":\"text\",\"text\":\"one\\ntwo\"}\n{\"type\":\"closed\"}\n";
        Check(Encoding.UTF8.GetString(body.ToArray()) == expected && firstFlushes > 0 && body.Flushes > firstFlushes,
            "HTTP sink preserves exact NDJSON with one LF and flush per event");
        var problemContext = new DefaultHttpContext { RequestServices = services };
        using var problemBody = new MemoryStream();
        problemContext.Response.Body = problemBody;
        await new HttpConversationEventSink(problemContext).ProblemAsync(409, "fixture_conflict");
        using var problem = JsonDocument.Parse(problemBody.ToArray());
        Check(problemContext.Response.StatusCode == 409
            && problemContext.Response.ContentType?.StartsWith("application/problem+json", StringComparison.Ordinal) == true
            && problem.RootElement.GetProperty("code").GetString() == "fixture_conflict", "pre-stream HTTP ProblemDetails");
    }

    private static void VerifyControlReceipts()
    {
        var ledger = new DeviceMediaControlLedger();
        var request = Guid.NewGuid();
        var body = "{\"type\":\"availability\",\"operationEpoch\":\"18446744073709551615\"}"u8.ToArray();
        Check(!ledger.IsReplay(request, body) && ledger.IsReplay(request, body), "exact control replay never reapplies");
        Reject(() => ledger.Result(request), "control cannot acknowledge before actual application outcome");
        ledger.Complete(request, 409, "conversation_restart_required");
        Check(ledger.IsReplay(request, body) && ledger.Result(request) == (409, "conversation_restart_required"),
            "typed control refusal replay preserves actual status and code");
        Reject(() => ledger.Complete(request, 204), "completed control receipt cannot be restamped as success");
        Reject(() => ledger.IsReplay(request, "{\"type\":\"availability\",\"operationEpoch\":\"1\"}"u8),
            "same request ID cannot mutate canonical epoch");
        Reject(() => ledger.IsReplay(Guid.Empty, body), "empty control request ID rejected");
        for (var count = 1; count < DeviceMediaControlLedger.Capacity; count++)
        {
            Check(!ledger.IsReplay(Guid.NewGuid(), body), "distinct request has distinct receipt");
        }
        Check(ledger.IsReplay(request, body), "known replay survives full bounded ledger");
        Reject(() => ledger.IsReplay(Guid.NewGuid(), body), "new controls fail closed at bounded capacity");
    }

    private static async Task VerifyStalledWriterAsync()
    {
        using var lifetime = new CancellationTokenSource();
        using var stalled = new StalledSocket();
        var socket = new DeviceMediaSocket(stalled, lifetime);
        var receive = socket.ReceiveAsync(new byte[32], lifetime.Token);
        var send = socket.SendAsync(new byte[2], WebSocketMessageType.Binary, CancellationToken.None);
        await ExpectCancellationAsync(send.WaitAsync(TimeSpan.FromSeconds(5)));
        await ExpectCancellationAsync(receive.WaitAsync(TimeSpan.FromSeconds(1)));
        Check(lifetime.IsCancellationRequested && stalled.Aborted, "write deadline aborts socket and cancels receive owner");
    }

    private static async Task VerifyOperationPublicationAsync()
    {
        using (var lifetime = new CancellationTokenSource())
        using (var transport = new PublicationSocket())
        {
            var sink = new DeviceMediaEventSink(new DeviceMediaSocket(transport, lifetime), lifetime, _ => { });
            sink.BindOperation("old-operation");
            var pending = sink.WriteAsync(new ConversationOperationBinding("old-operation", 0), CancellationToken.None);
            await transport.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(!sink.HasPublishedOperation && !sink.MatchesOperation("old-operation", 0), "pending send grants no operation authority");
            sink.BindOperation("new-operation");
            transport.Release.TrySetResult();
            await ExpectCancellationAsync(pending);
            Check(!sink.HasPublishedOperation && !sink.MatchesOperation("new-operation", 0),
                "stale completed send cannot authorize either retired operation or newly bound operation");
            await sink.WriteAsync(new ConversationOperationBinding("new-operation", 7), CancellationToken.None);
            Check(sink.MatchesOperation("new-operation", 7) && !sink.MatchesOperation("new-operation", 6)
                && !sink.MatchesOperation("old-operation", 7), "completed publication authorizes only exact operation and generation");
            sink.BindOperation("third-operation");
            Check(!sink.HasPublishedOperation, "new bind immediately retires previous publication");
            await ExpectCancellationAsync(sink.WriteAsync(new ConversationOperationBinding("new-operation", 7), CancellationToken.None));
            Check(!sink.HasPublishedOperation, "stale marker rejected before send cannot borrow new operation");
        }
        foreach (var revocation in new[] { "writer", "connection" })
        {
            using var lifetime = new CancellationTokenSource();
            using var writer = new CancellationTokenSource();
            using var transport = new PublicationSocket();
            var sink = new DeviceMediaEventSink(new DeviceMediaSocket(transport, lifetime), lifetime, _ => { });
            sink.BindOperation("cancelled-operation");
            var pending = sink.WriteAsync(new ConversationOperationBinding("cancelled-operation", 0), writer.Token);
            await transport.SendEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            if (revocation == "writer") { writer.Cancel(); }
            else { lifetime.Cancel(); }
            // Simulate an underlying send completing despite cancellation. The
            // sink must check cancellation again before publishing authority.
            transport.Release.TrySetResult();
            await ExpectCancellationAsync(pending);
            Check(!sink.HasPublishedOperation, "revoked " + revocation + " publication grants no cancellation authority");
        }
    }

    private static async Task VerifyReceiveLeaseReleaseAsync()
    {
        using var lifetime = new CancellationTokenSource();
        using var transport = new ReceiveLeaseSocket();
        var socket = new DeviceMediaSocket(transport, lifetime);
        var buffer = Enumerable.Repeat((byte)0xA5, 64).ToArray();
        var receive = socket.ReceiveAsync(buffer, lifetime.Token);
        await transport.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        socket.Abort();
        var cleanup = DeviceMediaSocket.ReleaseReceiveAsync(receive, buffer);
        Check(!cleanup.IsCompleted && buffer.All(value => value == 0xA5),
            "production cleanup waits for actual receive memory lease after Abort before scrubbing");
        transport.Release.TrySetResult();
        await cleanup.WaitAsync(TimeSpan.FromSeconds(1));
        Check(transport.WroteAfterAbort && buffer.All(value => value == 0),
            "late receive write finishes before cleanup zeroes borrowed bytes");
        Array.Fill(buffer, (byte)0xA5);
        await DeviceMediaSocket.ReleaseReceiveAsync(null, buffer);
        Check(buffer.All(value => value == 0), "no pending receive still scrubs connection buffer");
    }

    private static async Task VerifyControlReplyCapacityAsync(Uri address, IServiceProvider services,
        ConcurrentQueue<ActiveProviderSocket> providers)
    {
        using var active = await OpenActiveAsync(address, services, providers);
        var store = services.GetRequiredService<ConversationStore>();
        var journal = services.GetRequiredService<OperationJournal>();
        var beforeCharge = journal.ReadSessionCharge(active.Conversation);
        using var lifetime = new CancellationTokenSource();
        using var publication = new PublicationSocket();
        publication.Release.TrySetResult();
        var sink = new DeviceMediaEventSink(new DeviceMediaSocket(publication, lifetime), lifetime, _ => { });
        sink.BindOperation(active.Operation);
        await sink.WriteAsync(new ConversationOperationBinding(active.Operation, 0), lifetime.Token);
        using var transport = new ReplyGateSocket();
        var replies = new DeviceMediaControlReplies(new DeviceMediaSocket(transport, lifetime), lifetime.Token);
        var completion = replies.Completion;
        var ledger = new DeviceMediaControlLedger();
        var admitted = 0;
        Guid dropped = default;
        byte[]? droppedBody = null;
        for (var index = 0; index < DeviceMediaControlLedger.Capacity; index++)
        {
            var request = Guid.NewGuid();
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            { type = "cancel", requestId = request, operationId = "stale-operation", responseGeneration = 0 });
            Check(!ledger.IsReplay(request, body), "new refused cancel gets its own bounded receipt");
            using var document = JsonDocument.Parse(body);
            try
            {
                DeviceMediaEndpoints.ApplyControl(document.RootElement, active.Session, sink, store, active.Device, active.Conversation, out _);
                throw new InvalidOperationException("Stale cancel unexpectedly applied.");
            }
            catch (DeviceMediaEndpoints.ControlRefusal refusal) { ledger.Complete(request, 409, refusal.Code); }
            var receipt = ledger.Result(request);
            if (replies.TryEnqueue(request, receipt.Status, receipt.Code)) { admitted++; }
            else if (dropped == Guid.Empty) { dropped = request; droppedBody = body; }
            if (index == 0) { await transport.FirstSend.Task.WaitAsync(TimeSpan.FromSeconds(1)); }
            Check(ReferenceEquals(completion, replies.Completion), "flood retains the same sole reply completion pump");
        }
        Check(admitted == 2 && dropped != Guid.Empty && replies.HasPending && !completion.IsCompleted,
            "blocked replies admit exactly one in-flight and one queued despite urgent cancel flood");
        var normalRequest = Guid.NewGuid();
        Check(replies.TryTakeCredit() && !replies.TryTakeCredit()
            && replies.TryEnqueue(normalRequest, 204, null, returnsCredit: true),
            "urgent flood cannot consume normal control credit or its reserved reply slot");
        Check(store.GetOperation(active.Device, active.Operation).Operation?.Status == "running"
            && journal.ReadSessionCharge(active.Conversation) == beforeCharge && !active.Provider.Aborted,
            "refused stale cancellation flood changes no journal, session or provider state");
        transport.Release.TrySetResult();
        await transport.TwoSent.Task.WaitAsync(TimeSpan.FromSeconds(1));
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            while (replies.HasPending) { await Task.Delay(1, timeout.Token); }
        }
        Check(ledger.IsReplay(dropped, droppedBody!), "unacknowledged urgent refusal retains exact retry identity");
        var recovered = ledger.Result(dropped);
        Check(recovered == (409, "media_cancel_mismatch") && replies.TryEnqueue(dropped, recovered.Status, recovered.Code),
            "exact retry recovers cached refusal after ACK capacity returns");
        replies.Complete();
        await completion.WaitAsync(TimeSpan.FromSeconds(1));
        Check(!replies.HasPending && transport.Sent.Count == 4
            && transport.Sent.Last().GetProperty("requestId").GetGuid() == dropped
            && transport.Sent.Single(item => item.GetProperty("requestId").GetGuid() == normalRequest).GetProperty("status").GetInt32() == 204
            && transport.Sent.Where(item => item.GetProperty("requestId").GetGuid() != normalRequest)
                .All(item => item.GetProperty("status").GetInt32() == 409),
            "single pump drains only admitted receipts and recovered exact refusal");
        await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", CancellationToken.None);
        await CheckReleasedAsync(active, services);
    }

    private static async Task VerifyClaimAdmissionFencesAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<ConversationStore>();
        foreach (var outcome in new[] { "success", "unknown", "known-refusal" })
        {
            var device = "claim-fence-" + Guid.NewGuid().ToString("N");
            var boot = Guid.NewGuid();
            var create = new CreateConversationRequest(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false), MaximumEpoch);
            var created = store.Create(device, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(create, RealtimeProtocol.JsonOptions), create);
            Check(created.Status == 201, "claim fixture creates actual journal conversation");
            var conversation = created.Response!.ConversationId;
            var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1, MaximumEpoch);
            var admission = store.BeginRealtimeTurn(device, conversation, Guid.NewGuid(),
                JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions), request, true,
                CancellationToken.None, CancellationToken.None, handsFree: true);
            var call = admission.Call ?? throw new InvalidOperationException("Claim fixture admission failed: " + admission.Code);
            Check(store.StartRealtimeTurn(call), "claim fixture starts actual operation A");
            using var lifetime = new CancellationTokenSource();
            using var transport = new PublicationSocket();
            transport.Release.TrySetResult();
            var sink = new DeviceMediaEventSink(new DeviceMediaSocket(transport, lifetime), lifetime, _ => { });
            sink.BindOperation(call.OperationId);
            await sink.WriteAsync(new ConversationOperationBinding(call.OperationId, 0), lifetime.Token);
            using var session = new RealtimeLiveSession(device, conversation, new ForbiddenInput(), lifetime);
            var claim = sink.TryClaimCancellation(call.OperationId, 0, () => session.OutputGeneration);
            Check(claim is not null, "published exact operation admits one opaque cancellation claim");
            Check(sink.TryClaimCancellation(call.OperationId, 0, () => 0) is null, "claimed operation cannot be claimed twice");
            Reject(() => sink.BindOperation("premature-operation-B"), "bind during durable claim fails closed");
            Check(store.CompleteRealtimeTurn(call, new("", "Jak ti rikaji?", "ask", "", null, 0)) is not null,
                "operation A can complete while cancellation claim is paused before durable mutation");
            store.ReleaseRealtimeTurn(call, dispatched: true, completed: true);
            var playback = session.PreparePlayback(call.OperationId);
            Check(session.Played(call.OperationId, 0) && playback.IsCompletedSuccessfully, "operation A playback drains before attempted next admission");
            var admittedB = false;
            RealtimeCall? callB = null;
            async Task AdmitNextAsync()
            {
                await sink.BeforeNextOperationAsync(lifetime.Token);
                admittedB = true;
                var next = request with { ClientTurnId = Guid.NewGuid(), Opening = false };
                callB = store.BeginRealtimeTurn(device, conversation, Guid.NewGuid(),
                    JsonSerializer.SerializeToUtf8Bytes(next, RealtimeProtocol.JsonOptions), next, true,
                    lifetime.Token, CancellationToken.None, handsFree: true).Call;
                Check(callB is not null && store.StartRealtimeTurn(callB), "operation B can start only after admission fence releases");
            }
            var nextAdmission = AdmitNextAsync();
            using var writer = new SemaphoreSlim(1);
            async Task<long> AdvanceUnderWriterAsync()
            {
                await writer.WaitAsync();
                try { return await sink.AdvanceOutputAsync(session.CancelOutput, lifetime.Token); }
                finally { writer.Release(); }
            }
            var advance = AdvanceUnderWriterAsync();
            Check(!nextAdmission.IsCompleted && !advance.IsCompleted && !admittedB && writer.CurrentCount == 0,
                "claim holds next admission and output transition while output writer is occupied");
            if (outcome == "success")
            {
                Check(store.CancelOperation(device, call.OperationId, Guid.NewGuid()).Status == 204,
                    "durable cancellation settles without ACK or output writer acquisition");
            }
            else if (outcome == "known-refusal")
            {
                Check(store.CancelOperation("wrong-device", call.OperationId, Guid.NewGuid()).Status == 403,
                    "known durable refusal has no operation mutation");
            }
            sink.CompleteCancellation(claim!, close: outcome != "known-refusal");
            if (outcome != "known-refusal")
            {
                await ExpectCancellationAsync(nextAdmission.WaitAsync(TimeSpan.FromSeconds(1)));
                await ExpectCancellationAsync(advance.WaitAsync(TimeSpan.FromSeconds(1)));
                Check(!admittedB && callB is null && writer.CurrentCount == 1,
                    "success or unknown durable outcome closes admission with no B provider callback and no writer deadlock");
            }
            else
            {
                await nextAdmission.WaitAsync(TimeSpan.FromSeconds(1));
                await advance.WaitAsync(TimeSpan.FromSeconds(1));
                Check(admittedB && callB is not null && !sink.HasPublishedOperation
                    && sink.TryClaimCancellation(call.OperationId, 0, () => session.OutputGeneration) is null,
                    "known refusal releases next admission but retires A before B publication");
                sink.BindOperation(callB!.OperationId);
                Check(!sink.HasPublishedOperation, "admitted B has no cancellation authority before publication");
                await sink.WriteAsync(new ConversationOperationBinding(callB.OperationId, session.OutputGeneration), lifetime.Token);
                Check(sink.MatchesOperation(callB.OperationId, 1)
                    && sink.TryClaimCancellation(call.OperationId, 0, () => session.OutputGeneration) is null
                    && store.GetOperation(device, callB.OperationId).Operation?.Status == "running",
                    "published B remains running when stale A cancellation cannot claim authority");
                store.ReleaseRealtimeTurn(callB, dispatched: true, completed: false);
            }
        }
    }

    private static async Task VerifyRealSocketsAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "device-media-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clock = new MediaClock();
        var providers = new ConcurrentQueue<ActiveProviderSocket>();
        var liveProviders = new ConcurrentQueue<LiveProviderSocket>();
        var socketGates = new ConcurrentDictionary<Guid, WireGate>();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // Clear inherited provider configuration: this fixture never makes paid calls.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "isolated.sqlite"),
            ["Backend:Memory:Enabled"] = "false"
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton<DeviceAccessStore>();
        builder.Services.AddSingleton<OperationJournal>();
        builder.Services.AddSingleton(services => new ConversationStore(clock,
            services.GetRequiredService<OperationJournal>(), configuration: builder.Configuration));
        builder.Services.AddSingleton(new OpenAiRealtimeClient(_ =>
        {
            var socket = new ActiveProviderSocket();
            providers.Enqueue(socket);
            return Task.FromResult<WebSocket>(socket);
        }));
        var liveConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OPENAI_API_KEY"] = "offline-device-media-fixture",
            ["Backend:GptLive:InputQuietMs"] = "500",
            ["Backend:GptLive:OutputPauseMs"] = "500",
            ["Backend:GptLive:TerminalQuietMs"] = "1000"
        }).Build();
        builder.Services.AddSingleton(new GptLiveClient(liveConfiguration, _ =>
        {
            var socket = new LiveProviderSocket();
            liveProviders.Enqueue(socket);
            return Task.FromResult<WebSocket>(socket);
        }));
        builder.Services.AddSingleton<RealtimeLiveRegistry>();
        builder.Services.AddSingleton<WandDecisionService>();
        builder.Services.AddSingleton<EveningMemoryStore>();
        builder.Services.AddSingleton(services => HybridMemoryOptions.Read(builder.Configuration,
            services.GetRequiredService<OperationJournal>()));
        builder.Services.AddSingleton<OpenAiVectorMemoryProvider>();
        builder.Services.AddSingleton<HybridMemoryService>();
        try
        {
            await using var app = builder.Build();
            app.UseWebSockets();
            app.Use(async (context, next) =>
            {
                if (Guid.TryParse(context.Request.Headers["X-Device-Media-Test-Gate"], out var gateId)
                    && socketGates.TryGetValue(gateId, out var gate))
                {
                    var feature = context.Features.Get<IHttpWebSocketFeature>()!;
                    context.Features.Set<IHttpWebSocketFeature>(new GatedWebSocketFeature(feature, gate));
                }
                if (context.Request.Headers.ContainsKey("Cookie"))
                {
                    context.Items["dv-account-device"] = "media-device";
                }
                try { await next(context); }
                finally
                {
                    if (Guid.TryParse(context.Request.Headers["X-Device-Media-Test-Gate"], out var finishedId)
                        && socketGates.TryGetValue(finishedId, out var finished)) { finished.RequestEnded.TrySetResult(); }
                }
            });
            app.MapDeviceMedia();
            app.MapGet("/fixture-media", ServeFramesAsync);
            await app.StartAsync();
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var access = app.Services.GetRequiredService<DeviceAccessStore>();
            await VerifyScopedControlsAsync(app.Services.GetRequiredService<ConversationStore>());
            var token = access.IssueToken("media-device")!.AccessToken;
            const string route = "/v1/conversations/fixture/device-media";
            await CheckAdmissionAsync(http, route, null, null, false, 401, "access_token_required");
            await CheckAdmissionAsync(http, route, null, null, true, 401, "access_token_required");
            await CheckAdmissionAsync(http, route, "invalid", Guid.NewGuid().ToString("D"), true, 401, "access_token_required");
            await CheckAdmissionAsync(http, route, token, null, false, 400, "invalid_idempotency_key");
            await CheckAdmissionAsync(http, route, token, Guid.Empty.ToString("D"), false, 400, "invalid_idempotency_key");
            await CheckAdmissionAsync(http, route, token, Guid.NewGuid().ToString("D"), false, 400, "device_media_protocol_required");
            await VerifyHandshakeRejectedAsync(http.BaseAddress!, route, token);
            foreach (var payload in new[] { "{", "{\"type\":\"open\"}", new string('x', 8193) })
            {
                using var client = await ConnectAsync(http.BaseAddress!, route, token);
                await client.SendAsync(Encoding.UTF8.GetBytes(payload).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None);
                await CheckDisconnectedAsync(client);
            }
            using (var client = await ConnectAsync(http.BaseAddress!, route, token))
            {
                await client.SendAsync(InputFrame(1, 0, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
                await CheckDisconnectedAsync(client);
            }
            foreach (var engine in new[] { "gpt-live", "realtime" })
            {
                using var client = await ConnectAsync(http.BaseAddress!, route, token);
                var bytes = Encoding.UTF8.GetBytes(Opening(engine));
                await client.SendAsync(bytes.AsMemory(0, 11), WebSocketMessageType.Text, false, CancellationToken.None);
                await client.SendAsync(bytes.AsMemory(11), WebSocketMessageType.Text, true, CancellationToken.None);
                var failure = await ReadAsync(client);
                using var json = JsonDocument.Parse(failure.Bytes);
                Check(failure.Kind == WebSocketMessageType.Text && json.RootElement.GetProperty("type").GetString() == "failed"
                    && json.RootElement.TryGetProperty("code", out _), "post-upgrade admission failure is socket event for " + engine);
                await CheckDisconnectedAsync(client);
            }
            await VerifyOrderedFramesAsync(http.BaseAddress!);
            await VerifyOpeningExpiryAtReceiveAsync(http.BaseAddress!, app.Services, socketGates, providers, clock);
            token = access.IssueToken("media-device")!.AccessToken;
            await VerifyAckVisibilityAsync(http.BaseAddress!, app.Services, socketGates, providers);
            await VerifyPrimaryLiveAsync(http.BaseAddress!, app.Services, liveProviders);
            await VerifyControlReplyCapacityAsync(http.BaseAddress!, app.Services, providers);
            await VerifyClaimAdmissionFencesAsync(app.Services);
            await VerifyPlayedReplyFencesAsync(app.Services);
            await VerifyMappedPlayedReplyAsync(http.BaseAddress!, app.Services, socketGates, providers);
            await VerifyMappedTerminalRepliesAsync(http.BaseAddress!, app.Services, socketGates, providers, liveProviders);
            await VerifyActiveProviderAsync(http.BaseAddress!, app.Services, providers, clock);
            token = access.IssueToken("media-device")!.AccessToken;
            using (var client = await ConnectAsync(http.BaseAddress!, route, token))
            {
                clock.Advance(TimeSpan.FromMinutes(16));
                await CheckDisconnectedAsync(client);
            }
            await CheckAdmissionAsync(http, route, token, Guid.NewGuid().ToString("D"), false, 401, "access_token_required");
            await app.StopAsync();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }

    private static async Task ServeFramesAsync(HttpContext context)
    {
        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(DeviceMediaProtocol.Subprotocol);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        var socket = new DeviceMediaSocket(webSocket, lifetime);
        var sink = new DeviceMediaEventSink(socket, lifetime, _ => { });
        var input = Guid.NewGuid();
        try
        {
            await sink.WriteAsync(new { type = "listening", inputId = input }, lifetime.Token);
            var buffer = new byte[8193];
            var frame = await socket.ReceiveAsync(buffer, lifetime.Token);
            var header = DeviceMediaProtocol.ReadPcmHeader(buffer.AsSpan(0, frame.Count));
            Check(frame.Kind == WebSocketMessageType.Binary && sink.AdmitInput(header.Stream, header.Sequence) == (input, 0),
                "fragmented input reassembles before sequence admission");
            Reject(() => sink.AdmitInput(header.Stream, 0), "duplicate input sequence rejected");
            Reject(() => sink.AdmitInput(header.Stream, 2), "out of order input rejected");
            await sink.WriteAsync(new { type = "speech_stopped" }, lifetime.Token);
            await sink.WriteAsync(new { type = "listening", inputId = input }, lifetime.Token);
            Check(sink.AdmitInput(header.Stream, 1) == (input, 1), "repeated listener retains exact stream sequence");
            var nextInput = Guid.NewGuid();
            await sink.WriteAsync(new { type = "listening", inputId = nextInput }, lifetime.Token);
            Check(sink.AdmitInput(header.Stream, 2) == (input, 2), "late frame retains retired input identity");
            Reject(() => sink.AdmitInput(header.Stream, 2), "retired stream duplicate rejected");
            await sink.WriteAsync(new { type = "audio", responseGeneration = 3L,
                delta = Convert.ToBase64String(Enumerable.Range(0, 9000).Select(index => (byte)index).ToArray()) }, lifetime.Token);
            await sink.WriteAsync(new { type = "audio_cancel", responseGeneration = 4L }, lifetime.Token);
            await sink.WriteAsync(new { type = "audio", responseGeneration = 4L, delta = "AQI=" }, lifetime.Token);
            await sink.WriteAsync(new { type = "closed" }, lifetime.Token);
            using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", closeDeadline.Token);
        }
        finally { socket.Abort(); }
    }

    private static async Task VerifyOpeningExpiryAtReceiveAsync(Uri address, IServiceProvider services,
        ConcurrentDictionary<Guid, WireGate> gates, ConcurrentQueue<ActiveProviderSocket> providers, MediaClock clock)
    {
        var gateId = Guid.NewGuid();
        var gate = new WireGate { OpeningReceived = () => clock.Advance(TimeSpan.FromMinutes(2)) };
        gates[gateId] = gate;
        var device = "open-expiry-" + Guid.NewGuid().ToString("N");
        var boot = Guid.NewGuid();
        var token = services.GetRequiredService<DeviceAccessStore>().IssueToken(device)!.AccessToken;
        // Keep the conversation fresh independently of the older bearer. An
        // expired conversation would mask the missing post-upgrade auth check.
        clock.Advance(TimeSpan.FromMinutes(14));
        var create = new CreateConversationRequest(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false), MaximumEpoch);
        var store = services.GetRequiredService<ConversationStore>();
        var created = store.Create(device, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(create, RealtimeProtocol.JsonOptions), create);
        Check(created.Status == 201, "opening expiry fixture creates isolated conversation");
        var conversation = created.Response!.ConversationId;
        using var client = await ConnectAsync(address, "/v1/conversations/" + conversation + "/device-media", token, gateId);
        var opening = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1, MaximumEpoch);
        await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new { type = "open", engine = "realtime", request = opening }, RealtimeProtocol.JsonOptions));
        await CheckDisconnectedAsync(client);
        Check(gate.OpeningApplied && created.Response.ExpiresAt > clock.GetUtcNow()
            && providers.IsEmpty && CountOperationRows(services, conversation) == 0,
            "bearer expiring exactly at opening receive cannot admit journal operation or provider before watcher tick");
        gates.TryRemove(gateId, out _);
    }

    private static async Task VerifyPlayedReplyFencesAsync(IServiceProvider services)
    {
        var store = services.GetRequiredService<ConversationStore>();
        foreach (var ending in new[] { "delivered", "unknown", "cancel" })
        {
            var device = "played-fence-" + Guid.NewGuid().ToString("N");
            var boot = Guid.NewGuid();
            var create = new CreateConversationRequest(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false), MaximumEpoch);
            var created = store.Create(device, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(create, RealtimeProtocol.JsonOptions), create);
            Check(created.Status == 201, "played fence fixture creates actual conversation");
            var conversation = created.Response!.ConversationId;
            var request = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1, MaximumEpoch);
            var call = store.BeginRealtimeTurn(device, conversation, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions),
                request, true, CancellationToken.None, CancellationToken.None, handsFree: true).Call
                ?? throw new InvalidOperationException("Played fixture admission failed.");
            Check(store.StartRealtimeTurn(call)
                && store.CompleteRealtimeTurn(call, new("", "Jak ti rikaji?", "ask", "", null, 0)) is not null,
                "played fence fixture completes actual operation A");
            store.ReleaseRealtimeTurn(call, dispatched: true, completed: true);
            using var lifetime = new CancellationTokenSource();
            using var transport = new PublicationSocket(); transport.Release.TrySetResult();
            var sink = new DeviceMediaEventSink(new DeviceMediaSocket(transport, lifetime), lifetime, _ => { });
            sink.BindOperation(call.OperationId);
            await sink.WriteAsync(new ConversationOperationBinding(call.OperationId, 0), lifetime.Token);
            using var session = new RealtimeLiveSession(device, conversation, new ForbiddenInput(), lifetime);
            var drained = session.PreparePlayback(call.OperationId);
            var requestId = Guid.NewGuid();
            var body = JsonSerializer.SerializeToUtf8Bytes(new { type = "played", requestId, operationId = call.OperationId, responseGeneration = 0 });
            var ledger = new DeviceMediaControlLedger();
            Check(!ledger.IsReplay(requestId, body), "first played receipt has unique exact envelope");
            using var document = JsonDocument.Parse(body);
            Check(!DeviceMediaEndpoints.ApplyControl(document.RootElement, session, sink, store, device, conversation, out var published)
                && published is not null && drained.IsCompletedSuccessfully, "played applies RAM drain only after installing reply publication claim");
            ledger.Complete(requestId, 204);
            Check(ledger.IsReplay(requestId, body) && sink.ClaimPlayedReply(call.OperationId, 0, Guid.NewGuid(), () => 0) is null,
                "exact ledger retry does not reapply played or install another release barrier");
            var admittedB = false;
            RealtimeCall? callB = null;
            async Task AdmitBAsync()
            {
                await sink.BeforeNextOperationAsync(lifetime.Token);
                admittedB = true;
                var next = request with { Opening = false, ClientTurnId = Guid.NewGuid() };
                callB = store.BeginRealtimeTurn(device, conversation, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(next, RealtimeProtocol.JsonOptions),
                    next, true, lifetime.Token, CancellationToken.None, handsFree: true).Call;
                Check(callB is not null && store.StartRealtimeTurn(callB), "next actual operation starts after exact played ACK publication");
            }
            var nextAdmission = AdmitBAsync();
            var terminal = sink.WriteAsync(new { type = "closed", reason = "conversation_terminal", responseGeneration = 0 }, lifetime.Token);
            Check(!terminal.IsCompleted && !transport.SentTypes.Contains("closed"),
                "terminal event waits for exact played publication before entering an otherwise free socket writer");
            Check(!nextAdmission.IsCompleted && !admittedB && CountOperationRows(services, conversation) == 1,
                "completed drained A cannot admit journal/provider B while exact played ACK is held");
            if (ending == "cancel")
            {
                var cancellation = sink.TryClaimCancellation(call.OperationId, 0, () => 0);
                Check(cancellation is not null && store.CancelOperation(device, call.OperationId, Guid.NewGuid()).Status == 204,
                    "urgent current cancellation owns store mutation independently of blocked played ACK");
                sink.CompleteCancellation(cancellation!, close: true);
                await ExpectCancellationAsync(nextAdmission.WaitAsync(TimeSpan.FromSeconds(1)));
                await ExpectCancellationAsync(terminal.WaitAsync(TimeSpan.FromSeconds(1)));
                published!(true); // A late socket continuation cannot reopen the settled cancellation.
            }
            else
            {
                published!(ending == "delivered");
                if (ending == "delivered")
                {
                    await nextAdmission.WaitAsync(TimeSpan.FromSeconds(1));
                    await terminal.WaitAsync(TimeSpan.FromSeconds(1));
                    Check(transport.SentTypes.Count(type => type == "closed") == 1, "successful exact publication releases one terminal event");
                    store.ReleaseRealtimeTurn(callB!, dispatched: true, completed: false);
                }
                else
                {
                    await ExpectCancellationAsync(nextAdmission.WaitAsync(TimeSpan.FromSeconds(1)));
                    await ExpectCancellationAsync(terminal.WaitAsync(TimeSpan.FromSeconds(1)));
                }
            }
            Check(admittedB == (ending == "delivered") && CountOperationRows(services, conversation) == (ending == "delivered" ? 2 : 1),
                "only delivered exact played ACK permits next durable operation; unknown/cancel remain closed");
            Check(ending == "delivered" || !transport.SentTypes.Contains("closed"),
                "unknown publication or urgent cancellation cannot publish stale terminal success");
        }
    }

    private static async Task VerifyMappedPlayedReplyAsync(Uri address, IServiceProvider services,
        ConcurrentDictionary<Guid, WireGate> gates, ConcurrentQueue<ActiveProviderSocket> providers)
    {
        foreach (var cancel in new[] { false, true })
        {
            var gateId = Guid.NewGuid();
            var gate = new WireGate(); gates[gateId] = gate;
            using var active = await OpenActiveAsync(address, services, providers, gateId);
            active.Provider.CompleteResponse();
            await active.Inbox.JsonAsync("completed");
            var requestId = Guid.NewGuid();
            gate.AckType = "control.ack"; gate.AckRequest = requestId;
            var played = JsonSerializer.SerializeToUtf8Bytes(new { type = "played", requestId, operationId = active.Operation, responseGeneration = 0 });
            await SendControlAsync(active.Client, played);
            var ack = await active.Inbox.JsonAsync("control.ack");
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(ack.GetProperty("requestId").GetGuid() == requestId && CountOperationRows(services, active.Conversation) == 1,
                "real played ACK bytes are visible while next operation remains fenced behind send completion");
            if (cancel)
            {
                await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
                { type = "cancel", requestId = Guid.NewGuid(), operationId = active.Operation, responseGeneration = 0 }));
                await CheckTargetedCancellationAsync(() => ReadAsync(active.Client),
                    () => active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture cancellation", CancellationToken.None), "blocked played ACK");
                await CheckReleasedAsync(active, services);
                Check(CountOperationRows(services, active.Conversation) == 1
                    && services.GetRequiredService<OperationJournal>().GetOperation(active.Device, active.Operation).Operation?.Status == "cancelled",
                    "urgent mapped cancellation closes played waiter and cannot start B while ACK send is blocked");
            }
            else
            {
                gate.Release.TrySetResult();
                await active.Inbox.JsonAsync("media.operation");
                await active.Inbox.JsonAsync("media.input");
                await active.Inbox.JsonAsync("listening");
                Check(CountOperationRows(services, active.Conversation) == 2, "actual B admission follows exact played ACK publication");
                await SendControlAsync(active.Client, played);
                var replay = await active.Inbox.JsonAsync("control.ack");
                Check(replay.GetProperty("requestId").GetGuid() == requestId && replay.GetProperty("status").GetInt32() == 204,
                    "exact old played retry returns cached success without another operation-bound reply claim");
                await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", CancellationToken.None);
                await CheckReleasedAsync(active, services);
            }
            gates.TryRemove(gateId, out _);
        }
        using (var active = await OpenActiveAsync(address, services, providers))
        {
            await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "played", requestId = Guid.NewGuid(), operationId = active.Operation, responseGeneration = 0 }));
            var refused = await active.Inbox.JsonAsync("control.ack");
            Check(refused.GetProperty("status").GetInt32() == 409 && refused.GetProperty("code").GetString() == "media_playback_mismatch",
                "valid but premature played receives typed refusal while original operation stays live");
            await BeginInputAsync(active); // The refused attempt must leave no stuck release claim.
            await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", CancellationToken.None);
            await CheckReleasedAsync(active, services);
        }
    }

    private static long CountOperationRows(IServiceProvider services, string conversation)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = configuration["Backend:DatabasePath"] }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM turn_operations WHERE conversation_id = $conversation";
        command.Parameters.AddWithValue("$conversation", conversation);
        return (long)command.ExecuteScalar()!;
    }

    private static string ReadJournalStatus(IServiceProvider services, string operation)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = services.GetRequiredService<IConfiguration>()["Backend:DatabasePath"] }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM turn_operations WHERE operation_id = $operation";
        command.Parameters.AddWithValue("$operation", operation);
        return (string)command.ExecuteScalar()!;
    }

    private static async Task VerifyMappedTerminalRepliesAsync(Uri address, IServiceProvider services,
        ConcurrentDictionary<Guid, WireGate> gates, ConcurrentQueue<ActiveProviderSocket> realtimeProviders,
        ConcurrentQueue<LiveProviderSocket> liveProviders)
    {
        foreach (var engine in new[] { "gpt-live", "realtime" })
        foreach (var ending in new[] { "delivered", "cancel", "unknown" })
        {
            Console.WriteLine($"Device media terminal fixture: {engine}/{ending}.");
            var gateId = Guid.NewGuid();
            var gate = new WireGate(); gates[gateId] = gate;
            var device = "terminal-media-" + Guid.NewGuid().ToString("N");
            var boot = Guid.NewGuid();
            var store = services.GetRequiredService<ConversationStore>();
            var create = new CreateConversationRequest(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false), MaximumEpoch);
            var created = store.Create(device, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(create, RealtimeProtocol.JsonOptions), create);
            Check(created.Status == 201, "terminal fixture creates actual owned conversation");
            var conversation = created.Response!.ConversationId;
            var bearer = services.GetRequiredService<DeviceAccessStore>().IssueToken(device)!.AccessToken;
            using var client = await ConnectAsync(address, "/v1/conversations/" + conversation + "/device-media", bearer, gateId);
            var opening = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, engine == "gpt-live" ? 0 : 63,
                7, PartyWandCatalog.Version, true, "", null, "marin", 1, MaximumEpoch);
            await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new { type = "open", engine, request = opening }, RealtimeProtocol.JsonOptions));
            var inbox = new MediaInbox(client);
            var sessionEvent = await inbox.JsonAsync("session");
            var started = await inbox.JsonAsync("started");
            await inbox.JsonAsync("media.operation");
            var operation = started.GetProperty("operationId").GetString()!;
            Task providerEnded;
            if (engine == "gpt-live")
            {
                await inbox.JsonAsync("media.input");
                await inbox.JsonAsync("listening");
                Check(liveProviders.TryDequeue(out var provider), "terminal Live uses local protocol override");
                provider!.EmitTerminalSpeech();
                await inbox.JsonAsync("media.output");
                await inbox.BinaryAsync();
                var checkpoint = await inbox.JsonAsync("playback_checkpoint");
                var checkpointRequest = Guid.NewGuid();
                await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    type = "playback", requestId = checkpointRequest,
                    playbackId = checkpoint.GetProperty("playbackId").GetGuid(),
                    pcmBytes = checkpoint.GetProperty("pcmBytes").GetInt32(), drained = true
                }));
                var checkpointAck = await inbox.JsonAsync("control.ack");
                Check(checkpointAck.GetProperty("requestId").GetGuid() == checkpointRequest
                    && checkpointAck.GetProperty("status").GetInt32() == 204, "terminal Live exact checkpoint drain accepted");
                providerEnded = provider.Ended.Task;
            }
            else
            {
                Check(realtimeProviders.TryDequeue(out var provider), "terminal Realtime uses local SDK protocol override");
                await inbox.JsonAsync("media.output");
                await inbox.BinaryAsync();
                provider!.CompleteTerminalResponse();
                providerEnded = provider.Ended.Task;
            }
            var completed = await inbox.JsonAsync("completed");
            Check(completed.GetProperty("operationId").GetString() == operation
                && completed.GetProperty("result").GetProperty("decision").GetString() != "ask",
                "actual provider/domain terminal result precedes played acknowledgement");
            var requestId = Guid.NewGuid();
            gate.AckType = "control.ack"; gate.AckRequest = requestId; gate.ObserveRequest = requestId;
            await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "played", requestId, operationId = operation, responseGeneration = 0 }));
            var ack = await inbox.JsonAsync("control.ack");
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await gate.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(ack.GetProperty("requestId").GetGuid() == requestId && ack.GetProperty("status").GetInt32() == 204
                && gate.TerminalEvents == 0 && gate.CloseCalls == 0 && !gate.RequestEnded.Task.IsCompleted,
                "terminal ACK bytes visible while actual send continuation and endpoint closure remain held");
            if (ending == "delivered")
            {
                gate.Release.TrySetResult();
                var closed = await inbox.JsonAsync("closed");
                Check(closed.GetProperty("reason").GetString() == "conversation_terminal", "terminal success follows exact played publication");
                var close = await ReadAsync(client);
                Check(close.Kind == WebSocketMessageType.Close, "terminal JSON followed directly by WebSocket close");
                await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "terminal fixture complete", CancellationToken.None);
            }
            else if (ending == "cancel")
            {
                await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new
                { type = "cancel", requestId = Guid.NewGuid(), operationId = operation, responseGeneration = 0 }));
                await CheckTargetedCancellationAsync(() => ReadAsync(client),
                    () => client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "terminal fixture cancelled", CancellationToken.None),
                    engine + " terminal blocked ACK");
                Check(!gate.Release.Task.IsCompleted, "urgent current cancellation settles independently of held ACK writer");
            }
            else
            {
                gate.Release.TrySetException(new WebSocketException("Synthetic unknown publication outcome."));
                await CheckDisconnectedAsync(client);
            }
            await gate.RequestEnded.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await providerEnded.WaitAsync(TimeSpan.FromSeconds(3));
            var registry = services.GetRequiredService<RealtimeLiveRegistry>();
            Check(registry.Find(sessionEvent.GetProperty("streamId").GetGuid(), device, conversation) is null
                && CountOperationRows(services, conversation) == 1 && liveProviders.IsEmpty && realtimeProviders.IsEmpty,
                "terminal acknowledgement outcome releases actual provider and registry without another admission");
            Check(gate.TerminalEvents == (ending == "delivered" ? 1 : 0)
                && gate.CloseCalls == (ending == "delivered" ? 1 : 0), "terminal publishes exactly once on success and never after unknown/cancel");
            var recorded = store.GetOperation(device, operation).Operation;
            Check(recorded?.Status == (ending == "cancel" ? "cancelled" : "completed")
                && ReadJournalStatus(services, operation) == (ending == "cancel" ? "cancelled" : "completed")
                && recorded.Correlation.OperationEpoch == MaximumEpoch, "terminal cleanup retains actual original journal outcome and epoch");
            gates.TryRemove(gateId, out _);
        }
    }

    private static async Task VerifyAckVisibilityAsync(Uri address, IServiceProvider services,
        ConcurrentDictionary<Guid, WireGate> gates, ConcurrentQueue<ActiveProviderSocket> providers)
    {
        var pcmGateId = Guid.NewGuid();
        var pcmGate = new WireGate { AckType = "media.ack", ObserveBinarySequence = 1 };
        gates[pcmGateId] = pcmGate;
        using (var active = await OpenActiveAsync(address, services, providers, pcmGateId))
        {
            var stream = await BeginInputAsync(active);
            await active.Client.SendAsync(InputFrame(stream, 0, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await active.Inbox.JsonAsync("media.ack");
            await pcmGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await active.Client.SendAsync(InputFrame(stream, 1, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await pcmGate.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(active.Provider.InputBytes == 4 && !active.Provider.Aborted,
                "real receive loop admits next credited PCM while prior ACK send continuation is held");
            pcmGate.Release.TrySetResult();
            var ack = await active.Inbox.JsonAsync("media.ack");
            Check(ack.GetProperty("sequence").GetUInt32() == 1 && active.Provider.InputBytes == 8,
                "queued second PCM reaches actual provider once after first ACK send returns");
            await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", CancellationToken.None);
            await CheckReleasedAsync(active, services);
        }
        gates.TryRemove(pcmGateId, out _);

        var controlGateId = Guid.NewGuid();
        var controlGate = new WireGate();
        gates[controlGateId] = controlGate;
        using (var active = await OpenActiveAsync(address, services, providers, controlGateId))
        {
            await BeginInputAsync(active);
            var playbackId = Guid.NewGuid();
            var checkpoint = active.Session.PrepareCheckpoint(new(playbackId, 42, 1));
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            controlGate.AckType = "control.ack"; controlGate.AckRequest = first; controlGate.ObserveRequest = second;
            await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "playback", requestId = first, playbackId, pcmBytes = 42, drained = true }));
            var firstAck = await active.Inbox.JsonAsync("control.ack");
            await controlGate.Held.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(firstAck.GetProperty("status").GetInt32() == 204 && checkpoint.IsCompletedSuccessfully,
                "first normal control applies before visible ACK");
            await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "availability", requestId = second, bootId = active.Boot, generation = 1,
                readyMask = 63, deviceRevision = 7, operationEpoch = MaximumEpoch }));
            await controlGate.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Check(!active.Provider.Aborted, "normal next control admitted before preceding visible ACK send continuation completes");
            controlGate.Release.TrySetResult();
            var secondAck = await active.Inbox.JsonAsync("control.ack");
            Check(secondAck.GetProperty("requestId").GetGuid() == second && secondAck.GetProperty("status").GetInt32() == 204,
                "actual mapped next control retains its reserved normal reply and applied outcome");
            await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", CancellationToken.None);
            await CheckReleasedAsync(active, services);
        }
        gates.TryRemove(controlGateId, out _);
        using (var active = await OpenActiveAsync(address, services, providers))
        {
            var stream = await BeginInputAsync(active);
            active.Provider.StallInput = true;
            await active.Client.SendAsync(InputFrame(stream, 0, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await active.Provider.InputStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await active.Client.SendAsync(InputFrame(stream, 1, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await CheckDisconnectedAsync(active.Client);
            await CheckReleasedAsync(active, services);
            Check(active.Provider.InputBytes == 0, "malicious next PCM before any ACK credit still closes before provider acceptance");
        }
    }

    private static async Task VerifyScopedControlsAsync(ConversationStore conversations)
    {
        using var lifetime = new CancellationTokenSource();
        using var transport = new PublicationSocket();
        transport.Release.TrySetResult();
        var sink = new DeviceMediaEventSink(new DeviceMediaSocket(transport, lifetime), lifetime, _ => { });
        sink.BindOperation("current-operation");
        await sink.WriteAsync(new ConversationOperationBinding("current-operation", 0), lifetime.Token);
        using var session = new RealtimeLiveSession("media-device", "fixture", new ForbiddenInput(), lifetime);
        var playbackId = Guid.NewGuid();
        var checkpoint = session.PrepareCheckpoint(new(playbackId, 42, 1));
        bool Apply(object control)
        {
            using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(control));
            var close = DeviceMediaEndpoints.ApplyControl(document.RootElement, session, sink, conversations, "media-device", "fixture", out var published);
            published?.Invoke(true); // This unit explicitly models successful exact ACK publication.
            return close;
        }
        Reject(() => Apply(new { type = "playback", requestId = Guid.NewGuid(), playbackId, pcmBytes = 40, drained = true }),
            "playback PCM count must match issued checkpoint");
        Reject(() => Apply(new { type = "playback", requestId = Guid.NewGuid(), playbackId, pcmBytes = 42, drained = false }),
            "undrained playback cannot confirm presentation");
        Check(!checkpoint.IsCompleted, "invalid playback leaves checkpoint pending");
        Check(!Apply(new { type = "playback", requestId = Guid.NewGuid(), playbackId, pcmBytes = 42, drained = true })
            && checkpoint.IsCompletedSuccessfully, "exact drained checkpoint applies without closing session");
        var played = session.PreparePlayback("current-operation");
        Reject(() => Apply(new { type = "played", requestId = Guid.NewGuid(), operationId = "other-operation", responseGeneration = 0 }),
            "played cannot acknowledge another operation");
        Reject(() => Apply(new { type = "played", requestId = Guid.NewGuid(), operationId = "current-operation", responseGeneration = 1 }),
            "played cannot acknowledge another generation");
        Check(!played.IsCompleted, "mismatched played leaves operation pending");
        Check(!Apply(new { type = "played", requestId = Guid.NewGuid(), operationId = "current-operation", responseGeneration = 0 })
            && played.IsCompletedSuccessfully, "exact operation and generation release playback barrier");
        Reject(() => Apply(new { type = "cancel", requestId = Guid.NewGuid(), operationId = "other-operation", responseGeneration = 0 }),
            "unscoped cancellation cannot reach conversation cancellation");
        Reject(() => Apply(new { type = "availability", requestId = Guid.NewGuid(), bootId = Guid.NewGuid(), generation = 1,
            readyMask = 63, deviceRevision = 7, operationEpoch = "01" }), "control rejects noncanonical epoch");
        Reject(() => Apply(new { type = "played", requestId = Guid.NewGuid(), operationId = "current-operation", responseGeneration = 0,
            extra = true }), "control rejects extra fields");
    }

    private static async Task VerifyActiveProviderAsync(Uri address, IServiceProvider services,
        ConcurrentQueue<ActiveProviderSocket> providers, MediaClock clock)
    {
        var journal = services.GetRequiredService<OperationJournal>();
        using (var active = await OpenActiveAsync(address, services, providers))
        {
            var input = await BeginInputAsync(active);
            var pcm = InputFrame(input, 0, 4);
            await active.Client.SendAsync(pcm.AsMemory(0, 7), WebSocketMessageType.Binary, false, CancellationToken.None);
            await active.Client.SendAsync(pcm.AsMemory(7), WebSocketMessageType.Binary, true, CancellationToken.None);
            var ack = await active.Inbox.JsonAsync("media.ack");
            Check(ack.GetProperty("stream").GetUInt32() == input && ack.GetProperty("sequence").GetUInt32() == 0
                && active.Provider.InputBytes == 4, "actual provider upload acknowledged once with original stream and sequence");

            var availabilityRequest = Guid.NewGuid();
            await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "availability", requestId = availabilityRequest, bootId = active.Boot, generation = 1,
                readyMask = 63, deviceRevision = 7, operationEpoch = MaximumEpoch }));
            var availabilityAck = await active.Inbox.JsonAsync("control.ack");
            Check(availabilityAck.GetProperty("requestId").GetGuid() == availabilityRequest
                && availabilityAck.GetProperty("status").GetInt32() == 204,
                "actual Realtime availability acknowledges exact immutable opening snapshot");
            var changedRequest = Guid.NewGuid();
            var changedAvailability = JsonSerializer.SerializeToUtf8Bytes(new
            { type = "availability", requestId = changedRequest, bootId = active.Boot, generation = 1,
                readyMask = 63, deviceRevision = 8, operationEpoch = MaximumEpoch });
            var beforeChange = journal.ReadSessionCharge(active.Conversation);
            foreach (var attempt in new[] { "initial", "exact replay" })
            {
                await SendControlAsync(active.Client, changedAvailability);
                var changedAck = await active.Inbox.JsonAsync("control.ack");
                Check(changedAck.GetProperty("requestId").GetGuid() == changedRequest
                    && changedAck.GetProperty("status").GetInt32() == 409
                    && changedAck.GetProperty("code").GetString() == "conversation_restart_required"
                    && journal.ReadSessionCharge(active.Conversation) == beforeChange,
                    "Realtime changed snapshot gets typed restart requirement without new usage on " + attempt);
            }

            var playbackId = Guid.NewGuid();
            var checkpoint = active.Session.PrepareCheckpoint(new(playbackId, 42, 1));
            var requestId = Guid.NewGuid();
            var control = JsonSerializer.SerializeToUtf8Bytes(new { type = "playback", requestId, playbackId, pcmBytes = 42, drained = true });
            var before = journal.ReadSessionCharge(active.Conversation);
            await SendControlAsync(active.Client, control);
            var controlAck = await active.Inbox.JsonAsync("control.ack");
            Check(controlAck.GetProperty("requestId").GetGuid() == requestId && controlAck.GetProperty("status").GetInt32() == 204
                && checkpoint.IsCompletedSuccessfully && checkpoint.Result.Drained, "actual mapped playback applies before correlated control ACK");
            await SendControlAsync(active.Client, control);
            var replayAck = await active.Inbox.JsonAsync("control.ack");
            Check(replayAck.GetProperty("requestId").GetGuid() == requestId && active.Provider.InputBytes == 4
                && journal.ReadSessionCharge(active.Conversation) == before, "exact control replay has ACK without duplicate input or usage");
            var conflict = JsonSerializer.SerializeToUtf8Bytes(new { type = "playback", requestId, playbackId, pcmBytes = 40, drained = true });
            await SendControlAsync(active.Client, conflict);
            await CheckDisconnectedAsync(active.Client);
            await CheckReleasedAsync(active, services);
        }
        foreach (var invalid in new[] { "out-of-order", "duplicate", "malformed" })
        {
            using var active = await OpenActiveAsync(address, services, providers);
            var input = await BeginInputAsync(active);
            if (invalid == "duplicate")
            {
                await active.Client.SendAsync(InputFrame(input, 0, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
                await active.Inbox.JsonAsync("media.ack");
            }
            var badFrame = InputFrame(input, invalid == "out-of-order" ? 1U : 0U, 4);
            if (invalid == "malformed") { badFrame[12] = 6; }
            await active.Client.SendAsync(badFrame.AsMemory(0, 7), WebSocketMessageType.Binary, false, CancellationToken.None);
            await active.Client.SendAsync(badFrame.AsMemory(7), WebSocketMessageType.Binary, true, CancellationToken.None);
            await CheckDisconnectedAsync(active.Client);
            await CheckReleasedAsync(active, services);
            Check(active.Provider.InputBytes == (invalid == "duplicate" ? 4 : 0), "invalid fragmented frame never reaches provider twice");
        }
        foreach (var mismatch in new[] { "generation", "operation" })
        {
            using var active = await OpenActiveAsync(address, services, providers);
            await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
            { type = "cancel", requestId = Guid.NewGuid(), operationId = mismatch == "operation" ? "other-operation" : active.Operation,
                responseGeneration = mismatch == "generation" ? 1 : 0 }));
            var refusal = await active.Inbox.JsonAsync("control.ack");
            Check(refusal.GetProperty("status").GetInt32() == 409
                && refusal.GetProperty("code").GetString() == "media_cancel_mismatch"
                && journal.GetOperation(active.Device, active.Operation).Operation?.Status == "running"
                && !active.Provider.Aborted && services.GetRequiredService<RealtimeLiveRegistry>()
                    .Find(active.Session.Id, active.Device, active.Conversation) == active.Session,
                "operation/generation-mismatched cancel receives typed refusal without mutation or connection loss");
            await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture peer close", CancellationToken.None);
            await CheckReleasedAsync(active, services);
        }
        using (var active = await OpenActiveAsync(address, services, providers))
        {
            await SendControlAsync(active.Client, Encoding.UTF8.GetBytes(Opening("realtime")));
            await CheckDisconnectedAsync(active.Client);
            await CheckReleasedAsync(active, services);
            Check(providers.IsEmpty, "second opening cannot start a second provider session");
        }
        foreach (var ending in new[] { "close", "cancel", "expiry" })
        {
            using var active = await OpenActiveAsync(address, services, providers);
            var input = await BeginInputAsync(active);
            active.Provider.StallInput = true;
            await active.Client.SendAsync(InputFrame(input, 0, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
            await active.Provider.InputStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (ending == "close")
            {
                await active.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture peer close", CancellationToken.None);
            }
            else if (ending == "cancel")
            {
                using var other = await OpenActiveAsync(address, services, providers);
                await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
                { type = "cancel", requestId = Guid.NewGuid(), operationId = active.CurrentOperation, responseGeneration = 0 }));
                await CheckDisconnectedAsync(active.Client);
                await CheckReleasedAsync(active, services);
                Check(journal.GetOperation(active.Device, active.CurrentOperation).Operation?.Status == "cancelled"
                    && services.GetRequiredService<ConversationStore>().GetOperation(active.Device, active.Operation).Operation?.Status == "completed"
                    && !other.Provider.Aborted && services.GetRequiredService<RealtimeLiveRegistry>()
                        .Find(other.Session.Id, other.Device, other.Conversation) == other.Session,
                    "targeted cancellation remains receivable during stalled upload and preserves another device session");
                await other.Client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture peer close", CancellationToken.None);
                await CheckReleasedAsync(other, services);
                continue;
            }
            else { clock.Advance(TimeSpan.FromMinutes(16)); }
            await CheckDisconnectedAsync(active.Client);
            await CheckReleasedAsync(active, services);
            Check(active.Provider.InputBytes == 0, "stalled upload has no accepted provider PCM on " + ending);
        }
        using (var active = await OpenActiveAsync(address, services, providers))
        {
            clock.Advance(TimeSpan.FromMinutes(16));
            await CheckDisconnectedAsync(active.Client);
            await CheckReleasedAsync(active, services);
        }
        Check(providers.IsEmpty, "disconnects do not reconnect or open another provider call");
    }

    private static async Task VerifyPrimaryLiveAsync(Uri address, IServiceProvider services,
        ConcurrentQueue<LiveProviderSocket> providers)
    {
        var device = "primary-live-media-" + Guid.NewGuid().ToString("N");
        var boot = Guid.NewGuid();
        var create = new CreateConversationRequest(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false), MaximumEpoch);
        var conversations = services.GetRequiredService<ConversationStore>();
        var created = conversations.Create(device, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(create, RealtimeProtocol.JsonOptions), create);
        Check(created.Status == 201 && created.Response is not null, "primary Live fixture creates real scoped conversation");
        var conversation = created.Response!.ConversationId;
        var bearer = services.GetRequiredService<DeviceAccessStore>().IssueToken(device)!.AccessToken;
        using var client = await ConnectAsync(address, "/v1/conversations/" + conversation + "/device-media", bearer);
        var opening = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1, MaximumEpoch);
        await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new { type = "open", engine = "gpt-live", request = opening }, RealtimeProtocol.JsonOptions));
        var inbox = new MediaInbox(client);
        var sessionEvent = await inbox.JsonAsync("session");
        var binding = await inbox.JsonAsync("media.operation");
        var started = await inbox.JsonAsync("started");
        var operation = started.GetProperty("operationId").GetString()!;
        Check(binding.GetProperty("operationId").GetString() == operation
            && started.GetProperty("correlation").GetProperty("operationEpoch").GetString() == MaximumEpoch,
            "primary Live binds actual operation and retains original canonical epoch");
        var input = await inbox.JsonAsync("media.input");
        await inbox.JsonAsync("listening");
        Check(providers.TryDequeue(out var provider), "primary Live uses mandatory local provider override");
        var stream = input.GetProperty("stream").GetUInt32();
        await client.SendAsync(InputFrame(stream, 0, 4).AsMemory(), WebSocketMessageType.Binary, true, CancellationToken.None);
        var ack = await inbox.JsonAsync("media.ack");
        Check(ack.GetProperty("stream").GetUInt32() == stream && ack.GetProperty("sequence").GetUInt32() == 0
            && provider!.InputBytes == 4, "primary Live persistent binary input reaches provider exactly once before media ACK");
        var output = await inbox.JsonAsync("media.output");
        var audio = await inbox.BinaryAsync();
        Check(output.GetProperty("responseGeneration").GetInt64() == 0 && audio.AsSpan(16).SequenceEqual(new byte[] { 1, 2, 3, 4 }),
            "primary Live provider audio returns as raw KF PCM");
        var sessionId = sessionEvent.GetProperty("streamId").GetGuid();
        var registry = services.GetRequiredService<RealtimeLiveRegistry>();
        Check(registry.Find(sessionId, device, conversation) is not null, "primary Live owns actual registered media session");
        await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new
        { type = "cancel", requestId = Guid.NewGuid(), operationId = operation, responseGeneration = 0 }));
        await CheckTargetedCancellationAsync(() => ReadAsync(client),
            () => client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture acknowledged cancellation", CancellationToken.None),
            "primary Live");
        await provider!.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (registry.Find(sessionId, device, conversation) is not null) { await Task.Delay(10, deadline.Token); }
        var recorded = services.GetRequiredService<OperationJournal>().GetOperation(device, operation).Operation;
        Check(recorded?.Status == "cancelled" && recorded.Correlation.OperationEpoch == MaximumEpoch
            && provider.Aborted && providers.IsEmpty, "primary Live targeted cancel releases provider/session without reconnect or epoch restamping");
    }

    private static async Task<ActiveMedia> OpenActiveAsync(Uri address, IServiceProvider services,
        ConcurrentQueue<ActiveProviderSocket> providers, Guid? testGate = null)
    {
        var device = "active-media-" + Guid.NewGuid().ToString("N");
        var boot = Guid.NewGuid();
        var request = new CreateConversationRequest(device, boot, 1, PartyWandCatalog.Version, "cs-CZ", new(true, false), MaximumEpoch);
        var store = services.GetRequiredService<ConversationStore>();
        var created = store.Create(device, Guid.NewGuid(), JsonSerializer.SerializeToUtf8Bytes(request, RealtimeProtocol.JsonOptions), request);
        Check(created.Status == 201 && created.Response is not null, "active fixture creates actual scoped conversation");
        var conversation = created.Response!.ConversationId;
        var token = services.GetRequiredService<DeviceAccessStore>().IssueToken(device)!.AccessToken;
        var client = await ConnectAsync(address, "/v1/conversations/" + conversation + "/device-media", token, testGate);
        var opening = new RealtimeTurnRequest(Guid.NewGuid(), boot, 1, 63, 7, PartyWandCatalog.Version, true, "", null, "marin", 1, MaximumEpoch);
        await SendControlAsync(client, JsonSerializer.SerializeToUtf8Bytes(new { type = "open", engine = "realtime", request = opening }, RealtimeProtocol.JsonOptions));
        var inbox = new MediaInbox(client);
        var sessionEvent = await inbox.JsonAsync("session");
        Check(providers.TryDequeue(out var provider), "available provider uses mandatory in-process override");
        var started = await inbox.JsonAsync("started");
        var binding = await inbox.JsonAsync("media.operation");
        Check(binding.GetProperty("operationId").GetString() == started.GetProperty("operationId").GetString(),
            "device binding names actual opening operation without changing canonical event");
        var output = await inbox.JsonAsync("media.output");
        var audio = await inbox.BinaryAsync();
        Check(audio.Length == 20 && audio[3] == 2 && BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(4)) == output.GetProperty("stream").GetUInt32()
            && audio.AsSpan(16).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "actual started session streams raw provider audio");
        var session = services.GetRequiredService<RealtimeLiveRegistry>().Find(sessionEvent.GetProperty("streamId").GetGuid(), device, conversation);
        Check(session is not null, "mapped session registry ownership");
        return new(client, inbox, provider!, session!, device, boot, conversation, started.GetProperty("operationId").GetString()!);
    }

    private static async Task<uint> BeginInputAsync(ActiveMedia active)
    {
        active.Provider.CompleteResponse();
        await active.Inbox.JsonAsync("completed");
        var requestId = Guid.NewGuid();
        await SendControlAsync(active.Client, JsonSerializer.SerializeToUtf8Bytes(new
        { type = "played", requestId, operationId = active.Operation, responseGeneration = 0 }));
        var ack = await active.Inbox.JsonAsync("control.ack");
        Check(ack.GetProperty("requestId").GetGuid() == requestId, "actual played operation receives correlated ACK");
        var input = await active.Inbox.JsonAsync("media.input");
        await active.Inbox.JsonAsync("listening");
        var binding = await active.Inbox.JsonAsync("media.operation");
        active.CurrentOperation = binding.GetProperty("operationId").GetString()!;
        Check(active.CurrentOperation != active.Operation, "next listening input exposes its own actual operation for scoped cancellation");
        return input.GetProperty("stream").GetUInt32();
    }

    private static Task SendControlAsync(ClientWebSocket client, byte[] bytes) =>
        client.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None).AsTask();

    private static async Task CheckReleasedAsync(ActiveMedia active, IServiceProvider services)
    {
        await active.Provider.Ended.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var registry = services.GetRequiredService<RealtimeLiveRegistry>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (registry.Find(active.Session.Id, active.Device, active.Conversation) is not null)
        {
            await Task.Delay(10, timeout.Token);
        }
        Check(active.Provider.Aborted, "disconnect releases provider connection and registry slot");
    }

    private static async Task VerifyOrderedFramesAsync(Uri address)
    {
        using var client = await ConnectAsync(address, "/fixture-media", null);
        var declaration = await ReadJsonAsync(client, "media.input");
        var stream = declaration.GetProperty("stream").GetUInt32();
        await ReadJsonAsync(client, "listening");
        var frame = InputFrame(stream, 0, 4);
        await client.SendAsync(frame.AsMemory(0, 5), WebSocketMessageType.Binary, false, CancellationToken.None);
        await client.SendAsync(frame.AsMemory(5), WebSocketMessageType.Binary, true, CancellationToken.None);
        await ReadJsonAsync(client, "speech_stopped");
        await ReadJsonAsync(client, "listening");
        await ReadJsonAsync(client, "media.input");
        await ReadJsonAsync(client, "listening");
        var output = await ReadJsonAsync(client, "media.output");
        var outputStream = output.GetProperty("stream").GetUInt32();
        Check(outputStream != stream, "input and output use distinct streams");
        using var pcm = new MemoryStream();
        for (uint sequence = 0; sequence < 3; sequence++)
        {
            var message = await ReadAsync(client);
            var bytes = message.Bytes;
            Check(message.Kind == WebSocketMessageType.Binary && bytes[3] == 2
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) == outputStream
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)) == sequence
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) == bytes.Length - 16
                && bytes.Length <= 4112, "ordered bounded output KF frame");
            pcm.Write(bytes, 16, bytes.Length - 16);
        }
        Check(pcm.ToArray().SequenceEqual(Enumerable.Range(0, 9000).Select(index => (byte)index)), "raw PCM survives chunking exactly");
        await ReadJsonAsync(client, "audio_cancel");
        var replacement = await ReadJsonAsync(client, "media.output");
        var replacementFrame = await ReadAsync(client);
        Check(replacement.GetProperty("stream").GetUInt32() != outputStream
            && replacement.GetProperty("responseGeneration").GetInt64() == 4
            && BinaryPrimitives.ReadUInt32LittleEndian(replacementFrame.Bytes.AsSpan(8)) == 0
            && replacementFrame.Bytes.AsSpan(16).SequenceEqual(new byte[] { 1, 2 }), "cancelled output uses fresh stream and resets sequence");
        await ReadJsonAsync(client, "closed");
        await client.CloseAsync(WebSocketCloseStatus.NormalClosure, "fixture complete", CancellationToken.None);
    }

    private static async Task CheckAdmissionAsync(HttpClient http, string route, string? bearer, string? key,
        bool cookie, int status, string code)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, route);
        if (bearer is not null) { request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer); }
        if (key is not null) { request.Headers.TryAddWithoutValidation("Idempotency-Key", key); }
        if (cookie) { request.Headers.TryAddWithoutValidation("Cookie", "fixture-account=authenticated"); }
        using var response = await http.SendAsync(request);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Check((int)response.StatusCode == status && json.RootElement.GetProperty("code").GetString() == code,
            "device route admission: " + code);
    }

    private static async Task VerifyHandshakeRejectedAsync(Uri address, string route, string token)
    {
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer " + token);
        client.Options.SetRequestHeader("Idempotency-Key", Guid.NewGuid().ToString("D"));
        client.Options.AddSubProtocol("wrong-device-protocol");
        try
        {
            await client.ConnectAsync(WebSocketUri(address, route), CancellationToken.None);
            throw new InvalidOperationException("Wrong subprotocol was admitted.");
        }
        catch (WebSocketException) { }
    }

    private static async Task<ClientWebSocket> ConnectAsync(Uri address, string route, string? token, Guid? testGate = null)
    {
        var client = new ClientWebSocket();
        client.Options.AddSubProtocol(DeviceMediaProtocol.Subprotocol);
        client.Options.SetRequestHeader("Idempotency-Key", Guid.NewGuid().ToString("D"));
        if (testGate is { } gate) { client.Options.SetRequestHeader("X-Device-Media-Test-Gate", gate.ToString("D")); }
        if (token is not null) { client.Options.SetRequestHeader("Authorization", "Bearer " + token); }
        await client.ConnectAsync(WebSocketUri(address, route), CancellationToken.None);
        Check(client.SubProtocol == DeviceMediaProtocol.Subprotocol, "negotiated device media protocol");
        return client;
    }

    private static Uri WebSocketUri(Uri address, string route) => new UriBuilder(new Uri(address, route)) { Scheme = "ws" }.Uri;

    private static async Task<(WebSocketMessageType Kind, byte[] Bytes)> ReadAsync(ClientWebSocket client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var body = new MemoryStream();
        var buffer = new byte[1024];
        ValueWebSocketReceiveResult part;
        do
        {
            part = await client.ReceiveAsync(buffer.AsMemory(), timeout.Token);
            body.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        return (part.MessageType, body.ToArray());
    }

    private static async Task<JsonElement> ReadJsonAsync(ClientWebSocket client, string expectedType)
    {
        var message = await ReadAsync(client);
        using var json = JsonDocument.Parse(message.Bytes);
        Check(message.Kind == WebSocketMessageType.Text && json.RootElement.GetProperty("type").GetString() == expectedType,
            "ordered event " + expectedType);
        return json.RootElement.Clone();
    }

    private static async Task CheckDisconnectedAsync(ClientWebSocket client)
    {
        try
        {
            var message = await ReadAsync(client);
            Check(message.Kind == WebSocketMessageType.Close, "invalid or expired connection closes without HTTP bytes");
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "fixture acknowledged", CancellationToken.None);
        }
        catch (WebSocketException) { }
    }

    private static async Task CheckTargetedCancellationAsync(
        Func<Task<(WebSocketMessageType Kind, byte[] Bytes)>> read,
        Func<Task> acknowledgeClose, string scope)
    {
        try
        {
            var message = await read();
            if (message.Kind != WebSocketMessageType.Close)
            {
                using var json = JsonDocument.Parse(message.Bytes);
                var root = json.RootElement;
                var expectedFailure = message.Kind == WebSocketMessageType.Text
                    && DeviceMediaProtocol.Fields(root, "type", "code")
                    && root.GetProperty("type").ValueKind == JsonValueKind.String
                    && root.GetProperty("type").GetString() == "failed"
                    && root.GetProperty("code").ValueKind == JsonValueKind.String
                    && root.GetProperty("code").GetString() == "operation_cancelled";
                Check(expectedFailure, "targeted cancellation accepts only canonical failed/operation_cancelled before closure");
                Console.WriteLine("Device media cancellation schedule (" + scope + "): failed/operation_cancelled then close.");
                message = await read();
            }
            Check(message.Kind == WebSocketMessageType.Close, "targeted cancellation permits at most one terminal failure before close");
            await acknowledgeClose();
        }
        catch (WebSocketException) { }
    }

    private static async Task VerifyCancellationSchedulesAsync()
    {
        var close = (WebSocketMessageType.Close, Array.Empty<byte>());
        var cancelled = (WebSocketMessageType.Text, "{\"type\":\"failed\",\"code\":\"operation_cancelled\"}"u8.ToArray());
        foreach (var schedule in new[] { new[] { close }, new[] { cancelled, close } })
        {
            var frames = new Queue<(WebSocketMessageType Kind, byte[] Bytes)>(schedule);
            var acknowledged = false;
            await CheckTargetedCancellationAsync(() => Task.FromResult(frames.Dequeue()),
                () => { acknowledged = true; return Task.CompletedTask; }, "deterministic fixture");
            Check(acknowledged && frames.Count == 0, "both cancellation schedules consume expected terminal and close");
        }
        foreach (var schedule in new[]
        {
            new[] { cancelled, cancelled, close },
            new[] { (WebSocketMessageType.Text, "{\"type\":\"control.ack\",\"status\":409}"u8.ToArray()), close },
            new[] { (WebSocketMessageType.Text, "{\"type\":\"failed\",\"code\":\"ai_unavailable\"}"u8.ToArray()), close },
            new[] { (WebSocketMessageType.Text, "{\"type\":\"failed\",\"code\":\"operation_cancelled\",\"extra\":true}"u8.ToArray()), close },
            new[] { (WebSocketMessageType.Binary, "{\"type\":\"failed\",\"code\":\"operation_cancelled\"}"u8.ToArray()), close }
        })
        {
            var frames = new Queue<(WebSocketMessageType Kind, byte[] Bytes)>(schedule);
            var rejected = false;
            try
            {
                await CheckTargetedCancellationAsync(() => Task.FromResult(frames.Dequeue()),
                    () => Task.CompletedTask, "invalid deterministic fixture");
            }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "targeted cancellation rejects refusal, duplicate terminal, altered failure and binary data");
        }
    }

    private static byte[] InputFrame(uint stream, uint sequence, int pcmBytes)
    {
        var frame = new byte[16 + pcmBytes];
        DeviceMediaProtocol.WritePcmHeader(frame, stream, sequence, pcmBytes);
        frame[3] = 1;
        return frame;
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or FormatException
            or DeviceMediaEndpoints.ControlRefusal)
        { return; }
        throw new InvalidOperationException("Device media: " + message);
    }

    private static async Task ExpectCancellationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Device media: expected cancellation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException("Device media: " + message); }
    }

    private sealed class FlushedStream : MemoryStream
    {
        internal int Flushes { get; private set; }
        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushes++;
            return Task.CompletedTask;
        }
    }

    private sealed class MediaClock : TimeProvider
    {
        private long ticks = DateTimeOffset.UtcNow.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        internal void Advance(TimeSpan amount) => Interlocked.Add(ref ticks, amount.Ticks);
    }

    private sealed class ForbiddenInput : IStreamingAudioInput
    {
        public Task SendAudioAsync(byte[] pcm, CancellationToken token) =>
            throw new InvalidOperationException("Control fixture must not invoke any provider.");
    }

    private sealed record ActiveMedia(ClientWebSocket Client, MediaInbox Inbox, ActiveProviderSocket Provider,
        RealtimeLiveSession Session, string Device, Guid Boot, string Conversation, string Operation) : IDisposable
    {
        internal string CurrentOperation { get; set; } = Operation;
        public void Dispose() => Client.Dispose();
    }

    private sealed class WireGate
    {
        internal string? AckType { get; set; }
        internal Guid? AckRequest { get; set; }
        internal uint? ObserveBinarySequence { get; set; }
        internal Guid? ObserveRequest { get; set; }
        internal Action? OpeningReceived { get; init; }
        internal bool OpeningApplied { get; private set; }
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource RequestEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int terminalEvents, closeCalls;
        internal int TerminalEvents => Volatile.Read(ref terminalEvents);
        internal int CloseCalls => Volatile.Read(ref closeCalls);
        internal void Closing() => Interlocked.Increment(ref closeCalls);
        private readonly MemoryStream incoming = new();
        private bool observed;
        private int held;
        internal void BeforeReceive()
        {
            if (observed) { Admitted.TrySetResult(); }
        }
        internal void Received(ReadOnlyMemory<byte> bytes, WebSocketMessageType kind, bool complete)
        {
            incoming.Write(bytes.Span);
            if (!complete) { return; }
            var body = incoming.ToArray(); incoming.SetLength(0);
            if (kind == WebSocketMessageType.Binary && body.Length >= 16)
            {
                if (ObserveBinarySequence == BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(8))) { observed = true; }
                return;
            }
            if (kind != WebSocketMessageType.Text) { return; }
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "open" && OpeningReceived is not null && !OpeningApplied)
            {
                OpeningApplied = true; OpeningReceived();
            }
            if (root.TryGetProperty("requestId", out var request) && request.TryGetGuid(out var id) && id == ObserveRequest)
            {
                observed = true;
            }
        }
        internal async Task ForwardedAsync(ReadOnlyMemory<byte> bytes, WebSocketMessageType kind, CancellationToken token)
        {
            if (kind != WebSocketMessageType.Text) { return; }
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.TryGetProperty("type", out var terminalType) && terminalType.GetString() == "closed")
            {
                Interlocked.Increment(ref terminalEvents);
            }
            if (AckType is null || Volatile.Read(ref held) != 0) { return; }
            if (!root.TryGetProperty("type", out var type) || type.GetString() != AckType
                || AckRequest is { } expected && (!root.TryGetProperty("requestId", out var request) || request.GetGuid() != expected)) { return; }
            if (Interlocked.Exchange(ref held, 1) != 0) { return; }
            Held.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    private sealed class GatedWebSocketFeature(IHttpWebSocketFeature inner, WireGate gate) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => inner.IsWebSocketRequest;
        public async Task<WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            new GatedServerSocket(await inner.AcceptAsync(context), gate);
    }

    // Bytes reach the real client before the server SendAsync continuation is
    // released. This exercises the production mapped route, not an ACK mock.
    private sealed class GatedServerSocket(WebSocket inner, WireGate gate) : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => inner.CloseStatus;
        public override string? CloseStatusDescription => inner.CloseStatusDescription;
        public override WebSocketState State => inner.State;
        public override string? SubProtocol => inner.SubProtocol;
        public override void Abort() => inner.Abort();
        public override void Dispose() => inner.Dispose();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            inner.CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            gate.Closing();
            return inner.CloseOutputAsync(closeStatus, statusDescription, cancellationToken);
        }
        public override async ValueTask<ValueWebSocketReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            gate.BeforeReceive();
            var result = await inner.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType != WebSocketMessageType.Close)
            {
                gate.Received(buffer[..result.Count], result.MessageType, result.EndOfMessage);
            }
            return result;
        }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            var result = await ReceiveAsync(buffer.AsMemory(), cancellationToken);
            return new(result.Count, result.MessageType, result.EndOfMessage);
        }
        public override async ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken = default)
        {
            await inner.SendAsync(buffer, messageType, endOfMessage, cancellationToken);
            await gate.ForwardedAsync(buffer, messageType, cancellationToken);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => SendAsync(buffer.AsMemory(), messageType, endOfMessage, cancellationToken).AsTask();
    }

    private sealed class PublicationSocket : WebSocket
    {
        internal ConcurrentQueue<string> SentTypes { get; } = new();
        internal TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Publication fixture has no receive owner.");
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            if (messageType == WebSocketMessageType.Text)
            {
                using var document = JsonDocument.Parse(buffer);
                if (document.RootElement.TryGetProperty("type", out var type)) { SentTypes.Enqueue(type.GetString()!); }
            }
            SendEntered.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class ReceiveLeaseSocket : WebSocket
    {
        private bool aborted;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WroteAfterAbort { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() => aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            buffer.AsSpan().Fill(0x5A);
            WroteAfterAbort = aborted;
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class ReplyGateSocket : WebSocket
    {
        private int sends;
        internal TaskCompletionSource FirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource TwoSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<JsonElement> Sent { get; } = new();
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Reply pump fixture has no receiver.");
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref sends);
            if (number == 1)
            {
                FirstSend.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            using var json = JsonDocument.Parse(buffer);
            Sent.Enqueue(json.RootElement.Clone());
            if (number == 2) { TwoSent.TrySetResult(); }
        }
    }

    private sealed class LiveProviderSocket : WebSocket
    {
        private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource ended = new();
        private byte[]? current;
        private int offset;
        private int inputBytes;
        internal int InputBytes => Volatile.Read(ref inputBytes);
        internal bool Aborted => ended.IsCancellationRequested;
        internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private void Emit(object value) => events.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        internal void EmitTerminalSpeech()
        {
            Emit(new { type = "session.output_transcript.delta", delta = "Zadna fyzicka hulka neni dostupna. Necham kufr odpocivat.",
                start_ms = 0, end_ms = 1000, event_id = Guid.NewGuid().ToString("N") });
            Emit(new { type = "session.output_audio.delta", delta = "AQIDBA==" });
        }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { ended.Cancel(); Ended.TrySetResult(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended.Token);
            current ??= await events.Reader.ReadAsync(linked.Token);
            var count = Math.Min(buffer.Count, current.Length - offset);
            current.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var complete = offset == current.Length;
            if (complete) { current = null; offset = 0; }
            return new(count, WebSocketMessageType.Text, complete);
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Check(endOfMessage && messageType == WebSocketMessageType.Text, "Live provider commands are complete JSON messages");
            using var json = JsonDocument.Parse(buffer);
            var root = json.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "session.start":
                    Check(root.GetProperty("session").GetProperty("model").GetString() == GptLiveProtocol.Model,
                        "primary Live uses exact pinned model through isolated local protocol");
                    Emit(new { type = "session.started", client_event_id = root.GetProperty("event_id").GetString(),
                        session = new { model = GptLiveProtocol.Model, id = "device-media-offline-live" } });
                    break;
                case "session.instructions.append":
                    Emit(new { type = "session.instructions.appended", client_event_id = root.GetProperty("event_id").GetString() });
                    break;
                case "session.input_audio.append":
                    Interlocked.Add(ref inputBytes, Convert.FromBase64String(root.GetProperty("audio").GetString()!).Length);
                    Emit(new { type = "session.output_audio.delta", delta = "AQIDBA==" });
                    break;
                case "session.close":
                    Emit(new { type = "session.closed", client_event_id = root.GetProperty("event_id").GetString(),
                        reason = "close_requested", usage = new { seconds = 1.25 } });
                    break;
            }
            return Task.CompletedTask;
        }
    }

    private sealed class MediaInbox(ClientWebSocket client)
    {
        private readonly List<(WebSocketMessageType Kind, byte[] Bytes)> pending = [];
        internal async Task<JsonElement> JsonAsync(string type)
        {
            while (true)
            {
                for (var index = 0; index < pending.Count; index++)
                {
                    if (pending[index].Kind != WebSocketMessageType.Text) { continue; }
                    using var json = JsonDocument.Parse(pending[index].Bytes);
                    if (json.RootElement.GetProperty("type").GetString() != type) { continue; }
                    var result = json.RootElement.Clone();
                    pending.RemoveAt(index);
                    return result;
                }
                var next = await ReadAsync(client);
                if (next.Kind == WebSocketMessageType.Close)
                {
                    var eventTypes = pending.Where(item => item.Kind == WebSocketMessageType.Text).Select(item =>
                    {
                        using var document = JsonDocument.Parse(item.Bytes);
                        var root = document.RootElement;
                        return root.GetProperty("type").GetString()
                            + (root.TryGetProperty("code", out var code) ? "/" + code.GetString() : "");
                    });
                    throw new InvalidOperationException("Fixture closed while waiting for " + type + "; received " + string.Join(",", eventTypes));
                }
                pending.Add(next);
                Check(pending.Count < 64, "active fixture bounds pending events");
            }
        }
        internal async Task<byte[]> BinaryAsync()
        {
            while (true)
            {
                var index = pending.FindIndex(message => message.Kind == WebSocketMessageType.Binary);
                if (index >= 0)
                {
                    var result = pending[index].Bytes;
                    pending.RemoveAt(index);
                    return result;
                }
                pending.Add(await ReadAsync(client));
            }
        }
    }

    // This provider has no network path. Its wire events pass through the real
    // pinned SDK reader; test actions control input acceptance and completion.
    private sealed class ActiveProviderSocket : WebSocket
    {
        private readonly Channel<byte[]> events = Channel.CreateUnbounded<byte[]>();
        private readonly CancellationTokenSource ended = new();
        private readonly MemoryStream sent = new();
        private byte[]? current;
        private int offset;
        private int inputBytes;
        internal bool StallInput { get; set; }
        internal int InputBytes => Volatile.Read(ref inputBytes);
        internal bool Aborted => ended.IsCancellationRequested;
        internal TaskCompletionSource InputStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Ended { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ActiveProviderSocket() => Emit(new { type = "session.created", session = new { type = "realtime", model = RealtimeProtocol.Model } });
        private void Emit(object value) => events.Writer.TryWrite(JsonSerializer.SerializeToUtf8Bytes(value));
        private int responseNumber;
        private bool terminalAfterTool;
        internal void CompleteTerminalResponse()
        {
            terminalAfterTool = true;
            Emit(new
            {
                type = "response.done", response = new
                {
                    id = "response-1", status = "completed",
                    output = new[] { new { type = "function_call", name = "end_conversation", call_id = "terminal-tool",
                        arguments = "{\"reason\":\"fixture terminal\"}" } },
                    usage = new { input_tokens = 123, output_tokens = 200,
                        input_token_details = new { text_tokens = 123, audio_tokens = 0 } }
                }
            });
        }
        internal void CompleteResponse() => CompleteResponse("response-1", "Jak ti rikaji?");
        private void CompleteResponse(string responseId, string transcript) => Emit(new
        {
            type = "response.done",
            response = new
            {
                id = responseId, status = "completed",
                output = new[] { new { type = "message", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_audio", transcript } } } },
                usage = new { input_tokens = 123, output_tokens = 200, input_token_details = new { text_tokens = 123, audio_tokens = 0 } }
            }
        });
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { ended.Cancel(); Ended.TrySetResult(); }
        public override void Dispose() => Abort();
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        { Abort(); return Task.CompletedTask; }
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended.Token);
            current ??= await events.Reader.ReadAsync(linked.Token);
            var count = Math.Min(buffer.Count, current.Length - offset);
            current.AsSpan(offset, count).CopyTo(buffer.AsSpan());
            offset += count;
            var complete = offset == current.Length;
            if (complete) { current = null; offset = 0; }
            return new(count, WebSocketMessageType.Text, complete);
        }
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken)
        {
            sent.Write(buffer);
            if (!endOfMessage) { return; }
            using var json = JsonDocument.Parse(sent.ToArray());
            sent.SetLength(0);
            var root = json.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "session.update":
                    Emit(new { type = "session.updated", session = new { type = "realtime", model = RealtimeProtocol.Model } });
                    break;
                case "response.create":
                    var number = Interlocked.Increment(ref responseNumber);
                    var responseId = "response-" + number;
                    Emit(new { type = "response.created", response = new { id = responseId } });
                    Emit(new { type = "response.output_audio.delta", response_id = responseId, delta = "AQIDBA==" });
                    if (terminalAfterTool && number == 2) { CompleteResponse(responseId, "Rozhovor skoncil. Kufr si odpocine."); }
                    break;
                case "input_audio_buffer.append":
                    InputStarted.TrySetResult();
                    if (StallInput)
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended.Token);
                        await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
                    }
                    Interlocked.Add(ref inputBytes, Convert.FromBase64String(root.GetProperty("audio").GetString()!).Length);
                    break;
            }
        }
    }

    private sealed class StalledSocket : WebSocket
    {
        internal bool Aborted { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => Aborted ? WebSocketState.Aborted : WebSocketState.Open;
        public override string? SubProtocol => DeviceMediaProtocol.Subprotocol;
        public override void Abort() => Aborted = true;
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable stalled receive.");
        }
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage,
            CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}

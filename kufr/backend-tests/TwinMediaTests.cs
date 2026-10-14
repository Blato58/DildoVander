using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.DigitalTwin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class TwinMediaTests
{
    internal static async Task RunAsync()
    {
        await FramingAsync();
        ProviderSource();
        CancellationMemory();
        var native = Environment.GetEnvironmentVariable("KUFR_TWIN_TEST_NATIVE")
            ?? throw new InvalidOperationException("Browser media acceptance requires KUFR_TWIN_TEST_NATIVE.");
        var root = Path.Combine(Path.GetTempPath(), "twin-media-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string control = "offline-control-credential-00000000000000000000000000000001";
        const string observe = "offline-observe-credential-00000000000000000000000000000002";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DigitalTwin:DataRoot"] = root, ["DigitalTwin:Origin"] = "https://twin.test",
            ["Backend:DatabasePath"] = Path.Combine(root, "isolated.sqlite"), ["Backend:Memory:Enabled"] = "false",
            ["DigitalTwin:IntegrationBackendUrl"] = "http://127.0.0.1:1/",
            ["DigitalTwin:Instances:sim-media:Mode"] = "simulation", ["DigitalTwin:Instances:sim-media:DeviceId"] = "sim-media",
            ["DigitalTwin:Instances:sim-media:Profile"] = "integration", ["DigitalTwin:Instances:sim-media:Executable"] = native,
            ["DigitalTwin:Instances:sim-media:BuildId"] = "offline-media-acceptance",
            ["DigitalTwin:Instances:live-media:Mode"] = "live", ["DigitalTwin:Instances:live-media:DeviceId"] = "live-media",
            ["Devices:0:DeviceId"] = "sim-media", ["Devices:0:CredentialSha256"] = Hash("private-sim-device-credential-placeholder"),
            ["DigitalTwin:Credentials:0:Role"] = "control", ["DigitalTwin:Credentials:0:Mode"] = "simulation",
            ["DigitalTwin:Credentials:0:InstanceId"] = "sim-media", ["DigitalTwin:Credentials:0:Sha256"] = Hash(control),
            ["DigitalTwin:Credentials:1:Role"] = "observe", ["DigitalTwin:Credentials:1:Sha256"] = Hash(observe)
        });
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DeviceAccessStore>(); builder.Services.AddSingleton<TwinAccess>();
        builder.Services.AddSingleton<TwinStore>(); builder.Services.AddSingleton<TwinHost>();
        builder.Services.AddHostedService(services => services.GetRequiredService<TwinHost>());
        builder.Services.AddSingleton<TwinMediaSettingsStore>(); builder.Services.AddSingleton<TwinFakeInputPolicy>();
        builder.Services.AddSingleton<OperationJournal>(); builder.Services.AddSingleton<ConversationStore>();
        builder.Services.AddSingleton<TwinMediaSessions>();
        await using var app = builder.Build();
        app.MapDigitalTwin(); app.MapTwinMedia(); app.MapConversations(); app.MapCostumeImages();
        await app.StartAsync();
        builder.Configuration["DigitalTwin:IntegrationBackendUrl"] = app.Urls.Single() + "/";
        var host = app.Services.GetRequiredService<TwinHost>();
        for (var count = 0; count < 100 && !host.IsReady(); count++) { await Task.Delay(20); }
        Check(host.IsReady(), "native readiness");
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var owner = await LoginAsync(http, control);
        var spectator = await LoginAsync(http, observe);
        var second = await LoginAsync(http, control);
        const string prefix = "/v1/twin/instances/sim-media";
        var revokedRoot = Path.Combine(root, "revoked");
        Directory.CreateDirectory(Path.Combine(revokedRoot, "simulation", "sim-media"));
        var revokedConfiguration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DigitalTwin:DataRoot"] = revokedRoot, ["Backend:DatabasePath"] = Path.Combine(revokedRoot, "isolated.sqlite"),
            ["DigitalTwin:Instances:sim-media:Mode"] = "simulation", ["DigitalTwin:Instances:sim-media:Profile"] = "integration",
            ["DigitalTwin:ExternalServicesAuthorized"] = "false", ["OPENAI_API_KEY"] = "offline-placeholder"
        }).Build();
        var persisted = new TwinMediaSettings("8", "realtime", "cedar", 1, false, "main", new(true, true, true, true, true, true));
        File.WriteAllBytes(Path.Combine(revokedRoot, "simulation", "sim-media", "media-settings.json"), JsonSerializer.SerializeToUtf8Bytes(persisted, TwinSnapshot.JsonOptions));
        using (var revoked = new TwinMediaSessions(host, app.Services.GetRequiredService<TwinStore>(),
            new TwinMediaSettingsStore(revokedConfiguration), app.Services.GetRequiredService<DeviceAccessStore>(), revokedConfiguration,
            TimeProvider.System, app.Services.GetRequiredService<ConversationStore>()))
        {
            var blocked = await revoked.StartAsync("sim-media", app.Services.GetRequiredService<TwinAccess>().Session(owner.Split('=', 2)[1])!,
                new TwinMediaStart(Guid.NewGuid().ToString("D"), app.Services.GetRequiredService<TwinStore>().Read("sim-media")!.Snapshot.Source.BootId, true, false, true, true));
            Check(blocked.Error == "provider_not_authorized_or_configured", "revoked authorization overrides persisted real engine and key presence before native/network admission");
        }
        foreach (var route in new[] { "/settings", "/media/state", "/media/output" })
        {
            Check((await SendAsync(HttpMethod.Get, prefix + route, spectator)).StatusCode == HttpStatusCode.Forbidden, "observer private payload denied");
            Check((await SendAsync(HttpMethod.Get, "/v1/twin/instances/live-media" + route, owner)).StatusCode == HttpStatusCode.Forbidden, "simulation cannot read physical media");
        }
        Check((await SendAsync(HttpMethod.Post, prefix + "/commands", owner, new { action = "inspect" })).IsSuccessStatusCode, "native explicit inspection");
        var settings = await ReadAsync(prefix + "/settings", owner);
        Check(settings.GetProperty("engine").GetString() == "fake" && !settings.GetProperty("availability").GetProperty("realtime").GetBoolean(), "offline default/no provider");
        using (var forbidden = await SendAsync(HttpMethod.Put, prefix + "/settings", owner, new
            { expectedRevision = "0", engine = "realtime", voice = "cedar", speed = 1, photoConsent = false, presentationVariant = "short" }))
        { Check(forbidden.StatusCode == HttpStatusCode.Conflict, "real API needs operator authorization"); }
        using (var changed = await SendAsync(HttpMethod.Put, prefix + "/settings", owner, new
            { expectedRevision = "0", engine = "fake", voice = "marin", speed = 1, photoConsent = false, presentationVariant = "short" }))
        { Check(changed.IsSuccessStatusCode, "durable page settings"); }
        using (var stale = await SendAsync(HttpMethod.Put, prefix + "/settings", owner, new
            { expectedRevision = "0", engine = "fake", voice = "marin", speed = 1, photoConsent = false, presentationVariant = "short" }))
        { Check(stale.StatusCode == HttpStatusCode.Conflict, "revision conflict"); }
        var snapshot = app.Services.GetRequiredService<TwinStore>().Read("sim-media")!.Snapshot;
        var operation = Guid.NewGuid().ToString("D");
        var start = new { operationId = operation, expectedBootId = snapshot.Source.BootId, audioConsent = true,
            photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false };
        var cancelledBeforeActor = Guid.NewGuid().ToString("D");
        using (var earlyCancel = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
            new { operationId = cancelledBeforeActor, expectedBootId = snapshot.Source.BootId }))
        { Check(earlyCancel.IsSuccessStatusCode, "cancellation is remembered before any actor exists"); }
        using (var lateStart = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner,
            start with { operationId = cancelledBeforeActor }))
        {
            var problem = await lateStart.Content.ReadFromJsonAsync<JsonElement>();
            Check(lateStart.StatusCode == HttpStatusCode.Conflict && problem.GetProperty("code").GetString() == "media_cancelled",
                "cancel before first Start blocks delayed HTTP arrival");
        }
        using var admitted = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner, start);
        var state = await admitted.Content.ReadFromJsonAsync<JsonElement>();
        Check(admitted.IsSuccessStatusCode, "native media admission: " + state);
        using (var duplicate = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner, start))
        {
            Check(duplicate.IsSuccessStatusCode && (await duplicate.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionId").GetString() == state.GetProperty("sessionId").GetString(), "duplicate start does not repeat input");
        }
        Check((await SendAsync(HttpMethod.Get, prefix + "/media/state", second)).StatusCode == HttpStatusCode.Conflict, "second actor cannot read media");
        Check((await SendAsync(HttpMethod.Post, prefix + "/media/start", second, start)).StatusCode == HttpStatusCode.Conflict, "second actor cannot own media");
        var inputs = 0; var drains = 0; var captions = 0; var staleAckChecked = false;
        string? lastInputEpoch = null;
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            using var output = await SendAsync(HttpMethod.Get, prefix + "/media/output", owner);
            Check(output.IsSuccessStatusCode, "private output transport");
            if (output.StatusCode != HttpStatusCode.NoContent)
            {
                var frame = await output.Content.ReadFromJsonAsync<JsonElement>();
                var kind = frame.GetProperty("kind").GetString();
                if (kind == "pcm") { Check(Convert.FromBase64String(frame.GetProperty("pcmBase64").GetString()!).Length == 4800, "actual production fake speech converted to24k PCM"); }
                if (kind == "dialogue") { captions++; }
                if (kind == "drain")
                {
                    var played = new { sessionId = frame.GetProperty("sessionId").GetString(), bootId = frame.GetProperty("bootId").GetString(),
                        epoch = frame.GetProperty("epoch").GetString(), outputEpoch = frame.GetProperty("outputEpoch").GetString(),
                        sequence = frame.GetProperty("sequence").GetString(), pcmBytes = frame.GetProperty("pcmBytes").GetString(), drained = true };
                    if (!staleAckChecked)
                    {
                        using var wrong = await SendAsync(HttpMethod.Post, prefix + "/media/played", owner, played with { outputEpoch = "0" });
                        Check(wrong.StatusCode == HttpStatusCode.Conflict, "stale playback ACK denied"); staleAckChecked = true;
                    }
                    using var acknowledged = await SendAsync(HttpMethod.Post, prefix + "/media/played", owner, played);
                    Check(acknowledged.IsSuccessStatusCode, "actual native drain ACK"); drains++;
                }
            }
            state = await ReadAsync(prefix + "/media/state", owner);
            if (state.GetProperty("inputEnabled").GetBoolean() && state.GetProperty("inputEpoch").GetString() != lastInputEpoch)
            {
                lastInputEpoch = state.GetProperty("inputEpoch").GetString();
                using var request = new HttpRequestMessage(HttpMethod.Post, prefix + "/media/input") { Content = new ByteArrayContent(new byte[9600]) };
                request.Content.Headers.ContentType = new("application/octet-stream"); request.Headers.Add("Cookie", owner); request.Headers.Add("Origin", "https://twin.test");
                foreach (var header in new[] { ("Session-Id", "sessionId"), ("Boot-Id", "bootId"), ("Media-Epoch", "epoch"), ("Input-Epoch", "inputEpoch") })
                { request.Headers.Add(header.Item1, state.GetProperty(header.Item2).GetString()); }
                request.Headers.Add("Input-Sequence", "0");
                using var uploaded = await http.SendAsync(request);
                Check(uploaded.IsSuccessStatusCode, "actual native microphone input"); inputs++;
                Check(uploaded.Headers.GetValues("X-Twin-Media-Accepted-Bytes").Single() == "9600", "accepted input bytes are explicit transport evidence");
            }
            if (!state.GetProperty("active").GetBoolean()) { break; }
            await Task.Delay(15);
        }
        Check(inputs == 4 && drains == 5 && captions > 0 && !state.GetProperty("active").GetBoolean()
            && (!state.TryGetProperty("error", out var mediaError) || mediaError.ValueKind == JsonValueKind.Null), "full production fake dialogue completion");
        var store = app.Services.GetRequiredService<TwinStore>();
        for (var count = 0; count < 50 && store.Read("sim-media")!.Snapshot.Reported.Slot == 0; count++) { await Task.Delay(20); }
        snapshot = store.Read("sim-media")!.Snapshot;
        Check(snapshot.Reported.Slot is >= 1 and <= 6 && snapshot.Reported.PhysicalVerification == "unavailable", "native proposal owns presentation/no sensor elevation");
        var technical = JsonSerializer.Serialize(store.Recording("sim-media"), TwinSnapshot.JsonOptions);
        Check(!technical.Contains("pcmBase64") && !technical.Contains("mediaState") && !technical.Contains("Testovací přepis") && !technical.Contains("mediaServiceRequest"), "private media excluded from technical recordings");
        using (var completedCancel = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
            new { operationId = operation, expectedBootId = snapshot.Source.BootId }))
        { Check(completedCancel.IsSuccessStatusCode, "close completed browser HAL"); }
        await Task.Delay(50);
        Check(store.Read("sim-media")!.Snapshot.Reported.Slot == snapshot.Reported.Slot
            && !store.Read("sim-media")!.Snapshot.Reported.NeedsRecovery, "media cancellation never cancels already admitted physical presentation");
        using (var stopped = await SendAsync(HttpMethod.Post, prefix + "/commands", second, new { action = "stop" }))
        { Check(stopped.IsSuccessStatusCode, "scoped second session retains urgent Stop"); }
        Check(store.Read("sim-media")!.Snapshot.Reported.NeedsRecovery, "Stop retains journal recovery");
        var media = app.Services.GetRequiredService<TwinMediaSessions>();
        await PendingStartStopAsync("before-reservation");
        await PendingStartStopAsync("before-admission");
        await LostAdmissionAsync();
        await PendingAdmissionCancelAsync(false);
        await PendingAdmissionCancelAsync(true);
        await OwnerExpiresDuringAdmissionAsync();
        await CancelIsolationAndCapacityAsync();
        await app.StopAsync();
        Check(new TwinMediaSettingsStore(builder.Configuration).Read("sim-media")?.Revision == "1", "settings survive service restart");
        Console.WriteLine("PASS Twin browser media HTTP: scoped ownership, settings, native fake voice/input/drain/proposal, privacy and no physical feedback");

        async Task PendingStartStopAsync(string checkpoint)
        {
            using (var inspection = await SendAsync(HttpMethod.Post, prefix + "/commands", owner, new { action = "inspect" }))
            { Check(inspection.IsSuccessStatusCode, "race setup inspection"); }
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            media.StartCheckpoint = async stage =>
            {
                if (stage == checkpoint) { entered.TrySetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            };
            try
            {
                var pending = SendAsync(HttpMethod.Post, prefix + "/media/start", owner, new
                {
                    operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read("sim-media")!.Snapshot.Source.BootId,
                    audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false
                });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                using (var stop = await SendAsync(HttpMethod.Post, prefix + "/commands", second, new { action = "stop" }))
                { Check(stop.IsSuccessStatusCode, "second owner Stop cancels pending " + checkpoint); }
                using (var inspection = await SendAsync(HttpMethod.Post, prefix + "/commands", owner, new { action = "inspect" }))
                { Check(inspection.IsSuccessStatusCode, "explicit recovery after pending Stop"); }
                resume.TrySetResult();
                using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Check(response.StatusCode == HttpStatusCode.Conflict && problem.GetProperty("code").GetString() == "media_cancelled",
                    "stopped pending request cannot revive after inspection at " + checkpoint);
                Check(!media.Busy("sim-media") && store.Read("sim-media")!.Snapshot.Reported.Slot == 0,
                    "pending Stop leaves no busy actor or physical proposal at " + checkpoint);
            }
            finally { resume.TrySetResult(); media.StartCheckpoint = null; }
        }
        async Task PendingAdmissionCancelAsync(bool pauseBeforeRegistration)
        {
            var request = new { operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read("sim-media")!.Snapshot.Source.BootId,
                audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            media.StartCheckpoint = async stage =>
            {
                if (pauseBeforeRegistration && stage == "before-reservation")
                { entered.TrySetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            };
            try
            {
                Task<HttpResponseMessage>? pending = null;
                if (pauseBeforeRegistration)
                {
                    pending = SendAsync(HttpMethod.Post, prefix + "/media/start", owner, request);
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                using (var cancelled = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
                    new { request.operationId, request.expectedBootId }))
                { Check(cancelled.IsSuccessStatusCode, "cancel pending admission with previous inactive actor"); }
                resume.TrySetResult();
                using var response = await (pending ?? SendAsync(HttpMethod.Post, prefix + "/media/start", owner, request)).WaitAsync(TimeSpan.FromSeconds(5));
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Check(response.StatusCode == HttpStatusCode.Conflict && problem.GetProperty("code").GetString() == "media_cancelled",
                    pauseBeforeRegistration ? "cancelled pre-registration request cannot revive" : "cancel arriving before Start cannot revive");
                Check(!media.Busy("sim-media") && store.Read("sim-media")!.Snapshot.Reported.Slot == 0,
                    "pending cancellation does not admit media or a physical proposal");
            }
            finally { resume.TrySetResult(); media.StartCheckpoint = null; }
        }
        async Task OwnerExpiresDuringAdmissionAsync()
        {
            var ownerSession = app.Services.GetRequiredService<TwinAccess>().Session(owner.Split('=', 2)[1])!;
            var temporaryOwner = ownerSession with { Expires = DateTimeOffset.UtcNow.AddSeconds(1) };
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            media.StartCheckpoint = async stage =>
            {
                if (stage == "before-admission") { entered.TrySetResult(); await resume.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            };
            try
            {
                var pending = media.StartAsync("sim-media", temporaryOwner,
                    new TwinMediaStart(Guid.NewGuid().ToString("D"), store.Read("sim-media")!.Snapshot.Source.BootId, true, false, true, true));
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(temporaryOwner.Expires - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(50));
                resume.TrySetResult();
                var result = await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Check(result.Error == "media_owner_required" && !media.Busy("sim-media")
                    && store.Read("sim-media")!.Snapshot.Reported.Slot == 0, "owner expiry is rechecked before native enqueue");
            }
            finally { resume.TrySetResult(); media.StartCheckpoint = null; }
        }
        async Task CancelIsolationAndCapacityAsync()
        {
            var request = new { operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read("sim-media")!.Snapshot.Source.BootId,
                audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false };
            using (var admitted = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner, request))
            { Check(admitted.IsSuccessStatusCode, "capacity fixture active admission"); }
            using (var wrongOwner = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", second,
                new { request.operationId, request.expectedBootId }))
            { Check(wrongOwner.IsSuccessStatusCode && media.Busy("sim-media"), "other owner's cancellation cannot cancel current actor"); }
            using (var wrongBoot = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
                new { request.operationId, expectedBootId = Guid.NewGuid().ToString("D") }))
            {
                var problem = await wrongBoot.Content.ReadFromJsonAsync<JsonElement>();
                Check(wrongBoot.StatusCode == HttpStatusCode.Conflict && problem.GetProperty("code").GetString() == "media_boot_mismatch"
                    && media.Busy("sim-media"), "wrong boot cannot poison or cancel current actor");
            }
            using (var unrelated = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
                new { operationId = Guid.NewGuid().ToString("D"), request.expectedBootId }))
            { Check(unrelated.IsSuccessStatusCode && media.Busy("sim-media"), "unrelated operation cannot cancel current actor"); }
            var ownerSession = app.Services.GetRequiredService<TwinAccess>().Session(owner.Split('=', 2)[1])!;
            var expired = ownerSession with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) };
            var invalidUuid = await media.CancelAsync("sim-media", ownerSession,
                new TwinMediaCancel(OperationId: "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", ExpectedBootId: request.expectedBootId));
            Check(invalidUuid == "media_admission_mismatch", "cancellation rejects noncanonical UUIDs");
            var wrongScope = await media.CancelAsync("sim-media", ownerSession with { Principal = new("control", "other-sim", "simulation") },
                new TwinMediaCancel(OperationId: request.operationId, ExpectedBootId: request.expectedBootId));
            Check(wrongScope == "media_owner_required", "cancellation rejects wrong instance scope");
            var expiredCancel = await media.CancelAsync("sim-media", expired,
                new TwinMediaCancel(OperationId: request.operationId, ExpectedBootId: request.expectedBootId));
            Check(expiredCancel == "media_owner_required" && media.Busy("sim-media"), "expired owner cannot cancel active media");
            var denied = await media.StartAsync("sim-media", expired,
                new TwinMediaStart(Guid.NewGuid().ToString("D"), request.expectedBootId, true, false, true, true));
            Check(denied.Error == "media_owner_required" && media.Busy("sim-media"), "expired owner cannot admit or disturb active media");
            var saturated = false;
            for (var count = 0; count <= 64; count++)
            {
                using var cancellation = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
                    new { operationId = Guid.NewGuid().ToString("D"), request.expectedBootId });
                if (cancellation.StatusCode == HttpStatusCode.Conflict)
                {
                    var problem = await cancellation.Content.ReadFromJsonAsync<JsonElement>();
                    Check(problem.GetProperty("code").GetString() == "media_cancellation_capacity", "bounded cancellation memory reports saturation");
                    saturated = true; break;
                }
                Check(cancellation.IsSuccessStatusCode, "bounded cancellation memory records admission");
            }
            Check(saturated && media.Busy("sim-media"), "memory saturates without cancelling unrelated active media");
            using (var cancelled = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner,
                new { request.operationId, request.expectedBootId }))
            { Check(cancelled.IsSuccessStatusCode && !media.Busy("sim-media"), "matching active actor cancels even with full memory"); }
            using var fresh = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner,
                request with { operationId = Guid.NewGuid().ToString("D") });
            var blocked = await fresh.Content.ReadFromJsonAsync<JsonElement>();
            Check(fresh.StatusCode == HttpStatusCode.Conflict && blocked.GetProperty("code").GetString() == "media_cancellation_capacity",
                "fresh admission fails closed at capacity");
        }
        async Task LostAdmissionAsync()
        {
            media.StartCheckpoint = stage => stage == "after-admission"
                ? Task.FromException(new TimeoutException("Offline fixture: accepted native reply was lost.")) : Task.CompletedTask;
            try
            {
                using var response = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner, new
                {
                    operationId = Guid.NewGuid().ToString("D"), expectedBootId = store.Read("sim-media")!.Snapshot.Source.BootId,
                    audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false
                });
                var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                Check(response.StatusCode == HttpStatusCode.Conflict && problem.GetProperty("code").GetString() == "media_admission_outcome_unknown",
                    "lost accepted admission returns typed uncertainty");
                Check(!media.Busy("sim-media"), "lost admission clears Starting and invalidates actor");
            }
            finally { media.StartCheckpoint = null; }
            // Cancellation wakes the actual native worker; wait one bounded
            // owner/worker scheduling interval before an explicit fresh input.
            await Task.Delay(100);
            var sessionOwner = app.Services.GetRequiredService<TwinAccess>().Session(owner.Split('=', 2)[1])!;
            var state = media.State("sim-media", sessionOwner);
            Check(state is { Active: false } && store.Read("sim-media")!.Snapshot.Reported.Slot == 0,
                "late native replies after lost admission cannot revive actor or presentation");
            var freshOperation = Guid.NewGuid().ToString("D");
            using (var fresh = await SendAsync(HttpMethod.Post, prefix + "/media/start", owner, new
            {
                operationId = freshOperation, expectedBootId = store.Read("sim-media")!.Snapshot.Source.BootId,
                audioConsent = true, photoConsent = false, micAvailable = true, speakerAvailable = true, cameraAvailable = false
            }))
            { Check(fresh.IsSuccessStatusCode, "fresh media admission succeeds after uncertain cleanup"); }
            using (var cancelled = await SendAsync(HttpMethod.Post, prefix + "/media/cancel", owner, new
                { operationId = freshOperation, expectedBootId = store.Read("sim-media")!.Snapshot.Source.BootId }))
            { Check(cancelled.IsSuccessStatusCode, "fresh offline session cleanup"); }
        }

        Task<HttpResponseMessage> SendAsync(HttpMethod method, string route, string cookie, object? body = null)
        {
            var request = new HttpRequestMessage(method, route); request.Headers.Add("Cookie", cookie); request.Headers.Add("Origin", "https://twin.test");
            if (body is not null) { request.Content = JsonContent.Create(body); }
            return http.SendAsync(request);
        }
        async Task<JsonElement> ReadAsync(string route, string cookie)
        { using var response = await SendAsync(HttpMethod.Get, route, cookie); Check(response.IsSuccessStatusCode, "read " + route); return await response.Content.ReadFromJsonAsync<JsonElement>(); }
        async Task<string> LoginAsync(HttpClient client, string credential)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/session") { Content = JsonContent.Create(new { credential }) };
            request.Headers.Add("Origin", "https://twin.test"); using var response = await client.SendAsync(request);
            Check(response.IsSuccessStatusCode, "login"); return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }
    }
    private static async Task FramingAsync()
    {
        await using var wire = new MemoryStream();
        var header = Encoding.UTF8.GetBytes("{\"media\":{\"byteLength\":4}}\n");
        wire.Write(header); wire.Write(new byte[] { 10, 0, 13, 255, 10 }); wire.Position = 0;
        var reply = await TwinNativeFraming.ReadAsync(wire, CancellationToken.None);
        using (reply.Document) { Check(reply.Attachment.SequenceEqual(new byte[] { 10, 0, 13, 255 }), "binary framing does not consume PCM newlines"); }
        await using var truncated = new MemoryStream(Encoding.UTF8.GetBytes("{\"media\":{\"byteLength\":4}}\nxx"));
        var rejected = false;
        try { await TwinNativeFraming.ReadAsync(truncated, CancellationToken.None); } catch (EndOfStreamException) { rejected = true; }
        Check(rejected, "truncated frame rejected");
        Console.WriteLine("PASS Twin binary framing bounds/truncation");
    }
    private static void CancellationMemory()
    {
        var now = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        var boot = Guid.NewGuid();
        var memory = new TwinAdmissionCancellations();
        var first = new TwinAdmissionIdentity("owner", boot, Guid.NewGuid());
        Check(!memory.Record(first, now, now), "expired owner cannot allocate cancellation memory");
        Check(memory.Record(first, now.AddMinutes(1), now), "first cancellation remembered");
        Check(memory.Error(first, now, fresh: true) == "media_cancelled", "exact cancellation denied");
        Check(memory.Error(first with { OwnerId = "other-owner" }, now, fresh: true) is null, "cancellation owner isolation");
        for (var count = 1; count < 64; count++)
        { Check(memory.Record(new("owner", boot, Guid.NewGuid()), now.AddMinutes(1), now), "cancellation capacity entry " + count); }
        var overflow = new TwinAdmissionIdentity("later-owner", boot, Guid.NewGuid());
        Check(!memory.Record(overflow, now.AddMinutes(2), now), "65th cancellation does not evict an entry");
        Check(memory.Error(first, now, fresh: true) == "media_cancelled", "saturation preserves exact tombstones");
        Check(memory.Error(overflow, now.AddMinutes(1), fresh: true) == "media_cancellation_capacity",
            "overflow barrier prevents revival after earlier tombstones expire");
        Check(memory.Error(overflow, now.AddMinutes(2), fresh: true) is null, "expired owner releases overflow barrier");
        Check(memory.Record(first, now.AddMinutes(3), now.AddMinutes(2)), "expired slots can be reused");
        Check(memory.Error(first with { BootId = Guid.NewGuid() }, now.AddMinutes(2), fresh: true) is null, "old-boot cancellation does not match new boot");
        Check(memory.Error(first, now.AddMinutes(2), fresh: true) == "media_cancelled", "new boot lookup cannot erase an unexpired cancellation");
        Console.WriteLine("PASS Twin cancellation memory: owner/boot isolation, 64 entries, no eviction, expiry and overflow barrier");
    }
    private static void ProviderSource()
    {
        var root = Path.Combine(Path.GetTempPath(), "twin-provider-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "existing-server.env");
        File.WriteAllText(source, "OPENAI_API_KEY=offline-placeholder\nDevices__0__DeviceId=physical-private\nBackend__DatabasePath=/production.sqlite\n");
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DigitalTwin:DataRoot"] = root, ["Backend:DatabasePath"] = Path.Combine(root, "isolated.sqlite"),
            ["DigitalTwin:ExternalServicesAuthorized"] = "true", ["DigitalTwin:ProviderEnvFile"] = source
        });
        TwinProviderCredentials.Load(configuration);
        Check(configuration["OPENAI_API_KEY"] == "offline-placeholder" && configuration["Devices:0:DeviceId"] is null
            && configuration["Backend:DatabasePath"] == Path.Combine(root, "isolated.sqlite"), "reuse only provider key without production identities/data");
        configuration["Backend:Memory:Enabled"] = "true";
        var rejected = false;
        try { TwinProviderCredentials.Load(configuration); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "provider refuses real guest memory");
        Console.WriteLine("PASS Twin provider source isolation (offline fixture only)");
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Check(bool result, string name) { if (!result) { throw new InvalidOperationException("Twin media acceptance failed: " + name); } }
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.DigitalTwin;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Kufr.Backend.Features.Conversations;

internal static class TwinTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-twin-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var observer = new string('a', 64);
        var control = new string('b', 64);
        var uploader = new string('c', 64);
        var liveUploader = new string('d', 64);
        var settings = new Dictionary<string, string?>
        {
            ["DigitalTwin:DataRoot"] = directory, ["DigitalTwin:Origin"] = "https://twin.test",
            ["DigitalTwin:Instances:sim-test:Mode"] = "simulation",
            ["DigitalTwin:Instances:sim-test:DeviceId"] = "sim-test",
            ["DigitalTwin:Instances:live-test:Mode"] = "live",
            ["DigitalTwin:Instances:live-test:DeviceId"] = "live-test",
            ["DigitalTwin:Credentials:0:Role"] = "observe", ["DigitalTwin:Credentials:0:Sha256"] = Hash(observer),
            ["DigitalTwin:Credentials:1:Role"] = "control", ["DigitalTwin:Credentials:1:Mode"] = "simulation",
            ["DigitalTwin:Credentials:1:InstanceId"] = "sim-test", ["DigitalTwin:Credentials:1:Sha256"] = Hash(control),
            ["DigitalTwin:Credentials:2:Role"] = "ingest", ["DigitalTwin:Credentials:2:Mode"] = "simulation",
            ["DigitalTwin:Credentials:2:InstanceId"] = "sim-test", ["DigitalTwin:Credentials:2:Sha256"] = Hash(uploader),
            ["DigitalTwin:Credentials:3:Role"] = "ingest", ["DigitalTwin:Credentials:3:Mode"] = "live",
            ["DigitalTwin:Credentials:3:InstanceId"] = "live-test", ["DigitalTwin:Credentials:3:Sha256"] = Hash(liveUploader)
        };
        var deviceCredential = new string('e', 64);
        settings["Devices:0:DeviceId"] = "live-test";
        settings["Devices:0:CredentialSha256"] = Hash(deviceCredential);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DeviceAccessStore>();
        builder.Services.AddSingleton<TwinAccess>();
        builder.Services.AddSingleton<TwinStore>();
        builder.Services.AddSingleton<TwinHost>();
        await using var app = builder.Build();
        app.MapDigitalTwin();
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert((await http.GetAsync("/v1/twin/instances")).StatusCode == HttpStatusCode.Unauthorized, "anonymous read");
        var observeCookie = await LoginAsync(http, observer);
        var controlCookie = await LoginAsync(http, control);
        Assert((await LoginResponseAsync(http, uploader, "https://twin.test")).StatusCode == HttpStatusCode.Unauthorized, "ingest cannot log in");
        Assert((await LoginResponseAsync(http, observer, "https://evil.test")).StatusCode == HttpStatusCode.Forbidden, "cross origin login");

        var snapshot = Snapshot("sim-test", "simulation", "boot-1", "9007199254740993", "40");
        Assert(snapshot.Valid(), "valid exact decimal snapshot");
        Assert((snapshot with { Reported = snapshot.Reported with { LedGrb = null } }).Valid(), "unavailable LED driver state is unknown");
        Assert((snapshot with { Source = snapshot.Source with { CalibrationRevision = null } }).Valid(), "unconfigured calibration is unknown");
        Assert((await IngestAsync(http, "sim-test", snapshot, observer)).StatusCode == HttpStatusCode.Forbidden, "observer cannot upload");
        Assert((await IngestAsync(http, "live-test", snapshot, uploader)).StatusCode == HttpStatusCode.Forbidden, "sim uploader cannot upload live");
        Assert((await IngestAsync(http, "sim-test", snapshot, uploader)).StatusCode == HttpStatusCode.NoContent, "upload");
        Assert((await IngestAsync(http, "sim-test", snapshot, uploader)).StatusCode == HttpStatusCode.NoContent, "duplicate idempotent");
        Assert((await IngestAsync(http, "sim-test", snapshot with { TimeMs = "41" }, uploader)).StatusCode == HttpStatusCode.Conflict, "conflicting duplicate");
        Assert((await IngestAsync(http, "sim-test", snapshot with { Sequence = "9007199254740994",
            Source = snapshot.Source with { BuildId = "changed" } }, uploader)).StatusCode == HttpStatusCode.Conflict, "immutable boot provenance");
        Assert((await IngestAsync(http, "sim-test", snapshot with { Sequence = "9007199254740994", TimeMs = "39" }, uploader)).StatusCode == HttpStatusCode.Conflict, "regressed clock");
        Assert((await IngestAsync(http, "sim-test", snapshot with { Reported = snapshot.Reported with { PhysicalVerification = "measured" } }, uploader)).StatusCode == HttpStatusCode.Conflict, "no invented sensor");
        Assert((await IngestAsync(http, "sim-test", snapshot with { Trace = JsonDocument.Parse("{\"events\":[],\"transcript\":\"private\"}").RootElement.Clone() }, uploader)).StatusCode == HttpStatusCode.Conflict, "no private trace fields");
        var boot2 = Snapshot("sim-test", "simulation", "boot-2", "1", "0");
        Assert((await IngestAsync(http, "sim-test", boot2, uploader)).StatusCode == HttpStatusCode.NoContent, "new boot");
        Assert((await IngestAsync(http, "sim-test", snapshot with { Sequence = "9007199254740994" }, uploader)).StatusCode == HttpStatusCode.Conflict, "retired boot");
        Assert((await IngestAsync(http, "live-test", Snapshot("live-test", "live", "boot-real", "1", "100"), liveUploader)).StatusCode == HttpStatusCode.NoContent, "live contract");
        var devices = app.Services.GetRequiredService<DeviceAccessStore>();
        var ordinaryToken = devices.IssueToken("live-test")!.AccessToken;
        Assert((await IngestAsync(http, "live-test", Snapshot("live-test", "live", "boot-real", "2", "101"), ordinaryToken)).StatusCode == HttpStatusCode.Forbidden, "ordinary device token cannot ingest");
        using var grantRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/device-token");
        grantRequest.Headers.Authorization = new("Bearer", deviceCredential);
        var grantResponse = await http.SendAsync(grantRequest);
        Assert(grantResponse.IsSuccessStatusCode, "scoped device grant");
        var grant = await grantResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sourceToken = grant.GetProperty("accessToken").GetString()!;
        Assert(devices.VerifyAccessToken(sourceToken) is null, "telemetry grant cannot control device backend");
        Assert((await IngestAsync(http, "live-test", Snapshot("live-test", "live", "boot-real", "2", "101"), sourceToken)).StatusCode == HttpStatusCode.NoContent, "scoped firmware ingest");
        Assert((await SendAsync(http, HttpMethod.Get, "/v1/twin/instances/live-test", observeCookie)).StatusCode == HttpStatusCode.OK, "observer read live");
        Assert((await SendAsync(http, HttpMethod.Get, "/v1/twin/instances/live-test", controlCookie)).StatusCode == HttpStatusCode.Forbidden, "sim principal cannot read live");
        Assert((await SendAsync(http, HttpMethod.Post, "/v1/twin/instances/live-test/commands", observeCookie, new { action = "stop" })).StatusCode == HttpStatusCode.Forbidden, "observer cannot command live");
        Assert((await SendAsync(http, HttpMethod.Post, "/v1/twin/instances/sim-test/commands", observeCookie, new { action = "text" })).StatusCode == HttpStatusCode.Forbidden, "observer cannot command simulation");
        Assert((await SendAsync(http, HttpMethod.Post, "/v1/twin/instances/live-test/commands", controlCookie, new { action = "inspect" })).StatusCode == HttpStatusCode.Forbidden, "simulation cannot command live");
        var store = app.Services.GetRequiredService<TwinStore>();
        for (var sequence = 2; sequence < 200; sequence++)
        {
            Assert(store.Accept(boot2 with { Sequence = sequence.ToString(), TimeMs = sequence.ToString() }) is null, "bounded ingest");
        }
        Assert(store.Recording("sim-test").Length == 128, "bounded browser history");
        var view = JsonSerializer.Serialize(store.View(store.Read("sim-test")!), TwinSnapshot.JsonOptions);
        Assert(view.Contains("serverDroppedSnapshots") && !view.Contains("transcript"), "overflow visible");
        await app.StopAsync();
        TestFakeIsolation(directory);
        Console.WriteLine("PASS Twin HTTP authorization, epoch, decimal precision, privacy and bounded buffer checks");
    }
    private static void TestFakeIsolation(string directory)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(directory, "fake-isolation.sqlite"),
            ["DigitalTwin:DataRoot"] = directory,
            ["DigitalTwin:Instances:sim-integration:Mode"] = "simulation",
            ["DigitalTwin:Instances:sim-integration:Profile"] = "integration",
            ["DigitalTwin:Instances:sim-integration:DeviceId"] = "sim-integration"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var policy = new TwinFakeInputPolicy(configuration);
        Assert(policy.Allows("sim-integration") && !policy.Allows("live-device"), "explicit fake identity scope");
        using var journal = new OperationJournal(configuration, TimeProvider.System);
        var calls = 0;
        using var runner = new TextDialogRunner((_, _, _, _, _) =>
        {
            calls++;
            return Task.FromException<TextDialogResponse>(new InvalidOperationException("Provider must not be called."));
        });
        runner.SetEnabled(true);
        var store = new ConversationStore(TimeProvider.System, journal, runner, twinFakeInputs: policy);
        var boot = Guid.NewGuid();
        var created = store.Create("sim-integration", Guid.NewGuid(), "test-create"u8,
            new CreateConversationRequest("sim-integration", boot, 0, "test-catalog", "cs-CZ", new ConversationConsent(false, false)));
        Assert(created.Response is not null, "fake conversation");
        var request = new TextTurnRequest(Guid.NewGuid(), boot, 0, "synthetic", 63, 1, "test-catalog");
        var accepted = store.SubmitTurn("sim-integration", created.Response!.ConversationId, Guid.NewGuid(), "test-turn"u8, request);
        Assert(accepted.Status == 202 && calls == 0, "runtime provider switch cannot affect isolated fake profile");
        settings["Backend:Memory:Enabled"] = "true";
        var rejected = false;
        try { _ = new TwinFakeInputPolicy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "fake profile cannot use guest memory");
        settings["Backend:Memory:Enabled"] = "false";
        settings["Backend:Dialog:Mode"] = "responses";
        rejected = false;
        try { _ = new TwinFakeInputPolicy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build()); }
        catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, "external service requires separate opt-in");
        settings["DigitalTwin:ExternalServicesAuthorized"] = "true";
        var external = new TwinFakeInputPolicy(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        Assert(!external.Allows("sim-integration"), "authorized external profile does not substitute fake decisions");
    }
    private static TwinSnapshot Snapshot(string id, string mode, string boot, string sequence, string time) => new(1,
        new TwinSource(mode, id, id, boot, boot, "test", "test-build", "B-effects", new string('a', 64), null, "1"),
        sequence, time, "1", new TwinReported("idle", 0, 63, "0", false, "unavailable",
            Enumerable.Range(1, 6).Select(slot => new TwinServo(slot, null, "unknown")).ToArray(), false, new int[108], 0, "unavailable"),
        null, JsonDocument.Parse("{\"events\":[]}").RootElement.Clone(), new TwinTransport("0"), ["observe"]);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Assert(bool value, string name) { if (!value) { throw new InvalidOperationException("Twin check failed: " + name); } }
    private static Task<HttpResponseMessage> LoginResponseAsync(HttpClient http, string credential, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/session") { Content = JsonContent.Create(new { credential }) };
        request.Headers.Add("Origin", origin);
        return http.SendAsync(request);
    }
    private static async Task<string> LoginAsync(HttpClient http, string credential)
    {
        var response = await LoginResponseAsync(http, credential, "https://twin.test");
        Assert(response.IsSuccessStatusCode, "login");
        return response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }
    private static Task<HttpResponseMessage> IngestAsync(HttpClient http, string id, TwinSnapshot snapshot, string credential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/twin/ingest/" + id)
        { Content = JsonContent.Create(snapshot, options: TwinSnapshot.JsonOptions) };
        request.Headers.Authorization = new("Bearer", credential);
        return http.SendAsync(request);
    }
    private static Task<HttpResponseMessage> SendAsync(HttpClient http, HttpMethod method, string path, string cookie, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", "https://twin.test");
        if (body is not null) { request.Content = JsonContent.Create(body); }
        return http.SendAsync(request);
    }
}

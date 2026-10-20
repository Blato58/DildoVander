using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Show;
using Kufr.Backend.Features.Speech;
using Kufr.Backend.Features.VoiceTurns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

internal static class ShowAmbientPreviewTests
{
    private const string Device = "ambient-preview-device";

    internal static async Task RunAsync()
    {
        await OneClaimAndUnchangedShowAsync();
        await UnavailableFailureAndCancellationAsync();
        await RetentionCapacityAndRestartAsync();
        await EndpointsRequireOperateAsync();
        await MapsWithoutPreviewRegistrationAsync();
        Console.WriteLine("Phone ambient preview tests passed: operator scope, bounded audio, idempotency, no GET generation, cancellation and unchanged show state (no network provider).");
    }

    private static async Task MapsWithoutPreviewRegistrationAsync()
    {
        using var fixture = new Fixture();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(fixture.Show);
        builder.Services.AddSingleton<ServerAdminAccess>();
        await using var app = builder.Build();
        app.MapShowEndpoints();
        var endpoints = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints).ToArray();
        Check(endpoints.Length >= 5, "Show route metadata does not infer preview as an additional body when an unrelated host omits its registration");
    }

    private static async Task OneClaimAndUnchangedShowAsync()
    {
        using var fixture = new Fixture();
        var operation = Guid.NewGuid();
        var state = JsonSerializer.Serialize(fixture.Show.Read(Device));
        Check(fixture.Preview.Read(Device, operation).StatusCode == 404 && fixture.Calls == 0,
            "GET cannot create a job");
        Check(fixture.Preview.Start(Device, operation).StatusCode == 202, "POST queues a preview");
        await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Preview.Start(Device, operation).StatusCode == 202
            && fixture.Preview.Read(Device, operation).Response.Status == "pending" && fixture.Calls == 1,
            "repeated POST and GET share exactly one job");
        Check(fixture.Preview.Start(Device, Guid.NewGuid()).Response.Code == "preview_busy",
            "another preview cannot compete with active preview work");
        Check(fixture.Voice.TryAcquire(), "a pending phone preview leaves real device narration's slot available");
        fixture.Voice.Release();
        fixture.Audio.SetResult(Wav());
        await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var ready = fixture.Preview.Read(Device, operation);
        Check(ready is { StatusCode: 200, Response.Status: "ready", Response.Synthetic: false }
            && Convert.FromBase64String(ready.Response.WavBase64!).Length <= ShowAmbientPreview.MaximumWavBytes,
            "ready result contains bounded real-provider WAV");
        Check(fixture.Preview.Start(Device, operation).Response == ready.Response && fixture.Calls == 1,
            "completed POST retry reuses audio without billing");
        Check(fixture.Preview.Read("other-device", operation).StatusCode == 404,
            "another device cannot read preview audio");
        Check(fixture.Sql("SELECT count(*) FROM turn_operations WHERE kind='speech'") == 1
            && fixture.Sql("SELECT sum(reserved_micro_usd) FROM cost_reservations") == ShowSpeechProvider.SpeechReservationMicroUsd,
            "one conservative speech reservation audits the provider attempt");
        Check(JsonSerializer.Serialize(fixture.Show.Read(Device)) == state
            && fixture.Sql("SELECT count(*) FROM show_director_commands") == 0,
            "phone previews cannot mutate show state or queue actual device commands");
        Check(fixture.Voice.TryAcquire(), "real device narration can occupy its dedicated provider slot");
        var independentPreview = Guid.NewGuid();
        Check(fixture.Preview.Start(Device, independentPreview).StatusCode == 202,
            "a held real device slot does not block phone preview admission");
        await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Preview.Read(Device, independentPreview).Response.Status == "ready" && fixture.Calls == 2,
            "phone preview completes independently while real device narration holds its slot");
        Check(!fixture.Voice.TryAcquire(), "preview completion cannot release the slot owned by real device narration");
        fixture.Voice.Release();
    }

    private static async Task UnavailableFailureAndCancellationAsync()
    {
        using (var fixture = new Fixture("off"))
        {
            Check(fixture.Preview.Start(Device, Guid.NewGuid()).Response.Code == "preview_unavailable"
                && fixture.Calls == 0 && fixture.Sql("SELECT count(*) FROM turn_operations") == 0,
                "off mode never claims speech");
        }
        using (var fixture = new Fixture())
        {
            fixture.Runner.SetEnabled(false);
            Check(fixture.Preview.Start(Device, Guid.NewGuid()).StatusCode == 503 && fixture.Calls == 0,
                "disabled speech provider is explicitly unavailable");
            Check(fixture.Preview.Start(Device, Guid.Empty).StatusCode == 400, "empty operation identity is invalid");
        }
        using (var fixture = new Fixture())
        {
            var operation = Guid.NewGuid();
            fixture.Preview.Start(Device, operation);
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Audio.SetException(new IOException("uncertain fixture failure"));
            await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Preview.Read(Device, operation).Response is { Status: "failed", Code: "preview_failed" }
                && fixture.Preview.Start(Device, operation).Response.Status == "failed" && fixture.Calls == 1,
                "uncertain provider failure is terminal with no automatic retry");
            Check(fixture.Sql("SELECT count(*) FROM turn_operations WHERE status='failed'") == 1,
                "failed attempt keeps its audited claim");
        }
        using (var fixture = new Fixture())
        {
            var operation = Guid.NewGuid();
            fixture.Preview.Start(Device, operation);
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Stopping.Cancel();
            await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Preview.Read(Device, operation).Response.Code == "preview_cancelled" && fixture.Calls == 1,
                "host cancellation cannot revive or regenerate preview audio");
            Check(fixture.Voice.TryAcquire(), "cancelled preview leaves the real device narration slot available");
            fixture.Voice.Release();
        }
        using (var fixture = new Fixture())
        {
            var operation = Guid.NewGuid();
            fixture.Preview.Start(Device, operation);
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Preview.Dispose();
            Check(fixture.Preview.DrainAsync().IsCompleted && fixture.Calls == 1
                && fixture.Sql("SELECT count(*) FROM turn_operations WHERE status='failed'") == 1,
                "service disposal drains cancellation before the journal can be disposed");
        }
        using (var fixture = new Fixture())
        {
            var operation = Guid.NewGuid();
            fixture.Preview.Start(Device, operation);
            await fixture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oversized = PcmSpeechAudio.ToDeviceWav(new byte[24_000 * 2 * 9]);
            fixture.Audio.SetResult(oversized);
            await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(fixture.Preview.Read(Device, operation).Response.Code == "preview_failed",
                "valid WAV longer than eight seconds is rejected");
        }
    }

    private static async Task RetentionCapacityAndRestartAsync()
    {
        using var fixture = new Fixture("fake");
        var operation = Guid.NewGuid();
        fixture.Preview.Start(Device, operation);
        await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Check(fixture.Preview.Read(Device, operation).Response is { Status: "ready", Synthetic: true }
            && fixture.Calls == 0 && fixture.Sql("SELECT count(*) FROM cost_reservations") == 0,
            "explicit fake WAV is marked synthetic and never fabricates paid usage");
        for (var index = 1; index < ShowAmbientPreview.MaximumEntries; index++)
        {
            Check(fixture.Preview.Start(Device, Guid.NewGuid()).StatusCode == 202, "bounded preview capacity available");
            await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Check(fixture.Preview.Start("other-device", Guid.NewGuid()).Response.Code == "preview_busy",
            "entry capacity is global rather than an unbounded per-device dictionary");
        fixture.Clock.Advance(ShowAmbientPreview.Retention);
        Check(fixture.Preview.Read(Device, operation).Response is { Status: "failed", Code: "preview_expired" }
            && fixture.Preview.Start(Device, operation).StatusCode == 410,
            "retention expiry erases audio and POST cannot regenerate the consumed operation");
        var fresh = Guid.NewGuid();
        Check(fixture.Preview.Start(Device, fresh).StatusCode == 202, "expiry frees bounded storage");
        await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Preview.Dispose();
        fixture.Journal.Dispose();
        using var reopenedJournal = new OperationJournal(fixture.Configuration, fixture.Clock);
        using var restarted = new ShowAmbientPreview(fixture.Configuration, reopenedJournal, fixture.Voice, fixture.Clock);
        Check(restarted.Read(Device, fresh).StatusCode == 410 && restarted.Start(Device, fresh).StatusCode == 410,
            "SQLite reopen preserves consumed identities during the journal's 24-hour window");
        var changedConfiguration = new ConfigurationBuilder().AddConfiguration(fixture.Configuration)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Backend:Voice:Voice"] = "cedar" }).Build();
        using var changed = new ShowAmbientPreview(changedConfiguration, reopenedJournal, fixture.Voice, fixture.Clock);
        Check(changed.Start(Device, fresh).Response.Code == "preview_operation_mismatch",
            "request identity also binds repertoire, voice, speed and synthetic mode");
    }

    private static async Task EndpointsRequireOperateAsync()
    {
        using var fixture = new Fixture("fake");
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddConfiguration(fixture.Configuration);
        builder.Services.AddSingleton(fixture.Show);
        builder.Services.AddSingleton(fixture.Preview);
        builder.Services.AddSingleton<ServerAdminAccess>();
        builder.Services.AddDbContext<AccountDatabase>(options => options.UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = Path.Combine(fixture.DirectoryPath, "identity.sqlite") }.ToString()));
        await using var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await db.Database.EnsureCreatedAsync();
            db.ResourceGrants.AddRange(
                new AccountResourceGrant { Id = "read", UserId = "reader", ResourceType = "device", ResourceId = Device, Permission = "read" },
                new AccountResourceGrant { Id = "operate", UserId = "operator", ResourceType = "device", ResourceId = Device, Permission = "operate" });
            await db.SaveChangesAsync();
        }
        // Deliberately supply test principals only in this local fixture. Real cookie validation
        // is covered by AccountIdentityTests; this checks endpoint permission selection and scope.
        app.Use(async (context, next) =>
        {
            var user = context.Request.Headers["X-Test-User"].ToString();
            if (user.Length > 0)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([
                    new Claim(ClaimTypes.NameIdentifier, user), new Claim(AccountIdentity.SessionClaim, "fixture-session"),
                    new Claim(ClaimTypes.Role, user == "owner" ? AccountRoles.Owner : AccountRoles.Operator)
                ], "fixture"));
            }
            await next(context);
        });
        app.MapShowAmbientPreviews();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var operation = Guid.NewGuid();
        var path = $"/v1/show/devices/{Device}/ambient-previews";
        using (var response = await client.PostAsJsonAsync(path, new { operationId = operation }))
        { Check(response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.CacheControl?.NoStore == true,
            "unauthenticated POST is rejected without caching"); }
        client.DefaultRequestHeaders.Add("X-Test-User", "reader");
        client.DefaultRequestHeaders.Add("X-DV-Device-Id", Device);
        using (var response = await client.GetAsync(path + "/" + operation))
        { Check(response.StatusCode == HttpStatusCode.Forbidden, "GET requires operate even with a read grant and Operator role"); }
        using (var response = await client.PostAsJsonAsync(path, new { operationId = operation }))
        { Check(response.StatusCode == HttpStatusCode.Forbidden, "read-only operator cannot generate audio"); }
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "operator");
        using (var response = await client.PostAsJsonAsync(path, new { operationId = operation, text = "untrusted provider input" }))
        { Check(response.StatusCode == HttpStatusCode.BadRequest && fixture.Calls == 0,
            "preview input matches the documented UUID-only request without dispatching provider work"); }
        using (var response = await client.GetAsync(path + "/" + operation))
        { Check(response.StatusCode == HttpStatusCode.NotFound && fixture.Calls == 0, "authorized GET never creates audio"); }
        using (var response = await client.PostAsJsonAsync(path, new { operationId = operation }))
        { Check(response.StatusCode == HttpStatusCode.Accepted && response.Headers.CacheControl?.NoStore == true,
            "operator POST creates one no-store pending result"); }
        await fixture.Preview.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var response = await client.GetAsync(path + "/" + operation))
        {
            var body = await response.Content.ReadFromJsonAsync<ShowAmbientPreviewResponse>();
            Check(response.StatusCode == HttpStatusCode.OK && body is { Status: "ready", Synthetic: true, WavBase64: not null }
                && response.Headers.CacheControl?.NoStore == true, "authorized GET returns the bound result contract");
        }
        client.DefaultRequestHeaders.Remove("X-DV-Device-Id");
        client.DefaultRequestHeaders.Add("X-DV-Device-Id", "other-device");
        using (var response = await client.GetAsync(path + "/" + operation))
        { Check(response.StatusCode == HttpStatusCode.Forbidden, "device scope remains mandatory for result reads"); }
        client.DefaultRequestHeaders.Remove("X-Test-User");
        client.DefaultRequestHeaders.Add("X-Test-User", "owner");
        client.DefaultRequestHeaders.Remove("X-DV-Device-Id");
        client.DefaultRequestHeaders.Add("X-DV-Device-Id", "missing-device");
        using (var response = await client.PostAsJsonAsync("/v1/show/devices/missing-device/ambient-previews", new { operationId = Guid.NewGuid() }))
        { Check(response.StatusCode == HttpStatusCode.Forbidden, "unconfigured device cannot be selected even by account owner"); }
        await app.StopAsync();
    }

    private static byte[] Wav() => PcmSpeechAudio.ToDeviceWav(new byte[4800]);
    private static void Check(bool condition, string message)
    { if (!condition) { throw new InvalidOperationException("Ambient preview assertion failed: " + message); } }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => now.UtcTicks;
        internal void Advance(TimeSpan span) => now += span;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "kufr-ambient-preview-" + Guid.NewGuid().ToString("N"));
        internal readonly Clock Clock = new();
        internal readonly CancellationTokenSource Stopping = new();
        internal readonly TaskCompletionSource<byte[]> Audio = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly IConfiguration Configuration;
        internal readonly OperationJournal Journal;
        internal readonly ShowCoordinator Show;
        internal readonly VoiceProviderRunner Runner;
        internal readonly ShowSpeechProvider Voice;
        internal readonly ShowAmbientPreview Preview;
        internal int Calls;

        internal Fixture(string mode = "configured")
        {
            Directory.CreateDirectory(DirectoryPath);
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(DirectoryPath, "journal.sqlite"),
                ["Devices:0:DeviceId"] = Device, ["Devices:1:DeviceId"] = "other-device",
                ["Show:DeviceCueSpeechMode"] = mode
            }).Build();
            Journal = new OperationJournal(Configuration, Clock);
            var memory = new EveningMemoryStore(Configuration, Journal, Clock);
            Show = new ShowCoordinator(Configuration, Journal, memory, Clock);
            Runner = new VoiceProviderRunner((_, _) => Task.FromResult("unused"), async (_, _, token) =>
            {
                Interlocked.Increment(ref Calls);
                Started.TrySetResult();
                return (await Audio.Task.WaitAsync(token)).ToArray();
            }, Stopping.Token);
            Voice = new ShowSpeechProvider(Runner);
            Preview = new ShowAmbientPreview(Configuration, Journal, Voice, Clock);
        }

        internal long Sql(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "journal.sqlite") }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public void Dispose()
        {
            Stopping.Cancel();
            Audio.TrySetResult(Wav());
            Preview.DrainAsync().GetAwaiter().GetResult();
            Preview.Dispose();
            Voice.Dispose();
            Runner.Dispose();
            Journal.Dispose();
            Stopping.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(DirectoryPath, true);
        }
    }
}

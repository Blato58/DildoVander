using System.Buffers;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Customers;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

internal static partial class AccountIdentityTests
{
    public static async Task RunAccountDataExportHttpAsync()
    {
        await using var fixture = await StartAccountExportFixtureAsync();
        using var anonymous = fixture.Client();
        Check((await anonymous.GetAsync("/app/v1/customer/data/exports/00000000-0000-0000-0000-000000000001")).StatusCode == HttpStatusCode.Unauthorized,
            "account export endpoint requires a real authenticated account");

        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Export owner");
        var ownerSession = await CsrfAsync(owner);
        var ownerId = ownerSession.GetProperty("account").GetProperty("id").GetString()!;
        await SeedCustomerSessionAsync(fixture.App.Configuration["Backend:DatabasePath"]!, ownerId);

        var createPath = "/app/v1/customer/data/exports";
        var validBody = new { schemaVersion = 1 };
        var validKey = Guid.NewGuid().ToString("D");
        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        using (var missingCsrf = await PostExportAsync(owner, validBody, validKey))
        {
            Check(missingCsrf.StatusCode == HttpStatusCode.BadRequest, "export creation requires account CSRF");
        }
        await CsrfAsync(owner);
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://evil.invalid");
        using (var badOrigin = await PostExportAsync(owner, validBody, validKey))
        {
            Check(badOrigin.StatusCode == HttpStatusCode.Forbidden, "export creation enforces the configured HTTPS origin");
        }
        owner.DefaultRequestHeaders.Remove("Origin");
        owner.DefaultRequestHeaders.Add("Origin", "https://localhost");
        await CsrfAsync(owner);

        using (var invalidSchema = await PostExportAsync(owner, new { schemaVersion = 2 }, Guid.NewGuid().ToString("D")))
        {
            Check(invalidSchema.StatusCode == HttpStatusCode.BadRequest, "unsupported export schema is rejected");
        }
        using (var unknownProperty = await PostExportRawAsync(owner,
            "{\"schemaVersion\":1,\"extra\":true}", Guid.NewGuid().ToString("D")))
        {
            Check(unknownProperty.StatusCode == HttpStatusCode.BadRequest, "export request rejects unknown JSON members");
        }
        using (var oversized = await PostExportRawAsync(owner,
            "{\"schemaVersion\":1,\"extra\":\"" + new string('x', 1100) + "\"}", Guid.NewGuid().ToString("D")))
        {
            Check(oversized.StatusCode == HttpStatusCode.RequestEntityTooLarge, "export request body is bounded at 1024 bytes");
        }
        using (var invalidKey = await PostExportAsync(owner, validBody, "not-a-guid"))
        {
            Check(invalidKey.StatusCode == HttpStatusCode.BadRequest, "export idempotency key must be a nonempty GUID");
        }
        using (var missingKey = await PostExportAsync(owner, validBody, null))
        {
            Check(missingKey.StatusCode == HttpStatusCode.BadRequest, "export creation requires an idempotency key");
        }

        var memory = fixture.App.Services.GetRequiredService<TestCustomerExportMemory>();
        var store = fixture.App.Services.GetRequiredService<AccountDataExportStore>();
        var service = fixture.App.Services.GetRequiredService<AccountDataExportService>();
        var requestKey = Guid.NewGuid().ToString("D");
        using var createdResponse = await PostExportAsync(owner, validBody, requestKey);
        Check(createdResponse.StatusCode == HttpStatusCode.Accepted, "valid export request is accepted");
        var created = await JsonAsync(createdResponse);
        var createdId = created.GetProperty("id").GetString()!;
        Check(created.GetProperty("state").GetString() == "requested", "new export is reported as requested");
        using var replayResponse = await PostExportAsync(owner, validBody, requestKey);
        Check(replayResponse.StatusCode == HttpStatusCode.Accepted
            && (await JsonAsync(replayResponse)).GetProperty("id").GetString() == createdId,
            "same idempotency key replays the same account export job");

        await CsrfAsync(owner);
        var pause = memory.PauseNextAdmission();
        var preparingTask = service.PrepareNextAsync();
        await pause.Started.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            using var connection = new SqliteConnection("Data Source=" + store.DatabasePath);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT state FROM account_data_export_jobs WHERE id=$id";
            command.Parameters.AddWithValue("$id", createdId);
            Check(command.ExecuteScalar() as string == "preparing",
                "controlled worker durably claims preparing before memory admission");
        }
        finally { pause.Release(); await preparingTask; }
        Check(await preparingTask, "controlled worker processes the requested export");
        using (var readyResponse = await owner.GetAsync($"{createPath}/{createdId}"))
        {
            var ready = await JsonAsync(readyResponse);
            Check(ready.GetProperty("state").GetString() == "ready"
                && ready.GetProperty("downloadPath").GetString() == $"{createPath}/{createdId}/download",
                "successful preparation exposes a ready job and its download path");
        }

        using var stranger = fixture.Client();
        using var strangerKey = new VirtualPasskey();
        using var invitationResponse = await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Viewer", recoveryUserId = (string?)null });
        var invitation = await JsonAsync(invitationResponse);
        await EnrollAsync(stranger, strangerKey, invitation.GetProperty("token").GetString()!, "Export viewer");
        var strangerSession = await CsrfAsync(stranger);
        var strangerId = strangerSession.GetProperty("account").GetProperty("id").GetString()!;
        using (var wrongAccount = await stranger.GetAsync($"{createPath}/{createdId}"))
        {
            Check(wrongAccount.StatusCode == HttpStatusCode.NotFound, "another account cannot discover an export job");
        }
        using (var wrongAccountDownload = await stranger.GetAsync($"{createPath}/{createdId}/download"))
        {
            Check(wrongAccountDownload.StatusCode == HttpStatusCode.NotFound,
                "another account cannot download an export job");
        }

        using (var secondSession = fixture.Client())
        {
            await SignInAsync(secondSession, ownerKey);
            await CsrfAsync(secondSession);
            using (var changedAuthorityReplay = await PostExportAsync(secondSession, validBody, requestKey))
            {
                Check(changedAuthorityReplay.StatusCode == HttpStatusCode.Conflict,
                    "idempotency replay from a different live session conflicts");
            }
            using var wrongSession = await secondSession.GetAsync($"{createPath}/{createdId}");
            Check(wrongSession.StatusCode == HttpStatusCode.NotFound,
                "another authenticated session of the same account cannot discover an existing export");
        }

        var proofSha = ReadExportProofHash(store.DatabasePath, createdId);
        using (var download = await owner.GetAsync($"{createPath}/{createdId}/download"))
        {
            var bytes = await download.Content.ReadAsByteArrayAsync();
            Check(download.StatusCode == HttpStatusCode.OK && download.Content.Headers.ContentType?.MediaType == "application/json"
                && download.Content.Headers.ContentDisposition?.DispositionType == "attachment"
                && download.Headers.CacheControl?.NoStore == true,
                "download is a no-store JSON attachment");
            Check(proofSha == Convert.ToHexString(SHA256.HashData(bytes)),
                "download bytes match the durable SHA-256 proof");
            using var exportDocument = JsonDocument.Parse(bytes);
            Check(exportDocument.RootElement.GetProperty("schemaVersion").GetInt32() == 1
                && exportDocument.RootElement.GetProperty("account").GetProperty("id").GetString() == ownerId
                && exportDocument.RootElement.GetProperty("history").GetProperty("sessions").GetArrayLength() == 1,
                "download contains the explicit account and owned-session projection");
            var json = Encoding.UTF8.GetString(bytes);
            Check(json.Contains("full_stories_photos_and_media", StringComparison.Ordinal)
                && !json.Contains("expectedGuestId", StringComparison.Ordinal)
                && !json.Contains("authorityRevision", StringComparison.Ordinal),
                "download lists excluded private content and omits guest and authority internals");
        }

        var beforeDelete = fixture.App.Services.GetRequiredService<OperationJournal>()
            .CustomerSessions(ownerId).Select(session => session.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

        var replacementKey = Guid.NewGuid().ToString("D");
        using var replacementResponse = await PostExportAsync(owner, validBody, replacementKey);
        var replacement = await JsonAsync(replacementResponse);
        var replacementId = replacement.GetProperty("id").GetString()!;
        Check(replacementId != createdId, "a fresh request explicitly replaces the previous per-account job");
        using (var staleReplacement = await owner.GetAsync($"{createPath}/{createdId}"))
        {
            Check(staleReplacement.StatusCode == HttpStatusCode.NotFound,
                "replaced job id is no longer visible to its previous owner");
        }
        Check(await service.PrepareNextAsync(), "replacement job can be prepared manually");

        using (var deleteResponse = await owner.DeleteAsync($"{createPath}/{replacementId}"))
        {
            Check(deleteResponse.StatusCode == HttpStatusCode.NoContent, "owner can explicitly delete the export job");
        }
        Check(!ExportJobExists(store.DatabasePath, replacementId),
            "deleted export job metadata is no longer available");
        var afterDelete = fixture.App.Services.GetRequiredService<OperationJournal>()
            .CustomerSessions(ownerId).Select(session => session.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Check(beforeDelete.SequenceEqual(afterDelete), "deleting an export job leaves original customer-session data intact");

        var memoryMismatchKey = Guid.NewGuid().ToString("D");
        using var mismatchRequest = await PostExportAsync(owner, validBody, memoryMismatchKey);
        var mismatchId = (await JsonAsync(mismatchRequest)).GetProperty("id").GetString()!;
        Check(await service.PrepareNextAsync(), "memory mismatch job is prepared against the current revision");
        var previousRevision = memory.Revision;
        memory.Revision = previousRevision + "-changed";
        var releasesBeforeMismatch = memory.ReleaseCount;
        using (var staleDownload = await owner.GetAsync($"{createPath}/{mismatchId}/download"))
        {
            Check(staleDownload.StatusCode == HttpStatusCode.Conflict,
                "changed guarded-memory revision invalidates a ready export before release");
        }
        using var mismatchStatusResponse = await owner.GetAsync($"{createPath}/{mismatchId}");
        var mismatchStatus = await JsonAsync(mismatchStatusResponse);
        Check(memory.ReleaseCount == releasesBeforeMismatch
            && mismatchStatus.GetProperty("state").GetString() == "failed"
            && mismatchStatus.GetProperty("reason").GetString() == "source_changed",
            "memory mismatch releases no bytes and clears the durable proof");

        var uncertainKey = Guid.NewGuid().ToString("D");
        using var uncertainRequest = await PostExportAsync(owner, validBody, uncertainKey);
        var uncertainId = (await JsonAsync(uncertainRequest)).GetProperty("id").GetString()!;
        Check(await service.PrepareNextAsync(), "uncertain-release job is prepared");
        memory.FailNextReleaseAfterPartialWrite();
        var uncertainSawAbort = false;
        try
        {
            using var partial = await owner.GetAsync($"{createPath}/{uncertainId}/download",
                HttpCompletionOption.ResponseHeadersRead);
            var partialBytes = await partial.Content.ReadAsByteArrayAsync();
            Check(partial.StatusCode == HttpStatusCode.OK && partialBytes.Length < memory.LastPayloadLength
                && !Encoding.UTF8.GetString(partialBytes).Contains("account_export_unavailable", StringComparison.Ordinal),
                "uncertain release returns only a truncated stream and never appends a problem JSON body");
            uncertainSawAbort = true;
        }
        catch (HttpRequestException)
        {
            uncertainSawAbort = true;
        }
        catch (IOException)
        {
            uncertainSawAbort = true;
        }
        Check(uncertainSawAbort, "uncertain release aborts the HTTPS response");

        var expiringKey = Guid.NewGuid().ToString("D");
        using var expiringRequest = await PostExportAsync(owner, validBody, expiringKey);
        var expiringId = (await JsonAsync(expiringRequest)).GetProperty("id").GetString()!;
        Check(await service.PrepareNextAsync(), "expiring download job is prepared");
        fixture.App.Services.GetRequiredService<TestExportClock>().Advance(TimeSpan.FromHours(2));
        using (var expired = await owner.GetAsync($"{createPath}/{expiringId}/download"))
        {
            Check(expired.StatusCode == HttpStatusCode.Gone, "expired export download returns 410");
        }
        using var expiredStatusResponse = await owner.GetAsync($"{createPath}/{expiringId}");
        var expiredStatus = await JsonAsync(expiredStatusResponse);
        Check(expiredStatus.GetProperty("state").GetString() == "failed"
            && expiredStatus.GetProperty("reason").GetString() == "download_expired",
            "expired download clears its ready proof while retaining the job");

        var ownerAccount = await JsonAsync(await owner.GetAsync("/app/v1/account"));
        var ownerSessionId = ownerAccount.GetProperty("sessions").EnumerateArray()
            .Single(session => session.GetProperty("current").GetBoolean()).GetProperty("id").GetString()!;
        await CsrfAsync(owner);
        using (var revokeSession = await owner.DeleteAsync($"/app/v1/sessions/{ownerSessionId}"))
        {
            Check(revokeSession.StatusCode == HttpStatusCode.NoContent, "account owner can revoke the active session");
        }
        using (var revokedSession = await owner.GetAsync($"{createPath}/{expiringId}"))
        {
            Check(revokedSession.StatusCode == HttpStatusCode.Unauthorized,
                "revoked account session cannot read an export job");
        }

        var viewerJobKey = Guid.NewGuid().ToString("D");
        using var viewerJobResponse = await PostExportAsync(stranger, validBody, viewerJobKey);
        var viewerJobId = (await JsonAsync(viewerJobResponse)).GetProperty("id").GetString()!;
        await SeedAsync(fixture.App, db => db.Users.Where(user => user.Id == strangerId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.SecurityStamp, "revoked-" + Guid.NewGuid().ToString("N"))));
        using (var revokedStamp = await stranger.GetAsync($"{createPath}/{viewerJobId}"))
        {
            Check(revokedStamp.StatusCode == HttpStatusCode.Unauthorized,
                "security-stamp revocation invalidates account export access");
        }

        await VerifyDownloadSlotLimitAsync();
        Console.WriteLine("PASS account export HTTPS auth, CSRF/origin, idempotency, worker states, guarded download, expiry and revocation");
    }

    private static async Task VerifyDownloadSlotLimitAsync()
    {
        await using var fixture = await StartAccountExportFixtureAsync();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Export slot owner");
        var session = await CsrfAsync(owner);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;
        await SeedCustomerSessionAsync(fixture.App.Configuration["Backend:DatabasePath"]!, ownerId);

        var memory = fixture.App.Services.GetRequiredService<TestCustomerExportMemory>();
        memory.EnableLargePayload();
        var service = fixture.App.Services.GetRequiredService<AccountDataExportService>();
        await CsrfAsync(owner);
        using var create = await PostExportAsync(owner, new { schemaVersion = 1 }, Guid.NewGuid().ToString("D"));
        Check(create.StatusCode == HttpStatusCode.Accepted, "large bounded export request is accepted");
        var id = (await JsonAsync(create)).GetProperty("id").GetString()!;
        Check(await service.PrepareNextAsync(), "large bounded export is prepared without provider calls");

        var path = $"/app/v1/customer/data/exports/{id}/download";
        using var firstClient = fixture.Client();
        using var secondClient = fixture.Client();
        using var thirdClient = fixture.Client();
        CopyCookies(fixture, owner, firstClient);
        CopyCookies(fixture, owner, secondClient);
        CopyCookies(fixture, owner, thirdClient);
        using var first = await firstClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(10));
        using var second = await secondClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Check(first.StatusCode == HttpStatusCode.OK && second.StatusCode == HttpStatusCode.OK,
            "two large HTTPS downloads acquire the two bounded response slots");

        using (var third = await thirdClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)
            .WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Check(third.StatusCode == HttpStatusCode.TooManyRequests,
                "a third concurrent download is rejected while both response flushes are held by unread clients");
        }

        first.Dispose();
        second.Dispose();
        var subsequentCompleted = false;
        for (var attempt = 0; attempt < 20 && !subsequentCompleted; attempt++)
        {
            using var subsequent = await thirdClient.GetAsync(path, HttpCompletionOption.ResponseHeadersRead)
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (subsequent.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100));
                continue;
            }
            Check(subsequent.StatusCode == HttpStatusCode.OK,
                "a download after closing the slow clients can acquire a released slot");
            var bytes = await subsequent.Content.ReadAsByteArrayAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Check(bytes.Length > 8 * 1024 * 1024,
                "the concurrency exercise transfers a genuinely large bounded response");
            subsequentCompleted = true;
        }
        Check(subsequentCompleted, "download slots are released after slow response clients disconnect");
    }

    private static void CopyCookies(Fixture fixture, HttpClient source, HttpClient destination)
    {
        foreach (Cookie cookie in fixture.Cookies(source).GetCookies(source.BaseAddress!))
        {
            var copy = new Cookie(cookie.Name, cookie.Value, cookie.Path)
            {
                Secure = cookie.Secure,
                HttpOnly = cookie.HttpOnly,
                Expires = cookie.Expires
            };
            fixture.Cookies(destination).Add(destination.BaseAddress!, copy);
        }
    }

    private static async Task<Fixture> StartAccountExportFixtureAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "dv-account-export-http-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var notLaunched = Path.Combine(root, "authorization-only-native.stub");
        await File.WriteAllTextAsync(notLaunched, "Never launched: account export tests make no provider calls.");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        request.CertificateExtensions.Add(san.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        var config = new Dictionary<string, string?>
        {
            ["Backend:DatabasePath"] = Path.Combine(root, "journal.sqlite"),
            ["ApplicationIdentity:Enabled"] = "true",
            ["ApplicationIdentity:DataRoot"] = Path.Combine(root, "accounts"),
            ["ApplicationIdentity:DataProtectionRoot"] = Path.Combine(root, "keys"),
            ["ApplicationIdentity:RelyingPartyId"] = "localhost",
            ["ApplicationIdentity:AllowedOrigins:0"] = "https://localhost",
            ["ApplicationIdentity:Bootstrap:TokenSha256"] = AccountEndpoints.Hash(Fixture.Bootstrap),
            ["ApplicationIdentity:Bootstrap:ExpiresAt"] = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            ["ApplicationIdentity:RequestsPerMinute"] = "120",
            ["Keepsake:Enabled"] = "true",
            ["Keepsake:GenerationEnabled"] = "true",
            ["Keepsake:Origin"] = "https://localhost",
            ["Keepsake:DataRoot"] = Path.Combine(root, "guest-content"),
            ["Keepsake:DataProtectionRoot"] = Path.Combine(root, "keys")
        };
        builder.Configuration.AddInMemoryCollection(config);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(certificate)));

        var clock = new TestExportClock(DateTimeOffset.UtcNow);
        var memoryPath = Path.Combine(root, "memory.sqlite");
        using (File.Create(memoryPath)) { }
        var memory = new TestCustomerExportMemory(memoryPath);
        builder.Services.AddSingleton<TimeProvider>(clock);
        builder.Services.AddSingleton(clock);
        builder.Services.AddAccountIdentity(builder.Configuration);
        builder.Services.AddCustomerCharacters(builder.Configuration);
        builder.Services.AddSingleton<OperationJournal>();
        builder.Services.AddSingleton<KeepsakeStore>();
        builder.Services.AddSingleton(memory);
        builder.Services.AddSingleton<ICustomerDataMemoryAdmission>(services => services.GetRequiredService<TestCustomerExportMemory>());
        builder.Services.AddSingleton<ICustomerMemoryGuardedDataSource>(services => services.GetRequiredService<TestCustomerExportMemory>());
        builder.Services.AddAccountDataExports(builder.Configuration);
        foreach (var descriptor in builder.Services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                     && descriptor.ImplementationType is { } type
                     && (type == typeof(AccountDataExportWorker) || type == typeof(CharacterEnrichmentWorker))).ToArray())
        {
            builder.Services.Remove(descriptor);
        }

        var app = builder.Build();
        _ = app.Services.GetRequiredService<OperationJournal>();
        await AccountProvisioning.MigrateAsync(app.Services);
        await app.InitializeAccountsAsync();
        app.UseRateLimiter();
        app.UseAccountIdentity();
        app.MapAccountEndpoints();
        app.MapAccountDataExports();
        await app.StartAsync();
        var origin = app.Urls.Single().Replace("127.0.0.1", "localhost", StringComparison.Ordinal);
        app.Configuration["Keepsake:Origin"] = origin;
        return new Fixture(app, root, certificate);
    }

    private static async Task SeedCustomerSessionAsync(string databasePath, string ownerId)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO customer_sessions(id,owner_id,operation_id,device_id,boot_id,profile_id,
                profile_revision,snapshot,state,conversation_id,end_requested,created_at,updated_at)
            VALUES($id,$owner,$operation,$device,'boot',$profile,3,$snapshot,'completed',NULL,0,$created,$created)
            """;
        command.Parameters.AddWithValue("$id", "fixture-session");
        command.Parameters.AddWithValue("$owner", ownerId);
        command.Parameters.AddWithValue("$operation", "fixture-operation");
        command.Parameters.AddWithValue("$device", "fixture-device");
        command.Parameters.AddWithValue("$profile", "fixture-profile");
        command.Parameters.AddWithValue("$snapshot", "{\"safe\":true}");
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync();
    }

    private static string? ReadExportProofHash(string databasePath, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_sha256 FROM account_data_export_jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    private static bool ExportJobExists(string databasePath, string id)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM account_data_export_jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private static Task<HttpResponseMessage> PostExportAsync(HttpClient client, object body, string? idempotencyKey) =>
        PostExportRawAsync(client, JsonSerializer.Serialize(body), idempotencyKey);

    private static async Task<HttpResponseMessage> PostExportRawAsync(HttpClient client, string body, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/app/v1/customer/data/exports")
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (idempotencyKey is not null) { request.Headers.Add("Idempotency-Key", idempotencyKey); }
        return await client.SendAsync(request);
    }

    private sealed class TestExportClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan by) => current = current.Add(by);
    }

    private sealed class TestCustomerExportMemory(string databasePath) : ICustomerMemoryGuardedDataSource
    {
        private AdmissionPause? nextPause;
        private int uncertainNextRelease;
        private int releaseCount;
        private int lastPayloadLength;
        private CustomerMemoryItem[]? largeItems;

        public string DatabasePath { get; } = databasePath;
        internal string Revision { get; set; } = "memory-revision-v1";
        internal int ReleaseCount => Volatile.Read(ref releaseCount);
        internal int LastPayloadLength => Volatile.Read(ref lastPayloadLength);

        internal void EnableLargePayload()
        {
            largeItems ??= Enumerable.Range(0, CustomerDataLimits.MaxMemoryItems)
                .Select(index => new CustomerMemoryItem($"fixture-memory-{index:D4}", 1,
                    new string('x', 4_096), "fixture", "fixture-session", DateTimeOffset.UnixEpoch,
                    "fixture-profile", "fixture-session"))
                .ToArray();
        }

        internal AdmissionPause PauseNextAdmission()
        {
            var pause = new AdmissionPause();
            if (Interlocked.CompareExchange(ref nextPause, pause, null) is not null)
            {
                throw new InvalidOperationException("Only one admission pause may be active.");
            }
            return pause;
        }

        internal void FailNextReleaseAfterPartialWrite() => Interlocked.Exchange(ref uncertainNextRelease, 1);

        public async Task<ICustomerDataMemoryLease> AcquireAsync(CustomerDataGuardDeadline deadline,
            CancellationToken token = default)
        {
            deadline.Check();
            var pause = Interlocked.Exchange(ref nextPause, null);
            if (pause is not null)
            {
                pause.SignalStarted();
                await pause.ReleaseTask.WaitAsync(token);
            }
            deadline.Check();
            return new Lease();
        }

        public CustomerMemoryDataSnapshot Capture(CustomerDataGuardScope scope)
        {
            var session = scope.Inventory.Sessions.FirstOrDefault();
            var revision = Revision;
            IReadOnlyList<CustomerMemoryItem> items = largeItems ??
            [
                new CustomerMemoryItem("fixture-memory", 1, "Synthetic export fact", "fixture",
                    session?.SessionId ?? "fixture-source", DateTimeOffset.UnixEpoch,
                    session?.ProfileId ?? "fixture-profile", session?.SessionId ?? "fixture-session")
            ];
            return new CustomerMemoryDataSnapshot(scope.Inventory.Revision, revision,
                items, true);
        }

        public bool IsCurrent(CustomerDataGuardScope scope, CustomerMemoryDataSnapshot snapshot) =>
            snapshot.Complete && snapshot.InventoryRevision == scope.Inventory.Revision
            && snapshot.Revision == Revision;

        public void Release(CustomerDataGuardScope scope, CustomerMemoryDataSnapshot snapshot,
            ReadOnlyMemory<byte> payload, string payloadSha256, IBufferWriter<byte> output)
        {
            if (Convert.ToHexString(SHA256.HashData(payload.Span)) != payloadSha256)
            {
                throw new InvalidOperationException("Guarded fake received a payload hash mismatch.");
            }
            Interlocked.Increment(ref releaseCount);
            Volatile.Write(ref lastPayloadLength, payload.Length);
            var uncertain = Interlocked.Exchange(ref uncertainNextRelease, 0) == 1;
            var count = uncertain ? Math.Min(16, payload.Length) : payload.Length;
            payload.Span[..count].CopyTo(output.GetSpan(count));
            output.Advance(count);
            if (uncertain)
            {
                throw new CustomerDataReleaseUncertainException(new IOException("synthetic uncertain release"));
            }
        }

        private sealed class Lease : ICustomerDataMemoryLease
        {
            private bool disposed;
            public void ValidateCurrent()
            {
                ObjectDisposedException.ThrowIf(disposed, this);
            }
            public void Dispose() => disposed = true;
        }

        internal sealed class AdmissionPause
        {
            private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Task Started => started.Task;
            internal Task ReleaseTask => release.Task;
            internal void SignalStarted() => started.TrySetResult();
            internal void Release() => release.TrySetResult();
        }
    }
}

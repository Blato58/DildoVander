using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;

internal static class ServerLogTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kufr-logs-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var clock = new AdjustableTimeProvider(DateTimeOffset.UtcNow);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Backend:DatabasePath"] = Path.Combine(directory, "journal.sqlite"),
                ["Backend:Logs:Capacity"] = "200",
                ["Backend:Admin:CredentialSha256"] = Hash("admin-secret"),
                ["Backend:Logs:CredentialSha256"] = Hash("reader-secret"),
                ["Devices:0:CredentialSha256"] = Hash("device-secret")
            }).Build();
            var admin = new ServerAdminAccess(configuration);
            var access = new LogReadAccess(configuration, admin);
            foreach (var (credential, allowed) in new[] { ("admin-secret", true), ("reader-secret", true), ("device-secret", false), ("access-token", false), ("", false) })
            {
                var context = new DefaultHttpContext();
                context.Request.Headers.Authorization = "Bearer " + credential;
                Require(access.Authenticate(context) == allowed, "reader/admin authorization isolation");
                if (credential == "reader-secret") Require(!admin.Authenticate(context), "reader has no admin privileges");
            }
            configuration["Backend:Logs:CredentialSha256"] = Hash("device-secret");
            try { _ = new LogReadAccess(configuration, admin); throw new Exception("Credential collision accepted"); }
            catch (InvalidOperationException) { }
            configuration["Backend:Logs:CredentialSha256"] = Hash("reader-secret");

            var logs = new ServerLogStore(configuration, clock);
            var trace = Guid.NewGuid().ToString();
            var operation = Guid.NewGuid().ToString();
            for (var index = 0; index < 205; index++)
                logs.Write(index % 2 == 0 ? "info" : "warning", "http_request", traceId: trace, operationId: operation);
            var tail = logs.Read(null, 10, "info", null, null);
            Require(tail.Entries.Count == 10 && tail.Entries[0].Id == 196 && tail.NextCursor == 205 && !tail.HasMore, "latest tail in ascending order");
            var page = logs.Read(0, 10, "warning", trace, operation);
            Require(page.Entries.Count == 10 && page.Entries.All(entry => entry.Level == "warning") && page.HasMore, "filtered pagination");
            var next = logs.Read(page.NextCursor, 10, "warning", trace, operation);
            Require(next.Entries[0].Id > page.Entries[^1].Id, "cursor never replays a matching entry");
            Require(logs.Read(1, 200, "info", null, null).Truncated, "retention gap exposed");
            Require(logs.Read(0, 200, "info", null, null).Entries.Count == 200, "capacity enforced");
            Require(logs.Read(0, 200, "error", null, null).NextCursor == 205, "empty filter advances cursor");
            Require(logs.Read(999, 10, "info", null, null).Truncated, "cursor beyond tail reset");
            var reopened = new ServerLogStore(configuration, clock);
            Require(reopened.Read(null, 10, "info", null, null).StoreId == tail.StoreId, "store identity survives restart");
            reopened.Write("error", "server_started");
            Require(reopened.Read(205, 200, "info", null, null).Entries.Single().Id == 206, "cursor survives restart");

            var logger = new ServerLogProvider(logs).CreateLogger("Kufr.Backend.Features.Conversations.ConversationStore");
            logger.LogWarning(new Exception("private-provider-secret"), "Text dialog failed for operation {OperationId}.", operation);
            logger.LogError("Unknown private event {Secret}", "private-secret");
            var safe = logs.Read(206, 200, "info", null, null);
            Require(safe.Entries.Count == 1 && safe.Entries[0].OperationId == operation && safe.Entries[0].EventCode == "dialog_failed", "safe provider projection");
            Require(!JsonSerializer.Serialize(safe).Contains("private"), "exception and unknown formatter content excluded");
            using var services = new ServiceCollection().AddSingleton(logs).BuildServiceProvider();
            var stream = new DefaultHttpContext { RequestServices = services, TraceIdentifier = trace };
            stream.Request.Path = "/v1/conversations/" + Guid.NewGuid();
            stream.Request.RouteValues["id"] = stream.Request.Path.Value!.Split('/')[3];
            ServerLogEvents.Stream(stream, new { type = "audio", delta = "private-audio" });
            ServerLogEvents.Stream(stream, new { type = "visitor", text = "private-visitor" });
            ServerLogEvents.Stream(stream, new { type = "failed", code = "cost_limit", operationId = operation,
                text = "private-text", result = new { credential = "private-secret" } });
            var streamed = logs.Read(safe.NextCursor, 200, "info", trace, operation);
            Require(streamed.Entries.Single().EventCode == "realtime_failed" && streamed.Entries[0].Code == "cost_limit",
                "failure inside HTTP 200 stream is recorded with correlation");
            Require(!JsonSerializer.Serialize(streamed).Contains("private"), "stream content excluded");
            foreach (var engine in new[] { "realtime-live", "gpt-live" })
            {
                var before = logs.Read(null, 200, "info", null, null).NextCursor;
                var upload = new DefaultHttpContext();
                upload.Request.Method = "POST";
                upload.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
                    RoutePatternFactory.Parse($"/v1/conversations/{{id}}/{engine}/{{streamId:guid}}/audio"),
                    0, EndpointMetadataCollection.Empty, "audio upload"));
                ServerLogEvents.RequestAsync(upload, context =>
                {
                    context.Response.StatusCode = 204;
                    return Task.CompletedTask;
                }, logs).GetAwaiter().GetResult();
                Require(logs.Read(before, 200, "info", null, null).Entries.Count == 0,
                    "successful constrained PCM route must not flood diagnostics");
                ServerLogEvents.RequestAsync(upload, context =>
                {
                    context.Response.StatusCode = 409;
                    return Task.CompletedTask;
                }, logs).GetAwaiter().GetResult();
                var failedUpload = logs.Read(before, 200, "info", null, null).Entries.Single();
                Require(failedUpload.Status == 409 && failedUpload.Route!.EndsWith("/{streamId}/audio"),
                    "failed PCM uploads retain a readable normalized route");
            }
            foreach (var (route, method, status, eventCode) in new[]
            {
                ("/app/v1/session", "GET", 200, "account_session_checked"),
                ("/app/v1/session", "DELETE", 204, "account_sign_out_completed"),
                ("/app/v1/passkeys/sign-in", "POST", 401, "account_sign_in_failed"),
                ("/app/v1/passkeys/sign-in", "POST", 204, "account_sign_in_completed")
            })
            {
                var before = logs.Read(null, 200, "info", null, null).NextCursor;
                var request = new DefaultHttpContext();
                request.Request.Method = method;
                request.Request.Headers["X-Request-Id"] = "private-client-request-id";
                request.Request.Headers.Authorization = "private-token";
                request.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(route),
                    0, EndpointMetadataCollection.Empty, "account action"));
                ServerLogEvents.RequestAsync(request, context =>
                {
                    context.Response.StatusCode = status;
                    if (status == 401) { context.Items["diagnosticCode"] = "passkey_not_verified"; }
                    return Task.CompletedTask;
                }, logs).GetAwaiter().GetResult();
                var entry = logs.Read(before, 200, "info", null, null).Entries.Single();
                Require(entry.EventCode == eventCode && entry.Status == status
                    && Guid.TryParse(entry.TraceId, out _) && request.Response.Headers["X-Request-Id"] == entry.TraceId,
                    "account actions retain safe outcomes and server-owned correlation");
                Require(entry.Code == (status == 401 ? "passkey_not_verified" : method == "GET" ? "anonymous" : null),
                    "session and authentication failure reasons are explicit");
                Require(!JsonSerializer.Serialize(entry).Contains("private", StringComparison.Ordinal), "request headers are excluded");
            }
            var workerLogger = new ServerLogProvider(logs).CreateLogger("Kufr.Backend.Features.TestingSessions.TestingCharacterEnrichmentWorker");
            var workerCursor = logs.Read(null, 200, "info", null, null).NextCursor;
            workerLogger.LogError("Testing character enrichment worker failed ({ErrorType}).", "private-exception-message");
            var workerEntry = logs.Read(workerCursor, 200, "info", null, null).Entries.Single();
            Require(workerEntry.EventCode == "testing_character_worker_failed" && !JsonSerializer.Serialize(workerEntry).Contains("private"),
                "worker infrastructure failures survive safe logger projection without messages");
            clock.Advance(TimeSpan.FromHours(25));
            Require(logs.Read(null, 200, "info", null, null).Entries.Count == 0, "time retention on read without new writes");
            logs.Write("info", "server_started");
            Require(logs.Read(null, 200, "info", null, null).Entries.Single().Id > 206, "pruning preserves sequence");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
        Console.WriteLine("Server log tests passed.");
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}

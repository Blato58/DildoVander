using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.DigitalTwin;

internal static class TwinEndpoints
{
    private const string Cookie = "__Host-kufr-twin";
    private sealed record Login(string Credential);
    public static IEndpointRouteBuilder MapDigitalTwin(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/healthz/twin", (TwinHost hosts) => Results.Json(
            new { status = hosts.IsReady() ? "ready" : "starting-or-failed" }, statusCode: hosts.IsReady() ? 200 : 503));
        endpoints.MapGet("/twin", (IConfiguration configuration) =>
        {
            var path = configuration["DigitalTwin:ViewerPath"];
            return path is not null && Path.IsPathFullyQualified(path) && File.Exists(path)
                ? Results.File(path, "text/html; charset=utf-8") : Results.StatusCode(503);
        });
        endpoints.MapPost("/v1/twin/session", async (HttpContext context, TwinAccess access, TwinHost hosts) =>
        {
            if (!SameOrigin(context)) { return ApiProblem.Create(context, 403, "origin_forbidden"); }
            var login = await ReadAsync<Login>(context, 2048);
            var session = login is null ? null : access.CreateSession(login.Credential, context.Request.Cookies[Cookie]);
            if (session is null) { return ApiProblem.Create(context, 401, "twin_credential_invalid"); }
            hosts.RenewOwner(session);
            context.RequestServices.GetService<TwinMediaSessions>()?.RenewOwner(session);
            context.Response.Cookies.Append(Cookie, session.Id, new CookieOptions
            {
                HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, Path = "/", Expires = session.Expires
            });
            return Results.Json(new { role = session.Principal.Role, instanceId = session.Principal.InstanceId });
        });
        endpoints.MapDelete("/v1/twin/session", (HttpContext context, TwinAccess access) =>
        {
            if (!SameOrigin(context)) { return ApiProblem.Create(context, 403, "origin_forbidden"); }
            access.Revoke(context.Request.Cookies[Cookie]);
            context.Response.Cookies.Delete(Cookie, new CookieOptions { Secure = true, Path = "/" });
            return Results.NoContent();
        });
        endpoints.MapGet("/v1/twin/instances", async (HttpContext context, TwinAccess access, TwinStore store,
            TwinHost hosts, IConfiguration configuration) =>
        {
            var session = access.Session(context.Request.Cookies[Cookie]);
            var account = context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim);
            if (session is null && !account) { return ApiProblem.Create(context, 401, "twin_session_required"); }
            var instances = new List<object>();
            foreach (var id in store.Ids())
            {
                var allowed = account ? await AccountResourceAuthorization.HasPermissionAsync(context, "workshop", id, "read") : CanObserve(session!, id);
                if (!allowed) { continue; }
                var canControl = hosts.IsSimulation(id) && (account
                    ? await AccountResourceAuthorization.HasPermissionAsync(context, "workshop", id, "operate")
                    : session!.Principal.Role == "control" && session.Principal.InstanceId == id);
                    var latest = store.Read(id);
                    var status = store.Status(latest);
                    instances.Add(new { instanceId = id, mode = configuration[$"DigitalTwin:Instances:{id}:Mode"], canControl,
                        profile = configuration[$"DigitalTwin:Instances:{id}:Profile"]
                            ?? (configuration[$"DigitalTwin:Instances:{id}:Mode"] == "live" ? "device" : "deterministic"), online = status == "online", status,
                        capabilities = latest?.Snapshot.Capabilities ?? ["awaiting-snapshot"] });
            }
            return Results.Json(new { role = account ? "account" : session!.Principal.Role, instances });
        });
        endpoints.MapGet("/v1/twin/instances/{id}", async (string id, HttpContext context, TwinAccess access, TwinStore store) =>
        {
            var session = access.Session(context.Request.Cookies[Cookie]);
            var account = context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim);
            if (session is null && !account) { return ApiProblem.Create(context, 401, "twin_session_required"); }
            if (account ? !await AccountResourceAuthorization.HasPermissionAsync(context, "workshop", id, "read") : !CanObserve(session!, id))
            { return ApiProblem.Create(context, 403, "observe_forbidden"); }
            var latest = store.Read(id);
            return latest is null ? ApiProblem.Create(context, 404, "snapshot_unavailable") : Results.Json(store.View(latest));
        });
        endpoints.MapGet("/v1/twin/instances/{id}/recording", async (string id, HttpContext context, TwinAccess access, TwinStore store) =>
        {
            var session = access.Session(context.Request.Cookies[Cookie]);
            var account = context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim);
            if (session is null && !account) { return ApiProblem.Create(context, 401, "twin_session_required"); }
            if (account ? !await AccountResourceAuthorization.HasPermissionAsync(context, "workshop", id, "read") : !CanObserve(session!, id))
            { return ApiProblem.Create(context, 403, "observe_forbidden"); }
            return Results.Json(new { schemaVersion = 1, bounded = true, snapshots = store.Recording(id)
                .Select(envelope => new { envelope.Snapshot, envelope.ReceivedAtUtc, ageMs = 0, status = "online" }) });
        });
        endpoints.MapPost("/v1/twin/instances/{id}/commands", async (string id, HttpContext context,
            TwinAccess access, TwinHost hosts) =>
        {
            if (!SameOrigin(context)) { return ApiProblem.Create(context, 403, "origin_forbidden"); }
            var session = access.Session(context.Request.Cookies[Cookie]);
            if (context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim))
            {
                session = await AccountControlSessionAsync(context, id, hosts);
                if (session is null) { return ApiProblem.Create(context, 403, "control_forbidden"); }
            }
            if (session is null) { return ApiProblem.Create(context, 401, "twin_session_required"); }
            if (session.Principal.Role != "control" || session.Principal.InstanceId != id || !hosts.IsSimulation(id))
            {
                return ApiProblem.Create(context, 403, "control_forbidden");
            }
            var command = await ReadAsync<TwinCommand>(context, 2048);
            if (command is null) { return ApiProblem.Create(context, 400, "invalid_twin_command"); }
            try
            {
                if (command.Action == "stop") { context.RequestServices.GetService<TwinMediaSessions>()?.StopMedia(id, session); }
                var result = await hosts.CommandAsync(id, session, command, context.RequestAborted);
                return Results.Json(result, statusCode: result.Accepted ? 200 : 409);
            }
            catch (TimeoutException) { return ApiProblem.Create(context, 504, "command_outcome_unknown"); }
        });
        endpoints.MapPost("/v1/twin/ingest/{id}", async (string id, HttpContext context, TwinAccess access,
            DeviceAccessStore devices, TwinStore store, TwinHost hosts, IConfiguration configuration) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var principal = header.StartsWith("Bearer ", StringComparison.Ordinal)
                ? access.VerifySource(header[7..]) : null;
            var mode = configuration[$"DigitalTwin:Instances:{id}:Mode"];
            var expectedDevice = configuration[$"DigitalTwin:Instances:{id}:DeviceId"];
            // The native process publishes directly. No HTTP principal can spoof it.
            if (hosts.IsSimulation(id) || mode is not ("live" or "simulation")
                || !(principal is { Role: "ingest" } && principal.InstanceId == id && principal.Mode == mode))
            {
                return ApiProblem.Create(context, 403, "ingest_forbidden");
            }
            var snapshot = await ReadAsync<TwinSnapshot>(context, 32768);
            if (snapshot is null || snapshot.Source is null || snapshot.Source.InstanceId != id
                || snapshot.Source.Mode != mode || snapshot.Source.DeviceId != expectedDevice)
            {
                return ApiProblem.Create(context, 400, "invalid_twin_snapshot");
            }
            var failure = store.Accept(snapshot);
            return failure is null ? Results.NoContent() : ApiProblem.Create(context, 409, failure);
        });
        endpoints.MapPost("/v1/twin/device-token", (HttpContext context, TwinAccess access,
            DeviceAccessStore devices, IConfiguration configuration) =>
        {
            var header = context.Request.Headers.Authorization.ToString();
            var deviceId = header.StartsWith("Bearer ", StringComparison.Ordinal) ? devices.VerifyCredential(header[7..]) : null;
            if (deviceId is null || deviceId == ServerAdminAccess.DebugDeviceId) { return ApiProblem.Create(context, 401, "device_credential_required"); }
            var instance = configuration.GetSection("DigitalTwin:Instances").GetChildren().SingleOrDefault(entry =>
                entry["Mode"] == "live" && entry["DeviceId"] == deviceId && entry.Key == deviceId);
            if (instance is null) { return ApiProblem.Create(context, 403, "live_instance_not_provisioned"); }
            var grant = access.IssueSourceToken(deviceId, instance.Key);
            return grant is null ? ApiProblem.Create(context, 429, "source_token_limit")
                : Results.Json(new { accessToken = grant.Id, tokenType = "Bearer", expiresInSeconds = 900, deviceId });
        });
        endpoints.MapGet("/v1/twin/health", (HttpContext context, TwinAccess access, TwinHost hosts) =>
            access.Session(context.Request.Cookies[Cookie]) is null
                ? ApiProblem.Create(context, 401, "twin_session_required") : Results.Json(hosts.Health()));
        return endpoints;
    }
    private static bool CanObserve(TwinSession session, string id) => session.Principal.Role == "observe"
        || session.Principal.Role == "control" && session.Principal.InstanceId == id;
    internal static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim))
        {
            return context.Request.IsHttps && context.RequestServices.GetRequiredService<AccountConfiguration>()
                .AllowedOrigins.Contains(origin, StringComparer.Ordinal);
        }
        // nginx terminates TLS; expected origin is explicitly configured, never
        // derived from an untrusted forwarded header.
        var configured = context.RequestServices.GetRequiredService<IConfiguration>()["DigitalTwin:Origin"];
        return configured is not null && origin == configured;
    }

    internal static async Task<TwinSession?> AccountControlSessionAsync(HttpContext context, string id, TwinHost hosts)
    {
        if (!hosts.IsSimulation(id) || !await AccountResourceAuthorization.HasPermissionAsync(context, "workshop", id, "operate")) { return null; }
        var sid = context.User.FindFirstValue(AccountIdentity.SessionClaim)!;
        var session = await context.RequestServices.GetRequiredService<AccountDatabase>().Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid);
        if (session is null || session.ExpiresAt <= AccountEndpoints.Now()) { return null; }
        // Account revocation cannot leave an unattended simulation lease for an
        // entire login lifetime. Commands renew a short existing host lease.
        var expiry = DateTimeOffset.UtcNow.AddSeconds(30);
        var accountExpiry = DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt);
        var result = new TwinSession("account:" + sid, new TwinPrincipal("control", id, "simulation"), expiry < accountExpiry ? expiry : accountExpiry);
        hosts.RenewOwner(result);
        context.RequestServices.GetService<TwinMediaSessions>()?.RenewOwner(result);
        return result;
    }
    private static async Task<T?> ReadAsync<T>(HttpContext context, int maximum)
    {
        if (context.Request.ContentLength > maximum) { return default; }
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) { feature.MaxRequestBodySize = maximum; }
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, TwinSnapshot.JsonOptions, context.RequestAborted);
        }
        catch (JsonException) { return default; }
        catch (BadHttpRequestException) { return default; }
    }
}

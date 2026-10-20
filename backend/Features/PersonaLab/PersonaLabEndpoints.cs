using System.Security.Claims;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.GptLive;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Realtime;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.PersonaLab;

internal static class PersonaLabEndpoints
{
    internal static IServiceCollection AddPersonaLab(this IServiceCollection services)
    {
        services.AddSingleton(sp => PersonaLabOptions.Read(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var journal = sp.GetRequiredService<OperationJournal>();
            var baseline = LivePromptSnapshot.Create(journal.ReadPersonality(), sp.GetRequiredService<GptLiveOptions>());
            var path = configuration["PersonaLab:DatabasePath"]
                ?? (configuration["Backend:DatabasePath"] ?? throw new InvalidOperationException("Backend:DatabasePath missing.")) + ".persona-lab.sqlite";
            return new PersonaLabStore(path, baseline);
        });
        services.AddSingleton<LabTicketRegistry>();
        services.AddSingleton<PersonaLabRuntime>();
        services.AddSingleton<PersonaLabHardware>();
        services.AddHostedService(sp => sp.GetRequiredService<PersonaLabHardware>());
        services.AddSingleton<PersonaLabSuites>();
        services.AddHostedService(sp => sp.GetRequiredService<PersonaLabSuites>());
        services.AddSingleton<PersonaLabImages>();
        return services;
    }
    internal static void MapPersonaLab(this WebApplication app)
    {
        // Resolve registered configuration during composition so an invalid
        // production technical limit fails before serving lab requests.
        _ = app.Services.GetService<PersonaLabOptions>();
        var group = app.MapGroup("/app/v1/persona-lab").RequireAuthorization().RequireRateLimiting("account");
        group.MapPersonaLabImages();
        group.MapPersonaLabHardware();
        group.AddEndpointFilter(async (invocation, next) =>
        {
            try
            {
                var http = invocation.HttpContext;
                // Authenticated account principals, including Viewer, may test without a device grant.
                if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 })
                { return ApiProblem.Create(http, 401, "account_required"); }
                return await next(invocation);
            }
            catch (LabException error) { return ApiProblem.Create(invocation.HttpContext, error.Status, error.Code); }
            catch (JsonException) { return ApiProblem.Create(invocation.HttpContext, 400, "invalid_lab_request"); }
        });
        group.MapGet("/catalog", (PersonaLabStore store, PersonaLabRuntime runtime) => Results.Json(new
        {
            capabilities = new { live = runtime.LiveAvailable, realtime = runtime.RealtimeAvailable, voice = true,
                text = true, simulationOnly = false, defaultMode = "simulation", hardwareContext = true, imageRealtime = true },
            activeRevisionId = store.ActiveRevisionId, versions = store.Revisions(),
            scenarios = LabContract.Scenarios, memoryFixtures = LabContract.Fixtures
        }));
        group.MapPost("/revisions", async (HttpContext http, LabRevisionRequest request, PersonaLabStore store) =>
        {
            var denied = await AdminAsync(http);
            return denied ?? Results.Json(store.CreateRevision(request));
        });
        group.MapPost("/revisions/{id}/publish", async (HttpContext http, string id, LabPublishRequest request, PersonaLabStore store) =>
        {
            var denied = await AdminAsync(http);
            return denied ?? Results.Json(store.Publish(id, request.ExpectedActiveRevisionId));
        });
        group.MapPost("/rollback", async (HttpContext http, LabRollbackRequest request, PersonaLabStore store) =>
        {
            var denied = await AdminAsync(http);
            return denied ?? Results.Json(store.Publish(request.RevisionId, request.ExpectedActiveRevisionId, rollback: true));
        });
        group.MapGet("/sessions", (HttpContext http, PersonaLabStore store) => Results.Json(store.Sessions(Owner(http))));
        group.MapGet("/suites", (HttpContext http, PersonaLabStore store) => Results.Json(store.Suites(Owner(http))));
        group.MapGet("/suites/{id}", (HttpContext http, string id, PersonaLabStore store) => Results.Json(store.Suite(Owner(http), id)));
        group.MapPost("/suites", async (HttpContext http, LabSuiteRequest request, PersonaLabSuites suites) =>
        {
            LabContract.ExecutionPlatform(request.ExecutionPlatform);
            var admission = await AdmissionAsync(http, "suite", http.Request.Headers.Origin.ToString());
            return Results.Json(suites.Start(admission.Owner, request,
                token => AuthorizedAsync(app.Services, admission, token)));
        });
        group.MapPost("/sessions", (HttpContext http, LabSessionRequest request, PersonaLabStore store) =>
            Results.Json(store.CreateSession(Owner(http), request)));
        group.MapGet("/sessions/{id}", (HttpContext http, string id, PersonaLabRuntime runtime) => Results.Json(runtime.Detail(Owner(http), id)));
        group.MapGet("/sessions/{id}/context", (HttpContext http, string id, PersonaLabStore store) =>
        {
            var session = store.Session(Owner(http), id);
            return Results.Json(new { synthetic = true, fixture = session.MemorySnapshot ?? LabContract.Fixtures.Single(x => x.Id == session.FixtureId) });
        });
        group.MapDelete("/sessions/{id}", (HttpContext http, string id, PersonaLabRuntime runtime) =>
        {
            runtime.Delete(Owner(http), id);
            return Results.NoContent();
        });
        group.MapPost("/sessions/{id}/turns", async (HttpContext http, string id, LabTurnRequest request, PersonaLabRuntime runtime) =>
            Results.Json(await runtime.TurnAsync(Owner(http), id, request.Text, http.RequestAborted, platform: request.Platform)));
        group.MapPost("/sessions/{id}/end", (HttpContext http, string id, PersonaLabRuntime runtime) => Results.Json(runtime.End(Owner(http), id)));
        group.MapPut("/sessions/{id}/evaluation", (HttpContext http, string id, LabEvaluation request, PersonaLabStore store) =>
            Results.Json(store.Evaluate(Owner(http), id, request)));
        group.MapGet("/analytics", (HttpContext http, PersonaLabStore store) => Results.Json(LabAnalytics.Summarize(store.Sessions(Owner(http)))));
        group.MapGet("/diagnostics", (HttpContext http, PersonaLabStore store, PersonaLabRuntime runtime) =>
            AccountRoles.IsAdmin(http.User)
                ? Results.Json(new { storage = store.Diagnostics(), activeConnections = runtime.ActiveCount,
                    liveAvailable = runtime.LiveAvailable, realtimeAvailable = runtime.RealtimeAvailable, simulationOnly = false, hardwareContext = true })
                : ApiProblem.Create(http, 403, "administrator_required"));
        group.MapPost("/sessions/{id}/socket-ticket", async (HttpContext http, string id, LabTicketRequest request, PersonaLabStore store, LabTicketRegistry registry) =>
        {
            LabContract.ExecutionPlatform(request.Platform);
            var owner = Owner(http);
            var session = store.Session(owner, id);
            if (session.Status != "open") { throw new LabException(409, "lab_session_closed"); }
            var origin = http.Request.Headers.Origin.ToString();
            var configuration = http.RequestServices.GetRequiredService<AccountConfiguration>();
            if (!http.Request.IsHttps || !configuration.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            { throw new LabException(403, "account_origin_required"); }
            var socketOrigin = request.SocketOrigin ?? origin;
            if (socketOrigin != "capacitor://localhost" && !configuration.AllowedOrigins.Contains(socketOrigin, StringComparer.Ordinal))
            { throw new LabException(403, "lab_socket_origin_required"); }
            var admission = await AdmissionAsync(http, id, socketOrigin, request.Recovery, request.Platform);
            var ticket = registry.Mint(admission);
            var path = "/app/v1/persona-lab/sessions/" + id + "/live?ticket=" + ticket.Token;
            // A configured allowed origin, rather than Host/forwarded Host, owns the credential destination.
            var url = new UriBuilder(origin) { Scheme = "wss", Path = path.Split('?')[0], Query = "ticket=" + ticket.Token }.Uri.AbsoluteUri;
            return Results.Json(new LabTicket(url, path, ticket.ExpiresAt));
        });
        // Cookies are unavailable to the native JS WebSocket. Only a short-lived, one-use
        // account-session ticket authorizes this otherwise anonymous upgrade.
        app.MapGet("/app/v1/persona-lab/sessions/{id}/live", VoiceAsync).AllowAnonymous().RequireRateLimiting("account");
    }
    private static string Owner(HttpContext context) => context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new LabException(401, "account_required");
    private static async Task<IResult?> AdminAsync(HttpContext context)
    {
        if (!AccountRoles.IsAdmin(context.User)) { return ApiProblem.Create(context, 403, "administrator_required"); }
        var recent = await AccountAuthorization.RequireRecentAsync(context);
        if (recent is not null) { return recent; }
        return context.Request.Headers["X-DV-Confirm"] == "yes" ? null : ApiProblem.Create(context, 409, "confirmation_required");
    }
    private static async Task<bool> AuthorizedAsync(IServiceProvider services, LabAdmission admission, CancellationToken token)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == admission.AccountSessionId, token);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == admission.Owner, token);
        return session is not null && session.UserId == admission.Owner && session.ExpiresAt > DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            && user is not null && !user.Disabled && user.SecurityStamp == admission.SecurityStamp;
    }
    private static async Task<LabAdmission> AdmissionAsync(HttpContext http, string sessionId, string origin,
        bool recovery = false, string? platform = null)
    {
        var owner = Owner(http);
        var sid = http.User.FindFirstValue(AccountIdentity.SessionClaim) ?? "";
        var users = http.RequestServices.GetRequiredService<UserManager<AccountUser>>();
        var user = await users.FindByIdAsync(owner);
        if (user is null || user.Disabled || sid.Length == 0) { throw new LabException(401, "account_required"); }
        var admission = new LabAdmission(owner, sessionId, sid, user.SecurityStamp ?? "", origin, DateTimeOffset.UtcNow, recovery, platform);
        if (!await AuthorizedAsync(http.RequestServices, admission, http.RequestAborted)) { throw new LabException(401, "account_required"); }
        return admission;
    }
    private static async Task VoiceAsync(HttpContext http, string id, PersonaLabStore store, PersonaLabRuntime runtime, LabTicketRegistry tickets)
    {
        try
        {
            var origin = http.Request.Headers.Origin.ToString();
            var config = http.RequestServices.GetRequiredService<AccountConfiguration>();
            if (!http.Request.IsHttps || !config.Enabled
                || origin != "capacitor://localhost" && !config.AllowedOrigins.Contains(origin, StringComparer.Ordinal))
            { throw new LabException(403, "account_origin_required"); }
            var admission = tickets.Take(http.Request.Query["ticket"].ToString(), id, origin);
            store.Session(admission.Owner, id);
            if (!http.WebSockets.IsWebSocketRequest) { throw new LabException(400, "websocket_required"); }
            if (!await AuthorizedAsync(http.RequestServices, admission, http.RequestAborted)) { throw new LabException(401, "account_session_revoked"); }
            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            var sender = new LabSocketSender(socket, token => AuthorizedAsync(http.RequestServices, admission, token));
            try
            {
                await runtime.VoiceAsync(admission.Owner, id, socket,
                    token => AuthorizedAsync(http.RequestServices, admission, token), http.RequestAborted, admission.Recovery,
                    acknowledgedPlayback: http.Request.Query["playback"] == "1", platform: admission.Platform);
                await sender.JsonAsync(new { type = "session", session = runtime.Detail(admission.Owner, id) }, http.RequestAborted);
                await sender.JsonAsync(new { type = "ended" }, http.RequestAborted);
            }
            catch (LabException error)
            {
                if (socket.State == System.Net.WebSockets.WebSocketState.Open)
                { await sender.JsonAsync(new { type = "error", code = error.Code }, http.RequestAborted); }
            }
            finally
            {
                if (socket.State == System.Net.WebSockets.WebSocketState.Open)
                {
                    using var close = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "ended", close.Token);
                }
            }
        }
        catch (LabException error) when (!http.Response.HasStarted)
        { await ApiProblem.Create(http, error.Status, error.Code).ExecuteAsync(http); }
    }
}

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Customers;

internal static class CustomerSessionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static void MapCustomerSessions(this WebApplication app)
    {
        var devices = app.MapGroup("/v1/customer-sessions");
        AddErrors(devices);
        devices.MapGet("/device", DeviceStatusAsync);
        devices.MapPost("/device-challenge", async (HttpContext http, DeviceAccessStore access, OperationJournal journal) =>
        {
            var device = Device(http, access);
            var request = await ReadAsync<CustomerDeviceChallenge>(http);
            if (request.ProtocolVersion != 1 || !Uuid(request.BootId) || !Opaque(request.Challenge, 16, 128)
                || request.Generation < 0 || !Opaque(request.CatalogVersion, 1, 128) || !OperationEpoch.IsValid(request.OperationEpoch))
            { throw new CustomerSessionException(400, "invalid_device_challenge"); }
            journal.RegisterCustomerChallenge(device, request);
            return Results.Ok(new { protocolVersion = 1, enabled = journal.CustomerMode(device) });
        });
        devices.MapPost("/device-idle", async (HttpContext http, DeviceAccessStore access, OperationJournal journal, ConversationStore conversations) =>
        {
            var device = Device(http, access);
            var request = await ReadAsync<CustomerIdle>(http);
            if (!Uuid(request.BootId)) { throw new CustomerSessionException(400, "invalid_device_epoch"); }
            var previous = journal.OldBootConversation(device, request.BootId);
            if (previous is not null)
            {
                await conversations.EndConversationAfterImageAsync(device, previous, Guid.NewGuid(), http.RequestAborted);
            }
            // The transaction rechecks the registered boot after any async
            // provider/image cleanup, and never releases a current-boot lease.
            journal.ReportCustomerIdle(device, request.BootId);
            return Results.NoContent();
        });
        devices.MapPost("/redeem", RedeemAsync);
        devices.MapPost("/{id:guid}/device-state", async (string id, HttpContext http, DeviceAccessStore access,
            OperationJournal journal, ConversationStore conversations) =>
        {
            var device = Device(http, access);
            var report = await ReadAsync<CustomerDeviceState>(http);
            if (!Uuid(report.BootId) || report.State is not ("context_ready" or "running" or "completed" or "failed" or "idle")
                || report.ConversationId is not null && !Uuid(report.ConversationId))
            { throw new CustomerSessionException(400, "invalid_customer_state"); }
            var previous = journal.CustomerSession(id);
            if (previous is null || previous.DeviceId != device || journal.CustomerChallenge(device)?.BootId != report.BootId)
            { throw new CustomerSessionException(409, "device_epoch_mismatch"); }
            if (report.State is "completed" or "failed" or "idle" && previous.ConversationId is { } conversation)
            {
                await conversations.EndConversationAfterImageAsync(device, conversation, Guid.NewGuid(), http.RequestAborted);
            }
            journal.ReportCustomerState(device, id, report);
            return Results.Ok(new { state = journal.CustomerSession(id)!.State });
        });

        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled) { return; }
        var accounts = app.MapGroup("/app/v1/customer").RequireAuthorization().RequireRateLimiting("account");
        AddErrors(accounts);
        accounts.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            var owner = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (owner is null || !http.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
            { return ApiProblem.Create(http, 401, "account_required"); }
            var db = http.RequestServices.GetRequiredService<AccountDatabase>();
            if (!await db.Users.AsNoTracking().AnyAsync(user => user.Id == owner && !user.Disabled, http.RequestAborted))
            { return ApiProblem.Create(http, 401, "account_required"); }
            return await next(invocation);
        });
        accounts.MapCustomerSessionResult();
        accounts.MapPost("/start-tickets", async (HttpContext http, CharacterProfileStore profiles,
            OperationJournal journal, IDataProtectionProvider protection) =>
        {
            var request = await ReadAsync<CustomerTicketRequest>(http);
            if (request.ProtocolVersion != 1 || !Uuid(request.OperationId) || !Uuid(request.BootId)
                || !(Uuid(request.ProfileId) || Guid.TryParseExact(request.ProfileId, "N", out _))
                || request.ProfileRevision < 1 || !Opaque(request.DeviceId, 1, 128)
                || !Opaque(request.Challenge, 16, 128))
            { throw new CustomerSessionException(400, "invalid_start_request"); }
            if (!request.OpenAiConsent) { throw new CustomerSessionException(409, "openai_consent_required"); }
            var owner = Owner(http);
            var snapshot = await profiles.CaptureAsync(owner, request.ProfileId, request.ProfileRevision, http.RequestAborted);
            if (snapshot is null) { throw new CustomerSessionException(409, "character_revision_mismatch"); }
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var protector = protection.CreateProtector("DildoVander.CustomerStartTicket.v1");
            var result = journal.IssueCustomerTicket(owner, request, JsonSerializer.Serialize(new CustomerConversationSnapshot(snapshot, request.PersonalMemoryConsent, OpenAiConsent: true), Json),
                OperationJournal.CustomerHash(token), protector.Protect(token));
            if (result.ProtectedToken.Length == 0) { throw new CustomerSessionException(410, "start_ticket_revoked"); }
            return Results.Ok(new { protocolVersion = 1, ticket = protector.Unprotect(result.ProtectedToken),
                operationId = request.OperationId, expiresAt = result.ExpiresAt, sessionId = result.SessionId });
        });
        accounts.MapGet("/sessions", (HttpContext http, OperationJournal journal) =>
            Results.Ok(new { items = journal.CustomerSessions(Owner(http)).Select(Project),
                expiredOperations = journal.ExpiredCustomerOperations(Owner(http)) }));
        accounts.MapGet("/sessions/{id:guid}", (string id, HttpContext http, OperationJournal journal) =>
        {
            var session = journal.CustomerSession(id, Owner(http));
            return session is null ? ApiProblem.Create(http, 404, "customer_session_unavailable") : Results.Ok(Project(session));
        });
        accounts.MapPost("/sessions/{id:guid}/end", (string id, HttpContext http, OperationJournal journal) =>
            journal.RequestCustomerEnd(id, Owner(http)) ? Results.Accepted(value: new { state = "ending" })
                : ApiProblem.Create(http, 404, "customer_session_unavailable"));
        accounts.MapGet("/admin/devices/{id}", async (string id, HttpContext http, OperationJournal journal) =>
            await AccountResourceAuthorization.HasPermissionAsync(http, "device", id, "manage")
                ? Results.Ok(new { deviceId = id, enabled = journal.CustomerMode(id), protocolVersion = 1 })
                : ApiProblem.Create(http, 403, "device_manage_required"));
        accounts.MapPut("/admin/devices/{id}", async (string id, HttpContext http, OperationJournal journal, IConfiguration configuration) =>
        {
            if (!await AccountResourceAuthorization.HasPermissionAsync(http, "device", id, "manage"))
            { return ApiProblem.Create(http, 403, "device_manage_required"); }
            if (!configuration.GetSection("Devices").GetChildren().Any(child => child["DeviceId"] == id))
            { return ApiProblem.Create(http, 404, "device_unavailable"); }
            var request = await ReadAsync<CustomerModeChange>(http);
            journal.SetCustomerMode(id, request.Enabled);
            return Results.Ok(new { deviceId = id, enabled = request.Enabled, protocolVersion = 1 });
        });
        accounts.MapGet("/admin/sessions", async (HttpContext http, OperationJournal journal) =>
        {
            var items = new List<object>();
            foreach (var session in journal.CustomerSessions().Where(row => row.State is not ("completed" or "failed")))
            {
                if (await AccountResourceAuthorization.HasPermissionAsync(http, "device", session.DeviceId, "operate"))
                { items.Add(Project(session)); }
            }
            if (!AccountRoles.IsStaff(http.User)) { return ApiProblem.Create(http, 403, "staff_required"); }
            return Results.Ok(new { items });
        });
        accounts.MapGet("/admin/characters", async (HttpContext http, CharacterProfileStore profiles) =>
            AccountRoles.IsAdmin(http.User) ? Results.Ok(new { items = await profiles.JobSummariesAsync(http.RequestAborted) })
                : ApiProblem.Create(http, 403, "administrator_required"));
        accounts.MapPost("/admin/sessions/{id:guid}/end", async (string id, HttpContext http, OperationJournal journal) =>
        {
            var session = journal.CustomerSession(id);
            if (session is null || !await AccountResourceAuthorization.HasPermissionAsync(http, "device", session.DeviceId, "operate"))
            { return ApiProblem.Create(http, 404, "customer_session_unavailable"); }
            journal.RequestCustomerEnd(id, session.OwnerId);
            return Results.Accepted(value: new { state = "ending" });
        });
    }

    private static async Task<IResult> DeviceStatusAsync(HttpContext http, DeviceAccessStore access,
        OperationJournal journal)
    {
        var device = Device(http, access);
        var active = journal.ActiveCustomerSession(device);
        if (active is not null)
        {
            var options = http.RequestServices.GetRequiredService<AccountConfiguration>();
            var user = options.Enabled ? await http.RequestServices.GetRequiredService<AccountDatabase>().Users
                .AsNoTracking().SingleOrDefaultAsync(user => user.Id == active.OwnerId, http.RequestAborted) : null;
            if (!options.Enabled) { journal.SuspendCustomerAccount(active.OwnerId); }
            else if (user is null) { journal.RevokeCustomerProfile(active.OwnerId); }
            else if (user.Disabled) { journal.SuspendCustomerAccount(active.OwnerId); }
            active = journal.ActiveCustomerSession(device);
        }
        return Results.Ok(new { protocolVersion = 1, enabled = journal.CustomerMode(device),
            activeSessionId = active?.Id, bootId = active?.BootId, operationId = active?.OperationId,
            conversationId = active?.ConversationId, state = active?.State ?? "idle", endRequested = active?.EndRequested ?? false });
    }

    private static async Task<IResult> RedeemAsync(HttpContext http, DeviceAccessStore access, OperationJournal journal,
        ConversationStore conversations, EveningMemoryStore evenings)
    {
        var device = Device(http, access);
        if (!http.RequestServices.GetRequiredService<AccountConfiguration>().Enabled)
        { throw new CustomerSessionException(503, "accounts_not_configured"); }
        var request = await ReadAsync<CustomerRedemption>(http);
        if (request.ProtocolVersion != 1 || !Opaque(request.Ticket, 43, 43) || !Uuid(request.OperationId)
            || !Uuid(request.BootId) || !Opaque(request.Challenge, 16, 128))
        { throw new CustomerSessionException(400, "invalid_start_ticket"); }
        var owner = journal.CustomerTicketOwner(OperationJournal.CustomerHash(request.Ticket));
        if (owner is null) { throw new CustomerSessionException(404, "start_ticket_unavailable"); }
        var profiles = http.RequestServices.GetRequiredService<CharacterProfileStore>();
        var db = http.RequestServices.GetRequiredService<AccountDatabase>();
        var exists = await db.Users.AsNoTracking().AnyAsync(user => user.Id == owner.OwnerId && !user.Disabled, http.RequestAborted);
        // An accepted operation retains its captured revision even after a later
        // ordinary profile edit. Revocation is a separate durable fence.
        if (!exists || owner.SessionId is null && await profiles.CaptureAsync(owner.OwnerId, owner.ProfileId, owner.Revision, http.RequestAborted) is null)
        { throw new CustomerSessionException(409, "character_revision_mismatch"); }
        var session = journal.RedeemCustomerTicket(device, request);
        if (session.Snapshot.Length == 0 || session.EndRequested || session.State is "recovering" or "completed" or "failed")
        { throw new CustomerSessionException(409, "customer_session_requires_reconciliation"); }
        var challenge = journal.CustomerChallenge(device)!;
        var create = new CreateConversationRequest(device, Guid.Parse(session.BootId), challenge.Generation,
            challenge.CatalogVersion, "cs-CZ", new(true, false), challenge.OperationEpoch);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(create, Json);
        var created = conversations.Create(device, Guid.Parse(session.OperationId), bytes, create, customerSessionId: session.Id);
        if (created.Response is null) { throw new CustomerSessionException(409, "device_busy"); }
        if (owner.SessionId is null && await profiles.CaptureAsync(owner.OwnerId, owner.ProfileId, owner.Revision, http.RequestAborted) is null)
        {
            journal.RequestCustomerEnd(session.Id, session.OwnerId);
            throw new CustomerSessionException(409, "character_revision_mismatch");
        }
        // Verified personal continuity never derives from the character name.
        var guest = "account_" + OperationJournal.CustomerHash(session.OwnerId + ":" + session.ProfileId).ToLowerInvariant();
        if (evenings.GuestPermission(device, created.Response.ConversationId) is null)
        {
            var captured = JsonSerializer.Deserialize<CustomerConversationSnapshot>(session.Snapshot, Json)!;
            var bound = evenings.SetGuestPermission(device, created.Response.ConversationId, guest,
                "verified_customer_account", true, captured.PersonalMemoryConsent, false, 0);
            if (bound is null) { throw new CustomerSessionException(409, "guest_binding_conflict"); }
        }
        return Results.Json(new { created.Response.ConversationId, created.Response.ExpiresAt, created.Response.Limits,
            customerSessionId = session.Id }, statusCode: 201);
    }

    private static object Project(CustomerSessionRecord session) => new { session.Id, session.OperationId,
        session.DeviceId, session.ProfileId, session.ProfileRevision, session.State, session.EndRequested, session.CreatedAt,
        characterName = CharacterName(session) };
    private static string? CharacterName(CustomerSessionRecord session)
    {
        if (session.Snapshot.Length == 0) { return null; }
        using var snapshot = JsonDocument.Parse(session.Snapshot);
        return snapshot.RootElement.GetProperty("profile").GetProperty("userInput").GetProperty("name").GetString();
    }
    private static string Owner(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private static string Device(HttpContext http, DeviceAccessStore access)
    {
        // Account device grants may not mint challenges or acknowledge physical state.
        if (!http.Request.Headers.ContainsKey("Authorization") || DeviceAccessEndpoints.Authenticate(http, access) is not { } device)
        { throw new CustomerSessionException(401, "device_token_required"); }
        return device;
    }
    private static bool Uuid(string? value) => Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty;
    private static bool Opaque(string? value, int minimum, int maximum) => value is not null
        && value.Length >= minimum && value.Length <= maximum && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    private static async Task<T> ReadAsync<T>(HttpContext http)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[1024];
        while (true)
        {
            var count = await http.Request.Body.ReadAsync(bytes, http.RequestAborted);
            if (count == 0) { break; }
            if (buffer.Length + count > 2048) { throw new CustomerSessionException(413, "request_too_large"); }
            buffer.Write(bytes, 0, count);
        }
        return JsonSerializer.Deserialize<T>(buffer.ToArray(), Json) ?? throw new JsonException();
    }
    private static void AddErrors(RouteGroupBuilder group) => group.AddEndpointFilter(async (invocation, next) =>
    {
        try
        {
            if (invocation.HttpContext.RequestServices.GetService<CharacterProfileStore>() is { } profiles)
            {
                await CharacterEndpoints.ReconcileDeletionsAsync(invocation.HttpContext.RequestServices,
                    profiles, invocation.HttpContext.RequestAborted);
            }
            return await next(invocation);
        }
        catch (CustomerSessionException error) { return ApiProblem.Create(invocation.HttpContext, error.Status, error.Code); }
        catch (JsonException) { return ApiProblem.Create(invocation.HttpContext, 400, "invalid_customer_request"); }
    });
    private sealed record CustomerIdle(string BootId);
    private sealed record CustomerModeChange(bool Enabled);
}

internal sealed record CustomerConversationSnapshot(CharacterConversationSnapshot Profile, bool PersonalMemoryConsent, bool OpenAiConsent = false);

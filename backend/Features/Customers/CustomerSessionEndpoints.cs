using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Customers;

internal static partial class CustomerSessionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    internal static void MapCustomerSessions(this WebApplication app, bool includeSessionResults = true)
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
        if (includeSessionResults) { accounts.MapCustomerSessionResult(); }
        MapCustomerWaiting(accounts);
        accounts.MapPost("/start-tickets", async (HttpContext http, CustomerAdmissionAuthority authority,
            OperationJournal journal, EveningMemoryStore evenings, IDataProtectionProvider protection) =>
        {
            var request = await ReadAsync<CustomerTicketRequest>(http);
            if (request.ProtocolVersion != 1 || !Uuid(request.OperationId) || !Uuid(request.BootId)
                || !(Uuid(request.ProfileId) || Guid.TryParseExact(request.ProfileId, "N", out _))
                || request.ProfileRevision < 1 || !Opaque(request.DeviceId, 1, 128)
                || !Opaque(request.Challenge, 16, 128))
            { throw new CustomerSessionException(400, "invalid_start_request"); }
            if (!request.OpenAiConsent) { throw new CustomerSessionException(409, "openai_consent_required"); }
            if (http.Request.Headers.ContainsKey("Authorization"))
            { throw new CustomerSessionException(401, "account_required"); }
            authority.ValidateDatabasePaths(journal.CustomerDataDatabasePath, evenings.CustomerMemoryDatabasePath);
            using var guard = authority.AcquireCookie(http, token: http.RequestAborted);
            var owner = guard.Proof.AccountId;
            var protector = protection.CreateProtector("DildoVander.CustomerStartTicket.v1");
            if (guard.IsProfileRevoked(request.ProfileId))
            { throw new CustomerSessionException(410, "customer_profile_revoked"); }
            if (journal.HasCustomerTicketForOperation(owner, request.OperationId))
            {
                // Revalidate the immutable receipt and revocation fences before
                // applying any requirements that belong only to new admission.
                var replay = journal.ReplayCustomerTicket(owner, request, guard.ValidateCurrent);
                if (replay.ProtectedToken.Length == 0) { throw new CustomerSessionException(410, "start_ticket_revoked"); }
                return Results.Ok(new { protocolVersion = 1, ticket = protector.Unprotect(replay.ProtectedToken),
                    operationId = request.OperationId, expiresAt = replay.ExpiresAt, sessionId = replay.SessionId });
            }
            var snapshot = guard.CaptureOwnedProfile(request.ProfileId, request.ProfileRevision);
            var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var captured = JsonSerializer.Serialize(new CustomerConversationSnapshot(snapshot, request.PersonalMemoryConsent, OpenAiConsent: true), Json);
            var protectedToken = protector.Protect(token);
            CustomerTicketRecord Issue() => journal.IssueCustomerTicket(owner, request, captured,
                OperationJournal.CustomerHash(token), protectedToken, guard.ValidateCurrent);
            var waiting = journal.ReadCustomerWaitingEvent(request.DeviceId);
            var result = waiting is null
                ? Issue()
                : evenings.WithCustomerWaitingTicket(request.DeviceId, waiting.EveningId,
                    waiting.ActivationRevision, Issue, http.RequestAborted);
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
        var query = http.Request.Query;
        if (query.ContainsKey("bootId") || query.ContainsKey("ready"))
        {
            var boots = query["bootId"];
            var ready = query["ready"];
            if (boots.Count != 1 || ready.Count != 1 || !Uuid(boots[0]) || ready[0] is not ("true" or "false"))
            {
                throw new CustomerSessionException(400, "invalid_runtime_observation");
            }
            // Old clients omit these fields and continue to receive the same
            // status. Only a matching authenticated runtime can supply presence.
            journal.ObserveCustomerRuntime(device, boots[0]!, ready[0] == "true");
        }
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
        var hash = OperationJournal.CustomerHash(request.Ticket);
        var initial = journal.ReadCustomerTicketAdmission(hash)
            ?? throw new CustomerSessionException(404, "start_ticket_unavailable");
        if (initial.DeviceId != device || initial.OperationId != request.OperationId
            || initial.BootId != request.BootId || initial.Challenge != request.Challenge)
        { throw new CustomerSessionException(409, "start_ticket_binding_mismatch"); }
        var authority = http.RequestServices.GetRequiredService<CustomerAdmissionAuthority>();
        authority.ValidateDatabasePaths(journal.CustomerDataDatabasePath, evenings.CustomerMemoryDatabasePath);
        try
        {
            using var guard = authority.AcquireDelegated(initial.Owner, http.RequestAborted);
            var receipt = journal.ReadCustomerTicketAdmission(hash)
                ?? throw new CustomerSessionException(404, "start_ticket_unavailable");
            var owner = receipt.Owner;
            if (owner.OwnerId != guard.Proof.AccountId || owner.ProfileId != initial.Owner.ProfileId
                || owner.Revision != initial.Owner.Revision)
            { throw new CustomerSessionException(409, "start_ticket_binding_mismatch"); }
            if (guard.IsProfileRevoked(owner.ProfileId))
            {
                if (owner.SessionId is { } revokedSession) journal.RequestCustomerEnd(revokedSession, owner.OwnerId);
                throw new CustomerSessionException(409, "customer_session_requires_reconciliation");
            }
            // Re-read acceptance under the reservations. Accepted replay keeps
            // its original context after ordinary edits and ticket expiry.
            if (owner.SessionId is null) guard.ReadOwnedProfile(owner.ProfileId, owner.Revision);
            var clock = http.RequestServices.GetRequiredService<TimeProvider>();
            void ValidateRedemption()
            {
                guard.ValidateCurrent();
                if (owner.SessionId is null && receipt.ExpiresAt <= clock.GetUtcNow().ToUnixTimeSeconds())
                { throw new CustomerSessionException(410, "start_ticket_expired"); }
            }
            CustomerSessionRecord Redeem() => journal.RedeemCustomerTicket(device, request, ValidateRedemption);
            var waiting = journal.ReadCustomerWaitingEvent(device);
            var session = owner.SessionId is not null || waiting is null
                ? Redeem()
                : evenings.WithCustomerWaitingSession(device, waiting.EveningId,
                    waiting.ActivationRevision, Redeem, http.RequestAborted);
            if (session.Snapshot.Length == 0 || session.EndRequested || session.State is "recovering" or "completed" or "failed")
            { throw new CustomerSessionException(409, "customer_session_requires_reconciliation"); }
            guard.ValidateCurrent();
            // The waiting memory reservation has ended before Create acquires
            // conversation -> memory -> journal. Identity/profile guards remain.
            var challenge = journal.CustomerChallenge(device)!;
            var create = new CreateConversationRequest(device, Guid.Parse(session.BootId), challenge.Generation,
                challenge.CatalogVersion, "cs-CZ", new(true, false), challenge.OperationEpoch);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(create, Json);
            var created = conversations.Create(device, Guid.Parse(session.OperationId), bytes, create, customerSessionId: session.Id);
            if (created.Response is null) { throw new CustomerSessionException(409, "device_busy"); }
            guard.ValidateCurrent();
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
        catch (CustomerDataAccessException error) when (error.Status == 401)
        {
            // This is delegated account revocation, not an invalid device token.
            // Preserve the lease and request normal device reconciliation.
            var revoked = journal.ReadCustomerTicketAdmission(hash)?.Owner;
            if (revoked?.SessionId is { } id) journal.RequestCustomerEnd(id, revoked.OwnerId);
            throw new CustomerSessionException(409, "customer_session_requires_reconciliation");
        }
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
        catch (CustomerDataAccessException error) { return ApiProblem.Create(invocation.HttpContext, error.Status, error.Code); }
        catch (JsonException) { return ApiProblem.Create(invocation.HttpContext, 400, "invalid_customer_request"); }
    });
    private sealed record CustomerIdle(string BootId);
    private sealed record CustomerModeChange(bool Enabled);
}

internal sealed record CustomerConversationSnapshot(CharacterConversationSnapshot Profile, bool PersonalMemoryConsent, bool OpenAiConsent = false);

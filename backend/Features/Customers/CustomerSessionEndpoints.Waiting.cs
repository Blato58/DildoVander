using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.CustomerData;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Kufr.Backend.Features.Customers;

internal static partial class CustomerSessionEndpoints
{
    internal static void MapCustomerWaiting(RouteGroupBuilder accounts)
    {
        var waiting = accounts.MapGroup("/waiting");
        AddAdmissionErrors(waiting);
        waiting.MapGet("/{deviceId}", ReadWaitingAsync);
        waiting.MapPost("/{deviceId}/join", JoinWaitingAsync);
        waiting.MapPost("/{deviceId}/leave", LeaveWaitingAsync);
        waiting.MapPost("/{deviceId}/claim", ClaimWaitingAsync);

        var admin = accounts.MapGroup("/admin/devices");
        AddAdmissionErrors(admin);
        admin.MapGet("/{deviceId}/waiting", ReadOperatorWaitingAsync);
        admin.MapPost("/{deviceId}/waiting/open", OpenWaitingAsync);
        admin.MapPost("/{deviceId}/waiting/state", ChangeWaitingStateAsync);
        admin.MapPost("/{deviceId}/waiting/skip", SkipWaitingAsync);
    }

    private static IResult ReadWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        ValidateWaitingDevice(deviceId, http);
        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        return CustomerWaitingResponse(guard.Proof.AccountId, deviceId, journal, evenings, http.RequestAborted);
    }

    private static async Task<IResult> JoinWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        var request = await ReadAsync<WaitingJoinRequest>(http);
        ValidateWaitingDevice(deviceId, http);
        if (request.SchemaVersion != 1 || !Uuid(request.RequestId) || !Uuid(request.EventId)
            || !ProfileUuid(request.ProfileId) || !Uuid(request.BootId) || !Opaque(request.Challenge, 16, 128))
        { throw new CustomerSessionException(400, "invalid_waiting_request"); }

        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, request.ProfileId, token: http.RequestAborted);
        var command = new CustomerWaitingJoin(request.RequestId, request.EventId, request.ProfileId);
        var proximity = new CustomerWaitingProximity(request.BootId, request.Challenge);
        if (journal.HasCustomerWaitingReceipt(deviceId, guard.Proof.AccountId, request.RequestId))
        {
            journal.JoinCustomerWaiting(guard.Proof.AccountId, deviceId, command, proximity, guard.ValidateCurrent);
        }
        else
        {
            var scope = evenings.ReadCustomerWaitingEvening(deviceId, http.RequestAborted);
            var current = journal.ReadCustomerWaitingEvent(deviceId);
            if (!IsCurrentWaitingScope(scope, current) || current is null || current.Id != request.EventId || current.State != "open")
            { throw new CustomerSessionException(409, "waiting_event_unavailable"); }
            evenings.WithCustomerWaitingEvening(deviceId, current.EveningId, current.ActivationRevision,
                () => journal.JoinCustomerWaiting(guard.Proof.AccountId, deviceId, command, proximity, guard.ValidateCurrent),
                http.RequestAborted);
        }
        return CustomerWaitingResponse(guard.Proof.AccountId, deviceId, journal, evenings, http.RequestAborted);
    }

    private static async Task<IResult> LeaveWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        var request = await ReadAsync<WaitingEntryRequest>(http);
        ValidateWaitingDevice(deviceId, http);
        ValidateWaitingEntryRequest(request.SchemaVersion, request.RequestId, request.EventId, request.EntryId, request.ExpectedRevision);

        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        var profileId = journal.CustomerWaitingOwnedEntryProfile(guard.Proof.AccountId, deviceId, request.EventId, request.EntryId);
        if (profileId is null)
        { throw new CustomerSessionException(404, "waiting_entry_unavailable"); }
        guard.ReadOwnedProfile(profileId);
        journal.LeaveCustomerWaiting(guard.Proof.AccountId, deviceId,
            new(request.RequestId, request.EventId, request.EntryId, request.ExpectedRevision), guard.ValidateCurrent);
        return CustomerWaitingResponse(guard.Proof.AccountId, deviceId, journal, evenings, http.RequestAborted);
    }

    private static async Task<IResult> ClaimWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        var request = await ReadAsync<WaitingClaimRequest>(http);
        ValidateWaitingDevice(deviceId, http);
        ValidateWaitingEntryRequest(request.SchemaVersion, request.RequestId, request.EventId, request.EntryId, request.ExpectedRevision);
        if (!Uuid(request.BootId) || !Opaque(request.Challenge, 16, 128))
        { throw new CustomerSessionException(400, "invalid_waiting_proximity"); }

        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        var profileId = journal.CustomerWaitingOwnedEntryProfile(guard.Proof.AccountId, deviceId, request.EventId, request.EntryId);
        if (profileId is null)
        { throw new CustomerSessionException(404, "waiting_entry_unavailable"); }
        guard.ReadOwnedProfile(profileId);
        var command = new CustomerWaitingEntryCommand(request.RequestId, request.EventId, request.EntryId, request.ExpectedRevision);
        var proximity = new CustomerWaitingProximity(request.BootId, request.Challenge);
        if (journal.HasCustomerWaitingReceipt(deviceId, guard.Proof.AccountId, request.RequestId))
        {
            journal.ClaimCustomerWaiting(guard.Proof.AccountId, deviceId, command, proximity, guard.ValidateCurrent);
        }
        else
        {
            var scope = evenings.ReadCustomerWaitingEvening(deviceId, http.RequestAborted);
            var current = journal.ReadCustomerWaitingEvent(deviceId);
            if (!IsCurrentWaitingScope(scope, current) || current is null || current.Id != request.EventId || current.State != "open")
            { throw new CustomerSessionException(409, "waiting_event_unavailable"); }
            evenings.WithCustomerWaitingEvening(deviceId, current.EveningId, current.ActivationRevision, () =>
            {
                var availability = journal.CustomerAvailabilityFor(deviceId, true, paused: false);
                if (availability.State != CustomerAvailabilityState.Available || !availability.Fresh)
                { throw new CustomerSessionException(409, "waiting_runtime_unavailable"); }
                return journal.ClaimCustomerWaiting(guard.Proof.AccountId, deviceId, command, proximity, guard.ValidateCurrent);
            }, http.RequestAborted);
        }
        return CustomerWaitingResponse(guard.Proof.AccountId, deviceId, journal, evenings, http.RequestAborted);
    }

    private static IResult ReadOperatorWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        ValidateWaitingDevice(deviceId, http);
        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        guard.RequireDevicePermission(deviceId);
        return OperatorWaitingResponse(deviceId, journal, evenings, http.RequestAborted);
    }

    private static async Task<IResult> OpenWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        var request = await ReadAsync<WaitingOpenRequest>(http);
        ValidateWaitingDevice(deviceId, http);
        if (request.SchemaVersion != 1 || !Uuid(request.RequestId)
            || (request.PreviousEventId is null) != (request.ExpectedRevision is null)
            || request.PreviousEventId is not null && (!Uuid(request.PreviousEventId) || request.ExpectedRevision is null or <= 0))
        { throw new CustomerSessionException(400, "invalid_waiting_request"); }

        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        guard.RequireDevicePermission(deviceId);
        var hasReceipt = journal.HasCustomerWaitingReceipt(deviceId, null, request.RequestId);
        if (hasReceipt)
        {
            // This context only reconstructs an immutable server-derived input for
            // the writer's full fingerprint check; it is not returned or trusted as
            // current event/authority state.
            var replay = journal.ReadCustomerWaitingOpenReplayContext(deviceId, request.RequestId);
            if (replay is null)
            { throw new CustomerSessionException(409, "waiting_request_conflict"); }
            journal.OpenCustomerWaiting(deviceId, replay.EveningId, request.RequestId,
                request.PreviousEventId, request.ExpectedRevision, replay.ActivationRevision, guard.ValidateCurrent);
        }
        else
        {
            var scope = evenings.ReadCustomerWaitingEvening(deviceId, http.RequestAborted);
            var eveningId = scope.EveningId;
            var activationRevision = scope.ActivationRevision;
            if (!scope.Open || eveningId is null || activationRevision is null)
            { throw new CustomerSessionException(409, "waiting_evening_unavailable"); }
            evenings.WithCustomerWaitingEvent(deviceId, eveningId, activationRevision,
                () => journal.OpenCustomerWaiting(deviceId, eveningId, request.RequestId,
                    request.PreviousEventId, request.ExpectedRevision, activationRevision, guard.ValidateCurrent), http.RequestAborted);
        }
        return OperatorWaitingResponse(deviceId, journal, evenings, http.RequestAborted);
    }

    private static async Task<IResult> ChangeWaitingStateAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        var request = await ReadAsync<WaitingStateRequest>(http);
        ValidateWaitingDevice(deviceId, http);
        if (request.SchemaVersion != 1 || !Uuid(request.RequestId) || !Uuid(request.EventId)
            || request.ExpectedRevision < 1 || request.State is not ("open" or "paused" or "closed"))
        { throw new CustomerSessionException(400, "invalid_waiting_request"); }

        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        guard.RequireDevicePermission(deviceId);
        if (request.State == "open")
        {
            if (journal.HasCustomerWaitingReceipt(deviceId, null, request.RequestId))
            {
                journal.ChangeCustomerWaitingEvent(deviceId, request.EventId, request.RequestId,
                    request.ExpectedRevision, request.State, guard.ValidateCurrent);
            }
            else
            {
                var scope = evenings.ReadCustomerWaitingEvening(deviceId, http.RequestAborted);
                var current = journal.ReadCustomerWaitingEvent(deviceId);
                var eveningId = scope.EveningId;
                var activationRevision = scope.ActivationRevision;
                if (!scope.Open || eveningId is null || activationRevision is null
                    || current is null || current.Id != request.EventId || current.EveningId != eveningId
                    || current.ActivationRevision != activationRevision)
                { throw new CustomerSessionException(409, "waiting_evening_unavailable"); }
                evenings.WithCustomerWaitingEvent(deviceId, eveningId, activationRevision,
                    () => journal.ChangeCustomerWaitingEvent(deviceId, request.EventId, request.RequestId,
                        request.ExpectedRevision, request.State, guard.ValidateCurrent), http.RequestAborted);
            }
        }
        else
        {
            journal.ChangeCustomerWaitingEvent(deviceId, request.EventId, request.RequestId,
                request.ExpectedRevision, request.State, guard.ValidateCurrent);
        }
        return OperatorWaitingResponse(deviceId, journal, evenings, http.RequestAborted);
    }

    private static async Task<IResult> SkipWaitingAsync(string deviceId, HttpContext http,
        CustomerAdmissionAuthority authority, OperationJournal journal, EveningMemoryStore evenings)
    {
        var request = await ReadAsync<WaitingEntryRequest>(http);
        ValidateWaitingDevice(deviceId, http);
        ValidateWaitingEntryRequest(request.SchemaVersion, request.RequestId, request.EventId, request.EntryId, request.ExpectedRevision);
        using var guard = AcquireWaitingCookie(http, authority, journal, evenings, token: http.RequestAborted);
        guard.RequireDevicePermission(deviceId);
        journal.CancelCustomerWaiting(deviceId,
            new(request.RequestId, request.EventId, request.EntryId, request.ExpectedRevision), guard.ValidateCurrent);
        return OperatorWaitingResponse(deviceId, journal, evenings, http.RequestAborted);
    }

    private static IResult CustomerWaitingResponse(string owner, string deviceId, OperationJournal journal,
        EveningMemoryStore evenings, CancellationToken token)
    {
        var scope = evenings.ReadCustomerWaitingEvening(deviceId, token);
        var current = journal.ReadCustomerWaitingEvent(deviceId);
        var currentScope = IsCurrentWaitingScope(scope, current);
        var view = currentScope
            ? journal.ReadCustomerWaiting(owner, deviceId, current!.Id)
            : new CustomerWaitingView(null, null, null, 0);
        var availability = journal.CustomerAvailabilityFor(deviceId, currentScope, current?.State == "paused");
        return Results.Ok(CustomerWaitingPresentation.Project(view, availability));
    }

    private static IResult OperatorWaitingResponse(string deviceId, OperationJournal journal,
        EveningMemoryStore evenings, CancellationToken token)
    {
        var scope = evenings.ReadCustomerWaitingEvening(deviceId, token);
        var current = journal.ReadCustomerWaitingEvent(deviceId);
        var currentScope = IsCurrentWaitingScope(scope, current);
        // Operators need the latest durable event/revision even after closure
        // or activation changes, so an explicit replacement can name it.
        var view = journal.ReadCustomerWaitingOperator(deviceId);
        var availability = journal.CustomerAvailabilityFor(deviceId, currentScope, current?.State == "paused");
        var eventView = view.Event is { } item
            ? new CustomerWaitingHttpEvent(item.Id, item.DeviceId, item.EveningId, item.State, item.Revision)
            : null;
        return Results.Ok(new { schemaVersion = 1, availability = HttpAvailability(availability), @event = eventView,
            head = view.Head, count = view.Count });
    }

    private static CustomerAdmissionGuard AcquireWaitingCookie(HttpContext http, CustomerAdmissionAuthority authority,
        OperationJournal journal, EveningMemoryStore evenings, string? profileId = null, long? expectedRevision = null,
        CancellationToken token = default)
    {
        if (http.Request.Headers.ContainsKey("Authorization"))
        { throw new CustomerDataAccessException(401, "account_access_unavailable"); }
        authority.ValidateDatabasePaths(journal.CustomerDataDatabasePath, evenings.CustomerMemoryDatabasePath);
        return authority.AcquireCookie(http, profileId, expectedRevision, token);
    }

    private static void AddAdmissionErrors(RouteGroupBuilder group) => group.AddEndpointFilter(async (invocation, next) =>
    {
        try { return await next(invocation); }
        catch (CustomerDataAccessException error)
        { return ApiProblem.Create(invocation.HttpContext, error.Status, error.Code); }
    });

    private static bool IsCurrentWaitingScope(CustomerWaitingEveningScope scope, CustomerWaitingEvent? current)
        => scope.Open && current is not null && current.State != "closed"
            && current.EveningId == scope.EveningId && current.ActivationRevision == scope.ActivationRevision;

    private static CustomerWaitingHttpAvailability HttpAvailability(CustomerAvailabilityProjection availability)
    {
        var state = availability.State switch
        {
            CustomerAvailabilityState.Available => "available",
            CustomerAvailabilityState.Busy => "busy",
            CustomerAvailabilityState.Recovering => "recovering",
            CustomerAvailabilityState.Paused => "paused",
            CustomerAvailabilityState.Offline => "offline",
            _ => "unknown"
        };
        return new(state, availability.Reason, availability.Fresh);
    }

    private static void ValidateWaitingDevice(string deviceId, HttpContext http)
    {
        var rawTarget = http.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = rawTarget?.Split('?', 2)[0] ?? http.Request.Path.Value ?? string.Empty;
        if (rawPath.Contains('%') || rawPath.Contains('\\') || !Opaque(deviceId, 1, 128) || deviceId is "." or "..")
        { throw new CustomerSessionException(400, "invalid_waiting_device"); }
    }

    private static bool ProfileUuid(string? value)
        => Uuid(value) || value is not null && Guid.TryParseExact(value, "N", out var profile) && profile != Guid.Empty;

    private static void ValidateWaitingEntryRequest(int schemaVersion, string requestId, string eventId,
        string entryId, long expectedRevision)
    {
        if (schemaVersion != 1 || !Uuid(requestId) || !Uuid(eventId) || !Uuid(entryId) || expectedRevision < 1)
        { throw new CustomerSessionException(400, "invalid_waiting_request"); }
    }

    private sealed record WaitingJoinRequest(int SchemaVersion, string RequestId, string EventId,
        string ProfileId, string BootId, string Challenge);
    private sealed record WaitingEntryRequest(int SchemaVersion, string RequestId, string EventId,
        string EntryId, long ExpectedRevision);
    private sealed record WaitingClaimRequest(int SchemaVersion, string RequestId, string EventId,
        string EntryId, long ExpectedRevision, string BootId, string Challenge);
    private sealed record WaitingOpenRequest(int SchemaVersion, string RequestId, string? PreviousEventId,
        long? ExpectedRevision);
    private sealed record WaitingStateRequest(int SchemaVersion, string RequestId, string EventId,
        long ExpectedRevision, string State);
}

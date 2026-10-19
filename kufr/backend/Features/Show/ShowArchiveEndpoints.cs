using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Show;

internal static class ShowArchiveEndpoints
{
    internal sealed record EndEveningRequest(string EveningId, long ExpectedStateRevision);

    internal static void MapShowArchiveEndpoints(this WebApplication app)
    {
        var routes = app.MapGroup("/v1/show/devices/{deviceId}");
        routes.AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            if (!services.GetRequiredService<ServerAdminAccess>().Authenticate(context.HttpContext))
            { return Results.Unauthorized(); }
            var device = context.HttpContext.Request.RouteValues["deviceId"]?.ToString();
            if (device is null || !services.GetRequiredService<ShowCoordinator>().IsKnownDevice(device))
            { return Results.NotFound(); }
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return await next(context);
        });
        routes.MapGet("/identities", (string deviceId, string eveningId, KeepsakeStore store) =>
            Results.Json(store.ListPublicWizardIdentities(deviceId, eveningId).Select(identity => new
            {
                identity.IdentityId, displayName = identity.WizardAlias, wandId = identity.CatalogWandId
            })));
        routes.MapGet("/prophecies", (string deviceId, string eveningId, KeepsakeStore store) =>
            Results.Json(store.ListOperatorProphecies(deviceId, eveningId)));
        routes.MapPost("/prophecies/{outcomeId}/reveal", (string deviceId, string outcomeId,
            RevealRequest request, KeepsakeStore store) =>
        {
            try { return Results.Json(new { changed = store.RevealProphecy(deviceId, request.EveningId, outcomeId) }); }
            catch (KeepsakeException error)
            { return Results.Json(new { errorCode = error.Code }, statusCode: error.Status); }
        });
        routes.MapPost("/end-evening", (string deviceId, EndEveningRequest request,
            ShowCoordinator director, EveningMemoryStore memory, KeepsakeStore store) =>
        {
            ChronicleSnapshot? snapshot = null;
            var closed = director.TryCloseEvening(deviceId, request.ExpectedStateRevision, request.EveningId, () =>
            {
                snapshot = memory.EndShowEvening(deviceId, request.EveningId);
                if (snapshot is not null) { store.UnlockEveningProphecies(deviceId, request.EveningId); }
            });
            if (!closed) { return Results.Json(new { errorCode = "state_conflict_or_visit_active" }, statusCode: 409); }
            return snapshot is null ? Results.NotFound() : Results.Json(snapshot);
        });
    }

    internal sealed record RevealRequest(string EveningId);
}

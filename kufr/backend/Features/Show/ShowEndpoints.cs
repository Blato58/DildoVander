using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Show;

internal static class ShowEndpoints
{
    internal static IEndpointRouteBuilder MapShowEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/show/devices/{deviceId}", (string deviceId, HttpContext context,
            ServerAdminAccess admin, ShowCoordinator show) =>
        {
            if (!admin.Authenticate(context)) { return ApiProblem.Create(context, 401, "server_admin_required"); }
            if (!show.IsKnownDevice(deviceId)) { return ApiProblem.Create(context, 404, "device_not_found"); }
            return Results.Ok(show.Read(deviceId));
        });
        routes.MapGet("/v1/show/content", (HttpContext context, ServerAdminAccess admin) =>
        {
            if (!admin.Authenticate(context)) { return ApiProblem.Create(context, 401, "server_admin_required"); }
            return Results.Ok(new { sceneVersion = ShowSceneLibrary.Version, scenes = ShowSceneLibrary.Archetypes,
                definitions = ShowSceneLibrary.Definitions("content-preview"), repertoireVersion = ShowRepertoire.Version,
                wands = ShowRepertoire.Wands, ambient = ShowRepertoire.Ambient });
        });
        routes.MapPost("/v1/show/devices/{deviceId}/commands", (string deviceId, ShowCommand command,
            HttpContext context, ServerAdminAccess admin, ShowCoordinator show) =>
        {
            if (!admin.Authenticate(context)) { return ApiProblem.Create(context, 401, "server_admin_required"); }
            if (!show.IsKnownDevice(deviceId)) { return ApiProblem.Create(context, 404, "device_not_found"); }
            var result = show.Command(deviceId, command);
            var status = result.Status is "accepted" or "replayed" ? 200 : result.Status == "expired" ? 410
                : result.Code.StartsWith("invalid_", StringComparison.Ordinal) ? 400 : 409;
            return Results.Json(result, statusCode: status);
        });
        return routes;
    }
}

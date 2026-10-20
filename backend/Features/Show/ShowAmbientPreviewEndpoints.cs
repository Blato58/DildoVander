using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Kufr.Backend.Features.Show;

internal static class ShowAmbientPreviewEndpoints
{
    internal static IEndpointRouteBuilder MapShowAmbientPreviews(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/show/devices/{deviceId}/ambient-previews", async (string deviceId,
            ShowAmbientPreviewRequest request, HttpContext context, ShowCoordinator show, [FromServices] ShowAmbientPreview preview) =>
        {
            var denied = await AuthorizeAsync(context, deviceId, show);
            if (denied is not null) { return denied; }
            return Respond(context, preview.Start(deviceId, request.OperationId));
        });
        routes.MapGet("/v1/show/devices/{deviceId}/ambient-previews/{operationId:guid}", async (string deviceId,
            Guid operationId, HttpContext context, ShowCoordinator show, [FromServices] ShowAmbientPreview preview) =>
        {
            var denied = await AuthorizeAsync(context, deviceId, show);
            if (denied is not null) { return denied; }
            return Respond(context, preview.Read(deviceId, operationId));
        });
        return routes;
    }

    private static async Task<IResult?> AuthorizeAsync(HttpContext context, string device, ShowCoordinator show)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        // Reading generated preview audio requires the same operator authority as starting it.
        var denied = await AccountShowAuthorization.RequireAsync(context, device, requiredPermission: "operate");
        return denied ?? (show.IsKnownDevice(device) ? null : ApiProblem.Create(context, 404, "device_not_found"));
    }

    private static IResult Respond(HttpContext context, ShowAmbientPreviewResult result)
    {
        if (result.StatusCode == 202) { context.Response.Headers.RetryAfter = "1"; }
        return Results.Json(result.Response, statusCode: result.StatusCode);
    }
}

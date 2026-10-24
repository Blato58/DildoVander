using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.Show;

internal static class DuelEndpoints
{
    public static IEndpointRouteBuilder MapDuels(this IEndpointRouteBuilder endpoints)
    {
        var operators = endpoints.MapGroup("/v1/show/devices/{deviceId}/duels");
        operators.AddEndpointFilter(async (invocation, next) =>
        {
            var context = invocation.HttpContext;
            PrepareResponse(context);
            var device = context.Request.RouteValues["deviceId"]?.ToString();
            var denied = await AccountShowAuthorization.RequireAsync(context, device ?? "");
            if (denied is not null) { return denied; }
            var config = context.RequestServices.GetRequiredService<IConfiguration>();
            if (string.IsNullOrWhiteSpace(device)
                || !config.GetSection("Devices").GetChildren().Any(section => section["DeviceId"] == device))
            {
                return Error(404, "device_unavailable");
            }
            return await next(invocation);
        });
        operators.MapGet("", (HttpContext context, string deviceId, string eveningId, DuelStore store) =>
            Respond(context, () => store.List(deviceId, eveningId)));
        operators.MapPost("", (HttpContext context, string deviceId, DuelCreateRequest request, DuelStore store) =>
            Respond(context, () => store.Create(deviceId, request)));
        operators.MapGet("/{id:guid}", (HttpContext context, string deviceId, string id, DuelStore store) =>
            Respond(context, () => store.Read(id, deviceId)));
        operators.MapPost("/{id:guid}/start", (HttpContext context, string deviceId, string id, DuelCommand request, DuelStore store) =>
            Respond(context, () => store.Start(deviceId, id, request)));
        operators.MapPost("/{id:guid}/cancel", (HttpContext context, string deviceId, string id, DuelCommand request, DuelStore store) =>
            Respond(context, () => store.Cancel(deviceId, id, request)));
        operators.MapPost("/{id:guid}/grants", (HttpContext context, string deviceId, string id, DuelGrantRequest request, DuelStore store) =>
            Respond(context, () => store.Grant(deviceId, id, request)));
        operators.MapPost("/{id:guid}/grants/revoke", (HttpContext context, string deviceId, string id, DuelGrantRequest request, DuelStore store) =>
            Respond(context, () => store.Revoke(deviceId, id, request)));

        var guests = endpoints.MapGroup("/guest/v1/duels").RequireRateLimiting("keepsake-public");
        guests.AddEndpointFilter(async (invocation, next) =>
        {
            PrepareResponse(invocation.HttpContext);
            return invocation.HttpContext.Request.Query.Count == 0 ? await next(invocation) : Error(400, "invalid_request");
        });
        guests.MapGet("/{id:guid}", (HttpContext context, string id, IConfiguration config, DuelStore store) =>
        {
            var device = PublicDevice(context, config);
            return device is null ? Error(404, "duel_unavailable")
                : Respond(context, () => store.Read(id, device, publicView: true));
        });
        guests.MapPost("/{id:guid}/choices", (HttpContext context, string id, DuelChoiceRequest request,
            IConfiguration config, DuelStore store) =>
        {
            var device = PublicDevice(context, config);
            if (device is null) { return Error(404, "duel_unavailable"); }
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) { return Error(403, "grant_unavailable"); }
            // The origin/device projection is checked independently of the side grant.
            return Respond(context, () =>
            {
                _ = store.Read(id, device, publicView: true);
                return store.Choose(id, authorization[7..], request);
            });
        });
        guests.MapPost("/{id:guid}/join", (HttpContext context, string id, DuelJoinRequest request,
            IConfiguration config, DuelStore store) =>
        {
            var device = PublicDevice(context, config);
            if (device is null) { return Error(404, "duel_unavailable"); }
            var authorization = context.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) { return Error(403, "grant_unavailable"); }
            return Respond(context, () =>
            {
                _ = store.Read(id, device, publicView: true);
                return store.Join(id, authorization[7..], request);
            });
        });
        return endpoints;
    }

    private static IResult Respond<T>(HttpContext context, Func<T> action)
    {
        PrepareResponse(context);
        try { return Results.Json(action()); }
        catch (DuelException exception) { return Error(exception.Status, exception.Code); }
    }

    private static void PrepareResponse(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static string? PublicDevice(HttpContext context, IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Keepsake:Enabled") || !context.Request.IsHttps
            || !Uri.TryCreate(configuration["Keepsake:Origin"], UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.PathAndQuery != "/" || origin.Fragment.Length != 0
            || origin.UserInfo.Length != 0
            || !string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var device = configuration["Keepsake:PublicDeviceId"];
        return !string.IsNullOrWhiteSpace(device)
            && configuration.GetSection("Devices").GetChildren().Any(section => section["DeviceId"] == device) ? device : null;
    }

    private static IResult Error(int status, string code) => Results.Json(new { schemaVersion = 1, errorCode = code }, statusCode: status);
}

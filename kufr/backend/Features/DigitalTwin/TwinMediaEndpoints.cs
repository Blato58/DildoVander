using System.Text.Json;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.DigitalTwin;

internal static class TwinMediaEndpoints
{
    internal static IEndpointRouteBuilder MapTwinMedia(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/v1/twin/instances/{id}");
        group.MapGet("/settings", async (string id, HttpContext context, TwinAccess access, TwinMediaSettingsStore settings) =>
        {
            var authorization = await ControlAsync(context, access, id, false);
            if (authorization.Error is not null) { return authorization.Error; }
            var value = settings.Read(id);
            return value is null ? ApiProblem.Create(context, 409, "settings_unavailable") : Results.Json(value);
        });
        group.MapPut("/settings", async (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) =>
        {
            var authorization = await ControlAsync(context, access, id, true);
            if (authorization.Error is not null) { return authorization.Error; }
            var change = await ReadJsonAsync<TwinMediaSettingsChange>(context);
            if (change is null) { return ApiProblem.Create(context, 400, "invalid_settings"); }
            var result = media.ChangeSettings(id, authorization.Session!, change);
            return result.Error is null ? Results.Json(result.Settings) : ApiProblem.Create(context, 409, result.Error);
        });
        group.MapPost("/media/start", async (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) =>
        {
            var authorization = await ControlAsync(context, access, id, true);
            if (authorization.Error is not null) { return authorization.Error; }
            var start = await ReadJsonAsync<TwinMediaStart>(context);
            if (start is null) { return ApiProblem.Create(context, 400, "invalid_media_start"); }
            var result = await media.StartAsync(id, authorization.Session!, start);
            return result.Error is null ? Results.Json(result.State) : ApiProblem.Create(context, 409, result.Error);
        });
        group.MapPost("/media/cancel", async (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) =>
        {
            var authorization = await ControlAsync(context, access, id, true);
            if (authorization.Error is not null) { return authorization.Error; }
            var cancel = await ReadJsonAsync<TwinMediaCancel>(context);
            if (cancel is null) { return ApiProblem.Create(context, 400, "invalid_media_cancel"); }
            var error = await media.CancelAsync(id, authorization.Session!, cancel);
            return error is null ? Results.NoContent() : ApiProblem.Create(context, 409, error);
        });
        group.MapGet("/media/state", async (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) =>
        {
            var authorization = await ControlAsync(context, access, id, false);
            if (authorization.Error is not null) { return authorization.Error; }
            var state = media.State(id, authorization.Session!);
            return state is null ? ApiProblem.Create(context, 409, "media_owner_required") : Results.Json(state);
        });
        group.MapGet("/media/output", async (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) =>
        {
            var authorization = await ControlAsync(context, access, id, false);
            if (authorization.Error is not null) { return authorization.Error; }
            var result = await media.OutputAsync(id, authorization.Session!);
            if (result.Error is not null) { return ApiProblem.Create(context, 409, result.Error); }
            if (result.Reply?.Media is not { } frame) { return Results.NoContent(); }
            var fields = JsonSerializer.SerializeToElement(frame, TwinSnapshot.JsonOptions).EnumerateObject()
                .Where(field => field.Value.ValueKind != JsonValueKind.Null)
                .ToDictionary(field => field.Name, field => (object?)field.Value.Clone());
            if (frame.Kind == "pcm") { fields.Add("pcmBase64", Convert.ToBase64String(result.Reply.Attachment!)); }
            return Results.Json(fields);
        });
        group.MapPost("/media/played", async (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) =>
        {
            var authorization = await ControlAsync(context, access, id, true);
            if (authorization.Error is not null) { return authorization.Error; }
            var played = await ReadJsonAsync<TwinMediaPlayed>(context);
            if (played is null) { return ApiProblem.Create(context, 400, "invalid_media_played"); }
            var error = await media.PlayedAsync(id, authorization.Session!, played);
            return error is null ? Results.NoContent() : ApiProblem.Create(context, 409, error);
        });
        group.MapPost("/media/input", (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) => InputAsync(id, context, access, media, false));
        group.MapPost("/media/photo", (string id, HttpContext context, TwinAccess access, TwinMediaSessions media) => InputAsync(id, context, access, media, true));
        return routes;
    }
    private static async Task<IResult> InputAsync(string id, HttpContext context, TwinAccess access, TwinMediaSessions media, bool photo)
    {
        var authorization = await ControlAsync(context, access, id, true);
        if (authorization.Error is not null) { return authorization.Error; }
        if (context.Request.ContentType != (photo ? "image/jpeg" : "application/octet-stream"))
        { return ApiProblem.Create(context, 400, "invalid_media_content_type"); }
        var maximum = photo ? 262144 : 9600;
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) { feature.MaxRequestBodySize = maximum; }
        if (context.Request.ContentLength > maximum) { return ApiProblem.Create(context, 413, "media_body_limit"); }
        try
        {
            var body = await TwinMediaSessions.ReadBoundedAsync(context.Request.Body, maximum, context.RequestAborted);
            var headers = context.Request.Headers;
            var error = await media.InputAsync(id, authorization.Session!, headers["Session-Id"].ToString(), headers["Boot-Id"].ToString(),
                headers["Media-Epoch"].ToString(), headers["Input-Epoch"].ToString(), headers["Input-Sequence"].ToString(), body, photo);
            if (error is null) { context.Response.Headers["X-Twin-Media-Accepted-Bytes"] = body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            Array.Clear(body);
            return error is null ? Results.NoContent() : ApiProblem.Create(context, 409, error);
        }
        catch (Exception error) when (error is InvalidDataException or BadHttpRequestException)
        { return ApiProblem.Create(context, 413, "media_body_limit"); }
    }
    internal static async Task<(TwinSession? Session, IResult? Error)> ControlAsync(HttpContext context, TwinAccess access, string id, bool write)
    {
        var session = access.Session(context.Request.Cookies["__Host-kufr-twin"]);
        if (context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim))
        {
            session = await TwinEndpoints.AccountControlSessionAsync(context, id, context.RequestServices.GetRequiredService<TwinHost>());
            if (session is null) { return (null, ApiProblem.Create(context, 403, "simulation_media_forbidden")); }
        }
        if (session is null) { return (null, ApiProblem.Create(context, 401, "twin_session_required")); }
        if (session.Principal.Role != "control" || session.Principal.Mode != "simulation" || session.Principal.InstanceId != id)
        { return (null, ApiProblem.Create(context, 403, "simulation_media_forbidden")); }
        if (write && !TwinEndpoints.SameOrigin(context)) { return (null, ApiProblem.Create(context, 403, "origin_forbidden")); }
        return (session, null);
    }
    private static async Task<T?> ReadJsonAsync<T>(HttpContext context)
    {
        if (context.Request.ContentLength > 4096) { return default; }
        try { return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, TwinSnapshot.JsonOptions, context.RequestAborted); }
        catch (Exception error) when (error is JsonException or BadHttpRequestException) { return default; }
    }
}

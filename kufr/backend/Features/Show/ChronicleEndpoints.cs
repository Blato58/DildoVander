using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Conversations;
using Kufr.Backend.Features.Keepsakes;

namespace Kufr.Backend.Features.Show;

internal static class ChronicleEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static IEndpointRouteBuilder MapShowChronicle(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/guest/v1/chronicle", (HttpContext context, IConfiguration config, EveningMemoryStore store) =>
        {
            NoCache(context);
            if (context.Request.Query.Count != 0) { return Error(400, "invalid_request"); }
            var device = PublicDevice(context, config);
            if (device is null) { return Error(404, "access_unavailable"); }
            var evening = store.ReadActiveEveningId(device);
            var snapshot = evening is null ? null : store.ReadChronicle(device, evening);
            return snapshot is null ? Error(404, "chronicle_unavailable") : Results.Json(snapshot);
        }).RequireRateLimiting("keepsake-public");

        routes.MapGet("/v1/admin/show/chronicle", (HttpContext context, ServerAdminAccess admin, EveningMemoryStore store) =>
        {
            NoCache(context);
            if (!admin.Authenticate(context)) { return Error(401, "admin_credential_required"); }
            var device = context.Request.Query["deviceId"].ToString();
            var evening = context.Request.Query["eveningId"].ToString();
            if (device.Length == 0 || evening.Length == 0) { return Error(400, "invalid_request"); }
            var snapshot = store.ReadChronicleAdministration(device, evening);
            return snapshot is null ? Error(404, "evening_not_found") : Results.Json(snapshot);
        });

        routes.MapPost("/v1/admin/show/evenings/{eveningId:guid}/end", async (string eveningId,
            HttpContext context, ServerAdminAccess admin, EveningMemoryStore store, KeepsakeStore keepsakes) =>
        {
            NoCache(context);
            if (!admin.Authenticate(context)) { return Error(401, "admin_credential_required"); }
            var request = await ReadCommandAsync(context);
            if (request is null) { return Error(400, "invalid_request"); }
            var snapshot = store.EndShowEvening(request.DeviceId, eveningId);
            if (snapshot is null) { return Error(404, "evening_not_found"); }
            keepsakes.UnlockEveningProphecies(request.DeviceId, eveningId);
            return Results.Json(snapshot);
        });

        routes.MapPost("/v1/admin/show/evenings/{eveningId:guid}/chronicle-editions", async (string eveningId,
            HttpContext context, ServerAdminAccess admin, EveningMemoryStore store) =>
        {
            NoCache(context);
            if (!admin.Authenticate(context)) { return Error(401, "admin_credential_required"); }
            var request = await ReadCommandAsync(context);
            if (request is null) { return Error(400, "invalid_request"); }
            var snapshot = store.RegenerateChronicle(request.DeviceId, eveningId);
            return snapshot is null ? Error(409, "evening_not_closed") : Results.Json(snapshot);
        });

        routes.MapDelete("/v1/admin/show/chronicle/sources/{sourceId}", async (string sourceId,
            HttpContext context, ServerAdminAccess admin, EveningMemoryStore store) =>
        {
            NoCache(context);
            if (!admin.Authenticate(context)) { return Error(401, "admin_credential_required"); }
            var request = await ReadCommandAsync(context);
            if (request is null || sourceId.Length > 240) { return Error(400, "invalid_request"); }
            store.RevokeShowSource(request.DeviceId, sourceId);
            return Results.Json(new { retention = "manual_only", derivedPublicContent = "recomputed", backupCleanup = "not_confirmed", providerCleanup = "not_requested" });
        });
        return routes;
    }

    private static async Task<CommandRequest?> ReadCommandAsync(HttpContext context)
    {
        try
        {
            var buffer = new byte[2049];
            var used = 0;
            while (used < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(used), context.RequestAborted);
                if (read == 0) { break; }
                used += read;
            }
            if (used > 2048) { return null; }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, used));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().GroupBy(p => p.Name).Any(group => group.Count() > 1)) { return null; }
            var request = document.RootElement.Deserialize<CommandRequest>(Json);
            return request is { DeviceId.Length: > 0 and <= 240 } && !request.DeviceId.Any(char.IsControl) ? request : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? PublicDevice(HttpContext context, IConfiguration config)
    {
        if (!config.GetValue<bool>("Keepsake:Enabled") || !context.Request.IsHttps
            || !Uri.TryCreate(config["Keepsake:Origin"], UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.PathAndQuery != "/" || origin.Fragment.Length != 0
            || origin.UserInfo.Length != 0
            || !string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase)) { return null; }
        var device = config["Keepsake:PublicDeviceId"];
        return !string.IsNullOrWhiteSpace(device)
            && config.GetSection("Devices").GetChildren().Any(section => section["DeviceId"] == device) ? device : null;
    }

    private static void NoCache(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    }

    private static IResult Error(int status, string code) => Results.Json(new { schemaVersion = 1, errorCode = code }, statusCode: status);
    private sealed record CommandRequest([property: JsonRequired] string DeviceId);
}

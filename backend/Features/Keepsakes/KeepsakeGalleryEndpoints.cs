using System.Security.Cryptography;
using Kufr.Backend.Features.Conversations;

namespace Kufr.Backend.Features.Keepsakes;

internal static class KeepsakeGalleryEndpoints
{
    private static readonly SemaphoreSlim Downloads = new(4, 4);

    public static IEndpointRouteBuilder MapKeepsakeGallery(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/guest/v1/evening", (HttpContext context, KeepsakeStore store, EveningMemoryStore evenings, IConfiguration config) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            var device = PublicDevice(context, config);
            if (device is null) { return Error(404, "access_unavailable"); }
            if (context.Request.Query.Keys.Any(key => key is not ("limit" or "cursor"))) { return Error(400, "invalid_request"); }
            var limitText = context.Request.Query["limit"].ToString();
            var limit = limitText.Length == 0 ? 24 : int.TryParse(limitText, out var parsed) ? parsed : 0;
            try { return Results.Json(store.ReadEveningGallery(device, evenings.ReadActiveEveningId(device), limit, context.Request.Query["cursor"].ToString())); }
            catch (KeepsakeException exception) { return Error(exception.Status, exception.Code); }
        }).RequireRateLimiting("keepsake-public");
        endpoints.MapGet("/guest/v1/evenings/{eveningId:guid}/keepsakes/{id:guid}/editions/{editionId:guid}/assets/{assetId:guid}",
            DownloadAsync).RequireRateLimiting("keepsake-public");
        return endpoints;
    }

    private static async Task<IResult> DownloadAsync(HttpContext context, string eveningId, string id,
        string editionId, string assetId, KeepsakeStore store, EveningMemoryStore evenings, IConfiguration config)
    {
        var device = PublicDevice(context, config);
        if (device is null || evenings.ReadActiveEveningId(device) != eveningId) { return Error(404, "access_unavailable"); }
        if (!Downloads.Wait(0)) { return Error(429, "download_backpressure"); }
        try
        {
            var file = store.OpenGalleryAsset(device, eveningId, id, editionId, assetId);
            await using var stream = file.Stream;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            if (Convert.ToHexString(await SHA256.HashDataAsync(stream, deadline.Token)) != file.Asset.Sha256)
            { return Error(503, "asset_integrity_failure"); }
            if (evenings.ReadActiveEveningId(device) != eveningId
                || !store.IsGalleryAssetVisible(device, eveningId, id, editionId, assetId))
            { return Error(404, "access_unavailable"); }
            stream.Position = 0;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.ContentLength = file.Asset.Length;
            var filename = file.Asset.Kind switch
            {
                "export_story" => "story.txt", "export_card" => "keepsake-card.png", "export_zip" => "keepsake.zip", _ => "wand.png"
            };
            await Results.Stream(output => stream.CopyToAsync(output, 64 * 1024, deadline.Token),
                file.Asset.MediaType, filename).ExecuteAsync(context);
            return Results.Empty;
        }
        catch (KeepsakeException exception) { return Error(exception.Status, "access_unavailable"); }
        catch (FileNotFoundException) { return Error(404, "access_unavailable"); }
        catch (DirectoryNotFoundException) { return Error(404, "access_unavailable"); }
        catch (OperationCanceledException) { context.Abort(); return Results.Empty; }
        finally { Downloads.Release(); }
    }

    private static string? PublicDevice(HttpContext context, IConfiguration config)
    {
        if (!config.GetValue<bool>("Keepsake:Enabled") || !context.Request.IsHttps
            || !Uri.TryCreate(config["Keepsake:Origin"], UriKind.Absolute, out var origin)
            || origin.Scheme != "https" || origin.PathAndQuery != "/" || !string.IsNullOrEmpty(origin.Fragment)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.Equals(context.Request.Host.Value, origin.Authority, StringComparison.OrdinalIgnoreCase)) { return null; }
        var device = config["Keepsake:PublicDeviceId"];
        return !string.IsNullOrWhiteSpace(device)
            && config.GetSection("Devices").GetChildren().Any(section => section["DeviceId"] == device) ? device : null;
    }
    private static IResult Error(int status, string code) => Results.Json(new { schemaVersion = 1, errorCode = code }, statusCode: status);
}

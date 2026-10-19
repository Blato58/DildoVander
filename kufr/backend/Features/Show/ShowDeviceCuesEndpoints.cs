using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Show;

internal static class ShowDeviceCuesEndpoints
{
    internal static IEndpointRouteBuilder MapShowDeviceCues(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/v1/show/device-cues", Poll);
        routes.MapGet("/v1/show/device-cues/{cueId:guid}/audio", Audio);
        routes.MapPost("/v1/show/device-cues/{cueId:guid}/ack", AcknowledgeAsync);
        return routes;
    }

    private static IResult Poll(HttpContext context, DeviceAccessStore access, ShowDeviceCueBridge bridge)
    {
        NoCache(context);
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { return ApiProblem.Create(context, 401, "device_access_required"); }
        var query = context.Request.Query;
        var allowed = new[] { "bootId", "generation", "deviceRevision", "deviceTimeMs", "appliedSettingsRevision", "appliedQuiet", "appliedAmbient", "voice", "appliedStopRevision" };
        if (query.Any(q => !allowed.Contains(q.Key) || q.Value.Count != 1)
            || !Guid.TryParseExact(query["bootId"], "D", out var boot) || boot == Guid.Empty
            || !int.TryParse(query["generation"], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !int.TryParse(query["deviceRevision"], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || !long.TryParse(query["deviceTimeMs"], NumberStyles.None, CultureInfo.InvariantCulture, out var time))
        { return ApiProblem.Create(context, 400, "invalid_device_cue_poll"); }
        uint? applied = null;
        bool? quiet = null, ambient = null;
        if (query.ContainsKey("appliedSettingsRevision") || query.ContainsKey("appliedQuiet") || query.ContainsKey("appliedAmbient"))
        {
            if (!uint.TryParse(query["appliedSettingsRevision"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || !bool.TryParse(query["appliedQuiet"], out var parsedQuiet)
                || !bool.TryParse(query["appliedAmbient"], out var parsedAmbient))
            { return ApiProblem.Create(context, 400, "invalid_device_cue_poll"); }
            applied = parsed; quiet = parsedQuiet; ambient = parsedAmbient;
        }
        var voice = query.ContainsKey("voice") ? query["voice"].ToString() : null;
        uint? appliedStop = null;
        if (query.ContainsKey("appliedStopRevision"))
        {
            if (!uint.TryParse(query["appliedStopRevision"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedStop))
            { return ApiProblem.Create(context, 400, "invalid_device_cue_poll"); }
            appliedStop = parsedStop;
        }
        try { return Results.Json(bridge.Poll(device, new(boot, generation, revision, time, applied, quiet, ambient, voice, appliedStop))); }
        catch (ArgumentException) { return ApiProblem.Create(context, 400, "invalid_device_cue_poll"); }
    }

    private static IResult Audio(Guid cueId, HttpContext context, DeviceAccessStore access, ShowDeviceCueBridge bridge)
    {
        NoCache(context);
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { return ApiProblem.Create(context, 401, "device_access_required"); }
        var result = bridge.ReadAudio(device, cueId);
        if (result.Wav is { } wav) { return Results.File(wav, "audio/wav", enableRangeProcessing: false); }
        if (result.Status == 202) { context.Response.Headers.RetryAfter = "1"; }
        return Results.Json(new { code = result.Code }, statusCode: result.Status);
    }

    private static async Task<IResult> AcknowledgeAsync(Guid cueId, HttpContext context,
        DeviceAccessStore access, ShowDeviceCueBridge bridge)
    {
        NoCache(context);
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { return ApiProblem.Create(context, 401, "device_access_required"); }
        try
        {
            var buffer = new byte[2049];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), context.RequestAborted);
                if (read == 0) { break; }
                count += read;
            }
            if (count > 2048) { return ApiProblem.Create(context, 413, "cue_ack_too_large"); }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Select(property => property.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.RootElement.EnumerateObject().Count())
            { return ApiProblem.Create(context, 400, "invalid_cue_ack"); }
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true };
            var request = JsonSerializer.Deserialize<ShowDeviceCueAck>(buffer.AsSpan(0, count), options);
            if (request is null) { return ApiProblem.Create(context, 400, "invalid_cue_ack"); }
            var result = bridge.Acknowledge(device, cueId, request);
            return Results.Json(new { code = result.Code, outcome = result.Outcome }, statusCode: result.Status);
        }
        catch (JsonException) { return ApiProblem.Create(context, 400, "invalid_cue_ack"); }
    }

    private static void NoCache(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }
}

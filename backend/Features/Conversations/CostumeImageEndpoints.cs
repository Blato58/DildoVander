using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Http.Features;

namespace Kufr.Backend.Features.Conversations;

internal static class CostumeImageEndpoints
{
    internal static IEndpointRouteBuilder MapCostumeImages(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/v1/conversations/{id}/image", UploadAsync);
        routes.MapPut("/v1/conversations/{id}/costume-context", CorrectAsync);
        routes.MapDelete("/v1/conversations/{id}/image", (string id, HttpContext context,
            DeviceAccessStore access, ConversationStore store) =>
        {
            var device = DeviceAccessEndpoints.Authenticate(context, access);
            if (device is null) { return ApiProblem.Create(context, 401, "access_token_required"); }
            if (store.IsLabConversation(device, id)) { return ApiProblem.Create(context, 403, "lab_hardware_image_forbidden"); }
            if (!ReadHeaders(context, out _, out var correlation) || correlation?.Revision is null)
            { return ApiProblem.Create(context, 400, "invalid_request"); }
            var result = store.DiscardVisualRevision(device, id, correlation);
            return result.Status == 204 ? Results.NoContent() : ApiProblem.Create(context, result.Status, "image_revision_unavailable");
        });
        routes.MapDelete("/v1/admin/conversations/{id}/media", (string id, string deviceId,
            HttpContext context, ServerAdminAccess admin, ConversationStore store) =>
        {
            if (!admin.Authenticate(context)) { return ApiProblem.Create(context, 401, "admin_credential_required"); }
            if (string.IsNullOrWhiteSpace(deviceId)) { return ApiProblem.Create(context, 400, "invalid_request"); }
            if (store.IsLabConversation(deviceId, id)) { return ApiProblem.Create(context, 403, "lab_hardware_image_forbidden"); }
            store.DeleteVisualContext(deviceId, id);
            return Results.NoContent();
        });
        return routes;
    }

    private static async Task<IResult> UploadAsync(string id, HttpContext context, DeviceAccessStore access, ConversationStore store)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { return ApiProblem.Create(context, 401, "access_token_required"); }
            if (store.IsLabConversation(device, id)) { return ApiProblem.Create(context, 403, "lab_hardware_image_forbidden"); }
        if (!ReadHeaders(context, out var key, out var upload)) { return ApiProblem.Create(context, 400, "invalid_request"); }
        if (context.Request.ContentType != "image/jpeg") { return ApiProblem.Create(context, 415, "jpeg_required"); }
        byte[]? body;
        try { body = await ReadBodyAsync(context, CostumeJpeg.MaximumBytes); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return ApiProblem.Create(context, 408, "image_upload_timeout"); }
        if (body is null) { return ApiProblem.Create(context, 413, "request_too_large"); }
        try
        {
            var result = store.SubmitImage(device, id, key, upload! with { Jpeg = body });
            return result.OperationId is null
                ? ApiProblem.Create(context, result.Status, result.ErrorCode!)
                : Results.Json(new { operationId = result.OperationId, kind = "image", status = "pending" }, statusCode: 202);
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    private static async Task<IResult> CorrectAsync(string id, HttpContext context, DeviceAccessStore access, ConversationStore store)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { return ApiProblem.Create(context, 401, "access_token_required"); }
            if (store.IsLabConversation(device, id)) { return ApiProblem.Create(context, 403, "lab_hardware_image_forbidden"); }
        if (!ReadHeaders(context, out var key, out var correlation)) { return ApiProblem.Create(context, 400, "invalid_request"); }
        byte[]? body;
        try { body = await ReadBodyAsync(context, 2048); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { return ApiProblem.Create(context, 408, "image_upload_timeout"); }
        if (body is null) { return ApiProblem.Create(context, 413, "request_too_large"); }
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1
                || !root.TryGetProperty("description", out var value) || value.ValueKind != JsonValueKind.String
                || !CostumeImageClient.ValidText(value.GetString(), 240))
            { return ApiProblem.Create(context, 400, "invalid_request"); }
            var result = store.CorrectCostume(device, id, key, correlation!, value.GetString()!);
            return result.Operation is null ? ApiProblem.Create(context, result.Status, "costume_context_unavailable")
                : Results.Json(result.Operation, statusCode: result.Status);
        }
        catch (JsonException) { return ApiProblem.Create(context, 400, "invalid_request"); }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    internal static bool ReadHeaders(HttpContext context, out Guid key, out CostumeImageUpload? upload)
    {
        upload = null;
        key = default;
        if (!Guid.TryParseExact(context.Request.Headers["Idempotency-Key"], "D", out key)
            || key == Guid.Empty
            || !Guid.TryParseExact(context.Request.Headers["Client-Turn-Id"], "D", out var turn) || turn == Guid.Empty
            || !Guid.TryParseExact(context.Request.Headers["Boot-Id"], "D", out var boot) || boot == Guid.Empty
            || !int.TryParse(context.Request.Headers["Generation"], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !int.TryParse(context.Request.Headers["Device-Revision"], NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || context.Request.Headers["Catalog-Version"].ToString() is not { Length: > 0 and <= 128 } catalog)
        { return false; }
        var imageId = context.Request.Headers["Image-Id"].ToString();
        var source = context.Request.Headers["Image-Source"].ToString();
        var captured = context.Request.Headers["Captured-At"].ToString();
        var imageRevision = context.Request.Headers["Image-Revision"].ToString();
        if (imageId.Length > 0 && (!Guid.TryParseExact(imageId, "D", out var parsedImage) || parsedImage == Guid.Empty)
            || source.Length > 0 && source is not ("phone" or "browser" or "suitcase")
            || captured.Length > 0 && !DateTimeOffset.TryParse(captured, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out _)
            || imageRevision.Length > 0 && (!int.TryParse(imageRevision, NumberStyles.None,
                CultureInfo.InvariantCulture, out var parsedRevision) || parsedRevision < 1))
        { return false; }
        upload = new(turn, boot, generation, revision, catalog, [])
        {
            ImageId = imageId.Length == 0 ? null : imageId,
            Source = source.Length == 0 ? "phone" : source,
            CapturedAt = captured.Length == 0 ? null : DateTimeOffset.Parse(captured, CultureInfo.InvariantCulture),
            Revision = imageRevision.Length == 0 ? null : int.Parse(imageRevision, CultureInfo.InvariantCulture),
            ExplicitConsent = context.Request.Headers["Image-Consent"] == "true"
        };
        return true;
    }

    internal static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maximum, TimeSpan? timeout = null)
    {
        if (context.Request.ContentLength > maximum) { CloseRejectedBody(context); return null; }
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false }) { feature.MaxRequestBodySize = maximum; }
        using var memory = new MemoryStream(maximum);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(buffer, deadline.Token);
                if (count == 0) { return memory.ToArray(); }
                if (memory.Length + count > maximum) { CloseRejectedBody(context); return null; }
                memory.Write(buffer, 0, count);
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        { CloseRejectedBody(context); return null; }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            if (memory.TryGetBuffer(out var bytes)) { CryptographicOperations.ZeroMemory(bytes.AsSpan()); }
        }
    }

    private static void CloseRejectedBody(HttpContext context)
    {
        // Kestrel may close HTTP/1 while draining an unread oversized body after
        // flushing the 413. Tell pooled clients before they reuse that socket.
        if (context.Request.Protocol is "HTTP/1.0" or "HTTP/1.1")
        {
            context.Response.Headers.Connection = "close";
        }
    }
}

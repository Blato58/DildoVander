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
        return routes;
    }

    private static async Task<IResult> UploadAsync(string id, HttpContext context, DeviceAccessStore access, ConversationStore store)
    {
        var device = DeviceAccessEndpoints.Authenticate(context, access);
        if (device is null) { return ApiProblem.Create(context, 401, "access_token_required"); }
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
        upload = new(turn, boot, generation, revision, catalog, []);
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

using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Keepsakes;

internal static class KeepsakeAdminEndpoints
{
    public static IEndpointRouteBuilder MapKeepsakeAdministration(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/admin/keepsakes", (HttpContext c, KeepsakeStore store) =>
        {
            var limit = int.TryParse(c.Request.Query["limit"], out var parsed) ? parsed : 100;
            var after = c.Request.Query["afterId"].ToString();
            var items = store.AdminList(limit, after.Length == 0 ? null : after);
            return Results.Json(new { schemaVersion = 1, items, nextCursor = items.Count == limit ? items[^1].Id : null });
        }).AddEndpointFilter(GuardAsync);
        app.MapPost("/v1/admin/keepsake-deletions/preview", async (HttpContext c, KeepsakeStore store) =>
        {
            var request = await BodyAsync<PreviewRequest>(c);
            if (request.Scope is not ("all" or "portrait")) { throw new KeepsakeException("invalid_scope"); }
            var preview = store.PreviewAdminDeletion(request.TargetIds,
                request.Scope == "all" ? KeepsakeDeleteScope.All : KeepsakeDeleteScope.PhotosAndPortraits);
            return Results.Json(new { schemaVersion = 1, preview }, statusCode: 201);
        }).AddEndpointFilter(GuardAsync);
        app.MapPost("/v1/admin/keepsake-deletions/{previewId:guid}/confirm", async (HttpContext c, string previewId, KeepsakeStore store) =>
        {
            var request = await BodyAsync<ConfirmRequest>(c);
            var batch = store.ConfirmAdminDeletion(previewId, request.Confirm, Key(c), request.PreviewDigest);
            return Results.Json(new { schemaVersion = 1, batch }, statusCode: 202);
        }).AddEndpointFilter(GuardAsync);
        app.MapGet("/v1/admin/keepsake-deletions/batches/{batchId:guid}", (string batchId, KeepsakeStore store) =>
            Results.Json(new { schemaVersion = 1, batch = store.AdminGetBatchReceipt(batchId) })).AddEndpointFilter(GuardAsync);
        app.MapGet("/v1/admin/keepsake-deletions/receipts/{receiptId:guid}", (string receiptId, KeepsakeStore store) =>
            Results.Json(new { schemaVersion = 1, receipt = store.AdminGetDeletionReceipt(receiptId) })).AddEndpointFilter(GuardAsync);
        app.MapPost("/v1/admin/keepsakes/{id:guid}/jobs/{jobId:guid}/cancel", async (HttpContext c, string id, string jobId, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(c); store.AdminCancel(id, jobId, Key(c)); return Results.NoContent();
        }).AddEndpointFilter(GuardAsync);
        app.MapPost("/v1/admin/keepsakes/{id:guid}/revoke-shares", async (HttpContext c, string id, KeepsakeStore store) =>
        {
            await BodyAsync<EmptyRequest>(c); store.AdminRevokeShares(id, Key(c)); return Results.NoContent();
        }).AddEndpointFilter(GuardAsync);
        return app;
    }

    private static async ValueTask<object?> GuardAsync(EndpointFilterInvocationContext invocation, EndpointFilterDelegate next)
    {
        var c = invocation.HttpContext;
        if (!c.RequestServices.GetRequiredService<ServerAdminAccess>().Authenticate(c)) { return ApiProblem.Create(c, 401, "admin_credential_required"); }
        if (c.Request.Method != "GET" && !Guid.TryParseExact(c.Request.Headers["Idempotency-Key"], "D", out _))
        { return ApiProblem.Create(c, 400, "invalid_idempotency_key"); }
        try { return await next(invocation); }
        catch (KeepsakeException e) { return ApiProblem.Create(c, e.Status, e.Code); }
        catch (JsonException) { return ApiProblem.Create(c, 400, "invalid_request"); }
    }
    private static string Key(HttpContext c) => c.Request.Headers["Idempotency-Key"].ToString();
    private static async Task<T> BodyAsync<T>(HttpContext c) where T : VersionedRequest
    {
        if (!c.Request.HasJsonContentType()) { throw new KeepsakeException("json_required", 415); }
        if (c.Request.ContentLength is > 4096) { throw new KeepsakeException("request_too_large", 413); }
        var request = await c.Request.ReadFromJsonAsync<T>(new JsonSerializerOptions(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true }, c.RequestAborted);
        if (request is null || request.SchemaVersion != 1) { throw new KeepsakeException("invalid_request"); }
        return request;
    }
    private record VersionedRequest(int SchemaVersion);
    private sealed record EmptyRequest(int SchemaVersion) : VersionedRequest(SchemaVersion);
    private sealed record PreviewRequest(int SchemaVersion, string[] TargetIds, string Scope) : VersionedRequest(SchemaVersion);
    private sealed record ConfirmRequest(int SchemaVersion, string Confirm, string PreviewDigest) : VersionedRequest(SchemaVersion);
}

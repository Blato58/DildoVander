using System.Text.Json;
using System.Text.Json.Serialization;
using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;

namespace Kufr.Backend.Features.Keepsakes;

internal static class CustomerCompletionEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    // The application owner maps this helper with the configured archive store.
    // It uses the existing account session, origin and CSRF protections.
    internal static void MapCustomerCompletionUpdates(this IEndpointRouteBuilder app)
    {
        if (!app.ServiceProvider.GetRequiredService<AccountConfiguration>().Enabled
            || !app.ServiceProvider.GetRequiredService<IServiceProviderIsService>().IsService(typeof(KeepsakeStore)))
        {
            return;
        }
        var group = app.MapGroup("/app/v1/customer/completion-updates")
            .RequireAuthorization().RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            http.Response.Headers.CacheControl = "no-store";
            try { return await next(invocation); }
            catch (KeepsakeException error)
            {
                var code = error.Status is 403 or 404 ? "completion_update_unavailable" : error.Code;
                return ApiProblem.Create(http, error.Status == 403 ? 404 : error.Status, code);
            }
        });
        group.MapGet("", (HttpContext http, KeepsakeStore store, int? limit, string? cursor) =>
            Results.Json(store.ReadCompletionUpdates(AccountOwnedArchives.Actor(http), limit ?? 25, cursor)));
        group.MapPut("/{id:guid}/read", async (string id, HttpContext http, KeepsakeStore store) =>
        {
            await ReadRequestAsync(http);
            store.MarkCompletionRead(AccountOwnedArchives.Actor(http), id);
            return Results.NoContent();
        });
    }

    private static async Task ReadRequestAsync(HttpContext http)
    {
        if (!http.Request.HasJsonContentType())
        {
            throw new KeepsakeException("invalid_completion_request");
        }
        var bytes = new byte[257];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(count), http.RequestAborted);
            if (read == 0) { break; }
            count += read;
        }
        if (count > 256) { throw new KeepsakeException("invalid_completion_request", 413); }
        try
        {
            var request = JsonSerializer.Deserialize<CustomerCompletionReadRequest>(bytes.AsSpan(0, count), Json);
            if (request?.SchemaVersion != 1) { throw new KeepsakeException("invalid_completion_request"); }
        }
        catch (JsonException) { throw new KeepsakeException("invalid_completion_request"); }
    }
}

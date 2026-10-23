using Kufr.Backend.Features.Access;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Memory;

namespace Kufr.Backend.Features.Tonight;

internal static class TonightEndpoints
{
    internal static void MapTonightEndpoints(this WebApplication app)
    {
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled)
        {
            return;
        }

        var routes = app.MapGroup("/app/v1/tonight")
            .RequireAuthorization()
            .RequireRateLimiting("account");
        routes.MapGet("/devices/{deviceId}/memory-status", ReadMemoryStatusAsync);
    }

    private static async Task<IResult> ReadMemoryStatusAsync(HttpContext context, string deviceId,
        HybridMemoryService memory)
    {
        // This route has account authority only, even when a legacy service
        // credential is configured for separate administrative routes.
        if (context.Request.Headers.ContainsKey("Authorization")
            || !context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim))
        {
            return ApiProblem.Create(context, 401, "account_required");
        }
        if (!await AccountResourceAuthorization.HasCurrentDevicePermissionAsync(context, deviceId))
        {
            return ApiProblem.Create(context, 404, "device_unavailable");
        }

        // Device permission admits this read; capacity still describes the
        // shared archive, not the selected device's guest records.
        return Results.Ok(TonightMemoryStatus.From(memory.Status));
    }
}

internal sealed record TonightMemoryStatus(long StoredBytes, long CapacityBytes, string StorageStatus,
    long RejectedWrites, bool StorageIncomplete, string? LastStorageErrorCode, DateTimeOffset? LastStorageErrorAt)
{
    internal static TonightMemoryStatus From(MemoryProviderStatus status)
    {
        var knownCapacityError = status.LastStorageErrorCode == "archive_capacity";
        return new(status.StoredBytes, status.CapacityBytes, status.StorageStatus, status.RejectedWrites,
            status.StorageIncomplete, knownCapacityError ? "archive_capacity" : null,
            knownCapacityError ? status.LastStorageErrorAt : null);
    }
}

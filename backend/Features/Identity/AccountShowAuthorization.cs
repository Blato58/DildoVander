using Kufr.Backend.Features.Access;

namespace Kufr.Backend.Features.Identity;

// Show operator authority is deliberately separate from firmware cue delivery.
internal static class AccountShowAuthorization
{
    public static bool IsAccount(HttpContext context) => !context.Request.Headers.ContainsKey("Authorization")
        && context.User.HasClaim(claim => claim.Type == AccountIdentity.SessionClaim);

    public static async Task<IResult?> RequireAsync(HttpContext context, string deviceId, string? requiredPermission = null)
    {
        if (!IsAccount(context))
        {
            return context.RequestServices.GetRequiredService<ServerAdminAccess>().Authenticate(context)
                ? null : ApiProblem.Create(context, 401, "server_admin_required");
        }
        if (context.Request.Headers["X-DV-Device-Id"] != deviceId)
        { return ApiProblem.Create(context, 403, "device_scope_required"); }
        var config = context.RequestServices.GetRequiredService<IConfiguration>();
        if (!config.GetSection("Devices").GetChildren().Any(device => device["DeviceId"] == deviceId))
        { return ApiProblem.Create(context, 403, "device_scope_required"); }
        var path = (context.Request.Path.Value ?? "").TrimEnd('/');
        var grantManagement = path.EndsWith("/grants", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/grants/revoke", StringComparison.OrdinalIgnoreCase);
        var permission = requiredPermission ?? (context.Request.Method == "GET" ? "read" : grantManagement ? "manage" : "operate");
        if (!await AccountResourceAuthorization.HasPermissionAsync(context, "device", deviceId, permission))
        { return ApiProblem.Create(context, 403, "resource_access_denied"); }
        var sensitive = context.Request.Method != "GET" && (grantManagement
            || path.EndsWith("/reveal", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/end-evening", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/offline-outcomes", StringComparison.OrdinalIgnoreCase));
        if (sensitive)
        {
            var recent = await AccountAuthorization.RequireRecentAsync(context);
            if (recent is not null) { return recent; }
            if (context.Request.Headers["X-DV-Confirm"] != "yes")
            { return ApiProblem.Create(context, 409, "confirmation_required"); }
        }
        return null;
    }
}

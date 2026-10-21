using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Kufr.Backend.Features.Identity;

internal static class AccountResourceAuthorization
{
    // Cookie authentication runs once at upgrade. Recheck database authority on
    // each persistent input frame instead of trusting that original principal.
    public static async Task<bool> HasCurrentDevicePermissionAsync(HttpContext context, string deviceId)
    {
        var sessionId = context.User.FindFirstValue(AccountIdentity.SessionClaim);
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (sessionId is null || userId is null) { return false; }
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        if (!configuration.GetSection("Devices").GetChildren().Any(x => x["DeviceId"] == deviceId)) { return false; }
        await using var scope = context.RequestServices.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        if (!await db.Sessions.AsNoTracking().AnyAsync(x => x.Id == sessionId && x.UserId == userId && x.ExpiresAt > AccountEndpoints.Now())) { return false; }
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && !x.Disabled);
        var stampClaim = scope.ServiceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        if (user is null || user.SecurityStamp != context.User.FindFirstValue(stampClaim)) { return false; }
        var roles = await (from membership in db.UserRoles
                           join role in db.Roles on membership.RoleId equals role.Id
                           where membership.UserId == userId
                           select role.Name).ToArrayAsync();
        if (roles.Contains(AccountRoles.Owner) || roles.Contains(AccountRoles.Administrator)) { return true; }
        return roles.Contains(AccountRoles.Operator) && await db.ResourceGrants.AsNoTracking().AnyAsync(x => x.UserId == userId
            && x.ResourceType == "device" && x.ResourceId == deviceId && (x.Permission == "operate" || x.Permission == "manage"));
    }

    public static async Task<bool> HasPermissionAsync(HttpContext context, string resourceType, string resourceId, string permission)
    {
        if (!context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim)) { return false; }
        if (!AccountRoles.IsStaff(context.User)) { return false; }
        if (AccountRoles.IsAdmin(context.User)) { return true; }
        if (permission != "read" && !context.User.IsInRole(AccountRoles.Operator)) { return false; }
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = context.RequestServices.GetRequiredService<AccountDatabase>();
        return await db.ResourceGrants.AnyAsync(x => x.UserId == userId && x.ResourceType == resourceType && x.ResourceId == resourceId
            && (x.Permission == permission || x.Permission == "manage" || (permission == "read" && x.Permission == "operate")));
    }
}

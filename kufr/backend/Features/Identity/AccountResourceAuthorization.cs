using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal static class AccountResourceAuthorization
{
    public static async Task<bool> HasPermissionAsync(HttpContext context, string resourceType, string resourceId, string permission)
    {
        if (!context.User.HasClaim(c => c.Type == AccountIdentity.SessionClaim)) { return false; }
        if (AccountRoles.IsAdmin(context.User)) { return true; }
        if (permission != "read" && !context.User.IsInRole(AccountRoles.Operator)) { return false; }
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = context.RequestServices.GetRequiredService<AccountDatabase>();
        return await db.ResourceGrants.AnyAsync(x => x.UserId == userId && x.ResourceType == resourceType && x.ResourceId == resourceId
            && (x.Permission == permission || x.Permission == "manage" || (permission == "read" && x.Permission == "operate")));
    }
}

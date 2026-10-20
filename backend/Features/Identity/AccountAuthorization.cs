using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal static class AccountAuthorization
{
    public static async Task<IResult?> RequireRecentAsync(HttpContext context)
    {
        var sid = context.User.FindFirstValue(AccountIdentity.SessionClaim);
        if (sid is null) { return ApiProblem.Create(context, 401, "account_required"); }
        var session = await context.RequestServices.GetRequiredService<AccountDatabase>().Sessions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == sid);
        var now = AccountEndpoints.Now();
        return session is not null && session.ExpiresAt > now && session.AuthenticatedAt <= now && session.AuthenticatedAt >= now - 600
            ? null : ApiProblem.Create(context, 403, "recent_passkey_required");
    }
}

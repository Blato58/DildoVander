using System.Security.Claims;
using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal static partial class AccountEndpoints
{
    private static async Task<IResult> AdminUsersAsync(HttpContext context, AccountDatabase db, UserManager<AccountUser> users)
    {
        if (!AccountRoles.IsAdmin(context.User)) { return ApiProblem.Create(context, 403, "account_admin_required"); }
        var result = new List<object>();
        foreach (var user in await db.Users.OrderBy(x => x.Id).Take(500).ToArrayAsync())
        {
            result.Add(new { user.Id, user.DisplayName, user.Disabled, user.CreatedAt, roles = await users.GetRolesAsync(user) });
        }
        return Results.Ok(new
        {
            users = result,
            grants = await db.ResourceGrants.OrderBy(x => x.Id).Take(1000).ToArrayAsync(),
            invitations = await db.Invitations.Where(x => x.Purpose != "bootstrap" && !x.Consumed && x.ExpiresAt > Now())
                .Select(x => new { x.Purpose, x.Role, x.UserId, x.ExpiresAt }).Take(100).ToArrayAsync()
        });
    }

    private static async Task<IResult> InviteAsync(InvitationRequest request, HttpContext context,
        AccountDatabase db, UserManager<AccountUser> users)
    {
        // Only an owner can issue credentials or recovery. Administrators manage
        // resources, but cannot manufacture a second owner or take over accounts.
        if (!context.User.IsInRole(AccountRoles.Owner)) { return ApiProblem.Create(context, 403, "owner_required"); }
        if (!AccountRoles.All.Contains(request.Role)) { return Error(context, "invalid_role"); }
        if (request.RecoveryUserId is not null && await users.FindByIdAsync(request.RecoveryUserId) is not { Disabled: false })
        {
            return Error(context, "recovery_unavailable");
        }
        if (await db.Invitations.CountAsync(x => !x.Consumed && x.ExpiresAt > Now()) >= 100)
        {
            return ApiProblem.Create(context, 429, "invitation_limit");
        }
        var token = Secret();
        var invitation = new AccountInvitation
        {
            TokenHash = Hash(token), Purpose = request.RecoveryUserId is null ? "invite" : "recovery",
            Role = request.Role, UserId = request.RecoveryUserId, ExpiresAt = Now() + 3600
        };
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync();
        return Results.Ok(new { token, invitation.ExpiresAt, invitation.Purpose });
    }

    private static async Task<IResult> ChangeUserAsync(string id, UserChange request, HttpContext context,
        AccountDatabase db, UserManager<AccountUser> users)
    {
        if (!context.User.IsInRole(AccountRoles.Owner)) { return ApiProblem.Create(context, 403, "owner_required"); }
        if (!AccountRoles.All.Contains(request.Role)) { return Error(context, "invalid_role"); }
        return await ChangeUserCoreAsync(id, request, context, db, users);
    }

    internal static async Task<IResult> ChangeUserCoreAsync(string id, UserChange request, HttpContext context,
        AccountDatabase db, UserManager<AccountUser> users)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Acquire SQLite's write lock before counting owners. Independent requests
        // cannot both observe two owners and remove the last active owner.
        await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET Disabled = Disabled WHERE Id = {0}", id);
        var user = await users.FindByIdAsync(id);
        if (user is null) { return ApiProblem.Create(context, 404, "account_not_found"); }
        var roles = await users.GetRolesAsync(user);
        if (roles.Contains(AccountRoles.Owner) && (!user.Disabled && (request.Disabled || request.Role != AccountRoles.Owner)))
        {
            var owners = await (from u in db.Users
                                join ur in db.UserRoles on u.Id equals ur.UserId
                                join role in db.Roles on ur.RoleId equals role.Id
                                where role.Name == AccountRoles.Owner && !u.Disabled
                                select u.Id).CountAsync();
            if (owners <= 1) { return ApiProblem.Create(context, 409, "last_owner_required"); }
        }
        if (!(await users.RemoveFromRolesAsync(user, roles)).Succeeded
            || !(await users.AddToRoleAsync(user, request.Role)).Succeeded)
        {
            return Error(context, "role_change_failed");
        }
        user.Disabled = request.Disabled;
        if (!(await users.UpdateAsync(user)).Succeeded) { return Error(context, "account_change_failed"); }
        await db.Sessions.Where(x => x.UserId == id).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> CreateGrantAsync(GrantRequest request, HttpContext context,
        AccountDatabase db, UserManager<AccountUser> users)
    {
        if (!AccountRoles.IsAdmin(context.User)) { return ApiProblem.Create(context, 403, "account_admin_required"); }
        if (request.ResourceType is not ("device" or "workshop")
            || request.Permission is not ("read" or "operate" or "manage")
            || string.IsNullOrWhiteSpace(request.ResourceId) || request.ResourceId.Length > 128
            || await users.FindByIdAsync(request.UserId) is not { Disabled: false })
        {
            return Error(context, "invalid_resource_grant");
        }
        if (await db.ResourceGrants.CountAsync(x => x.UserId == request.UserId) >= 100)
        {
            return Error(context, "resource_grant_limit");
        }
        if (await db.ResourceGrants.AnyAsync(x => x.UserId == request.UserId && x.ResourceType == request.ResourceType
            && x.ResourceId == request.ResourceId && x.Permission == request.Permission))
        {
            return ApiProblem.Create(context, 409, "resource_grant_exists");
        }
        var grant = new AccountResourceGrant
        {
            Id = Guid.NewGuid().ToString("N"), UserId = request.UserId,
            ResourceType = request.ResourceType, ResourceId = request.ResourceId, Permission = request.Permission
        };
        db.ResourceGrants.Add(grant);
        await db.SaveChangesAsync();
        return Results.Ok(grant);
    }

    private static async Task<IResult> RevokeGrantAsync(string id, HttpContext context, AccountDatabase db)
    {
        if (!AccountRoles.IsAdmin(context.User)) { return ApiProblem.Create(context, 403, "account_admin_required"); }
        var deleted = await db.ResourceGrants.Where(x => x.Id == id).ExecuteDeleteAsync();
        return deleted == 1 ? Results.NoContent() : ApiProblem.Create(context, 404, "resource_grant_not_found");
    }
}

using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal sealed record CustomerEnrollmentRequest(string DisplayName);

internal static partial class AccountEndpoints
{
    private static async Task<bool> CustomerRegistrationAvailableAsync(AccountDatabase db) =>
        await (from membership in db.UserRoles
               join role in db.Roles on membership.RoleId equals role.Id
               join user in db.Users on membership.UserId equals user.Id
               where role.Name == AccountRoles.Owner && !user.Disabled
               select user.Id).AnyAsync();

    private static async Task<IResult> CustomerEnrollmentOptionsAsync(CustomerEnrollmentRequest request,
        HttpContext context, SignInManager<AccountUser> signIn, AccountDatabase db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblem.Create(context, 409, "account_already_signed_in");
        }
        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 80
            || request.DisplayName.Any(char.IsControl))
        {
            return Error(context, "invalid_enrollment");
        }
        // Public enrollment cannot close the existing first-owner bootstrap.
        if (!await CustomerRegistrationAvailableAsync(db))
        {
            return ApiProblem.Create(context, 503, "customer_registration_unavailable");
        }
        if (await db.Ceremonies.CountAsync(ceremony => ceremony.Kind == "customer-enroll"
            && !ceremony.Consumed && ceremony.ExpiresAt > Now()) >= 1000)
        {
            return ApiProblem.Create(context, 429, "customer_enrollment_limit");
        }
        var id = Guid.NewGuid().ToString("N");
        var name = request.DisplayName.Trim();
        var label = PasskeyLabels.Account(name, [AccountRoles.Customer]);
        var options = await signIn.MakePasskeyCreationOptionsAsync(new() { Id = id, Name = label, DisplayName = label });
        await SaveCeremonyAsync(context, db, "customer-enroll", id, name, null);
        return Results.Content(options, "application/json");
    }

    private static async Task<IResult> CompleteCustomerEnrollmentAsync(PasskeyResponse request,
        HttpContext context, SignInManager<AccountUser> signIn, UserManager<AccountUser> users, AccountDatabase db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            return ApiProblem.Create(context, 409, "account_already_signed_in");
        }
        if (request.Credential.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            return Error(context, "invalid_account_request");
        }
        var ceremony = await ConsumeCeremonyAsync(context, db, "customer-enroll");
        if (ceremony is null) { return Error(context, "ceremony_expired_or_used"); }
        var attestation = await signIn.PerformPasskeyAttestationAsync(request.Credential.GetRawText());
        if (!attestation.Succeeded || attestation.UserEntity.Id != ceremony.UserId)
        {
            return Error(context, "passkey_not_verified");
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await CustomerRegistrationAvailableAsync(db))
        {
            return ApiProblem.Create(context, 503, "customer_registration_unavailable");
        }
        var user = new AccountUser
        {
            Id = ceremony.UserId, UserName = ceremony.UserId,
            DisplayName = ceremony.DisplayName, CreatedAt = Now()
        };
        // Role is assigned exclusively here, never read from a request or attestation.
        if (!(await users.CreateAsync(user)).Succeeded
            || !(await users.AddToRoleAsync(user, AccountRoles.Customer)).Succeeded)
        {
            return Error(context, "account_not_created");
        }
        PasskeyLabels.NameNew(attestation.Passkey, PasskeyLabels.Account(user.DisplayName, [AccountRoles.Customer]));
        if (!(await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey)).Succeeded)
        {
            return Error(context, "passkey_not_saved");
        }
        await transaction.CommitAsync();
        await CreateSessionAsync(context, user, signIn, db);
        return Results.NoContent();
    }
}

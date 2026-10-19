using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kufr.Backend.Features.Identity;

internal sealed record EnrollmentRequest(string Token, string DisplayName);
internal sealed record PasskeyResponse(JsonElement Credential);
internal sealed record InvitationRequest(string Role, string? RecoveryUserId);
internal sealed record UserChange(string Role, bool Disabled);
internal sealed record GrantRequest(string UserId, string ResourceType, string ResourceId, string Permission);
internal sealed record InstallationChange(string Name, bool Trusted);
internal sealed record PasskeyNameChange(string Name);

internal static partial class AccountEndpoints
{
    public static void MapAccountEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/app/v1").RequireRateLimiting("account");
        group.AddEndpointFilter(async (invocation, next) =>
        {
            var http = invocation.HttpContext;
            if (http.Request.Path.StartsWithSegments("/app/v1/admin") && http.Request.Method is not ("GET" or "HEAD"))
            {
                if (http.User.Identity?.IsAuthenticated != true) { return ApiProblem.Create(http, 401, "account_required"); }
                var recent = await AccountAuthorization.RequireRecentAsync(http);
                if (recent is not null) { return recent; }
                if (http.Request.Headers["X-DV-Confirm"] != "yes") { return ApiProblem.Create(http, 409, "confirmation_required"); }
            }
            if (http.Request.Path.StartsWithSegments("/app/v1/installations") && http.Request.Method == "PUT"
                || http.Request.Path.StartsWithSegments("/app/v1/passkeys") && (http.Request.Method is "DELETE" or "PUT"
                    || http.Request.Path.Value is "/app/v1/passkeys/options" or "/app/v1/passkeys/complete"))
            {
                var recent = await AccountAuthorization.RequireRecentAsync(http);
                if (recent is not null) { return recent; }
                if (http.Request.Method == "DELETE" && http.Request.Headers["X-DV-Confirm"] != "yes")
                { return ApiProblem.Create(http, 409, "confirmation_required"); }
            }
            try { return await next(invocation); }
            catch (JsonException) { return Error(invocation.HttpContext, "invalid_account_request"); }
        });
        group.MapGet("/session", SessionAsync);
        if (!app.Services.GetRequiredService<AccountConfiguration>().Enabled)
        {
            group.Map("/{**path}", (HttpContext context) => ApiProblem.Create(context, 503, "accounts_not_configured"));
            return;
        }
        app.MapMethods("/.well-known/apple-app-site-association", ["GET", "HEAD"], (IConfiguration configuration) =>
        {
            var id = configuration["ApplicationIdentity:AppleAppId"];
            return id is not null && System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Z0-9]{10}\\.[A-Za-z0-9.-]+$")
                ? Results.Json(new { webcredentials = new { apps = new[] { id } } })
                : Results.NotFound();
        });
        group.MapDelete("/session", SignOutAsync).RequireAuthorization();
        group.MapPost("/passkeys/request-options", RequestOptionsAsync);
        group.MapPost("/passkeys/sign-in", SignInAsync);
        group.MapPost("/passkeys/reauthentication-options", ReauthenticationOptionsAsync).RequireAuthorization();
        group.MapPost("/passkeys/reauthenticate", ReauthenticateAsync).RequireAuthorization();
        group.MapPost("/enrollment/options", EnrollmentOptionsAsync);
        group.MapPost("/enrollment/complete", CompleteEnrollmentAsync);
        group.MapPost("/passkeys/options", AdditionalPasskeyOptionsAsync).RequireAuthorization();
        group.MapPost("/passkeys/complete", CompleteAdditionalPasskeyAsync).RequireAuthorization();
        group.MapDelete("/passkeys/{credentialId}", DeletePasskeyAsync).RequireAuthorization();
        group.MapPut("/passkeys/{credentialId}", RenamePasskeyAsync).RequireAuthorization();
        group.MapGet("/account", AccountDetailsAsync).RequireAuthorization();
        group.MapDelete("/sessions/{id}", RevokeSessionAsync).RequireAuthorization();
        group.MapPut("/installations/{id}", UpdateInstallationAsync).RequireAuthorization();
        group.MapPost("/devices/{id}/association", AssociateDeviceAsync).RequireAuthorization();
        group.MapDelete("/devices/{id}/association", RemoveDeviceAssociationAsync).RequireAuthorization();
        group.MapGet("/admin/users", AdminUsersAsync).RequireAuthorization();
        group.MapPut("/admin/users/{id}", ChangeUserAsync).RequireAuthorization();
        group.MapPost("/admin/invitations", InviteAsync).RequireAuthorization();
        group.MapPost("/admin/grants", CreateGrantAsync).RequireAuthorization();
        group.MapDelete("/admin/grants/{id}", RevokeGrantAsync).RequireAuthorization();
        group.MapAccountOperations();
        if (app.Services.GetService<Kufr.Backend.Features.Keepsakes.KeepsakeStore>() is not null)
        {
            app.MapAccountArchives();
        }
    }

    private static async Task<IResult> SessionAsync(HttpContext context, AccountConfiguration config, IAntiforgery csrf)
    {
        if (!config.Enabled) { return Results.Ok(new { enabled = false, account = (object?)null, csrfToken = (string?)null }); }
        AccountUser? user = null;
        if (context.User.Identity?.IsAuthenticated == true)
        {
            user = await context.RequestServices.GetRequiredService<UserManager<AccountUser>>().GetUserAsync(context.User);
        }
        var roles = user is null ? [] : await context.RequestServices.GetRequiredService<UserManager<AccountUser>>().GetRolesAsync(user);
        return Results.Ok(new
        {
            enabled = true,
            origin = config.AllowedOrigins[0],
            account = user is null ? null : new { user.Id, user.DisplayName, roles },
            csrfToken = csrf.GetAndStoreTokens(context).RequestToken
        });
    }

    private static async Task<IResult> RequestOptionsAsync(HttpContext context, SignInManager<AccountUser> signIn, AccountDatabase db)
    {
        var options = await signIn.MakePasskeyRequestOptionsAsync(null);
        await SaveCeremonyAsync(context, db, "signin", "", "", null);
        return Results.Content(options, "application/json");
    }

    private static async Task<IResult> SignInAsync(PasskeyResponse request, HttpContext context,
        SignInManager<AccountUser> signIn, AccountDatabase db)
    {
        if (request.Credential.ValueKind != JsonValueKind.Object) { return Error(context, "invalid_account_request"); }
        if (await ConsumeCeremonyAsync(context, db, "signin") is null)
        {
            return Error(context, "ceremony_expired_or_used");
        }
        var assertion = await signIn.PerformPasskeyAssertionAsync(request.Credential.GetRawText());
        if (!assertion.Succeeded || assertion.User.Disabled)
        {
            return ApiProblem.Create(context, 401, "passkey_not_verified");
        }
        var users = context.RequestServices.GetRequiredService<UserManager<AccountUser>>();
        var result = await users.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey);
        if (!result.Succeeded) { return Error(context, "passkey_not_saved"); }
        await CreateSessionAsync(context, assertion.User, signIn, db);
        return Results.NoContent();
    }

    private static async Task<IResult> EnrollmentOptionsAsync(EnrollmentRequest request, HttpContext context,
        SignInManager<AccountUser> signIn, AccountDatabase db, IConfiguration configuration)
    {
        if (request.Token is not { Length: >= 20 and <= 256 })
        {
            return Error(context, "invalid_enrollment");
        }
        var hash = Hash(request.Token);
        var invitation = await db.Invitations.FindAsync(hash);
        if (invitation is null && BootstrapMatches(configuration, hash)
            && !await db.Users.AnyAsync())
        {
            invitation = new AccountInvitation
            {
                TokenHash = hash, Purpose = "bootstrap", Role = AccountRoles.Owner,
                ExpiresAt = DateTimeOffset.Parse(configuration["ApplicationIdentity:Bootstrap:ExpiresAt"]!).ToUnixTimeSeconds()
            };
            db.Invitations.Add(invitation);
            await db.SaveChangesAsync();
        }
        if (invitation is null || invitation.Consumed || invitation.ExpiresAt <= Now()
            || (invitation.Purpose == "bootstrap" && !BootstrapMatches(configuration, hash))
            || (invitation.Purpose is "bootstrap" or "bootstrap-local" && await db.Users.AnyAsync()))
        {
            return Error(context, "invitation_expired_or_used");
        }
        var id = invitation.UserId ?? Guid.NewGuid().ToString("N");
        var recoveryUser = invitation.UserId is null ? null : await db.Users.SingleOrDefaultAsync(x => x.Id == id && !x.Disabled);
        if (invitation.UserId is not null && recoveryUser is null)
        {
            return Error(context, "recovery_unavailable");
        }
        if (recoveryUser is null && (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Length > 80))
        { return Error(context, "invalid_enrollment"); }
        var name = recoveryUser?.DisplayName ?? request.DisplayName.Trim();
        var options = await signIn.MakePasskeyCreationOptionsAsync(new() { Id = id, Name = id, DisplayName = name });
        await SaveCeremonyAsync(context, db, "enroll", id, name, hash);
        return Results.Content(options, "application/json");
    }

    private static async Task<IResult> ReauthenticationOptionsAsync(HttpContext context, SignInManager<AccountUser> signIn,
        UserManager<AccountUser> users, AccountDatabase db)
    {
        var user = (await users.GetUserAsync(context.User))!;
        var options = await signIn.MakePasskeyRequestOptionsAsync(user);
        await SaveCeremonyAsync(context, db, "reauth", user.Id, user.DisplayName, null);
        return Results.Content(options, "application/json");
    }

    private static async Task<IResult> ReauthenticateAsync(PasskeyResponse request, HttpContext context,
        SignInManager<AccountUser> signIn, UserManager<AccountUser> users, AccountDatabase db)
    {
        var ceremony = await ConsumeCeremonyAsync(context, db, "reauth");
        var userId = users.GetUserId(context.User);
        if (ceremony is null || ceremony.UserId != userId || request.Credential.ValueKind != JsonValueKind.Object)
        { return Error(context, "ceremony_expired_or_used"); }
        var assertion = await signIn.PerformPasskeyAssertionAsync(request.Credential.GetRawText());
        if (!assertion.Succeeded || assertion.User.Disabled || assertion.User.Id != userId)
        { return ApiProblem.Create(context, 403, "same_account_passkey_required"); }
        if (!(await users.AddOrUpdatePasskeyAsync(assertion.User, assertion.Passkey)).Succeeded)
        { return Error(context, "passkey_not_saved"); }
        var sid = context.User.FindFirstValue(AccountIdentity.SessionClaim);
        await db.Sessions.Where(x => x.Id == sid && x.UserId == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, Now()));
        return Results.NoContent();
    }

    private static async Task<IResult> CompleteEnrollmentAsync(PasskeyResponse request, HttpContext context,
        SignInManager<AccountUser> signIn, UserManager<AccountUser> users, AccountDatabase db, IConfiguration configuration)
    {
        if (request.Credential.ValueKind != JsonValueKind.Object) { return Error(context, "invalid_account_request"); }
        var ceremony = await ConsumeCeremonyAsync(context, db, "enroll");
        if (ceremony is null) { return Error(context, "ceremony_expired_or_used"); }
        var attestation = await signIn.PerformPasskeyAttestationAsync(request.Credential.GetRawText());
        if (!attestation.Succeeded || attestation.UserEntity.Id != ceremony.UserId)
        {
            return Error(context, "passkey_not_verified");
        }
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Conditional consumption and account creation share one SQLite transaction.
        // Concurrent completion can never consume the same invitation twice.
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == ceremony.InvitationHash);
        if (invitation is null || (invitation.Purpose == "bootstrap" && !BootstrapMatches(configuration, invitation.TokenHash))
            || (invitation.Purpose is "bootstrap" or "bootstrap-local" && await db.Users.AnyAsync())
            || await db.Invitations.Where(x => x.TokenHash == ceremony.InvitationHash && !x.Consumed && x.ExpiresAt > Now())
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.Consumed, true)) != 1)
        {
            return Error(context, "invitation_expired_or_used");
        }
        var user = invitation.UserId is null ? new AccountUser
        {
            Id = ceremony.UserId, UserName = ceremony.UserId, DisplayName = ceremony.DisplayName, CreatedAt = Now()
        } : await users.FindByIdAsync(invitation.UserId);
        if (user is null || user.Disabled) { return Error(context, "recovery_unavailable"); }
        if (invitation.UserId is null)
        {
            if (!(await users.CreateAsync(user)).Succeeded || !(await users.AddToRoleAsync(user, invitation.Role)).Succeeded)
            {
                return Error(context, "account_not_created");
            }
        }
        else
        {
            foreach (var passkey in await users.GetPasskeysAsync(user))
            {
                if (!(await users.RemovePasskeyAsync(user, passkey.CredentialId)).Succeeded)
                {
                    return Error(context, "recovery_failed");
                }
            }
            await db.Sessions.Where(x => x.UserId == user.Id).ExecuteDeleteAsync();
            if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) { return Error(context, "recovery_failed"); }
        }
        if (!(await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey)).Succeeded)
        {
            return Error(context, "passkey_not_saved");
        }
        await transaction.CommitAsync();
        await CreateSessionAsync(context, user, signIn, db);
        return Results.NoContent();
    }

    private static async Task<IResult> AdditionalPasskeyOptionsAsync(HttpContext context, SignInManager<AccountUser> signIn,
        UserManager<AccountUser> users, AccountDatabase db)
    {
        var user = (await users.GetUserAsync(context.User))!;
        if ((await users.GetPasskeysAsync(user)).Count >= 8) { return Error(context, "passkey_limit"); }
        var options = await signIn.MakePasskeyCreationOptionsAsync(new() { Id = user.Id, Name = user.UserName!, DisplayName = user.DisplayName });
        await SaveCeremonyAsync(context, db, "add", user.Id, user.DisplayName, null);
        return Results.Content(options, "application/json");
    }

    private static async Task<IResult> CompleteAdditionalPasskeyAsync(PasskeyResponse request, HttpContext context,
        SignInManager<AccountUser> signIn, UserManager<AccountUser> users, AccountDatabase db)
    {
        if (request.Credential.ValueKind != JsonValueKind.Object) { return Error(context, "invalid_account_request"); }
        var ceremony = await ConsumeCeremonyAsync(context, db, "add");
        var user = (await users.GetUserAsync(context.User))!;
        if (ceremony is null || ceremony.UserId != user.Id || (await users.GetPasskeysAsync(user)).Count >= 8)
        {
            return Error(context, "ceremony_expired_or_used");
        }
        var attestation = await signIn.PerformPasskeyAttestationAsync(request.Credential.GetRawText());
        if (!attestation.Succeeded || attestation.UserEntity.Id != user.Id) { return Error(context, "passkey_not_verified"); }
        return (await users.AddOrUpdatePasskeyAsync(user, attestation.Passkey)).Succeeded ? Results.NoContent() : Error(context, "passkey_not_saved");
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, AccountDatabase db)
    {
        await db.Sessions.Where(x => x.Id == context.User.FindFirstValue(AccountIdentity.SessionClaim)).ExecuteDeleteAsync();
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.NoContent();
    }

    private static async Task<IResult> AccountDetailsAsync(HttpContext context, AccountDatabase db, UserManager<AccountUser> users)
    {
        var id = users.GetUserId(context.User)!;
        var user = (await users.FindByIdAsync(id))!;
        var passkeys = await users.GetPasskeysAsync(user);
        var devices = new List<object>();
        foreach (var device in context.RequestServices.GetRequiredService<IConfiguration>().GetSection("Devices").GetChildren())
        {
            if (device["DeviceId"] is not { Length: > 0 } deviceId
                || !await AccountResourceAuthorization.HasPermissionAsync(context, "device", deviceId, "read")) { continue; }
            devices.Add(new { deviceId, canOperate = await AccountResourceAuthorization.HasPermissionAsync(context, "device", deviceId, "operate") });
        }
        return Results.Ok(new
        {
            devices,
            sessions = await db.Sessions.Where(x => x.UserId == id && x.ExpiresAt > Now()).Select(x => new
            { x.Id, x.ExpiresAt, x.LastSeenAt, x.InstallationName, x.Trusted, current = x.Id == context.User.FindFirstValue(AccountIdentity.SessionClaim) }).ToArrayAsync(),
            grants = await db.ResourceGrants.Where(x => x.UserId == id).ToArrayAsync(),
            associations = await db.DeviceAssociations.Where(x => x.UserId == id)
                .Select(x => new { x.Id, x.DeviceId, x.Alias, x.InstallationId, x.CreatedAt }).ToArrayAsync(),
            passkeyCount = passkeys.Count,
            passkeys = passkeys.Select(x => new
            {
                id = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(x.CredentialId), x.Name, x.CreatedAt, x.IsBackedUp
            })
        });
    }

    private static async Task<IResult> DeletePasskeyAsync(string credentialId, HttpContext context, AccountDatabase db, UserManager<AccountUser> users)
    {
        if (credentialId.Length > 2048) { return Error(context, "invalid_passkey"); }
        byte[] id;
        try { id = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(credentialId); }
        catch (FormatException) { return Error(context, "invalid_passkey"); }
        await using var transaction = await db.Database.BeginTransactionAsync();
        var userId = users.GetUserId(context.User)!;
        await db.Database.ExecuteSqlRawAsync("UPDATE AspNetUsers SET Disabled = Disabled WHERE Id = {0}", userId);
        var user = (await users.FindByIdAsync(userId))!;
        var keys = await users.GetPasskeysAsync(user);
        if (!keys.Any(x => x.CredentialId.AsSpan().SequenceEqual(id))) { return ApiProblem.Create(context, 404, "passkey_not_found"); }
        if (keys.Count <= 1) { return ApiProblem.Create(context, 409, "last_passkey_required"); }
        if (!(await users.RemovePasskeyAsync(user, id)).Succeeded) { return Error(context, "passkey_not_removed"); }
        var current = context.User.FindFirstValue(AccountIdentity.SessionClaim);
        await db.Sessions.Where(x => x.UserId == userId && x.Id != current).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        return Results.NoContent();
    }

    private static async Task<IResult> RenamePasskeyAsync(string credentialId, PasskeyNameChange request,
        HttpContext context, UserManager<AccountUser> users)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Name.Any(char.IsControl))
        { return Error(context, "invalid_passkey_name"); }
        if (credentialId.Length > 2048) { return Error(context, "invalid_passkey"); }
        byte[] id;
        try { id = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(credentialId); }
        catch (FormatException) { return Error(context, "invalid_passkey"); }
        var user = (await users.GetUserAsync(context.User))!;
        var passkey = await users.GetPasskeyAsync(user, id);
        if (passkey is null) { return ApiProblem.Create(context, 404, "passkey_not_found"); }
        passkey.Name = request.Name.Trim();
        return (await users.AddOrUpdatePasskeyAsync(user, passkey)).Succeeded
            ? Results.NoContent() : Error(context, "passkey_not_saved");
    }

    private static async Task<IResult> RevokeSessionAsync(string id, HttpContext context, AccountDatabase db, UserManager<AccountUser> users)
    {
        var count = await db.Sessions.Where(x => x.Id == id && x.UserId == users.GetUserId(context.User)).ExecuteDeleteAsync();
        if (id == context.User.FindFirstValue(AccountIdentity.SessionClaim)) { await context.SignOutAsync(IdentityConstants.ApplicationScheme); }
        return count == 1 ? Results.NoContent() : ApiProblem.Create(context, 404, "session_not_found");
    }

    private static async Task<IResult> UpdateInstallationAsync(string id, InstallationChange request, HttpContext context,
        AccountDatabase db, UserManager<AccountUser> users)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) { return Error(context, "invalid_installation_name"); }
        var count = await db.Sessions.Where(x => x.Id == id && x.UserId == users.GetUserId(context.User))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.InstallationName, request.Name.Trim()).SetProperty(x => x.Trusted, request.Trusted));
        return count == 1 ? Results.NoContent() : ApiProblem.Create(context, 404, "session_not_found");
    }

    private sealed record DeviceAssociationRequest(string Alias);
    private static async Task<IResult> AssociateDeviceAsync(string id, DeviceAssociationRequest request, HttpContext context,
        AccountDatabase db, IConfiguration configuration)
    {
        if (string.IsNullOrWhiteSpace(request.Alias) || request.Alias.Length > 80) { return Error(context, "invalid_device_alias"); }
        if (!configuration.GetSection("Devices").GetChildren().Any(x => x["DeviceId"] == id)
            || !await AccountResourceAuthorization.HasPermissionAsync(context, "device", id, "read"))
        {
            return ApiProblem.Create(context, 403, "device_grant_required");
        }
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var association = await db.DeviceAssociations.SingleOrDefaultAsync(x => x.UserId == userId && x.DeviceId == id);
        if (association is null)
        {
            if (await db.DeviceAssociations.CountAsync(x => x.UserId == userId) >= 100) { return Error(context, "association_limit"); }
            association = new AccountDeviceAssociation { Id = Guid.NewGuid().ToString("N"), UserId = userId, DeviceId = id, CreatedAt = Now() };
            db.DeviceAssociations.Add(association);
        }
        // This is an account label for an already authorized target. It neither
        // verifies local BLE proof nor grants any new device capability.
        association.Alias = request.Alias.Trim();
        association.InstallationId = context.User.FindFirstValue(AccountIdentity.SessionClaim)!;
        await db.SaveChangesAsync();
        return Results.Ok(new { association.Id, association.DeviceId, association.Alias, association.InstallationId, association.CreatedAt });
    }

    private static async Task<IResult> RemoveDeviceAssociationAsync(string id, HttpContext context, AccountDatabase db)
    {
        var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        await db.DeviceAssociations.Where(x => x.UserId == userId && x.DeviceId == id).ExecuteDeleteAsync();
        return Results.NoContent();
    }

    private static async Task CreateSessionAsync(HttpContext context, AccountUser user, SignInManager<AccountUser> signIn, AccountDatabase db)
    {
        await db.Sessions.Where(x => x.ExpiresAt <= Now()).ExecuteDeleteAsync();
        // Keep the installation/session list bounded without silently extending credentials.
        var old = await db.Sessions.Where(x => x.UserId == user.Id).OrderByDescending(x => x.ExpiresAt).Skip(19).ToArrayAsync();
        db.Sessions.RemoveRange(old);
        var session = new AccountSession { Id = Secret(), UserId = user.Id, ExpiresAt = Now() + 12 * 3600, AuthenticatedAt = Now(), LastSeenAt = Now() };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        await signIn.SignInWithClaimsAsync(user, new AuthenticationProperties
        {
            IsPersistent = false, ExpiresUtc = DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAt)
        }, [new Claim(AccountIdentity.SessionClaim, session.Id)]);
    }

    private static async Task SaveCeremonyAsync(HttpContext context, AccountDatabase db, string kind, string userId, string name, string? invitationHash)
    {
        await db.Ceremonies.Where(x => x.ExpiresAt <= Now() || x.Consumed).ExecuteDeleteAsync();
        // A browser has at most one active ceremony. Replacing it invalidates its prior challenge.
        if (context.Request.Cookies.TryGetValue(AccountIdentity.CeremonyCookie, out var previous))
        {
            await db.Ceremonies.Where(x => x.Id == previous).ExecuteDeleteAsync();
        }
        var ceremony = new AccountCeremony
        {
            Id = Secret(), Kind = kind, UserId = userId, DisplayName = name,
            InvitationHash = invitationHash, ExpiresAt = Now() + 300
        };
        db.Ceremonies.Add(ceremony);
        await db.SaveChangesAsync();
        context.Response.Cookies.Append(AccountIdentity.CeremonyCookie, ceremony.Id, AccountIdentity.CeremonyCookieOptions);
    }

    internal static async Task<AccountCeremony?> ConsumeCeremonyAsync(HttpContext context, AccountDatabase db, string kind)
    {
        var id = context.Request.Cookies[AccountIdentity.CeremonyCookie];
        if (id is null) { return null; }
        context.Response.Cookies.Delete(AccountIdentity.CeremonyCookie, AccountIdentity.CeremonyCookieOptions);
        if (await db.Ceremonies.Where(x => x.Id == id && x.Kind == kind && !x.Consumed && x.ExpiresAt > Now())
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.Consumed, true)) != 1) { return null; }
        return await db.Ceremonies.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static bool BootstrapMatches(IConfiguration config, string hash) =>
        config["ApplicationIdentity:Bootstrap:TokenSha256"] is { Length: 64 } expected
        && DateTimeOffset.TryParse(config["ApplicationIdentity:Bootstrap:ExpiresAt"], out var expiry) && expiry > DateTimeOffset.UtcNow
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected.ToUpperInvariant()), Encoding.ASCII.GetBytes(hash));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Secret() => Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
    internal static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private static IResult Error(HttpContext context, string code) => ApiProblem.Create(context, 400, code);
}

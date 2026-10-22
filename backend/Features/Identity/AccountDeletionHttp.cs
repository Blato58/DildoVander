using System.Security.Claims;
using System.Security.Cryptography;
using Kufr.Backend.Features.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;

namespace Kufr.Backend.Features.Identity;

internal static partial class AccountEndpoints
{
    private const string DeletionReceiptCookie = "__Host-dv-account-deletion";
    private static readonly TimeSpan DeletionReceiptLifetime = TimeSpan.FromDays(7);

    private static ITimeLimitedDataProtector DeletionReceiptProtector(IDataProtectionProvider protection)
        => protection.CreateProtector("DildoVander.AccountDeletionReceipt.v1").ToTimeLimitedDataProtector();

    private static CookieOptions DeletionReceiptOptions() => new()
    {
        Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = DeletionReceiptLifetime
    };

    private static async Task<IResult> DeleteAccountAsync(HttpContext context, AccountDeletionService deletion,
        IDataProtectionProvider protection)
    {
        var accountId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (accountId is null) { return ApiProblem.Create(context, 401, "account_required"); }
        var recent = await AccountAuthorization.RequireRecentAsync(context);
        if (recent is not null) { return recent; }
        if (context.Request.Headers["X-DV-Confirm"] != "yes")
        {
            return ApiProblem.Create(context, 409, "confirmation_required");
        }
        AccountDeletionRequestResult result;
        try { result = await deletion.RequestAsync(accountId, context.RequestAborted); }
        catch (AccountDeletionUnavailableException)
        {
            return ApiProblem.Create(context, 503, "account_deletion_unavailable");
        }
        if (result.Status == AccountDeletionRequestStatus.LastOwner)
        {
            return ApiProblem.Create(context, 409, "last_owner_required");
        }
        if (result.Status == AccountDeletionRequestStatus.NotFound)
        {
            return ApiProblem.Create(context, 401, "account_required");
        }
        var receipt = result.ReceiptId ?? throw new InvalidOperationException("Accepted deletion has no receipt.");
        context.Response.Cookies.Append(DeletionReceiptCookie,
            DeletionReceiptProtector(protection).Protect(receipt, DeletionReceiptLifetime), DeletionReceiptOptions());
        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new { status = "deletion_requested" }, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> AccountDeletionStatusAsync(HttpContext context, AccountDeletionService deletion,
        IDataProtectionProvider protection)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        var cookie = context.Request.Cookies[DeletionReceiptCookie];
        if (string.IsNullOrEmpty(cookie)) { return Results.NotFound(); }
        string receipt;
        try { receipt = DeletionReceiptProtector(protection).Unprotect(cookie, out _); }
        catch (CryptographicException)
        {
            var expiredOptions = DeletionReceiptOptions();
            expiredOptions.MaxAge = TimeSpan.Zero;
            context.Response.Cookies.Delete(DeletionReceiptCookie, expiredOptions);
            return Results.NotFound();
        }
        var status = await deletion.ReadStatusAsync(receipt, context.RequestAborted);
        return status switch
        {
            "pending" => Results.Json(new { status = "deletion_requested" }),
            "completed" => Results.Json(new { status = "deleted" }),
            _ => Results.NotFound()
        };
    }
}

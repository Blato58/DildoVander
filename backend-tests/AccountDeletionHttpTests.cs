using System.Net;
using System.Net.Http.Json;
using Kufr.Backend.Features.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

internal static partial class AccountIdentityTests
{
    public static async Task RunAccountDeletionHttpAsync()
    {
        await using var fixture = await Fixture.StartAsync(deletionFeatures: true);
        // Drive durable retry explicitly so pending/completed assertions are deterministic.
        await fixture.App.Services.GetServices<IHostedService>().OfType<AccountDeletionWorker>().Single().StopAsync(default);
        using var anonymous = fixture.Client();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        Check((await anonymous.DeleteAsync("/app/v1/account")).StatusCode == HttpStatusCode.Unauthorized,
            "anonymous account deletion is rejected");
        Check((await anonymous.GetAsync("/app/v1/account/deletion")).StatusCode == HttpStatusCode.NotFound,
            "deletion status requires its separate protected receipt");
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Deleting test owner");
        var session = await CsrfAsync(owner);
        var ownerId = session.GetProperty("account").GetProperty("id").GetString()!;

        owner.DefaultRequestHeaders.Remove("X-DV-CSRF");
        Check((await owner.DeleteAsync("/app/v1/account")).StatusCode == HttpStatusCode.BadRequest,
            "account deletion retains the CSRF boundary");
        await CsrfAsync(owner);
        owner.DefaultRequestHeaders.Remove("X-DV-Confirm");
        Check((await owner.DeleteAsync("/app/v1/account")).StatusCode == HttpStatusCode.Conflict,
            "account deletion requires an explicit irreversible confirmation");
        owner.DefaultRequestHeaders.Add("X-DV-Confirm", "yes");
        await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId == ownerId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now() - 601)));
        Check((await owner.DeleteAsync("/app/v1/account")).StatusCode == HttpStatusCode.Forbidden,
            "account deletion requires a recent passkey assertion");
        await SeedAsync(fixture.App, db => db.Sessions.Where(x => x.UserId == ownerId)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.AuthenticatedAt, AccountEndpoints.Now())));
        using var lastOwner = await owner.DeleteAsync("/app/v1/account");
        Check(lastOwner.StatusCode == HttpStatusCode.Conflict
            && (await lastOwner.Content.ReadAsStringAsync()).Contains("last_owner_required", StringComparison.Ordinal),
            "the last active owner cannot delete the only operating account");

        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = "Owner", recoveryUserId = (string?)null }));
        using var remainingOwner = fixture.Client();
        using var remainingKey = new VirtualPasskey();
        await EnrollAsync(remainingOwner, remainingKey, invitation.GetProperty("token").GetString()!, "Remaining owner");
        await CsrfAsync(remainingOwner);
        using var accepted = await owner.DeleteAsync("/app/v1/account");
        Check(accepted.StatusCode == HttpStatusCode.Accepted, "202 is returned after durable deletion acceptance");
        var acceptance = await JsonAsync(accepted);
        Check(acceptance.GetProperty("status").GetString() == "deletion_requested"
            && acceptance.EnumerateObject().Count() == 1, "acceptance exposes no account or receipt identifier");
        var receipt = fixture.Cookies(owner).GetCookies(owner.BaseAddress!).Cast<Cookie>()
            .Single(cookie => cookie.Name == "__Host-dv-account-deletion");
        Check(receipt.Secure && receipt.HttpOnly && receipt.Path == "/",
            "deletion receipt is host-bound secure HttpOnly state separate from account access");
        Check(accepted.Headers.GetValues("Set-Cookie").Any(value => value.StartsWith("__Host-dv-account-deletion=", StringComparison.Ordinal)
            && value.Contains("samesite=strict", StringComparison.OrdinalIgnoreCase)), "receipt uses SameSite Strict");
        Check((await owner.GetAsync("/app/v1/account")).StatusCode == HttpStatusCode.Unauthorized,
            "accepted deletion immediately revokes its account session");
        using var pending = await owner.GetAsync("/app/v1/account/deletion");
        Check(pending.Headers.CacheControl?.NoStore == true
            && (await JsonAsync(pending)).GetProperty("status").GetString() == "deletion_requested",
            "signed-out user can read only the pending deletion status without caching");
        Check((await anonymous.GetAsync("/app/v1/account/deletion")).StatusCode == HttpStatusCode.NotFound,
            "another browser cannot inspect the deletion receipt");
        using (var scope = fixture.App.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountDeletionService>().ProcessPendingAsync();
        }
        var completed = await JsonAsync(await owner.GetAsync("/app/v1/account/deletion"));
        Check(completed.GetProperty("status").GetString() == "deleted" && completed.EnumerateObject().Count() == 1,
            "the receipt confirms actual local completion without personal data");
        Check((await remainingOwner.GetAsync("/app/v1/account")).StatusCode == HttpStatusCode.OK,
            "another guest or owner keeps their account and access");
        anonymous.DefaultRequestHeaders.Add("Cookie", "__Host-dv-account-deletion=forged-receipt");
        Check((await anonymous.GetAsync("/app/v1/account/deletion")).StatusCode == HttpStatusCode.NotFound,
            "forged deletion receipts fail closed");
        Console.WriteLine("PASS account deletion HTTPS authentication, CSRF, recent passkey, confirmation, last-owner guard, durable receipt isolation and local completion");
    }
}

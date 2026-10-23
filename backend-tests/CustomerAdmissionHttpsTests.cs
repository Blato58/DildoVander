using System.Net;
using System.Security.Claims;
using Kufr.Backend.Features.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

internal static partial class AccountIdentityTests
{
    internal static async Task RunCustomerAdmissionHttpsAsync()
    {
        await using var fixture = await Fixture.StartAsync(customerFeatures: true);
        var owner = fixture.Client();
        var other = fixture.Client();
        var account = await AddAdmissionCookieAsync(fixture, owner);
        await AddAdmissionCookieAsync(fixture, other);
        const string path = "/app/v1/customer/waiting/granted-device";
        using (var initial = await owner.GetAsync(path))
        {
            Check(initial.StatusCode == HttpStatusCode.OK, "real HTTPS waiting read accepts production cookie and neutral authority");
        }
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            database.DeletionRequests.Add(new AccountDeletionRequest
            {
                UserId = account, ReceiptId = Guid.NewGuid().ToString("N"), RequestedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });
            await database.SaveChangesAsync();
        }
        // Cookie middleware still sees a live user/session. The held admission prefix
        // must independently observe the already committed durable deletion request.
        using (var deleted = await owner.GetAsync(path))
        {
            Check(deleted.StatusCode == HttpStatusCode.Unauthorized, "deletion-first HTTPS admission is denied before asynchronous journal revocation");
        }
        using (var isolated = await other.GetAsync(path))
        {
            Check(isolated.StatusCode == HttpStatusCode.OK, "another account's HTTPS admission remains available");
        }
        await using (var scope = fixture.App.Services.CreateAsyncScope())
        {
            var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
            await database.DeletionRequests.Where(row => row.UserId == account).ExecuteDeleteAsync();
            await database.Users.Where(row => row.Id == account).ExecuteUpdateAsync(set => set.SetProperty(row => row.SecurityStamp, "changed"));
        }
        using var stale = await owner.GetAsync(path);
        Check(stale.StatusCode == HttpStatusCode.Unauthorized, "production HTTPS middleware rejects a stale stamp cookie");
    }

    private static async Task<string> AddAdmissionCookieAsync(Fixture fixture, HttpClient client)
    {
        await using var scope = fixture.App.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AccountUser>>();
        var id = Guid.NewGuid().ToString("N");
        var user = new AccountUser { Id = id, UserName = id, DisplayName = "Admission test guest", CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
        Check((await users.CreateAsync(user)).Succeeded, "HTTPS admission user seed");
        var session = new AccountSession
        {
            Id = Guid.NewGuid().ToString("N"), UserId = id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
            AuthenticatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), LastSeenAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
        var database = scope.ServiceProvider.GetRequiredService<AccountDatabase>();
        database.Sessions.Add(session); await database.SaveChangesAsync();
        var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AccountUser>>().CreateAsync(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new(AccountIdentity.SessionClaim, session.Id));
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(principal, new AuthenticationProperties
        {
            IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
        }, IdentityConstants.ApplicationScheme);
        fixture.Cookies(client).Add(client.BaseAddress!, new Cookie(options.Cookie.Name!, options.TicketDataFormat.Protect(ticket), "/")
        { Secure = true, HttpOnly = true });
        return id;
    }
}

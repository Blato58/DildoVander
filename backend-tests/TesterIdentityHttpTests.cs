using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Kufr.Backend.Features.Identity;

internal static partial class AccountIdentityTests
{
    internal static async Task RunTesterIdentityAsync()
    {
        await using var fixture = await Fixture.StartAsync();
        using var owner = fixture.Client();
        using var ownerKey = new VirtualPasskey();
        await EnrollAsync(owner, ownerKey, Fixture.Bootstrap, "Owner");
        await CsrfAsync(owner);
        var invitation = await JsonAsync(await owner.PostAsJsonAsync("/app/v1/admin/invitations",
            new { role = AccountRoles.Tester, recoveryUserId = (string?)null }));
        using var tester = fixture.Client();
        using var testerKey = new VirtualPasskey();
        await EnrollAsync(tester, testerKey, invitation.GetProperty("token").GetString()!, "Tester");
        var session = await CsrfAsync(tester);
        Check(session.GetProperty("account").GetProperty("roles").EnumerateArray().Single().GetString() == AccountRoles.Tester,
            "normal invitation and verified passkey provision Tester without staff privileges");
        using var resumed = fixture.Client();
        await SignInAsync(resumed, testerKey);
        var resumedSession = await CsrfAsync(resumed);
        Check(resumedSession.GetProperty("account").GetProperty("id").GetString() == session.GetProperty("account").GetProperty("id").GetString(),
            "Tester normal passkey sign-in retains persistent identity");
        Check((await tester.GetAsync("/app/v1/admin/users")).StatusCode == HttpStatusCode.Forbidden,
            "Tester cannot inspect production users");
        Check((await tester.PostAsJsonAsync("/app/v1/admin/invitations", new { role = AccountRoles.Owner })).StatusCode == HttpStatusCode.Forbidden,
            "Tester cannot mint production privileges");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[] {
            new Claim(ClaimTypes.NameIdentifier, "tester"), new Claim(ClaimTypes.Role, AccountRoles.Tester)
        }, "test"));
        Check(AccountRoles.CanUsePersonaLab(principal) && !AccountRoles.IsAdmin(principal) && !AccountRoles.IsStaff(principal),
            "Lab capability does not imply staff or administrator authority");
        Console.WriteLine("Tester invitation, passkey persistence and privilege isolation passed.");
    }
}

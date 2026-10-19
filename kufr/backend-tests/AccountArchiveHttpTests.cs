using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kufr.Backend.Features.Identity;
using Kufr.Backend.Features.Keepsakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

internal static class AccountArchiveHttpTests
{
    public static async Task RunAsync(WebApplication app, HttpClient accountA, HttpClient accountB, string targetId)
    {
        var store = app.Services.GetRequiredService<KeepsakeStore>();
        var issued = store.Issue(new KeepsakeIssueSnapshot(Guid.NewGuid().ToString("D"), "granted-device", 1,
            "fixture-wand", "Synthetic account association wand", "Synthetic catalog evidence", "No hardware is involved."));
        var guest = store.Bootstrap();
        store.Claim(store.IssueClaim(issued.Id, "granted-device").Token, guest.Token, Guid.NewGuid().ToString("D"));
        var job = store.LeaseNext()!;
        var fake = new FakeKeepsakeProvider();
        Check(store.Complete(job, await fake.GenerateAsync(store.Dispatch(job)!, default)), "offline fixture generated");
        store.CreateLegacyWizardIdentity(issued.Id, guest.Token, store.Read(issued.Id, guest.Token).Editions.Single().Id);
        var proof = store.AuthorizeAccountAssociation(issued.Id, guest.Token);
        async Task<string> TicketAsync()
        {
            using var scope = app.Services.CreateScope();
            return await AccountArchiveAssociations.IssueAsync(new DefaultHttpContext { RequestServices = scope.ServiceProvider }, targetId, proof);
        }
        var token = await TicketAsync();
        Check((await accountB.PostAsJsonAsync("/app/v1/archive/associations", new { ticket = token })).StatusCode == HttpStatusCode.NotFound,
            "target-account binding denies another logged-in user");
        Check((await accountA.PostAsJsonAsync("/app/v1/archive/associations", new { ticket = token })).IsSuccessStatusCode, "explicit owner ticket consumed by target account");
        Check((await accountA.PostAsJsonAsync("/app/v1/archive/associations", new { ticket = token })).StatusCode == HttpStatusCode.NotFound, "account ticket replay denied");
        var path = "/app/v1/archive/associations/" + issued.Id;
        var view = await accountA.GetAsync(path);
        Check(view.IsSuccessStatusCode && view.Headers.CacheControl?.NoStore == true, "account-linked private read is not cached");
        var json = JsonDocument.Parse(await view.Content.ReadAsStringAsync()).RootElement;
        Check(json.GetProperty("wizardIdentities").GetArrayLength() == 1, "linked account reads explicit public wizard identity");
        Check(!json.TryGetProperty("prophecies", out var prophecy) || prophecy.ValueKind == JsonValueKind.Null || prophecy.GetArrayLength() == 0,
            "linked account is not upgraded to guest owner private prophecy authority");
        var assetId = json.GetProperty("assets")[0].GetProperty("id").GetString();
        Check((await accountA.GetAsync(path + "/assets/" + assetId)).IsSuccessStatusCode, "linked image access belongs to target account");
        Check((await accountB.GetAsync(path)).StatusCode == HttpStatusCode.NotFound
            && (await accountB.GetAsync(path + "/assets/" + assetId)).StatusCode == HttpStatusCode.NotFound, "account B cannot read account A story or assets");
        Check((await accountA.GetAsync(path + "/assets/" + Guid.NewGuid().ToString("D"))).StatusCode == HttpStatusCode.NotFound,
            "missing asset maps to privacy-safe 404 rather than an exception");
        var concurrent = await TicketAsync();
        var results = await Task.WhenAll(
            accountA.PostAsJsonAsync("/app/v1/archive/associations", new { ticket = concurrent }),
            accountA.PostAsJsonAsync("/app/v1/archive/associations", new { ticket = concurrent }));
        Check(results.Count(x => x.IsSuccessStatusCode) == 1 && results.Count(x => x.StatusCode == HttpStatusCode.NotFound) == 1,
            "concurrent capability consumption succeeds exactly once");
        var expired = await TicketAsync();
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AccountDatabase>().ArchiveTickets.Where(x => x.TokenHash == AccountEndpoints.Hash(expired))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ExpiresAt, AccountEndpoints.Now() - 1));
        }
        Check((await accountA.PostAsJsonAsync("/app/v1/archive/associations", new { ticket = expired })).StatusCode == HttpStatusCode.NotFound, "account link capability expiry enforced");
        var inflight = accountA.GetAsync(path + "/assets/" + assetId);
        store.RevokeAllAccess(issued.Id, guest.Token);
        var raced = await inflight;
        Check(raced.IsSuccessStatusCode || raced.StatusCode == HttpStatusCode.NotFound, "concurrent guest revocation never produces a server exception");
        Check((await accountA.GetAsync(path)).StatusCode == HttpStatusCode.NotFound
            && (await accountA.GetAsync(path + "/assets/" + assetId)).StatusCode == HttpStatusCode.NotFound, "guest access revocation fences linked account reads and assets");
        Console.WriteLine("PASS account archive HTTP target binding, explicit single-use/replay/expiry/concurrent consumption, A/B story/asset isolation and guest-revocation race");
    }
    private static void Check(bool value, string label) { if (!value) { throw new InvalidOperationException("Account archive HTTP: " + label); } }
}
